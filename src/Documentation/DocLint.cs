using System.Collections.Concurrent;
using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Documentation;

/// <summary>
/// One slip in a doc comment: its code, the prose, and the routine it belongs to.
/// </summary>
public sealed record DocNote(SemanticWarningCode Code, string Message, SourceLocation Location);

/// <summary>
/// The notes about doc comments (<c>note[RV-W305]</c> to <c>note[RV-W308]</c>). Every routine of a library or
/// example file carries a <c>###</c> doc comment right above it: what it does, a <c>:param name:</c> line for each
/// parameter but <c>me</c>, and a <c>:returns:</c> line when it gives a result. Revare holds every routine to
/// all four notes. Mielia asks for the comment only on a routine of a module another file imports, and for its
/// <c>:param</c> lines wherever a comment exists (it has no <c>:returns:</c> note).
/// <para>A file under a folder named <c>tests</c>, <c>playground</c>, <c>scratch</c> or <c>generated</c> (below its
/// project folder) is not held to the rule: golden tests, throwaway programs and a generator's output are not
/// documentation.</para>
/// </summary>
public static class DocLint
{
    /// <summary>The folders whose files are not library or example files.</summary>
    public static readonly string[] ExemptDirectories = ["tests", "playground", "scratch", "generated"];

    private static readonly ConcurrentDictionary<string, string?> ProjectRoots =
        new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="file"/> is held to the rule: no folder of its path below the project folder (the one
    /// with the <c>config.toml</c>, or the whole path when there is none) is named like an exempt folder.
    /// </summary>
    public static bool IsLibraryFile(string file)
    {
        string full = Path.GetFullPath(path: file);
        string? folder = Path.GetDirectoryName(path: full);
        string? root = folder == null ? null : ProjectRootOf(folder: folder);
        string relative = root != null && full.StartsWith(value: root, comparisonType: StringComparison.Ordinal)
            ? full[root.Length..]
            : full;
        string[] parts = relative.Split(separator: ['/', '\\'], options: StringSplitOptions.RemoveEmptyEntries);
        return !parts[..^1].Any(predicate: part => ExemptDirectories.Contains(value: part,
            comparer: StringComparer.OrdinalIgnoreCase));
    }

    private static string? ProjectRootOf(string folder)
    {
        return ProjectRoots.GetOrAdd(key: folder, valueFactory: start =>
        {
            for (string? dir = start; !string.IsNullOrEmpty(value: dir); dir = Path.GetDirectoryName(path: dir))
            {
                if (File.Exists(path: Path.Combine(path1: dir, path2: "config.toml")))
                {
                    return dir.EndsWith(value: Path.DirectorySeparatorChar)
                        ? dir
                        : dir + Path.DirectorySeparatorChar;
                }
            }

            return null;
        });
    }

    /// <summary>
    /// The notes for the routines of <paramref name="program"/> (a file of <paramref name="language"/>).
    /// <paramref name="isImported"/> says whether another file imports the file's module, which is what makes Mielia
    /// ask for a doc comment at all.
    /// </summary>
    public static List<DocNote> Check(Program program, string file, Language language, bool isImported)
    {
        var notes = new List<DocNote>();
        if (!IsLibraryFile(file: file))
        {
            return notes;
        }

        bool revare = language == Language.Revare;
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is RoutineDeclaration { IsScriptEntry: false, IsBuilderWritten: false } routine)
            {
                CheckRoutine(routine: routine, revare: revare, requireDoc: revare || isImported,
                    notes: notes);
            }
        }

        return notes;
    }

    private static void CheckRoutine(RoutineDeclaration routine, bool revare, bool requireDoc, List<DocNote> notes)
    {
        string name = routine.Name;
        if (string.IsNullOrWhiteSpace(value: routine.Documentation))
        {
            if (requireDoc)
            {
                string tail = revare
                    ? "a `:param` line for each parameter and a `:returns:` line when it gives a result"
                    : "a `:param` line for each parameter";
                notes.Add(item: new DocNote(Code: SemanticWarningCode.RoutineHasNoDocComment,
                    Message: $"The routine '{name}' has no doc comment. Write `###` lines right above it that say " +
                             $"what it does, with {tail}.",
                    Location: routine.Location));
            }

            return;
        }

        DocComment doc = DocComment.Parse(doc: routine.Documentation);
        List<string> parameters = routine.Parameters.Select(selector: p => p.Name)
                                         .Where(predicate: p => p != "me")
                                         .ToList();
        List<string> documented = doc.Params.Select(selector: p => p.Name)
                                     .ToList();
        foreach (string parameter in parameters.Where(predicate: p => !documented.Contains(item: p)))
        {
            notes.Add(item: new DocNote(Code: SemanticWarningCode.DocCommentLacksParam,
                Message: $"The doc comment of '{name}' has no `:param` line for '{parameter}'. Add " +
                         $"`:param {parameter}:` and say what it is.",
                Location: routine.Location));
        }

        foreach (string extra in documented.Where(predicate: p => !parameters.Contains(item: p)))
        {
            notes.Add(item: new DocNote(Code: SemanticWarningCode.DocCommentNamesUnknownParam,
                Message: $"The doc comment of '{name}' has a `:param` line for '{extra}', but '{name}' has no " +
                         $"parameter '{extra}'. Remove the line or fix the name.",
                Location: routine.Location));
        }

        if (revare && routine.ReturnType is { Name: not "None" } returned && doc.Returns == null)
        {
            notes.Add(item: new DocNote(Code: SemanticWarningCode.DocCommentLacksReturns,
                Message: $"The doc comment of '{name}' has no `:returns:` line, and '{name}' returns " +
                         $"'{returned.Name}'. Add `:returns:` and say what the result is.",
                Location: routine.Location));
        }
    }
}
