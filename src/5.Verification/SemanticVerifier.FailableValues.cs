using Builder.Diagnostics;
using Builder.Instantiation;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// A routine value that may fail goes only where its type says so. A lambda whose body can fail where it stands (a
/// checked operator, a subscript, a call of a failable routine) and a failable routine named as a value have a
/// <c>failable Callable[...]</c> type, and a parameter or a member variable that takes one is written
/// <c>failable Callable[(T,), U]</c>. A call that hands one to such a parameter can fail through it
/// (<see cref="FailableValues"/>). Handing one to a plain <c>Callable[...]</c> is RF-S761: the routine receiving it is
/// a wall, and the value's failure would crash inside it with no way to receive it.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>Reports RF-S761 for each routine value that may fail handed to a plain routine type in
    /// <paramref name="body"/>, an analyzed routine body.</summary>
    private void CheckFailableValuesGoWhereMarked(Statement body)
    {
        AstWalker.WalkExpressions(root: body, visit: expression =>
        {
            switch (expression)
            {
                case CallExpression { ResolvedRoutine: { IsRecoveryVariant: false } routine } call
                    when FailableValues.FailableValueToPlainParameter(call: call, routine: routine) is
                        { } found:
                    ReportFailableValueToPlain(value: found.argument, slot: found.parameter, routine: routine.Name);
                    break;
                case CreatorExpression { ResolvedType: { } created } creator:
                    List<MemberVariableInfo>? fields = created switch
                    {
                        RecordTypeSymbol record => record.MemberVariables,
                        EntityTypeSymbol entity => entity.MemberVariables,
                        _ => null
                    };
                    foreach ((string name, Expression value) in creator.MemberVariables)
                    {
                        if (fields?.FirstOrDefault(predicate: f => f.Name == name) is
                                { Type: RoutineTypeSymbol { IsFailable: false } } &&
                            value.ResolvedType is RoutineTypeSymbol { IsFailable: true })
                        {
                            ReportFailableValueToPlain(value: value, slot: name, routine: created.Name);
                            break;
                        }
                    }

                    break;
            }
        });
    }

    private void ReportFailableValueToPlain(Expression value, string slot, string routine)
    {
        string what = value is LambdaExpression
            ? "This lambda can fail (its body has a checked operator, a subscript or a call of a failable routine)"
            : "This routine value can fail";
        ReportError(code: SemanticDiagnosticCode.FailableValueToPlainParameter,
            message: $"{what}, and you handed it to '{slot}' of '{routine}', whose routine type is not failable. " +
                     $"'{routine}' could not pass the failure on, so it would crash there. Write '{slot}' as " +
                     "'failable Callable[...]' if the routine is yours, or hand it a routine value that cannot fail " +
                     "(wrapping, clamping or float arithmetic, or a routine that is not failable).",
            location: value.Location);
    }
}
