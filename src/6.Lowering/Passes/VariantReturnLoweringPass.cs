using Builder.Instantiation;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Phase 8: lowers builder-synthesized <see cref="VariantReturnStatement"/> nodes into ordinary AST
/// so codegen needs no carrier-construction special-casing (and the dump shows no <c>#carrier</c>
/// pseudo-op).
///
/// STAGE 1 (current): only the <see cref="ErrorHandlingVariantKind.TryBool"/> variant, whose carrier
/// is a plain <c>Bool</c>. A <c>FromReturn</c> site becomes <c>return true</c>; a
/// <c>FromThrow</c>/<c>FromAbsent</c> site becomes <c>return false</c>. The thrown error is discarded
/// in a TryBool context (the routine returns Bool, never the error) — matching codegen's
/// <c>EmitTryBoolVariantReturn</c>, which drops it — so it is simply never constructed: nothing to
/// clean up, no leak. Try (Maybe) / Check (Result) / Lookup carriers still lower in codegen and are
/// handled by later stages.
/// </summary>
internal sealed class VariantReturnLoweringPass(PostprocessingContext ctx) : AstRewriter
{
    private readonly Dictionary<string, Statement>? _variantBodies = ctx.VariantBodies;

    /// <summary>Return type (the concrete carrier, e.g. <c>Maybe[S64]</c>) of the routine whose body
    /// is being lowered — needed to construct the carrier record.</summary>
    private TypeSymbol? _carrierReturn;

    private Dictionary<string, TypeSymbol?>? _returnByKey;

    private Dictionary<string, TypeSymbol?> ReturnByKey => _returnByKey ??= ctx.Registry
       .GetAllRoutines()
       .GroupBy(keySelector: r => r.RegistryKey)
       .ToDictionary(keySelector: g => g.Key,
            elementSelector: g => g.First()
                                   .ReturnType);

    private TypeSymbol? _boolType;
    private TypeSymbol? BoolType => _boolType ??= ctx.Registry.LookupType(name: "Bool");

    /// <summary>A <c>Bool</c>-typed literal for a carrier's <c>present</c> flag.</summary>
    private LiteralExpression BoolLiteral(bool value, SourceLocation loc)
    {
        return new LiteralExpression(Value: value,
            LiteralType: value
                ? TokenType.True
                : TokenType.False,
            Location: loc) { ResolvedType = BoolType };
    }

    /// <summary>Builds `return Carrier(arm: value)` for a Check/Lookup carrier: no value is the absent arm, a thrown
    /// error (a crashable, or a caught <c>Crashables</c> thrown again) the error arm, anything else the success arm
    /// (<see cref="ConstructionLoweringPass"/> finds each in the carrier).</summary>
    private static ReturnStatement MakeCarrierReturn(RecordTypeSymbol carrier, Expression? payload, bool thrown,
        SourceLocation loc)
    {
        string arm = payload switch
        {
            null => "None",
            _ when thrown || payload.ResolvedType is CrashableTypeSymbol => Builder.Declaration.RuntimeContract.Crashables,
            _ => SuccessArm
        };
        return new ReturnStatement(Value: new CreatorExpression(TypeName: carrier.Name,
                TypeArguments: null,
                MemberVariables: [(arm, payload!)],
                Location: loc) { ResolvedType = carrier },
            Location: loc);
    }

    /// <summary>The arm name a carrier return gives its success value (the arm is the carrier's <c>T</c>, whatever it
    /// is once the carrier is concrete).</summary>
    internal const string SuccessArm = "T";

