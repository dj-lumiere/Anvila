using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Instantiation.Passes;

/// <summary>
/// Lowers protocol default-impl routines to per-implementer routines at the AST level.
///
/// A protocol-extension routine such as
/// <code>routine Iterable[Text].join(separator: Text) -> Text</code>
/// carries a real body whose <c>me</c> is typed as the protocol itself. The body's
/// nested calls (e.g. <c>for part in me</c> -> <c>me.iter()</c> / <c>me.emit!()</c>)
/// cannot resolve statically against an abstract protocol owner; codegen needs the
/// concrete implementer.
///
/// Approach (per Rust's trait-default-memberRoutine monomorphization):
/// for every call site that resolves to a protocol-default-impl routine with a body,
/// synthesize a per-(protocol-routine, implementer) routine whose owner is the
/// implementer. The synthesized routine reuses the protocol routine's AST body with
/// <c>me</c> rebound to the implementer; nested calls then re-resolve against the
/// implementer's memberRoutines through the standard GMP path.
///
/// Pipeline placement: runs inside <see cref="GenericClosurePass"/> before GMP, so the
/// synthesized routines flow through the normal monomorphization machinery and land
/// in <see cref="InstantiationContext.InstantiatedGenericBodies"/>.
///
/// Codegen is unmodified — by the time it sees the call site, <c>ResolvedRoutine</c>
/// already points at the implementer-owned synthesized routine, so the existing
/// mangling and emission paths "just work" (symbol owner = implementer).
/// </summary>
internal sealed class ProtocolDefaultImplLoweringPass(InstantiationContext ctx)
{
    /// <summary>(protocolRoutineKey, implementerFullName) -> synthesized per-implementer routine.</summary>
    private readonly Dictionary<(string ProtoKey, string ImplKey), RoutineInfo> _synthesized =
        new();

    /// <summary>(resolved call routine key, implementerFullName) -> the body made for a call whose member routine has
    /// type parameters of its own (<c>select[U]</c>): a call resolved on the protocol (<c>Emittable[S64].select</c>)
    /// is rebound to it, as a call resolved on the implementer already names it.</summary>
    private readonly Dictionary<(string CallKey, string ImplKey), RoutineInfo> _synthesizedForCall = new();

    /// <summary>
    /// Synthesizes per-implementer protocol-default-impl bodies for every call site reachable from
    /// the current body set, iterating to a local fixed point. Returns <c>true</c> if any new body
    /// was synthesized — the caller re-runs GMP + this pass so that protocol-default calls appearing
    /// only inside GMP-monomorphized bodies (e.g. <c>source.List()</c> inside <c>ReverseIterator.iter</c>)
    /// are also lowered.
    /// </summary>
    /// <summary>
    /// Bodies already scanned (by reference). Persists across Run() calls AND across this Run's
    /// fixed-point rounds so a post-GMP re-run only walks bodies synthesized/monomorphized since the
    /// last pass — re-walking the entire (post-GMP) body set every round is the dominant cost and made
    /// the PDIL→GMP fixed point unusably slow. A body's protocol-default call sites are resolvable on
    /// first scan (monomorphized receivers are concrete), so re-scanning never surfaces new work.
    /// </summary>
    private readonly HashSet<Statement> _walkedBodies =
        new(comparer: ReferenceEqualityComparer.Instance);

    public bool Run()
    {
        // Iterate until fixed point: synthesizing per-implementer bodies may surface new
        // call sites (the cloned body has its own protocol-default-impl calls). Each round processes
        // only the bodies not yet scanned, so the work is bounded by the delta.
        bool changed;
        bool synthesizedAny = false;
        do
        {
            var freshBodies = EnumerateLiveRoutineBodies()
                             .Where(predicate: b => !_walkedBodies.Contains(item: b))
                             .ToList();
            if (freshBodies.Count == 0)
            {
                break;
            }

            changed = DiscoverAndSynthesize(bodies: freshBodies);
            if (changed)
            {
                synthesizedAny = true;
                RewriteCallSites(bodies: freshBodies);
            }

            foreach (Statement b in freshBodies)
            {
                _walkedBodies.Add(item: b);
            }
        } while (changed);

        return synthesizedAny;
    }

    /// <summary>
    /// Walks the supplied routine bodies looking for calls to protocol-default-impl routines.
    /// For each unique (protocolRoutine, implementer) pair not yet synthesized, creates the
    /// per-implementer RoutineInfo, clones the AST body, registers both with the registry
    /// and the instantiation context.
    /// </summary>
    private bool DiscoverAndSynthesize(IReadOnlyList<Statement> bodies)
    {
        bool added = false;
        foreach (Statement body in bodies)
        {
            AstWalker.WalkExpressions(root: body,
                visit: expr =>
                {
                    if (SynthesizeForCallExpression(expr: expr))
                    {
                        added = true;
                    }
                });
        }

        return added;
    }

