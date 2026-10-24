using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Instantiation;

/// <summary>
/// Failures that rise through routine values. A failure rises only through failable routines: a routine that is
/// not failable is a wall, and a failure inside it crashes there. A routine value whose type is written
/// <c>failable Callable[(T,), U]</c> may fail (a lambda whose body can fail, a failable routine named as a value),
/// and a call that hands one to such a parameter, or works on a value holding one (an iterator adapter holding the
/// lambda it calls later), can fail through it. So <c>try xs.select(transform: x =&gt; 10 // x).to[List]()</c> is
/// recovered, while the same expression with no keyword crashes on the failure.
/// </summary>
internal static class FailableValues
{
    /// <summary>
    /// Whether <paramref name="call"/> hands on or works on a routine value that may fail: its receiver or one of
    /// its arguments is one, or holds one (<see cref="RoutineValueCalls.MayHoldRoutineValue"/>).
    /// </summary>
    public static bool HandsFailableValue(CallExpression call)
    {
        if (call.Callee is MemberExpression { Object.ResolvedType: { } receiver } &&
            RoutineValueCalls.MayHoldRoutineValue(type: receiver))
        {
            return true;
        }

        return call.Arguments.Any(predicate: argument =>
            RoutineValueCalls.MayHoldRoutineValue(type: (argument is NamedArgumentExpression named
                ? named.Value
                : argument).ResolvedType));
    }

    /// <summary>
    /// Whether <paramref name="call"/> can fail where it stands, as a recovery keyword above it sees it: a call of a
    /// failable routine, a call through a routine value that may fail, or a call that hands on, or works on, a routine
    /// value that may fail. That is read from the call and the types alone: a routine value that may fail goes only to
    /// a <c>failable Callable[...]</c> (RF-S761), so a routine it reaches passes its failure on
    /// (<see cref="TypeRegistry.CanFailUnderRecovery"/> builds the variant that does).
    /// </summary>
    public static bool CallMayFail(CallExpression call, TypeRegistry registry)
    {
        if (RoutineValueCalls.ValueType(call: call) != null)
        {
            return true;
        }

        if (call.ResolvedRoutine is not { } routine)
        {
            return false;
        }

        return routine.IsFailable || routine.HasThrow || HandsFailableValue(call: call) ||
               routine.IsLambda && registry.CanFailUnderRecovery(routine: routine);
    }

    /// <summary>
    /// The first argument of <paramref name="call"/> that is a routine value that may fail given to a parameter whose
    /// routine type is not written <c>failable</c>, with that parameter's name, or null.
    /// </summary>
    public static (Expression argument, string parameter)? FailableValueToPlainParameter(CallExpression call,
        RoutineInfo routine)
    {
        IReadOnlyList<ParamInfo> parameters = routine.Parameters;
        int position = 0;
        foreach (Expression argument in call.Arguments)
        {
            ParamInfo? parameter = argument is NamedArgumentExpression named
                ? parameters.FirstOrDefault(predicate: p => p.Name == named.Name)
                : position < parameters.Count
                    ? parameters[index: position]
                    : null;
            position++;
            Expression value = argument is NamedArgumentExpression namedValue
                ? namedValue.Value
                : argument;
            if (parameter is { Type: RoutineTypeSymbol { IsFailable: false } } &&
                value.ResolvedType is RoutineTypeSymbol { IsFailable: true })
            {
                return (value, parameter.Name);
            }
        }

        return null;
    }
}
