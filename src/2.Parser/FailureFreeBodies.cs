using SyntaxTree;

namespace Builder.Parser;

/// <summary>
/// Tells, from its text alone, that a routine body has nothing that can fail: it is empty (the builder writes it), or
/// it only names values, reads member variables and calls LLVM intrinsics (<c>return LLVM::add(a: me, b: you)</c>,
/// in a <c>danger</c> block too).
/// Such a routine has no failure to pass to its caller, so a <c>try</c> above it never needs its recovery variant.
/// Anything else (an operator, a subscript, a call of a routine, a <c>throw</c>) may fail.
/// </summary>
internal static class FailureFreeBodies
{
    /// <summary>Whether <paramref name="body"/> has nothing that can fail.</summary>
    public static bool CannotFail(Statement body)
    {
        bool mayFail = false;
        AstWalker.Walk(root: body,
            visit: node =>
            {
                mayFail |= node switch
                {
                    BlockStatement or ReturnStatement or ExpressionStatement or IfStatement or NothingStatement
                        or DangerStatement => false,
                    DeclarationStatement { Declaration: VariableDeclaration } => false,
                    VariableDeclaration => false,
                    LiteralExpression or IdentifierExpression or MemberExpression or NamedArgumentExpression => false,
                    TupleLiteralExpression => false,
                    CallExpression { Callee: IdentifierExpression { Realm: "LLVM" } } => false,
                    // `LLVM::load[T](me)`: the intrinsic with its type arguments, read before its brackets are sorted out.
                    BracketAccessExpression { Object: IdentifierExpression { Realm: "LLVM" }, CallArgs: not null } => false,
                    GenericMemberRoutineCallExpression { Object: IdentifierExpression { Realm: "LLVM" } } => false,
                    TypeExpression => false,
                    Statement or Expression => true,
                    _ => false
                };
            });
        return !mayFail;
    }
}
