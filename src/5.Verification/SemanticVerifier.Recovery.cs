using Builder.Declaration;
using Builder.Diagnostics;
using Builder.Instantiation;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Phase 5: whole-expression <c>try</c>/<c>grab</c>/<c>lookup</c> recovery composition.
///
/// A recovery keyword wraps an ENTIRE expression and short-circuits to its carrier on the FIRST failure
/// (evaluation order: inner→outer, left→right). Rather than lower this inline (which, lacking a non-local
/// break, would nest a <c>when</c> per hop), we synthesize a failable BASE routine
/// <c>__recover_N!(freevars) -&gt; T</c> whose body is the inner expression in A-normal form (each failable
/// sub-call hoisted to its own <c>var</c>, in evaluation order), then take that base's
/// <c>try</c>/<c>grab</c>/<c>lookup</c> variant. The variant's <see cref="Builder.Instantiation.ErrorHandlingVariantPass.TransformBody"/>
/// turns every hoisted failable <c>var</c> into its own recovery variant + unwrap + early <c>return</c> —
/// i.e. the existing per-routine variant machinery does all the threading. The <see cref="RecoveryExpression"/>'s
/// <see cref="RecoveryExpression.LoweredCall"/> becomes a call to that variant (Phase-6 splices it in).
///
/// An expression with NO failable call degenerates naturally: the base body is just <c>return Inner</c>, and
/// its variant wraps the value into an always-present carrier — no error, nothing to recover.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>Monotonic sequence for uniquely naming synthesized recovery base routines per compile.</summary>
    private int _recoveryCompositionSeq;

    /// <summary>Prefix for hoisted per-sub-call temporaries in a composition base body.</summary>
    private const string RecoveryTempPrefix = "__rc_t";

    /// <summary>
    /// Analyzes a <c>try</c>/<c>grab</c>/<c>lookup</c> <see cref="RecoveryExpression"/> as a whole-expression
    /// composition (see the class remarks). Returns the carrier type (Maybe/Check/Lookup[T], None-collapsed);
    /// stashes the synthesized variant call on <see cref="RecoveryExpression.LoweredCall"/>.
    /// </summary>
    /// <summary>
    /// A <c>@crash_only</c> routine may only be called bare: reports a call of one written under the keyword
    /// (<c>try boom()</c>) and returns whether it found one.
    /// </summary>
    private bool ReportCrashOnlyRecovery(RecoveryExpression recovery)
    {
        CallExpression? crashOnly = null;
        AstWalker.WalkExpressions(root: recovery.Inner,
            visit: expression =>
            {
                if (crashOnly == null && expression is CallExpression { ResolvedRoutine: { } routine } call &&
                    routine.Annotations.Contains(item: "crash_only"))
                {
                    crashOnly = call;
                }
            });
        if (crashOnly?.ResolvedRoutine is not { } crashOnlyRoutine)
        {
            return false;
        }

        string keyword = RecoveryKeyword(kind: recovery.Kind);
        ReportError(code: SemanticDiagnosticCode.CrashOnlyRecovered,
            message: $"You wrote '{keyword}' over a call of '{crashOnlyRoutine.Name}', but '{crashOnlyRoutine.Name}' " +
                     "is '@crash_only': its failure cannot be recovered, so it is only called bare. Drop " +
                     $"'{keyword}', or call a routine that may be recovered.",
            location: crashOnly.Location);
        return true;
    }

    private TypeSymbol AnalyzeRecoveryExpression(RecoveryExpression recovery)
    {
        // Analyze the inner expression first so every sub-call carries its ResolvedRoutine and ResolvedType.
        // The decomposition below keys failable-ness off the ResolvedRoutine IsFailable flag.
        TypeSymbol innerType = AnalyzeExpression(expression: recovery.Inner);
        if (innerType is ErrorTypeSymbol)
        {
            return ErrorTypeSymbol.Instance;
        }

        if (ReportCrashOnlyRecovery(recovery: recovery))
        {
            return ErrorTypeSymbol.Instance;
        }

        // Decompose the inner expression into A-normal form: hoist each failable sub-call (post-order =
        // evaluation order) into `var __rc_tN = <call>`, replacing the call with a reference to the temp.
        // Referenced identifiers are collected in the SAME walk so we can pass the outer locals the body
        // reads as parameters of the synthesized routine.
        // A call written with explicit type arguments (`f[T](x)`, `o.m[T](x)`) is still its generic-call node
        // here (it becomes a plain call in a later lowering): take it as the plain call it is bound to.
        Expression inner = AsPlainCall(expr: recovery.Inner);
        var hoister = new RecoveryFailableHoister(seed: 0, registry: _registry);
        Expression residual = hoister.VisitExpression(expr: inner);

        // SINGLE failable call (the overwhelmingly common case): bind its recovery variant DIRECTLY by
        // reference (TypeRegistry.LookupRecoveryVariant) — the variant is the base routine's, never found by
        // a name. The base call already resolved concretely (a concrete receiver → the concrete member
        // routine, or the failable free reader for a conversion), so the variant comes substituted for that
        // same owner: concrete carrier `Maybe[S64]` (not the generic-def `Maybe[T]`), receiver stays in place
        // so an entity is BORROWED as the base call borrowed it (no synth-routine by-value capture → no
        // spurious RF-S413). Routine synthesis (below) is the fallback ONLY for a MULTI-call short-circuit, or
        // a shape the base has no variant for (grab over an absent-only base — the routine path shapes that).
        if (hoister.Hoisted is
                [
                    DeclarationStatement
                    {
                        Declaration: VariableDeclaration { Initializer: CallExpression }
                    }
                ] &&
            residual is IdentifierExpression &&
            inner is CallExpression { ResolvedRoutine: { } singleBase } singleCall &&
            _registry.LookupRecoveryVariant(recovered: singleBase, kind: recovery.Kind) is { } singleVariant &&
            BindResolvedVariantCall(call: singleCall, variant: singleVariant) is { } boundCall)
        {
            // A protocol's routine reached through its receiver carries `Me` in its carrier
            // (`Maybe[Me/Item]` for `try it.emit()`): `Me` is that receiver.
            if (boundCall is { Callee: MemberExpression { Object.ResolvedType: { } boundReceiver }, ResolvedType: { } carrier })
            {
                boundCall.ResolvedType = _registry.ReplaceProtocolSelf(type: carrier, owner: boundReceiver);
            }

            recovery.LoweredCall = boundCall;
            return boundCall.ResolvedType!;
        }

        // Free variables = referenced identifiers that resolve to an outer local/param (not a hoisted temp,
        // not a type/routine/global). These become the base routine's parameters.
        var freeParams = new List<ParamInfo>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (string name in hoister.ReferencedNames)
        {
            if (name.StartsWith(value: RecoveryTempPrefix, comparisonType: StringComparison.Ordinal) ||
                !seen.Add(item: name))
            {
                continue;
            }

            VariableInfo? local = _registry.LookupVariable(name: name);
            if (local != null)
            {
                // A parameter is a slot like any written one: in Suflae an entity local is passed as its
                // Roamed handle (the local itself is retyped to it later), so the slot takes that type too.
                freeParams.Add(item: new ParamInfo(name: name,
                    type: _typeResolver.RoamInferredSlot(inferred: local.Type)));
            }
        }

        // Build the base routine's body: the hoisted failable-call decls, then `return residual`.
        var bodyStmts = new List<Statement>(collection: hoister.Hoisted)
        {
            new ReturnStatement(Value: residual, Location: recovery.Location)
        };
        var baseBody = new BlockStatement(Statements: bodyStmts, Location: recovery.Location);

        // Synthesize the failable base routine __recover_N! and register it. It is a TEMPLATE — never called
        // directly (only its recovery variant is), so it is never collected/emitted on its own.
        // The carrier the KEYWORD selects drives which variant the base must expose:
        //  - try    → try    (Maybe): pessimistic (throw+absent) generates try.
        //  - lookup → lookup (Lookup): pessimistic (throw+absent) generates lookup.
        //  - grab   → grab  (Check): grab collapses the WHOLE crashable set (and, per the design, promotes
        //    a sub-call's absent to AbsentValueError) into Check's single Crashable arm. The variant rules
        //    only mint grab for a THROW-ONLY shape, so mark grab's base throw-only (HasThrow, no HasAbsent,
        //    non-pessimistic) — otherwise the both-shape yields lookup and no grab exists.
        bool grab = recovery.Kind == RecoveryKind.Grab;
        string baseName = $"__recover_{_recoveryCompositionSeq++}";
        var baseRoutine = new RoutineInfo(name: baseName)
        {
            Kind = RoutineKind.FreeRoutine,
            Parameters = freeParams,
            ReturnType = innerType,
            IsFailable = true,
            IsSynthesized = true,
            HasThrow = grab,
            // Open: this is builder-internal, and its variant inherits this visibility. Secret would trip
            // the cross-module access check (RF-S403) at the call site.
            Visibility = VisibilityModifier.Open,
            Location = recovery.Location,
            Module = _currentRoutine?.Module,
            ModulePath = _currentRoutine?.ModulePath
        };
        _registry.RegisterRoutine(routine: baseRoutine);

        // Index the base for on-demand variant synthesis. try/lookup use the pessimistic (throw+absent)
        // shape; grab uses the throw-only shape stamped above (so grab is generated).
        _registry.DeferredVariantBases[key: baseRoutine.RegistryKey] = (baseRoutine, baseBody, !grab);

        RoutineInfo? variant = _registry.LookupRecoveryVariant(recovered: baseRoutine, kind: recovery.Kind);
        if (variant == null)
        {
            // The requested carrier variant is not (yet) synthesizable for this base shape (e.g. `grab`'s
            // grab before the Check/Lookup propagation generalization). Surface as a generation error
            // rather than silently miscompiling.
            ReportError(code: SemanticDiagnosticCode.VariantGenerationError,
                message:
                $"'{RecoveryKeyword(kind: recovery.Kind)}' recovery composition could not synthesize its " +
                $"carrier variant for this expression.",
                location: recovery.Location);
            return ErrorTypeSymbol.Instance;
        }

        // ONE failable call on a receiver whose type is still a parameter (`try me.first.emit()` with
        // `first: TFirstIter`) reaches the protocol's routine, which has no variant of its own. The synthesized
        // base would keep that parameter in a body nothing instantiates. Monomorphization binds the concrete
        // receiver's own variant (GenericAstRewriter.BindRecoveryOnConcreteReceiver), so leave the call unbound
        // and give the expression the carrier type.
        if (hoister.Hoisted.Count == 1 && residual is IdentifierExpression &&
            inner is CallExpression { Callee: MemberExpression { Object.ResolvedType: { } receiverType } } &&
            ContainsUnresolvedTypeParameter(type: receiverType))
        {
            return variant.ReturnType ?? ErrorTypeSymbol.Instance;
        }

        // The LoweredCall is a free call bound to the variant, passing the captured locals as named
        // arguments. The variant has its base's name, so the call is bound here and marked analyzed: a
        // re-analysis by name would bind the failable base instead.
        List<Expression> args = freeParams
                                .Select(selector: p =>
                                 {
                                     var value = new IdentifierExpression(Name: p.Name, Location: recovery.Location);
                                     AnalyzeExpression(expression: value);
                                     return (Expression)new NamedArgumentExpression(Name: p.Name,
                                         Value: value,
                                         Location: recovery.Location) { ResolvedType = value.ResolvedType };
                                 })
                                .ToList();
        var loweredCall = new CallExpression(
            Callee: new IdentifierExpression(Name: variant.Name, Location: recovery.Location)
            {
                ResolvedRoutine = variant
            },
            Arguments: args,
            Location: recovery.Location)
        {
            ResolvedRoutine = variant,
            ResolvedType = variant.ReturnType,
            LoweringKind = ClassifyStandaloneRoutineCall(routine: variant),
            IsPreAnalyzed = true
        };
        recovery.LoweredCall = loweredCall;
        return variant.ReturnType ?? ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Binds a single failable <paramref name="call"/> to its recovery <paramref name="variant"/> (already
    /// substituted for the call's owner). The variant has the base's name and parameters, so the call keeps
    /// its shape and only its binding changes: ResolvedRoutine/LoweringKind/ResolvedType are stamped as the
    /// normal resolution would, and the call is marked analyzed (a re-analysis by name would bind the failable
    /// base). Returns null when the call shape is not a directly-bindable single call (the caller then defers
    /// to routine-synthesis composition).
    /// </summary>
    private static CallExpression? BindResolvedVariantCall(CallExpression call, RoutineInfo variant)
    {
        // CONVERSION recovery (`try x.S8()`): the base is a failable free reader `S8!(from:)` bound as a
        // TypeConstructor; its variant is the reader's recovery variant. Keep the member-call shape — the
        // construction passes the receiver as the `from:` argument and dispatches on ResolvedRoutine — and
        // stamp the variant + carrier return (the variant's ReturnType is Maybe/Check/Lookup[T]).
        if (call.LoweringKind == CallLoweringKind.TypeConstructor)
        {
            var conv = call with { IsFailable = false, IsPreAnalyzed = true };
            conv.ResolvedRoutine = variant;
            conv.LoweringKind = CallLoweringKind.TypeConstructor;
            conv.ConstructedType = call.ConstructedType;
            conv.ResolvedType = variant.ReturnType;
            return conv;
        }

        switch (call.Callee)
        {
            // MEMBER call (`l.remove_last()`): the receiver stays in place (borrowed, not consumed).
            case MemberExpression m:
            {
                var member = call with
                {
                    Callee = m with { IsFailable = false },
                    IsFailable = false,
                    IsPreAnalyzed = true
                };
                member.ResolvedRoutine = variant;
                member.LoweringKind = ClassifyMemberRoutineCall(memberRoutine: variant);
                member.ResolvedType = variant.ReturnType;
                return member;
            }

            case IdentifierExpression id:
            {
                var free = call with
                {
                    Callee = id with { ResolvedRoutine = variant },
                    IsFailable = false,
                    IsPreAnalyzed = true
                };
                free.ResolvedRoutine = variant;
                free.LoweringKind = ClassifyStandaloneRoutineCall(routine: variant);
                free.ResolvedType = variant.ReturnType;
                return free;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// The plain call an analyzed explicit-type-argument call (<c>f[T](x)</c>, <c>o.m[T](x)</c>) is bound to,
    /// with the same binding, arguments and type. Any other expression, a construction (<c>List[T]()</c>), or
    /// an unbound call is returned unchanged.
    /// </summary>
    private static Expression AsPlainCall(Expression expr)
    {
        // A construction with explicit type arguments (`List[S64](from: v)`) is the same creator call a
        // construction without them is (`Integer(text: t)`): the type it builds, called with its arguments.
        if (expr is GenericMemberRoutineCallExpression
            {
                ResolvedRoutine: { } creator, ConstructedType: { } constructed,
                Object: IdentifierExpression typeName
            } construction)
        {
            return new CallExpression(Callee: typeName with { ResolvedRoutine = creator },
                Arguments: construction.Arguments,
                Location: construction.Location)
            {
                ResolvedRoutine = creator,
                ResolvedType = construction.ResolvedType,
                ConstructedType = constructed,
                LoweringKind = construction.LoweringKind,
                TypeArguments = construction.TypeArguments
            };
        }

        if (expr is not GenericMemberRoutineCallExpression
            {
                ResolvedRoutine: { } routine, ConstructedType: null
            } generic)
        {
            return expr;
        }

        Expression callee = routine.OwnerType == null &&
                            generic.Object is IdentifierExpression id && id.Name == generic.MemberRoutineName
            ? id with { ResolvedRoutine = routine }
            : new MemberExpression(Object: generic.Object,
                MemberName: generic.MemberRoutineName,
                Location: generic.Location) { ResolvedType = generic.ResolvedType };
        return new CallExpression(Callee: callee, Arguments: generic.Arguments, Location: generic.Location)
        {
            ResolvedRoutine = routine,
            ResolvedType = generic.ResolvedType,
            LoweringKind = generic.LoweringKind,
            TypeArguments = generic.TypeArguments
        };
    }

    /// <summary>The surface keyword for a <see cref="RecoveryKind"/>, for diagnostics.</summary>
    private static string RecoveryKeyword(RecoveryKind kind) => kind switch
    {
        RecoveryKind.Grab => "grab",
        RecoveryKind.Lookup => "lookup",
        _ => "try"
    };

    /// <summary>
    /// An <see cref="AstRewriter"/> that rewrites an expression into A-normal form for recovery composition:
    /// every failable <see cref="CallExpression"/> (post-order = evaluation order) is hoisted into a fresh
    /// <c>var __rc_tN = &lt;call&gt;</c> declaration (collected in <see cref="Hoisted"/>) and replaced in the
    /// tree by a reference to that temp. All identifier names encountered are recorded in
    /// <see cref="ReferencedNames"/> so the caller can capture the outer locals the body reads as parameters.
    /// </summary>
    private sealed class RecoveryFailableHoister(int seed, TypeRegistry registry) : AstRewriter
    {
        private int _seq = seed;

        /// <summary>The hoisted `var __rc_tN = &lt;failable call&gt;` declarations, in evaluation order.</summary>
        public List<Statement> Hoisted { get; } = [];

        /// <summary>Every identifier name seen while walking the expression.</summary>
        public HashSet<string> ReferencedNames { get; } = new(comparer: StringComparer.Ordinal);

        public override Expression VisitExpression(Expression expr)
        {
            if (expr is IdentifierExpression id)
            {
                ReferencedNames.Add(item: id.Name);
            }

            return base.VisitExpression(expr: expr);
        }

        protected override Expression VisitCall(CallExpression e)
        {
            // Rewrite children first (post-order) so any inner failable calls are already hoisted + replaced.
            Expression rewritten = base.VisitCall(e: e);
            // A sub-call is failable when its resolved routine is failable. IsFailable is only DERIVED from
            // HasThrow/HasAbsent by a later pass, so during Phase-5 an INFERRED-failable callee (a body that
            // `throw`/`absent`s with no explicit `!`, already analyzed before this call site) carries
            // HasThrow/HasAbsent but not yet IsFailable: CanFailUnderRecovery checks all three, and also takes
            // a user routine that is not failable but can fail beneath its call (a checked operator or a bare
            // failable call in its body), which is recovered through its own variant.
            // A call through a routine value is hoisted too: the lambda may fail, and the variant built from the
            // hoisted body calls the value's recovering entry (RoutineValueCalls).
            if (rewritten is CallExpression valueCall && RoutineValueCalls.ValueType(call: valueCall) != null)
            {
                return HoistFailable(call: valueCall);
            }

            if (rewritten is not CallExpression { ResolvedRoutine: { } rr } call ||
                !registry.CanFailUnderRecovery(routine: rr))
            {
                return rewritten;
            }

            return HoistFailable(call: call);
        }

        /// <summary>
        /// A checked-arithmetic operator (<c>+ - * / // % **</c>) dispatches to a failable member routine
        /// (<c>add</c>/<c>sub</c>/… throw on overflow / divide-by-zero), so `try a + b` must recover the
        /// overflow just like `try a.add(b)` would. The operator→member-call lowering normally happens at
        /// Phase 6 (OperatorLoweringPass), AFTER this Phase-5 hoist, so the same failable member call is
        /// built here from the already-analyzed operands (<see cref="FailurePointCalls"/>) and hoisted.
        /// An operator whose routine cannot fail (wrapping, float, text concatenation, comparison) is left
        /// as a plain <see cref="BinaryExpression"/> for the normal Phase-6 path.
        /// </summary>
        protected override Expression VisitBinary(BinaryExpression e)
        {
            // Rewrite operands first (post-order = left→right eval order) so any inner failable calls hoist.
            Expression rewritten = base.VisitBinary(e: e);
            return rewritten is BinaryExpression bin && FailurePointCalls.ForBinary(bin: bin, registry: registry) is
                { } call
                ? HoistFailable(call: call)
                : rewritten;
        }

        /// <summary>A checked negation (<c>-x</c> of the type's minimum) is a failable call, as above.</summary>
        protected override Expression VisitUnary(UnaryExpression e)
        {
            Expression rewritten = base.VisitUnary(e: e);
            return rewritten is UnaryExpression unary &&
                   FailurePointCalls.ForUnary(unary: unary, registry: registry) is { } call
                ? HoistFailable(call: call)
                : rewritten;
        }

        /// <summary>An element read (<c>xs[i]</c>) is a failable <c>getitem</c> call, as above.</summary>
        protected override Expression VisitIndex(IndexExpression e)
        {
            Expression rewritten = base.VisitIndex(e: e);
            return rewritten is IndexExpression idx && FailurePointCalls.ForIndex(idx: idx) is { } call
                ? HoistFailable(call: call)
                : rewritten;
        }

        /// <summary>Hoists a resolved failable call into a fresh <c>var __rc_tN = call</c> and returns a
        /// reference to the temp (carrying the call's result type).</summary>
        private Expression HoistFailable(CallExpression call)
        {
            string tempName = $"{RecoveryTempPrefix}{_seq++}";
            var decl = new VariableDeclaration(Name: tempName,
                Type: null,
                Initializer: call,
                Visibility: VisibilityModifier.Secret,
                Location: call.Location);
            Hoisted.Add(item: new DeclarationStatement(Declaration: decl, Location: call.Location));

            return new IdentifierExpression(Name: tempName, Location: call.Location)
            {
                ResolvedType = call.ResolvedType
            };
        }
    }
}
