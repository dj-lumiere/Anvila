using SyntaxTree;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// A generic parameter is its slot, not its name: <c>routine Box[TItem].peek() -> TItem</c> for <c>record Box[T]</c>
/// means the same routine as <c>routine Box[T].peek() -> T</c>. This renames each name a receiver writes in a slot
/// of its type to that type's own parameter name, across the whole declaration (parameters, return type, body,
/// constraints, receiver), before either registration path reads it. Like <see cref="VariadicParamDesugar"/> it
/// changes the declaration in place, so every later pass sees one spelling. Idempotent.
/// </summary>
internal static class ReceiverSlotRename
{
    /// <summary>Renames the receiver's parameter names of <paramref name="routine"/> to the slots of
    /// <paramref name="owner"/>. <paramref name="namesType"/> tells a name that is a type (a concrete argument, as in
    /// <c>Iterable[Text].join</c>), which stays as written.</summary>
    public static void Apply(RoutineDeclaration routine, TypeSymbol? owner, Func<string, bool> namesType)
    {
        if (routine.ReceiverType?.GenericArguments is not { Count: > 0 } args ||
            owner?.GenericParameters is not { } slots || slots.Count != args.Count ||
            routine.GenericParameters is not { } own)
        {
            return;
        }

        var subs = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < args.Count; i++)
        {
            TypeExpression arg = args[index: i];
            if (arg.GenericArguments is { Count: > 0 } || arg.Name == slots[index: i] ||
                !own.Contains(item: arg.Name) || namesType(arg.Name))
            {
                continue;
            }

            subs[key: arg.Name] = slots[index: i];
        }

        // A slot name the routine already uses for another parameter of its own would merge the two: leave it.
        if (subs.Count == 0 || subs.Values.Any(predicate: slot => own.Contains(item: slot) && !subs.ContainsKey(key: slot)))
        {
            return;
        }

        for (int i = 0; i < routine.Parameters.Count; i++)
        {
            Parameter parameter = routine.Parameters[index: i];
            if (parameter.Type != null)
            {
                routine.Parameters[index: i] = parameter with { Type = Rename(type: parameter.Type, subs: subs) };
            }
        }

        if (routine.ReturnType != null)
        {
            routine.ReturnType = Rename(type: routine.ReturnType, subs: subs);
        }

        if (routine.Body is BlockStatement block &&
            Instantiation.GenericAstRewriter.RewriteStatement(stmt: block, subs: subs) is BlockStatement renamed)
        {
            block.Statements.Clear();
            block.Statements.AddRange(collection: renamed.Statements);
        }

        routine.GenericConstraints = routine.GenericConstraints
                                           ?.Select(selector: c => c with
                                            {
                                                ParameterName = subs.GetValueOrDefault(key: c.ParameterName,
                                                    defaultValue: c.ParameterName),
                                                ConstraintTypes = c.ConstraintTypes
                                                                  ?.Select(selector: t => Rename(type: t, subs: subs))
                                                                   .ToList()
                                            })
                                            .ToList();
        routine.GenericParameters = own.Select(selector: n => subs.GetValueOrDefault(key: n, defaultValue: n))
                                       .ToList();
        routine.ReceiverType = Rename(type: routine.ReceiverType, subs: subs);
        if (routine.RenderedReceiver != null)
        {
            routine.RenderedReceiver = Render(type: routine.ReceiverType);
        }
    }

    private static string Render(TypeExpression type)
    {
        return type.GenericArguments is { Count: > 0 } args
            ? $"{type.Name}[{string.Join(separator: ", ", values: args.Select(selector: Render))}]"
            : type.Name;
    }

    private static TypeExpression Rename(TypeExpression type, IReadOnlyDictionary<string, string> subs)
    {
        return type with
        {
            Name = subs.GetValueOrDefault(key: type.Name, defaultValue: type.Name),
            GenericArguments = type.GenericArguments?.Select(selector: a => Rename(type: a, subs: subs))
                                   .ToList(),
            UnionMembers = type.UnionMembers?.Select(selector: a => Rename(type: a, subs: subs))
                               .ToList()
        };
    }
}
