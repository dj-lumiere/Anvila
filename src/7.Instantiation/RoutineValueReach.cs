using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Instantiation;

/// <summary>
/// Whether a library routine's body reaches a routine value, read from the body as written (a library body may
/// not be analyzed yet): it calls a routine value it was handed or holds in a field (<c>predicate(item)</c>,
/// <c>me.transform(item)</c>), hands one on to another routine (<c>me.insertion_sort_by(compare: compare)</c>),
/// or steps an iterator (<c>each item in me</c>, <c>source.emit()</c>), which may be an adapter calling a lambda.
/// A library routine that is not failable is recovered beneath <c>try</c>/<c>grab</c>/<c>lookup</c> only through
/// the routine values it reaches (see <see cref="RoutineValueCalls"/>).
/// </summary>
internal static class RoutineValueReach
{
    private enum Reach
    {
        None,
        Steps,
        Calls
    }

    /// <summary>Whether <paramref name="body"/> calls or hands on a routine value, or steps an iterator.</summary>
    public static bool Reaches(RoutineInfo routine, Statement body)
    {
        return Scan(routine: routine, body: body) != Reach.None;
    }

    /// <summary>
    /// Whether a call of <paramref name="routine"/> can fail beneath it through a routine value: its body calls
    /// or hands on one, or steps an iterator while the types it works on may hold one (a concrete instance over
    /// plain values cannot).
    /// </summary>
    public static bool MayFail(RoutineInfo routine, Statement body)
    {
        return Scan(routine: routine, body: body) switch
        {
            Reach.Calls => true,
            Reach.Steps => RoutineValueCalls.MayHoldRoutineValue(type: routine.OwnerType) ||
                           routine.TypeArguments?.Any(predicate: RoutineValueCalls.MayHoldRoutineValue) == true ||
                           routine.Parameters.Any(predicate: p => RoutineValueCalls.MayHoldRoutineValue(type: p.Type)),
            _ => false
        };
    }

    private static Reach Scan(RoutineInfo routine, Statement body)
    {
        var values = new HashSet<string>(
            collection: routine.Parameters.Where(predicate: p => p.Type is RoutineTypeSymbol).Select(selector: p => p.Name),
            comparer: StringComparer.Ordinal);
        List<MemberVariableInfo>? ownerFields = routine.OwnerType switch
        {
            EntityTypeSymbol entity => entity.MemberVariables,
            RecordTypeSymbol record => record.MemberVariables,
            _ => null
        };
        var fields = new HashSet<string>(
            collection: ownerFields?.Where(predicate: f => f.Type is RoutineTypeSymbol).Select(selector: f => f.Name) ?? [],
            comparer: StringComparer.Ordinal);
        var scanner = new Scanner(values: values, fields: fields);
        scanner.VisitStatement(stmt: body);
        return scanner.Found;
    }

    private sealed class Scanner(HashSet<string> values, HashSet<string> fields) : AstRewriter
    {
        public Reach Found { get; private set; }

        public override Statement VisitStatement(Statement stmt)
        {
            if (Found == Reach.Calls)
            {
                return stmt;
            }

            if (stmt is EachStatement)
            {
                Found = Reach.Steps;
            }

            return base.VisitStatement(stmt: stmt);
        }

        public override Expression VisitExpression(Expression expr)
        {
            if (Found == Reach.Calls)
            {
                return expr;
            }

            switch (expr)
            {
                case CallExpression call when IsValue(expr: call.Callee) ||
                                              call.Arguments.Any(predicate: a => IsValue(
                                                  expr: a is NamedArgumentExpression named ? named.Value : a)):
                    Found = Reach.Calls;
                    return expr;
                case CallExpression { Callee: MemberExpression { MemberName: "emit" } }:
                    Found = Reach.Steps;
                    break;
            }

            return base.VisitExpression(expr: expr);
        }

        /// <summary>A routine value the routine was handed or holds in a field, or one the analysis typed.</summary>
        private bool IsValue(Expression expr)
        {
            return expr switch
            {
                IdentifierExpression { ResolvedRoutine: null } id => values.Contains(item: id.Name) ||
                                                                     id.ResolvedType is RoutineTypeSymbol,
                MemberExpression { Object: IdentifierExpression { Name: "me" }, MemberName: var field } =>
                    fields.Contains(item: field),
                _ => false
            };
        }
    }
}
