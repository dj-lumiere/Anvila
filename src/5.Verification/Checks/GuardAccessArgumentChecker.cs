using Builder.Declaration;
using Builder.Diagnostics;
using SyntaxTree;

namespace Builder.Verification;

/// <summary>
/// A <c>Guarded</c>'s <c>g.view()</c> / <c>g.modify()</c> holds the access for one call: the builder takes it before
/// the statement and gives it up after. So such a call is written only as a call argument (or the receiver of a member
/// call, its <c>me</c>), in a statement of its own
/// (an expression statement, a <c>var</c>, an assignment, a <c>return</c> or a <c>throw</c>): anywhere else is RF-S652.
/// Passing one for a <c>Guarded</c> that an enclosing block of the routine has open is RF-S630. Runs over the analyzed
/// user bodies.
/// </summary>
internal sealed class GuardAccessArgumentChecker(DiagnosticReporter report)
{
    /// <summary>Checks every routine of <paramref name="programs"/>.</summary>
    internal void Check(IEnumerable<SyntaxTree.Program> programs)
    {
        foreach (SyntaxTree.Program program in programs)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                foreach (RoutineDeclaration routine in RoutinesOf(node: node))
                {
                    Walk(stmt: routine.Body, open: []);
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

    /// <summary>Checks a statement, with <paramref name="open"/> the paths of the Guarded handles the enclosing blocks
    /// of the routine have open.</summary>
    private void Walk(Statement? stmt, IReadOnlyList<(string Path, int Line)> open)
    {
        switch (stmt)
        {
            case null:
                return;
            case BlockStatement block:
                foreach (Statement s in block.Statements)
                {
                    Walk(stmt: s, open: open);
                }

                return;
            case IfStatement ifs:
                CheckOwn(expr: ifs.Condition, inOwnStatement: false, open: open);
                Walk(stmt: ifs.ThenStatement, open: open);
                Walk(stmt: ifs.ElseStatement, open: open);
                return;
            case WhileStatement w:
                CheckOwn(expr: w.Condition, inOwnStatement: false, open: open);
                Walk(stmt: w.Body, open: open);
                Walk(stmt: w.ThenBranch, open: open);
                return;
            case LoopStatement loop:
                Walk(stmt: loop.Body, open: open);
                Walk(stmt: loop.ThenBranch, open: open);
                return;
            case EachStatement each:
                CheckOwn(expr: each.Iterable, inOwnStatement: false, open: open);
                Walk(stmt: each.Body, open: open);
                Walk(stmt: each.ThenBranch, open: open);
                return;
            case WhenStatement whenStmt:
                CheckOwn(expr: whenStmt.Expression, inOwnStatement: false, open: open);
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    Walk(stmt: clause.Body, open: open);
                }

                return;
            case DangerStatement danger:
                Walk(stmt: danger.Body, open: open);
                return;
            case UsingStatement usingStmt:
                CheckOwn(expr: usingStmt.Resource, inOwnStatement: false, open: open);
                Walk(stmt: usingStmt.Body, open: open);
                return;
            case AccessStatement access:
            {
                var inner = new List<(string Path, int Line)>(collection: open);
                foreach (AccessTarget target in access.Targets)
                {
                    CheckOwn(expr: target.Target, inOwnStatement: false, open: open);
                    if (target.Target.ResolvedType is { } type && GuardAccess.TakesAccess(type: type) &&
                        AccessPaths.Of(expr: target.Target) is { } path)
                    {
                        inner.Add(item: (path, access.Location.Line));
                    }
                }

                Walk(stmt: access.Body, open: inner);
                Walk(stmt: access.ElseBody, open: open);
                return;
            }
            case ExpressionStatement or DeclarationStatement or AssignmentStatement or ReturnStatement
                or ThrowStatement:
                foreach (object child in AstWalker.EnumerateChildren(node: stmt) ?? [])
                {
                    if (child is Expression e)
                    {
                        CheckOwn(expr: e, inOwnStatement: true, open: open);
                    }
                    else if (child is VariableDeclaration { Initializer: { } init })
                    {
                        CheckOwn(expr: init, inOwnStatement: true, open: open);
                    }
                }

                return;
            default:
                foreach (object child in AstWalker.EnumerateChildren(node: stmt) ?? [])
                {
                    switch (child)
                    {
                        case Expression e:
                            CheckOwn(expr: e, inOwnStatement: false, open: open);
                            break;
                        case Statement s:
                            Walk(stmt: s, open: open);
                            break;
                    }
                }

                return;
        }
    }

    /// <summary>Checks the <c>g.view()</c> / <c>g.modify()</c> calls in one expression of a statement.</summary>
    private void CheckOwn(Expression expr, bool inOwnStatement, IReadOnlyList<(string Path, int Line)> open)
    {
        var nodes = new List<object>();
        AstWalker.Walk(root: expr, visit: n => nodes.Add(item: n));
        var arguments = new HashSet<Expression>(comparer: ReferenceEqualityComparer.Instance);
        foreach (CallExpression call in nodes.OfType<CallExpression>())
        {
            foreach (Expression argument in call.Arguments)
            {
                arguments.Add(item: argument is NamedArgumentExpression named ? named.Value : argument);
            }

            // The receiver of a member call is its `me` argument: `g.view().count()` holds the access for `count`.
            if (call.Callee is MemberExpression { Object: var receiver })
            {
                arguments.Add(item: receiver);
            }
        }

        // A member variable read through it holds the access for the read: `g.view().balance`.
        foreach (MemberExpression read in nodes.OfType<MemberExpression>())
        {
            arguments.Add(item: read.Object);
        }

        foreach (CallExpression call in nodes.OfType<CallExpression>())
        {
            if (GuardAccess.LockingReceiver(expression: call, isWrite: out bool isWrite) is not { } receiver)
            {
                continue;
            }

            string verb = isWrite ? "modify" : "view";
            string holder = AccessPaths.Of(expr: receiver) ?? "g";
            if (!arguments.Contains(item: call))
            {
                report(code: SemanticDiagnosticCode.GuardAccessOutsideArgument,
                    message: $"You use '{holder}.{verb}()' where it isn't passed to a call. Its access is held for one " +
                             $"call, so pass it to the routine that uses it ('f(x: {holder}.{verb}())' or " +
                             $"'{holder}.{verb}().count()'), or open a block for longer work: " +
                             $"'{(isWrite ? "modifying" : "viewing")} {holder} as x'.",
                    location: call.Location);
                continue;
            }

            if (!inOwnStatement)
            {
                report(code: SemanticDiagnosticCode.GuardAccessOutsideArgument,
                    message: $"You pass '{holder}.{verb}()' inside a condition or the head of a block. Its access is " +
                             "held for the statement that passes it, so make that a statement of its own: keep the " +
                             "result in a variable, then test the variable.",
                    location: call.Location);
                continue;
            }

            foreach ((string path, int line) in open)
            {
                if (path != holder)
                {
                    continue;
                }

                report(code: SemanticDiagnosticCode.GuardedOpenedAgain,
                    message: $"You pass '{holder}.{verb}()' inside a block (line {line}) that already has '{holder}' open. " +
                             "Waiting here would never end, since the block can't end while it waits, and going in " +
                             "would see that block's work half done. Pass the token the block gives instead.",
                    location: call.Location);
                break;
            }
        }
    }
}
