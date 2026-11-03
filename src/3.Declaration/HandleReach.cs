using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// Whether a value of a type holds a handle to a shared entity (<c>Roamed[E]</c>), directly or inside a record,
/// tuple or variant it owns by value. The answer behind the <c>reaches_entity</c> BuilderQuery, so library code that
/// stores a value somewhere another task can reach (a container's places) pays for the promotion only for a type
/// that can need it.
/// </summary>
internal static class HandleReach
{
    /// <summary>Whether a value of <paramref name="type"/> holds a <c>Roamed</c> handle.</summary>
    public static bool Reaches(TypeSymbol type)
    {
        return Reaches(type: type, depth: 0);
    }

    private static bool Reaches(TypeSymbol type, int depth)
    {
        if (depth > 8)
        {
            return false;
        }

        string? baseName = TypeRegistry.GetRcWrapperBaseName(type: type);
        if (baseName == RuntimeContract.Roamed)
        {
            return true;
        }

        if (baseName is not null)
        {
            return false;
        }

        return type switch
        {
            TupleTypeSymbol t => t.ElementTypes.Any(predicate: e => Reaches(type: e, depth: depth + 1)),
            VariantTypeSymbol v => v.Members.Any(predicate: m => m.Type is { } member && Reaches(type: member, depth: depth + 1)),
            RecordTypeSymbol { IsGenericDefinition: false } r when
                !RuntimeContract.WrapperTypes.Contains(item: r.GenericDefinition?.Name ?? r.Name)
                => r.MemberVariables.Any(predicate: m => Reaches(type: m.Type, depth: depth + 1)),
            _ => false
        };
    }
}
