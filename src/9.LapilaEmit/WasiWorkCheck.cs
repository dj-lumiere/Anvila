using Builder.Diagnostics;
using Builder.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using Builder.Declaration;

namespace Builder.LapilaEmit;

/// <summary>
/// A build error the backend found in the program: the build stops with it, rendered as any other build error.
/// </summary>
public sealed class BackendBuildErrorException(SemanticError error) : Exception(message: error.FormattedMessage)
{
    /// <summary>The error.</summary>
    public SemanticError Error { get; } = error;
}

/// <summary>
/// The Work rule on WebAssembly under WASI: a Work runs as a coroutine (a stack of its own the runtime switches to) or
/// on a thread, and wasm32-wasip1-none has neither. A program that starts one is a build error there, which names the
/// target and shows the call path from <c>start</c> to the place that would make the coroutine or start the thread.
/// </summary>
internal static class WasiWorkCheck
{
    /// <summary>The runtime's routines that make a coroutine or start a thread for a Work.</summary>
    private static readonly HashSet<string> StackRoutines = new(comparer: StringComparer.Ordinal)
    {
        "ig_coro_create", "ig_sched_spawn_default", "ig_sched_run_until_default", "ig_task_spawn_threaded"
    };

    /// <summary>
    /// Throws <see cref="BackendBuildErrorException"/> when a routine reached from <paramref name="start"/> makes a
    /// coroutine or starts a thread. The error points at the first call on the path written in a program file (not
    /// in the standard library), and its message lists the path.
    /// </summary>
    public static void Check(RoutineInfo start, IReadOnlyList<(RoutineInfo Info, Statement Body)> routines,
        TypeRegistry registry, IReadOnlySet<string> programFiles)
    {
        var bodies = new Dictionary<RoutineInfo, Statement>();
        foreach ((RoutineInfo info, Statement body) in routines)
        {
            bodies.TryAdd(key: info, value: body);
        }

        // Breadth first from start, so the path shown is a shortest one.
        var cameFrom = new Dictionary<RoutineInfo, (RoutineInfo Caller, SourceLocation At)>();
        var queue = new Queue<RoutineInfo>();
        var seen = new HashSet<RoutineInfo> { start };
        queue.Enqueue(item: start);
        while (queue.Count > 0)
        {
            RoutineInfo routine = queue.Dequeue();
            if (!bodies.TryGetValue(key: routine, value: out Statement? body))
            {
                continue;
            }

            foreach ((RoutineInfo callee, SourceLocation at) in Callees(body: body))
            {
                if (!seen.Add(item: callee))
                {
                    continue;
                }

                cameFrom[key: callee] = (routine, at);
                if (callee.CallingConvention == "C" && StackRoutines.Contains(item: callee.Name))
                {
                    throw new BackendBuildErrorException(error: Error(found: callee, start: start, cameFrom: cameFrom,
                        registry: registry, programFiles: programFiles));
                }

                queue.Enqueue(item: callee);
            }
        }
    }

    /// <summary>The routines a body calls or takes as a value, each with where it does.</summary>
    private static List<(RoutineInfo Callee, SourceLocation At)> Callees(Statement body)
    {
        var callees = new List<(RoutineInfo, SourceLocation)>();
        AstWalker.WalkExpressions(root: body, visit: expression =>
        {
            switch (expression)
            {
                case CallExpression { ResolvedRoutine: { } called } call:
                    callees.Add(item: (called, call.Location));
                    break;
                case IdentifierExpression { ResolvedRoutine: { } named } reference:
                    callees.Add(item: (named, reference.Location));
                    break;
            }
        });
        return callees;
    }

    private static SemanticError Error(RoutineInfo found, RoutineInfo start,
        Dictionary<RoutineInfo, (RoutineInfo Caller, SourceLocation At)> cameFrom, TypeRegistry registry,
        IReadOnlySet<string> programFiles)
    {
        var path = new List<(RoutineInfo Routine, SourceLocation At)>();
        for (RoutineInfo at = found; cameFrom.TryGetValue(key: at, value: out var step); at = step.Caller)
        {
            path.Add(item: (at, step.At));
        }

        path.Reverse();
        // The call to point at: the last one written in the program's own files, the place it hands the work on to
        // the standard library.
        SourceLocation place = path.Select(selector: p => p.At)
                                   .LastOrDefault(predicate: l => l.FileName is { } file &&
                                                                  programFiles.Contains(item: Path.GetFullPath(path: file)))
                               ?? path[index: 0].At;
        string shown = string.Join(separator: " -> ",
            values: new[] { Display(routine: start) }.Concat(
                second: path.Where(predicate: p => p.Routine.CallingConvention != "C")
                            .Select(selector: p => Display(routine: p.Routine))));
        // Mielia's words have no "stack" in them: a Work there runs alongside the routine that starts it.
        string why = registry.Language == Language.Mielia
            ? "WebAssembly under WASI runs one routine at a time, and a Work runs alongside the routine that starts it"
            : "a Work runs on a stack of its own or on another thread, and WebAssembly under WASI has one thread and " +
              "can't switch stacks";
        return new SemanticError(Code: SemanticDiagnosticCode.WorkNeedsStackOnTarget,
            Message: $"This starts a Work, and a Work can't run on {Targeting.TargetConfig.WasiTriple}: {why}. The " +
                     $"call path: {shown}. Do the work in the routine itself, or build for this machine.",
            Location: place);
    }

    private static string Display(RoutineInfo routine) =>
        routine.OwnerType != null ? $"{routine.OwnerType.Name}.{routine.Name}()" : $"{routine.Name}()";
}
