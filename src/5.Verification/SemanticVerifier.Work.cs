using Builder.Desugaring.Passes;
using Builder.Diagnostics;
using Builder.Instantiation;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Phase 5: <c>f.to[Work](...)</c>. The call chain is analyzed where it is written, so its arguments are typed against
/// the parameters they go to and its calls are resolved as any call. Then three routines are written for it:
/// <list type="bullet">
/// <item>the work routine <c>__work_N_work(p0, p1, …) -&gt; R</c>, the chain over parameters that stand for its
/// arguments (and for a receiver that is a value, as in <c>list.sum.to[Work]()</c>),</item>
/// <item>the spawn and run routines <see cref="WorkRecipe"/> builds over it.</item>
/// </list>
/// The <see cref="WorkExpression"/>'s <see cref="WorkExpression.LoweredCall"/> becomes a call of the spawn routine
/// with the analyzed arguments, so they are computed now, and the calls run wherever the verb that starts the Work
/// puts them. The three routines are collected and their signatures resolved at once (the call binds to the spawn
/// routine), and their bodies are analyzed after the file's own bodies (<see cref="AnalyzeWorkRoutines"/>).
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>Numbers the routines written for <c>to[Work]</c> conversions, unique per build.</summary>
    private int _agentSeq;

    /// <summary>The routines written for <c>to[Work]</c> conversions whose bodies are not analyzed yet, each with the module
    /// of the routine its call is written in.</summary>
    private readonly List<(RoutineDeclaration Routine, string? Module)> _pendingWorkRoutines = [];

    private TypeSymbol AnalyzeWorkExpression(WorkExpression agent)
    {
        // A clean copy before analysis annotates the chain: it becomes the work routine's body.
        Expression pristine = GenericAstRewriter.DeepCloneExpression(expr: agent.Inner);
        TypeSymbol resultType = AnalyzeExpression(expression: agent.Inner);
        if (resultType is ErrorTypeSymbol)
        {
            return ErrorTypeSymbol.Instance;
        }

        var hoisted = new List<(string Name, Expression Value)>();
        Expression? deferred = DeferWorkChain(analyzed: agent.Inner, pristine: pristine, hoisted: hoisted);
        if (deferred == null)
        {
            ReportError(code: SemanticDiagnosticCode.WorkNotSupportedHere,
                message: "This Work is made of something that is not a routine. 'to[Work]' converts a routine with " +
                         "its arguments, as in 'download.to[Work](url: u)', into a Work you start with start_now().",
                location: agent.Location);
            return ErrorTypeSymbol.Instance;
        }

        var arguments = new List<(Expression Value, TypeSymbol Type)>();
        foreach ((string _, Expression value) in hoisted)
        {
            if (value.ResolvedType is not { } type || type is ErrorTypeSymbol)
            {
                return ErrorTypeSymbol.Instance;
            }

            arguments.Add(item: (value, type));
        }

        if (ContainsUnresolvedTypeParameter(type: resultType) ||
            arguments.Any(predicate: a => ContainsUnresolvedTypeParameter(type: a.Type)))
        {
            ReportError(code: SemanticDiagnosticCode.WorkNotSupportedHere,
                message: "This 'to[Work]' works on values whose type is a parameter of this generic routine, and an " +
                         "Work runs one concrete routine. Make the Work in a routine that is not generic, and " +
                         "pass it the values.",
                location: agent.Location);
            return ErrorTypeSymbol.Instance;
        }

        agent.OneThreadArgument = ValidateWorkArguments(arguments: arguments);

        // The routines written for this call.
        SourceLocation loc = agent.Location;
        string baseName = $"__work_{_agentSeq++}";
        var parameters = new List<Parameter>();
        for (int i = 0; i < hoisted.Count; i++)
        {
            TypeSymbol slot = _typeResolver.RoamInferredSlot(inferred: arguments[index: i].Type);
            parameters.Add(item: new Parameter(Name: hoisted[index: i].Name,
                Type: Known(type: slot, loc: loc),
                DefaultValue: null,
                Location: loc)
            {
                // An Unbroken is shared with the work, not copied: every holder works on the caller's one cell.
                IsByReference = slot.BareName == Declaration.RuntimeContract.Unbroken
            });
        }

        // The work returns what the chain came to: `check` keeps its failure, which surfaces where the Work is
        // retrieved instead of ending the program where the work runs.
        var work = new RoutineDeclaration(Name: baseName + "_work",
            Parameters: parameters,
            ReturnType: new TypeExpression(Name: "Check", GenericArguments: [Known(type: resultType, loc: loc)],
                Location: loc),
            Body: new BlockStatement(Statements:
                [
                    new ReturnStatement(Value: new RecoveryExpression(Kind: RecoveryKind.Check, Inner: deferred,
                        Location: loc) { IsSynthesized = true }, Location: loc)
                ], Location: loc),
            Visibility: VisibilityModifier.Secret,
            Annotations: [],
            Location: loc)
        {
            IsBuilderWritten = true,
            // A crash in the work reads as the Work it ran in, not the name the builder gave it: the frame's
            // position is the job call.
            TraceName = _currentRoutine != null
                ? $"work in {Builder.Collection.TraceFrames.Name(routine: _currentRoutine)}"
                : "work"
        };
        string spawnName = baseName + "_spawn";
        TypeExpression agentResult = Known(type: resultType, loc: loc);
        RoutineDeclaration spawn = WorkRecipe.BuildSpawn(routine: work, spawnName: spawnName,
            runName: baseName + "_run", resultType: agentResult);
        RoutineDeclaration run = WorkRecipe.BuildRun(routine: work, runName: baseName + "_run",
            resultType: agentResult);

        // Desugar them as every routine is before analysis, then register their signatures now: the call binds
        // to the spawn routine.
        // They belong to the module of the routine the call is written in, which sees the routines the chain calls.
        var written = new Program(Declarations: [work, spawn, run], Location: loc);
        RunPhase4SyntaxPrepass(program: written);
        string? module = _currentRoutine?.Module ?? GetCurrentModuleName();
        string? previousModule = _currentModuleName;
        _currentModuleName = module;
        foreach (RoutineDeclaration routine in written.Declarations.OfType<RoutineDeclaration>())
        {
            CollectRoutineDeclaration(routine: routine);
        }

        _signatureResolver.ResolveAndRegisterPendingRoutines();
        _currentModuleName = previousModule;
        _pendingWorkRoutines.AddRange(
            collection: written.Declarations.OfType<RoutineDeclaration>().Select(selector: r => (r, module)));

        RoutineInfo? workInfo = written.Declarations.OfType<RoutineDeclaration>()
                                       .First(predicate: r => r.Name == work.Name)
                                       .ResolvedInfo;
        RoutineInfo? spawnInfo = written.Declarations.OfType<RoutineDeclaration>()
                                        .First(predicate: r => r.Name == spawnName)
                                        .ResolvedInfo;
        if (workInfo == null || spawnInfo == null)
        {
            return ErrorTypeSymbol.Instance;
        }

        // Promotes a Roamed argument before the Work is built (RoamedSpawnPromotionLoweringPass).
        spawnInfo.AsyncSpawnOf = workInfo;

        List<Expression> callArguments = hoisted
                                         .Select(selector: h => (Expression)new NamedArgumentExpression(
                                              Name: h.Name,
                                              Value: h.Value,
                                              Location: h.Value.Location) { ResolvedType = h.Value.ResolvedType })
                                         .ToList();
        agent.LoweredCall = new CallExpression(
            Callee: new IdentifierExpression(Name: spawnName, Location: loc) { ResolvedRoutine = spawnInfo },
            Arguments: callArguments,
            Location: loc)
        {
            ResolvedRoutine = spawnInfo,
            ResolvedType = spawnInfo.ReturnType,
            LoweringKind = ClassifyStandaloneRoutineCall(routine: spawnInfo),
            IsPreAnalyzed = true,
            WorkOneThreadArgument = agent.OneThreadArgument
        };
        return spawnInfo.ReturnType ?? ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Analyzes the bodies of the routines written for this program's <c>to[Work]</c> conversions, and adds them to the
    /// program so every later pass lowers and emits them like the program's own. A work routine may itself make
    /// <c>to[Work]</c> conversions, so this runs until no more are written.
    /// </summary>
    private void AnalyzeWorkRoutines(Program program)
    {
        if (_pendingWorkRoutines.Count == 0)
        {
            return;
        }

        while (_pendingWorkRoutines.Count > 0)
        {
            List<(RoutineDeclaration Routine, string? Module)> batch = [.._pendingWorkRoutines];
            _pendingWorkRoutines.Clear();
            program.Declarations.AddRange(collection: batch.Select(selector: b => b.Routine));
            foreach ((RoutineDeclaration routine, string? module) in batch)
            {
                string? previousModule = _currentModuleName;
                _currentModuleName = module;
                AnalyzeDeclaration(node: routine);
                _currentModuleName = previousModule;
            }
        }

        SpliceWorkCalls(program: program);
    }

    /// <summary>
    /// Puts each analyzed <c>to[Work]</c> expression's lowering in its place in every routine body of the program, so
    /// no later pass meets a <see cref="WorkExpression"/>. The statements of each body block are replaced in
    /// place: the block itself stays the one the registry may have stored.
    /// </summary>
    private static void SpliceWorkCalls(Program program)
    {
        var splicer = new WorkCallSplicer();
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            IEnumerable<RoutineDeclaration> routines = node switch
            {
                RoutineDeclaration routine => [routine],
                EntityDeclaration entity => entity.Members.OfType<RoutineDeclaration>(),
                RecordDeclaration record => record.Members.OfType<RoutineDeclaration>(),
                CrashableDeclaration crashable => crashable.Members.OfType<RoutineDeclaration>(),
                ChoiceDeclaration choice => choice.MemberRoutines,
                _ => []
            };
            foreach (RoutineDeclaration routine in routines)
            {
                if (routine.Body is not BlockStatement block)
                {
                    continue;
                }

                for (int i = 0; i < block.Statements.Count; i++)
                {
                    block.Statements[index: i] = splicer.VisitStatement(stmt: block.Statements[index: i]);
                }
            }
        }
    }

    /// <summary>Replaces each analyzed job expression with its lowering (the base <see cref="AstRewriter"/> does),
    /// and leaves a recovery as it is, splicing only inside it: the recovery is spliced later, by its own pass.</summary>
    private sealed class WorkCallSplicer : AstRewriter
    {
        protected override Expression VisitRecovery(RecoveryExpression e)
        {
            if (e.LoweredCall is { } lowered)
            {
                e.LoweredCall = VisitExpression(expr: lowered);
            }

            Expression inner = VisitExpression(expr: e.Inner);
            return ReferenceEquals(objA: inner, objB: e.Inner)
                ? e
                : e with { Inner = inner };
        }
    }

    /// <summary>
    /// The deferred copy of a <c>to[Work]</c> conversion chain, walked beside the analyzed chain: each argument (and a
    /// receiver that is a value) becomes a parameter of the work routine, recorded in <paramref name="hoisted"/>
    /// with its analyzed expression, which is computed now. Literals stay in the chain: they are the same later,
    /// and keep the type their parameter gives them. Null when the chain is not a call.
    /// </summary>
    private Expression? DeferWorkChain(Expression analyzed, Expression pristine,
        List<(string Name, Expression Value)> hoisted)
    {
        switch (analyzed, pristine)
        {
            case (RecoveryExpression analyzedRecovery, RecoveryExpression pristineRecovery):
            {
                Expression? inner = DeferWorkChain(analyzed: analyzedRecovery.Inner, pristine: pristineRecovery.Inner,
                    hoisted: hoisted);
                return inner == null
                    ? null
                    : new RecoveryExpression(Kind: pristineRecovery.Kind, Inner: inner,
                        Location: pristineRecovery.Location) { IsSynthesized = pristineRecovery.IsSynthesized };
            }

            case (CallExpression analyzedCall, CallExpression pristineCall):
            {
                Expression? callee = (analyzedCall.Callee, pristineCall.Callee) switch
                {
                    (MemberExpression analyzedMember, MemberExpression pristineMember) => pristineMember with
                    {
                        Object = DeferWorkReceiver(analyzed: analyzedMember.Object, pristine: pristineMember.Object,
                            hoisted: hoisted)
                    },
                    (_, IdentifierExpression or GenericMemberExpression) => pristineCall.Callee,
                    _ => null
                };
                return callee == null
                    ? null
                    : pristineCall with
                    {
                        Callee = callee,
                        Arguments = DeferWorkArguments(analyzed: analyzedCall.Arguments,
                            pristine: pristineCall.Arguments, hoisted: hoisted)
                    };
            }

            case (GenericMemberRoutineCallExpression analyzedGeneric, GenericMemberRoutineCallExpression pristineGeneric):
                return pristineGeneric with
                {
                    Object = DeferWorkReceiver(analyzed: analyzedGeneric.Object, pristine: pristineGeneric.Object,
                        hoisted: hoisted),
                    Arguments = DeferWorkArguments(analyzed: analyzedGeneric.Arguments,
                        pristine: pristineGeneric.Arguments, hoisted: hoisted)
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// A receiver in a <c>to[Work]</c> conversion chain: a call is deferred with the chain, a name that is not a variable
    /// (a type, a module, a routine) stays, and any other value is computed now.
    /// </summary>
    private Expression DeferWorkReceiver(Expression analyzed, Expression pristine,
        List<(string Name, Expression Value)> hoisted)
    {
        if (analyzed is CallExpression or GenericMemberRoutineCallExpression &&
            DeferWorkChain(analyzed: analyzed, pristine: pristine, hoisted: hoisted) is { } deferred)
        {
            return deferred;
        }

        if (analyzed is IdentifierExpression id && id.Name != "me" &&
            _registry.LookupVariable(name: id.Name) == null)
        {
            return pristine;
        }

        return HoistWorkValue(analyzed: analyzed, pristine: pristine, hoisted: hoisted);
    }

    private List<Expression> DeferWorkArguments(List<Expression> analyzed, List<Expression> pristine,
        List<(string Name, Expression Value)> hoisted)
    {
        var deferred = new List<Expression>(capacity: pristine.Count);
        for (int i = 0; i < pristine.Count; i++)
        {
            deferred.Add(item: (analyzed[index: i], pristine[index: i]) switch
            {
                (NamedArgumentExpression analyzedNamed, NamedArgumentExpression pristineNamed) => pristineNamed with
                {
                    Value = HoistWorkValue(analyzed: analyzedNamed.Value, pristine: pristineNamed.Value,
                        hoisted: hoisted)
                },
                _ => HoistWorkValue(analyzed: analyzed[index: i], pristine: pristine[index: i], hoisted: hoisted)
            });
        }

        return deferred;
    }

    /// <summary>A value of a <c>to[Work]</c> conversion chain: computed now and passed in, or a literal kept as it is.</summary>
    private static Expression HoistWorkValue(Expression analyzed, Expression pristine,
        List<(string Name, Expression Value)> hoisted)
    {
        if (pristine is LiteralExpression ||
            pristine is UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression })
        {
            return pristine;
        }

        string name = $"__work_arg{hoisted.Count}";
        hoisted.Add(item: (name, analyzed));
        var parameter = new IdentifierExpression(Name: name, Location: pristine.Location);
        // A value moved into the job (`produce(out: steal tx)`) moves on from the work's parameter as well. So does
        // any entity: the argument as written was checked where it stands, and the computed value now belongs to the
        // work alone.
        // A new node, not a copy of the written one: the chain may be analyzed more than once (an argument of a call
        // whose overload is being chosen), and a copy would carry the type the earlier analysis stored on it, so the
        // work routine's analysis would skip the parameter inside it.
        if (pristine is StealExpression steal)
        {
            return new StealExpression(Operand: parameter, Location: steal.Location)
            {
                IsImplicitMove = steal.IsImplicitMove
            };
        }

        return analyzed.ResolvedType is EntityTypeSymbol
            ? new StealExpression(Operand: parameter, Location: pristine.Location)
            : parameter;
    }

    /// <summary>A type written by the builder for a type it already knows.</summary>
    private static TypeExpression Known(TypeSymbol type, SourceLocation loc)
    {
        return new TypeExpression(Name: type.Name, GenericArguments: null, Location: loc) { KnownType = type };
    }
}
