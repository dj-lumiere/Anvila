using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// A <c>Viewing[T]</c> or <c>Modifying[T]</c> parameter takes whatever can give that access, converted at the
/// call: the value itself (an entity through <c>.view()</c>/<c>.modify()</c>, a record or a type parameter through
/// the address of the place it is in), or a handle or token over a <c>T</c> that allows as much
/// (<c>Retained[T]</c>, <c>Roamed[T]</c>, <c>Amending[T]</c>, <c>Consulting[T]</c> for reading, <c>Modifying[T]</c>
/// for reading). The conversion only narrows what can be done, so it changes neither the value nor the cost.
/// </summary>
internal static class ImplicitToken
{
    /// <summary>The token a parameter of <paramref name="parameter"/>'s type takes (<c>Viewing</c> or
    /// <c>Modifying</c>) and the type it gives access to, or false for any other parameter.</summary>
    public static bool IsTokenParameter(TypeSymbol parameter, out string tokenKind, out TypeSymbol target)
    {
        tokenKind = parameter.BareName;
        if (tokenKind is RuntimeContract.Viewing or RuntimeContract.Modifying &&
            parameter.TypeArguments is [{ } inner])
        {
            target = inner;
            return true;
        }

        target = parameter;
        return false;
    }

    /// <summary>
    /// The handle or token kind <paramref name="source"/> is (<c>Retained</c>, <c>Amending</c>, ...) over a value of
    /// <paramref name="target"/>'s type, when it may stand for a token of <paramref name="tokenKind"/>, or null.
    /// </summary>
    public static string? HandleOver(TypeSymbol source, string tokenKind, TypeSymbol target)
    {
        string handle = source.BareName;
        bool allowed = tokenKind == RuntimeContract.Viewing
            ? handle is RuntimeContract.Retained or RuntimeContract.Roamed or RuntimeContract.Modifying
                or RuntimeContract.Amending or RuntimeContract.Consulting
            : handle is RuntimeContract.Retained or RuntimeContract.Roamed or RuntimeContract.Amending;
        return allowed && source.TypeArguments is [{ } inner, ..] && Matches(value: inner, target: target)
            ? handle
            : null;
    }

    /// <summary>Whether an argument of <paramref name="source"/>'s type converts to a parameter of
    /// <paramref name="parameter"/>'s type (a token parameter given a value, a handle or a wider token).</summary>
    public static bool Accepts(TypeSymbol source, TypeSymbol parameter)
    {
        if (!IsTokenParameter(parameter: parameter, tokenKind: out string tokenKind, target: out TypeSymbol target))
        {
            return false;
        }

        return Matches(value: source, target: target) ||
               HandleOver(source: source, tokenKind: tokenKind, target: target) != null;
    }

    /// <summary>Whether a value of <paramref name="value"/>'s type is a <paramref name="target"/>. Inside a generic
    /// type's own routines <c>me</c> is the definition (<c>List</c>) where a parameter names it over its own
    /// parameters (<c>List[T]</c>).</summary>
    public static bool Matches(TypeSymbol value, TypeSymbol target)
    {
        if (value.FullName == target.FullName || value.Name == target.Name)
        {
            return true;
        }

        return value.BareName == target.BareName &&
               (OverOwnParameters(definition: value, resolution: target) ||
                OverOwnParameters(definition: target, resolution: value));
    }

    private static bool OverOwnParameters(TypeSymbol definition, TypeSymbol resolution)
    {
        return definition.IsGenericDefinition && resolution.TypeArguments is { Count: > 0 } args &&
               args.Count == definition.GenericParameters!.Count &&
               args.All(predicate: a => a is GenericParameterTypeSymbol);
    }
}
