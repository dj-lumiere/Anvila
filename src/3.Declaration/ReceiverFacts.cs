using Builder.Verification.Enums;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// What the language says about a routine's <c>me</c> and its parameters that a backend turns into storage and
/// aliasing: the type the local <c>me</c> holds, and which pointers are exclusive or only read.
/// </summary>
internal static class ReceiverFacts
{
    /// <summary>
    /// The type the local <c>me</c> holds: a Suflae entity member routine receives <c>me</c> as the
    /// <c>Roamed[E]</c> handle (its <c>MeType</c>), every other routine its owner type.
    /// </summary>
    public static TypeSymbol MeLocalType(RoutineInfo routine)
    {
        return routine.MeType is RecordTypeSymbol { GenericDefinition.Name: RuntimeContract.Roamed }
            ? routine.MeType
            : routine.OwnerType!;
    }

    /// <summary>
    /// True when <c>me</c> is the only reference to what it points at for the call: a bare entity (a bound entity
    /// cannot be duplicated) or a <c>Modifying[T]</c> (a scope-bound exclusive token).
    /// </summary>
    public static bool MeIsExclusive(RoutineInfo routine)
    {
        return IsExclusive(type: routine.OwnerType);
    }

    /// <summary>True when <c>me</c> is a pointer the routine only reads: a <c>@readonly</c> routine of a wrapper.</summary>
    public static bool MeIsReadOnlyPointer(RoutineInfo routine)
    {
        return routine.MutationCategory == MutationCategory.Readonly &&
               routine.OwnerType is RecordTypeSymbol owner &&
               RuntimeContract.WrapperTypes.Contains(item: (owner.GenericDefinition ?? owner).BareName);
    }

    /// <summary>
    /// True when a routine of <paramref name="ownerType"/> takes <c>me</c> by reference (the caller's storage, so an
    /// in-place change reaches it): a struct record, or a backend-represented aggregate (an array <c>[N x T]</c>, a
    /// vector <c>&lt;N x E&gt;</c>), which is always reached through memory. A scalar backend type (<c>i64</c>,
    /// <c>ptr</c>) is a plain value its operators work on directly, and an entity is already a pointer.
    /// </summary>
    public static bool MeByReference(TypeSymbol? ownerType)
    {
        return ownerType is RecordTypeSymbol { BackendType: null } ||
               ownerType is RecordTypeSymbol { BackendType: { } backend } && backend.Length > 0 && backend[0] is '[' or '<';
    }

    /// <summary>True when a parameter of <paramref name="type"/> is an exclusive pointer (see <see cref="MeIsExclusive"/>).</summary>
    public static bool IsExclusive(TypeSymbol? type)
    {
        return type is EntityTypeSymbol || WrapperShape.Is(type: type, name: RuntimeContract.Modifying);
    }
}
