using SyntaxTree;

namespace Builder.Declaration;

/// <summary>Finds, in a routine body as written, an assignment to a member variable of <c>me</c>.</summary>
internal static class MemberVariableWrites
{
    /// <summary>True when <paramref name="body"/> assigns <c>me.x</c> (plainly or with a compound operator) anywhere
    /// it can reach, nested blocks and branches included.</summary>
    public static bool WritesMe(Statement? body)
    {
        return body switch
        {
            null => false,
            AssignmentStatement assign => IsMeMember(target: assign.Target),
            ExpressionStatement { Expression: BinaryExpression { Operator: BinaryOperator.Assign } binary } =>
                IsMeMember(target: binary.Left),
            BlockStatement block => block.Statements.Any(predicate: WritesMe),
            IfStatement ifs => WritesMe(body: ifs.ThenStatement) || WritesMe(body: ifs.ElseStatement),
            WhileStatement loop => WritesMe(body: loop.Body) || WritesMe(body: loop.ThenBranch),
            EachStatement each => WritesMe(body: each.Body) || WritesMe(body: each.ThenBranch),
            LoopStatement loop => WritesMe(body: loop.Body),
            WhenStatement whenStmt => whenStmt.Clauses.Any(predicate: c => WritesMe(body: c.Body)),
            DangerStatement danger => WritesMe(body: danger.Body),
            UsingStatement usingStmt => WritesMe(body: usingStmt.Body) || WritesMe(body: usingStmt.FallbackBody),
            _ => false
        };
    }

    private static bool IsMeMember(Expression target)
    {
        return target is MemberExpression { Object: IdentifierExpression { Name: "me" } };
    }
}
