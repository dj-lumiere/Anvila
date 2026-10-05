using Builder.Declaration;
using Builder.Instantiation;
using Builder.Tokenizer;
using Builder.Verification;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using BigInteger = System.Numerics.BigInteger;

namespace Builder.Lowering.Passes;

/// <summary>
/// Lowers domain-specific literal tokens to equivalent record constructor expressions
/// before codegen. After this pass, codegen never sees ByteSize, Duration, Character,
/// or ByteLetter literals -> only <see cref="CreatorExpression"/> nodes.
///
/// <para>Handled token types:</para>
/// <list type="bullet">
/// <item>ByteSize (<c>64kib</c>, <c>100mb</c>, ?? -> <c>ByteSize(value: bytes_u64)</c></item>
/// <item>Duration (<c>5s</c>, <c>100ms</c>, ?? -> <c>Duration(seconds: s64, nanoseconds: u32)</c></item>
/// <item>Character (<c>'a'</c>) -> <c>Character(from: codepoint_u32)</c></item>
/// <item>ByteLetter (<c>b'x'</c>) -> <c>Byte(from: byte_u8)</c></item>
/// </list>
///
/// <para><c>Bytes</c> literals (<c>b"..."</c>) are not lowered here -> they produce global-constant
/// entity allocations and remain in codegen.</para>
/// </summary>
internal sealed class LiteralLoweringPass : AstRewriter
{
    private readonly Dictionary<string, Statement>? _variantBodies;

    // Integer literal lowering: `123n` (or a bare literal typed `Integer`) -> the Integer record over limbs the
    // builder computed, laid down as read-only module data.
    private readonly RecordTypeSymbol? _integerType;
    private readonly TypeSymbol? _boolType;
    private readonly TypeSymbol? _u64Type;

    // Imaginary literal lowering: `4.0i` -> a pure-imaginary complex constructor of the resolved
    // complex type — C64(2×B32), C128(2×B64, the default) or C256(2×B128).
    private readonly TypeSymbol? _c64Type;
    private readonly TypeSymbol? _c128Type;
    private readonly TypeSymbol? _c256Type;
    private readonly TypeSymbol? _b32Type;
    private readonly TypeSymbol? _b64Type;

    private readonly TypeSymbol? _b128Type;

    // Domain-literal record types, stamped onto the lowered CreatorExpression's ResolvedType. Without this
    // the creator carries no type, and any pipeline copy that reaches OperatorLoweringPass WITHOUT first
    // running CallOverloadResolutionPass (which is what otherwise fills a creator's ResolvedType) lowers a
    // comparison like `'a' < 'b'` to an UNRESOLVED `.lt` call (LoweringKind=Unknown, no ResolvedRoutine) —
    // which then hard-errors at codegen. Stamping the type here makes the operand type flow deterministically.
    private readonly TypeSymbol? _characterType;
    private readonly TypeSymbol? _byteType;
    private readonly TypeSymbol? _byteSizeType;
    private readonly TypeSymbol? _durationType;
    private readonly TypeRegistry _registry;

