using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// A <c>Viewing[T]</c> or <c>Modifying[T]</c> parameter takes whatever can give that access, converted at the
/// call: the value itself (an entity through <c>.view()</c>/<c>.modify()</c>, a record or a type parameter through
/// the address of the place it is in), or a handle or token over a <c>T</c> that allows as much
/// (<c>Retained[T]</c>, <c>Roamed[T]</c>, <c>Modifying[T]</c>
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
    /// The handle or token kind <paramref name="source"/> is (<c>Retained</c>, <c>Modifying</c>, ...) over a value of
    /// <paramref name="target"/>'s type, when it may stand for a token of <paramref name="tokenKind"/>, or null.
    /// </summary>
    public static string? HandleOver(TypeSymbol source, string tokenKind, TypeSymbol target)
    {
        string handle = source.BareName;
        bool allowed = tokenKind == RuntimeContract.Viewing
            ? handle is RuntimeContract.Retained or RuntimeContract.Roamed or RuntimeContract.Modifying
            : handle is RuntimeContract.Retained or RuntimeContract.Roamed;
        return allowed && source.TypeArguments is [{ } inner, ..] && Matches(value: inner, target: target)
            ? handle
            : null;
    }

    /// <summary>Whether an argument of <paramref name="source"/>'s type converts to a parameter of
    /// <paramref name="parameter"/>'s type (a token parameter given a value, a handle or a wider token).</summary>
    public static bool Accepts(TypeSymbol source, TypeSymbol parameter)
    {
        if (LinkMade(source: source, target: parameter) != null)
        {
            return true;
        }

        if (!IsTokenParameter(parameter: parameter, tokenKind: out string tokenKind, target: out TypeSymbol target))
        {
            return false;
        }

        return Matches(value: source, target: target) ||
               HandleOver(source: source, tokenKind: tokenKind, target: target) != null;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a link: an optional token on an entity (<c>Modifying[Node]?</c>,
    /// <c>Viewing[Node]?</c>). A member variable, a collection element, a global or a local may keep one, and it never
    /// keeps its entity alive: once the entity ended, taking it gives none.
    /// </summary>
    public static bool IsLink(TypeSymbol? type)
    {
        return type is { BareName: RuntimeContract.OptionalCarrier, TypeArguments: [{ } token] } &&
               LinkedEntity(token: token) != null;
    }

    /// <summary>The entity type a token is on when a link may hold it (<c>Node</c> of <c>Modifying[Node]</c>), or null
    /// for any other type, a token on a record or on an entity laid out in place included.</summary>
    public static EntityTypeSymbol? LinkedEntity(TypeSymbol token)
    {
        return token.BareName is RuntimeContract.Viewing or RuntimeContract.Modifying &&
               token.TypeArguments is [EntityTypeSymbol entity] && entity is not { IsGenericDefinition: true }
            ? entity
            : null;
    }

    /// <summary>
    /// The routine that makes the token a link of <paramref name="target"/>'s type (<c>Modifying[E]?</c> or
    /// <c>Viewing[E]?</c>) is made from, given a value of <paramref name="source"/>'s type, or null when it takes the value
    /// as it is (a token of its own kind) or not at all: the entity itself gives <c>modify()</c> / <c>view()</c>, a
    /// <c>Modifying[E]</c> into a <c>Viewing[E]?</c> gives <c>view()</c>. A place that keeps a link takes the entity the
    /// way a token parameter does, and the builder writes the call (LinkConversionPass).
    /// </summary>
    public static string? LinkMade(TypeSymbol source, TypeSymbol target)
    {
        if (!IsLink(type: target))
        {
            return null;
        }

        TypeSymbol token = target.TypeArguments![index: 0];
        EntityTypeSymbol entity = LinkedEntity(token: token)!;
        bool reads = token.BareName == RuntimeContract.Viewing;
        if (source.Category == TypeModel.Enums.TypeCategory.Entity && Matches(value: source, target: entity))
        {
            return reads ? "view" : "modify";
        }

        return reads && source is { BareName: RuntimeContract.Modifying, TypeArguments: [{ } inner] } &&
               Matches(value: inner, target: entity)
            ? "view"
            : null;
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
