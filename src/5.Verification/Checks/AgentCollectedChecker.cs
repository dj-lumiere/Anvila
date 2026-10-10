using Builder.Diagnostics;
using SyntaxTree;

namespace Builder.Verification;

/// <summary>
/// Warns about an Agent bound to a variable that is started and then only left to its scope's end (RF-W009): the
/// end cancels it and waits for it to stop, so its work may be cut short and what it came to is lost. Taking its value
/// (`retrieve`, `waitfor`), ending it on purpose (`drop job`), or handing it on (an argument, `steal`, `return`) is
/// what a program that means it writes. Runs over the analyzed user bodies.
/// </summary>
internal sealed class AgentCollectedChecker(Action<SemanticWarningCode, string, SourceLocation> warn)
{
    private static readonly HashSet<string> StartingVerbs = new(comparer: StringComparer.Ordinal)
    {
        "start_now", "start_here", "start_apart"
    };

    /// <summary>The calls on an Agent that neither take what it came to nor hand it on.</summary>
    private static readonly HashSet<string> LeavingVerbs = new(comparer: StringComparer.Ordinal)
    {
        "start_now", "start_here", "start_apart", "cancel", "cancelled", "is_done", "is_running"
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
        var nodes = new List<object>();
        Collect(root: routine.Body, into: nodes);

        var agents = new Dictionary<string, VariableDeclaration>(comparer: StringComparer.Ordinal);
        foreach (object n in nodes)
        {
            if (n is VariableDeclaration { Initializer: { ResolvedType: { } type } } v &&
                SemanticVerifier.IsAgentType(type: type))
            {
                agents[key: v.Name] = v;
            }
        }

        if (agents.Count == 0)
        {
            return;
        }

        var started = new HashSet<string>(comparer: StringComparer.Ordinal);
        var leaving = new HashSet<IdentifierExpression>(comparer: ReferenceEqualityComparer.Instance);
        foreach (object n in nodes)
        {
            if (n is CallExpression { Callee: MemberExpression { Object: IdentifierExpression receiver, MemberName: var verb } } &&
                agents.ContainsKey(key: receiver.Name) && LeavingVerbs.Contains(item: verb))
            {
                leaving.Add(item: receiver);
                if (StartingVerbs.Contains(item: verb))
                {
                    started.Add(item: receiver.Name);
                }
            }
        }

        // Any other use of the name takes what it came to, ends it on purpose, or hands it on.
        var collected = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (object n in nodes)
        {
            if (n is IdentifierExpression id && agents.ContainsKey(key: id.Name) && !leaving.Contains(item: id))
            {
                collected.Add(item: id.Name);
            }
        }

        foreach ((string name, VariableDeclaration declaration) in agents)
        {
            if (started.Contains(item: name) && !collected.Contains(item: name))
            {
                warn(arg1: SemanticWarningCode.AgentNeverCollected,
                    arg2: $"'{name}' is started, but nothing takes what it came to: the end of its scope cancels it and " +
                          $"waits for it to stop. Take its value with '{name}.retrieve()', or end it on purpose with " +
                          $"'drop {name}'.",
                    arg3: declaration.Location);
            }
        }
    }

    /// <summary>Every node under <paramref name="root"/>, the call a <c>check</c>/<c>try</c> recovers included.</summary>
    private static void Collect(object root, List<object> into)
    {
        AstWalker.Walk(root: root,
            visit: n =>
            {
                into.Add(item: n);
                if (n is RecoveryExpression recovery)
                {
                    Collect(root: recovery.Inner, into: into);
                }
            });
    }
}
