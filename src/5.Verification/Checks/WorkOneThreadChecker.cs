using Builder.Diagnostics;
using SyntaxTree;

namespace Builder.Verification;

/// <summary>
/// A Work made with an argument only one thread may use (a token, a <c>Retained</c>) runs on the thread that made
/// it, as a fiber: it is started with <c>start_here()</c>, never with <c>start_now()</c>/<c>start_apart()</c> (or
/// retrieved without being started, which starts it with <c>start_now()</c>), and it stays in the routine that made it,
/// where what it borrows lives: it is not passed on, stored or returned (RV-S632). Runs over the analyzed user bodies.
/// </summary>
internal sealed class WorkOneThreadChecker(DiagnosticReporter report)
{
    private static readonly HashSet<string> OtherThreadVerbs = new(comparer: StringComparer.Ordinal)
    {
        "start_now", "start_apart"
    };

    private static readonly HashSet<string> CollectingVerbs = new(comparer: StringComparer.Ordinal)
    {
        "retrieve", "gather", "gather_all", "race", "next_finished"
    };

    /// <summary>Checks every routine of <paramref name="programs"/>.</summary>
    internal void Check(IEnumerable<SyntaxTree.Program> programs)
    {
        foreach (SyntaxTree.Program program in programs)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                foreach (RoutineDeclaration routine in RoutinesOf(node: node))
                {
                    CheckRoutine(routine: routine);
                }
            }
        }
    }

    private static IEnumerable<RoutineDeclaration> RoutinesOf(ISyntaxTreeNode node)
    {
        return node switch
        {
            RoutineDeclaration r => [r],
            EntityDeclaration e => e.Members.OfType<RoutineDeclaration>(),
            RecordDeclaration r => r.Members.OfType<RoutineDeclaration>(),
            _ => []
        };
    }

    private void CheckRoutine(RoutineDeclaration routine)
    {
        var held = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var allowed = new HashSet<Expression>(comparer: ReferenceEqualityComparer.Instance);
        var startedHere = new HashSet<string>(comparer: StringComparer.Ordinal);
        var nodes = new List<object>();
        AstWalker.Walk(root: routine.Body, visit: n => nodes.Add(item: n));

        // The variables a Work of one thread is bound to, and the job expressions that bind them.
        foreach (object n in nodes)
        {
            if (n is VariableDeclaration { Initializer: { } init } v && OneThreadReason(expr: init, held: held) is { } why)
            {
                held[key: v.Name] = why;
                allowed.Add(item: init);
                if (init is WorkExpression { LoweredCall: { } lowered })
                {
                    allowed.Add(item: lowered);
                }
            }
        }

        // Calls on such a Work: which verbs start or collect it.
        foreach (object n in nodes)
        {
            if (n is CallExpression { Callee: MemberExpression { Object: var receiver, MemberName: var verb } } call &&
                OneThreadReason(expr: receiver, held: held) is { } reason)
            {
                allowed.Add(item: receiver);
                if (receiver is WorkExpression { LoweredCall: { } loweredReceiver })
                {
                    allowed.Add(item: loweredReceiver);
                }

                // A start verb the Work may not use is reported there, so the collecting verb after it says nothing more.
                if ((verb == "start_here" || OtherThreadVerbs.Contains(item: verb)) &&
                    receiver is IdentifierExpression started)
                {
                    startedHere.Add(item: started.Name);
                }

                if (OtherThreadVerbs.Contains(item: verb))
                {
                    report(code: SemanticDiagnosticCode.ThreadArgNotShareable,
                        message: $"This Work holds {reason}, which only the thread that made it may use, so it starts " +
                                 $"with 'start_here()' (a fiber on this thread), not '{verb}()'. To run it on another " +
                                 "thread, hand it what another thread may use: a Guarded, an Unbroken, a copied value, " +
                                 "or an entity handed over with 'steal'.",
                        location: call.Location);
                }
            }
        }

        foreach (object n in nodes)
        {
            if (n is CallExpression { Callee: MemberExpression { Object: var receiver, MemberName: var verb } } call &&
                CollectingVerbs.Contains(item: verb) && OneThreadReason(expr: receiver, held: held) is { } reason &&
                !(receiver is IdentifierExpression id && startedHere.Contains(item: id.Name)))
            {
                report(code: SemanticDiagnosticCode.ThreadArgNotShareable,
                    message: $"'{verb}()' on a Work nobody started starts it with start_now(), on another thread, " +
                             $"and this one holds {reason}, which only this thread may use. Start it with " +
                             "'start_here()' first.",
                    location: call.Location);
            }
        }

        CheckBlockMoves(nodes: nodes, held: held);

        // Anywhere else, the Work would leave the block where the argument it holds can be used.
        foreach (object n in nodes)
        {
            if (n is Expression e && !allowed.Contains(item: e) && OneThreadReason(expr: e, held: held) is { } reason)
            {
                report(code: SemanticDiagnosticCode.ThreadArgNotShareable,
                    message: $"This Work holds {reason}, so it stays in the block that made it, the only place " +
                             "that argument can be used: start it with 'start_here()' and retrieve it there. Passing " +
                             "it on, storing it or returning it would let the work run after that block ends.",
                    location: e.Location);
            }
        }
    }

    /// <summary>
    /// A routine marked <c>@moves_block</c> (<c>StampManager.add</c>) moves the entity handed to it into another block
    /// and gives the old one back. A Work of this routine whose arguments reach that entity (a token of it, or a member
    /// a Work changes where it lies, an <c>Unbroken</c>) and that is not collected yet would then work on the given-back
    /// block, so handing the entity over before the Work is collected is RV-S632. A Work handed a copied member value
    /// counts too: the build can't tell it from a member it works on in place.
    /// </summary>
    private void CheckBlockMoves(List<object> nodes, Dictionary<string, string> held)
    {
        // Each one-thread Work variable, where it is made, and the names its arguments reach.
        var reaches = new Dictionary<string, (SourceLocation Made, HashSet<string> Names)>(comparer: StringComparer.Ordinal);
        var collected = new List<(string Work, SourceLocation At)>();
        foreach (object n in nodes)
        {
            if (n is VariableDeclaration { Initializer: { } init } v &&
                (held.ContainsKey(key: v.Name) || init is WorkExpression ||
                 init.ResolvedType?.BareName == Declaration.RuntimeContract.WorkTypeName))
            {
                // The Work's arguments, in the job expression or the call analysis lowered it to.
                var names = new HashSet<string>(comparer: StringComparer.Ordinal);
                AstWalker.WalkExpressions(root: init, visit: e =>
                {
                    if (e is IdentifierExpression id)
                    {
                        names.Add(item: id.Name);
                    }
                });
                reaches[key: v.Name] = (v.Location, names);
            }
            else if (n is CallExpression { Callee: MemberExpression { Object: IdentifierExpression work, MemberName: var verb } } call &&
                     CollectingVerbs.Contains(item: verb))
            {
                collected.Add(item: (work.Name, call.Location));
            }
        }

        if (reaches.Count == 0)
        {
            return;
        }

        foreach (object n in nodes)
        {
            if (n is not CallExpression { ResolvedRoutine: { } routine } call ||
                !(routine.RecoveryOf ?? routine).Annotations.Contains(value: "moves_block"))
            {
                continue;
            }

            foreach (Expression argument in call.Arguments)
            {
                Expression value = argument is NamedArgumentExpression named ? named.Value : argument;
                if (value is not StealExpression { Operand: IdentifierExpression moved })
                {
                    continue;
                }

                foreach ((string work, (SourceLocation made, HashSet<string> names)) in reaches)
                {
                    if (!names.Contains(item: moved.Name) || !Before(a: made, b: call.Location) ||
                        collected.Any(predicate: c => c.Work == work && Before(a: c.At, b: call.Location)))
                    {
                        continue;
                    }

                    report(code: SemanticDiagnosticCode.ThreadArgNotShareable,
                        message: $"'{moved.Name}' goes into the StampManager here, which moves it, while the Work " +
                                 $"'{work}' (line {made.Line}) was handed '{moved.Name}' or part of it and hasn't " +
                                 $"finished: what it holds would no longer reach '{moved.Name}'. Collect the Work first " +
                                 $"('{work}.retrieve()'), then hand '{moved.Name}' to the StampManager. A member value the Work " +
                                 "only needs a copy of can be put in a name first and that name handed to the Work.",
                        location: call.Location);
                }
            }
        }
    }

    private static bool Before(SourceLocation a, SourceLocation b)
    {
        return a.Line < b.Line || a.Line == b.Line && a.Column < b.Column;
    }

    private static string? OneThreadReason(Expression expr, Dictionary<string, string> held)
    {
        return expr switch
        {
            WorkExpression { OneThreadArgument: { } why } => why,
            CallExpression { WorkOneThreadArgument: { } why } => why,
            IdentifierExpression id when held.TryGetValue(key: id.Name, value: out string? why) => why,
            _ => null
        };
    }
}