    /// <summary>Lowers routine bodies in a single program (user file or stdlib file).</summary>
    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(program: program, lower: LowerRoutineBody);
    }

    /// <summary>Lowers the synthesized try/grab/lookup variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        if (_variantBodies == null)
        {
            return;
        }

        BodyDispatch.RunOnVariantBodies(bodies: _variantBodies,
            lower: (key, body) =>
            {
                _carrierReturn = ReturnByKey.GetValueOrDefault(key: key);
                return VisitStatement(stmt: body);
            });
    }

    /// <summary>
    /// Per-routine body lowering shared by the program and member-list sweeps: the carrier return type
    /// is read off the routine's resolved info before the variant-return sites are lowered.
    /// </summary>
    private Statement LowerRoutineBody(RoutineDeclaration routine)
    {
        _carrierReturn = routine.ResolvedInfo?.ReturnType;
        return VisitStatement(stmt: routine.Body);
    }

    /// <summary>Lowers carrier-return sites inside monomorphized generic instances (e.g. a concrete
    /// <c>ListEmittable[Character].emit's try variant</c>), which are not part of the program/variant-body tracks.</summary>
    public void RunOnMonomorphizedBodies()
    {
        if (ctx.MonomorphizedBodies is not { } bodies)
        {
            return;
        }

        RunOnInstantiatedGenericBodies(bodies: bodies);
    }

    /// <summary>Lowers carrier-return sites in a supplied instantiated-body map — used by the demand
    /// collector's <c>LowerFreshBodies</c>, whose freshly-built variant bodies (a composed iterator's
    /// the <c>try</c> variant of <c>emit</c> built via path-2) are NOT in <see cref="PostprocessingContext.MonomorphizedBodies"/>
    /// and so would otherwise reach codegen with un-lowered <see cref="VariantReturnStatement"/> carriers.</summary>
    public void RunOnInstantiatedGenericBodies(Dictionary<string, MonomorphizedBody> bodies)
    {
        BodyDispatch.RunOnInstantiatedGenericBodies(bodies: bodies,
            lower: (_, mono) =>
            {
                _carrierReturn = mono.Info.ReturnType;
                return VisitStatement(stmt: mono.Ast.Body);
            });
    }

    private ReturnStatement LowerTryBoolVariant(VariantReturnStatement vr)
    {
        return new ReturnStatement(Value: BoolLiteral(value: vr.SiteKind == VariantSiteKind.FromReturn,
                loc: vr.Location),
            Location: vr.Location);
    }

    // Try → Maybe[T] (a plain `{present: Bool, value: T}` record) built with a real
    // CreatorExpression: present carries the value; throw / absent / return-a-crashable = absent.
    private ReturnStatement LowerTryVariant(VariantReturnStatement vr, RecordTypeSymbol maybe)
    {
        if (vr.SiteKind == VariantSiteKind.FromVariantPassthrough && vr.Value != null)
        {
            return new ReturnStatement(Value: vr.Value, Location: vr.Location);
        }

        bool present = vr.SiteKind == VariantSiteKind.FromReturn &&
                       vr.Value?.ResolvedType is not CrashableTypeSymbol;
        bool hasValue = present && vr.Value is not null
            and not IdentifierExpression { Name: "None" };

        var members = new List<(string Name, Expression Value)>
        {
            ("present", BoolLiteral(value: present, loc: vr.Location))
        };
        if (hasValue)
        {
            members.Add(item: ("value", vr.Value!));
        }

        return new ReturnStatement(
            Value: new CreatorExpression(TypeName: maybe.Name,
                TypeArguments: null,
                MemberVariables: members,
                Location: vr.Location) { ResolvedType = maybe },
            Location: vr.Location);
    }

    // Check / Lookup: build the variant from the arm the value goes in. Absent (and a `none` return) is the
    // None arm, a thrown crashable or a rethrown Crashables the error arm, a returned value the success arm.
    private Statement LowerCheckLookupVariant(Statement statement, VariantReturnStatement vr,
        RecordTypeSymbol carrier)
    {
        if (vr.SiteKind == VariantSiteKind.FromVariantPassthrough && vr.Value != null)
        {
            return new ReturnStatement(Value: vr.Value, Location: vr.Location);
        }

        if (vr.SiteKind == VariantSiteKind.FromAbsent ||
            vr.SiteKind == VariantSiteKind.FromReturn &&
            vr.Value is null or IdentifierExpression { Name: "None" })
        {
            return MakeCarrierReturn(carrier: carrier, payload: null, thrown: false, loc: vr.Location);
        }

        if (vr.Value is { ResolvedType: not null } || vr is { SiteKind: VariantSiteKind.FromThrow, Value: not null })
        {
            return MakeCarrierReturn(carrier: carrier, payload: vr.Value,
                thrown: vr.SiteKind == VariantSiteKind.FromThrow, loc: vr.Location);
        }

        // No resolved type on the value — leave for codegen.
        return statement;
    }

    /// <summary>
    /// The only node this pass rewrites: a synthesized <see cref="VariantReturnStatement"/> becomes an
    /// ordinary <c>return</c> of the concrete carrier. All structural recursion (blocks, if/while/loop/
    /// each/when/danger/using bodies) is supplied by <see cref="AstRewriter"/>. The
    /// <c>VariantReturnStatement</c> carries no rewritable children of interest here — its
    /// <c>Value</c> is placed verbatim into the constructed carrier — so this override does not recurse.
    /// </summary>
    protected override Statement VisitVariantReturn(VariantReturnStatement s)
    {
        return s switch
        {
            { VariantKind: ErrorHandlingVariantKind.TryBool } => LowerTryBoolVariant(vr: s),
            // Try → Maybe[T] (a plain `{present: Bool, value: T}` record) built with a real
            // CreatorExpression: present carries the value; throw / absent / return-a-crashable = absent.
            { VariantKind: ErrorHandlingVariantKind.Try } when
                _carrierReturn is RecordTypeSymbol maybe => LowerTryVariant(vr: s, maybe: maybe),
            // Check / Lookup: a variant built from the arm the value goes in.
            { VariantKind: ErrorHandlingVariantKind.Check or ErrorHandlingVariantKind.Lookup } when
                _carrierReturn is RecordTypeSymbol carrier => LowerCheckLookupVariant(statement: s,
                    vr: s,
                    carrier: carrier),
            _ => s
        };
    }
}
