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
    /// Collects the globals of <paramref name="orderedFiles"/> (within one module the last declaration of a name wins,
    /// two modules may each have a global of the same name), strips their
    /// initializers, and orders them. Returns false after reporting RF-S436 when they read each other in a cycle, and
    /// true with an empty list when there are none.
    /// </summary>
    public static bool TryOrder(List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        Action<SemanticDiagnosticCode, string, SourceLocation> report,
        out List<ModuleGlobal> globals,
        out List<HashSet<int>> deps, out List<int> order)
    {
        var collected =
            new List<ModuleGlobal>();
        foreach ((SyntaxTree.Program program, string _) in orderedFiles)
        {
            string? module = ModuleOf(program: program);
            List<ISyntaxTreeNode> decls = program.Declarations;
            for (int i = 0; i < decls.Count; i++)
            {
                if (decls[index: i] is VariableDeclaration
                    {
                        IsGlobal: true, Initializer: not null, Type: not null
                    } g)
                {
                    collected.Add(item: new ModuleGlobal(Module: module, Name: g.Name, Type: g.Type,
                        Init: g.Initializer, Loc: g.Location));
                    decls[index: i] = g with { Initializer = null };
                }
            }
        }

        globals = DeduplicateGlobals(collected: collected);
        var namesSeen = globals.GroupBy(keySelector: g => g.Name)
                               .Where(predicate: group => group.Count() > 1)
                               .Select(selector: group => group.Key)
                               .ToHashSet(comparer: StringComparer.Ordinal);
        globals = globals.Select(selector: g => g with { NameIsShared = namesSeen.Contains(item: g.Name) })
                         .ToList();
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
            List<ModuleGlobal> declared = globals;
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
        List<ModuleGlobal> all = globals;
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

    /// <summary>Deduplicates collected globals by module and name (the last declaration wins) while preserving
    /// first-seen order for a stable field layout.</summary>
    private static List<ModuleGlobal> DeduplicateGlobals(List<ModuleGlobal> collected)
    {
        var seenOrder = new List<(string?, string)>();
        var latest = new Dictionary<(string?, string), ModuleGlobal>();
        foreach (ModuleGlobal global in collected)
        {
            (string?, string) key = (global.Module, global.Name);
            if (!latest.ContainsKey(key: key))
            {
                seenOrder.Add(item: key);
            }

            latest[key: key] = global;
        }

        return seenOrder.Select(selector: key => latest[key: key])
                        .ToList();
    }

    /// <summary>The module a program declares (its <c>module</c> header, which the build driver adds to a file
    /// without one), or null.</summary>
    public static string? ModuleOf(SyntaxTree.Program program)
    {
        return program.Declarations.OfType<ModuleDeclaration>()
                      .FirstOrDefault()
                     ?.Path;
    }

    /// <summary>
    /// The name a global goes by where every global is kept together (Suflae's one holder of all globals): its own
    /// name when no other module has a global of that name, or the name joined with its module when two modules
    /// do, so the two stay apart.
    /// </summary>
    public static string StorageName(string? module, string name, bool nameIsShared)
    {
        if (!nameIsShared || string.IsNullOrEmpty(value: module))
        {
            return name;
        }

        var sb = new System.Text.StringBuilder(value: name).Append(value: "__");
        foreach (char c in module)
        {
            sb.Append(value: char.IsLetterOrDigit(c: c) ? c : '_');
        }

        return sb.ToString();
    }

    /// <summary>Which global a name read in <paramref name="module"/> means: that module's own global of the name,
    /// else the one global of that name another module has (the last one when several do), else none.</summary>
    private sealed class GlobalIndex
    {
        private readonly Dictionary<(string?, string), int> _byModule = new();
        private readonly Dictionary<string, int> _byName = new(comparer: StringComparer.Ordinal);

        public GlobalIndex(List<ModuleGlobal> globals)
        {
            for (int i = 0; i < globals.Count; i++)
            {
                _byModule[key: (globals[index: i].Module, globals[index: i].Name)] = i;
                _byName[key: globals[index: i].Name] = i;
            }
        }

        public bool TryFind(string? module, string name, out int index)
        {
            return _byModule.TryGetValue(key: (module, name), value: out index) ||
                   _byName.TryGetValue(key: name, value: out index);
        }
    }

    /// <summary>The free routines of the build by name, each with its module, so a call is followed into the
    /// routine of the caller's own module first.</summary>
    private sealed class RoutineIndex
    {
        private readonly Dictionary<(string?, string), Statement> _byModule = new();
        private readonly Dictionary<string, (Statement Body, string? Module)> _byName =
            new(comparer: StringComparer.Ordinal);

        public void Add(string? module, string name, Statement body)
        {
            _byModule[key: (module, name)] = body;
            _byName[key: name] = (body, module);
        }

        public void TryAdd(string? module, string name, Statement body)
        {
            if (!_byModule.ContainsKey(key: (module, name)))
            {
                Add(module: module, name: name, body: body);
            }
        }

        public bool TryFind(string? module, string name, out Statement body, out string? bodyModule)
        {
            if (_byModule.TryGetValue(key: (module, name), value: out Statement? own))
            {
                body = own;
                bodyModule = module;
                return true;
            }

            if (_byName.TryGetValue(key: name, value: out (Statement Body, string? Module) other))
            {
                body = other.Body;
                bodyModule = other.Module;
                return true;
            }

            body = null!;
            bodyModule = null;
            return false;
        }
    }

    /// <summary>Computes, for each global, the set of OTHER globals it (transitively) depends on. A global's
    /// initializer reading another global is a dependency; a free-routine call is followed once into the
    /// callee's body so a hidden read (`global a = compute()` where `compute` reads `b`) counts too.
    /// (Member-routine calls are not followed — a global read hidden behind `x.foo()` is the residual.)</summary>
    private static List<HashSet<int>> ComputeGlobalDependencies(
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        List<ModuleGlobal> globals)
    {
        int n = globals.Count;
        var index = new GlobalIndex(globals: globals);

        // Index every free routine's body by name and module so the dependency scan can follow calls.
        var routineBodies = new RoutineIndex();
        foreach ((SyntaxTree.Program program, string _) in orderedFiles)
        {
            string? module = ModuleOf(program: program);
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is RoutineDeclaration { Body: { } body } r)
                {
                    routineBodies.Add(module: module, name: r.Name, body: body);
                }
            }
        }

        var deps = new List<HashSet<int>>(capacity: n);
        for (int i = 0; i < n; i++)
        {
            deps.Add(item: ComputeSingleGlobalDeps(init: globals[index: i].Init,
                module: globals[index: i].Module,
                index: index,
                routineBodies: routineBodies));
        }

        return deps;
    }

    /// <summary>Adds to each global's dependencies the globals the <c>destruct</c> of its type reads (and the
    /// destructs of the entity types it holds as member variables, and the free routines they call).</summary>
    private static void AddDestructDependencies(List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        List<ModuleGlobal> globals, List<HashSet<int>> deps)
    {
        var index = new GlobalIndex(globals: globals);
        var routineBodies = new RoutineIndex();
        var memberTypes = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);
        var typeModules = new Dictionary<string, string?>(comparer: StringComparer.Ordinal);
        foreach ((SyntaxTree.Program program, string _) in orderedFiles)
        {
            string? module = ModuleOf(program: program);
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                switch (node)
                {
                    case RoutineDeclaration { Body: { } body } r:
                        routineBodies.Add(module: module, name: r.Name, body: body);
                        if (r is { OwnerName: { } ownerName, MemberRoutineName: { } memberName })
                        {
                            routineBodies.Add(module: module, name: $"{ownerName}.{memberName}", body: body);
                        }

                        break;
                    case EntityDeclaration e:
                        typeModules[key: e.Name] = module;
                        memberTypes[key: e.Name] = e.Members.OfType<VariableDeclaration>()
                                                    .Where(predicate: v => v.Type != null)
                                                    .Select(selector: v => v.Type!.Name)
                                                    .ToList();
                        foreach (RoutineDeclaration m in e.Members.OfType<RoutineDeclaration>())
                        {
                            if (m.Body is { } memberBody)
                            {
                                routineBodies.TryAdd(module: module, name: $"{e.Name}.{m.Name}", body: memberBody);
                            }
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

                string? typeModule = typeModules.GetValueOrDefault(key: type) ?? globals[index: i].Module;
                if (routineBodies.TryFind(module: typeModule, name: $"{type}.destruct", body: out Statement destruct,
                        bodyModule: out string? destructModule))
                {
                    foreach (int j in ComputeSingleGlobalDeps(init: destruct, module: destructModule, index: index,
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

    /// <summary>BFS over AST expressions from the given initializer (written in <paramref name="module"/>);
    /// collects indices of globals this global directly or transitively depends on by following free-routine call
    /// bodies, a name read in a routine meaning that routine's module's global first.</summary>
    private static HashSet<int> ComputeSingleGlobalDeps(object init, string? module, GlobalIndex index,
        RoutineIndex routineBodies)
    {
        var d = new HashSet<int>();
        var visitedRoutines = new HashSet<(string?, string)>();
        var toScan = new Queue<(object Root, string? Module)>();
        toScan.Enqueue(item: (init, module));
        while (toScan.Count > 0)
        {
            (object root, string? rootModule) = toScan.Dequeue();
            AstWalker.WalkExpressions(root: root,
                visit: e =>
                {
                    if (e is IdentifierExpression id &&
                        index.TryFind(module: rootModule, name: id.Name, index: out int j))
                    {
                        d.Add(item: j);
                    }

                    // Follow a call into the callee's body once (transitive hidden dependency).
                    if (e is CallExpression { Callee: IdentifierExpression callee } &&
                        routineBodies.TryFind(module: rootModule, name: callee.Name, body: out Statement calleeBody,
                            bodyModule: out string? calleeModule) &&
                        visitedRoutines.Add(item: (calleeModule, callee.Name)))
                    {
                        toScan.Enqueue(item: (calleeBody, calleeModule));
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

/// <summary>A module-level <c>global</c> taken off its declaration: the module it is declared in, its name and type,
/// its initializer and where it is declared.</summary>
public sealed record ModuleGlobal(string? Module, string Name, TypeExpression Type, Expression Init, SourceLocation Loc)
{
    /// <summary>Whether another global of the build has the same name in a different module.</summary>
    public bool NameIsShared { get; init; }

    /// <summary>The name it goes by where every global is kept together (see
    /// <see cref="ModuleGlobalOrder.StorageName"/>).</summary>
    public string StorageName => ModuleGlobalOrder.StorageName(module: Module, name: Name, nameIsShared: NameIsShared);
}
