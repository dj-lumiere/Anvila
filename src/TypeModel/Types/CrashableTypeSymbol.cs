using System.Text;
using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Type information for crashable types: the errors a <c>throw</c> raises. A crashable is a value, a record with
/// no identity, so nothing about ownership or sharing applies to it. When a recovery carrier (<c>Check</c>,
/// <c>Lookup</c>) catches one, the value goes into the carrier's <c>Crashables</c>, which holds it in a fixed room
/// every crashable fits in (RF-S711), with its type id in front. Automatically conforms to the Crashable protocol.
/// Must provide crash_message() -> Text; crash_title() is synthesized from the type name.
/// </summary>
public sealed class CrashableTypeSymbol : RecordTypeSymbol
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Crashable;

    /// <summary>
    /// The synthesized crash title (sentence-cased type name, e.g. "Network error" for NetworkError).
    /// Computed once and stored here for codegen use.
    /// </summary>
    public string CrashTitle { get; init; }

    /// <summary>
    /// Initializes a new instance of <see cref="CrashableTypeSymbol"/>.
    /// </summary>
    /// <param name="name">The type name.</param>
    public CrashableTypeSymbol(string name) : base(name: name)
    {
        CrashTitle = SynthesizeCrashTitle(typeName: name);
    }

    /// <inheritdoc/>
    public override TypeSymbol CreateInstance(List<TypeSymbol> typeArguments)
    {
        throw new InvalidOperationException(
            message: $"Crashable type '{Name}' cannot be resolved with type arguments.");
    }

    /// <summary>
    /// Converts a CamelCase type name to a sentence-cased crash title. A run of capitals is one word and keeps its
    /// capitals.
    /// Examples: NetworkError -> "Network error", VerificationFailedError -> "Verification failed error",
    /// IOError -> "IO error"
    /// </summary>
    public static string SynthesizeCrashTitle(string typeName)
    {
        if (string.IsNullOrEmpty(value: typeName))
        {
            return typeName;
        }

        var words = new List<string>();
        int start = 0;
        for (int i = 1; i < typeName.Length; i++)
        {
            char c = typeName[index: i];
            char before = typeName[index: i - 1];
            bool afterLower = char.IsUpper(c: c) && !char.IsUpper(c: before);
            bool acronymEnds = char.IsUpper(c: c) && char.IsUpper(c: before) && i + 1 < typeName.Length &&
                               char.IsLower(c: typeName[index: i + 1]);
            if (afterLower || acronymEnds)
            {
                words.Add(item: typeName[start..i]);
                start = i;
            }
        }

        words.Add(item: typeName[start..]);
        var sb = new StringBuilder();
        for (int w = 0; w < words.Count; w++)
        {
            string word = words[index: w];
            bool acronym = word.Length > 1 && word.All(predicate: char.IsUpper);
            string shown = acronym ? word : word.ToLowerInvariant();
            if (w == 0)
            {
                shown = char.ToUpperInvariant(c: shown[index: 0]) + shown[1..];
            }
            else
            {
                sb.Append(value: ' ');
            }

            sb.Append(value: shown);
        }

        return sb.ToString();
    }
}
