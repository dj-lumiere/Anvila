using Builder.Declaration;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Instantiation;

/// <summary>
/// Generates error handling variants for failable (!) routines.
///
/// Generation rules based on throw/absent usage:
/// - Only absent: try (returns T?)
/// - Only throw: try (returns T?) + grab (returns Result&lt;T&gt;)
/// - Both: try (returns T?) + lookup (returns Lookup&lt;T&gt;)
///
/// Phase 1: Keyword Detection - scan for throw/absent in body
/// Phase 2: Variant Generation - determine which variants to create
/// Phase 3: Code Transformation - generate variant routines
/// </summary>
public sealed class ErrorHandlingGenerator
{
    private const string NoneTypeName = "None";

    private readonly TypeRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorHandlingGenerator"/> class.
    /// </summary>
    /// <param name="registry">The type registry for lookups and registration.</param>
    public ErrorHandlingGenerator(TypeRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// The <see cref="RoutineKind"/> a failable variant must carry. For a NON-creator base the variant keeps
    /// the base's kind. A CREATOR base becomes a <see cref="RoutineKind.CommonRoutine"/> — a STATIC member
    /// routine (owner-scoped, NO <c>me</c> receiver, like a <c>common routine</c>): a creator-kind variant would
    /// be treated as one of the type's constructors, and a plain <see cref="RoutineKind.MemberRoutine"/> gets a
    /// synthetic <c>me</c> receiver at codegen (<c>OwnerType != null &amp;&amp; !IsCommon</c>) that the
    /// creator does not have, so the call (which passes only <c>from_x</c>) would ABI-mismatch the definition.
    /// </summary>
    private static RoutineKind VariantKind(RoutineInfo original)
    {
        return original.IsCreator
            ? RoutineKind.CommonRoutine
            : original.Kind;
    }

    /// <summary>
    /// The <see cref="RoutineInfo.MeType"/> a failable variant must carry. A CREATOR has no receiver (it
    /// CONSTRUCTS the owner), so its demoted member-routine variant (see <see cref="VariantKind"/>) must keep
    /// <c>MeType = null</c> — a STATIC member routine like a <c>common routine</c>. Copying the creator's own
    /// MeType would give the variant a spurious <c>me</c> receiver, shifting the ABI (the caller passes only
    /// the <c>from_x</c> arg, but the def expects <c>me</c> first) → garbage/absent result. Non-creator bases
    /// keep their receiver.
    /// </summary>
    private static TypeSymbol? VariantMeType(RoutineInfo original)
    {
        return original.IsCreator
            ? null
            : original.MeType;
    }

    /// <summary>
    /// Analyzes a failable routine and generates appropriate variants.
    /// </summary>
    /// <param name="routine">The routine to analyze.</param>
    /// <param name="body">The routine's body statement.</param>
    /// <returns>The result containing generated variants and any errors.</returns>
    public ErrorHandlingResult GenerateVariants(RoutineInfo routine, Statement body)
    {
        return GenerateVariants(routine: routine, body: body, pessimistic: false);
    }

    /// <summary>
    /// Generates wrapper variants. When <paramref name="pessimistic"/> is true, the analysis is
    /// forced to <c>HasThrow=true, HasAbsent=true</c> regardless of body contents — used by
    /// pre-registration for failable routines whose body has no direct <c>throw</c>/<c>absent</c>
    /// (failability is propagated from called <c>!</c> routines) so that <c>try</c>/<c>lookup</c>
    /// stub variants exist by name for SA resolution. <see cref="ErrorHandlingVariantPass"/>
    /// later refines them after fixpoint propagation.
    /// </summary>
    /// <param name="everyKind">Generate try, grab and lookup whatever the body's own throw/absent say, and
    /// accept a routine that is not failable. A user routine gets every kind: what can fail beneath it (a
    /// checked operator, a subscript, a call of a routine that is not failable but fails beneath its own call)
    /// is only known once its body is built, and each keyword must keep that failure's error.</param>
    /// <param name="withLookup">Generate the lookup variant as well whatever the body says: an iterator's
    /// <c>emit</c> needs it for an <c>each</c> loop beneath a recovery keyword, whose step must tell the end of the
    /// iteration (absent) apart from a failure beneath it (an error).</param>
    public ErrorHandlingResult GenerateVariants(RoutineInfo routine, Statement body,
        bool pessimistic, bool everyKind = false, bool withLookup = false)
    {
        if (!routine.IsFailable && !everyKind)
        {
            return ErrorHandlingResult.Empty;
        }

        // Phase 1: Keyword Detection (+ pessimistic override + propagated failability merge).
        ErrorHandlingAnalysis analysis =
            BuildFailabilityAnalysis(routine: routine, body: body, pessimistic: pessimistic);

        // Validate: ! functions must use throw, absent, or call other failable functions
        if (analysis is { HasThrow: false, HasAbsent: false })
        {
            return new ErrorHandlingResult
            {
                Error = $"Failable function '{routine.Name}!' must use 'throw' or 'absent'",
                HasThrow = false,
                HasAbsent = false
            };
        }

        // Phase 2: Variant Generation
        List<GeneratedVariant> variants =
            BuildVariants(routine: routine, analysis: analysis, everyKind: everyKind, withLookup: withLookup);

        return new ErrorHandlingResult
        {
            Variants = variants,
            HasThrow = analysis.HasThrow,
            HasAbsent = analysis.HasAbsent,
            ThrownTypes = analysis.ThrownTypes.ToList()
        };
    }

    /// <summary>
    /// Phase 1: builds the throw/absent analysis for a failable routine — scans the body,
    /// applies the <paramref name="pessimistic"/> override, merges propagated failability from
    /// called <c>!</c> routines, and applies the failable-calls fallback.
    /// </summary>
    private static ErrorHandlingAnalysis BuildFailabilityAnalysis(RoutineInfo routine,
        Statement body, bool pessimistic)
    {
        ErrorHandlingAnalysis analysis = AnalyzeBody(body: body);

        if (pessimistic)
        {
            analysis.HasThrow = true;
            analysis.HasAbsent = true;
        }

        // Propagated failability: callees' HasThrow/HasAbsent/ThrowableTypes are merged in
        // here. ErrorHandlingVariantPass runs a fixpoint over FailableCallees beforehand
        // so by the time we land here, routine.HasThrow/HasAbsent already reflect the
        // transitive closure for routines whose failability is purely propagated
        // (e.g. `routine S64_from_text!(t: Text) -> S64 return S64!(from_text: t)`).
        if (routine.HasThrow)
        {
            analysis.HasThrow = true;
        }

        if (routine.HasAbsent)
        {
            analysis.HasAbsent = true;
        }

        analysis.ThrownTypes.UnionWith(other: routine.ThrowableTypes);

        // If no direct or propagated throw/absent info but the routine calls failable
        // routines, conservatively assume throw (legacy behavior for arithmetic-overflow
        // crashable calls etc.).
        if (analysis is { HasThrow: false, HasAbsent: false } && routine.HasFailableCalls)
        {
            analysis.HasThrow = true;
        }

        return analysis;
    }

    /// <summary>
    /// Phase 2: builds the list of wrapper variants (try always; grab for throw-only;
    /// lookup for throw+absent) for a failable routine from its <paramref name="analysis"/>. With
    /// <paramref name="everyKind"/> (a routine recovered beneath its call, whose failures are only known once its
    /// body is built) all three are generated: its grab turns an absence beneath it into AbsentValueError.
    /// </summary>
    private List<GeneratedVariant> BuildVariants(RoutineInfo routine,
        ErrorHandlingAnalysis analysis, bool everyKind = false, bool withLookup = false)
    {
        var variants = new List<GeneratedVariant>();

        // try variant is always generated
        RoutineInfo tryVariant = GenerateTryVariant(original: routine);
        variants.Add(item: new GeneratedVariant(Kind: ErrorHandlingVariantKind.Try,
            Routine: tryVariant));

        // grab variant if only throw (no absent)
        if (analysis is { HasThrow: true, HasAbsent: false } || everyKind)
        {
            RoutineInfo checkVariant = GenerateCheckVariant(original: routine);
            variants.Add(item: new GeneratedVariant(Kind: ErrorHandlingVariantKind.Check,
                Routine: checkVariant));
        }

        // lookup variant if both throw and absent
        if (analysis is { HasThrow: true, HasAbsent: true } || everyKind || withLookup)
        {
            RoutineInfo lookupVariant = GenerateLookupVariant(original: routine);
            // Lookup[None] degenerates to grab (Result[None]) when the return type is None:
            // absent and return are both None so only throw vs no-throw matters.
            // Use Check kind so TransformBody emits Result carriers in the variant body —
            // if Lookup kind is used, the body emits Lookup[None] but the declaration says Result[None].
            ErrorHandlingVariantKind lookupKind =
                routine.ReturnType == null || routine.ReturnType.Name == NoneTypeName
                    ? ErrorHandlingVariantKind.Check
                    : ErrorHandlingVariantKind.Lookup;
            variants.Add(item: new GeneratedVariant(Kind: lookupKind, Routine: lookupVariant));
        }

        return variants;
    }

    /// <summary>
    /// Phase 1: Analyzes the body for throw/absent keywords.
    /// </summary>
    /// <param name="body">The statement body to analyze.</param>
    /// <returns>Analysis result with throw/absent flags.</returns>
    public static ErrorHandlingAnalysis AnalyzeBody(Statement body)
    {
        var analysis = new ErrorHandlingAnalysis();
        AnalyzeStatementRecursive(statement: body, analysis: analysis);
        return analysis;
    }

    /// <summary>
    /// Quick check: returns true if the body contains at least one throw or absent statement.
    /// Used to filter bodies before storing them for variant generation.
    /// </summary>
    public static bool BodyHasThrowOrAbsent(Statement body)
    {
        ErrorHandlingAnalysis analysis = AnalyzeBody(body: body);
        return analysis.HasThrow || analysis.HasAbsent;
    }

    /// <summary>
    /// Recursively analyzes statements for throw/absent keywords.
    /// </summary>
    /// <param name="statement">The statement to analyze.</param>
    /// <param name="analysis">The analysis result to update.</param>
    private static void AnalyzeStatementRecursive(Statement statement,
        ErrorHandlingAnalysis analysis)
    {
        switch (statement)
        {
            case ThrowStatement { IsFatal: true }:
                // `pierce` is an uncatchable crash — it does not make the routine recoverably
                // failable, so it contributes no throw/variant surface.
                break;

            case ThrowStatement ts:
                analysis.HasThrow = true;
                if (ts.Error?.ResolvedType is { } thrownType)
                {
                    analysis.ThrownTypes.Add(item: thrownType);
                }

                break;

            case AbsentStatement:
                analysis.HasAbsent = true;
                break;

            case BlockStatement block:
                foreach (Statement stmt in block.Statements)
                {
                    AnalyzeStatementRecursive(statement: stmt, analysis: analysis);
                }

                break;

            case IfStatement ifStmt:
                AnalyzeStatementRecursive(statement: ifStmt.ThenStatement, analysis: analysis);
                if (ifStmt.ElseStatement != null)
                {
                    AnalyzeStatementRecursive(statement: ifStmt.ElseStatement, analysis: analysis);
                }

                break;

            case WhileStatement whileStmt:
                AnalyzeStatementRecursive(statement: whileStmt.Body, analysis: analysis);
                break;

            case EachStatement eachStmt:
                AnalyzeStatementRecursive(statement: eachStmt.Body, analysis: analysis);
                break;

            case WhenStatement whenStmt:
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    AnalyzeStatementRecursive(statement: clause.Body, analysis: analysis);
                }

                break;

            case DangerStatement dangerStmt:
                AnalyzeStatementRecursive(statement: dangerStmt.Body, analysis: analysis);
                break;

            case LoopStatement loopStmt:
                AnalyzeStatementRecursive(statement: loopStmt.Body, analysis: analysis);
                break;
        }
    }

