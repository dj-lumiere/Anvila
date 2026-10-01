using Builder.Declaration;

namespace TypeModel.Types;

/// <summary>
/// Recognizes a wrapper type (Viewing, Modifying, Consulting, Amending, Guarded, Witnessed, Retained,
/// Tracked, Hijacked, Roamed, and the builder's own Owned) in either representation: the standard library
/// record's resolution (<c>Hijacked[S64]</c>, the representation every phase now produces) or a
/// <see cref="WrapperTypeSymbol"/> still built while the wrapper's record is not yet registered.
/// </summary>
public static class WrapperShape
{
    /// <summary>True when <paramref name="name"/> names a wrapper type.</summary>
    public static bool IsWrapperName(string name)
    {
        return RuntimeContract.WrapperTypes.Contains(item: name) || name == RuntimeContract.Owned;
    }

    /// <summary>
    /// The wrapper's name and inner type when <paramref name="type"/> is a concrete wrapper
    /// (<c>Hijacked[S64]</c> or <c>Hijacked[T]</c> inside a generic body), false otherwise.
    /// </summary>
    public static bool TryGet(TypeSymbol? type, out string name, out TypeSymbol inner)
    {
        switch (type)
        {
            case WrapperTypeSymbol w:
                name = w.Name;
                inner = w.InnerType;
                return true;
            case RecordTypeSymbol { GenericDefinition: { } def, TypeArguments: [var arg] }
                when IsWrapperName(name: def.Name):
                name = def.Name;
                inner = arg;
                return true;
            default:
                name = "";
                inner = null!;
                return false;
        }
    }

    /// <summary>True when <paramref name="type"/> is a concrete wrapper (see <see cref="TryGet"/>).</summary>
    public static bool Is(TypeSymbol? type)
    {
        return TryGet(type: type, name: out _, inner: out _);
    }

    /// <summary>True when <paramref name="type"/> is the wrapper named <paramref name="name"/>.</summary>
    public static bool Is(TypeSymbol? type, string name)
    {
        return TryGet(type: type, name: out string actual, inner: out _) && actual == name;
    }

    /// <summary>The inner type of a wrapper, or null when <paramref name="type"/> is not one.</summary>
    public static TypeSymbol? InnerOf(TypeSymbol? type)
    {
        return TryGet(type: type, name: out _, inner: out TypeSymbol inner)
            ? inner
            : null;
    }

    /// <summary>True for the read-only wrappers (Viewing, Consulting).</summary>
    public static bool IsReadOnly(TypeSymbol? type)
    {
        return TryGet(type: type, name: out string name, inner: out _) &&
               RuntimeContract.ReadOnlyWrapperTypes.Contains(item: name);
    }
}
