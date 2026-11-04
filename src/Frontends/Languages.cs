using TypeModel.Enums;

namespace Builder.Frontends;

/// <summary>
/// The rules of each language, registered by its front end (the Revare and Mielia projects) when its
/// command line or test host starts. The builder core reaches every language-specific decision through
/// here, so it never depends on a particular language.
/// </summary>
public static class Languages
{
    private static readonly LanguageRules?[] Registered =
        new LanguageRules?[Enum.GetValues<Language>().Length];

    /// <summary>Registers <paramref name="rules"/> as the rules of their language (idempotent).</summary>
    public static void Register(LanguageRules rules)
    {
        Volatile.Write(location: ref Registered[(int)rules.Language], value: rules);
    }

    /// <summary>True when <paramref name="language"/>'s front end has registered its rules.</summary>
    public static bool IsRegistered(Language language)
    {
        return Volatile.Read(location: ref Registered[(int)language]) != null;
    }

    /// <summary>The rules of <paramref name="language"/>.</summary>
    public static LanguageRules For(Language language)
    {
        return Volatile.Read(location: ref Registered[(int)language]) ??
               throw new InvalidOperationException(
                   message: $"No {language} rules are registered: the {language} front end registers them at startup.");
    }

    /// <summary>
    /// The language a source file is written in, by its extension. A file whose extension no registered
    /// language claims (including a missing file name) is <paramref name="fallback"/>.
    /// </summary>
    public static Language OfFile(string? fileName, Language fallback = Language.Revare)
    {
        if (fileName == null)
        {
            return fallback;
        }

        foreach (LanguageRules? rules in Registered)
        {
            if (rules != null &&
                fileName.EndsWith(value: rules.FileExtension, comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                return rules.Language;
            }
        }

        return fallback;
    }

    /// <summary>The registered languages' rules, Revare first.</summary>
    public static IReadOnlyList<LanguageRules> All =>
        Registered.Select(selector: rules => rules).OfType<LanguageRules>().ToList();

    /// <summary>The registered source file extensions, with their dots (<c>.rvr</c>), Revare first.</summary>
    public static IEnumerable<string> FileExtensions => All.Select(selector: rules => rules.FileExtension);

    /// <summary>A directory search pattern per registered source extension (<c>*.rvr</c>), Revare first.</summary>
    public static IEnumerable<string> SourceGlobs => FileExtensions.Select(selector: ext => "*" + ext);

    /// <summary>The short name (<c>RV</c>) of the language <paramref name="fileName"/> is written in: the
    /// realm its declarations register under.</summary>
    public static string RealmOf(string? fileName)
    {
        Language language = OfFile(fileName: fileName);
        return IsRegistered(language: language)
            ? For(language: language).ShortName
            : TypeModel.Realms.Shared;
    }

    /// <summary>
    /// The standard library directories a <paramref name="language"/> build reads under
    /// <paramref name="stdlibRoot"/>, each with its search pattern: Revare's always (every build shares
    /// it), then the language's own when it has one: its sources, and the Revare-written modules kept with them.
    /// </summary>
    public static List<(string Dir, string Glob)> StandardLibraryRoots(string stdlibRoot, Language language)
    {
        LanguageRules revare = For(language: Language.Revare);
        var roots = new List<(string Dir, string Glob)>
        {
            (Path.Combine(path1: stdlibRoot, path2: revare.Name), "*" + revare.FileExtension)
        };
        LanguageRules rules = For(language: language);
        if (rules.HasOwnStandardLibrary)
        {
            string own = Path.Combine(path1: stdlibRoot, path2: rules.Name);
            // A module of the language's own library that needs Revare's buildtime features (`expand`) is
            // written in Revare and still belongs to the language: only its builds read this directory. It
            // is shared code, read before the language's own files, so a surface routine the language writes
            // over one of its routines finds it already there.
            if (rules.Language != Language.Revare)
            {
                roots.Add(item: (own, "*" + revare.FileExtension));
            }

            roots.Add(item: (own, "*" + rules.FileExtension));
        }

        return roots;
    }

    /// <summary>The language whose sources had <paramref name="fileName"/>'s extension before 0.5, or null.</summary>
    public static LanguageRules? WithPreviousExtension(string? fileName)
    {
        return fileName == null
            ? null
            : All.FirstOrDefault(predicate: rules => PreviousExtensionOf(rules: rules, fileName: fileName) != null);
    }

    /// <summary>The extension from before that <paramref name="fileName"/> ends in, for <paramref name="rules"/>'s
    /// language, or null.</summary>
    public static string? PreviousExtensionOf(LanguageRules rules, string fileName)
    {
        return rules.PreviousFileExtensions.FirstOrDefault(predicate: old =>
            fileName.EndsWith(value: old, comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The build error for a source file that still has its language's extension from before 0.5
    /// (<c>hello.rf</c>, <c>hello.rr</c>), or null for any other file. It names the new extension and the <c>migrate</c> step that
    /// renames a folder's files.</summary>
    public static string? PreviousExtensionMessage(string? fileName)
    {
        if (WithPreviousExtension(fileName: fileName) is not { } rules)
        {
            return null;
        }

        string renamed = Path.ChangeExtension(path: Path.GetFileName(path: fileName!), extension: rules.FileExtension);
        return $"error: {fileName} has the old extension {PreviousExtensionOf(rules: rules, fileName: fileName!)}: {rules.Name} sources end in " +
               $"{rules.FileExtension} now. Rename it to {renamed} ({rules.ToolName} migrate --step extensions <folder> " +
               "renames every file of a folder).";
    }

    /// <summary>True when <paramref name="fileName"/> carries the extension of a registered language.</summary>
    public static bool HasSourceExtension(string? fileName)
    {
        return fileName != null && Registered.Any(predicate: rules =>
            rules != null &&
            fileName.EndsWith(value: rules.FileExtension, comparisonType: StringComparison.OrdinalIgnoreCase));
    }
}
