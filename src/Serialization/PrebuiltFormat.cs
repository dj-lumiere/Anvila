using TypeModel.Enums;

namespace Builder.Serialization;

/// <summary>
/// Names of a language's prebuilt standard-library files: the compiled-stdlib snapshot each language's builder
/// loads instead of analyzing its standard library on every run. Each language's snapshot carries its own
/// extension (<c>.pbrf</c> for RazorForge, <c>.pbsf</c> for Suflae), so a file says which builder made it and
/// one language's files are never read as the other's.
/// </summary>
public static class PrebuiltFormat
{
    /// <summary>The folder under the standard-library root that holds one subfolder of prebuilt files per
    /// language.</summary>
    public const string FolderName = ".prebuilt";

    /// <summary>The extension, with its dot, of <paramref name="language"/>'s prebuilt files.</summary>
    public static string Extension(Language language)
    {
        return language switch
        {
            Language.RazorForge => ".pbrf",
            Language.Suflae => ".pbsf",
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(language), actualValue: language,
                message: "This language has no prebuilt file extension.")
        };
    }

    /// <summary>The index file of <paramref name="language"/>'s modular prebuilt files.</summary>
    public static string IndexFileName(Language language)
    {
        return "index" + Extension(language: language);
    }
}
