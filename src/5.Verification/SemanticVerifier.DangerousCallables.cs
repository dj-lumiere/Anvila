using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// A routine value taken from a dangerous routine is a dangerous callable: a call of it is as dangerous as a call of
/// the routine, so the builder keeps it where every call of it is written inside <c>danger</c> or a dangerous routine.
/// <list type="bullet">
/// <item>It goes only to a C routine's <c>Callable</c> parameter, to a dangerous routine's <c>Callable</c> parameter, or
/// into a local variable, which is a dangerous callable from then on.</item>
/// <item>A dangerous routine's own <c>Callable</c> parameters are dangerous callables in its body.</item>
/// <item>It is called only inside <c>danger</c> or a dangerous routine.</item>
/// </list>
/// Any other place (a plain routine's argument, a member variable, a collection, a result, a lambda's value) is a plain
/// routine value that can be called anywhere, so putting it there is RF-S609.
/// <para>Each use is noted when its name is read and let go when the statement around it puts it in one of the places
/// above. A use still noted when its statement ends went somewhere else.</para>
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>The reads of dangerous callables the current statement has not put anywhere yet, each with what it
    /// reads: the dangerous routine, or the variable that holds one.</summary>
    private readonly Dictionary<IdentifierExpression, (RoutineInfo? Routine, VariableInfo? Variable)>
        _pendingDangerousCallables = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Notes a read of the dangerous routine <paramref name="routine"/> as a value.</summary>
    private void NoteDangerousRoutineValue(IdentifierExpression id, RoutineInfo routine)
    {
        if (!_isInCompilerGeneratedBody)
        {
            _pendingDangerousCallables[key: id] = (routine, null);
        }
    }

    /// <summary>Notes a read of a variable that holds a dangerous callable.</summary>
    private void NoteDangerousCallableRead(IdentifierExpression id, VariableInfo variable)
    {
        if (variable.IsDangerousCallable && !_isInCompilerGeneratedBody)
        {
            _pendingDangerousCallables[key: id] = (null, variable);
        }
    }

    /// <summary>Whether <paramref name="value"/> is a noted read of a dangerous callable. Lets it go when it is.</summary>
    private bool TakeDangerousCallable(Expression value)
    {
        return UnwrapNamedArgument(argument: value) is IdentifierExpression id &&
               _pendingDangerousCallables.Remove(key: id);
    }

    /// <summary>A dangerous routine's <c>Callable</c> parameters are dangerous callables in its body.</summary>
    private void MarkDangerousCallableParameters(RoutineInfo routineInfo)
    {
        if (!routineInfo.IsDangerous)
        {
            return;
        }

        foreach (ParamInfo param in routineInfo.Parameters)
        {
            if (param.Type is RoutineTypeSymbol && _registry.LookupVariable(name: param.Name) is { } variable)
            {
                variable.IsDangerousCallable = true;
            }
        }
    }

    /// <summary>A local variable given a dangerous callable (<c>var f = raw</c>, <c>f = raw</c>) holds one from then on.</summary>
    private void TakeDangerousCallableIntoLocal(string name, Expression value)
    {
        if (TakeDangerousCallable(value: value) && _registry.LookupVariable(name: name) is
                { IsGlobal: false, IsPreset: false } variable)
        {
            variable.IsDangerousCallable = true;
        }
    }

    /// <summary>A C routine's or a dangerous routine's <c>Callable</c> parameter takes a dangerous callable.</summary>
    private void TakeDangerousCallableArgument(RoutineInfo routine, TypeSymbol paramType, Expression argument)
    {
        if ((routine.IsForeign || routine.IsDangerous) && paramType is RoutineTypeSymbol)
        {
            TakeDangerousCallable(value: argument);
        }
    }

    /// <summary>A call of a variable that holds a dangerous callable is written inside <c>danger</c> or a dangerous
    /// routine.</summary>
    private void ValidateDangerousCallableCall(Expression callee)
    {
        if (callee is not IdentifierExpression id ||
            !_pendingDangerousCallables.Remove(key: id, value: out (RoutineInfo? Routine, VariableInfo? Variable) read) ||
            InDangerBlock)
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.DangerousCallOutsideDangerBlock,
            message:
            $"You call '{id.Name}', which holds a routine value from a dangerous routine{OfParameter(read: read)}, so " +
            "the call is dangerous like a call of that routine. Write it inside a 'danger' block or a dangerous routine.",
            location: id.Location);
    }

    /// <summary>Reports every read of a dangerous callable that went to a place for plain routine values.</summary>
    private void ReportEscapedDangerousCallables()
    {
        if (_pendingDangerousCallables.Count == 0)
        {
            return;
        }

        foreach ((IdentifierExpression id, (RoutineInfo? routine, VariableInfo? variable)) in
                 _pendingDangerousCallables.OrderBy(keySelector: p => p.Key.Location.Line)
                                           .ThenBy(keySelector: p => p.Key.Location.Column))
        {
            string what = routine != null
                ? $"You take the dangerous routine '{routine.Name}' as a value"
                : $"'{id.Name}' holds a routine value from a dangerous routine{OfParameter(read: (routine, variable))}";
            ReportError(code: SemanticDiagnosticCode.DangerousCallOutsideDangerBlock,
                message:
                $"{what}, and this puts it where a plain routine value goes, which can be called anywhere, outside any " +
                "'danger' block. A routine value from a dangerous routine goes only to a C routine's 'Callable' " +
                "parameter, to a dangerous routine's 'Callable' parameter, or into a local variable. Pass it to one of " +
                "those, or write a plain routine that calls it inside 'danger' and use that routine instead.",
                location: id.Location);
        }

        _pendingDangerousCallables.Clear();
    }

    private static string OfParameter((RoutineInfo? Routine, VariableInfo? Variable) read)
    {
        return read.Variable is { IsParameter: true }
            ? " (a 'Callable' parameter of a dangerous routine is one)"
            : "";
    }
}
