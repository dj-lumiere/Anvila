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
        Func<string, TypeSymbol?> resolve)
    {
        if (routine.SpecializedTypeArguments is { Count: > 0 } done)
        {
            return done.Select(selector: t => t.KnownType ?? resolve(arg: t.Name))
                       .OfType<TypeSymbol>()
                       .ToList();
        }

        // Only a member routine: the bracket of a creator on a specialized type (`routine List[Byte](...)`) is its
        // type's.
        if (string.IsNullOrEmpty(value: routine.MemberRoutineName))
        {
            return null;
        }

        List<string> own = routine.GenericParameters?
                                  .Where(predicate: name => !receiverLeaves.Contains(item: name) &&
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
                resolve(arg: name) is not { } type || type is GenericParameterTypeSymbol)
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

    private static bool Mentions(TypeExpression? type, string name)
    {
        return type != null && (type.Name == name ||
                                (type.GenericArguments?.Any(predicate: a => Mentions(type: a, name: name)) ?? false));
    }
}
