namespace Builder.Frontends;

/// <summary>
/// The source file extensions of the languages the builder core knows, with their dots. This is the one place they are
/// spelled: the language rules (<see cref="LanguageRules.FileExtension"/>) and every builder path that must name a
/// language's extension without its rules being registered read them from here.
/// </summary>
public static class SourceExtensions
{
    /// <summary>Revare sources: <c>.rr</c>.</summary>
    public const string Revare = ".rr";

    /// <summary>Mielia sources: <c>.mie</c>.</summary>
    public const string Mielia = ".mie";

    /// <summary>Revare sources before 0.5: <c>.rf</c>.</summary>
    public const string PreviousRevare = ".rf";

    /// <summary>Mielia sources before 0.5: <c>.sf</c>.</summary>
    public const string PreviousMielia = ".sf";
}