    /// <summary>
    /// Initializes a new instance with the dependencies required for its builder phase.
    /// </summary>
    internal LiteralLoweringPass(PostprocessingContext ctx)
    {
        _variantBodies = ctx.VariantBodies;
        _registry = ctx.Registry;
        // An `n` literal has no scalar form (a raw one reaches the emitter as `store %Record.Integer 42n`), so
        // it becomes the Integer record itself. Integer lives in `module Numerics` — qualify (a bare lookup
        // depended on the cross-module short-name scan, and when it missed the literal reached the emitter raw).
        _integerType = (ctx.Registry.LookupType(name: "Numerics.Integer") ??
                        ctx.Registry.LookupType(name: "Integer")) as RecordTypeSymbol;
        _boolType = ctx.Registry.LookupType(name: "Bool");
        _u64Type = ctx.Registry.LookupType(name: "U64");

        // Imaginary `i` literals have no scalar form for the complex record types; lower them to
        // pure-imaginary complex constructors of the resolved complex type.
        _c64Type = ctx.Registry.LookupType(name: "C64");
        _c128Type = ctx.Registry.LookupType(name: "C128");
        _c256Type = ctx.Registry.LookupType(name: "C256");
        _b32Type = ctx.Registry.LookupType(name: "B32");
        _b64Type = ctx.Registry.LookupType(name: "B64");
        _b128Type = ctx.Registry.LookupType(name: "B128");

        _characterType = ctx.Registry.LookupType(name: "Character");
        _byteType = ctx.Registry.LookupType(name: "Byte");
        _byteSizeType = ctx.Registry.LookupType(name: "ByteSize");
        _durationType = ctx.Registry.LookupType(name: "Duration");
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Runs this builder phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(registry: _registry, program: program, lower: r => VisitStatement(stmt: r.Body));
    }

    /// <summary>
    /// Runs this builder phase over its configured input.
    /// </summary>
    public void RunOnVariantBodies()
    {
        if (_variantBodies == null)
        {
            return;
        }

        BodyDispatch.RunOnVariantBodies(registry: _registry, bodies: _variantBodies,
            lower: (_, body) => VisitStatement(stmt: body));
    }

