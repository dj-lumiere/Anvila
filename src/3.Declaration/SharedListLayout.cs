using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// What one member variable of the manager or the list behind a constant <c>Guarded[List[T], ReadOnly]</c> starts as,
/// when the builder lays the two down as module data (a big <c>Int</c> literal's limbs).
/// </summary>
public enum SharedListSlot
{
    /// <summary>The share count: 1 for the module data's own share, and one more for each evaluation.</summary>
    ShareCount,

    /// <summary>Zero (the weak count, the state of the <c>ReadOnly</c> policy).</summary>
    Zero,

    /// <summary>The address of the list.</summary>
    ListAddress,

    /// <summary>The address of the first element.</summary>
    ElementsAddress,

    /// <summary>The number of elements (the list's count and its capacity).</summary>
    ElementCount
}

/// <summary>
/// The layout of a constant <c>Guarded[List[T], ReadOnly]</c>: the manager (<c>GuardManager[List[T], ReadOnly]</c>) and
/// the list (<c>List[T]</c>) as their declarations in the standard library lay them out, each member variable with what
/// it starts as. <see cref="Resolve"/> reads the member variables from the declarations and stops the build when one
/// is missing, has another type, or is one it does not know, so a change to <c>GuardManager</c> or <c>List</c> shows up
/// here instead of as a constant the runtime reads wrongly.
/// </summary>
/// <param name="Handle">The handle type, <c>Guarded[List[T], ReadOnly]</c>, the value's type.</param>
/// <param name="Manager">The manager type the handle's address points at.</param>
/// <param name="ManagerSlots">What each member variable of <paramref name="Manager"/> starts as, in declaration order.</param>
/// <param name="List">The list type the manager's <c>data</c> points at.</param>
/// <param name="ListSlots">What each member variable of <paramref name="List"/> starts as, in declaration order.</param>
public sealed record SharedListLayout(
    RecordTypeSymbol Handle,
    EntityTypeSymbol Manager,
    IReadOnlyList<SharedListSlot> ManagerSlots,
    EntityTypeSymbol List,
    IReadOnlyList<SharedListSlot> ListSlots)
{
    /// <summary>The position of the share count among the manager's member variables.</summary>
    public int ShareCountIndex => ManagerSlots.ToList().IndexOf(item: SharedListSlot.ShareCount);

    /// <summary>
    /// The layout behind <paramref name="handle"/>, a <c>Guarded[List[T], ReadOnly]</c> record type.
    /// </summary>
    /// <exception cref="InvalidOperationException">When the declarations of <c>Guarded</c>, <c>GuardManager</c> or
    /// <c>List</c> no longer have the shape the builder writes.</exception>
    public static SharedListLayout Resolve(TypeRegistry registry, TypeSymbol handle)
    {
        if (handle is not RecordTypeSymbol
            {
                GenericDefinition.Name: "Guarded", TypeArguments: [EntityTypeSymbol list, RecordTypeSymbol policy]
            } guarded)
        {
            throw Drift(what: $"'{handle.Name}' is not a Guarded[List[T], ReadOnly]");
        }

        // The handle is the manager's address and nothing more.
        if (guarded.MemberVariables.Count != 0)
        {
            throw Drift(what: $"'{guarded.Name}' has member variables, so it is no longer only the manager's address");
        }

        if (policy.MemberVariables.Count != 0)
        {
            throw Drift(what: $"the policy '{policy.Name}' has state, which a constant list cannot start");
        }

        TypeSymbol managerDefinition = registry.LookupType(name: "GuardManager") ??
                                       throw Drift(what: "GuardManager is not registered");
        if (registry.GetOrCreateResolution(genericDef: managerDefinition,
                typeArguments: [list, policy]) is not EntityTypeSymbol manager)
        {
            throw Drift(what: $"GuardManager[{list.Name}, {policy.Name}] is not an entity");
        }

        TypeSymbol elementType = list.TypeArguments is [var element]
            ? element
            : throw Drift(what: $"'{list.Name}' does not take one element type");

        TypeSymbol u64 = registry.LookupType(name: "U64") ?? throw Drift(what: "U64 is not registered");
        var managerSlots = new List<SharedListSlot>();
        foreach (MemberVariableInfo field in manager.MemberVariables)
        {
            managerSlots.Add(item: field.Name switch
            {
                "strong_count" => Expect(field: field, owner: manager, expected: u64, slot: SharedListSlot.ShareCount),
                "weak_count" => Expect(field: field, owner: manager, expected: u64, slot: SharedListSlot.Zero),
                "data" => ExpectAddressOf(field: field, owner: manager, target: list, slot: SharedListSlot.ListAddress),
                "access" => Expect(field: field, owner: manager, expected: policy, slot: SharedListSlot.Zero),
                _ => throw Drift(what: $"'{manager.Name}' has the member variable '{field.Name}', which the builder " +
                                       "does not know how to start")
            });
        }

        var listSlots = new List<SharedListSlot>();
        foreach (MemberVariableInfo field in list.MemberVariables)
        {
            listSlots.Add(item: field.Name switch
            {
                "data" => ExpectAddressOf(field: field, owner: list, target: elementType,
                    slot: SharedListSlot.ElementsAddress),
                "count" or "capacity" => Expect(field: field, owner: list, expected: u64,
                    slot: SharedListSlot.ElementCount),
                _ => throw Drift(what: $"'{list.Name}' has the member variable '{field.Name}', which the builder does " +
                                       "not know how to start")
            });
        }

        if (!managerSlots.Contains(item: SharedListSlot.ShareCount) || !managerSlots.Contains(item: SharedListSlot.ListAddress))
        {
            throw Drift(what: $"'{manager.Name}' has no 'strong_count' or no 'data'");
        }

        if (!listSlots.Contains(item: SharedListSlot.ElementsAddress) || !listSlots.Contains(item: SharedListSlot.ElementCount))
        {
            throw Drift(what: $"'{list.Name}' has no 'data' or no 'count'");
        }

        return new SharedListLayout(Handle: guarded, Manager: manager, ManagerSlots: managerSlots, List: list,
            ListSlots: listSlots);
    }

    private static SharedListSlot Expect(MemberVariableInfo field, TypeSymbol owner, TypeSymbol expected,
        SharedListSlot slot)
    {
        return field.Type.FullName == expected.FullName
            ? slot
            : throw Drift(what: $"'{owner.Name}.{field.Name}' is a '{field.Type.Name}', not a '{expected.Name}'");
    }

    /// <summary>The slot of a member variable that must be a <c>Hijacked</c> address of <paramref name="target"/>.</summary>
    private static SharedListSlot ExpectAddressOf(MemberVariableInfo field, TypeSymbol owner, TypeSymbol target,
        SharedListSlot slot)
    {
        return field.Type is RecordTypeSymbol { GenericDefinition.Name: "Hijacked", TypeArguments: [var pointee] } &&
               pointee.FullName == target.FullName
            ? slot
            : throw Drift(what: $"'{owner.Name}.{field.Name}' is a '{field.Type.Name}', not the address of a " +
                                $"'{target.Name}'");
    }

    private static InvalidOperationException Drift(string what)
    {
        return new InvalidOperationException(
            message: $"The builder lays a constant Guarded[List[T], ReadOnly] down as module data, and {what}. " +
                     "SharedListLayout must follow the declarations in System/Memory/GuardManager.rf and System/List.rf.");
    }
}
