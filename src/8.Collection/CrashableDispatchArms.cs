using Builder.Declaration;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Collection;

/// <summary>
/// The arms of a crashable dispatch (<c>CrashableDispatchExpression</c>): for each crashable whose dispatched member
/// is live in this build, its <c>type_id</c> and that member. A crashable is thrown before it can land in a
/// carrier, so the live set covers every <c>type_id</c> a carrier can hold. Chosen per build from the live routine
/// set rather than stamped on the node, because the node sits in stdlib bodies kept across warm builds, where user
/// crashables registered later must still get an arm. Deterministic across a cold build (the registry holds the
/// reached crashables only) and a warm one (it holds the whole stdlib): only live members count.
/// </summary>
internal static class CrashableDispatchArms
{
    /// <summary>The arms for dispatching <paramref name="memberName"/>, in registry order.</summary>
    public static List<(ulong TypeId, RoutineInfo Member)> For(string memberName, TypeRegistry registry,
        IReadOnlySet<string> liveRoutineKeys)
    {
        var arms = new List<(ulong TypeId, RoutineInfo Member)>();
        foreach (TypeSymbol type in registry.GetTypesByCategory(category: TypeCategory.Crashable))
        {
            if (type is CrashableTypeSymbol crashable &&
                registry.LookupMemberRoutine(type: crashable, memberRoutineName: memberName, isFailable: false) is
                    { IsGenericDefinition: false } member &&
                liveRoutineKeys.Contains(item: member.RegistryKey))
            {
                arms.Add(item: (TypeIdHelper.ComputeTypeId(fullName: crashable.FullName), member));
            }
        }

        return arms;
    }
}
