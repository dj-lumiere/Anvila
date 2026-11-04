using Builder.Declaration;
using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// RV-S657: a <c>Viewing</c> promises that nothing reachable from it can be changed, so a value that holds a way to
/// change an entity (a stamp, a manager handle, a write token, or a record, tuple or variant with such a member
/// variable) is read in place there and never copied out of it. Copying one into a variable, an assignment, a return, a
/// call argument, a construction or a collection literal would hand out the way to change what the view reached.
/// <c>xs[0].target.fetch()</c> reads the stamp where it is and gives a read token. Runs over the analyzed user bodies,
/// where the language checks access tokens.
/// </summary>
internal sealed class ViewedCopyChecker(DiagnosticReporter report)
{
    private readonly HashSet<string> _viewedNames = new(comparer: StringComparer.Ordinal);

    /// <summary>Checks every routine of <paramref name="programs"/>.</summary>
    internal void Check(IEnumerable<SyntaxTree.Program> programs)
    {
        foreach (SyntaxTree.Program program in programs)
        {
            _viewedNames.Clear();
            Visit(node: program);
        }
    }

    private bool IsViewed(Expression? expression)
    {
        return ViewedValues.IsViewed(expression: expression, isViewedName: _viewedNames.Contains);
    }

    private void Visit(object node)
    {
        switch (node)
        {
            case RoutineDeclaration { ResolvedInfo: { } info } readOnly when ViewedValues.MeIsViewed(routine: info):
                // The `me` of a `@readonly` routine is a viewed place like a `Viewing` parameter.
                bool added = _viewedNames.Add(item: "me");
                foreach (object child in AstWalker.EnumerateChildren(node: readOnly))
                {
                    Visit(node: child);
                }

                if (added)
                {
                    _viewedNames.Remove(item: "me");
                }

                return;
            case WhenStatement whenStatement:
                Visit(node: whenStatement.Expression);
                VisitClauses(subject: whenStatement.Expression, clauses: whenStatement.Clauses);
                return;
            case WhenExpression { Expression: { } subject } whenExpression:
                Visit(node: subject);
                VisitClauses(subject: subject, clauses: whenExpression.Clauses);
                return;
            case VariableDeclaration { Initializer: { } initializer } declaration:
                CheckCopy(value: initializer, name: declaration.Name);
                break;
            case AssignmentStatement assignment:
                CheckCopy(value: assignment.Value, name: null);
                break;
            case ReturnStatement { Value: { } returned }:
                CheckCopy(value: returned, name: null);
                break;
            case CreatorExpression creator:
                foreach ((string _, Expression value) in creator.MemberVariables)
                {
                    CheckCopy(value: value, name: null);
                }

                break;
            case ListLiteralExpression list:
                CheckAll(values: list.Elements);
                break;
            case SetLiteralExpression set:
                CheckAll(values: set.Elements);
                break;
            case TupleLiteralExpression tuple:
                CheckAll(values: tuple.Elements);
                break;
            case CallExpression { ResolvedRoutine: { } callee } call:
                CheckArguments(call: call, callee: callee);
                break;
        }

        foreach (object child in AstWalker.EnumerateChildren(node: node))
        {
            Visit(node: child);
        }
    }

    /// <summary>The names a clause's pattern binds read the matched value where it is, so they are viewed while the
    /// clause's body is walked when the subject is.</summary>
    private void VisitClauses(Expression? subject, List<WhenClause> clauses)
    {
        bool viewed = IsViewed(expression: subject);
        foreach (WhenClause clause in clauses)
        {
            List<string> added = [];
            if (viewed)
            {
                foreach (string name in AstWalker.BoundNames(node: clause.Pattern))
                {
                    if (_viewedNames.Add(item: name))
                    {
                        added.Add(item: name);
                    }
                }
            }

            Visit(node: clause.Pattern);
            Visit(node: clause.Body);
            foreach (string name in added)
            {
                _viewedNames.Remove(item: name);
            }
        }
    }

    private void CheckAll(IEnumerable<Expression> values)
    {
        foreach (Expression value in values)
        {
            CheckCopy(value: value, name: null);
        }
    }

    /// <summary>The arguments a call hands to parameters that take a value. A token parameter takes the token itself,
    /// which reaches the value where it is.</summary>
    private void CheckArguments(CallExpression call, RoutineInfo callee)
    {
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            Expression argument = call.Arguments[index: i];
            string? parameterName = argument is NamedArgumentExpression named
                ? named.Name
                : i < callee.Parameters.Count
                    ? callee.Parameters[index: i].Name
                    : null;
            ParamInfo? parameter = callee.Parameters.FirstOrDefault(predicate: p => p.Name == parameterName);
            if (parameter == null || parameter.IsByReference ||
                parameter.Type.BareName is RuntimeContract.Viewing or RuntimeContract.Modifying)
            {
                continue;
            }

            CheckCopy(value: argument is NamedArgumentExpression wrapped ? wrapped.Value : argument, name: null);
        }
    }

    /// <summary>Reports <paramref name="value"/> when it copies a way to change an entity out of a viewed place.</summary>
    private void CheckCopy(Expression value, string? name)
    {
        if (value.ResolvedType is not { } type || ViewedValues.IsReadToken(type: type) ||
            value is not (IdentifierExpression or MemberExpression or IndexExpression or CallExpression) ||
            !IsViewed(expression: value) || ViewedValues.WayToChange(type: type) is not { } way)
        {
            return;
        }

        string root = ViewedValues.RootName(expression: value) ?? "the view";
        string what = name ?? Describe(value: value);
        bool bound = _viewedNames.Contains(item: root);
        string message = root == "me"
            ? $"'{what}' holds a way to change an entity ({way}), so it can't be copied out of 'me' in a '@readonly' " +
              $"routine (nothing reachable from 'me' can be changed there). Read it where it is in '{Describe(value: value)}', " +
              "or drop '@readonly' from the routine."
            : bound
            ? $"'{what}' holds a way to change an entity ({way}), so it can't be copied: '{root}' reads a value inside " +
              "a 'Viewing' (nothing reachable from a view can be changed). Read it where it is, or take the " +
              "container the 'when' matches as 'Modifying'."
            : $"'{what}' holds a way to change an entity ({way}), so it can't be copied out of '{root}', a 'Viewing' " +
              $"(nothing reachable from a view can be changed). Read it where it is in '{Describe(value: value)}', or " +
              $"take '{root}' as 'Modifying'.";
        report(code: SemanticDiagnosticCode.ViewedValueHoldsWayToChange,
            message: message,
            location: value.Location);
    }

    private static string Describe(Expression value)
    {
        return value switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression member => $"{Describe(value: member.Object)}.{member.MemberName}",
            IndexExpression element => $"{Describe(value: element.Object)}[...]",
            CallExpression { Callee: MemberExpression receiver } => $"{Describe(value: receiver.Object)}.{receiver.MemberName}(...)",
            _ => "this value"
        };
    }
}