    /// <summary>
    /// Generates the try variant (returns Maybe&lt;T&gt;).
    /// throw -> return None
    /// absent -> return None
    /// </summary>
    /// <param name="original">The original routine.</param>
    /// <returns>The try variant routine info.</returns>
    /// <summary>
    /// Generates only a try variant for a failable routine. Used for bodyless protocol
    /// memberRoutines (e.g. <c>Iterator[T].emit!</c>) so that for-loop desugaring's call to
    /// <c>iter.emit() (under try)</c> resolves when <c>iter</c> is typed as the bare protocol.
    /// </summary>
    public RoutineInfo GenerateTryVariantStub(RoutineInfo original)
    {
        return GenerateTryVariant(original: original);
    }

    /// <summary>
    /// Generates only a lookup variant for a bodyless protocol <c>emit</c>: the step of an <c>each</c> loop in a
    /// recovery variant of a generic routine, over an iterator typed as the bare protocol, takes it (its absent
    /// state ends the loop, its error state is a failure beneath the step).
    /// </summary>
    public RoutineInfo GenerateLookupVariantStub(RoutineInfo original)
    {
        return GenerateLookupVariant(original: original);
    }

    private RoutineInfo GenerateTryVariant(RoutineInfo original)
    {
        TypeSymbol noneType = _registry.LookupType(name: NoneTypeName) ??
                            throw new InvalidOperationException(
                                message: "None type not registered");
        TypeSymbol returnType = original.ReturnType ?? noneType;

        // the try variant of a None-returning routine returns Bool (true=success, false=absent/throw)
        // Maybe[None] = { i1, void } is not valid LLVM, so Bool is used directly.
        if (returnType.Name == NoneTypeName)
        {
            TypeSymbol boolType = _registry.LookupType(name: "Bool") ??
                                throw new InvalidOperationException(
                                    message: "Bool type not registered");

            return new RoutineInfo(name: original.Name)
            {
                Kind = VariantKind(original: original),
                OwnerType = original.OwnerType,
                MeType = VariantMeType(original: original),
                Parameters = original.Parameters,
                ReturnType = boolType,
                IsFailable = false,
                IsSynthesized = true,
                DeclaredMutation = original.DeclaredMutation,
                MutationCategory = original.MutationCategory,
                GenericParameters = original.GenericParameters,
                GenericConstraints = original.GenericConstraints,
                Visibility = original.Visibility,
                Location = original.Location,
                Module = original.Module,
                ModulePath = original.ModulePath,
                Annotations = original.Annotations,
                CallingConvention = original.CallingConvention,
                FailableVariant = FailableVariant.TryBool,
                RecoveryOf = original,
                SurfaceRealm = original.SurfaceRealm,
                Recovery = RecoveryKind.Try
            };
        }

        TypeSymbol carrierInner = WrapBareEntityForCarrier(type: returnType);

        TypeSymbol maybeDef = _registry.LookupType(name: "Maybe") ??
                            throw new InvalidOperationException(
                                message: "Maybe type not registered");
        TypeSymbol maybeType = _registry.GetOrCreateResolution(
            genericDef: maybeDef,
            typeArguments: [carrierInner]);

        return new RoutineInfo(name: original.Name)
        {
            Kind = VariantKind(original: original),
            OwnerType = original.OwnerType,
            MeType = VariantMeType(original: original),
            Parameters = original.Parameters,
            ReturnType = maybeType,
            IsFailable = false, // try variants don't fail
            IsSynthesized = true,
            DeclaredMutation = original.DeclaredMutation,
            MutationCategory = original.MutationCategory,
            GenericParameters = original.GenericParameters,
            GenericConstraints = original.GenericConstraints,
            Visibility = original.Visibility,
            Location = original.Location,
            Module = original.Module,
            ModulePath = original.ModulePath,
            Annotations = original.Annotations,
            CallingConvention = original.CallingConvention,
            RecoveryOf = original,
            SurfaceRealm = original.SurfaceRealm,
            Recovery = RecoveryKind.Try
        };
    }

