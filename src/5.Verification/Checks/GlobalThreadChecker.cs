using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;

namespace Builder.Verification;

/// <summary>
/// A Revare <c>global</c> is a place the program changes (<c>count += 1</c>). Only one thing is refused: a routine
/// that a Work started on another thread reaches (through any chain of calls) reading or changing a plain global that
/// is changed anywhere in the program. The error names the global and the line the work crosses at, and says to keep
/// the value in an <c>Unbroken</c> or a <c>Guarded</c>. A global nothing changes is read from anywhere, and a Work
/// run on the thread that made it (<c>start_here()</c>) does not cross. The judgment is over the whole program, so it
/// runs after every user body is analyzed.
/// </summary>
internal sealed class GlobalThreadChecker(DiagnosticReporter report)
{
    private static readonly HashSet<string> OtherThreadVerbs = new(comparer: StringComparer.Ordinal)
    {
        "start_now", "start_apart"
    };

    private sealed class Facts
    {
        public readonly HashSet<VariableInfo> Reads = new(comparer: ReferenceEqualityComparer.Instance);
        public readonly HashSet<VariableInfo> Writes = new(comparer: ReferenceEqualityComparer.Instance);
        public readonly List<RoutineDeclaration> Callees = [];
    }

    private readonly Dictionary<string, RoutineDeclaration> _byKey = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<RoutineDeclaration, Facts> _facts = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, SourceLocation> _globalLines = new(comparer: StringComparer.Ordinal);