    /// <summary>
    /// Inspects one expression for a protocol-default-impl call and, if found, synthesizes the
    /// per-implementer body (member-generic path or the standard path). Returns true if a new body
    /// was synthesized.
    /// </summary>
    private bool SynthesizeForCallExpression(Expression expr)
    {
        // Handle both the lowered call form and the still-generic memberRoutine-call form: explicit
        // memberRoutine type args (e.g. `select_many[S64](…)`) reach PDIL as a
        // GenericMemberRoutineCallExpression because GenericCallLoweringPass runs AFTER PDIL. Without
        // this its per-implementer body (List[S64].select_many) is never synthesized.
        if (!TryGetProtocolDefaultCallParts(expr: expr,
                resolvedRoutine: out RoutineInfo? rr0,
                receiverType: out TypeSymbol? recvType0,
                rebind: out _))
        {
            return false;
        }

        // A recovery variant of a protocol's own routine (`try xs.select(...).to[List]()`) is built for the
        // implementer from the protocol's body, as the routine itself is below.
        if (rr0 is { IsRecoveryVariant: true })
        {
            return SynthesizeRecoveryVariantBody(variant: rr0);
        }

        if (!TryResolveProtocolDefaultImpl(resolvedRoutine: rr0,
                receiverResolvedType: recvType0,
                protoRoutine: out RoutineInfo? pr,
                implementer: out TypeSymbol? implOrNull) || pr == null || implOrNull == null)
        {
            return false;
        }

        TypeSymbol impl = implOrNull;

        // memberRoutine-generic resolution (e.g. `List[Text].zip[S64, List[S64]]`): the call already
        // resolved to a fully-concrete resolution `rr` whose memberRoutine type-arguments bind the
        // protocol body's memberRoutine generics. SynthesizePerImplementer drops memberRoutine generics, so
        // instead generate the body FOR `rr` directly — substituting the protocol's own params
        // (T→Text) AND the memberRoutine generics (U→S64, S2→List[S64]) — keyed by rr's own
        // RegistryKey (which is exactly the symbol the call site emits).
        RoutineInfo rr = rr0!;
        // pr.GenericParameters lists the protocol owner's params first (e.g. T) then the
        // memberRoutine-level params (U, S2); rr.TypeArguments holds only the memberRoutine args. Align them
        // from the END so the trailing memberRoutine generics bind, while the owner param (T) is
        // supplied separately by BuildProtocolGenericSubs (T→Text from the conformance).
        // A specialization's bracket (`Iterable[T].to[Set[T]]`, resolved as `to[Set[S64]]`) picks the member of its
        // family and binds no parameter: it is carried on the synthesized routine's name, never read as `T`.
        bool specialization = (rr.RecoveryOf ?? rr).IsSpecialization || pr.IsSpecialization;
        if (!specialization && rr.TypeArguments is { Count: > 0 } memberRoutineArgs &&
            pr.GenericParameters is { Count: > 0 } allParams &&
            memberRoutineArgs.Count <= allParams.Count)
        {
            return SynthesizeMemberGenericBody(pr: pr,
                rr: rr,
                impl: impl,
                memberRoutineArgs: memberRoutineArgs,
                allParams: allParams);
        }

        return SynthesizeStandardBody(pr: pr, impl: impl,
            typeArguments: specialization ? rr.TypeArguments : null);
    }

    /// <summary>
    /// Builds the body of <paramref name="variant"/>, the recovery variant of a protocol's own routine made concrete
    /// for an implementer (<c>Emittable[T].to[List[T]]</c>'s <c>try</c> variant on a <c>SelectManyEmittable</c>),
    /// from the protocol's analyzed body: cloned for the implementer with the protocol's parameters bound by the
    /// conformance (the protocol's <c>T</c> is what the implementer emits, not the implementer's own <c>T</c>), then
    /// turned into the variant. The variant built once from the definition keeps the protocol's abstract types, so
    /// its teardown cannot tell an entity element from a value. The body is stored under the variant's own key,
    /// which the call already names. Returns true if a new body was added.
    /// </summary>
    private bool SynthesizeRecoveryVariantBody(RoutineInfo variant)
    {
        if (variant is not { RecoveryOf: { } recovered, OwnerType: RecordTypeSymbol or EntityTypeSymbol } ||
            variant.GenericDefinition?.OwnerType is not ProtocolTypeSymbol ||
            ctx.InstantiatedGenericBodies.ContainsKey(key: variant.RegistryKey) ||
            !TryResolveProtocolDefaultImpl(resolvedRoutine: recovered,
                receiverResolvedType: variant.OwnerType,
                protoRoutine: out RoutineInfo? pr,
                implementer: out TypeSymbol? impl) || pr == null || impl == null)
        {
            return false;
        }

        Dictionary<string, TypeSymbol> subs = BuildCallSubs(pr: pr,
            impl: impl,
            typeArguments: variant.TypeArguments,
            specialization: recovered.IsSpecialization || pr.IsSpecialization);
        Statement? cloned = CloneProtocolRoutineBody(protocolRoutine: pr,
            implementer: impl,
            synthesized: variant,
            protoSubs: subs);
        if (cloned == null)
        {
            return false;
        }

        // As the variants built from a definition are: a call written with type arguments is a plain call by now,
        // so the variant's rewrite sees the failable call it is. A routine that is not failable passes on only
        // what fails through the routine values it reaches.
        Statement source = new Builder.Desugaring.Passes.GenericCallLoweringPass(registry: ctx.Registry,
            variantBodies: new Dictionary<string, Statement>()).VisitStatement(stmt: cloned);
        Statement body = ErrorHandlingVariantPass.GenerateVariantBody(baseBody: source,
            kind: ErrorHandlingVariantPass.KindOf(variant: variant),
            registry: ctx.Registry,
            resolveFailurePoints: recovered.IsFailable || recovered.HasThrow);
        if (ctx.AnalyzeMaterializedDeriveBody is { } analyze)
        {
            body = analyze(arg1: variant, arg2: body);
        }

        ctx.LiveRoutineKeys.Add(item: variant.RegistryKey);
        TypeSymbol me = MeFor(implementer: impl);
        var bodySubs = new Dictionary<string, TypeSymbol>(dictionary: subs) { [key: "me"] = me, [key: "Me"] = me };
        bool check = variant.Recovery == RecoveryKind.Check;
        ctx.InstantiatedGenericBodies[key: variant.RegistryKey] = new MonomorphizedBody(
            Ast: WrapInShellDecl(name: variant.Name, body: body, info: variant),
            Info: variant,
            TypeSubs: bodySubs,
            VariantStatus: check
                ? FailableVariant.Check
                : variant.FailableVariant == FailableVariant.TryBool
                    ? FailableVariant.TryBool
                    : null,
            VariantInnerType: check && variant.ReturnType?.TypeArguments is [var inner] ? inner : null,
            IsSynthesized: false,
            OwesScopeTeardown: true);
        SeedConstructorCallees(body: body);
        return true;
    }

