using SyntaxTree;
using TypeModel.Symbols;

namespace Builder.Lowering.Passes;

/// <summary>
/// Puts the arguments of every call bound to a routine in the order of that routine's parameters. Named
/// arguments may be written in any order, and RazorForge evaluates arguments in parameter-declaration order,
/// so after this pass a backend binds the n-th argument to the n-th parameter and evaluates them as listed.
/// A named argument keeps its name, so a backend can check the binding. Semantic analysis has already written
/// every left-out default into the call, so a call names each parameter once. Runs at Phase 9 over every body
/// that reaches a backend, after <see cref="ConstructionLoweringPass"/> (which writes the calls of
/// <c>create</c> overloads with named arguments in field order).
/// </summary>
internal sealed class CallArgumentOrderPass : AstRewriter
{
    /// <summary>Orders the call arguments of one routine body in place.</summary>
    public static void Run(Statement body)
    {
        // A routine body is a block: its statement list is rewritten in place (the body itself is
        // referenced from several places and cannot be replaced).
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(
                message: $"A routine body must be a block, got {body.GetType().Name}.");
        }

        var pass = new CallArgumentOrderPass();
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    /// <inheritdoc/>
    protected override Expression VisitCall(CallExpression e)
    {
        var call = (CallExpression)base.VisitCall(e: e);
        return call.ResolvedRoutine is { } routine &&
               call.Arguments.Any(predicate: a => a is NamedArgumentExpression) &&
               InParameterOrder(arguments: call.Arguments, parameters: routine.Parameters) is { } ordered
            ? call with { Arguments = ordered }
            : call;
    }

    /// <summary>
    /// The argument a backend binds to parameter <paramref name="slot"/> of <paramref name="routine"/>: the one at
    /// that position, checked not to name another parameter. Null when the call has no argument there.
    /// </summary>
    public static Expression? ArgumentInSlot(List<Expression> arguments, RoutineInfo routine, int slot)
    {
        if (slot >= arguments.Count)
        {
            return null;
        }

        Expression argument = arguments[index: slot];
        string parameter = routine.Parameters[index: slot].Name;
        return argument is NamedArgumentExpression named && named.Name != parameter &&
               routine.Parameters.Any(predicate: p => p.Name == named.Name)
            ? throw new InvalidOperationException(
                message: $"The argument '{named.Name}' of a call to '{routine.Name}' reached a backend in the " +
                         $"slot of parameter '{parameter}'.")
            : argument;
    }

    /// <summary>The arguments in parameter order, or null when they already are or do not bind one to one.</summary>
    private static List<Expression>? InParameterOrder(List<Expression> arguments, List<ParamInfo> parameters)
    {
        if (arguments.Count != parameters.Count)
        {
            return null;
        }

        var ordered = new Expression?[parameters.Count];
        for (int i = 0; i < arguments.Count; i++)
        {
            int slot = arguments[index: i] is NamedArgumentExpression named
                ? parameters.FindIndex(match: p => p.Name == named.Name)
                : -1;
            // A positional argument, or one named for no parameter (a builder-written call naming another
            // overload's parameter), binds to its own position.
            if (slot < 0)
            {
                slot = i;
            }

            if (ordered[slot] != null)
            {
                return null;
            }

            ordered[slot] = arguments[index: i];
        }

        return ordered.SequenceEqual(second: arguments)
            ? null
            : [.. ordered!];
    }
}