    /// <summary>
    /// Runs this builder phase over the bodies monomorphization just built.
    /// </summary>
    public void RunOnInstantiatedGenericBodies(Dictionary<string, MonomorphizedBody> bodies)
    {
        BodyDispatch.RunOnInstantiatedGenericBodies(registry: _registry, bodies: bodies,
            lower: (_, entry) => VisitStatement(stmt: entry.Ast.Body));
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// The nodes this pass actually rewrites. A <see cref="LiteralExpression"/> is a leaf to the base
    /// rewriter, but it is the primary transform here (domain literals -> constructor calls). A
    /// <see cref="CarrierPayloadExpression"/> is not a node the base rewriter recurses into, so it is
    /// handled here to keep its <c>Carrier</c> child lowered. All other structural recursion is supplied
    /// by <see cref="AstRewriter"/>.
    /// </summary>
    public override Expression VisitExpression(Expression expr)
    {
        switch (expr)
        {
            case LiteralExpression literal:
            {
                // Complex literals FIRST: an imaginary `4i`, or a bare int/float literal SA promoted to
                // a complex type (the `3` in `3 + 4i`). Must precede the arbitrary-precision case so an
                // SF bare `3` promoted to complex builds `C(3, 0)` instead of an `Integer`.
                CreatorExpression? complex = TryLowerComplexLiteral(literal: literal);
                if (complex != null)
                {
                    return complex;
                }

                // An Integer literal -> the Integer record over build-time limbs.
                Expression? integer = TryLowerIntegerLiteral(literal: literal);
                if (integer != null)
                {
                    return integer;
                }

                Expression? lowered = TryLowerLiteral(literal: literal);
                if (lowered != null)
                {
                    return lowered;
                }

                return expr;
            }
            case CarrierPayloadExpression cpe:
            {
                Expression c = VisitExpression(expr: cpe.Carrier);
                return ReferenceEquals(objA: c, objB: cpe.Carrier)
                    ? expr
                    : cpe with { Carrier = c };
            }
            default:
                return base.VisitExpression(expr: expr);
        }
    }

    protected override Expression VisitBackIndex(BackIndexExpression e)
    {
        // `^n` is NOT materialized into a value here — it stays a `BackIndexExpression` marker.
        // OperatorLoweringPass (runs after this pass) rewrites the enclosing subscript/slice to
        // `back_resolve(count: coll.count(), offset: n)`. Retag an untyped/signed integer-literal
        // offset to U64 (the `^n` position is U64) BEFORE lowering, so it stays a scalar i64 and
        // is not lowered to an arbitrary-precision Integer (which is heap/Text-backed). A position the
        // analysis typed `Integer` (a Suflae collection's) stays one.
        Expression operand = e.Operand is LiteralExpression
        {
            LiteralType: TokenType.UndecidedInteger or TokenType.IntegerLiteral
            or TokenType.S64Literal
        } lit && e.ResolvedType?.Name != "Integer"
            ? lit with { LiteralType = TokenType.U64Literal }
            : e.Operand;
        Expression o = VisitExpression(expr: operand);
        return e with { Operand = o };
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Attempts to lower literal and reports whether it succeeded.
    /// </summary>
    private CreatorExpression? TryLowerLiteral(LiteralExpression literal)
    {
        SourceLocation loc = literal.Location;

        return literal.Value switch
        {
            char ch => MakeCharacterCreator(
                codepoint: char.ConvertToUtf32(s: ch.ToString(), index: 0),
                loc: loc),
            string s when IsByteSizeLiteralType(type: literal.LiteralType) => MakeByteSizeCreator(
                text: s,
                loc: loc),
            string s when IsDurationLiteralType(type: literal.LiteralType) => MakeDurationCreator(
                text: s,
                literalType: literal.LiteralType,
                loc: loc),
            string s when literal.LiteralType == TokenType.CharacterLiteral =>
                MakeCharacterCreator(codepoint: s.Length > 0
                        ? char.ConvertToUtf32(s: s, index: 0)
                        : 0,
                    loc: loc),
            string s when literal.LiteralType == TokenType.ByteLetterLiteral => MakeByteCreator(
                byteValue: s.Length > 0
                    ? s[index: 0] & 0xFF
                    : 0,
                loc: loc),
            _ => null
        };

    }

    /// <summary>
    /// Lowers an Integer literal to the Integer record it stands for, or returns null if
    /// <paramref name="literal"/> is not one. A magnitude below 2^64 is the value itself. The builder computes a
    /// bigger one's limbs, and they become read-only module data (<see cref="ConstantDataExpression"/>) under a null
    /// controller, which is how an Integer says its limbs are static: it never counts them and never frees them,
    /// like a Text literal.
    /// Decimal literals are not here: Decimal is an i128 BID whose literals are build-time constants already.
    /// </summary>
    private CreatorExpression? TryLowerIntegerLiteral(LiteralExpression literal)
    {
        if (literal.Value is not string raw || _integerType == null)
        {
            return null;
        }

        // Suflae: an UNSUFFIXED integer literal that SA resolved to `Integer` (the SF default, RF defaults to
        // S64) is still `UndecidedInteger` at this pass, since ExpressionLoweringPass only rewrites the token to
        // IntegerLiteral later. So match the resolved type too.
        bool integer = literal.LiteralType == TokenType.IntegerLiteral ||
                       literal.LiteralType == TokenType.UndecidedInteger &&
                       literal.ResolvedType?.Name == "Integer";
        if (!integer)
        {
            return null;
        }

        string digits = SemanticVerifier.CleanNumericLiteral(value: raw.EndsWith(value: 'n') ||
                                                                    raw.EndsWith(value: 'N')
            ? raw[..^1]
            : raw);
        if (!SemanticVerifier.TryParseWideMagnitude(cleaned: digits, value: out BigInteger value))
        {
            throw new InvalidOperationException(
                message: $"The Integer literal '{raw}' at {literal.Location} passed analysis but does not parse.");
        }

        return IntegerCreator(integer: _integerType, boolType: _boolType, u64Type: _u64Type, value: value,
            loc: literal.Location);
    }

    /// <summary>
    /// The Integer record a build-time <paramref name="value"/> stands for: <c>Integer(neg:, magnitude: v)</c> with
    /// the magnitude itself below 2^64, else its limbs as read-only module data
    /// (<c>IntegerLimbs(len:, tab: #constant_data[...], ctrl: none)</c>). Every Integer the builder writes as a
    /// constant (a literal, a range's default step) is built here.
    /// </summary>
    internal static CreatorExpression IntegerCreator(TypeRegistry registry, BigInteger value, SourceLocation loc)
    {
        RecordTypeSymbol integer = (registry.LookupType(name: "Numerics.Integer") ??
                                    registry.LookupType(name: "Integer")) as RecordTypeSymbol ??
                                   throw new InvalidOperationException(message: "Integer is not registered.");
        return IntegerCreator(integer: integer,
            boolType: registry.LookupType(name: "Bool"),
            u64Type: registry.LookupType(name: "U64"),
            value: value,
            loc: loc);
    }

    private static CreatorExpression IntegerCreator(RecordTypeSymbol integer, TypeSymbol? boolType,
        TypeSymbol? u64Type, BigInteger value, SourceLocation loc)
    {
        if (u64Type == null)
        {
            throw new InvalidOperationException(message: "U64 is not registered.");
        }

        List<long> limbs = Limbs(magnitude: BigInteger.Abs(value: value));
        VariantTypeSymbol magnitude =
            integer.MemberVariables.First(predicate: f => f.Name == "magnitude").Type as VariantTypeSymbol ??
            throw new InvalidOperationException(message: "Integer's magnitude is not a variant.");

        // A magnitude below 2^64 is the value itself (the U64 arm), a bigger one the limbs as read-only module data
        // with no controller, so the Integer never counts or frees them, like a Text literal.
        Expression payload;
        TypeSymbol armType;
        if (limbs.Count <= 1)
        {
            armType = u64Type;
            payload = new LiteralExpression(Value: limbs.Count == 0 ? 0UL : (ulong)limbs[index: 0],
                LiteralType: TokenType.U64Literal,
                Location: loc) { ResolvedType = u64Type };
        }
        else
        {
            RecordTypeSymbol limbsType = magnitude.Members.Select(selector: m => m.Type)
                                                  .OfType<RecordTypeSymbol>()
                                                  .First(predicate: t => t.Name != u64Type.Name);
            TypeSymbol FieldType(string name) => limbsType.MemberVariables.First(predicate: f => f.Name == name).Type;
            armType = limbsType;
            payload = new CreatorExpression(TypeName: limbsType.Name,
                TypeArguments: null,
                MemberVariables:
                [
                    ("len", new LiteralExpression(Value: (ulong)limbs.Count,
                        LiteralType: TokenType.U64Literal,
                        Location: loc) { ResolvedType = u64Type }),
                    ("tab", new ConstantDataExpression(Elements: limbs, ElementType: u64Type, Location: loc)
                    {
                        ResolvedType = FieldType(name: "tab")
                    }),
                    ("ctrl", new ZeroValueExpression(Location: loc) { ResolvedType = FieldType(name: "ctrl") })
                ],
                Location: loc) { ResolvedType = limbsType };
        }

        bool negative = value.Sign < 0;
        return new CreatorExpression(TypeName: integer.Name,
            TypeArguments: null,
            MemberVariables:
            [
                ("neg", new LiteralExpression(Value: negative,
                    LiteralType: negative
                        ? TokenType.True
                        : TokenType.False,
                    Location: loc) { ResolvedType = boolType }),
                ("magnitude", new CreatorExpression(TypeName: magnitude.Name,
                    TypeArguments: null,
                    MemberVariables: [(armType.Name, payload)],
                    Location: loc) { ResolvedType = magnitude, ConstructedType = magnitude })
            ],
            Location: loc) { ResolvedType = integer };
    }

    /// <summary>The little-endian 64-bit limbs of a non-negative value, with no high zero limb (none for zero).
    /// Each limb is given as the <c>long</c> with its bits.</summary>
    private static List<long> Limbs(BigInteger magnitude)
    {
        byte[] bytes = magnitude.ToByteArray(isUnsigned: true, isBigEndian: false);
        var limbs = new List<long>();
        if (magnitude.IsZero)
        {
            return limbs;
        }

        for (int at = 0; at < bytes.Length; at += 8)
        {
            ulong limb = 0;
            for (int b = 0; b < 8 && at + b < bytes.Length; b++)
            {
                limb |= (ulong)bytes[at + b] << (8 * b);
            }

            limbs.Add(item: unchecked((long)limb));
        }

        while (limbs.Count > 0 && limbs[^1] == 0)
        {
            limbs.RemoveAt(index: limbs.Count - 1);
        }

        return limbs;
    }

    /// <summary>
    /// Lowers a numeric literal whose SA-resolved type is complex into a complex constructor. A
    /// width-less imaginary literal (<c>4.0i</c> / <c>4.0_i</c>) becomes <c>C(real: 0, imag: value)</c>;
    /// a bare int/float literal that SA promoted to a complex type (the real component of <c>3 + 4i</c>)
    /// becomes <c>C(real: value, imag: 0)</c>. The resolved complex type fixes the components — C64→2×B32,
    /// C128→2×B64 (the imaginary default) or C256→2×B128. Returns null for
    /// any non-complex literal. Codegen has no scalar form for the complex record types, so this rewrite
    /// must happen before codegen.
    /// </summary>
    private CreatorExpression? TryLowerComplexLiteral(LiteralExpression literal)
    {
        if (literal.Value is not string raw)
        {
            return null;
        }

        bool imaginary = literal.LiteralType is TokenType.ImaginaryLiteral;
        bool realLiteral = literal.LiteralType is TokenType.UndecidedInteger
            or TokenType.UndecidedDecimal;
        if (!imaginary && !realLiteral)
        {
            return null;
        }

        string? complexName = literal.ResolvedType?.Name;
        // An imaginary literal always lowers (default C128); a real literal lowers ONLY when SA promoted
        // it to a complex type (e.g. the `3` in a `C128` context) — otherwise it stays a plain scalar.
        if (realLiteral && complexName is not ("C64" or "C128" or "C256"))
        {
            return null;
        }

        SourceLocation loc = literal.Location;
        string mag = ComplexMagnitude(raw: raw, imaginary: imaginary);
        return BuildComplexCreator(complexName: complexName ?? "C128",
            mag: mag,
            imaginary: imaginary,
            loc: loc);
    }

    /// <summary>
    /// Builds the concrete complex constructor for a resolved complex type name (C64→2×B32,
    /// C256→2×B128, or the C128 default/fallback). Returns null
    /// when the required component types were not resolved in the registry.
    /// </summary>
    private CreatorExpression? BuildComplexCreator(string complexName, string mag, bool imaginary,
        SourceLocation loc)
    {
        return complexName switch
        {
            "C64" when _c64Type != null => MakeComplexCreator(typeName: "C64",
                type: _c64Type,
                mag: mag,
                imaginary: imaginary,
                compLit: TokenType.B32Literal,
                compType: _b32Type,
                loc: loc),
            "C256" when _c256Type != null => MakeComplexCreator(typeName: "C256",
                type: _c256Type,
                mag: mag,
                imaginary: imaginary,
                compLit: TokenType.B128Literal,
                compType: _b128Type,
                loc: loc),
            // Default (C128, 2×B64) — also the fallback when the resolved type is C128 or unknown.
            _ when _c128Type != null => MakeComplexCreator(typeName: "C128",
                type: _c128Type,
                mag: mag,
                imaginary: imaginary,
                compLit: TokenType.B64Literal,
                compType: _b64Type,
                loc: loc),
            _ => null
        };
    }

    /// <summary>
    /// Extracts the bare magnitude digits from a complex literal's raw text: underscores are stripped,
    /// and for an imaginary literal the trailing <c>i</c>/<c>I</c> suffix is dropped as well.
    /// </summary>
    private static string ComplexMagnitude(string raw, bool imaginary)
    {
        if (!imaginary)
        {
            return raw.Replace(oldValue: "_", newValue: "");
        }

        int i = raw.LastIndexOfAny(anyOf: ['i', 'I']);
        return (i < 0 ? raw : raw[..i]).Replace(oldValue: "_", newValue: "");
    }

    /// <summary>Builds a fixed-width complex constructor from a single magnitude: an imaginary literal →
    /// <c>C(real: 0, imag: mag)</c>, a real literal → <c>C(real: mag, imag: 0)</c>. Components are float
    /// literals of <paramref name="compLit"/>.</summary>
    private static CreatorExpression MakeComplexCreator(string typeName, TypeSymbol type, string mag,
        bool imaginary, TokenType compLit, TypeSymbol? compType, SourceLocation loc)
    {
        var zero =
            new LiteralExpression(Value: "0.0", LiteralType: compLit, Location: loc)
            {
                ResolvedType = compType
            };
        var value =
            new LiteralExpression(Value: mag, LiteralType: compLit, Location: loc)
            {
                ResolvedType = compType
            };
        return new CreatorExpression(TypeName: typeName,
            TypeArguments: null,
            MemberVariables: imaginary
                ? [("real", zero), ("imag", value)]
                : [("real", value), ("imag", zero)],
            Location: loc) { ResolvedType = type };
    }

    /// <summary>
    /// Builds the make byte size creator used by later builder work.
    /// </summary>
    private CreatorExpression MakeByteSizeCreator(string text, SourceLocation loc)
    {
        ulong bytes = ComputeByteSizeValue(text: text);
        var valueLit = new LiteralExpression(Value: bytes.ToString(),
            LiteralType: TokenType.U64Literal,
            Location: loc);
        return new CreatorExpression(TypeName: "ByteSize",
            TypeArguments: null,
            MemberVariables: [("value", valueLit)],
            Location: loc) { ResolvedType = _byteSizeType };
    }

    /// <summary>
    /// Builds the make duration creator used by later builder work.
    /// </summary>
    private CreatorExpression MakeDurationCreator(string text, TokenType literalType,
        SourceLocation loc)
    {
        (long seconds, long nanoseconds) =
            ComputeDurationValues(text: text, literalType: literalType);
        var secsLit = new LiteralExpression(Value: seconds.ToString(),
            LiteralType: TokenType.S64Literal,
            Location: loc);
        var nsLit = new LiteralExpression(Value: nanoseconds.ToString(),
            LiteralType: TokenType.U32Literal,
            Location: loc);
        return new CreatorExpression(TypeName: "Duration",
            TypeArguments: null,
            MemberVariables: [("seconds", secsLit), ("nanoseconds", nsLit)],
            Location: loc) { ResolvedType = _durationType };
    }

    /// <summary>
    /// Builds the make character creator used by later builder work.
    /// </summary>
    private CreatorExpression MakeCharacterCreator(int codepoint, SourceLocation loc)
    {
        var cpLit = new LiteralExpression(Value: codepoint.ToString(),
            LiteralType: TokenType.U32Literal,
            Location: loc);
        return new CreatorExpression(TypeName: "Character",
            TypeArguments: null,
            MemberVariables: [("from", cpLit)],
            Location: loc) { ResolvedType = _characterType };
    }

    /// <summary>
    /// Builds the make byte creator used by later builder work.
    /// </summary>
    private CreatorExpression MakeByteCreator(int byteValue, SourceLocation loc)
    {
        var byteLit = new LiteralExpression(Value: byteValue.ToString(),
            LiteralType: TokenType.U8Literal,
            Location: loc);
        return new CreatorExpression(TypeName: "Byte",
            TypeArguments: null,
            MemberVariables: [("from", byteLit)],
            Location: loc) { ResolvedType = _byteType };
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Initializes a new instance with the dependencies required for its builder phase.
    /// </summary>
    private static readonly (string Suffix, ulong Multiplier)[] ByteSizeSuffixes =
    [
        ("gib", 1_073_741_824UL),
        ("mib", 1_048_576UL),
        ("kib", 1_024UL),
        ("gb", 1_000_000_000UL),
        ("mb", 1_000_000UL),
        ("kb", 1_000UL),
        ("b", 1UL)
    ];

    /// <summary>
    /// Performs the compute byte size value step for this builder phase.
    /// </summary>
    private static ulong ComputeByteSizeValue(string text)
    {
        string lower = text.ToLowerInvariant();
        foreach ((string suffix, ulong multiplier) in ByteSizeSuffixes)
        {
            if (!lower.EndsWith(value: suffix))
            {
                continue;
            }

            string numPart = text[..^suffix.Length]
                            .TrimEnd(trimChar: '_')
                            .Replace(oldValue: "_", newValue: "");
            if (ulong.TryParse(s: numPart, result: out ulong value))
            {
                return value * multiplier;
            }

            break;
        }

        return 0;
    }

    /// <summary>
    /// Initializes a new instance with the dependencies required for its builder phase.
    /// </summary>
    private static (long Seconds, long Nanoseconds) ComputeDurationValues(string text,
        TokenType literalType)
    {
        const long nsPerMicrosecond = 1_000L;
        const long nsPerMillisecond = 1_000_000L;
        const long nsPerSecond = 1_000_000_000L;
        const long secondsPerMinute = 60L;
        const long secondsPerHour = 3_600L;
        const long secondsPerDay = 86_400L;
        const long secondsPerWeek = 604_800L;

        string numericPart = literalType switch
        {
            TokenType.MillisecondLiteral => text[..^2],
            TokenType.MicrosecondLiteral => text[..^2],
            TokenType.NanosecondLiteral => text[..^2],
            _ => text[..^1]
        };
        numericPart = numericPart.Replace(oldValue: "_", newValue: "");
        if (!long.TryParse(s: numericPart, result: out long value))
        {
            value = 0;
        }

        long seconds = 0, nanoseconds = 0;
        switch (literalType)
        {
            case TokenType.WeekLiteral: seconds = value * secondsPerWeek; break;
            case TokenType.DayLiteral: seconds = value * secondsPerDay; break;
            case TokenType.HourLiteral: seconds = value * secondsPerHour; break;
            case TokenType.MinuteLiteral: seconds = value * secondsPerMinute; break;
            case TokenType.SecondLiteral: seconds = value; break;
            case TokenType.MillisecondLiteral:
                seconds = value / 1_000L;
                nanoseconds = value % 1_000L * nsPerMillisecond;
                break;
            case TokenType.MicrosecondLiteral:
                seconds = value / 1_000_000L;
                nanoseconds = value % 1_000_000L * nsPerMicrosecond;
                break;
            case TokenType.NanosecondLiteral:
                seconds = value / nsPerSecond;
                nanoseconds = value % nsPerSecond;
                break;
        }

        return (seconds, nanoseconds);
    }

    /// <summary>
    /// Returns whether is byte size literal type applies in the current builder context.
    /// </summary>
    private static bool IsByteSizeLiteralType(TokenType type)
    {
        return type is TokenType.ByteLiteral or TokenType.KilobyteLiteral
            or TokenType.KibibyteLiteral or TokenType.MegabyteLiteral or TokenType.MebibyteLiteral
            or TokenType.GigabyteLiteral or TokenType.GibibyteLiteral;
    }

    /// <summary>
    /// Returns whether is duration literal type applies in the current builder context.
    /// </summary>
    private static bool IsDurationLiteralType(TokenType type)
    {
        return type is TokenType.WeekLiteral or TokenType.DayLiteral or TokenType.HourLiteral
            or TokenType.MinuteLiteral or TokenType.SecondLiteral or TokenType.MillisecondLiteral
            or TokenType.MicrosecondLiteral or TokenType.NanosecondLiteral;
    }
}