    /// <summary>
    /// The bindings a body made for <paramref name="impl"/> from the protocol routine <paramref name="pr"/> uses:
    /// the protocol's parameters from the conformance, then the call's type arguments, either the routine's own
    /// parameters (<c>select[U]</c>, aligned from the end, after the protocol's) or a specialization's bracket
    /// (<c>to[Array[T, N]]</c> called as <c>to[Array[S64, 3]]</c>), which binds the parameters it names.
    /// </summary>
    private static Dictionary<string, TypeSymbol> BuildCallSubs(RoutineInfo pr, TypeSymbol impl,
        List<TypeSymbol>? typeArguments, bool specialization)
    {
        Dictionary<string, TypeSymbol> subs = BuildProtocolGenericSubs(protocolRoutine: pr, implementer: impl);
        if (typeArguments is not { Count: > 0 })
        {
            return subs;
        }

        RoutineInfo written = pr.GenericDefinition ?? pr;
        if (specialization)
        {
            if (written is { TypeArguments: { } pattern, GenericParameters: { Count: > 0 } own } &&
                pattern.Count == typeArguments.Count)
            {
                for (int i = 0; i < pattern.Count; i++)
                {
                    Builder.Declaration.TypeRegistry.UnifyReceiverGenerics(pattern: pattern[index: i],
                        concrete: typeArguments[index: i], genericParams: own, into: subs);
                }
            }

            return subs;
        }

        if (pr.GenericParameters is { Count: > 0 } allParams && typeArguments.Count <= allParams.Count)
        {
            int offset = allParams.Count - typeArguments.Count;
            for (int i = 0; i < typeArguments.Count; i++)
            {
                subs[key: allParams[index: offset + i]] = typeArguments[index: i];
            }
        }

        return subs;
    }

    /// <summary>
    /// Synthesizes the per-implementer body for a memberRoutine-generic resolution, keyed by the
    /// resolution's own RegistryKey. Returns true if a new body was added.
    /// </summary>
    private bool SynthesizeMemberGenericBody(RoutineInfo pr, RoutineInfo rr, TypeSymbol impl,
        List<TypeSymbol> memberRoutineArgs, List<string> allParams)
    {
        Dictionary<string, TypeSymbol> fullSubs = BuildProtocolGenericSubs(
            protocolRoutine: pr,
            implementer: impl);
        int offset = allParams.Count - memberRoutineArgs.Count;
        for (int i = 0; i < memberRoutineArgs.Count; i++)
        {
            fullSubs[key: allParams[index: offset + i]] = memberRoutineArgs[index: i];
        }

        // Build the per-implementer routine with Me + the memberRoutine generics substituted in
        // its signature (rr's own ReturnType still carries ProtocolSelf `Me`, which would
        // trip codegen's ContainsGenericParameter guard and silently skip emission). Carry
        // rr.TypeArguments so it mangles to the same symbol the call site emits.
        RoutineInfo mgInfo = SynthesizePerImplementer(protocolRoutine: pr,
            implementer: impl,
            protoSubs: fullSubs,
            typeArguments: memberRoutineArgs);

        // Guard on the key we actually store under (mgInfo's), or the fixed-point loop
        // never converges (re-adding every iteration).
        if (ctx.InstantiatedGenericBodies.TryGetValue(key: mgInfo.RegistryKey, value: out MonomorphizedBody? made))
        {
            return _synthesizedForCall.TryAdd(key: (rr.RegistryKey, impl.FullName), value: made.Info);
        }

        _synthesizedForCall[key: (rr.RegistryKey, impl.FullName)] = mgInfo;

        Statement? mgBody = CloneProtocolRoutineBody(protocolRoutine: pr,
            implementer: impl,
            synthesized: mgInfo,
            protoSubs: fullSubs);
        if (mgBody == null)
        {
            return false;
        }

        ctx.LiveRoutineKeys.Add(item: mgInfo.RegistryKey);
        TypeSymbol mgMe = MeFor(implementer: impl);
        var mgSubs = new Dictionary<string, TypeSymbol>(dictionary: fullSubs)
        {
            [key: "me"] = mgMe, [key: "Me"] = mgMe
        };
        ctx.InstantiatedGenericBodies[key: mgInfo.RegistryKey] = new MonomorphizedBody(
            Ast: WrapInShellDecl(name: mgInfo.Name, body: mgBody, info: mgInfo),
            Info: mgInfo,
            TypeSubs: mgSubs,
            VariantStatus: null,
            VariantInnerType: null,
            IsSynthesized: false,
            OwesScopeTeardown: true);
        SeedConstructorCallees(body: mgBody);
        return true;
    }

