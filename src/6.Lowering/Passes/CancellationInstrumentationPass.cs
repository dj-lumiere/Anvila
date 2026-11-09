using Builder.Declaration;
using Builder.Verification;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// v0.2.0 Phase 9-2 (Mechanism C): inserts coroutine cancellation push/pop markers into the
/// bodies of may-suspend routines, so a coroutine abandoned while parked tears down exactly the
/// owned values it had constructed (the design's cancellation shadow stack).
///
/// <para>The markers are <see cref="CancellationPushStatement"/> / <see cref="CancellationPopStatement"/>,
/// carrying every decision: which locals (those declared in the body, and the parameters, that its inline teardown
/// destroys), the <c>destruct</c> routine (the one that teardown calls), and whether it takes the local's address. This
/// pass runs LAST (after all analysis/lowering, just before the emitter), so nothing else observes the
/// markers; the emitter translates them to the <c>rf_coro_cf_push</c>/<c>rf_coro_cf_pop</c> runtime calls.</para>
///
/// <para>Consistency-by-construction: the set of instrumented locals is DERIVED from the inline
/// <c>local.destruct()</c> calls <c>ScopeTeardownLoweringPass</c> already inserted. A push goes
/// right after a local's construction (so a value built *after* a suspend point is not on the stack
/// before it — partial init), and a pop right before each of that local's inline
/// <c>destruct</c> (so the node is removed exactly when the inline teardown runs — the value can
/// never be torn down twice). A parameter the teardown destroys is pushed at the body's entry, in parameter
/// order, so the teardown's reverse order pops it last. A value handed on (an entity used bare as a value, an
/// argument or a stored value, or any local under <c>steal</c>) is popped right before the statement that hands it on,
/// since whoever takes it tears it down from then on. Every <c>return</c> and <c>throw</c> first pops each registered
/// local in scope: the one it hands out is torn down by its caller, and a node left linked would outlive its frame. A
/// local given a new value is pushed again after the assignment (the old one's teardown popped it). A pop unlinks its
/// node wherever it is on the stack, and does nothing for a node not on it. Empty may-suspend set ⇒ this pass is a
/// no-op.</para>
///
/// <para>Covers user routine declarations, monomorphized generic bodies and the synthesized bodies (the recovery
/// variants <c>try</c>/<c>check</c> bind to, which a Work runs). A body that already carries markers (a
/// synthesized body the daemon keeps between builds) is left as it is.</para>
/// </summary>
public sealed class CancellationInstrumentationPass
{
    private readonly HashSet<string> _maySuspend;

    private CancellationInstrumentationPass(HashSet<string> maySuspend)
    {
        _maySuspend = maySuspend;
    }

