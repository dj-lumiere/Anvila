using Builder.Frontends;
using TypeModel.Enums;

namespace Builder.Diagnostics;

/// <summary>
/// Picks the language a diagnostic code is spelled in. A code carries the language of the file the
/// diagnostic points at, so a Suflae file reports SF-S436 while an imported RazorForge module in the
/// same build still reports RF codes for its own lines.
/// </summary>
public static class DiagnosticLanguage
{
    /// <summary>The code prefix for a language (its short name, <c>RF</c>). A language whose front end is
    /// not registered falls back to <c>RF</c>.</summary>
    public static string Prefix(Language language)
    {
        return Languages.IsRegistered(language: language)
            ? Languages.For(language: language).ShortName
            : "RF";
    }

    /// <summary>The language of a source file, by extension (see <see cref="Languages.OfFile"/>).</summary>
    public static Language OfFile(string? fileName)
    {
        return Languages.OfFile(fileName: fileName);
    }

    /// <summary>A message about <paramref name="fileName"/> in the words that file's language uses: a type the
    /// builder wraps (a Suflae entity's handle) reads as the type its user wrote.</summary>
    public static string Surface(string message, string? fileName)
    {
        Language language = OfFile(fileName: fileName);
        return Languages.IsRegistered(language: language)
            ? Languages.For(language: language).SurfaceText(text: message)
            : message;
    }
}