    /// <summary>
    /// Synthesizes the per-(protocolRoutine, implementer) body for the standard (non-member-generic)
    /// case. Returns true if a new body was added.
    /// </summary>
    private bool SynthesizeStandardBody(RoutineInfo pr, TypeSymbol impl, List<TypeSymbol>? typeArguments = null)
    {
        (string ProtoKey, string ImplKey) key = SynthesisKey(pr: pr, impl: impl, typeArguments: typeArguments);
        if (_synthesized.ContainsKey(key: key))
        {
            return false;
        }

        // Bind the protocol's own generic params (e.g. Iterable[T].enumerate's `T`) from the
        // implementer's conformance (`List[Text] obeys Iterable[Text]` ⇒ T=Text), so the
        // synthesized body and signature don't leak the protocol element param.
        Dictionary<string, TypeSymbol> protoSubs =
            BuildProtocolGenericSubs(protocolRoutine: pr, implementer: impl);

        // A specialization whose bracket has parameters of its own (`to[Array[T, N]]` called as
        // `to[Array[S64, 3]]`) binds them from the call's bracket.
        RoutineInfo written = pr.GenericDefinition ?? pr;
        if (typeArguments != null && written is { TypeArguments: { } pattern, GenericParameters: { Count: > 0 } own } &&
            pattern.Count == typeArguments.Count)
        {
            for (int i = 0; i < pattern.Count; i++)
            {
                Builder.Declaration.TypeRegistry.UnifyReceiverGenerics(pattern: pattern[index: i],
                    concrete: typeArguments[index: i], genericParams: own, into: protoSubs);
            }
        }

        RoutineInfo synthesized = SynthesizePerImplementer(protocolRoutine: pr,
            implementer: impl,
            protoSubs: protoSubs,
            typeArguments: typeArguments);

        Statement? clonedBody = CloneProtocolRoutineBody(protocolRoutine: pr,
            implementer: impl,
            synthesized: synthesized,
            protoSubs: protoSubs);
        if (clonedBody == null)
        {
            return false;
        }

        _synthesized[key: key] = synthesized;
        ctx.Registry.RegisterRoutine(routine: synthesized);
        ctx.LiveRoutineKeys.Add(item: synthesized.RegistryKey);

        // Stash the cloned AST as a monomorphized body so codegen's normal
        // InstantiatedGenericBodies sweep picks it up under the implementer-owned key.
        // IsSynthesized=false: this body has a real AST cloned from the stdlib
        // protocol-default-impl body and must flow through every lowering pass
        // (ControlFlow, FString, Pattern, Expression, Operator, ...). Several
        // RunOnInstantiatedGenericBodies memberRoutines skip IsSynthesized=true entries
        // assuming there is no AST to walk, which is not the case here.
        // "me" is the receiver value binding; "Me" maps ProtocolSelf (Name "Me") to the
        // implementer so codegen's type substitution resolves `Me`-typed constructions
        // (e.g. `EnumerateIterator[T, Me]`) instead of leaking ProtocolSelf.
        TypeSymbol me = MeFor(implementer: impl);
        var bodySubs = new Dictionary<string, TypeSymbol>(dictionary: protoSubs)
        {
            [key: "me"] = me, [key: "Me"] = me
        };
        ctx.InstantiatedGenericBodies[key: synthesized.RegistryKey] = new MonomorphizedBody(
            Ast: WrapInShellDecl(name: synthesized.Name, body: clonedBody, info: synthesized),
            Info: synthesized,
            TypeSubs: bodySubs,
            VariantStatus: null,
            VariantInnerType: null,
            IsSynthesized: false,
            OwesScopeTeardown: true);
        SeedConstructorCallees(body: clonedBody);
        return true;
    }

    /// <summary>
    /// Seeds the no-arg constructor symbols a freshly-synthesized per-implementer body references so
    /// codegen emits their definitions. A collector body like <c>Iterable[T].Set</c> cloned onto
    /// <c>List[S64]</c> contains <c>var result = Set[S64]()</c>; reachability (which seeds
    /// <c>&lt;Type&gt;.create</c> for no-arg constructions) ran BEFORE this synthesis, so without this
    /// the call links to an undefined <c>Set[S64].create</c>. The subsequent incremental GMP pass
    /// monomorphizes the constructor body; this just keeps it past the liveness gate.
    /// </summary>
    private void SeedConstructorCallees(Statement body)
    {
        AstWalker.WalkExpressions(root: body,
            visit: expr =>
            {
                // A no-arg construction in a cloned collector body — e.g. `Set[T]()` / `List[T]()` —
                // reaches here as a GenericMemberRoutineCallExpression (or CreatorExpression) carrying a concrete
                // ConstructedType after the T→implementer substitution. Codegen emits a `<Type>.create`
                // call by mangled name; mark it live so the body GMP monomorphizes survives the gate.
                TypeSymbol? ct = expr switch
                {
                    CreatorExpression cre => cre.ConstructedType,
                    GenericMemberRoutineCallExpression gmc => gmc.ConstructedType,
                    CallExpression { Arguments.Count: 0 } ce => ce.ConstructedType,
                    _ => null
                };
                if (ct is null || !IsConcreteType(t: ct))
                {
                    return;
                }

                ctx.LiveRoutineKeys.Add(item: $"{ct.FullName}.create");
            });
    }

    /// <summary>True when <paramref name="t"/> carries no unresolved generic parameter (so its
    /// <c>create</c> mangles to a real symbol, not e.g. <c>ExcludeIterator[T, Me, SO].create</c>).</summary>
    private static bool IsConcreteType(TypeSymbol t)
    {
        if (t is GenericParameterTypeSymbol or ProtocolTypeSymbol)
        {
            return false;
        }

        if (t is { IsGenericDefinition: true, GenericParameters.Count: > 0 })
        {
            return false;
        }

        return t.TypeArguments is not { Count: > 0 } args || args.All(predicate: IsConcreteType);
    }