    internal void Check(IEnumerable<SyntaxTree.Program> programs)
    {
        List<SyntaxTree.Program> list = programs.ToList();
        var routines = new List<RoutineDeclaration>();
        foreach (SyntaxTree.Program program in list)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                switch (node)
                {
                    case RoutineDeclaration r:
                        routines.Add(item: r);
                        break;
                    case EntityDeclaration e:
                        routines.AddRange(collection: e.Members.OfType<RoutineDeclaration>());
                        break;
                    case RecordDeclaration rec:
                        routines.AddRange(collection: rec.Members.OfType<RoutineDeclaration>());
                        break;
                    case VariableDeclaration { IsGlobal: true } global:
                        _globalLines[key: global.Name] = global.Location;
                        break;
                }
            }
        }

        foreach (RoutineDeclaration routine in routines)
        {
            if (routine.ResolvedInfo is { } info)
            {
                _byKey.TryAdd(key: info.RegistryKey, value: routine);
            }
        }

        foreach (RoutineDeclaration routine in routines)
        {
            _facts[key: routine] = Collect(routine: routine);
        }

        var changed = new HashSet<VariableInfo>(comparer: ReferenceEqualityComparer.Instance);
        foreach (Facts facts in _facts.Values)
        {
            changed.UnionWith(other: facts.Writes);
        }

        if (changed.Count == 0)
        {
            return;
        }

        foreach (RoutineDeclaration routine in routines)
        {
            CheckCrossings(routine: routine, changed: changed);
        }
    }

    private static bool IsPlainGlobal(IdentifierExpression id, out VariableInfo variable)
    {
        variable = id.ResolvedVariable!;
        return id is { ResolvedVariable: { IsGlobal: true } } &&
               variable.Type.BareName is not ("Unbroken" or "Guarded");
    }

    private static Expression RootOf(Expression target)
    {
        while (true)
        {
            switch (target)
            {
                case MemberExpression m:
                    target = m.Object;
                    continue;
                case IndexExpression i:
                    target = i.Object;
                    continue;
                default:
                    return target;
            }
        }
    }

    /// <summary>The walk of <see cref="AstWalker"/>, which also goes into the call a <c>try</c> or <c>check</c> wraps.</summary>
    private static void WalkAll(object? root, Action<object> visit)
    {
        AstWalker.Walk(root: root, visit: node =>
        {
            visit(obj: node);
            if (node is RecoveryExpression recovery)
            {
                WalkAll(root: recovery.LoweredCall ?? recovery.Inner, visit: visit);
            }
        });
    }

    private Facts Collect(RoutineDeclaration routine)
    {
        var facts = new Facts();
        WalkAll(root: routine.Body, visit: node =>
        {
            switch (node)
            {
                case IdentifierExpression id when IsPlainGlobal(id: id, variable: out VariableInfo read):
                    facts.Reads.Add(item: read);
                    break;
                case AssignmentStatement { IsGlobalInit: false } assign
                    when RootOf(target: assign.Target) is IdentifierExpression target &&
                         IsPlainGlobal(id: target, variable: out VariableInfo written):
                    facts.Writes.Add(item: written);
                    break;
                case AccessStatement access:
                    foreach (AccessTarget opened in access.Targets.Where(predicate: t => t.IsWrite))
                    {
                        if (opened.Target is IdentifierExpression openedName &&
                            IsPlainGlobal(id: openedName, variable: out VariableInfo modified))
                        {
                            facts.Writes.Add(item: modified);
                            facts.Reads.Add(item: modified);
                        }
                    }

                    break;
                case BinaryExpression { Operator: BinaryOperator.Assign } assignment
                    when RootOf(target: assignment.Left) is IdentifierExpression target &&
                         IsPlainGlobal(id: target, variable: out VariableInfo written):
                    facts.Writes.Add(item: written);
                    break;
                case CompoundAssignmentExpression compound
                    when RootOf(target: compound.Target) is IdentifierExpression target &&
                         IsPlainGlobal(id: target, variable: out VariableInfo written):
                    facts.Writes.Add(item: written);
                    break;
                case CallExpression { ResolvedRoutine: { } callee } when FindDeclaration(routine: callee) is { } decl:
                    facts.Callees.Add(item: decl);
                    break;
            }
        });
        return facts;
    }

    private RoutineDeclaration? FindDeclaration(RoutineInfo routine)
    {
        routine = routine.RecoveryOf ?? routine;
        if (_byKey.TryGetValue(key: routine.RegistryKey, value: out RoutineDeclaration? d))
        {
            return d;
        }

        return routine.GenericDefinition is { } def && _byKey.TryGetValue(key: def.RegistryKey, value: out RoutineDeclaration? g)
            ? g
            : null;
    }

    private void CheckCrossings(RoutineDeclaration routine, HashSet<VariableInfo> changed)
    {
        // After analysis a `to[Work]` conversion is a call of its spawn routine, which knows the work routine.
        var works = new List<CallExpression>();
        var boundTo = new Dictionary<CallExpression, string>(comparer: ReferenceEqualityComparer.Instance);
        var verbsOn = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);
        var verbsOnCall = new Dictionary<CallExpression, HashSet<string>>(comparer: ReferenceEqualityComparer.Instance);
        WalkAll(root: routine.Body, visit: node =>
        {
            switch (node)
            {
                case CallExpression { ResolvedRoutine.AsyncSpawnOf: not null } spawn:
                    works.Add(item: spawn);
                    break;
                case VariableDeclaration { Initializer: CallExpression { ResolvedRoutine.AsyncSpawnOf: not null } bound } variable:
                    boundTo[key: bound] = variable.Name;
                    break;
                case CallExpression { Callee: MemberExpression { Object: var receiver, MemberName: var verb } }:
                    if (receiver is IdentifierExpression named)
                    {
                        if (!verbsOn.TryGetValue(key: named.Name, value: out HashSet<string>? set))
                        {
                            verbsOn[key: named.Name] = set = new HashSet<string>(comparer: StringComparer.Ordinal);
                        }

                        set.Add(item: verb);
                    }
                    else if (receiver is CallExpression directCall)
                    {
                        if (!verbsOnCall.TryGetValue(key: directCall, value: out HashSet<string>? set))
                        {
                            verbsOnCall[key: directCall] = set = new HashSet<string>(comparer: StringComparer.Ordinal);
                        }

                        set.Add(item: verb);
                    }

                    break;
            }
        });

        foreach (CallExpression spawn in works)
        {
            HashSet<string> verbs = verbsOnCall.TryGetValue(key: spawn, value: out HashSet<string>? direct)
                ? direct
                : boundTo.TryGetValue(key: spawn, value: out string? name) && verbsOn.TryGetValue(key: name, value: out HashSet<string>? bound)
                    ? bound
                    : [];
            // Started with start_here() it runs as a fiber of this thread, so it crosses nothing.
            bool crosses = verbs.Overlaps(other: OtherThreadVerbs) || !verbs.Contains(item: "start_here");
            if (crosses && FindDeclaration(routine: spawn.ResolvedRoutine!.AsyncSpawnOf!) is { } entry)
            {
                ReportReached(site: spawn.Location, entry: entry, changed: changed);
            }
        }
    }

    private void ReportReached(SourceLocation site, RoutineDeclaration entry, HashSet<VariableInfo> changed)
    {
        var seen = new HashSet<RoutineDeclaration>(comparer: ReferenceEqualityComparer.Instance) { entry };
        var queue = new Queue<(RoutineDeclaration Routine, RoutineDeclaration Called)>();
        foreach (RoutineDeclaration callee in _facts[key: entry].Callees)
        {
            if (seen.Add(item: callee))
            {
                queue.Enqueue(item: (callee, callee));
            }
        }

        var reported = new HashSet<VariableInfo>(comparer: ReferenceEqualityComparer.Instance);
        while (queue.Count > 0)
        {
            (RoutineDeclaration current, RoutineDeclaration called) = queue.Dequeue();
            Facts facts = _facts[key: current];
            foreach (VariableInfo global in facts.Reads.Where(predicate: changed.Contains))
            {
                if (!reported.Add(item: global))
                {
                    continue;
                }

                string where = ReferenceEquals(current, called)
                    ? $"'{current.Name}' uses"
                    : $"'{current.Name}', which '{called.Name}' calls, uses";
                string declared = _globalLines.TryGetValue(key: global.Name, value: out SourceLocation? line)
                    ? $" (declared on line {line.Line})"
                    : "";
                report(code: SemanticDiagnosticCode.GlobalChangedAcrossThreads,
                    message: $"This Work runs '{called.Name}' on another thread, and {where} the global '{global.Name}'{declared}, " +
                             "which the program changes. Another thread may change it while this one uses it. Keep the value " +
                             $"in an Unbroken (`global {global.Name}: Unbroken[{global.Type.ShortTypeName}]`) or a Guarded " +
                             "and change it through its own routines, or hand the Work the value as an argument.",
                    location: site);
            }

            foreach (RoutineDeclaration callee in facts.Callees)
            {
                if (seen.Add(item: callee))
                {
                    queue.Enqueue(item: (callee, called));
                }
            }
        }
    }
}
