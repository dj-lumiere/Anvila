using Builder.Declaration;
using Builder.Diagnostics;
using Builder.Instantiation;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Phase 5: whole-expression <c>try</c>/<c>check</c> recovery composition.
///
/// A recovery keyword wraps an ENTIRE expression and short-circuits to its carrier on the FIRST failure
/// (evaluation order: inner→outer, left→right). Rather than lower this inline (which, lacking a non-local
/// break, would nest a <c>when</c> per hop), we synthesize a failable BASE routine
/// <c>__recover_N!(freevars) -&gt; T</c> whose body is the inner expression in A-normal form (each failable
/// sub-call hoisted to its own <c>var</c>, in evaluation order), then take that base's
/// <c>try</c>/<c>check</c> variant. The variant's <see cref="Builder.Instantiation.ErrorHandlingVariantPass.TransformBody"/>
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

    /// <summary>The parameter a composition base body reads the enclosing routine's <c>me</c> through.</summary>
    private const string RecoveryMeAlias = "__rc_me";

    /// <summary>
    /// Analyzes a <c>try</c>/<c>check</c> <see cref="RecoveryExpression"/> as a whole-expression
    /// composition (see the class remarks). Returns the carrier type (Optional/Check[T], None-collapsed);
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

    /// <summary>
    /// Reports a <c>try</c>/<c>check</c> written over an expression that reaches no failable call (RF-S760). The
    /// diagnostic points at the call when the expression is one, and names it.
    /// </summary>
    private void ReportNothingToRecover(RecoveryExpression recovery)
    {
        string keyword = RecoveryKeyword(kind: recovery.Kind);
        Expression inner = recovery.Inner;
        string? routineName = inner switch
        {
            CallExpression { ResolvedRoutine: { } routine } => routine.Name,
            GenericMemberRoutineCallExpression { ResolvedRoutine: { } generic } => generic.Name,
            _ => null
        };
        string what = routineName != null
            ? $"'{routineName}' is not failable, so nothing under '{keyword}' can fail"
            : $"nothing under '{keyword}' can fail: the expression calls no failable routine";
        string fix = routineName != null
            ? $"Remove '{keyword}', or declare '{routineName}' as 'failable routine {routineName}(...)' if it can fail."
            : $"Remove '{keyword}', or call a failable routine under it.";
        ReportError(code: SemanticDiagnosticCode.NothingToRecover,
            message: $"You wrote '{keyword}', but {what}. A failure inside a routine that is not failable crashes " +
                     $"there, and '{keyword}' only receives failures that rise through failable routines. {fix}",
            location: inner.Location,
            fix: new SuggestedFix(Title: $"Remove '{keyword}'",
                Edits:
                [
                    new TextEdit(StartLine: recovery.Location.Line, StartColumn: recovery.Location.Column,
                        EndLine: inner.Location.Line, EndColumn: inner.Location.Column, NewText: "")
                ]));
    }

    /// <summary>The payload type a <c>try</c> (<c>Optional[T]</c>) or <c>check</c> (<c>Check[T]</c>) carrier holds, or
    /// null when <paramref name="carrier"/> is not that keyword's carrier.</summary>
    private static TypeSymbol? CarrierPayload(TypeSymbol? carrier, RecoveryKind kind)
    {
        string expectedBase = kind == RecoveryKind.Try ? "Optional" : "Check";
        return carrier is RecordTypeSymbol { GenericDefinition: { } definition, TypeArguments: [var payload] } &&
               definition.Name == expectedBase
            ? payload
            : null;
    }

    private TypeSymbol AnalyzeRecoveryExpression(RecoveryExpression recovery, TypeSymbol? expectedType = null)
    {
        // The names the expression itself steals (`check produce(out: steal feeder)`): the composition base takes them
        // as they were before the steal, and the steal inside the base hands them on.
        var deadBefore = new HashSet<string>(collection: _deadrefVariables, comparer: StringComparer.Ordinal);

        // Analyze the inner expression first so every sub-call carries its ResolvedRoutine and ResolvedType.
        // The decomposition below keys failable-ness off the ResolvedRoutine IsFailable flag. The inner expression
        // expects the carrier's payload: `var n: S64? = try count.to()` converts to `S64`.
        TypeSymbol innerType = AnalyzeExpression(expression: recovery.Inner,
            expectedType: CarrierPayload(carrier: expectedType, kind: recovery.Kind));
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

        // A keyword over an expression that reaches no failable call has nothing to receive.
        if (hoister.Hoisted.Count == 0 && !hoister.ReachesFailableValue && !recovery.IsSynthesized &&
            !ContainsUnresolvedTypeParameter(type: innerType))
        {
            ReportNothingToRecover(recovery: recovery);
            return ErrorTypeSymbol.Instance;
        }

        // SINGLE failable call (the overwhelmingly common case): bind its recovery variant DIRECTLY by
        // reference (TypeRegistry.LookupRecoveryVariant) — the variant is the base routine's, never found by
        // a name. The base call already resolved concretely (a concrete receiver → the concrete member
        // routine, or the failable free reader for a conversion), so the variant comes substituted for that
        // same owner: concrete carrier `Optional[S64]` (not the generic-def `Optional[T]`), receiver stays in place
        // so an entity is BORROWED as the base call borrowed it (no synth-routine by-value capture → no
        // spurious RF-S413). Routine synthesis (below) is the fallback ONLY for a MULTI-call short-circuit, or
        // a shape the base has no variant for (check over an absent-only base — the routine path shapes that).
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
            // (`Optional[Me/Item]` for `try it.emit()`): `Me` is that receiver.
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

            if (name == RecoveryMeAlias && hoister.MeType is { } meType)
            {
                freeParams.Add(item: new ParamInfo(name: name, type: meType));
                continue;
            }

            VariableInfo? local = _registry.LookupVariable(name: name);
            if (local != null)
            {
                // A parameter is a slot like any written one: in Suflae an entity local is passed as its
                // Roamed handle (the local itself is retyped to it later), so the slot takes that type too.
                // An Unbroken is passed by its address, as every routine taking one gets it (SignatureResolver):
                // a copy would be a second cell, and what the recovered call adds would never reach the caller's.
                TypeSymbol slot = _typeResolver.RoamInferredSlot(inferred: local.Type);
                freeParams.Add(item: new ParamInfo(name: name, type: slot)
                {
                    IsByReference = slot is RecordTypeSymbol { GenericDefinition.Name: RuntimeContract.Unbroken }
                });
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
        //  - try    → try    (Optional): pessimistic (throw+absent) generates try.
        //  - lookup → lookup (Lookup): pessimistic (throw+absent) generates lookup.
        //  - check   → check  (Check): check collapses the WHOLE crashable set (and, per the design, promotes
        //    a sub-call's absent to AbsentValueError) into Check's single Crashable arm. The variant rules
        //    only mint check for a THROW-ONLY shape, so mark check's base throw-only (HasThrow,
        //    non-pessimistic) — otherwise the both-shape yields lookup and no check exists.
        bool check = recovery.Kind == RecoveryKind.Check;
        string baseName = $"__recover_{_recoveryCompositionSeq++}";
        // In a generic routine the captured values and the result may name its type parameters (`T` of
        // `List[Work[T]].gather_all`): the base is generic over the ones they name, and the call passes them, so each
        // instance of the enclosing routine reaches its own instance of the base (and of its variant).
        List<string> typeParameters = RecoveryTypeParameters(types: freeParams.Select(selector: p => p.Type)
                                                                              .Append(element: innerType));
        var baseRoutine = new RoutineInfo(name: baseName)
        {
            Kind = RoutineKind.FreeRoutine,
            Parameters = freeParams,
            ReturnType = innerType,
            GenericParameters = typeParameters.Count > 0 ? typeParameters : null,
            GenericConstraints = typeParameters.Count > 0 ? RecoveryConstraints(names: typeParameters) : null,
            IsFailable = true,
            IsSynthesized = true,
            HasThrow = check,
            // Open: this is builder-internal, and its variant inherits this visibility. Secret would trip
            // the cross-module access check (RF-S403) at the call site.
            Visibility = VisibilityModifier.Open,
            Location = recovery.Location,
            Module = _currentRoutine?.Module,
            ModulePath = _currentRoutine?.ModulePath
        };
        _registry.RegisterRoutine(routine: baseRoutine);

        // Index the base for on-demand variant synthesis. try/lookup use the pessimistic (throw+absent)
        // shape; check uses the throw-only shape stamped above (so check is generated).
        _registry.DeferredVariantBases[key: baseRoutine.RegistryKey] = (baseRoutine, baseBody, !check);

        RoutineInfo? variant = _registry.LookupRecoveryVariant(recovered: baseRoutine, kind: recovery.Kind);
        if (variant == null)
        {
            // The requested carrier variant is not (yet) synthesizable for this base shape (e.g. `check`'s
            // check before the Check/Lookup propagation generalization). Surface as a generation error
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
                                     var value = new IdentifierExpression(
                                         Name: p.Name == RecoveryMeAlias ? "me" : p.Name,
                                         Location: recovery.Location);
                                     if (_deadrefVariables.Contains(item: p.Name) && !deadBefore.Contains(item: p.Name) &&
                                         _registry.LookupVariable(name: p.Name) is { } stolen)
                                     {
                                         value.ResolvedType = stolen.Type;
                                     }
                                     else
                                     {
                                         AnalyzeExpression(expression: value);
                                     }
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
            IsPreAnalyzed = true,
            // The enclosing routine's own type parameters, which its instances replace with their types.
            TypeArguments = typeParameters.Count > 0
                ? typeParameters.Select(selector: n => new TypeExpression(Name: n, GenericArguments: null,
                                         Location: recovery.Location))
                                .ToList()
                : null
        };
        recovery.LoweredCall = loweredCall;
        return variant.ReturnType ?? ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// The type parameters (of the routine being analyzed, or of its owner type) that <paramref name="types"/> name, in
    /// the order they are first met: what a composition base in a generic routine is generic over.
    /// </summary>
    private static List<string> RecoveryTypeParameters(IEnumerable<TypeSymbol> types)
    {
        var names = new List<string>();

        void Visit(TypeSymbol type)
        {
            if (type is GenericParameterTypeSymbol parameter)
            {
                if (!names.Contains(item: parameter.Name))
                {
                    names.Add(item: parameter.Name);
                }

                return;
            }

            foreach (TypeSymbol argument in type.TypeArguments ?? [])
            {
                Visit(type: argument);
            }
        }

        foreach (TypeSymbol type in types)
        {
            Visit(type: type);
        }

        return names;
    }

    /// <summary>The constraints of the routine being analyzed (and of its owner type) on <paramref name="names"/>: the
    /// composition base calls what the routine's body calls on them.</summary>
    private List<GenericConstraintDeclaration>? RecoveryConstraints(List<string> names)
    {
        IEnumerable<GenericConstraintDeclaration> own = _currentRoutine?.GenericConstraints ?? [];
        TypeSymbol? ownerType = _currentRoutine?.OwnerType;
        IEnumerable<GenericConstraintDeclaration> owner = ownerType switch
        {
            RecordTypeSymbol { GenericDefinition: { } def } => def.GenericConstraints ?? [],
            EntityTypeSymbol { GenericDefinition: { } def } => def.GenericConstraints ?? [],
            _ => ownerType?.GenericConstraints ?? []
        };
        List<GenericConstraintDeclaration> kept = own.Concat(second: owner)
                                                     .Where(predicate: c => names.Contains(item: c.ParameterName))
                                                     .ToList();
        return kept.Count > 0 ? kept : null;
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
        // stamp the variant + carrier return (the variant's ReturnType is Optional/Check[T]).
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
        // A construction by named arguments bound to a written creator (`List[S64](capacity: n)`) is the call of
        // that creator, as ConstructionLoweringPass makes it later.
        if (expr is CreatorExpression { ResolvedCreatorRoutine: { } bound } written)
        {
            return new CallExpression(
                Callee: new IdentifierExpression(Name: written.TypeName, Location: written.Location)
                {
                    ResolvedRoutine = bound
                },
                Arguments: written.MemberVariables
                                  .Select(selector: mv => (Expression)new NamedArgumentExpression(Name: mv.Name,
                                       Value: mv.Value,
                                       Location: mv.Value.Location) { ResolvedType = mv.Value.ResolvedType })
                                  .ToList(),
                Location: written.Location)
            {
                ResolvedRoutine = bound,
                ResolvedType = written.ResolvedType,
                ConstructedType = written.ConstructedType,
                LoweringKind = written.LoweringKind,
                TypeArguments = written.TypeArguments
            };
        }

        // A construction with explicit type arguments (`List[S64](from: v)`) is the same creator call a
        // construction without them is (`Int(text: t)`): the type it builds, called with its arguments.
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
                            generic.IsFree && generic.Object is IdentifierExpression id
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
        RecoveryKind.Check => "check",
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

        /// <summary>Whether the expression hands a routine value that may fail to a routine whose recovery variant is
        /// found only once its body is known: the keyword has a failure to receive.</summary>
        public bool ReachesFailableValue { get; private set; }

        /// <summary>The type of the enclosing routine's <c>me</c> when the expression reads it, else null.</summary>
        public TypeSymbol? MeType { get; private set; }

        public override Expression VisitExpression(Expression expr)
        {
            // `me` is the enclosing member routine's receiver, not a local: the synthesized base routine is a free
            // routine, so it takes the receiver as an ordinary parameter under another name.
            if (expr is IdentifierExpression { Name: "me" } me)
            {
                MeType = me.ResolvedType;
                ReferencedNames.Add(item: RecoveryMeAlias);
                return me with { Name = RecoveryMeAlias };
            }

            if (expr is IdentifierExpression id)
            {
                ReferencedNames.Add(item: id.Name);
            }

            // A bare number keeps the type analysis gave it here (`8` in `try d / 8` beside a Decimal): the body
            // built from the expression is analyzed again on its own, where the number would fall back to its
            // default type and pick another overload than the one already bound.
            if (expr is LiteralExpression literal &&
                UndecidedLiteralConformance.HasNumberToken(type: literal.ResolvedType))
            {
                return UndecidedLiteralConformance.Conform(literal: literal);
            }

            return base.VisitExpression(expr: expr);
        }

        protected override Expression VisitCall(CallExpression e)
        {
            // Rewrite children first (post-order) so any inner failable calls are already hoisted + replaced.
            Expression rewritten = base.VisitCall(e: e);
            // A sub-call is failable when its resolved routine is failable. A routine that is not failable is a
            // wall: its call is hoisted only when it hands on or works on a routine value that may fail, whose
            // failure it passes on (FailableValues.CallMayFail). A call through such a routine value is hoisted
            // too, and the variant built from the hoisted body calls the value's recovering entry
            // (RoutineValueCalls).
            if (rewritten is not CallExpression call || !FailableValues.CallMayFail(call: call, registry: registry))
            {
                return rewritten;
            }

            // A routine that is not failable whose library body is not known yet (analysis of the user's code
            // comes first) passes on the failure of the routine value it is handed: the variant built later finds
            // it there (a tail call of the composition), so the call stays where it is.
            if (call.ResolvedRoutine is { IsFailable: false, HasThrow: false } passing &&
                !registry.CanFailUnderRecovery(routine: passing))
            {
                ReachesFailableValue = true;
                return rewritten;
            }

            return HoistFailable(call: call);
        }

        /// <summary>A construction bound to a failable creator is a failable call, hoisted as one.</summary>
        protected override Expression VisitCreator(CreatorExpression e)
        {
            Expression rewritten = base.VisitCreator(e: e);
            return rewritten is CreatorExpression { ResolvedCreatorRoutine: { } bound } creator &&
                   (bound.IsFailable || bound.HasThrow) && AsPlainCall(expr: creator) is CallExpression call
                ? HoistFailable(call: call)
                : rewritten;
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
            return rewritten is IndexExpression idx && FailurePointCalls.ForIndex(idx: idx, registry: registry) is { } call
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