    /// <summary>
    /// Second-pass walk over the supplied bodies: rebinds every <c>ResolvedRoutine</c> that still
    /// points at a protocol-default-impl routine over to the corresponding per-implementer routine.
    /// Only the bodies scanned this round need rebinding — a call site to a routine synthesized this
    /// round lives in one of those bodies (it is what triggered the synthesis).
    /// </summary>
    private void RewriteCallSites(IReadOnlyList<Statement> bodies)
    {
        foreach (Statement body in bodies)
        {
            AstWalker.WalkExpressions(root: body,
                visit: expr =>
                {
                    if (!TryGetProtocolDefaultCallParts(expr: expr,
                            resolvedRoutine: out RoutineInfo? rr,
                            receiverType: out TypeSymbol? recvType,
                            rebind: out Action<RoutineInfo> rebind))
                    {
                        return;
                    }

                    if (!TryResolveProtocolDefaultImpl(resolvedRoutine: rr,
                            receiverResolvedType: recvType,
                            protoRoutine: out RoutineInfo? pr,
                            implementer: out TypeSymbol? impl) || pr == null || impl == null)
                    {
                        return;
                    }

                    if (_synthesizedForCall.TryGetValue(key: (rr!.RegistryKey, impl.FullName),
                            value: out RoutineInfo? madeForCall))
                    {
                        rebind(obj: madeForCall);
                        return;
                    }

                    bool specialization = (rr.RecoveryOf ?? rr).IsSpecialization || pr.IsSpecialization;
                    if (_synthesized.TryGetValue(key: SynthesisKey(pr: pr, impl: impl,
                                typeArguments: specialization ? rr.TypeArguments : null),
                            value: out RoutineInfo? newRoutine))
                    {
                        rebind(obj: newRoutine);
                    }
                });
        }
    }

    // -------- Helpers --------

    /// <summary>The key a body synthesized for <paramref name="pr"/> on <paramref name="impl"/> is kept under: a
    /// specialization's bracket is part of it, since one written with parameters of its own (<c>to[Array[T, N]]</c>)
    /// gives a body for each bracket it is called with.</summary>
    private static (string ProtoKey, string ImplKey) SynthesisKey(RoutineInfo pr, TypeSymbol impl,
        List<TypeSymbol>? typeArguments)
    {
        return typeArguments is { Count: > 0 }
            ? (pr.RegistryKey, $"{impl.FullName}[{string.Join(separator: ", ", values: typeArguments.Select(selector: t => t.FullName))}]")
            : (pr.RegistryKey, impl.FullName);
    }

    /// <summary>
    /// Extracts the resolved routine, receiver type, and a rebind callback from a call-shaped
    /// expression — either a lowered <see cref="CallExpression"/> (callee is a MemberExpression) or a
    /// still-generic <see cref="GenericMemberRoutineCallExpression"/> (explicit memberRoutine type args, not yet
    /// lowered by GenericCallLoweringPass, which runs after this pass). Returns false for anything else.
    /// </summary>
    /// <param name="expr">The expression to inspect.</param>
    /// <param name="resolvedRoutine">On success, the resolved routine from the call site.</param>
    /// <param name="receiverType">On success, the concrete receiver type the call dispatches on.</param>
    /// <param name="rebind">On success, a callback to replace the call's resolved routine with the synthesized one.</param>
    private static bool TryGetProtocolDefaultCallParts(Expression expr,
        out RoutineInfo? resolvedRoutine, out TypeSymbol? receiverType,
        out Action<RoutineInfo> rebind)
    {
        switch (expr)
        {
            case CallExpression { Callee: MemberExpression mem } ce:
                resolvedRoutine = ce.ResolvedRoutine;
                receiverType = mem.Object.ResolvedType;
                rebind = r => ce.ResolvedRoutine = r;
                return true;
            case GenericMemberRoutineCallExpression gmc:
                resolvedRoutine = gmc.ResolvedRoutine;
                receiverType = gmc.Object.ResolvedType;
                rebind = r => gmc.ResolvedRoutine = r;
                return true;
            default:
                resolvedRoutine = null;
                receiverType = null;
                rebind = static _ => { };
                return false;
        }
    }

    private bool TryResolveProtocolDefaultImpl(RoutineInfo? resolvedRoutine,
        TypeSymbol? receiverResolvedType, out RoutineInfo? protoRoutine, out TypeSymbol? implementer)
    {
        protoRoutine = null;
        implementer = null;
        if (resolvedRoutine is not { } rr)
        {
            return false;
        }

        // Walk the GenericDefinition chain to find a protocol-owned default-impl body.
        // One level covers the re-homed owner-resolved form; a member-routine-generic
        // resolution may require two levels to reach the protocol owner.
        RoutineInfo? proto = null;
        for (RoutineInfo? cur = rr; cur != null; cur = cur.GenericDefinition)
        {
            if (cur.OwnerType is ProtocolTypeSymbol && RoutineHasDefaultImplBody(routine: cur))
            {
                proto = cur;
                break;
            }
        }

        if (proto == null)
        {
            return false;
        }

        // Pick the implementer. When the resolved routine is ALREADY re-homed onto a concrete owner
        // (e.g. `me.transform(x).List()` monomorphized to `List[S64].List`), `rr.OwnerType` IS the
        // implementer — and is authoritative. The receiver expression's ResolvedType can be a stale
        // generic param here (GenericAstRewriter re-resolves the call's routine but leaves a dynamic
        // closure-call's receiver type unsubstituted), so trusting it would synthesize `Sub.List`
        // garbage instead of `List[S64].List`. Fall back to the receiver type only when `rr` is still
        // protocol-owned (not yet re-homed, e.g. an `Iterable[T].Set()` call).
        TypeSymbol? impl;
        if (rr.OwnerType is RecordTypeSymbol or EntityTypeSymbol)
        {
            impl = rr.OwnerType;
        }
        else if (receiverResolvedType is { } recvType)
        {
            impl = UnwrapWrappers(t: recvType);
        }
        else
        {
            impl = null;
        }

        if (impl is null or ProtocolTypeSymbol or GenericParameterTypeSymbol)
        {
            return false;
        }

        protoRoutine = proto;
        implementer = impl;
        return true;
    }

