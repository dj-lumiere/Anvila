using Builder.Diagnostics;
using Builder.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Where a lambda bound to a token or an entity may not go. A lambda that reaches `me`, a `view()` / `modify()` token,
/// a local collection or a `for` element (<see cref="LambdaExpression.BoundTo"/>) is a token itself, bound to those:
/// it may sit in a local, be handed to a routine and be called where it is made, but it may not be kept in a member
/// variable or returned, since it would then outlive what it reaches (RV-S606 for a token, RV-S607 for an entity).
/// A lambda handed on to a Work is the one-thread rule's (RV-S632). Runs over the analyzed user bodies.
/// </summary>
internal sealed class BoundLambdaChecker(Action<SemanticError> report)
{
    /// <summary>Checks every routine of <paramref name="programs"/>.</summary>
    internal void Check(IEnumerable<Program> programs)
    {
        List<Program> list = programs.ToList();
        var userTypes = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (Program program in list)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                switch (node)
                {
                    case EntityDeclaration entity:
                        userTypes.Add(item: entity.Name);
                        break;
                    case RecordDeclaration record:
                        userTypes.Add(item: record.Name);
                        break;
                }
            }
        }

        foreach (Program program in list)
        {
            AstWalker.Walk(root: program,
                visit: node =>
                {
                    if (node is RoutineDeclaration { Body: { } body })
                    {
                        CheckRoutine(body: body, userTypes: userTypes);
                    }
                });
        }
    }

    private void CheckRoutine(Statement body, HashSet<string> userTypes)
    {
        var bound = new Dictionary<string, LambdaExpression>(comparer: StringComparer.Ordinal);
        AstWalker.Walk(root: body,
            visit: node =>
            {
                switch (node)
                {
                    case VariableDeclaration { Initializer: { } init } declaration:
                        if (BoundOf(value: init, bound: bound) is { } held)
                        {
                            bound[key: declaration.Name] = held;
                        }
                        else
                        {
                            bound.Remove(key: declaration.Name);
                        }

                        break;
                    case AssignmentStatement { Target: var target, Value: var value }:
                        Assigned(target: target, value: value, bound: bound);
                        break;
                    case BinaryExpression { Operator: BinaryOperator.Assign, Left: var left, Right: var right }:
                        Assigned(target: left, value: right, bound: bound);
                        break;
                    case ReturnStatement { Value: { } returned } when BoundOf(value: returned, bound: bound) is { } back:
                        Report(lambda: back, at: returned.Location, action: "return it");
                        break;
                    case CreatorExpression creator:
                        foreach ((string name, Expression field) in creator.MemberVariables)
                        {
                            if (BoundOf(value: field, bound: bound) is { } inField)
                            {
                                Report(lambda: inField,
                                    at: field.Location,
                                    action: $"keep it in the member variable '{name}'");
                            }
                        }

                        break;
                    case CallExpression call when CreatedUserType(call: call, userTypes: userTypes) is { } created:
                        foreach (Expression argument in call.Arguments)
                        {
                            Expression passed = argument is NamedArgumentExpression named ? named.Value : argument;
                            if (BoundOf(value: passed, bound: bound) is { } inCreated)
                            {
                                Report(lambda: inCreated,
                                    at: passed.Location,
                                    action: $"keep it in a '{created}'");
                            }
                        }

                        break;
                }
            });
    }

    /// <summary>An assignment: a local takes on the lambda it is given (or loses it), anything else keeps it.</summary>
    private void Assigned(Expression target, Expression value, Dictionary<string, LambdaExpression> bound)
    {
        LambdaExpression? held = BoundOf(value: value, bound: bound);
        if (target is IdentifierExpression local)
        {
            if (held != null)
            {
                bound[key: local.Name] = held;
            }
            else
            {
                bound.Remove(key: local.Name);
            }

            return;
        }

        if (held != null)
        {
            Report(lambda: held,
                at: value.Location,
                action: target is MemberExpression m
                    ? $"keep it in the member variable '{m.MemberName}'"
                    : "keep it in a place that outlives this routine");
        }
    }

    /// <summary>The name of the record or entity a program declares that <paramref name="call"/> builds, or null.</summary>
    private static string? CreatedUserType(CallExpression call, HashSet<string> userTypes)
    {
        if (call.ResolvedRoutine is { Kind: RoutineKind.Creator, OwnerType: { } owner })
        {
            return userTypes.Contains(item: owner.BareName) ? owner.BareName : null;
        }

        return call is { ResolvedRoutine: null, Callee: IdentifierExpression { Name: var name } } &&
               userTypes.Contains(item: name)
            ? name
            : null;
    }

    /// <summary>The lambda bound to a token or an entity that <paramref name="value"/> is: one written here, or a local
    /// holding one.</summary>
    private static LambdaExpression? BoundOf(Expression value, Dictionary<string, LambdaExpression> bound)
    {
        return value switch
        {
            LambdaExpression { BoundTo.Count: > 0 } lambda => lambda,
            IdentifierExpression id => bound.GetValueOrDefault(key: id.Name),
            _ => null
        };
    }

    private void Report(LambdaExpression lambda, SourceLocation at, string action)
    {
        List<BoundCapture> captures = lambda.BoundTo!;
        string names = string.Join(separator: " and ", values: captures.Select(selector: c => $"'{c.Name}'"));
        string what = string.Join(separator: ", ",
            values: captures.Select(selector: c => c.MadeAt is { } made
                ? $"'{c.Name}' (made on line {made.Line})"
                : $"'{c.Name}'"));
        bool token = captures.Any(predicate: c => c.Kind == "token");
        report(obj: new SemanticError(
            Code: token ? SemanticDiagnosticCode.LambdaCaptureToken : SemanticDiagnosticCode.LambdaCaptureRawEntity,
            Message: $"You capture {names} in this lambda and {action}, but the lambda is bound to {what} and lasts " +
                     "only as long as it. Use it where you made it, or hand it to a routine that calls it. To keep " +
                     "it, copy what it needs out first (`var n = me.n`, then capture 'n') or capture a handle that " +
                     "keeps the entity (`Retained[T]`, or `Guarded[T]` across jobs).",
            Location: at) { BoundTo = captures.Select(selector: c => c.Name).ToList() });
    }
}
