using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Decides every representation change a backend makes, so a backend carries it out without choosing it.
/// <list type="bullet">
/// <item>A declaration written with a scalar type whose initializer has another scalar representation
/// (<c>var e: U32 = wide</c>) gets its conversion as an explicit <see cref="BackendCastExpression"/> around
/// the initializer.</item>
/// <item>Every <see cref="BackendCastExpression"/> gets what it does to the bits stamped on it
/// (<see cref="BackendCastExpression.Conversion"/>).</item>
/// </list>
/// The choice follows the two types' declared representations (a record's <c>@llvm</c> type, an entity's
/// pointer): the same representation passes through, an integer and a pointer convert, integers truncate or
/// extend (zero-filled into an unsigned type, sign-filled otherwise), and integers and floats convert by the
/// integer's signedness. Runs at Phase 9 over every body that reaches a backend, after monomorphization, so
/// every type is concrete.
/// </summary>
internal sealed class RepresentationCastPass : AstRewriter
{
    /// <summary>A type's representation: an integer of a width and signedness, a float of a width, a pointer,
    /// or something else (an aggregate) with its spelling.</summary>
    private readonly record struct Shape(char Kind, int Bits, bool Unsigned, string Spelling);

    /// <summary>Makes the representation changes of <paramref name="body"/> explicit and stamps each one.</summary>
    public static void Run(Statement body)
    {
        // A routine body is a block: its statement list is rewritten in place (the body itself is
        // referenced from several places and cannot be replaced).
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(
                message: $"A routine body must be a block, got {body.GetType().Name}.");
        }

        var pass = new RepresentationCastPass();
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }

        AstWalker.WalkExpressions(root: body,
            visit: expression =>
            {
                if (expression is BackendCastExpression cast)
                {
                    cast.Conversion = Decide(source: cast.Value.ResolvedType, target: cast.ResolvedType);
                }
            });
    }

    /// <inheritdoc/>
    protected override Statement VisitDeclarationStatement(DeclarationStatement s)
    {
        Statement visited = base.VisitDeclarationStatement(s: s);
        if (visited is not DeclarationStatement
            {
                Declaration: VariableDeclaration
                {
                    Type.ResolvedType: RecordTypeSymbol { BackendType: not null } declared,
                    Initializer: { ResolvedType: RecordTypeSymbol { BackendType: not null } initial } initializer
                } declaration
            } statement || ShapeOf(type: declared).Spelling == ShapeOf(type: initial).Spelling)
        {
            return visited;
        }

        var cast = new BackendCastExpression(Value: initializer, Location: initializer.Location)
        {
            ResolvedType = declared
        };
        return statement with { Declaration = declaration with { Initializer = cast } };
    }

    private static RepresentationConversion Decide(TypeSymbol? source, TypeSymbol? target)
    {
        // An untyped value (a bare literal) is written in the target's own representation. A generic template
        // body is never emitted: each instantiation's concrete body is annotated (and its casts decided) itself.
        if (source is null || target is null || IsGeneric(type: source) || IsGeneric(type: target))
        {
            return RepresentationConversion.Same;
        }

        Shape from = ShapeOf(type: source);
        Shape to = ShapeOf(type: target);
        if (from.Spelling == to.Spelling)
        {
            return RepresentationConversion.Same;
        }

        return (from.Kind, to.Kind) switch
        {
            ('i', 'p') => RepresentationConversion.IntToPointer,
            ('p', 'i') => RepresentationConversion.PointerToInt,
            ('i', 'i') when from.Bits > to.Bits => RepresentationConversion.Truncate,
            ('i', 'i') when from.Bits < to.Bits => to.Unsigned
                ? RepresentationConversion.ZeroExtend
                : RepresentationConversion.SignExtend,
            ('i', 'i') => RepresentationConversion.Bitcast,
            ('f', 'f') => from.Bits > to.Bits
                ? RepresentationConversion.FloatTruncate
                : RepresentationConversion.FloatExtend,
            ('f', 'i') => to.Unsigned
                ? RepresentationConversion.FloatToUnsigned
                : RepresentationConversion.FloatToSigned,
            ('i', 'f') => from.Unsigned
                ? RepresentationConversion.UnsignedToFloat
                : RepresentationConversion.SignedToFloat,
            _ => throw new InvalidOperationException(
                message: $"A representation cast from {source.FullName} ({from.Spelling}) to {target.FullName} " +
                         $"({to.Spelling}) has no conversion.")
        };
    }

    /// <summary>True for a type that still has a generic parameter in it: one of a template body.</summary>
    private static bool IsGeneric(TypeSymbol type)
    {
        return type is GenericParameterTypeSymbol || type.IsGenericDefinition ||
               type.TypeArguments?.Any(predicate: IsGeneric) == true;
    }

    private static Shape ShapeOf(TypeSymbol type)
    {
        // A const-generic value (the `4` of `Array[S64, 4]`) is an integer of its literal's type, U64 when the
        // literal is untyped.
        if (type is ConstGenericValueTypeSymbol constant)
        {
            string name = constant.ExplicitTypeName ?? "U64";
            int bits = int.TryParse(s: name[1..], result: out int width) ? width : 64;
            return new Shape(Kind: 'i', Bits: bits, Unsigned: name[0] == 'U', Spelling: $"i{bits}");
        }

        bool unsigned = type is RecordTypeSymbol record &&
                        record.ImplementedProtocols.Any(predicate: p => p.Name == "UnsignedIntegral");
        string? backend = type switch
        {
            EntityTypeSymbol => "ptr",
            RecordTypeSymbol { BackendType: { } declared } => declared,
            _ => null
        };
        return backend switch
        {
            null => new Shape(Kind: 'a', Bits: 0, Unsigned: false, Spelling: type.FullName),
            "ptr" => new Shape(Kind: 'p', Bits: 0, Unsigned: false, Spelling: "ptr"),
            "half" => new Shape(Kind: 'f', Bits: 16, Unsigned: false, Spelling: backend),
            "float" => new Shape(Kind: 'f', Bits: 32, Unsigned: false, Spelling: backend),
            "double" => new Shape(Kind: 'f', Bits: 64, Unsigned: false, Spelling: backend),
            "fp128" => new Shape(Kind: 'f', Bits: 128, Unsigned: false, Spelling: backend),
            ['i', .. var digits] when int.TryParse(s: digits, result: out int bits) =>
                new Shape(Kind: 'i', Bits: bits, Unsigned: unsigned, Spelling: backend),
            _ => new Shape(Kind: 'a', Bits: 0, Unsigned: false, Spelling: backend)
        };
    }
}
