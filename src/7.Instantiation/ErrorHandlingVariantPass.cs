using Builder.Desugaring;
using Builder.Lowering.Passes;
using Builder.Declaration;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Instantiation;

/// <summary>
/// Generates the recovery variants (the routines `try`, `grab` and `lookup` bind to) of failable routines.
/// A variant has its failable routine's name and parameters and is reached through it
/// (<see cref="TypeRegistry.LookupRecoveryVariant"/>), never by a name.
/// Runs once globally after Phase 4 body analysis.
///
/// Generation rules (based on throw/absent found in body):
/// - Only absent:       try
/// - Only throw:        try + grab
/// - Both:              try + lookup
/// </summary>
internal sealed class ErrorHandlingVariantPass(DesugaringContext ctx)
{
    /// <summary>The recovery keyword a variant kind serves.</summary>
    private static RecoveryKind ToRecovery(ErrorHandlingVariantKind kind)
    {
        return kind switch
        {
            ErrorHandlingVariantKind.Check => RecoveryKind.Grab,
            ErrorHandlingVariantKind.Lookup => RecoveryKind.Lookup,
            _ => RecoveryKind.Try
        };
    }

    /// <summary>
    /// <paramref name="call"/> bound to <paramref name="variant"/>, the recovery variant of the failable
    /// routine it called. The variant has that routine's name and parameters, so the call keeps its shape;
    /// it takes the variant's carrier type and is marked analyzed, since a re-analysis by name would bind
    /// the failable routine again.
    /// </summary>
    private static CallExpression BindToVariant(CallExpression call, RoutineInfo variant)
    {
        CallExpression bound = call with
        {
            ResolvedRoutine = variant, ResolvedType = variant.ReturnType, IsFailable = false, IsPreAnalyzed = true
        };
        return bound.Callee is MemberExpression member
            ? bound with { Callee = member with { IsFailable = false } }
            : bound;
    }

    /// <summary>The call of a failable creator's recovery variant that a creator expression stands for.</summary>
    private static CallExpression BindCreatorToVariant(CreatorExpression creator, RoutineInfo variant)
    {
        var typeId = new IdentifierExpression(Name: creator.TypeName, Location: creator.Location);
        var member = new MemberExpression(Object: typeId,
            MemberName: variant.Name,
            Location: creator.Location);
        var args = creator.MemberVariables
                          .Select(selector: mv => (Expression)new NamedArgumentExpression(
                               Name: mv.Name,
                               Value: mv.Value,
                               Location: creator.Location) { ResolvedType = mv.Value.ResolvedType })
                          .ToList();
        return new CallExpression(Callee: member, Arguments: args, Location: creator.Location)
        {
            ResolvedRoutine = variant, ResolvedType = variant.ReturnType, IsPreAnalyzed = true
        };
    }

    /// <summary>
    /// Per-file stub: variant generation is global only (see <see cref="RunGlobal"/>).
    /// This overload intentionally does nothing.
    /// </summary>
    public static void Run(Program program)
    {
        // Variant generation is a single global pass (RunGlobal); there is no per-file work to do.
    }

    /// <summary>
    /// Runs variant generation globally.
    /// Must be called once after all routine bodies have been analyzed (Phase 4).
    /// </summary>
    public void RunGlobal()
    {
        var generator = new ErrorHandlingGenerator(registry: ctx.Registry);

        // Snapshot before iteration -> registering variants adds new routines to the registry
        var routines = ctx.Registry
                          .GetAllRoutines()
                          .ToList();

        PopulateDirectFailability(routines: routines);
        MarkPessimisticStdlibFailability(routines: routines);
        PropagateFailabilityFixpoint(routines: routines);

        List<(RoutineInfo routine, Statement body, List<GeneratedVariant> variants)> pending =
            RegisterVariants(routines: routines, generator: generator);
        TransformPendingBodies(pending: pending);
    }

    /// <summary>
    /// Phase A: populate per-routine HasThrow/HasAbsent/ThrowableTypes from direct body
    /// scan. (Verifier sets HasThrow/HasAbsent for direct cases; we also need ThrowableTypes
    /// populated before propagation can fan them out through the call graph.)
    /// </summary>
    private void PopulateDirectFailability(List<RoutineInfo> routines)
    {
        foreach (RoutineInfo routine in routines.Where(predicate: r =>
                     r.IsFailable && ctx.RoutineBodies.ContainsKey(key: r.RegistryKey)))
        {
            Statement body = ctx.RoutineBodies[key: routine.RegistryKey];
            ErrorHandlingAnalysis analysis = ErrorHandlingGenerator.AnalyzeBody(body: body);
            if (analysis.HasThrow)
            {
                routine.HasThrow = true;
            }

            if (analysis.HasAbsent)
            {
                routine.HasAbsent = true;
            }

            foreach (TypeSymbol t in analysis.ThrownTypes.Where(predicate: t =>
                         !routine.ThrowableTypes.Contains(item: t)))
            {
                routine.ThrowableTypes.Add(item: t);
            }
        }
    }

    /// <summary>
    /// Phase A2: stdlib bodies are stored by CollectStdlibBodiesForVariantGeneration
    /// without running SA, so propagated-failability routines (e.g. stdlib
    /// `common routine S64.from_digit_bytes!` returning `S64.from_digit_bytes_at!`) have
    /// empty FailableCallees and no direct throw/absent. Detect them and mark pessimistic
    /// so variant generation produces try + lookup — matching what the pre-register
    /// pass registered as stubs.
    /// </summary>
    private void MarkPessimisticStdlibFailability(List<RoutineInfo> routines)
    {
        foreach (RoutineInfo routine in routines.Where(predicate: r =>
                     r.IsFailable && !r.HasThrow && !r.HasAbsent && r.FailableCallees.Count == 0 &&
                     ctx.RoutineBodies.ContainsKey(key: r.RegistryKey)))
        {
            routine.HasThrow = true;
            routine.HasAbsent = true;
        }
    }

