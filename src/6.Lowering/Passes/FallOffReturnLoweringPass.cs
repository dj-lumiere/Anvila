using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Ends every body with a <c>return</c>, so a backend never decides what falling off the end means: a body whose
/// last statement is not a <c>return</c> gets <c>return</c> (a routine returning nothing) or <c>return</c> of the
/// zero value of its return type. Where control cannot reach the end (every path already returned), the added
/// statement is never reached and a backend drops it. Runs at Phase 9 over every body that reaches a backend,
/// with the routine whose body it is.
/// </summary>
internal static class FallOffReturnLoweringPass
{
    /// <summary>Adds the closing return to <paramref name="body"/> when it does not end in one.</summary>
    public static void Run(Statement body, RoutineInfo? routine)
    {
        if (routine is null || body is not BlockStatement block || block.Statements is [.., ReturnStatement])
        {
            return;
        }

        Expression? value = routine.ReturnType is null or { IsNone: true } or RecordTypeSymbol { BackendType: "void" }
            ? null
            : new ZeroValueExpression(Location: block.Location) { ResolvedType = routine.ReturnType };
        block.Statements.Add(item: new ReturnStatement(Value: value, Location: block.Location));
    }
}
