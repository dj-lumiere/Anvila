using SyntaxTree;

namespace Builder.Declaration;

/// <summary>
/// Standard library files the build driver parsed to fill the import index, kept so the standard library loader
/// takes them instead of parsing every file a second time. Each file is handed out once: the loader changes the
/// program it takes (analysis, lowering), so a second taker must parse its own. Only files that parsed without an
/// error are kept, so the loader reports a broken file the way it always has.
/// </summary>
public sealed class ParsedStdlibFiles
{
    private readonly Dictionary<string, Program> _programs = new(comparer: StringComparer.Ordinal);

    /// <summary>Keeps <paramref name="program"/>, parsed from <paramref name="filePath"/>.</summary>
    internal void Add(string filePath, Program program)
    {
        _programs[key: Path.GetFullPath(path: filePath)] = program;
    }

    /// <summary>The program parsed from <paramref name="filePath"/>, which is no longer kept, or null when there is
    /// none.</summary>
    internal Program? Take(string filePath)
    {
        return _programs.Remove(key: Path.GetFullPath(path: filePath), value: out Program? program)
            ? program
            : null;
    }
}