    /// <summary>
    /// Generates the grab variant (returns Result&lt;T&gt;).
    /// throw -> return error
    /// </summary>
    /// <param name="original">The original routine.</param>
    /// <returns>The grab variant routine info.</returns>
    private RoutineInfo GenerateCheckVariant(RoutineInfo original)
    {
        // grab returns Result[T] — success carries T, throw carries the error.
        TypeSymbol innerType = original.ReturnType ?? _registry.LookupType(name: NoneTypeName) ??
            throw new InvalidOperationException(message: "None type not registered");

        TypeSymbol carrierInner = WrapBareEntityForCarrier(type: innerType);

        TypeSymbol resultDef = _registry.LookupType(name: "Check") ??
                             throw new InvalidOperationException(
                                 message: "Result type not registered");
        TypeSymbol resultType = _registry.GetOrCreateResolution(
            genericDef: resultDef,
            typeArguments: [carrierInner]);

        return new RoutineInfo(name: original.Name)
        {
            Kind = VariantKind(original: original),
            OwnerType = original.OwnerType,
            MeType = VariantMeType(original: original),
            Parameters = original.Parameters,
            ReturnType = resultType,
            IsFailable = false, // grab variants don't fail
            IsSynthesized = true,
            DeclaredMutation = original.DeclaredMutation,
            MutationCategory = original.MutationCategory,
            GenericParameters = original.GenericParameters,
            GenericConstraints = original.GenericConstraints,
            Visibility = original.Visibility,
            Location = original.Location,
            Module = original.Module,
            ModulePath = original.ModulePath,
            Annotations = original.Annotations,
            CallingConvention = original.CallingConvention,
            RecoveryOf = original,
            SurfaceRealm = original.SurfaceRealm,
            Recovery = RecoveryKind.Grab
        };
    }