    /// <summary>
    /// Maps the protocol's own generic parameters to the implementer's conformance type arguments.
    /// e.g. for <c>Iterable[T].enumerate</c> synthesized onto <c>List[Text]</c> (which obeys
    /// <c>Iterable[Text]</c>), returns <c>{ T → Text }</c>. Empty when the protocol is non-generic
    /// or no matching conformance is found.
    /// </summary>
    private static Dictionary<string, TypeSymbol> BuildProtocolGenericSubs(
        RoutineInfo protocolRoutine, TypeSymbol implementer)
    {
        var subs = new Dictionary<string, TypeSymbol>();
        if (protocolRoutine.OwnerType is not ProtocolTypeSymbol protoOwner)
        {
            return subs;
        }

        ProtocolTypeSymbol protoDef = protoOwner.GenericDefinition ?? protoOwner;
        if (protoDef.GenericParameters is not { Count: > 0 } pParams)
        {
            return subs;
        }

        // A protocol the implementer obeys through another (`Emittable[S64]` of a `ListEmitter[S64]`, which obeys
        // `Emitter[S64]`) binds the same way.
        List<TypeSymbol> protocols = Builder.Declaration.TypeRegistry.ObeyedProtocols(type: implementer);
        foreach (TypeSymbol p in protocols)
        {
            // Check the conformance to the same protocol by generic-definition identity (no name
            // string-munging): `Iterable[Text]`'s def is the same `Iterable` def the routine owns.
            TypeSymbol pDef = GenericDefOf(t: p) ?? p;
            if (!ReferenceEquals(objA: pDef, objB: protoDef) && pDef.Name != protoDef.Name)
            {
                continue;
            }

            if (p.TypeArguments is { Count: > 0 } args)
            {
                for (int i = 0; i < pParams.Count && i < args.Count; i++)
                {
                    subs[key: pParams[index: i]] = args[index: i];
                }
            }

            // A member written on a pattern of the protocol (`routine Emittable[Tuple[TKey, TValue]].to[...]`) binds
            // its own parameters from the conformance too (TKey, TValue from `Emittable[Tuple[S64, Text]]`).
            RoutineInfo written = protocolRoutine.GenericDefinition ?? protocolRoutine;
            if (written.MeType is { } pattern)
            {
                Builder.Declaration.TypeRegistry.UnifyReceiverGenerics(pattern: pattern, concrete: p,
                    genericParams: written.GenericParameters, into: subs);
            }

            break;
        }

        return subs;
    }

    /// <summary>
    /// Yields every routine body to be scanned: user program routines (incl. members of
    /// entity/record declarations), stdlib routine bodies indexed by registry key, and
    /// monomorphized bodies already produced (so cascade calls are also rewritten).
    /// </summary>
    private IEnumerable<Statement> EnumerateLiveRoutineBodies()
    {
        foreach ((Program prog, _, _) in ctx.UserPrograms)
        {
            foreach (Statement s in WalkDeclarationsForBodies(prog: prog))
            {
                yield return s;
            }
        }

        foreach (Statement s in ctx.RoutineBodies.Values)
        {
            yield return s;
        }

        // Snapshot: DiscoverAndSynthesize adds to ctx.InstantiatedGenericBodies as it walks, which
        // would invalidate a live enumerator (this matters on the post-GMP PDIL re-run, when the map
        // is already populated). New bodies from this pass are picked up by the outer fixed-point loop.
        foreach (MonomorphizedBody mb in ctx.InstantiatedGenericBodies.Values.ToList())
        {
            yield return mb.Ast.Body;
        }
    }

    private static IEnumerable<Statement> WalkDeclarationsForBodies(Program prog)
    {
        // Program.Declarations is List<ISyntaxTreeNode>: top-level statements are legal
        // alongside declarations, so pattern-match instead of casting each element.
        foreach (ISyntaxTreeNode d in prog.Declarations)
        {
            switch (d)
            {
                case RoutineDeclaration r:
                    yield return r.Body;
                    break;
                case EntityDeclaration ed:
                    foreach (Statement body in MemberRoutineBodies(members: ed.Members))
                    {
                        yield return body;
                    }

                    break;
                case RecordDeclaration rd:
                    foreach (Statement body in MemberRoutineBodies(members: rd.Members))
                    {
                        yield return body;
                    }

                    break;
                case Statement topLevel:
                    yield return topLevel;
                    break;
            }
        }
    }

    /// <summary>Yields the bodies of all routine members from a type member list.</summary>
    private static IEnumerable<Statement> MemberRoutineBodies(
        IEnumerable<SyntaxTree.Declaration> members)
    {
        foreach (SyntaxTree.Declaration m in members)
        {
            if (m is RoutineDeclaration mr)
            {
                yield return mr.Body;
            }
        }
    }

    /// <summary>
    /// True when this protocol routine is a default-impl (real body) rather than an abstract protocol
    /// stub. Checks <see cref="InstantiationContext.RoutineBodies"/> then the warm-only
    /// <see cref="InstantiationContext.StdlibTemplateBodies"/> (on a warm compile the stdlib extension
    /// templates live only in the latter — see its doc).
    /// </summary>
    private bool RoutineHasDefaultImplBody(RoutineInfo routine)
    {
        return HasTemplateBody(key: routine.RegistryKey) || routine.GenericDefinition != null &&
            HasTemplateBody(key: routine.GenericDefinition.RegistryKey);
    }

