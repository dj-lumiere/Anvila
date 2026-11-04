namespace Builder.Frontends;

/// <summary>
/// The source file extensions of the languages the builder core knows, with their dots. This is the one place they are
/// spelled: the language rules (<see cref="LanguageRules.FileExtension"/>) and every builder path that must name a
/// language's extension without its rules being registered read them from here.
/// </summary>
public static class SourceExtensions
{
    /// <summary>Revare sources: <c>.rvr</c>.</summary>
    public const string Revare = ".rvr";

    /// <summary>Mielia sources: <c>.mie</c>.</summary>
    public const string Mielia = ".mie";

    /// <summary>Lapila sources: <c>.lpa</c>.</summary>
    public const string Lapila = ".lpa";

    /// <summary>The extensions Revare sources had before: <c>.rf</c>, then <c>.rr</c>.</summary>
    public static readonly IReadOnlyList<string> PreviousRevare = [".rf", ".rr"];

    /// <summary>The extension Mielia sources had before: <c>.sf</c>.</summary>
    public static readonly IReadOnlyList<string> PreviousMielia = [".sf"];

    /// <summary>The extensions Lapila sources had before: <c>.te</c>, then <c>.li</c>.</summary>
    public static readonly IReadOnlyList<string> PreviousLapila = [".te", ".li"];
}