    /// <summary>
    /// Generates the lookup variant (returns Lookup&lt;T&gt;).
    /// throw -> return error
    /// absent -> return None
    /// </summary>
    /// <param name="original">The original routine.</param>
    /// <returns>The lookup variant routine info.</returns>
    private RoutineInfo GenerateLookupVariant(RoutineInfo original)
    {
        TypeSymbol noneType = _registry.LookupType(name: NoneTypeName) ??
                            throw new InvalidOperationException(
                                message: "None type not registered");
        TypeSymbol returnType = original.ReturnType ?? noneType;

        // Lookup[None] degenerates to Result[None]: absent and return are both None,
        // so the only distinction is throw vs no-throw — same as grab.
        if (returnType.Name == NoneTypeName)
        {
            TypeSymbol resultDef = _registry.LookupType(name: "Check") ??
                                 throw new InvalidOperationException(
                                     message: "Result type not registered");
            TypeSymbol resultType = _registry.GetOrCreateResolution(
                genericDef: resultDef,
                typeArguments: [noneType]);

            // Degenerated: Lookup[None] -> Result[None], and the API name becomes grab not lookup
            return new RoutineInfo(name: original.Name)
            {
                Kind = VariantKind(original: original),
                OwnerType = original.OwnerType,
                MeType = VariantMeType(original: original),
                Parameters = original.Parameters,
                ReturnType = resultType,
                IsFailable = false,
                IsSynthesized = true,
                DeclaredMutation = original.DeclaredMutation,
                MutationCategory = original.MutationCategory,
                GenericParameters = original.GenericParameters,
                GenericConstraints = original.GenericConstraints,
                Visibility = original.Visibility,
                Location = original.Location,
                Module = original.Module,
                ModulePath = original.ModulePath,
                Annotations = original.Annotations,
                CallingConvention = original.CallingConvention,
                RecoveryOf = original,
                SurfaceRealm = original.SurfaceRealm,
                Recovery = RecoveryKind.Grab
            };
        }

        TypeSymbol carrierInner = WrapBareEntityForCarrier(type: returnType);

        TypeSymbol lookupDef = _registry.LookupType(name: "Lookup") ??
                             throw new InvalidOperationException(
                                 message: "Lookup type not registered");
        TypeSymbol lookupType = _registry.GetOrCreateResolution(
            genericDef: lookupDef,
            typeArguments: [carrierInner]);

        return new RoutineInfo(name: original.Name)
        {
            Kind = VariantKind(original: original),
            OwnerType = original.OwnerType,
            MeType = VariantMeType(original: original),
            Parameters = original.Parameters,
            ReturnType = lookupType,
            IsFailable = false, // lookup variants don't fail
            IsSynthesized = true,
            DeclaredMutation = original.DeclaredMutation,
            MutationCategory = original.MutationCategory,
            GenericParameters = original.GenericParameters,
            GenericConstraints = original.GenericConstraints,
            Visibility = original.Visibility,
            Location = original.Location,
            Module = original.Module,
            ModulePath = original.ModulePath,
            Annotations = original.Annotations,
            CallingConvention = original.CallingConvention,
            RecoveryOf = original,
            SurfaceRealm = original.SurfaceRealm,
            Recovery = RecoveryKind.Lookup
        };
    }

    /// <summary>
    /// Carrier-shape adjustment for failable return types. Post-Owned-retirement,
    /// bare entity <c>T</c> IS the lvalue/bound form, so <c>Maybe[T]</c> /
    /// <c>Result[T]</c> / <c>Lookup[T]</c> over a bare entity is the correct shape:
    /// the carrier owns the bound entity directly, no <c>T</c> intermediary.
    /// Identity for all inputs; retained for the call-site hook in case future
    /// carrier-element transforms (e.g., needs-RecordType relaxation) want a single
    /// chokepoint.
    /// </summary>
    private static TypeSymbol WrapBareEntityForCarrier(TypeSymbol type)
    {
        return type;
    }
}