    /// <summary>
    /// Instruments every may-suspend routine in place: free routines and concrete member routines
    /// from <paramref name="programs"/>, plus monomorphized generic bodies in
    /// <paramref name="instantiatedBodies"/> (e.g. <c>List[Box].pop</c>).
    ///
    /// <para>The may-suspend set is computed HERE from the final resolved bodies, not read from
    /// <paramref name="maySuspendKeys"/>. The upstream <c>ComputeMaySuspend</c> runs in Phase 7 over a
    /// call graph that the retired <c>RoutineReachabilityPass</c> used to populate; with that pass gone the
    /// graph is empty, so its verdict is always empty. This pass runs LAST — after the demand collector has
    /// resolved every reached call — so it owns the authoritative bodies and rebuilds the graph itself
    /// (edges + <see cref="CallGraphNode.DirectlySuspends"/>/<see cref="CallGraphNode.HasIndirectCall"/>
    /// seeds) before running the <see cref="MaySuspendAnalysis"/> fixpoint. The passed-in keys are UNIONed
    /// in so any value a future upstream producer supplies is still honored (over-approximation only adds
    /// shadow-stack push/pops, never miscompiles teardown). No-op when nothing reaches a suspend primitive.</para>
    /// </summary>
    public static void Run(IEnumerable<(Program Program, string FilePath, string Module)> programs,
        IReadOnlyDictionary<string, Instantiation.MonomorphizedBody> instantiatedBodies,
        IReadOnlyDictionary<string, Statement> synthesizedBodies,
        TypeRegistry registry,
        IReadOnlyCollection<string> maySuspendKeys)
    {
        var programList = programs.ToList();
        List<(RoutineInfo Info, Statement Body)> synthesized = ResolveSynthesized(
            synthesizedBodies: synthesizedBodies,
            registry: registry);
        var maySuspend = new HashSet<string>(collection: maySuspendKeys,
            comparer: StringComparer.Ordinal);
        maySuspend.UnionWith(other: ComputeMaySuspendFromBodies(programs: programList,
            instantiatedBodies: instantiatedBodies,
            synthesized: synthesized));
        if (maySuspend.Count == 0)
        {
            return;
        }

        var pass = new CancellationInstrumentationPass(maySuspend: maySuspend);
        programs = programList;

        foreach ((Program program, _, _) in programs)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                pass.MaybeInstrument(decl: decl);
            }
        }

        // Monomorphized generic bodies (List[Box].pop, etc.) are keyed by concrete RegistryKey and
        // are the SAME objects codegen emits, so mutating their bodies in place takes effect. They
        // inherited the inline destroy calls from the lowered generic-def, so InstrumentBody derives
        // the teardown set the same way.
        foreach ((string key, Instantiation.MonomorphizedBody mb) in instantiatedBodies)
        {
            if (pass._maySuspend.Contains(item: key) ||
                pass._maySuspend.Contains(item: mb.Info.RegistryKey))
            {
                pass.InstrumentBody(body: mb.Ast.Body, info: mb.Info);
            }
        }

        // Synthesized bodies: the recovery variants (`parked?check`, the body a Work runs) and the other routines
        // the builder writes. The emitter takes them from the same dictionary, so the markers reach it.
        foreach ((RoutineInfo info, Statement body) in synthesized)
        {
            if (pass._maySuspend.Contains(item: info.RegistryKey))
            {
                pass.InstrumentBody(body: body, info: info);
            }
        }
    }

    /// <summary>The synthesized bodies whose routine is registered and concrete, with that routine.</summary>
    private static List<(RoutineInfo Info, Statement Body)> ResolveSynthesized(
        IReadOnlyDictionary<string, Statement> synthesizedBodies, TypeRegistry registry)
    {
        var resolved = new List<(RoutineInfo Info, Statement Body)>();
        foreach ((string key, Statement body) in synthesizedBodies)
        {
            if (registry.RoutineByRegistryKey(registryKey: key) is
                { IsGenericDefinition: false, OwnerType: null or { IsGenericDefinition: false } } info)
            {
                resolved.Add(item: (info, body));
            }
        }

        return resolved;
    }

    /// <summary>
    /// The <see cref="RoutineInfo"/> signature resolution recorded for a routine declaration, so it can be
    /// gated on the may-suspend set. A generic definition (or a member of a generic-definition owner,
    /// <c>List[T].x</c>) is skipped: its instrumentation belongs on the monomorphized bodies.
    /// </summary>
    private static RoutineInfo? ResolveDecl(RoutineDeclaration decl)
    {
        return decl.ResolvedInfo is
            { IsGenericDefinition: false, OwnerType: null or { IsGenericDefinition: false } } info
            ? info
            : null;
    }

    private void MaybeInstrument(RoutineDeclaration decl)
    {
        RoutineInfo? info = ResolveDecl(decl: decl);
        if (info != null && _maySuspend.Contains(item: info.RegistryKey))
        {
            InstrumentBody(body: decl.Body, info: info);
        }
    }

    /// <summary>
    /// Rebuilds the may-suspend call graph from the FINAL resolved bodies and runs the
    /// <see cref="MaySuspendAnalysis"/> fixpoint, returning the may-suspend routine keys. Re-homes the edge
    /// recording the retired <c>RoutineReachabilityPass</c> used to do: walk every user-routine and
    /// monomorphized body, add a caller→callee edge for each resolved call, seed
    /// <see cref="CallGraphNode.DirectlySuspends"/> when a callee is a suspend primitive
    /// (<see cref="SuspendPrimitives"/>) and <see cref="CallGraphNode.HasIndirectCall"/> for an unresolved
    /// call through a routine value. A caller with no resolvable <see cref="RoutineInfo"/> is skipped
    /// (it can't be keyed into the graph); its body still gets no instrumentation, which is sound.
    /// </summary>
    private static IReadOnlySet<string> ComputeMaySuspendFromBodies(
        List<(Program Program, string FilePath, string Module)> programs,
        IReadOnlyDictionary<string, Instantiation.MonomorphizedBody> instantiatedBodies,
        List<(RoutineInfo Info, Statement Body)> synthesized)
    {
        var graph = new CallGraph();

        foreach ((Program program, _, _) in programs)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                if (ResolveDecl(decl: decl) is { } caller)
                {
                    RecordBodyEdges(graph: graph, caller: caller, body: decl.Body);
                }
            }
        }

        foreach ((_, Instantiation.MonomorphizedBody mb) in instantiatedBodies)
        {
            RecordBodyEdges(graph: graph, caller: mb.Info, body: mb.Ast.Body);
        }

        foreach ((RoutineInfo info, Statement body) in synthesized)
        {
            RecordBodyEdges(graph: graph, caller: info, body: body);
        }

        return new MaySuspendAnalysis(callGraph: graph).Compute();
    }

    /// <summary>
    /// Records the caller→callee edges (and suspend/indirect seeds) for one routine body into
    /// <paramref name="graph"/>. Walks every expression; a resolved <see cref="CallExpression"/> /
    /// <see cref="GenericMemberRoutineCallExpression"/> yields a real edge, an unresolved call through a
    /// <see cref="RoutineTypeSymbol"/> callee marks the caller as having an indirect call.
    /// </summary>
    private static void RecordBodyEdges(CallGraph graph, RoutineInfo caller, Statement? body)
    {
        if (body == null)
        {
            return;
        }

        AstWalker.WalkExpressions(root: body, visit: expr =>
        {
            RoutineInfo? callee = expr switch
            {
                CallExpression { ResolvedRoutine: { } cr } => cr,
                GenericMemberRoutineCallExpression { ResolvedRoutine: { } gr } => gr,
                _ => null
            };
            if (callee != null)
            {
                graph.AddEdge(caller: caller, callee: callee, callsOnMe: false);
                if (SuspendPrimitives.IsSuspendPrimitive(routine: callee))
                {
                    graph.GetOrCreateNode(routine: caller).DirectlySuspends = true;
                }

                return;
            }

            // Unresolved indirect call through a routine value — the static graph can't see the target,
            // so treat the caller conservatively as may-suspend (over-approximation is teardown-safe).
            if (expr is CallExpression { ResolvedRoutine: null } ce &&
                ce.Callee.ResolvedType is RoutineTypeSymbol)
            {
                graph.GetOrCreateNode(routine: caller).HasIndirectCall = true;
            }
        });
    }

    /// <summary>
    /// Instruments one routine body (a source decl, a monomorphized generic body or a synthesized one). The
    /// set of instrumented locals is DERIVED from the inline <c>X.destruct()</c> calls
    /// <c>ScopeTeardownLoweringPass</c> already inserted — keeping abandon's set == inline's set. A parameter in
    /// that set is pushed at the body's entry.
    /// </summary>
    private void InstrumentBody(Statement body, RoutineInfo info)
    {
        if (body is not BlockStatement block || HasMarkers(body: block))
        {
            return;
        }

        // A local's own inline teardown names its destroy routine and its type. Only locals declared in the body and
        // the routine's parameters are registered.
        var declared = new HashSet<string>(comparer: StringComparer.Ordinal);
        var locals = new Dictionary<string, Teardown>(comparer: StringComparer.Ordinal);
        AstWalker.Walk(root: block,
            visit: n =>
            {
                switch (n)
                {
                    case DeclarationStatement { Declaration: VariableDeclaration v }:
                        declared.Add(item: v.Name);
                        break;
                    case CallExpression
                    {
                        Callee: MemberExpression
                        {
                            MemberName: "destruct", Object: IdentifierExpression destroyed
                        },
                        ResolvedRoutine: { } destroy
                    }:
                        locals.TryAdd(key: destroyed.Name,
                            value: new Teardown(Destroy: destroy,
                                PassesAddress: destroyed.ResolvedType is not EntityTypeSymbol));
                        break;
                }
            });
        List<string> parameters = info.Parameters
                                      .Select(selector: p => p.Name)
                                      .Where(predicate: name => name != "me" && !declared.Contains(item: name) &&
                                                                locals.ContainsKey(key: name))
                                      .ToList();
        foreach (string name in locals.Keys
                                      .Where(predicate: name => !declared.Contains(item: name) &&
                                                                !parameters.Contains(item: name))
                                      .ToList())
        {
            locals.Remove(key: name);
        }

        if (locals.Count == 0)
        {
            return;
        }

        InstrumentBlock(block: block, locals: locals, scope: parameters);

        // The parameters hold their values from the start: pushed first, in parameter order, so the teardown's
        // reverse order (locals, then the parameters from the last) pops each while it is the top.
        block.Statements.InsertRange(index: 0,
            collection: parameters.Select(selector: name => (Statement)new CancellationPushStatement(Local: name,
                Destroy: locals[key: name].Destroy,
                PassesAddress: locals[key: name].PassesAddress,
                Location: block.Location)));
    }

    /// <summary>What tears a registered local down: its <c>destruct</c> routine, and whether that takes the local's
    /// address (a value) rather than the reference it holds (an entity).</summary>
    private sealed record Teardown(RoutineInfo Destroy, bool PassesAddress);

    /// <summary>Whether <paramref name="body"/> already carries markers from an earlier build.</summary>
    private static bool HasMarkers(BlockStatement body)
    {
        bool found = false;
        AstWalker.Walk(root: body,
            visit: n => found |= n is CancellationPushStatement or CancellationPopStatement);
        return found;
    }

    /// <summary>
    /// Rewrites <paramref name="block"/>'s statement list in place: a push marker after each
    /// instrumented local's construction, a pop marker before each of its inline <c>destruct</c>
    /// and before a statement that hands its entity on, a pop and a push after an assignment of a new
    /// value to it, and before each <c>return</c> or <c>throw</c> a pop of every registered local in scope
    /// (one the exit hands out is torn down by no one here, and a node left linked would outlive its frame).
    /// Recurses into nested blocks first. <paramref name="scope"/> holds the registered names pushed in the
    /// enclosing blocks, in push order.
    /// </summary>
    private void InstrumentBlock(BlockStatement block, Dictionary<string, Teardown> locals, List<string> scope)
    {
        var rewritten = new List<Statement>(capacity: block.Statements.Count);
        var inScope = new List<string>(collection: scope);

        foreach (Statement stmt in block.Statements)
        {
            RecurseInto(stmt: stmt, locals: locals, scope: inScope);

            if (IsDestroyCall(stmt: stmt, local: out string? destroyed) &&
                locals.ContainsKey(key: destroyed!))
            {
                rewritten.Add(item: new CancellationPopStatement(Local: destroyed!, Location: stmt.Location));
                rewritten.Add(item: stmt);
                continue;
            }

            IEnumerable<string> popped = stmt is ReturnStatement or VariantReturnStatement or ThrowStatement
                ? Enumerable.Reverse(source: inScope)
                : HandedOn(stmt: stmt, locals: locals);
            foreach (string name in popped)
            {
                rewritten.Add(item: new CancellationPopStatement(Local: name, Location: stmt.Location));
            }

            rewritten.Add(item: stmt);
            if (stmt is DeclarationStatement { Declaration: VariableDeclaration v } &&
                locals.TryGetValue(key: v.Name, value: out Teardown? declaredTeardown))
            {
                rewritten.Add(item: Push(local: v.Name, teardown: declaredTeardown, location: stmt.Location));
                inScope.Remove(item: v.Name);
                inScope.Add(item: v.Name);
            }
            else if (AssignedLocal(stmt: stmt) is { } assigned &&
                     locals.TryGetValue(key: assigned.Name, value: out Teardown? assignedTeardown))
            {
                // The old value's teardown already popped the node. The pop makes the push safe on a path where
                // nothing popped it.
                rewritten.Add(item: new CancellationPopStatement(Local: assigned.Name, Location: stmt.Location));
                rewritten.Add(item: Push(local: assigned.Name, teardown: assignedTeardown, location: stmt.Location));
            }
        }

        block.Statements.Clear();
        block.Statements.AddRange(collection: rewritten);
    }

    /// <summary>The local <paramref name="stmt"/> gives a new value (<c>x = ...</c>, as a statement or still in operator
    /// form), or null.</summary>
    private static IdentifierExpression? AssignedLocal(Statement stmt)
    {
        return stmt switch
        {
            AssignmentStatement { Target: IdentifierExpression target } => target,
            ExpressionStatement { Expression: BinaryExpression { Operator: BinaryOperator.Assign, Left: IdentifierExpression left } } =>
                left,
            _ => null
        };
    }

    private static CancellationPushStatement Push(string local, Teardown teardown, SourceLocation location)
    {
        return new CancellationPushStatement(Local: local,
            Destroy: teardown.Destroy,
            PassesAddress: teardown.PassesAddress,
            Location: location);
    }

    /// <summary>
    /// The registered locals <paramref name="stmt"/> hands on: any it takes with <c>steal</c>, and each entity it uses
    /// as a value of its own (an argument, a stored value), not as the object of a member access (a field read, a call
    /// on it) and not as the target it assigns. Only the statement's own expressions count: a compound statement's
    /// nested statements are instrumented on their own.
    /// </summary>
    private static List<string> HandedOn(Statement stmt, Dictionary<string, Teardown> locals)
    {
        IEnumerable<object> roots = stmt switch
        {
            IfStatement i => [i.Condition],
            WhileStatement w => [w.Condition],
            ForStatement f => [f.Iterable],
            WhenStatement w => [w.Expression],
            UsingStatement u => [u.Resource],
            AssignmentStatement a => a.Target is IdentifierExpression
                ? [a.Value]
                : [a.Target, a.Value],
            ExpressionStatement { Expression: BinaryExpression { Operator: BinaryOperator.Assign } assign } =>
                assign.Left is IdentifierExpression
                    ? [assign.Right]
                    : [assign.Left, assign.Right],
            BlockStatement or LoopStatement or DangerStatement => [],
            _ => [stmt]
        };

        var objects = new HashSet<IdentifierExpression>(comparer: ReferenceEqualityComparer.Instance);
        var stolen = new HashSet<IdentifierExpression>(comparer: ReferenceEqualityComparer.Instance);
        var uses = new List<IdentifierExpression>();
        foreach (object root in roots)
        {
            AstWalker.Walk(root: root,
                visit: n =>
                {
                    switch (n)
                    {
                        case MemberExpression { Object: IdentifierExpression receiver }:
                            objects.Add(item: receiver);
                            break;
                        case StealExpression { Operand: IdentifierExpression taken }:
                            stolen.Add(item: taken);
                            break;
                        case IdentifierExpression id:
                            uses.Add(item: id);
                            break;
                    }
                });
        }

        var handedOn = new List<string>();
        foreach (IdentifierExpression id in uses)
        {
            if (!handedOn.Contains(item: id.Name) && locals.ContainsKey(key: id.Name) &&
                (stolen.Contains(item: id) || id.ResolvedType is EntityTypeSymbol && !objects.Contains(item: id)))
            {
                handedOn.Add(item: id.Name);
            }
        }

        return handedOn;
    }

    /// <summary>Descends into a statement's nested blocks so they are instrumented too.</summary>
    private void RecurseInto(Statement stmt, Dictionary<string, Teardown> locals, List<string> scope)
    {
        switch (stmt)
        {
            case BlockStatement b:
                InstrumentBlock(block: b, locals: locals, scope: scope);
                break;
            case IfStatement i:
                RecurseStmt(stmt: i.ThenStatement, locals: locals, scope: scope);
                if (i.ElseStatement != null)
                {
                    RecurseStmt(stmt: i.ElseStatement, locals: locals, scope: scope);
                }

                break;
            case WhileStatement w:
                RecurseStmt(stmt: w.Body, locals: locals, scope: scope);
                break;
            case LoopStatement l:
                RecurseStmt(stmt: l.Body, locals: locals, scope: scope);
                break;
            case ForStatement f:
                RecurseStmt(stmt: f.Body, locals: locals, scope: scope);
                break;
            case DangerStatement d:
                InstrumentBlock(block: d.Body, locals: locals, scope: scope);
                break;
            case UsingStatement u:
                RecurseStmt(stmt: u.Body, locals: locals, scope: scope);
                break;
            case WhenStatement whenStmt:
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    RecurseStmt(stmt: clause.Body, locals: locals, scope: scope);
                }

                break;
        }
    }

    private void RecurseStmt(Statement stmt, Dictionary<string, Teardown> locals, List<string> scope)
    {
        if (stmt is BlockStatement b)
        {
            InstrumentBlock(block: b, locals: locals, scope: scope);
        }
        else
        {
            RecurseInto(stmt: stmt, locals: locals, scope: scope);
        }
    }

    private static bool IsDestroyCall(Statement stmt, out string? local)
    {
        if (stmt is ExpressionStatement
            {
                Expression: CallExpression
                {
                    Callee: MemberExpression
                    {
                        MemberName: "destruct", Object: IdentifierExpression id
                    }
                }
            })
        {
            local = id.Name;
            return true;
        }

        local = null;
        return false;
    }
}
