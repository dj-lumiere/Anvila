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
/// carrying every decision: which locals (those declared in the body that its inline teardown destroys),
/// the <c>destroy</c> routine (the one that teardown calls), and whether it takes the local's address. This
/// pass runs LAST (after all analysis/lowering, just before the emitter), so nothing else observes the
/// markers; the emitter translates them to the <c>rf_coro_cf_push</c>/<c>rf_coro_cf_pop</c> runtime calls.</para>
///
/// <para>Consistency-by-construction: the set of instrumented locals is DERIVED from the inline
/// <c>local.destroy()</c> calls <c>ScopeTeardownLoweringPass</c> already inserted. A push goes
/// right after a local's construction (so a value built *after* a suspend point is not on the stack
/// before it — partial init), and a pop right before each of that local's inline
/// <c>destroy</c> (so the node is removed exactly when the inline teardown runs — the value can
/// never be torn down twice). Empty may-suspend set ⇒ this pass is a no-op.</para>
///
/// <para>First cut scope: owned LOCALS of free routines. Member routines and synthesized/generic
/// bodies are a follow-up (until then a may-suspend member routine that owns resources would leak
/// on abandon — tracked).</para>
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
        IReadOnlyCollection<string> maySuspendKeys)
    {
        var programList = programs.ToList();
        var maySuspend = new HashSet<string>(collection: maySuspendKeys,
            comparer: StringComparer.Ordinal);
        maySuspend.UnionWith(other: ComputeMaySuspendFromBodies(programs: programList,
            instantiatedBodies: instantiatedBodies));
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
                pass.InstrumentBody(body: mb.Ast.Body);
            }
        }
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
            InstrumentBody(body: decl.Body);
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
        IReadOnlyDictionary<string, Instantiation.MonomorphizedBody> instantiatedBodies)
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
    /// Instruments one routine body (whether a source decl or a monomorphized generic body). The
    /// set of instrumented locals is DERIVED from the inline <c>X.destroy()</c> calls
    /// <c>ScopeTeardownLoweringPass</c> already inserted — keeping abandon's set == inline's set.
    /// </summary>
    private void InstrumentBody(Statement body)
    {
        if (body is not BlockStatement block)
        {
            return;
        }

        // A local's own inline teardown names its destroy routine and its type; only locals declared in
        // the body are registered (a parameter is torn down by the same teardown but never pushed).
        var declared = new HashSet<string>(comparer: StringComparer.Ordinal);
        var locals = new Dictionary<string, (RoutineInfo Destroy, bool PassesAddress)>(comparer: StringComparer.Ordinal);
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
                            MemberName: "destroy", Object: IdentifierExpression destroyed
                        },
                        ResolvedRoutine: { } destroy
                    }:
                        locals.TryAdd(key: destroyed.Name,
                            value: (destroy, destroyed.ResolvedType is not EntityTypeSymbol));
                        break;
                }
            });
        foreach (string name in locals.Keys.Where(predicate: name => !declared.Contains(item: name)).ToList())
        {
            locals.Remove(key: name);
        }

        if (locals.Count == 0)
        {
            return;
        }

        InstrumentBlock(block: block, locals: locals);
    }

    /// <summary>
    /// Rewrites <paramref name="block"/>'s statement list in place: a push marker after each
    /// instrumented local's construction, a pop marker before each of its inline <c>destroy</c>,
    /// recursing into nested blocks first.
    /// </summary>
    private void InstrumentBlock(BlockStatement block, Dictionary<string, (RoutineInfo Destroy, bool PassesAddress)> locals)
    {
        var rewritten = new List<Statement>(capacity: block.Statements.Count);

        foreach (Statement stmt in block.Statements)
        {
            RecurseInto(stmt: stmt, locals: locals);

            if (IsDestroyCall(stmt: stmt, local: out string? destroyed) &&
                locals.ContainsKey(key: destroyed!))
            {
                rewritten.Add(item: new CancellationPopStatement(Local: destroyed!, Location: stmt.Location));
                rewritten.Add(item: stmt);
            }
            else if (stmt is DeclarationStatement { Declaration: VariableDeclaration v } &&
                     locals.TryGetValue(key: v.Name, value: out (RoutineInfo Destroy, bool PassesAddress) teardown))
            {
                rewritten.Add(item: stmt);
                rewritten.Add(item: new CancellationPushStatement(Local: v.Name,
                    Destroy: teardown.Destroy,
                    PassesAddress: teardown.PassesAddress,
                    Location: stmt.Location));
            }
            else
            {
                rewritten.Add(item: stmt);
            }
        }

        block.Statements.Clear();
        block.Statements.AddRange(collection: rewritten);
    }

    /// <summary>Descends into a statement's nested blocks so they are instrumented too.</summary>
    private void RecurseInto(Statement stmt, Dictionary<string, (RoutineInfo Destroy, bool PassesAddress)> locals)
    {
        switch (stmt)
        {
            case BlockStatement b:
                InstrumentBlock(block: b, locals: locals);
                break;
            case IfStatement i:
                RecurseStmt(stmt: i.ThenStatement, locals: locals);
                if (i.ElseStatement != null)
                {
                    RecurseStmt(stmt: i.ElseStatement, locals: locals);
                }

                break;
            case WhileStatement w:
                RecurseStmt(stmt: w.Body, locals: locals);
                if (w.ElseBranch != null)
                {
                    RecurseStmt(stmt: w.ElseBranch, locals: locals);
                }

                break;
            case LoopStatement l:
                RecurseStmt(stmt: l.Body, locals: locals);
                break;
            case EachStatement f:
                RecurseStmt(stmt: f.Body, locals: locals);
                if (f.ElseBranch != null)
                {
                    RecurseStmt(stmt: f.ElseBranch, locals: locals);
                }

                break;
            case DangerStatement d:
                InstrumentBlock(block: d.Body, locals: locals);
                break;
            case UsingStatement u:
                RecurseStmt(stmt: u.Body, locals: locals);
                if (u.FallbackBody != null)
                {
                    RecurseStmt(stmt: u.FallbackBody, locals: locals);
                }

                break;
            case WhenStatement whenStmt:
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    RecurseStmt(stmt: clause.Body, locals: locals);
                }

                break;
        }
    }

    private void RecurseStmt(Statement stmt, Dictionary<string, (RoutineInfo Destroy, bool PassesAddress)> locals)
    {
        if (stmt is BlockStatement b)
        {
            InstrumentBlock(block: b, locals: locals);
        }
        else
        {
            RecurseInto(stmt: stmt, locals: locals);
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
                        MemberName: "destroy", Object: IdentifierExpression id
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