    private bool HasTemplateBody(string key)
    {
        return ctx.RoutineBodies.ContainsKey(key: key) ||
               ctx.StdlibTemplateBodies.ContainsKey(key: key);
    }

    private Statement? GetDefaultImplBody(RoutineInfo routine)
    {
        return GetTemplateBody(key: routine.RegistryKey) ?? (routine.GenericDefinition != null
            ? GetTemplateBody(key: routine.GenericDefinition.RegistryKey)
            : null);
    }

    private Statement? GetTemplateBody(string key)
    {
        if (ctx.RoutineBodies.TryGetValue(key: key, value: out Statement? b))
        {
            return b;
        }

        if (ctx.StdlibTemplateBodies.TryGetValue(key: key, value: out b))
        {
            return b;
        }

        return null;
    }

    private static TypeSymbol UnwrapWrappers(TypeSymbol t)
    {
        // Strip Retained/Tracked/Viewing/Modifying/Hijacked/Accessing/Controlling layers
        // to get at the implementer record/entity.
        while (WrapperShape.InnerOf(type: t) is { } wrapped)
        {
            t = wrapped;
        }

        return t;
    }

    private RoutineInfo SynthesizePerImplementer(RoutineInfo protocolRoutine, TypeSymbol implementer,
        Dictionary<string, TypeSymbol> protoSubs, List<TypeSymbol>? typeArguments = null)
    {
        // Clone parameters/return with `Me` substituted to the implementer AND the protocol's own
        // generic params bound from the implementer's conformance (e.g. T→Text). The latter matters
        // for signatures like Iterable[T].enumerate() -> ?EnumerateIterator[T], whose return would
        // otherwise leak the protocol element param. For a memberRoutine-generic instantiation (zip[U,S2]),
        // protoSubs also carries the bound memberRoutine generics and <paramref name="typeArguments"/> the
        // concrete memberRoutine args, so the synthesized routine mangles identically to the call site's
        // resolution symbol (e.g. `List[Text].zip[S64,List[S64]](List[S64])`).
        TypeSymbol me = MeFor(implementer: implementer);
        var subs =
            new Dictionary<string, TypeSymbol>(dictionary: protoSubs) { [key: "Me"] = me };
        var newParams = protocolRoutine.Parameters
                                       .Select(selector: p =>
                                            p.WithSubstitutedType(
                                                newType: SubstituteMe(t: p.Type, subs: subs)))
                                       .ToList();

        TypeSymbol? newRet = protocolRoutine.ReturnType != null
            ? SubstituteMe(t: protocolRoutine.ReturnType, subs: subs)
            : null;

        return new RoutineInfo(name: protocolRoutine.Name)
        {
            Kind = protocolRoutine.Kind,
            OwnerType = implementer,
            // An entity a program writes is taken as its handle, as its own routines take it.
            MeType = ReferenceEquals(objA: me, objB: implementer) ? null : me,
            // A realm's surface routine stays one on each implementer, keyed and named apart from the shared one.
            SurfaceRealm = protocolRoutine.SurfaceRealm,
            Parameters = newParams,
            ReturnType = newRet,
            IsFailable = protocolRoutine.IsFailable,
            IsSynthesized = true,
            TypeArguments = typeArguments,
            Location = protocolRoutine.Location
            // GenericDefinition is left null intentionally — this is not a generic instantiation
            // in the usual sense; the body is keyed in InstantiatedGenericBodies by RegistryKey.
        };
    }

    private Statement? CloneProtocolRoutineBody(RoutineInfo protocolRoutine, TypeSymbol implementer,
        RoutineInfo synthesized, Dictionary<string, TypeSymbol> protoSubs)
    {
        // The default's file may not be analyzed yet when the call that reaches it sits in a library body made
        // concrete before anything else reached that file (Suflae's `skip(count: Int)` calls RazorForge's
        // `skip`): analyze it first, or the clone carries no types.
        ctx.AnalyzeRoutineOnDemand?.Invoke(arg: protocolRoutine.RegistryKey);
        if (protocolRoutine.GenericDefinition is { } protocolDefinition)
        {
            ctx.AnalyzeRoutineOnDemand?.Invoke(arg: protocolDefinition.RegistryKey);
        }

        Statement? originalBody = GetDefaultImplBody(routine: protocolRoutine);
        if (originalBody == null)
        {
            return null;
        }

        // GenericAstRewriter sets ctx.ParamTypes["me"] from enclosingRoutine.OwnerType, so passing
        // the synthesized routine (OwnerType = implementer) automatically rebinds `me` (the receiver)
        // to the implementer. `Me` (the typename) is substituted via typeSubs below; nested member
        // calls in the body then re-resolve against the implementer's memberRoutines. The protocol's own
        // generic params (e.g. T→Text) are folded in so body types like EnumerateIterator[T] become
        // concrete (EnumerateIterator[Text]) and don't leak the element param into codegen.
        TypeSymbol me = MeFor(implementer: implementer);
        var typeSubs =
            new Dictionary<string, TypeSymbol>(dictionary: protoSubs) { [key: "Me"] = me };
        var stringSubs = typeSubs.ToDictionary(keySelector: kv => kv.Key,
            elementSelector: kv => kv.Value.InstanceArgumentName);
        Statement cloned = GenericAstRewriter.RewriteStatement(stmt: originalBody,
            subs: stringSubs,
            typeSubs: typeSubs,
            registry: ctx.Registry,
            enclosingRoutine: synthesized);

        // Stdlib bodies are stored raw (no SA annotation). `me` identifiers therefore have
        // ResolvedType=null after cloning, which blocks downstream lowering: ControlFlowLowering
        // can only set the synthesized `emit`'s try variant call's ResolvedType when `forStmt.Iterable`
        // (= `me`) carries one — without that, PatternLoweringPass sees subjectType=null and
        // refuses to fold the `is None / else var v` when-chain, leaking a NonePattern into codegen.
        AnnotateMeReferences(node: cloned, implementer: me);
        return cloned;
    }

