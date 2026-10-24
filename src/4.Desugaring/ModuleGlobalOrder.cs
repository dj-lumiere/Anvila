using Builder.Diagnostics;
using SyntaxTree;

namespace Builder.Desugaring;

/// <summary>
/// The order a program's module-level <c>global</c>s are initialized in, shared by every language that has them. Each
/// global's initializer is taken off its declaration (which stays, so analysis registers the global) and the globals
/// are ordered so one whose initializer reads another comes after it. A cycle, including a read hidden in a called
/// free routine, is RF-S436.
/// </summary>
public static class ModuleGlobalOrder
{
    /// <summary>
    /// Collects the globals of <paramref name="orderedFiles"/> (the last declaration of a name wins), strips their
    /// initializers, and orders them. Returns false after reporting RF-S436 when they read each other in a cycle, and
    /// true with an empty list when there are none.
    /// </summary>
    public static bool TryOrder(List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        Action<SemanticDiagnosticCode, string, SourceLocation> report,
        out List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)> globals,
        out List<HashSet<int>> deps, out List<int> order)
    {
        var collected =
            new List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)>();
        foreach ((SyntaxTree.Program program, string _) in orderedFiles)
        {
            List<ISyntaxTreeNode> decls = program.Declarations;
            for (int i = 0; i < decls.Count; i++)
            {
                if (decls[index: i] is VariableDeclaration
                    {
                        IsGlobal: true, Initializer: not null, Type: not null
                    } g)
                {
                    collected.Add(item: (g.Name, g.Type, g.Initializer, g.Location));
                    decls[index: i] = g with { Initializer = null };
                }
            }
        }

        globals = DeduplicateGlobals(collected: collected);
        deps = ComputeGlobalDependencies(orderedFiles: orderedFiles, globals: globals);

        // A global whose initializer reads the global itself (`global A: S64 = A + 1`, or through a routine it
        // calls) would read it before it holds anything.
        for (int i = 0; i < globals.Count; i++)
        {
            if (deps[index: i].Contains(item: i))
            {
                report(SemanticDiagnosticCode.ModuleGlobalCircularInit,
                    $"The global '{globals[index: i].Name}' reads itself in its initializer, so it would read " +
                    "itself before it holds anything. Give it an initializer that does not read it.",
                    globals[index: i].Loc);
                order = [];
                return false;
            }
        }

        // A global is torn down after every global whose destruct reads it, so a destruct never reads a global that
        // is gone: a global read by another one's destruct is initialized first (globals are torn down in the
        // opposite order). Two whose destructs read each other are a cycle like two initializers.
        // The callers read `deps` as what each initializer reads, so the teardown edges go into a copy.
        List<HashSet<int>> ordering = deps.Select(selector: d => new HashSet<int>(collection: d))
                                          .ToList();
        AddDestructDependencies(orderedFiles: orderedFiles, globals: globals, deps: ordering);
        order = KahnOrder(deps: ordering, n: globals.Count);
        if (order.Count == globals.Count)
        {
            return true;
        }

        if (KahnOrder(deps: deps, n: globals.Count).Count == globals.Count)
        {
            List<int> ordered = order;
            List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)> declared = globals;
            List<string> stuck = Enumerable.Range(start: 0, count: globals.Count)
                                           .Where(predicate: i => !ordered.Contains(value: i))
                                           .Select(selector: i => $"'{declared[index: i].Name}'")
                                           .ToList();
            report(SemanticDiagnosticCode.ModuleGlobalCircularInit,
                $"The globals {string.Join(separator: ", ", values: stuck)} read each other in their initializers " +
                "and destructs, so whichever is torn down first leaves the other's destruct reading a global that is " +
                "gone. Let the destruct of one of them read none of the others.",
                globals[index: Enumerable.Range(start: 0, count: globals.Count)
                                         .First(predicate: i => !ordered.Contains(value: i))].Loc);
            return false;
        }

        List<int> placed = order;
        List<int> cyclic = Enumerable.Range(start: 0, count: globals.Count)
                                     .Where(predicate: i => !placed.Contains(value: i))
                                     .ToList();
        List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)> all = globals;
        string names = string.Join(separator: ", ", values: cyclic.Select(selector: i => $"'{all[index: i].Name}'"));
        report(SemanticDiagnosticCode.ModuleGlobalCircularInit,
            $"The globals {names} read each other in their initializers, so none of them can be " +
            "initialized first. Give one of them an initializer that does not read the others.",
            globals[index: cyclic[index: 0]].Loc);
        return false;
    }

    /// <summary>The block of the entry file's <c>routine start()</c> (the last in <paramref name="orderedFiles"/>), with its program, or
    /// null after reporting RF-S438 when there is none.</summary>
    public static (SyntaxTree.Program Program, BlockStatement Block)? FindStart(
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        Action<SemanticDiagnosticCode, string, SourceLocation> report, SourceLocation at)
    {
        // The files come in initialization order, a module after the modules it imports, so the entry file (which
        // imports the others) is the last with a start(): another module's start() is only a routine it calls.
        foreach ((SyntaxTree.Program program, string _) in Enumerable.Reverse(source: orderedFiles))
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is RoutineDeclaration { Name: "start", Body: BlockStatement block })
                {
                    return (program, block);
                }
            }
        }

        report(SemanticDiagnosticCode.ModuleGlobalWithoutStart,
            "Your globals are initialized when the program starts, but no file in the build has a " +
            "'routine start()' or top-level statements. Add one to the program's entry file.",
            at);
        return null;
    }

    /// <summary>Deduplicates collected globals by name (last-write-wins) while preserving first-seen order
    /// for a stable field layout.</summary>
    private static List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)>
        DeduplicateGlobals(
            List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)>
                collected)
    {
        var seenOrder = new List<string>();
        var latest =
            new Dictionary<string, (TypeExpression Type, Expression Init, SourceLocation Loc)>(
                comparer: StringComparer.Ordinal);
        foreach ((string name, TypeExpression type, Expression init, SourceLocation loc) in
                 collected)
        {
            if (!latest.ContainsKey(key: name))
            {
                seenOrder.Add(item: name);
            }

            latest[key: name] = (type, init, loc);
        }

        return seenOrder.Select(selector: name => (Name: name, latest[key: name].Type,
                             latest[key: name].Init, latest[key: name].Loc))
                        .ToList();
    }

    /// <summary>Computes, for each global, the set of OTHER globals it (transitively) depends on. A global's
    /// initializer reading another global is a dependency; a free-routine call is followed once into the
    /// callee's body so a hidden read (`global a = compute()` where `compute` reads `b`) counts too.
    /// (Member-routine calls are not followed — a global read hidden behind `x.foo()` is the residual.)</summary>
    private static List<HashSet<int>> ComputeGlobalDependencies(
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)> globals)
    {
        int n = globals.Count;
        var nameToIdx = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            nameToIdx[key: globals[index: i].Name] = i; // last decl of a dup name wins
        }

        // Index every free routine's body by bare name so the dependency scan can follow calls.
        var routineBodies = new Dictionary<string, Statement>(comparer: StringComparer.Ordinal);
        foreach ((SyntaxTree.Program program, string _) in orderedFiles)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is RoutineDeclaration { Body: { } body } r)
                {
                    routineBodies[key: r.Name] = body;
                }
            }
        }

        var deps = new List<HashSet<int>>(capacity: n);
        for (int i = 0; i < n; i++)
        {
            deps.Add(item: ComputeSingleGlobalDeps(init: globals[index: i].Init,
                nameToIdx: nameToIdx,
                routineBodies: routineBodies));
        }

        return deps;
    }

    /// <summary>Adds to each global's dependencies the globals the <c>destruct</c> of its type reads (and the
    /// destructs of the entity types it holds as member variables, and the free routines they call).</summary>
    private static void AddDestructDependencies(List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)> globals, List<HashSet<int>> deps)
    {
        var nameToIdx = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < globals.Count; i++)
        {
            nameToIdx[key: globals[index: i].Name] = i;
        }

        var routineBodies = new Dictionary<string, Statement>(comparer: StringComparer.Ordinal);
        var memberTypes = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);
        foreach ((SyntaxTree.Program program, string _) in orderedFiles)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                switch (node)
                {
                    case RoutineDeclaration { Body: { } body } r:
                        routineBodies[key: r.Name] = body;
                        if (r is { OwnerName: { } ownerName, MemberRoutineName: { } memberName })
                        {
                            routineBodies[key: $"{ownerName}.{memberName}"] = body;
                        }

                        break;
                    case EntityDeclaration e:
                        memberTypes[key: e.Name] = e.Members.OfType<VariableDeclaration>()
                                                    .Where(predicate: v => v.Type != null)
                                                    .Select(selector: v => v.Type!.Name)
                                                    .ToList();
                        foreach (RoutineDeclaration m in e.Members.OfType<RoutineDeclaration>())
                        {
                            routineBodies.TryAdd(key: $"{e.Name}.{m.Name}", value: m.Body);
                        }

                        break;
                }
            }
        }

        for (int i = 0; i < globals.Count; i++)
        {
            var seenTypes = new HashSet<string>(comparer: StringComparer.Ordinal);
            var types = new Queue<string>();
            types.Enqueue(item: globals[index: i].Type.Name);
            while (types.Count > 0)
            {
                string type = types.Dequeue();
                if (!seenTypes.Add(item: type))
                {
                    continue;
                }

                if (routineBodies.TryGetValue(key: $"{type}.destruct", value: out Statement? destruct))
                {
                    foreach (int j in ComputeSingleGlobalDeps(init: destruct, nameToIdx: nameToIdx,
                                 routineBodies: routineBodies))
                    {
                        if (j != i)
                        {
                            deps[index: i].Add(item: j);
                        }
                    }
                }

                foreach (string held in memberTypes.GetValueOrDefault(key: type) ?? [])
                {
                    types.Enqueue(item: held);
                }
            }
        }
    }

    /// <summary>BFS over AST expressions from the given initializer; collects indices of globals this
    /// global directly or transitively depends on by following free-routine call bodies one level.</summary>
    private static HashSet<int> ComputeSingleGlobalDeps(object init,
        Dictionary<string, int> nameToIdx, Dictionary<string, Statement> routineBodies)
    {
        var d = new HashSet<int>();
        var visitedRoutines = new HashSet<string>(comparer: StringComparer.Ordinal);
        var toScan = new Queue<object>();
        toScan.Enqueue(item: init);
        while (toScan.Count > 0)
        {
            object root = toScan.Dequeue();
            AstWalker.WalkExpressions(root: root,
                visit: e =>
                {
                    if (e is IdentifierExpression id &&
                        nameToIdx.TryGetValue(key: id.Name, value: out int j))
                    {
                        d.Add(item: j);
                    }

                    // Follow a call into the callee's body once (transitive hidden dependency).
                    if (e is CallExpression { Callee: IdentifierExpression callee } &&
                        routineBodies.TryGetValue(key: callee.Name,
                            value: out Statement? calleeBody) &&
                        visitedRoutines.Add(item: callee.Name))
                    {
                        toScan.Enqueue(item: calleeBody);
                    }
                });
        }

        return d;
    }

    /// <summary>Kahn's topological sort over the dependency edges (dependency j before dependent i), stable
    /// in source order among ready nodes. A returned order shorter than <paramref name="n"/> signals a cycle
    /// (the caller reports the un-ordered globals as RF-S436).</summary>
    private static List<int> KahnOrder(List<HashSet<int>> deps, int n)
    {
        int[] indegree = new int[n];
        for (int i = 0; i < n; i++)
        {
            indegree[i] += deps[index: i]
               .Count(predicate: j => j != i); // edge j -> i (dependency j before dependent i)
        }

        var order = new List<int>(capacity: n);
        bool ready;
        do
        {
            ready = KahnSweep(deps: deps,
                indegree: indegree,
                n: n,
                order: order);
        } while (ready);

        return order;
    }

    /// <summary>Single Kahn sweep: enqueues all zero-indegree nodes into <paramref name="order"/> and
    /// decrements their dependents' indegrees. Returns true if at least one node was emitted.</summary>
    private static bool KahnSweep(List<HashSet<int>> deps, int[] indegree, int n,
        List<int> order)
    {
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            if (indegree[i] != 0)
            {
                continue;
            }

            indegree[i] = -1; // consumed
            order.Add(item: i);
            any = true;
            for (int k = 0; k < n; k++)
            {
                if (k != i && deps[index: k]
                       .Contains(item: i))
                {
                    indegree[k]--;
                }
            }
        }

        return any;
    }
}
