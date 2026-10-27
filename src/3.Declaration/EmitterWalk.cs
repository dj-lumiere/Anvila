using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// What an emitter hands out at each step: an item it lends (a token on an element a container keeps, or a value
/// read in place that the container still holds) or an item it hands over (a value it made or took out, which the
/// walk owns from then on). An emitter's step type cannot tell the two apart (<c>ReverseEmitter</c> reads <c>T</c> in
/// place, <c>SelectEmitter</c> hands over a <c>T</c> its transform made), so an emitter that hands over says so on its
/// <c>emit</c> with <c>@hands_over</c>. An adapter whose <c>emit</c> gives back its source's <c>Item</c>
/// (<c>where</c>, <c>take</c>, <c>skip</c>) hands over exactly when its source does.
/// </summary>
public static class EmitterWalk
{
    /// <summary>The annotation an <c>emit</c> that hands its items over carries.</summary>
    public const string HandsOverAnnotation = "hands_over";

    private const string ItemSlot = "Item";
    private const string EmitterSlot = "Emitter";
    private const int MaxDepth = 16;

    /// <summary>Whether walking <paramref name="emitter"/> hands each item over to the walk.</summary>
    public static bool HandsOver(TypeSymbol? emitter, TypeRegistry registry)
    {
        return HandsOver(emitter: emitter, registry: registry, depth: 0);
    }

    /// <summary>Whether the <c>emit</c> <paramref name="emit"/>, called on <paramref name="receiver"/>, hands its item
    /// over.</summary>
    public static bool EmitHandsOver(RoutineInfo? emit, TypeSymbol? receiver, TypeRegistry registry)
    {
        RoutineInfo? declared = emit?.RecoveryOf ?? emit;
        if (declared is not { Name: RuntimeContract.Emit })
        {
            return false;
        }

        if ((declared.GenericDefinition ?? declared).Annotations.Contains(item: HandsOverAnnotation))
        {
            return true;
        }

        return HandsOver(emitter: receiver ?? declared.OwnerType, registry: registry, depth: 0);
    }

    /// <summary>
    /// The walk an emittable starts: the <c>Emitter</c> it relates, or the emitter itself when it is one. Null when it
    /// cannot be told yet (a type parameter).
    /// </summary>
    public static TypeSymbol? EmitterOf(TypeSymbol emittable)
    {
        TypeSymbol? bound = RecordTypeSymbol.ProjectAssociatedBinding(baseType: emittable, slot: EmitterSlot);
        if (bound is null or AssociatedProjectionTypeSymbol or GenericParameterTypeSymbol or ErrorTypeSymbol)
        {
            return TypeRegistry.ObeyedProtocols(type: emittable).Any(predicate: p => p.BareName == EmitterSlot)
                ? emittable
                : null;
        }

        return bound;
    }

    /// <summary>
    /// Whether walking <paramref name="emittable"/> hands out entities it only lends: a token on an entity a container
    /// keeps, or an entity read in place. Collecting such items into a container of their own, or keeping one as
    /// the result, would keep each entity in a second place.
    /// </summary>
    public static bool LendsEntities(TypeSymbol emittable, TypeRegistry registry, out TypeSymbol? lentEntity)
    {
        lentEntity = null;
        if (EmitterOf(emittable: emittable) is not { } emitter)
        {
            return false;
        }

        TypeSymbol? item = ItemOf(emitter: emitter, registry: registry);
        if (item == null)
        {
            return false;
        }

        lentEntity = LentEntityIn(item: item, registry: registry, handsOver: HandsOver(emitter: emitter,
            registry: registry));
        return lentEntity != null;
    }

    /// <summary>The entity a walk's item reaches without handing it over, or null.</summary>
    private static TypeSymbol? LentEntityIn(TypeSymbol item, TypeRegistry registry, bool handsOver)
    {
        if (TokenTarget(type: item) is { } reached)
        {
            return registry.IsEntityKind(type: reached) ? reached : null;
        }

        if (item is TupleTypeSymbol tuple)
        {
            return tuple.ElementTypes
                        .Select(selector: e => LentEntityIn(item: e, registry: registry, handsOver: handsOver))
                        .FirstOrDefault(predicate: e => e != null);
        }

        return !handsOver && registry.IsEntityKind(type: item) ? item : null;
    }

    /// <summary>What one step of <paramref name="emitter"/> hands out: the type its <c>emit</c> gives back.</summary>
    private static TypeSymbol? ItemOf(TypeSymbol emitter, TypeRegistry registry)
    {
        TypeSymbol? item = RecordTypeSymbol.ProjectAssociatedBinding(baseType: emitter, slot: ItemSlot);
        if (item is null or AssociatedProjectionTypeSymbol or GenericParameterTypeSymbol or ErrorTypeSymbol)
        {
            item = registry.LookupMemberRoutine(type: emitter, memberRoutineName: RuntimeContract.Emit)?.ReturnType;
        }

        return item is null or AssociatedProjectionTypeSymbol or GenericParameterTypeSymbol or ErrorTypeSymbol
            ? null
            : item;
    }

    /// <summary>The type an access token reaches, or null when <paramref name="type"/> is not a token.</summary>
    private static TypeSymbol? TokenTarget(TypeSymbol type)
    {
        string bare = type is RecordTypeSymbol { GenericDefinition: { } def } ? def.Name : type.BareName;
        return RuntimeContract.AccessTokenTypes.Contains(item: bare) && type.TypeArguments is [{ } inner]
            ? inner
            : null;
    }

    private static bool HandsOver(TypeSymbol? emitter, TypeRegistry registry, int depth)
    {
        if (emitter == null || depth > MaxDepth)
        {
            return false;
        }

        // A walk over a token on an emitter (`Emitter[T].emitter()` gives `Modifying[Me]`) is that emitter's walk.
        if (TokenTarget(type: emitter) is { } inner)
        {
            return HandsOver(emitter: inner, registry: registry, depth: depth + 1);
        }

        RoutineInfo? emit = registry.LookupMemberRoutine(type: emitter, memberRoutineName: RuntimeContract.Emit);
        if (emit == null)
        {
            return false;
        }

        RoutineInfo declared = emit.GenericDefinition ?? emit;
        if (declared.Annotations.Contains(item: HandsOverAnnotation))
        {
            return true;
        }

        // An adapter that gives back its source's item (`-> TSourceEmitter/Item`) hands over what its source does.
        if (declared.ReturnType is not AssociatedProjectionTypeSymbol
            {
                SlotName: ItemSlot, Base: GenericParameterTypeSymbol source
            })
        {
            return false;
        }

        TypeSymbol? definition = emitter switch
        {
            EntityTypeSymbol e => e.GenericDefinition,
            RecordTypeSymbol r => r.GenericDefinition,
            _ => null
        };
        int slot = definition?.GenericParameters?.IndexOf(item: source.Name) ?? -1;
        return slot >= 0 && emitter.TypeArguments is { } arguments && slot < arguments.Count &&
               HandsOver(emitter: arguments[index: slot], registry: registry, depth: depth + 1);
    }
}
