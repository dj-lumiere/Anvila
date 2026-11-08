using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// What a <c>Viewing</c> promises: nothing reachable from it can be changed, including an entity reached through a
/// stamp or a manager handle. Two questions follow. Does a value hold a way to change something
/// (<see cref="WayToChange"/>), and is an expression read from a viewed place (<see cref="IsViewed"/>)? A value that
/// holds a way to change is read in place from a view and never copied out of it.
/// </summary>
internal static class ViewedValues
{
    /// <summary>The type names whose values are a way to change an entity: a stamp, the manager handles and a write
    /// token.</summary>
    private static readonly IReadOnlySet<string> WayNames = new HashSet<string>(comparer: StringComparer.Ordinal)
    {
        RuntimeContract.StampedTypeName,
        RuntimeContract.Retained,
        RuntimeContract.Tracked,
        RuntimeContract.Guarded,
        RuntimeContract.Witnessed,
        RuntimeContract.Modifying
    };

    /// <summary>
    /// The part of <paramref name="type"/> that is a way to change an entity, as the source would name it:
    /// <c>Stamped[Node]</c> for a stamp itself, <c>target: Stamped[Node]</c> for the member variable of a record, tuple
    /// or variant that holds one (found through nested members, the instance's own types). Null when the value holds
    /// none. An entity is never copied, so it is not looked into.
    /// </summary>
    internal static string? WayToChange(TypeSymbol? type)
    {
        return WayToChange(type: type, seen: []);
    }

    private static string? WayToChange(TypeSymbol? type, HashSet<TypeSymbol> seen)
    {
        // A link never keeps its entity alive and is read through a view as a read-only link, so it may be copied out.
        if (type == null || type is EntityTypeSymbol || ImplicitToken.IsLink(type: type))
        {
            return null;
        }

        // A `Guarded` under the read-only policy only ever gives a read token (a `Text` holds one for its characters).
        if (WayNames.Contains(item: type.BareName) && !IsReadOnlyGuard(type: type))
        {
            return type.Name;
        }

        if (type is not RecordTypeSymbol record || !seen.Add(item: type))
        {
            return null;
        }

        foreach (TypeModel.Symbols.MemberVariableInfo member in record.MemberVariables)
        {
            if (WayToChange(type: member.Type, seen: seen) is { } inner)
            {
                return member.Type.BareName is not null && WayNames.Contains(item: member.Type.BareName)
                    ? $"{member.Name}: {inner}"
                    : $"{member.Name}.{inner}";
            }
        }

        if (record is VariantTypeSymbol variant)
        {
            foreach (VariantMemberInfo arm in variant.Members)
            {
                if (WayToChange(type: arm.Type, seen: seen) is { } inner)
                {
                    return inner;
                }
            }
        }

        return null;
    }

    private static bool IsReadOnlyGuard(TypeSymbol type)
    {
        return type is { BareName: RuntimeContract.Guarded or RuntimeContract.Witnessed, TypeArguments: [_, var policy] } &&
               policy.BareName == "ReadOnly";
    }

    /// <summary>
    /// Whether the <c>me</c> of <paramref name="routine"/> is a viewed place: a <c>@readonly</c> member routine
    /// promises to change nothing reachable from <c>me</c>, as a <c>Viewing</c> does. The routines of a handle type
    /// itself (<c>Retained.share</c>, <c>Stamped.fetch</c>) are the verbs that give a handle out, so their <c>me</c> is
    /// the handle and not a place a handle is reached through.
    /// </summary>
    internal static bool MeIsViewed(RoutineInfo? routine)
    {
        return routine is { IsReadOnly: true, IsCommon: false } &&
               !(routine.OwnerType?.BareName is { } owner && WayNames.Contains(item: owner));
    }

    /// <summary>Whether <paramref name="type"/> is a read token (<c>Viewing[T]</c>).</summary>
    internal static bool IsReadToken(TypeSymbol? type)
    {
        return type?.BareName is RuntimeContract.Viewing;
    }

    /// <summary>
    /// Whether <paramref name="expression"/> is read from a viewed place: a read token, a member variable or element
    /// of one (<c>links[0].target</c>), the result of a call made on one, or a name a <c>when</c> over one bound
    /// (<paramref name="isViewedName"/>).
    /// </summary>
    internal static bool IsViewed(Expression? expression, Func<string, bool> isViewedName)
    {
        return expression switch
        {
            null => false,
            { ResolvedType: { } type } when IsReadToken(type: type) => true,
            IdentifierExpression id => isViewedName(arg: id.Name),
            MemberExpression member => IsViewed(expression: member.Object, isViewedName: isViewedName),
            IndexExpression element => IsViewed(expression: element.Object, isViewedName: isViewedName),
            CallExpression { Callee: MemberExpression receiver } => IsViewed(expression: receiver.Object,
                isViewedName: isViewedName),
            _ => false
        };
    }

    /// <summary>The name of the read token a viewed expression starts at (<c>links</c> for <c>links[0].target</c>),
    /// or null when it starts at something else.</summary>
    internal static string? RootName(Expression? expression)
    {
        return expression switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression member => RootName(expression: member.Object),
            IndexExpression element => RootName(expression: element.Object),
            CallExpression { Callee: MemberExpression receiver } => RootName(expression: receiver.Object),
            _ => null
        };
    }
}