    /// <summary>
    /// What a body made for <paramref name="implementer"/> holds as <c>me</c> and means by <c>Me</c>: the handle of an
    /// entity a program writes (its own routines take the handle, and the body calls them), else the implementer
    /// itself. An adapter the body builds over <c>me</c> then holds the handle as its source.
    /// </summary>
    private TypeSymbol MeFor(TypeSymbol implementer)
    {
        return ctx.Registry.HandleTakenAsMe(type: implementer) ?? implementer;
    }

    private static void AnnotateMeReferences(object? node, TypeSymbol implementer)
    {
        AstWalker.WalkExpressions(root: node,
            visit: expr =>
            {
                if (expr is IdentifierExpression { Name: "me" } id)
                {
                    id.ResolvedType = implementer;
                }
            });
    }

    private static RoutineDeclaration WrapInShellDecl(string name, Statement body,
        RoutineInfo info)
    {
        return new RoutineDeclaration(Name: name,
            Parameters: [],
            ReturnType: null,
            Body: body,
            Visibility: VisibilityModifier.Open,
            Annotations: [],
            Location: info.Location ?? new SourceLocation(FileName: "",
                Line: 0,
                Column: 0,
                Position: 0));
    }

    private TypeSymbol SubstituteMe(TypeSymbol t, Dictionary<string, TypeSymbol> subs)
    {
        if (t is GenericParameterTypeSymbol gp &&
            subs.TryGetValue(key: gp.Name, value: out TypeSymbol? sub))
        {
            return sub;
        }

        // `Me` in a protocol-default-impl signature/body resolves to ProtocolSelf; bind it to the
        // implementer (subs["Me"]). Without this, a return type like `?EnumerateIterator[T, Me]`
        // keeps ProtocolSelf, which ContainsGenericParameter flags, making codegen skip the body.
        if (t is ProtocolSelfTypeSymbol && subs.TryGetValue(key: "Me", value: out TypeSymbol? meSub))
        {
            return meSub;
        }

        // RoutineTypeSymbol keeps its parameter/return types in dedicated properties, NOT in
        // TypeArguments, so the generic recursion below misses them. Substitute each explicitly so
        // a lambda parameter like `transform: Callable[(T,), U]` becomes `Callable[(S64,), S64]`.
        // Without this the synthesized routine mangles to `...select(Callable[(T,), U])` and never
        // matches the call site's `...select(Callable[(S64,), S64])` — "undefined symbol" at codegen.
        if (t is RoutineTypeSymbol rt)
        {
            return SubstituteRoutineType(rt: rt, subs: subs);
        }

        // Recurse into composite types (e.g. EnumerateIterator[T] → EnumerateIterator[Text],
        // List[Me] → List[List[Text]]) so the substituted param doesn't survive in a type argument.
        return SubstituteTypeArguments(t: t, subs: subs);
    }

    /// <summary>Substitutes generic parameters within a <see cref="RoutineTypeSymbol"/>'s parameter and return types.</summary>
    private RoutineTypeSymbol SubstituteRoutineType(RoutineTypeSymbol rt,
        Dictionary<string, TypeSymbol> subs)
    {
        var newParamTypes = rt.ParameterTypes
                              .Select(selector: p => SubstituteMe(t: p, subs: subs))
                              .ToList();
        TypeSymbol? newReturn = rt.ReturnType != null
            ? SubstituteMe(t: rt.ReturnType, subs: subs)
            : null;
        return new RoutineTypeSymbol(parameterTypes: newParamTypes, returnType: newReturn)
        {
            IsFailable = rt.IsFailable
        };
    }

    /// <summary>Recurses into composite type arguments and rebuilds the resolution if any argument changed.</summary>
    private TypeSymbol SubstituteTypeArguments(TypeSymbol t, Dictionary<string, TypeSymbol> subs)
    {
        if (t.TypeArguments is not { Count: > 0 } args)
        {
            return t;
        }

        bool changed = false;
        var newArgs = new List<TypeSymbol>(capacity: args.Count);
        foreach (TypeSymbol a in args)
        {
            TypeSymbol na = SubstituteMe(t: a, subs: subs);
            changed |= !ReferenceEquals(objA: na, objB: a);
            newArgs.Add(item: na);
        }

        if (changed)
        {
            TypeSymbol? def = GenericDefOf(t: t) ?? (t.IsGenericDefinition
                ? t
                : null);
            if (def != null)
            {
                return ctx.Registry.GetOrCreateResolution(genericDef: def, typeArguments: newArgs);
            }
        }

        return t;
    }

    /// <summary>Generic definition of a type, which lives on the concrete subtypes, not base TypeSymbol.</summary>
    private static TypeSymbol? GenericDefOf(TypeSymbol t)
    {
        return t switch
        {
            RecordTypeSymbol r => r.GenericDefinition,
            EntityTypeSymbol e => e.GenericDefinition,
            ProtocolTypeSymbol p => p.GenericDefinition,
            _ => null
        };
    }
}
