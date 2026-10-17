using Builder.Desugaring.Passes;
using Builder.Diagnostics;
using Builder.Instantiation;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Phase 5: <c>agent EXPR</c>. The call chain is analyzed where it is written, so its arguments are typed against
/// the parameters they go to and its calls are resolved as any call. Then three routines are written for it:
/// <list type="bullet">
/// <item>the work routine <c>__agent_N_work(p0, p1, …) -&gt; R</c>, the chain over parameters that stand for its
/// arguments (and for a receiver that is a value, as in <c>agent list.sum()</c>),</item>
/// <item>the spawn and run routines <see cref="AgentRecipe"/> builds over it.</item>
/// </list>
/// The <see cref="AgentExpression"/>'s <see cref="AgentExpression.LoweredCall"/> becomes a call of the spawn routine
/// with the analyzed arguments, so they are computed now, and the calls run wherever the verb that starts the Agent
/// puts them. The three routines are collected and their signatures resolved at once (the call binds to the spawn
/// routine), and their bodies are analyzed after the file's own bodies (<see cref="AnalyzeAgentRoutines"/>).
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>Numbers the routines written for <c>agent</c> calls, unique per build.</summary>
    private int _agentSeq;

    /// <summary>The routines written for <c>agent</c> calls whose bodies are not analyzed yet, each with the module
    /// of the routine its call is written in.</summary>
    private readonly List<(RoutineDeclaration Routine, string? Module)> _pendingAgentRoutines = [];

    private TypeSymbol AnalyzeAgentExpression(AgentExpression agent)
    {
        // A clean copy before analysis annotates the chain: it becomes the work routine's body.
        Expression pristine = GenericAstRewriter.DeepCloneExpression(expr: agent.Inner);
        TypeSymbol resultType = AnalyzeExpression(expression: agent.Inner);
        if (resultType is ErrorTypeSymbol)
        {
            return ErrorTypeSymbol.Instance;
        }

        var hoisted = new List<(string Name, Expression Value)>();
        Expression? deferred = DeferAgentChain(analyzed: agent.Inner, pristine: pristine, hoisted: hoisted);
        if (deferred == null)
        {
            ReportError(code: SemanticDiagnosticCode.AgentNotSupportedHere,
                message: "This Agent is made of something that is not a routine. 'to[Agent]' converts a routine with " +
                         "its arguments, as in 'download.to[Agent](url: u)', into an Agent you start with start_now().",
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
            ReportError(code: SemanticDiagnosticCode.AgentNotSupportedHere,
                message: "This 'to[Agent]' works on values whose type is a parameter of this generic routine, and an " +
                         "Agent runs one concrete routine. Make the Agent in a routine that is not generic, and " +
                         "pass it the values.",
                location: agent.Location);
            return ErrorTypeSymbol.Instance;
        }

        agent.OneThreadArgument = ValidateAgentArguments(arguments: arguments);

        // The routines written for this call.
        SourceLocation loc = agent.Location;
        string baseName = $"__agent_{_agentSeq++}";
        var parameters = new List<Parameter>();
        for (int i = 0; i < hoisted.Count; i++)
        {
            TypeSymbol slot = _typeResolver.RoamInferredSlot(inferred: arguments[index: i].Type);
            parameters.Add(item: new Parameter(Name: hoisted[index: i].Name,
                Type: Known(type: slot, loc: loc),
                DefaultValue: null,
                Location: loc)
            {
                // An Atomic is shared with the work, not copied: every holder works on the caller's one cell.
                IsByReference = slot.BareName == Declaration.RuntimeContract.Atomic
            });
        }

        // The work returns what the chain came to: `check` keeps its failure, which surfaces where the Agent is
        // retrieved instead of ending the program where the work runs.
        var work = new RoutineDeclaration(Name: baseName + "_work",
            Parameters: parameters,
            ReturnType: new TypeExpression(Name: "Check", GenericArguments: [Known(type: resultType, loc: loc)],
                Location: loc),
            Body: new BlockStatement(Statements:
                [
                    new ReturnStatement(Value: new RecoveryExpression(Kind: RecoveryKind.Check, Inner: deferred,
                        Location: loc), Location: loc)
                ], Location: loc),
            Visibility: VisibilityModifier.Secret,
            Annotations: [],
            Location: loc)
        {
            IsBuilderWritten = true,
            // A crash in the work reads as the Agent it ran in, not the name the builder gave it: the frame's
            // position is the agent call.
            TraceName = _currentRoutine != null
                ? $"agent in {Builder.Collection.TraceFrames.Name(routine: _currentRoutine)}"
                : "agent"
        };
        string spawnName = baseName + "_spawn";
        TypeExpression agentResult = Known(type: resultType, loc: loc);
        RoutineDeclaration spawn = AgentRecipe.BuildSpawn(routine: work, spawnName: spawnName,
            runName: baseName + "_run", resultType: agentResult);
        RoutineDeclaration run = AgentRecipe.BuildRun(routine: work, runName: baseName + "_run",
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
        _pendingAgentRoutines.AddRange(
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

        // Promotes a Roamed argument before the Agent is built (RoamedSpawnPromotionLoweringPass).
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
            AgentOneThreadArgument = agent.OneThreadArgument
        };
        return spawnInfo.ReturnType ?? ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Analyzes the bodies of the routines written for this program's <c>agent</c> calls, and adds them to the
    /// program so every later pass lowers and emits them like the program's own. A work routine may itself make
    /// <c>agent</c> calls, so this runs until no more are written.
    /// </summary>
    private void AnalyzeAgentRoutines(Program program)
    {
        if (_pendingAgentRoutines.Count == 0)
        {
            return;
        }

        while (_pendingAgentRoutines.Count > 0)
        {
            List<(RoutineDeclaration Routine, string? Module)> batch = [.._pendingAgentRoutines];
            _pendingAgentRoutines.Clear();
            program.Declarations.AddRange(collection: batch.Select(selector: b => b.Routine));
            foreach ((RoutineDeclaration routine, string? module) in batch)
            {
                string? previousModule = _currentModuleName;
                _currentModuleName = module;
                AnalyzeDeclaration(node: routine);
                _currentModuleName = previousModule;
            }
        }

        SpliceAgentCalls(program: program);
    }

    /// <summary>
    /// Puts each analyzed <c>agent</c> expression's lowering in its place in every routine body of the program, so
    /// no later pass meets an <see cref="AgentExpression"/>. The statements of each body block are replaced in
    /// place: the block itself stays the one the registry may have stored.
    /// </summary>
    private static void SpliceAgentCalls(Program program)
    {
        var splicer = new AgentCallSplicer();
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

    /// <summary>Replaces each analyzed agent expression with its lowering (the base <see cref="AstRewriter"/> does),
    /// and leaves a recovery as it is, splicing only inside it: the recovery is spliced later, by its own pass.</summary>
    private sealed class AgentCallSplicer : AstRewriter
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
    /// The deferred copy of an <c>agent</c> call chain, walked beside the analyzed chain: each argument (and a
    /// receiver that is a value) becomes a parameter of the work routine, recorded in <paramref name="hoisted"/>
    /// with its analyzed expression, which is computed now. Literals stay in the chain: they are the same later,
    /// and keep the type their parameter gives them. Null when the chain is not a call.
    /// </summary>
    private Expression? DeferAgentChain(Expression analyzed, Expression pristine,
        List<(string Name, Expression Value)> hoisted)
    {
        switch (analyzed, pristine)
        {
            case (RecoveryExpression analyzedRecovery, RecoveryExpression pristineRecovery):
            {
                Expression? inner = DeferAgentChain(analyzed: analyzedRecovery.Inner, pristine: pristineRecovery.Inner,
                    hoisted: hoisted);
                return inner == null
                    ? null
                    : new RecoveryExpression(Kind: pristineRecovery.Kind, Inner: inner,
                        Location: pristineRecovery.Location);
            }

            case (CallExpression analyzedCall, CallExpression pristineCall):
            {
                Expression? callee = (analyzedCall.Callee, pristineCall.Callee) switch
                {
                    (MemberExpression analyzedMember, MemberExpression pristineMember) => pristineMember with
                    {
                        Object = DeferAgentReceiver(analyzed: analyzedMember.Object, pristine: pristineMember.Object,
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
                        Arguments = DeferAgentArguments(analyzed: analyzedCall.Arguments,
                            pristine: pristineCall.Arguments, hoisted: hoisted)
                    };
            }

            case (GenericMemberRoutineCallExpression analyzedGeneric, GenericMemberRoutineCallExpression pristineGeneric):
                return pristineGeneric with
                {
                    Object = DeferAgentReceiver(analyzed: analyzedGeneric.Object, pristine: pristineGeneric.Object,
                        hoisted: hoisted),
                    Arguments = DeferAgentArguments(analyzed: analyzedGeneric.Arguments,
                        pristine: pristineGeneric.Arguments, hoisted: hoisted)
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// A receiver in an <c>agent</c> call chain: a call is deferred with the chain, a name that is not a variable
    /// (a type, a module, a routine) stays, and any other value is computed now.
    /// </summary>
    private Expression DeferAgentReceiver(Expression analyzed, Expression pristine,
        List<(string Name, Expression Value)> hoisted)
    {
        if (analyzed is CallExpression or GenericMemberRoutineCallExpression &&
            DeferAgentChain(analyzed: analyzed, pristine: pristine, hoisted: hoisted) is { } deferred)
        {
            return deferred;
        }

        if (analyzed is IdentifierExpression id && id.Name != "me" &&
            _registry.LookupVariable(name: id.Name) == null)
        {
            return pristine;
        }

        return HoistAgentValue(analyzed: analyzed, pristine: pristine, hoisted: hoisted);
    }

    private List<Expression> DeferAgentArguments(List<Expression> analyzed, List<Expression> pristine,
        List<(string Name, Expression Value)> hoisted)
    {
        var deferred = new List<Expression>(capacity: pristine.Count);
        for (int i = 0; i < pristine.Count; i++)
        {
            deferred.Add(item: (analyzed[index: i], pristine[index: i]) switch
            {
                (NamedArgumentExpression analyzedNamed, NamedArgumentExpression pristineNamed) => pristineNamed with
                {
                    Value = HoistAgentValue(analyzed: analyzedNamed.Value, pristine: pristineNamed.Value,
                        hoisted: hoisted)
                },
                _ => HoistAgentValue(analyzed: analyzed[index: i], pristine: pristine[index: i], hoisted: hoisted)
            });
        }

        return deferred;
    }

    /// <summary>A value of an <c>agent</c> call chain: computed now and passed in, or a literal kept as it is.</summary>
    private static Expression HoistAgentValue(Expression analyzed, Expression pristine,
        List<(string Name, Expression Value)> hoisted)
    {
        if (pristine is LiteralExpression ||
            pristine is UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression })
        {
            return pristine;
        }

        string name = $"__agent_arg{hoisted.Count}";
        hoisted.Add(item: (name, analyzed));
        var parameter = new IdentifierExpression(Name: name, Location: pristine.Location);
        // A value moved into the agent (`produce(out: steal tx)`) moves on from the work's parameter as well.
        return pristine is StealExpression steal
            ? steal with { Operand = parameter }
            : parameter;
    }

    /// <summary>A type written by the builder for a type it already knows.</summary>
    private static TypeExpression Known(TypeSymbol type, SourceLocation loc)
    {
        return new TypeExpression(Name: type.Name, GenericArguments: null, Location: loc) { KnownType = type };
    }
}