    /// <summary>
    /// Phase B: fixpoint propagation through FailableCallees. A routine whose failability
    /// is purely propagated (e.g. routine S64_from_text! returning S64.create!(from_text: t))
    /// has HasThrow=HasAbsent=false but FailableCallees containing S64.create!.
    /// We OR the callees' state into the caller until no further change.
    /// </summary>
    private static void PropagateFailabilityFixpoint(List<RoutineInfo> routines)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (RoutineInfo routine in routines.Where(predicate: r => r.IsFailable))
            {
                foreach (RoutineInfo callee in routine.FailableCallees)
                {
                    changed |= PropagateCalleeFailability(routine: routine, callee: callee);
                }
            }
        }
    }

    /// <summary>
    /// Merges one callee's HasThrow, HasAbsent, and ThrowableTypes flags into the caller routine.
    /// Returns true when any flag was newly set (signals that the fixpoint should continue).
    /// </summary>
    private static bool PropagateCalleeFailability(RoutineInfo routine, RoutineInfo callee)
    {
        bool changed = false;

        if (callee.HasThrow && !routine.HasThrow)
        {
            routine.HasThrow = true;
            changed = true;
        }

        if (callee.HasAbsent && !routine.HasAbsent)
        {
            routine.HasAbsent = true;
            changed = true;
        }

        var newTypes = callee.ThrowableTypes
                             .Where(predicate: t => !routine.ThrowableTypes.Contains(item: t))
                             .ToList();
        if (newTypes.Count > 0)
        {
            routine.ThrowableTypes.AddRange(collection: newTypes);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Phase C: register all variants first (no body transformation yet) — so the body
    /// rewriter in Phase D can find variants of callees regardless of iteration order.
    /// Returns the per-routine work items to transform in Phase D.
    /// </summary>
    private List<(RoutineInfo routine, Statement body, List<GeneratedVariant> variants)>
        RegisterVariants(List<RoutineInfo> routines, ErrorHandlingGenerator generator)
    {
        var pending =
            new List<(RoutineInfo routine, Statement body, List<GeneratedVariant> variants)>();

        // DEMAND-DRIVEN: only the iterator `emit` variants are generated eagerly here (their generic-def
        // bodies must exist before Phase-8 monomorphization of composed emitters). EVERY OTHER failable's
        // try/grab/lookup variant — body and registration — is produced ON DEMAND the first time a call
        // site reaches it (SemanticVerifier's TrySynthesizeVariantOnDemand → GenerateVariantBody, drained
        // before AnalyzeVariantBodies). This is what stops ~3600 stdlib variant bodies from being built +
        // analyzed every run when a program uses only a handful.
        foreach (RoutineInfo routine in routines.Where(predicate: r =>
                     r.IsFailable && r.Name == "emit"))
        {
            if (!ctx.RoutineBodies.TryGetValue(key: routine.RegistryKey,
                    value: out Statement? body))
            {
                continue;
            }

            RegisterVariantsForEmitRoutine(routine: routine,
                body: body,
                generator: generator,
                pending: pending);
        }

        return pending;
    }

    /// <summary>
    /// Registers error-handling variants for a single eagerly-processed <c>emit</c> routine.
    /// Handles the <c>@crash_only</c> annotation case (analyze but suppress safe variants) and
    /// the normal case (register all generated variants and enqueue for body transformation).
    /// </summary>
    private void RegisterVariantsForEmitRoutine(RoutineInfo routine, Statement body,
        ErrorHandlingGenerator generator,
        List<(RoutineInfo routine, Statement body, List<GeneratedVariant> variants)> pending)
    {
        // @crash_only: still analyze throw/absent but suppress safe variant generation
        if (routine.Annotations.Contains(item: "crash_only"))
        {
            ErrorHandlingResult crashOnlyResult =
                generator.GenerateVariants(routine: routine, body: body);
            routine.HasThrow = crashOnlyResult.HasThrow;
            routine.HasAbsent = crashOnlyResult.HasAbsent;
            return;
        }

        // The lookup variant is the step of an `each` loop beneath a recovery keyword (RoutineValueCalls).
        ErrorHandlingResult result =
            generator.GenerateVariants(routine: routine, body: body, pessimistic: false, withLookup: true);
        if (result.Error != null)
        {
            return;
        }

        routine.HasThrow = result.HasThrow;
        routine.HasAbsent = result.HasAbsent;
        routine.ThrowableTypes = result.ThrownTypes;

        foreach (RoutineInfo variantRoutine in result.Variants.Select(selector: v => v.Routine))
        {
            ctx.Registry.RegisterRoutine(routine: variantRoutine);
            variantRoutine.ThrowableTypes = result.ThrownTypes;
        }

        pending.Add(item: (routine, body, result.Variants));
    }

    /// <summary>
    /// Phase D: now that all variants are registered, transform each body — rewriter can
    /// find variants of inner failable calls and substitute them.
    /// </summary>
    private void TransformPendingBodies(
        List<(RoutineInfo routine, Statement body, List<GeneratedVariant> variants)> pending)
    {
        foreach ((RoutineInfo _, Statement body, List<GeneratedVariant> variants) in pending)
        {
            foreach (GeneratedVariant variant in variants)
            {
                ErrorHandlingVariantKind kind = DetermineVariantKind(variant: variant);
                Statement variantSourceBody = GenericAstRewriter.RewriteStatement(
                    stmt: body,
                    subs: new Dictionary<string, string>());
                // The try variant is every `each` loop's step: a lambda the iterator calls stays a plain call there.
                _valueCallsStayPlain = kind is ErrorHandlingVariantKind.Try or ErrorHandlingVariantKind.TryBool;
                Statement variantBody;
                try
                {
                    variantBody = TransformBody(body: variantSourceBody,
                        kind: kind,
                        rewriter: TryRewriteToVariantCall,
                        registry: ctx.Registry);
                }
                finally
                {
                    _valueCallsStayPlain = false;
                }
                // Memo content: a variant body RESTORED from the captured stdlib is already lowered +
                // analyzed — keep it instead of overwriting with a fresh un-analyzed regeneration (the
                // restored ones are what AnalyzeVariantBodies skips; overwriting would leave them
                // unanalyzed). Branch on memo CONTENT ("was this key restored?"), not on registry mode:
                // a cold compile has an empty RestoredVariantKeys so it always keeps the fresh body.
                if (ctx.RestoredVariantKeys.Contains(item: variant.Routine.RegistryKey) &&
                    ctx.VariantBodies.ContainsKey(key: variant.Routine.RegistryKey))
                {
                    continue;
                }

                ctx.VariantBodies[key: variant.Routine.RegistryKey] = variantBody;
            }
        }
    }

    /// <summary>
    /// Maps a <see cref="GeneratedVariant"/> to its <see cref="ErrorHandlingVariantKind"/>,
    /// including distinguishing the TryBool case (None-returning try variant).
    /// </summary>
    internal static ErrorHandlingVariantKind DetermineVariantKind(GeneratedVariant variant)
    {
        return variant.Kind switch
        {
            ErrorHandlingVariantKind.Try when variant.Routine.FailableVariant ==
                                              FailableVariant.TryBool => ErrorHandlingVariantKind
               .TryBool,
            _ => variant.Kind
        };
    }

    /// <summary>
    /// Builds ONE variant's body on demand (the same transform Phase D applies eagerly), for the
    /// SemanticVerifier's on-demand synthesizer. Uses the broad (path-1, <c>nextOnly:false</c>) propagation
    /// so inner failable calls are rewritten to THEIR try/grab/lookup variants — those inner lookups go
    /// through <see cref="TypeRegistry.LookupMemberRoutine"/>, whose on-demand hook synthesizes the inner
    /// variant transitively.
    /// </summary>
    public static Statement GenerateVariantBody(Statement baseBody, GeneratedVariant variant,
        TypeRegistry registry, bool resolveFailurePoints = true)
    {
        ErrorHandlingVariantKind kind = DetermineVariantKind(variant: variant);
        // Checked operators and subscripts in the body are failable calls too: spell them out before the
        // propagation below, so their failures become this variant's carrier instead of a crash. A library
        // routine recovered only for the routine values it reaches keeps its own operators as they are.
        Statement variantSourceBody =
            GenericAstRewriter.RewriteStatement(stmt: baseBody, subs: new Dictionary<string, string>());
        if (resolveFailurePoints)
        {
            variantSourceBody = FailurePointCalls.Resolve(body: variantSourceBody, registry: registry);
        }
        // A grab carrier has no absent state: an `absent` in the body is caught as AbsentValueError, the same
        // error an absence beneath a grab becomes.
        if (variant.Routine.Recovery == RecoveryKind.Grab &&
            registry.LookupType(name: "AbsentValueError") is { } absentError)
        {
            variantSourceBody = new AbsentAsThrow(absentError: absentError).VisitStatement(stmt: variantSourceBody);
        }

        return TransformBody(body: variantSourceBody,
            kind: kind,
            rewriter: MakeOnDemandVariantRewriter(registry: registry),
            registry: registry,
            nextOnlyPropagation: false);
    }

    /// <summary>
    /// A tail-return rewriter for on-demand variant-body generation: identical to
    /// <see cref="MakeNextVariantRewriter"/> but NOT restricted to <c>emit</c> — it rewrites a tail call to
    /// ANY failable routine into its matching variant. The variant lookup goes through
    /// <see cref="TypeRegistry.LookupMemberRoutine"/>, so a not-yet-synthesized inner variant is created on
    /// the spot by the on-demand hook.
    /// </summary>
    public static VariantCallRewriter MakeOnDemandVariantRewriter(TypeRegistry registry)
    {
        return (Expression? value, ErrorHandlingVariantKind kind, out Expression? rewritten) =>
        {
            rewritten = null;
            if (kind == ErrorHandlingVariantKind.TryBool)
            {
                return false;
            }

            RecoveryKind recovery = ToRecovery(kind: kind);
            switch (value)
            {
                case CallExpression { ResolvedRoutine: { } callee } call
                    when registry.CanFailUnderRecovery(routine: callee) &&
                         registry.LookupRecoveryVariant(recovered: callee, kind: recovery) is { } variant:
                    rewritten = BindToVariant(call: call, variant: variant);
                    return true;
                case CreatorExpression { ResolvedCreatorRoutine: { IsFailable: true } creatorRoutine } creator
                    when registry.LookupRecoveryVariant(recovered: creatorRoutine, kind: recovery) is { } variant:
                    rewritten = BindCreatorToVariant(creator: creator, variant: variant);
                    return true;
                default:
                    return false;
            }
        };
    }

    /// <summary>
    /// Signature for an optional rewriter that may convert a tail-return value into a passthrough
    /// call against the corresponding try/grab/lookup variant of an inner failable callee.
    /// </summary>
    public delegate bool VariantCallRewriter(Expression? value, ErrorHandlingVariantKind kind,
        out Expression? rewritten);

    /// <summary>
    /// Recursively walks a routine body and replaces throw/absent/return statements with
    /// <see cref="VariantReturnStatement"/> nodes appropriate for the given variant kind.
    /// All other statements are passed through unchanged (structurally cloned via record-with).
    /// When <paramref name="rewriter"/> succeeds on a tail-position return value, the value is
    /// emitted as <see cref="VariantSiteKind.FromVariantPassthrough"/> so codegen returns the
    /// already-carrier-shaped expression directly.
    /// </summary>
    /// <param name="body">The routine body statement to transform.</param>
    /// <param name="kind">Which error-handling variant shape to produce (try/check/lookup/try-bool).</param>
    /// <param name="rewriter">Optional tail-return rewriter; when it succeeds the return is emitted as a passthrough.</param>
    /// <param name="registry">Optional type registry used for try-variant synthesis when <paramref name="kind"/> is <c>Try</c>.</param>
    /// <param name="nextOnlyPropagation">
    /// When true, non-tail failable-call propagation is restricted to inner <c>emit</c> calls
    /// (iterator chaining). Used by the MONOMORPHIZED (path-2) caller, which runs AFTER reachability:
    /// any other <c>try_X</c> it introduces wouldn't be marked live and would LINKERR (e.g. a guarded
    /// <c>getitem!</c>), whereas the <c>try</c> variant of <c>emit</c> is always emitted for live iterators. When false
    /// (the global path-1 caller, which runs BEFORE reachability), ALL non-tail failable calls are
    /// propagated — reachability then sees the introduced <c>try_X</c> calls and emits them. Path-1
    /// MUST propagate broadly so a try variant whose failability is purely propagated through a
    /// non-tail call (e.g. the try variant of <c>from_digit_bytes</c> → <c>from_digit_bytes_at!</c>) actually catches
    /// the inner throw/absent instead of letting it escape uncaught.
    /// </param>
    internal static Statement TransformBody(Statement body, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter = null, TypeRegistry? registry = null,
        bool nextOnlyPropagation = false)
    {
        // Every variant kind propagates its non-tail failable calls: Try (the flat Maybe {present,value}
        // unwrap), TryBool (the same unwrap, its failure branch returning false) and Check/Lookup (the
        // tag-based `when` over the inner's variant, TryBuildCarrierSafeCall/BuildCarrierPropagationWhen).
        // A kind left without the registry keeps its inner failable calls raw, and their failure then
        // crashes through the recovery the caller asked for.
        return TransformBodyCore(body: body,
            kind: kind,
            rewriter: rewriter,
            registry: registry,
            nextOnly: nextOnlyPropagation);
    }

    private static Statement TransformBodyCore(Statement body, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter, TypeRegistry? registry, bool nextOnly)
    {
        // A lone statement outside a block (e.g. an `if` branch that is a bare `return f().item0`) gets the
        // same nested-failable split as block members. The hoisted temps then need a block to live in.
        if (body is not BlockStatement && TryHoistNestedFailables(stmt: body,
                kind: kind,
                registry: registry,
                nextOnly: nextOnly,
                hoisted: out List<Statement>? hoisted,
                residual: out Statement? residual))
        {
            var stmts = new List<Statement>(collection: hoisted!) { residual! };
            return new BlockStatement(Statements: TransformBlockStatements(stmts: stmts,
                    start: 0,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly),
                Location: body.Location);
        }

        return body switch
        {
            // `pierce` stays a crash even inside a try/grab variant — it pierces through the
            // recovery surface, so it is NOT rewritten into a recoverable return.
            ThrowStatement { IsFatal: true } => body,

            ThrowStatement ts => new VariantReturnStatement(VariantKind: kind,
                SiteKind: VariantSiteKind.FromThrow,
                Value: ts.Error,
                Location: ts.Location),

            AbsentStatement abs => new VariantReturnStatement(VariantKind: kind,
                SiteKind: VariantSiteKind.FromAbsent,
                Value: null,
                Location: abs.Location),

            ReturnStatement ret when rewriter != null && rewriter(value: ret.Value,
                kind: kind,
                rewritten: out Expression? vcall) => new VariantReturnStatement(VariantKind: kind,
                SiteKind: VariantSiteKind.FromVariantPassthrough,
                Value: vcall,
                Location: ret.Location),

            // `return if c then a else b` with a failure in a branch: each branch returns on its own, so the
            // failable call is split out only on the path that evaluates it.
            ReturnStatement { Value: ConditionalExpression conditional } ret when registry != null && !nextOnly &&
                (HasPropagatableFailure(expr: conditional.TrueExpression, registry: registry) ||
                 HasPropagatableFailure(expr: conditional.FalseExpression, registry: registry)) =>
                TransformBodyCore(body: new IfStatement(Condition: conditional.Condition,
                        ThenStatement: new BlockStatement(
                            Statements: [ret with { Value = conditional.TrueExpression }],
                            Location: ret.Location),
                        ElseStatement: new BlockStatement(
                            Statements: [ret with { Value = conditional.FalseExpression }],
                            Location: ret.Location),
                        Location: ret.Location),
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly),

            // A loop condition re-runs every iteration, so a failure in it cannot be split out ahead of the loop:
            // `while c` becomes `loop` over `if c then <body> else break`, whose condition is split out like
            // any other `if` condition, each time it runs. A `while … else` gets the flag its body sets, and its
            // `else` runs after the loop when the flag is still clear (the body never ran).
            WhileStatement ws when registry != null && !nextOnly &&
                                   HasPropagatableFailure(expr: ws.Condition, registry: registry) =>
                TransformBodyCore(body: LoopForRecovery(ws: ws),
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly),

            // A tail call whose callee has no variant of this kind (`return f()` in a grab variant, f able to
            // go absent but not throw): bind it to a temp so the statement propagation below picks the
            // callee's best variant and maps its failure onto this carrier.
            ReturnStatement { Value: CallExpression tailCall } ret when registry != null && !nextOnly &&
                                                                    registry.CanFailUnderRecovery(
                                                                        routine: tailCall.ResolvedRoutine) =>
                TransformBodyCore(body: SplitTailCall(ret: ret, call: tailCall),
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly),

            // `return f(x)` through a routine value: split like a tail call, so the statement propagation below
            // calls the value's recovering entry and maps its failure onto this carrier.
            ReturnStatement { Value: CallExpression valueCall } ret when registry != null &&
                                                                     ValueCallsRecover(kind: kind, nextOnly: nextOnly) &&
                                                                     RoutineValueCalls.ValueType(call: valueCall) !=
                                                                     null =>
                TransformBodyCore(body: SplitTailCall(ret: ret, call: valueCall),
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly),

            // The step of an `each` loop over an iterator that may call a routine value (an adapter holding a
            // lambda): it takes the iterator's lookup variant, whose absent state still ends the loop and whose
            // error state, a failure inside the lambda, becomes this variant's failure instead of a crash.
            WhenStatement ws when registry != null && ValueCallsRecover(kind: kind, nextOnly: nextOnly) &&
                                  RoutineValueCalls.LookupIterationStep(subject: ws.Expression, registry: registry) is
                                      { } lookupStep =>
                TransformRecoverableIteration(ws: ws,
                    lookupStep: lookupStep,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry),

            ReturnStatement ret => new VariantReturnStatement(VariantKind: kind,
                SiteKind: VariantSiteKind.FromReturn,
                Value: ret.Value,
                Location: ret.Location),

            BlockStatement block => block with
            {
                Statements = TransformBlockStatements(stmts: block.Statements,
                    start: 0,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
            },

            IfStatement ifs => TransformIf(ifs: ifs,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            WhileStatement ws => TransformWhile(ws: ws,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            EachStatement fs => TransformEach(fs: fs,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),

            WhenStatement ws => ws with
            {
                Clauses = ws.Clauses
                            .Select(selector: c => c with
                             {
                                 Body = TransformBodyCore(body: c.Body,
                                     kind: kind,
                                     rewriter: rewriter,
                                     registry: registry,
                                     nextOnly: nextOnly)
                             })
                            .ToList()
            },

            UsingStatement us => TransformUsing(us: us,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),

            DangerStatement danger => danger with
            {
                Body = (BlockStatement)TransformBodyCore(body: danger.Body,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
            },

            LoopStatement loop => loop with
            {
                Body = TransformBodyCore(body: loop.Body,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
            },

            _ => body // All other statements pass through unchanged
        };
    }

    /// <summary>
    /// Set while the try variant of an iterator's <c>emit</c> is built: that variant is the step of every
    /// <c>each</c> loop, whose absent state ENDS the loop, so a call through a routine value inside it (an
    /// adapter's lambda) stays a plain call and its failure crashes instead of quietly ending the loop. A loop
    /// beneath a recovery keyword takes the iterator's lookup variant instead, which keeps the failure apart
    /// from the end (<see cref="TransformRecoverableIteration"/>).
    /// </summary>
    [ThreadStatic] private static bool _valueCallsStayPlain;

    /// <summary>
    /// Whether a variant of <paramref name="kind"/> calls the recovering entry of the routine values it calls.
    /// Not inside the try variant of an iterator's <c>emit</c> (<see cref="_valueCallsStayPlain"/>). A variant
    /// built from a monomorphized body (<paramref name="nextOnly"/>) does it only for grab and lookup, whose
    /// carrier keeps a failure beneath apart from the absent end of an iteration: such a body is an iterator's
    /// <c>emit</c>, whose try variant is the plain step of an <c>each</c> loop. The recovering call reaches no
    /// routine of its own (the entry travels in the value), so it needs nothing marked live.
    /// </summary>
    private static bool ValueCallsRecover(ErrorHandlingVariantKind kind, bool nextOnly)
    {
        return !_valueCallsStayPlain &&
               (!nextOnly || kind is ErrorHandlingVariantKind.Check or ErrorHandlingVariantKind.Lookup);
    }

    /// <summary>Whether <paramref name="call"/> can fail beneath a recovery keyword: a routine that can fail
    /// under recovery, or a call through a routine value (whose lambda may fail).</summary>
    private static bool CallCanFail(CallExpression call, TypeRegistry registry)
    {
        return registry.CanFailUnderRecovery(routine: call.ResolvedRoutine) ||
               !_valueCallsStayPlain && RoutineValueCalls.ValueType(call: call) != null;
    }

    /// <summary>
    /// The <c>when</c> of an <c>each</c> loop over an iterator that may call a routine value, stepping with the
    /// iterator's lookup variant (<paramref name="lookupStep"/>): its absent arm still ends the loop, and an arm
    /// for the error state, a failure beneath the step, returns it as this variant's failure.
    /// </summary>
    private static Statement TransformRecoverableIteration(WhenStatement ws, Expression lookupStep,
        ErrorHandlingVariantKind kind, VariantCallRewriter? rewriter, TypeRegistry registry)
    {
        var clauses = ws.Clauses
                        .Select(selector: c => c with
                         {
                             Body = TransformBodyCore(body: c.Body,
                                 kind: kind,
                                 rewriter: rewriter,
                                 registry: registry,
                                 nextOnly: false)
                         })
                        .ToList();
        SourceLocation loc = ws.Location;
        return ws with
        {
            Expression = lookupStep,
            Clauses = RoutineValueCalls.WithStepFailureClause(clauses: clauses,
                kind: kind,
                registry: registry,
                loc: loc)
        };
    }

    /// <summary>Whether an always-evaluated part of <paramref name="expr"/> calls something that can fail
    /// under recovery.</summary>
    private static bool HasPropagatableFailure(Expression expr, TypeRegistry registry)
    {
        var scan = new NestedFailableHoister(
            propagates: call => CallCanFail(call: call, registry: registry),
            hoistUpTo: -1,
            skipIndex: -1);
        scan.VisitExpression(expr: expr);
        return scan.CallIsFailable.Contains(item: true);
    }

    /// <summary>
    /// <c>while c</c> as <c>loop { if c { body } else { break } }</c>, so the condition is an <c>if</c> condition the
    /// statement propagation splits out each time it runs. With an <c>else</c>, the body first sets a flag and the
    /// <c>else</c> runs after the loop when the flag is still clear (the body ran zero times).
    /// </summary>
    private static Statement LoopForRecovery(WhileStatement ws)
    {
        SourceLocation loc = ws.Location;
        string? ranName = ws.ElseBranch != null
            ? $"__rf_ran_{Interlocked.Increment(location: ref _hoistTemp)}"
            : null;
        Statement body = ranName != null
            ? new BlockStatement(Statements:
                [
                    new AssignmentStatement(Target: new IdentifierExpression(Name: ranName, Location: loc),
                        Value: new LiteralExpression(Value: true,
                            LiteralType: Builder.Tokenizer.TokenType.True,
                            Location: loc),
                        Location: loc),
                    ws.Body
                ],
                Location: loc)
            : ws.Body;
        var loop = new LoopStatement(Body: new BlockStatement(Statements:
                [
                    new IfStatement(Condition: ws.Condition,
                        ThenStatement: body,
                        ElseStatement: new BlockStatement(Statements: [new BreakStatement(Location: loc)],
                            Location: loc),
                        Location: loc)
                ],
                Location: loc),
            Location: loc);
        if (ranName == null)
        {
            return loop;
        }

        return new BlockStatement(Statements:
            [
                new DeclarationStatement(Declaration: new VariableDeclaration(Name: ranName,
                        Type: new TypeExpression(Name: "Bool", GenericArguments: null, Location: loc),
                        Initializer: new LiteralExpression(Value: false,
                            LiteralType: Builder.Tokenizer.TokenType.False,
                            Location: loc),
                        Visibility: VisibilityModifier.Secret,
                        Location: loc),
                    Location: loc),
                loop,
                new IfStatement(Condition: new UnaryExpression(Operator: UnaryOperator.Not,
                        Operand: new IdentifierExpression(Name: ranName, Location: loc),
                        Location: loc),
                    ThenStatement: ws.ElseBranch!,
                    ElseStatement: null,
                    Location: loc)
            ],
            Location: loc);
    }

    /// <summary>Rewrites each <c>absent</c> into <c>throw AbsentValueError()</c>.</summary>
    private sealed class AbsentAsThrow(TypeSymbol absentError) : AstRewriter
    {
        public override Statement VisitStatement(Statement stmt)
        {
            return stmt is AbsentStatement absent
                ? new ThrowStatement(Error: new CreatorExpression(TypeName: absentError.Name,
                        TypeArguments: null,
                        MemberVariables: [],
                        Location: absent.Location) { ResolvedType = absentError },
                    Location: absent.Location)
                : base.VisitStatement(stmt: stmt);
        }
    }

    /// <summary><c>return f()</c> as <c>var __rf_tail_N = f()</c> then <c>return __rf_tail_N</c> (an entity
    /// result is moved out of the temp with <c>steal</c>).</summary>
    private static BlockStatement SplitTailCall(ReturnStatement ret, CallExpression call)
    {
        // A call that returns nothing has no value to keep: it runs as a statement, then the routine returns.
        if (call.ResolvedType is null or { IsNone: true })
        {
            return new BlockStatement(Statements:
                [
                    new ExpressionStatement(Expression: call, Location: ret.Location),
                    ret with { Value = null }
                ],
                Location: ret.Location);
        }

        string tempName = $"__rf_tail_{Interlocked.Increment(location: ref _hoistTemp)}";
        var decl = new DeclarationStatement(Declaration: new VariableDeclaration(Name: tempName,
                Type: null,
                Initializer: call,
                Visibility: VisibilityModifier.Secret,
                Location: ret.Location),
            Location: ret.Location);
        var reference = new IdentifierExpression(Name: tempName, Location: ret.Location)
        {
            ResolvedType = call.ResolvedType
        };
        Expression value = call.ResolvedType?.Category == TypeCategory.Entity
            ? new StealExpression(Operand: reference, Location: ret.Location) { ResolvedType = call.ResolvedType }
            : reference;
        return new BlockStatement(Statements: [decl, ret with { Value = value }], Location: ret.Location);
    }

    private static IfStatement TransformIf(IfStatement ifs, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter, TypeRegistry? registry, bool nextOnly)
    {
        return ifs with
        {
            ThenStatement =
            TransformBodyCore(body: ifs.ThenStatement,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            ElseStatement = ifs.ElseStatement != null
                ? TransformBodyCore(body: ifs.ElseStatement,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
                : null
        };
    }

    private static WhileStatement TransformWhile(WhileStatement ws, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter, TypeRegistry? registry, bool nextOnly)
    {
        return ws with
        {
            Body = TransformBodyCore(body: ws.Body,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            ElseBranch = ws.ElseBranch != null
                ? TransformBodyCore(body: ws.ElseBranch,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
                : null
        };
    }

    private static EachStatement TransformEach(EachStatement fs, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter, TypeRegistry? registry, bool nextOnly)
    {
        // A loop not lowered yet (a library body) over an iterator that may call a routine value is lowered with a
        // lookup-shaped step (ControlFlowLoweringPass), like the lowered loops TransformRecoverableIteration takes.
        bool recoverSteps = registry != null && ValueCallsRecover(kind: kind, nextOnly: nextOnly) &&
                            (fs.Iterable.ResolvedType is not { } iterableType ||
                             RoutineValueCalls.MayHoldRoutineValue(type: iterableType));
        return fs with
        {
            StepFailureKind = recoverSteps ? kind : fs.StepFailureKind,
            Body = TransformBodyCore(body: fs.Body,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            ElseBranch = fs.ElseBranch != null
                ? TransformBodyCore(body: fs.ElseBranch,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
                : null
        };
    }

    private static UsingStatement TransformUsing(UsingStatement us, ErrorHandlingVariantKind kind,
        VariantCallRewriter? rewriter, TypeRegistry? registry, bool nextOnly)
    {
        return us with
        {
            Body = TransformBodyCore(body: us.Body,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly),
            FallbackBody = us.FallbackBody != null
                ? TransformBodyCore(body: us.FallbackBody,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly)
                : null
        };
    }

    private static int _propTemp;

    /// <summary>
    /// Transforms a block's statements, propagating NON-tail failable calls through their safe
    /// variant. The tail-position <paramref name="rewriter"/> only handles <c>return F!(x)</c>; a
    /// failable call used in statement position — e.g. <c>var item = src.emit!()</c> — would
    /// otherwise be left calling the raw <c>!</c> routine, which HARD-CRASHES on absence (the raw
    /// form lowers <c>absent</c> to a <c>crash_report</c> call). Inside a <c>try</c> variant that inner
    /// absence must instead become this variant's own <c>None</c> return.
    ///
    /// For each such statement the remainder of the block is folded into the success branch of a
    /// plain <c>if</c> over the inner safe variant's Maybe carrier:
    /// <code>
    /// var __rf_prop_N = src.emit() (under try)      # Maybe[T]
    /// if __rf_prop_N.present
    ///   var item = __rf_prop_N.value
    ///   &lt;rest of block&gt;
    /// else
    ///   &lt;return None&gt;                       # VariantReturnStatement(Try, FromAbsent)
    /// </code>
    /// Only the <c>if</c>, Bool field-read and field access are used — all codegen-ready without
    /// pattern/operator lowering, so this works in BOTH the global variant path (which has
    /// downstream lowering) and the monomorphized fallback path (which does not). Scoped to the
    /// <c>Try</c> kind: only the <c>Maybe</c> carrier has the flat <c>{present,value}</c> layout this
    /// unwrap relies on; Check/Lookup carriers keep the existing tail-position behavior.
    /// </summary>
    private static List<Statement> TransformBlockStatements(List<Statement> stmts, int start,
        ErrorHandlingVariantKind kind, VariantCallRewriter? rewriter, TypeRegistry? registry,
        bool nextOnly)
    {
        var result = new List<Statement>();
        for (int i = start; i < stmts.Count; i++)
        {
            Statement s = stmts[index: i];

            // A failable call NESTED inside the statement (`return f().item0`, `g(f())`) is invisible to the
            // statement-shaped propagation below. Split it into its own `var` first (A-normal form), then
            // process the hoisted declarations and the residual statement like any others.
            if (TryHoistNestedFailables(stmt: s,
                    kind: kind,
                    registry: registry,
                    nextOnly: nextOnly,
                    hoisted: out List<Statement>? hoisted,
                    residual: out Statement? residual))
            {
                var respliced = new List<Statement>(collection: hoisted!) { residual! };
                respliced.AddRange(collection: stmts.Skip(count: i + 1));
                result.AddRange(collection: TransformBlockStatements(stmts: respliced,
                    start: 0,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly));
                return result;
            }

            // A call through a routine value: its recovering entry's carrier is matched like an inner variant's,
            // whatever this variant's kind.
            if (registry != null && ValueCallsRecover(kind: kind, nextOnly: nextOnly) &&
                TryBuildRoutineValuePropagation(stmt: s,
                    registry: registry,
                    safeCall: out CallExpression? valueCall,
                    bindName: out string? valueBind,
                    canBeAbsent: out bool valueCanBeAbsent))
            {
                List<Statement> remainder = TransformBlockStatements(stmts: stmts,
                    start: i + 1,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly);
                result.Add(item: BuildCarrierPropagationWhen(subject: valueCall!,
                    bindName: valueBind,
                    caps: new CarrierCapabilities(Kind: kind,
                        InnerCanNone: valueCanBeAbsent,
                        InnerCanError: true),
                    remainder: remainder,
                    registry: registry,
                    loc: s.Location));
                return result; // remainder consumed into the when's success arm
            }

            if (kind is ErrorHandlingVariantKind.Try or ErrorHandlingVariantKind.TryBool && registry != null &&
                TryBuildTryPropagation(
                    stmt: s,
                    registry: registry,
                    nextOnly: nextOnly,
                    tempDecl: out Statement? tempDecl,
                    presentCondition: out Expression? presentCondition,
                    bindStmt: out Statement? bindStmt,
                    moveOutStmt: out Statement? moveOutStmt))
            {
                List<Statement> remainder = TransformBlockStatements(stmts: stmts,
                    start: i + 1,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly);

                var thenStmts = new List<Statement>();
                if (bindStmt != null)
                {
                    thenStmts.Add(item: bindStmt);
                }

                if (moveOutStmt != null)
                {
                    thenStmts.Add(item: moveOutStmt);
                }

                thenStmts.AddRange(collection: remainder);

                result.Add(item: tempDecl!);
                result.Add(item: new IfStatement(Condition: presentCondition!,
                    ThenStatement: new BlockStatement(Statements: thenStmts, Location: s.Location),
                    ElseStatement: new VariantReturnStatement(VariantKind: kind,
                        SiteKind: VariantSiteKind.FromAbsent,
                        Value: null,
                        Location: s.Location),
                    Location: s.Location));
                return result; // remainder consumed into the if's then-branch
            }

            // Check/Lookup variants: Result/Lookup carriers are tag-based (not the flat {present,value}
            // of Maybe), so propagate a non-tail failable call through a `when` over the inner's
            // same-kind variant. A variant built from a monomorphized body (nextOnly) does it for an inner `emit`
            // only, as for try (see TryBuildTryPropagation).
            if (kind is ErrorHandlingVariantKind.Check or ErrorHandlingVariantKind.Lookup &&
                registry != null && TryBuildCarrierSafeCall(stmt: s,
                    nextOnly: nextOnly,
                    registry: registry,
                    kind: kind,
                    safeCall: out Expression? carrierCall,
                    bindName: out string? carrierBind,
                    innerCanNone: out bool innerCanNone,
                    innerCanError: out bool innerCanError))
            {
                List<Statement> remainder = TransformBlockStatements(stmts: stmts,
                    start: i + 1,
                    kind: kind,
                    rewriter: rewriter,
                    registry: registry,
                    nextOnly: nextOnly);
                result.Add(item: BuildCarrierPropagationWhen(subject: carrierCall!,
                    bindName: carrierBind,
                    caps: new CarrierCapabilities(Kind: kind,
                        InnerCanNone: innerCanNone,
                        InnerCanError: innerCanError),
                    remainder: remainder,
                    registry: registry!,
                    loc: s.Location));
                return result; // remainder consumed into the when's success arm
            }

            result.Add(item: TransformBodyCore(body: s,
                kind: kind,
                rewriter: rewriter,
                registry: registry,
                nextOnly: nextOnly));
        }

        return result;
    }

    /// <summary>
    /// Splits a statement whose failable calls are NESTED inside an expression into A-normal form, so the
    /// statement-shaped propagation (<see cref="TryBuildTryPropagation"/> / <see cref="TryBuildCarrierSafeCall"/>)
    /// and the tail rewriter can see them. <c>return v.sqrtrem().item0</c> becomes
    /// <c>var __rf_hoist_0 = v.sqrtrem()</c> then <c>return __rf_hoist_0.item0</c>.
    ///
    /// Only eagerly evaluated positions are split: the value of a <c>return</c>, a variable initializer, an
    /// expression / <c>discard</c> statement, an assignment's value, and an <c>if</c> condition (evaluated once,
    /// before either branch). Inside an expression, conditionally evaluated operands are never hoisted
    /// ahead of their condition: an <c>and</c>/<c>or</c> whose right side holds a failable call is lowered to
    /// <c>var t = left</c> plus an <c>if</c> that assigns the right side only when it would run, so that call
    /// is split out inside the branch. The fallback of <c>??</c> is lowered the same way, to a <c>when</c> over
    /// the carrier that evaluates it only in the arm without a value. The branches of a conditional return and
    /// a loop condition (which re-runs every iteration) are reshaped by <see cref="TransformBodyCore"/> instead.
    /// Every operand of a chained comparison is evaluated eagerly, left to right, so each is split like any other.
    ///
    /// Evaluation order is preserved: every call evaluated up to the LAST nested failable call is hoisted in
    /// evaluation order, failable or not, so a plain call that ran before the failable one still does. A
    /// statement whose ROOT is itself a failable call (<c>return f(...)</c>, <c>var x = f(...)</c>, a bare
    /// <c>f(...)</c>) keeps that root in place, since the existing paths handle it, and only its nested calls
    /// are split out. Returns false when nothing needs splitting, or when no propagation would follow.
    /// </summary>
    private static bool TryHoistNestedFailables(Statement stmt, ErrorHandlingVariantKind kind,
        TypeRegistry? registry, bool nextOnly, out List<Statement>? hoisted, out Statement? residual)
    {
        hoisted = null;
        residual = null;
        // Split only when a propagation path will consume the hoisted declarations: any kind, with a registry
        // (a monomorphized body propagates only an inner `emit` and a routine value, see Propagates).
        bool propagationAvailable = registry != null;
        if (!propagationAvailable)
        {
            return false;
        }

        // Same restriction as the statement-shaped propagation: the monomorphized path-2 only threads
        // inner `emit` calls.
        bool Propagates(CallExpression call)
        {
            RoutineInfo? rr = call.ResolvedRoutine;
            if (!registry!.CanFailUnderRecovery(routine: rr))
            {
                // A call through a routine value is split out too (its recovering entry is called on its own).
                return ValueCallsRecover(kind: kind, nextOnly: nextOnly) && CallCanFail(call: call, registry: registry);
            }

            return !nextOnly || rr!.Name == "emit";
        }

        (Expression? root, bool keepRootCall) = stmt switch
        {
            ReturnStatement { Value: { } v } => (v, v is CallExpression c && Propagates(call: c)),
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } } =>
                (init, init is CallExpression c && Propagates(call: c)),
            ExpressionStatement { Expression: var e } => (e, e is CallExpression c && Propagates(call: c)),
            DiscardStatement { Expression: var e } => (e, false),
            AssignmentStatement { Value: var v } => (v, false),
            IfStatement { Condition: var c } => (c, false),
            _ => ((Expression?)null, false)
        };
        if (root == null)
        {
            return false;
        }

        // Pass 1: classify every eagerly evaluated call in evaluation (post-) order.
        var scan = new NestedFailableHoister(propagates: Propagates, hoistUpTo: -1, skipIndex: -1);
        scan.VisitExpression(expr: root);
        List<bool> failable = scan.CallIsFailable;
        int rootIndex = keepRootCall ? failable.Count - 1 : -1;
        int last = -1;
        for (int i = 0; i < failable.Count; i++)
        {
            if (failable[index: i] && i != rootIndex)
            {
                last = i;
            }
        }

        if (last < 0)
        {
            return false;
        }

        // Pass 2: hoist every call up to and including the last nested failable one (the kept root excepted).
        var hoister = new NestedFailableHoister(propagates: Propagates, hoistUpTo: last, skipIndex: rootIndex);
        Expression newRoot = hoister.VisitExpression(expr: root);
        hoisted = hoister.Hoisted;
        residual = stmt switch
        {
            ReturnStatement r => r with { Value = newRoot },
            DeclarationStatement { Declaration: VariableDeclaration vd } d =>
                d with { Declaration = vd with { Initializer = newRoot } },
            ExpressionStatement es => es with { Expression = newRoot },
            DiscardStatement ds => ds with { Expression = newRoot },
            AssignmentStatement a => a with { Value = newRoot },
            IfStatement ifs => ifs with { Condition = newRoot },
            _ => stmt
        };
        return true;
    }

    private static int _hoistTemp;

    /// <summary>
    /// Walks one expression in evaluation order, skipping conditionally evaluated operands. Every
    /// <see cref="CallExpression"/> it reaches gets a sequence index (post-order). The scan mode
    /// (<c>hoistUpTo</c> = -1) only records which indices are propagatable failable calls. The hoist mode
    /// moves each call with index &lt;= <c>hoistUpTo</c> (except <c>skipIndex</c>) into a fresh
    /// <c>var __rf_hoist_N = call</c> in <see cref="Hoisted"/> and replaces it with a reference to the temp.
    /// An entity-typed temp is referenced through <c>steal</c>, which keeps the original meaning of the
    /// call's result as a temporary that is moved into its use rather than a named local the scope destroys.
    /// </summary>
    private sealed class NestedFailableHoister(Func<CallExpression, bool> propagates, int hoistUpTo,
        int skipIndex) : AstRewriter
    {
        private int _index;

        /// <summary>Per reached call, in evaluation order: is it a propagatable failable call.</summary>
        public List<bool> CallIsFailable { get; } = [];

        /// <summary>The hoisted temp declarations, in evaluation order.</summary>
        public List<Statement> Hoisted { get; } = [];

        protected override Expression VisitCall(CallExpression e)
        {
            Expression rewritten = base.VisitCall(e: e);
            int index = _index++;
            if (rewritten is not CallExpression call)
            {
                return rewritten;
            }

            CallIsFailable.Add(item: propagates(arg: call));
            if (index > hoistUpTo || index == skipIndex)
            {
                return call;
            }

            string tempName = $"__rf_hoist_{Interlocked.Increment(location: ref _hoistTemp)}";
            Hoisted.Add(item: new DeclarationStatement(Declaration: new VariableDeclaration(
                    Name: tempName,
                    Type: null,
                    Initializer: call,
                    Visibility: VisibilityModifier.Secret,
                    Location: call.Location),
                Location: call.Location));
            var reference = new IdentifierExpression(Name: tempName, Location: call.Location)
            {
                ResolvedType = call.ResolvedType
            };
            return call.ResolvedType?.Category == TypeCategory.Entity
                ? new StealExpression(Operand: reference, Location: call.Location)
                {
                    ResolvedType = call.ResolvedType
                }
                : reference;
        }

        protected override Expression VisitBinary(BinaryExpression e)
        {
            if (e.Operator is not (BinaryOperator.And or BinaryOperator.Or or BinaryOperator.NoneCoalesce))
            {
                return base.VisitBinary(e: e);
            }

            // Short-circuit: only the left operand always runs.
            Expression left = VisitExpression(expr: e.Left);
            Expression kept = ReferenceEquals(objA: left, objB: e.Left) ? e : e with { Left = left };
            if (!RightHasFailable(right: e.Right))
            {
                return kept;
            }

            if (e.Operator == BinaryOperator.NoneCoalesce)
            {
                return HoistCoalesce(e: e, left: left, kept: kept);
            }

            // `and` / `or` with a failable call on the right: the right side counts as one failable step in
            // evaluation order (after the left), so everything before it is hoisted too.
            int index = _index++;
            CallIsFailable.Add(item: true);
            if (index > hoistUpTo || index == skipIndex)
            {
                return kept;
            }

            // Lower to statements so the right side still runs only when needed:
            //   var t = <left>
            //   if t       (and)        |  if t (or): nothing, else t = <right>
            //     t = <right>
            // The assignment's failable calls are split out again when the branch itself is transformed.
            string tempName = $"__rf_lazy_{Interlocked.Increment(location: ref _hoistTemp)}";
            SourceLocation loc = e.Location;
            Hoisted.Add(item: new DeclarationStatement(Declaration: new VariableDeclaration(Name: tempName,
                    Type: null,
                    Initializer: left,
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc));
            IdentifierExpression Ref() => new(Name: tempName, Location: loc) { ResolvedType = e.ResolvedType };
            var assignRight = new BlockStatement(
                Statements: [new AssignmentStatement(Target: Ref(), Value: e.Right, Location: loc)],
                Location: loc);
            Hoisted.Add(item: e.Operator == BinaryOperator.And
                ? new IfStatement(Condition: Ref(), ThenStatement: assignRight, ElseStatement: null, Location: loc)
                : new IfStatement(Condition: Ref(),
                    ThenStatement: new BlockStatement(Statements: [], Location: loc),
                    ElseStatement: assignRight,
                    Location: loc));
            return Ref();
        }

        /// <summary>
        /// <c>carrier ?? fallback</c> with a failable call in the fallback, which runs only when the carrier
        /// holds no value. Lowered to statements so the fallback's call is split out inside that branch:
        /// <code>
        /// var __rf_car_N = carrier
        /// var __rf_lazy_N: T         (late-initialized)
        /// when __rf_car_N
        ///   is T v => __rf_lazy_N = v
        ///   else => __rf_lazy_N = fallback
        /// </code>
        /// </summary>
        private Expression HoistCoalesce(BinaryExpression e, Expression left, Expression kept)
        {
            if (e.ResolvedType is not { } valueType || left.ResolvedType is not { } carrierType)
            {
                return kept;
            }

            int index = _index++;
            CallIsFailable.Add(item: true);
            if (index > hoistUpTo || index == skipIndex)
            {
                return kept;
            }

            int n = Interlocked.Increment(location: ref _hoistTemp);
            string carrierName = $"__rf_car_{n}";
            string resultName = $"__rf_lazy_{n}";
            string valueName = $"__rf_val_{n}";
            SourceLocation loc = e.Location;
            Hoisted.Add(item: new DeclarationStatement(Declaration: new VariableDeclaration(Name: carrierName,
                    Type: null,
                    Initializer: left,
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc));
            Hoisted.Add(item: new DeclarationStatement(Declaration: new VariableDeclaration(Name: resultName,
                    Type: ExpressionLoweringPass.TypeInfoToExpr(type: valueType, loc: loc),
                    Initializer: null,
                    Visibility: VisibilityModifier.Secret,
                    Location: loc,
                    IsLateInit: true),
                Location: loc));
            IdentifierExpression Result() => new(Name: resultName, Location: loc) { ResolvedType = valueType };
            Hoisted.Add(item: new WhenStatement(
                Expression: new IdentifierExpression(Name: carrierName, Location: loc) { ResolvedType = carrierType },
                Clauses:
                [
                    new WhenClause(Pattern: new TypePattern(
                            Type: ExpressionLoweringPass.TypeInfoToExpr(type: valueType, loc: loc),
                            VariableName: valueName,
                            Bindings: null,
                            Location: loc),
                        Body: new AssignmentStatement(Target: Result(),
                            Value: new IdentifierExpression(Name: valueName, Location: loc) { ResolvedType = valueType },
                            Location: loc),
                        Location: loc),
                    new WhenClause(Pattern: new ElsePattern(VariableName: null, Location: loc),
                        Body: new BlockStatement(
                            Statements: [new AssignmentStatement(Target: Result(), Value: e.Right, Location: loc)],
                            Location: loc),
                        Location: loc)
                ],
                Location: loc));
            return Result();
        }

        // Whether a conditionally evaluated operand holds a propagatable failable call anywhere.
        private bool RightHasFailable(Expression right)
        {
            var probe = new NestedFailableHoister(propagates: propagates, hoistUpTo: -1, skipIndex: -1);
            probe.VisitExpression(expr: right);
            return probe.CallIsFailable.Contains(item: true);
        }

        protected override Expression VisitConditional(ConditionalExpression e)
        {
            // Only the condition always runs.
            Expression condition = VisitExpression(expr: e.Condition);
            return ReferenceEquals(objA: condition, objB: e.Condition) ? e : e with { Condition = condition };
        }

        protected override Expression VisitChainedComparison(ChainedComparisonExpression e)
        {
            // Every operand of `a < b < c` runs, once, left to right, before any comparison (only the
            // comparisons stop at the first false link), so a failure in any operand is a failure point.
            var operands = new List<Expression>(collection: e.Operands);
            bool changed = false;
            for (int i = 0; i < operands.Count; i++)
            {
                Expression visited = VisitExpression(expr: operands[index: i]);
                changed |= !ReferenceEquals(objA: visited, objB: operands[index: i]);
                operands[index: i] = visited;
            }

            return changed ? e with { Operands = operands } : e;
        }

        protected override Expression VisitRecovery(RecoveryExpression e)
        {
            // A nested recovery keyword already handles its own failures.
            return e;
        }
    }

    /// <summary>Whether <paramref name="type"/> is a token on a value where it is stored (<c>Viewing[T]</c>,
    /// <c>Modifying[T]</c>).</summary>
    private static bool IsAccessToken(TypeSymbol type)
    {
        return type is RecordTypeSymbol token &&
               (token.GenericDefinition ?? token).BareName is RuntimeContract.Viewing or RuntimeContract.Modifying;
    }

    /// <summary>
    /// If <paramref name="stmt"/> uses a failable call in non-tail position (a <c>var x = F!(...)</c>
    /// declaration or a bare <c>F!(...)</c> expression statement) and the callee has a <c>try</c>
    /// variant returning a <c>Maybe</c>, produces the spliced pieces:
    /// <list type="bullet">
    /// <item><paramref name="tempDecl"/>: <c>var __rf_prop_N = recv.try_X(...)</c></item>
    /// <item><paramref name="presentCondition"/>: <c>__rf_prop_N.present</c> (the <c>if</c> condition)</item>
    /// <item><paramref name="bindStmt"/>: <c>var x = __rf_prop_N.value</c> (null when the result was discarded)</item>
    /// <item><paramref name="moveOutStmt"/>: <c>__rf_prop_N.present = false</c> when the value moves out to
    /// <c>x</c> (a payload with no <c>assign</c>, such as an entity): the carrier no longer owns it, so its
    /// teardown does not free what <c>x</c> now holds</item>
    /// </list>
    /// Returns false — leaving the original crash-on-absence statement untouched — when no matching
    /// Maybe-returning <c>try</c> variant resolves.
    /// </summary>
    private static bool TryBuildTryPropagation(Statement stmt, TypeRegistry registry,
        bool nextOnly, out Statement? tempDecl, out Expression? presentCondition,
        out Statement? bindStmt, out Statement? moveOutStmt)
    {
        tempDecl = null;
        presentCondition = null;
        bindStmt = null;
        moveOutStmt = null;

        CallExpression failCall;
        string? bindName;
        switch (stmt)
        {
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: CallExpression ce } vd
            } when registry.CanFailUnderRecovery(routine: ce.ResolvedRoutine):
                failCall = ce;
                bindName = vd.Name;
                break;
            case ExpressionStatement { Expression: CallExpression ce2 }
                when registry.CanFailUnderRecovery(routine: ce2.ResolvedRoutine):
                failCall = ce2;
                bindName = null;
                break;
            default:
                return false;
        }

        RoutineInfo failRoutine = failCall.ResolvedRoutine;
        string baseName = failRoutine.Name;

        // In the monomorphized (path-2) caller — which runs AFTER reachability — restrict propagation
        // to inner `emit!` calls: `emit`'s try variant is systematically emitted for live iterator instances, so
        // the propagated chain always links, whereas an arbitrary `try_X` introduced here wouldn't be
        // marked live and would LINKERR (e.g. a bounds-guarded `getitem!` in SortedSetIterator, which
        // also can't actually fail). The global (path-1) caller runs BEFORE reachability, so it
        // propagates ALL non-tail failable calls and reachability then emits the introduced variants.
        if (nextOnly && baseName != "emit")
        {
            return false;
        }

        // The try variant of THIS overload (free or member: a whole-expression `try` composition body —
        // SemanticVerifier.Recovery — hoists FREE failable calls, which have no OwnerType).
        RoutineInfo? variant = registry.LookupRecoveryVariant(recovered: failRoutine, kind: RecoveryKind.Try);

        if (variant?.ReturnType is not { } carrier)
        {
            return false;
        }

        // A protocol's routine reached through a receiver whose type is a parameter carries `Me` in its result
        // (`Emittable.emit!() -> Me/Item`). `Me` is that receiver, not the routine this statement is in.
        if (failCall.Callee is MemberExpression { Object.ResolvedType: { } receiverType })
        {
            carrier = registry.ReplaceProtocolSelf(type: carrier, owner: receiverType);
        }

        // A failable routine that returns nothing has the TryBool try variant: its Bool result is the
        // success flag itself, and there is no payload to bind. A statement that binds the result of
        // such a call has nothing to unwrap, so it is left as it is.
        bool flagOnly = variant.FailableVariant == FailableVariant.TryBool;
        if (flagOnly ? bindName != null : carrier.TypeArguments is not { Count: > 0 })
        {
            return false;
        }

        SourceLocation loc = stmt.Location;
        string tempName = $"__rf_prop_{Interlocked.Increment(location: ref _propTemp)}";

        // Retarget the failable call to its try variant, typed as the carrier.
        CallExpression safeCall = BindToVariant(call: failCall, variant: variant);
        safeCall.ResolvedType = carrier;

        tempDecl = new DeclarationStatement(Declaration: new VariableDeclaration(Name: tempName,
                Type: null,
                Initializer: safeCall,
                Visibility: VisibilityModifier.Secret,
                Location: loc),
            Location: loc);

        if (flagOnly)
        {
            presentCondition = new IdentifierExpression(Name: tempName, Location: loc)
            {
                ResolvedType = carrier
            };
            return true;
        }

        presentCondition = new MemberExpression(
            Object: new IdentifierExpression(Name: tempName, Location: loc)
            {
                ResolvedType = carrier
            },
            MemberName: RuntimeContract.Carrier.PresentField,
            Location: loc);

        if (bindName != null)
        {
            TypeSymbol? valueType = carrier.TypeArguments[index: 0];
            Expression valueAccess = new MemberExpression(
                Object: new IdentifierExpression(Name: tempName, Location: loc)
                {
                    ResolvedType = carrier
                },
                MemberName: RuntimeContract.Carrier.ValueField,
                Location: loc) { ResolvedType = valueType };

            // The extracted payload ALIASES the carrier's heap buffer as a plain field read rather than a
            // move. Binding the payload and later destroying BOTH the bound name AND the carrier would
            // double-free a MANAGED or ASSIGNABLE payload such as Bytes, Text, or a record. For those, the
            // Assign derive performs a refcount-increment share or structural co-own rather than a buffer
            // copy, which balances the two destroys. A MOVE-ONLY entity payload has no Assign derive because
            // it is single-owner. Leave that extract as a plain passthrough and let the ownership checker
            // treat it as the move it is. Trying to assign it would reach codegen unresolved.
            // A token payload (an iterator's `Modifying[T]`) is an address with nothing to own: it is read as it is.
            // Looking `assign` up on it would reach the value's own `assign` through the token.
            bool payloadAssignable = valueType != null && !IsAccessToken(type: valueType) && registry.LookupMemberRoutine(
                type: valueType,
                memberRoutineName: RuntimeContract.Duplication.Assign,
                isFailable: false) != null;
            Expression boundValue = payloadAssignable
                ? new CallExpression(
                    Callee: new MemberExpression(Object: valueAccess,
                        MemberName: RuntimeContract.Duplication.Assign,
                        Location: loc) { ResolvedType = valueType },
                    Arguments: [],
                    Location: loc) { ResolvedType = valueType }
                : valueType != null && !IsAccessToken(type: valueType)
                    // An entity moves out of the carrier, which forgets it below.
                    ? new StealExpression(Operand: valueAccess, Location: loc) { ResolvedType = valueType }
                    : valueAccess;

            bindStmt = new DeclarationStatement(
                Declaration: new VariableDeclaration(Name: bindName,
                    Type: null,
                    Initializer: boundValue,
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc);

            // A payload with no `assign` (an entity) moves out: the carrier gives it up, as `strip_out` does.
            if (!payloadAssignable && valueType != null && !IsAccessToken(type: valueType))
            {
                moveOutStmt = new AssignmentStatement(
                    Target: new MemberExpression(
                        Object: new IdentifierExpression(Name: tempName, Location: loc) { ResolvedType = carrier },
                        MemberName: RuntimeContract.Carrier.PresentField,
                        Location: loc) { ResolvedType = registry.LookupType(name: "Bool") },
                    Value: new LiteralExpression(Value: false,
                        LiteralType: Tokenizer.TokenType.False,
                        Location: loc) { ResolvedType = registry.LookupType(name: "Bool") },
                    Location: loc);
            }
        }

        return true;
    }

    /// <summary>
    /// <c>var x = f(a)</c> or a bare <c>f(a)</c> where <c>f</c> is a routine value: the call of its recovering
    /// entry (<see cref="RoutineValueCalls.Recovering"/>), the name its value binds, and whether its carrier can
    /// come back absent (it can always come back with an error). False for any other statement, and inside the
    /// try variant of an iterator's <c>emit</c> (see <see cref="_valueCallsStayPlain"/>).
    /// </summary>
    private static bool TryBuildRoutineValuePropagation(Statement stmt, TypeRegistry registry,
        out CallExpression? safeCall, out string? bindName, out bool canBeAbsent)
    {
        safeCall = null;
        bindName = null;
        canBeAbsent = false;
        if (_valueCallsStayPlain)
        {
            return false;
        }

        CallExpression call;
        switch (stmt)
        {
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: CallExpression ce } vd }:
                call = ce;
                bindName = vd.Name;
                break;
            case ExpressionStatement { Expression: CallExpression ce2 }:
                call = ce2;
                break;
            default:
                return false;
        }

        if (RoutineValueCalls.ValueType(call: call) is not { } routineType ||
            RoutineValueCalls.Recovering(call: call, routineType: routineType, registry: registry) is not
                { } recovering)
        {
            bindName = null;
            return false;
        }

        safeCall = recovering;
        canBeAbsent = RoutineValueCalls.CanBeAbsent(routineType: routineType);
        return true;
    }

    /// <summary>
    /// Like <see cref="TryBuildTryPropagation"/> but for Check/Lookup variants: retargets a non-tail
    /// failable call to the BEST available inner safe variant and reports what that carrier can fail
    /// with. The inner routine may not have the outer's exact kind — variant generation produces
    /// try only (absent-only), try+grab (throw-only), or try+lookup (both). So fall back:
    /// prefer the outer's kind, then lookup &gt; grab &gt; try (try always exists). The chosen
    /// carrier's capabilities (<paramref name="innerCanNone"/>/<paramref name="innerCanError"/>)
    /// drive which arms <see cref="BuildCarrierPropagationWhen"/> emits. Returns false when no failure
    /// arm would apply (e.g. a Check outer over an absent-only inner — an inconsistent combination),
    /// leaving the original statement untouched.
    /// </summary>
    private static bool TryBuildCarrierSafeCall(Statement stmt, TypeRegistry registry, bool nextOnly,
        ErrorHandlingVariantKind kind, out Expression? safeCall, out string? bindName,
        out bool innerCanNone, out bool innerCanError)
    {
        safeCall = null;
        bindName = null;
        innerCanNone = false;
        innerCanError = false;

        CallExpression failCall;
        switch (stmt)
        {
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: CallExpression ce } vd
            } when registry.CanFailUnderRecovery(routine: ce.ResolvedRoutine):
                failCall = ce;
                bindName = vd.Name;
                break;
            case ExpressionStatement { Expression: CallExpression ce2 }
                when registry.CanFailUnderRecovery(routine: ce2.ResolvedRoutine):
                failCall = ce2;
                bindName = null;
                break;
            default:
                return false;
        }

        RoutineInfo failRoutine = failCall.ResolvedRoutine;
        if (nextOnly && failRoutine.Name != "emit")
        {
            return false;
        }

        // Prefer the outer kind's variant, then fall back to the most-informative available (free or member
        // bases: a whole-expression `grab`/`lookup` composition body — SemanticVerifier.Recovery — hoists FREE
        // failable calls, which have no OwnerType).
        RecoveryKind[] order = kind == ErrorHandlingVariantKind.Check
            ? [RecoveryKind.Grab, RecoveryKind.Lookup, RecoveryKind.Try]
            : [RecoveryKind.Lookup, RecoveryKind.Grab, RecoveryKind.Try];
        (RoutineInfo? variant, RecoveryKind chosen) =
            ChooseInnerVariant(registry: registry, failRoutine: failRoutine, order: order);

        if (variant?.ReturnType is null)
        {
            return false;
        }

        innerCanNone = chosen is RecoveryKind.Try or RecoveryKind.Lookup;
        innerCanError = chosen is RecoveryKind.Grab or RecoveryKind.Lookup;

        // A Lookup outer represents None natively; a Check outer has no None state but ABSORBS a caught
        // absent by promoting it to AbsentValueError (a Crashable) — grab collapses "not found" into the
        // single Crashable arm. Both outers represent an error. If neither failure the inner can produce
        // maps onto the outer, propagation is meaningless — leave the call raw.
        bool outerAbsorbsNone = kind is ErrorHandlingVariantKind.Lookup or ErrorHandlingVariantKind.Check;
        if (!(innerCanNone && outerAbsorbsNone || innerCanError))
        {
            return false;
        }

        safeCall = BindToVariant(call: failCall, variant: variant);
        // The carrier holds what the call returns HERE. The variant's own return names its owner's parameter
        // (`List[T].getitem` gives `Check[T]`), which in a routine on a specialized receiver (`List[Agent[T]]`)
        // is that routine's `T` by name only, a different slot.
        if (variant.ReturnType is { TypeArguments: [_] } carrierType &&
            failCall.ResolvedType is { IsNone: false } payload &&
            carrierType switch
            {
                VariantTypeSymbol v => v.GenericDefinition,
                RecordTypeSymbol r => r.GenericDefinition,
                _ => null
            } is { } carrierDef)
        {
            safeCall.ResolvedType = registry.GetOrCreateResolution(genericDef: carrierDef, typeArguments: [payload]);
        }

        return true;
    }

    /// <summary>
    /// Walks the preferred recovery kinds in order and returns the first variant of
    /// <paramref name="failRoutine"/> whose carrier return type carries payload type arguments, together
    /// with the kind that selected it. Returns <c>(null, Try)</c> when none apply.
    /// </summary>
    private static (RoutineInfo? variant, RecoveryKind chosen) ChooseInnerVariant(TypeRegistry registry,
        RoutineInfo failRoutine, RecoveryKind[] order)
    {
        foreach (RecoveryKind kind in order)
        {
            if (registry.LookupRecoveryVariant(recovered: failRoutine, kind: kind) is
                { ReturnType.TypeArguments.Count: > 0 } variant)
            {
                return (variant, kind);
            }
        }

        return (null, RecoveryKind.Try);
    }

    /// <summary>
    /// Builds the <c>when</c> that short-circuits a Check/Lookup variant on the inner carrier's
    /// failure and otherwise binds the unwrapped success value before running the remainder:
    /// <code>
    /// when inner.&lt;safe&gt;_x()
    ///   is None -&gt; &lt;return None&gt;          # inner can None AND outer is Lookup
    ///   is Crashable e -&gt; &lt;return error e&gt; # inner can error (Check/Lookup outer)
    ///   else var x -&gt; &lt;remainder&gt;
    /// </code>
    /// Arms are emitted only for failures the chosen inner carrier can produce AND the outer can
    /// represent. Lowered by CrashableExpansionPass + PatternLoweringPass on path-1 variant bodies.
    /// </summary>
    /// <summary>
    /// The outer variant kind together with the failure states the chosen inner carrier can produce —
    /// the capability triple that drives which propagation arms <see cref="BuildCarrierPropagationWhen"/>
    /// emits.
    /// </summary>
    private readonly record struct CarrierCapabilities(
        ErrorHandlingVariantKind Kind, bool InnerCanNone, bool InnerCanError);

    private static Statement BuildCarrierPropagationWhen(Expression subject, string? bindName,
        CarrierCapabilities caps, List<Statement> remainder, TypeRegistry registry, SourceLocation loc)
    {
        ErrorHandlingVariantKind kind = caps.Kind;
        bool innerCanNone = caps.InnerCanNone;
        bool innerCanError = caps.InnerCanError;
        // Bind the subject carrier to a temp: the error arm moves the caught error out of it.
        string subjName = $"__rc_carrier_{Interlocked.Increment(location: ref _propTemp)}";
        TypeSymbol? subjType = subject.ResolvedType;
        var subjDecl = new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: subjName,
                Type: null,
                Initializer: subject,
                Visibility: VisibilityModifier.Secret,
                Location: loc),
            Location: loc);
        IdentifierExpression SubjRef() =>
            new(Name: subjName, Location: loc) { ResolvedType = subjType };

        var clauses = new List<WhenClause>();

        // A try carrier keeps no error: an absent and an error beneath both make it absent.
        if (kind is ErrorHandlingVariantKind.Try or ErrorHandlingVariantKind.TryBool)
        {
            if (innerCanNone)
            {
                clauses.Add(item: new WhenClause(Pattern: new NonePattern(Location: loc),
                    Body: new VariantReturnStatement(VariantKind: kind,
                        SiteKind: VariantSiteKind.FromAbsent,
                        Value: null,
                        Location: loc),
                    Location: loc));
            }

            if (innerCanError)
            {
                clauses.Add(item: new WhenClause(
                    Pattern: new CrashablePattern(ErrorType: null, VariableName: null, Location: loc),
                    Body: new VariantReturnStatement(VariantKind: kind,
                        SiteKind: VariantSiteKind.FromAbsent,
                        Value: null,
                        Location: loc),
                    Location: loc));
            }
        }
        else if (innerCanNone && kind == ErrorHandlingVariantKind.Lookup)
        {
            // Lookup natively carries an absent state.
            clauses.Add(item: new WhenClause(Pattern: new NonePattern(Location: loc),
                Body: new VariantReturnStatement(VariantKind: kind,
                    SiteKind: VariantSiteKind.FromAbsent,
                    Value: null,
                    Location: loc),
                Location: loc));
        }
        else if (innerCanNone && kind == ErrorHandlingVariantKind.Check)
        {
            // Check has no absent state — grab promotes a caught absent to AbsentValueError (a concrete
            // Crashable, so its type_id is baked correctly by the normal FromThrow lowering).
            var absentError = new CreatorExpression(TypeName: "AbsentValueError",
                TypeArguments: null,
                MemberVariables: [],
                Location: loc) { ResolvedType = registry.LookupType(name: "AbsentValueError") };
            clauses.Add(item: new WhenClause(Pattern: new NonePattern(Location: loc),
                Body: new VariantReturnStatement(VariantKind: kind,
                    SiteKind: VariantSiteKind.FromThrow,
                    Value: absentError,
                    Location: loc),
                Location: loc));
        }

        if (innerCanError && kind is not (ErrorHandlingVariantKind.Try or ErrorHandlingVariantKind.TryBool))
        {
            // The caught error MOVES to the outer carrier (PatternLoweringPass clears the inner carrier's tag, so its
            // teardown does not free the object the outer one now holds).
            const string errName = "__rf_prop_err";
            clauses.Add(item: new WhenClause(
                Pattern: new CrashablePattern(ErrorType: null,
                    VariableName: errName,
                    Location: loc),
                Body: new VariantReturnStatement(VariantKind: kind,
                    SiteKind: VariantSiteKind.FromThrow,
                    Value: new IdentifierExpression(Name: errName, Location: loc)
                    {
                        ResolvedType = registry.LookupType(name: RuntimeContract.Crashables)
                    },
                    Location: loc),
                Location: loc));
        }

        // The success value stays the carrier's (its teardown frees it), so the remainder takes a copy of its own:
        // the arm binds the payload as read, and `var x = <payload>` is an ordinary initialization the copy pass
        // gives a share (as the try propagation's `var x = __rf_prop.value`). Binding `x` straight to the payload
        // left a value the carrier freed under a returned or consumed `x`.
        string? payloadName = bindName != null
            ? $"__rc_ok_{Interlocked.Increment(location: ref _propTemp)}"
            : null;
        // The payload moves out: the binding takes it with `steal` and the carrier forgets it (its tag cleared, as
        // the error arm does), so only the binding tears it down. That is right for a value and for an entity alike,
        // and a `T` that may be either needs no choice. A token holds nothing to move.
        TypeSymbol? payloadType = subjType?.TypeArguments is [{ } held] ? held : null;
        bool movesOut = payloadType != null && !IsAccessToken(type: payloadType);
        Expression payloadRef = new IdentifierExpression(Name: payloadName ?? "", Location: loc) { ResolvedType = payloadType };
        List<Statement> body = bindName == null
            ? remainder
            :
            [
                new DeclarationStatement(
                    Declaration: new VariableDeclaration(Name: bindName,
                        Type: null,
                        Initializer: movesOut
                            ? new StealExpression(Operand: payloadRef, Location: loc) { ResolvedType = payloadType }
                            : payloadRef,
                        Visibility: VisibilityModifier.Secret,
                        Location: loc),
                    Location: loc),
                .. remainder
            ];
        clauses.Add(item: new WhenClause(
            Pattern: new ElsePattern(VariableName: payloadName, Location: loc) { MovesPayloadOut = movesOut },
            Body: new BlockStatement(Statements: body, Location: loc),
            Location: loc));

        var when = new WhenStatement(Expression: SubjRef(), Clauses: clauses, Location: loc);
        return new BlockStatement(Statements: [subjDecl, when], Location: loc);
    }

    /// <summary>
    /// Builds a registry-based <see cref="VariantCallRewriter"/> for the monomorphized fallback path
    /// (<see cref="Builder.Instantiation.Passes.GenericMonomorphizationPass"/>), which has no
    /// per-pass rewriter instance. It rewrites a TAIL-position <c>return src.emit!()</c> into a
    /// passthrough call to the matching <c>try/grab/lookupemit</c> variant (resolved via
    /// <see cref="TypeRegistry.LookupMemberRoutine"/> on the concrete callee owner). Restricted to
    /// <c>emit</c> for the same reason as <see cref="TryBuildTryPropagation"/>: the <c>try</c> variant of <c>emit</c> is
    /// systematically emitted for live iterator instances, so the rewritten chain always links.
    /// </summary>
    public static VariantCallRewriter MakeNextVariantRewriter(TypeRegistry registry)
    {
        return (Expression? value, ErrorHandlingVariantKind kind, out Expression? rewritten) =>
        {
            rewritten = null;
            if (kind == ErrorHandlingVariantKind.TryBool ||
                value is not CallExpression { ResolvedRoutine: { IsFailable: true, Name: "emit", OwnerType: not null } callee } call ||
                registry.LookupRecoveryVariant(recovered: callee, kind: ToRecovery(kind: kind)) is not { } variant)
            {
                return false;
            }

            rewritten = BindToVariant(call: call, variant: variant);
            return true;
        };
    }

    /// <summary>
    /// If <paramref name="value"/> is a tail-position call to a failable routine and a matching
    /// variant exists in the registry, returns a rewritten call that targets the variant.
    /// The rewritten call's resolved routine is the variant (non-failable) so codegen does not
    /// emit a throw-propagating call site.
    /// </summary>
    private bool TryRewriteToVariantCall(Expression? value, ErrorHandlingVariantKind kind,
        out Expression? rewritten)
    {
        return MakeOnDemandVariantRewriter(registry: ctx.Registry)(value: value, kind: kind, rewritten: out rewritten);
    }
}
