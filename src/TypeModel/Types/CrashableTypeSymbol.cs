using System.Text;
using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Type information for crashable types: the errors a <c>throw</c> raises. A crashable is a value, a record with
/// no identity, so nothing about ownership or sharing applies to it. When a recovery carrier (<c>Check</c>,
/// <c>Lookup</c>) catches one, the value is copied into a heap object the carrier owns, because the carrier's
/// error slot holds one pointer whatever the crashable's size. Automatically conforms to the Crashable protocol.
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
    /// Converts a CamelCase type name to a sentence-cased crash title.
    /// Examples: NetworkError -> "Network error", VerificationFailedError -> "Verification failed error"
    /// </summary>
    public static string SynthesizeCrashTitle(string typeName)
    {
        if (string.IsNullOrEmpty(value: typeName))
        {
            return typeName;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < typeName.Length; i++)
        {
            if (i > 0 && char.IsUpper(c: typeName[index: i]))
            {
                sb.Append(value: ' ');
            }

            sb.Append(value: typeName[index: i]);
        }

        string spaced = sb.ToString()
                          .ToLowerInvariant();
        return char.ToUpperInvariant(c: spaced[index: 0]) + spaced[1..];
    }
}
