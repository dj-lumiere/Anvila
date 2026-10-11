using SyntaxTree;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// A routine whose own bracket names types instead of parameters (<c>routine S64.to![U8]() -> U8</c>) is a
/// specialization: one routine of a family that a call picks by its type argument (<c>n.to[U8]()</c>). Both
/// registration paths (the library loader and the signature resolver) read the bracket through here.
/// </summary>
internal static class RoutineSpecialization
{
    /// <summary>
    /// The concrete types <paramref name="routine"/>'s own bracket names, or null when it declares generic
    /// parameters (or none). An entry counts as a type when <paramref name="resolve"/> finds one, and the
    /// bracket is a specialization only when every own entry is a type: a mix is a generic routine. A receiver's
    /// leaves (<c>T</c> of <c>routine T.to[S32]()</c>) are the receiver's, not the bracket's. The names move from
    /// <see cref="RoutineDeclaration.GenericParameters"/> to <see cref="RoutineDeclaration.SpecializedTypeArguments"/>,
    /// so asking again (a second realm registering the same declaration) gives the same answer.
    /// </summary>
    public static List<TypeSymbol>? Apply(RoutineDeclaration routine, HashSet<string> receiverLeaves,
        Func<string, TypeSymbol?> resolve, Func<TypeExpression, TypeSymbol?>? resolveExpression = null)
    {
        if (routine.SpecializedTypeArguments is { Count: > 0 } done)
        {
            return done.Select(selector: t => t.KnownType ?? ResolveEntry(name: t.Name, resolve: resolve,
                           resolveExpression: resolveExpression, location: routine.Location))
                       .OfType<TypeSymbol>()
                       .ToList();
        }

        // Only a member routine: the bracket of a creator on a specialized type (`routine List[Byte](...)`) is its
        // type's.
        if (string.IsNullOrEmpty(value: routine.MemberRoutineName))
        {
            return null;
        }

        // A receiver that is a plain type (`routine Text.to[Text]()`) brings no parameters of its own, so a bracket
        // name equal to its name is still the bracket's.
        bool plainReceiver = routine.OwnerName is { } ownerName && receiverLeaves.All(predicate: leaf => leaf == ownerName) &&
                             resolve(arg: ownerName) is { IsGenericDefinition: false } and not GenericParameterTypeSymbol;
        List<string> own = routine.GenericParameters?
                                  .Where(predicate: name => plainReceiver ||
                                                            !receiverLeaves.Contains(item: name) &&
                                                            name != routine.OwnerName)
                                  .ToList() ?? [];
        if (own.Count == 0)
        {
            return null;
        }

        // A name a parameter's type mentions is a generic parameter even where a type of that name exists
        // (`routine identity[T](x: T)` next to a `record T`): its identity is its slot. A specialization's
        // argument shows only in what the routine gives back (`to[U8]() -> U8`).
        var types = new List<TypeSymbol>(capacity: own.Count);
        foreach (string name in own)
        {
            if (routine.Parameters.Any(predicate: p => Mentions(type: p.Type, name: name)) ||
                ResolveEntry(name: name, resolve: resolve, resolveExpression: resolveExpression,
                    location: routine.Location) is not { } type || type is GenericParameterTypeSymbol)
            {
                return null;
            }

            types.Add(item: type);
        }

        routine.SpecializedTypeArguments = own.Zip(second: types,
                                                   resultSelector: (name, type) => new TypeExpression(Name: name,
                                                       GenericArguments: null,
                                                       Location: routine.Location) { KnownType = type })
                                              .ToList();
        List<string> remaining = routine.GenericParameters!.Where(predicate: name => !own.Contains(item: name))
                                        .ToList();
        routine.GenericParameters = remaining.Count > 0
            ? remaining
            : null;
        return types;
    }

    /// <summary>
    /// The type a bracket entry names: a plain name by <paramref name="resolve"/>, an entry with type arguments or a
    /// realm (`SF::Core.List[T]`, the serialized form the parser keeps) by <paramref name="resolveExpression"/>
    /// over the parsed expression. Null when the entry is not a type.
    /// </summary>
    private static TypeSymbol? ResolveEntry(string name, Func<string, TypeSymbol?> resolve,
        Func<TypeExpression, TypeSymbol?>? resolveExpression, SourceLocation location)
    {
        if (!name.Contains(value: '[') && !name.Contains(value: "::"))
        {
            return resolve(arg: name);
        }

        if (resolveExpression == null ||
            Builder.Verification.SemanticVerifier.ParseTypeExpressionString(text: name, location: location) is not
                { } parsed)
        {
            return null;
        }

        TypeSymbol? resolved = resolveExpression(arg: parsed);
        return resolved is ErrorTypeSymbol ? null : resolved;
    }

    private static bool Mentions(TypeExpression? type, string name)
    {
        return type != null && (type.Name == name ||
                                (type.GenericArguments?.Any(predicate: a => Mentions(type: a, name: name)) ?? false));
    }
}
