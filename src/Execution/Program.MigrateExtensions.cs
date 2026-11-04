using System.Text.RegularExpressions;
using Builder.Frontends;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>A diagnostic code spelled with a language's short name from before 0.5 (<c>RF-S625</c>,
    /// <c>SF-G010</c>), as a whole word.</summary>
    private static readonly Regex OldDiagnosticCode = new(pattern: @"(?<![\w-])(RF|SF)-([SGWC]\d{3})(?!\w)",
        options: RegexOptions.Compiled);

    /// <summary>The realm qualifier of the shared standard library from before 0.5, <c>RF::</c>.</summary>
    private static readonly Regex OldSharedRealm = new(pattern: @"(?<![\w`$])RF::", options: RegexOptions.Compiled);

    /// <summary>The binding annotation from before 0.5, <c>@rf("Type.name")</c>.</summary>
    private static readonly Regex OldBindingAnnotation = new(pattern: @"(?<![\w`$])@rf(?=\s*\()",
        options: RegexOptions.Compiled);

    /// <summary>A code block of a page opened with a language's name from before 0.5 (<c>```razorforge</c>).</summary>
    private static readonly Regex OldFenceLanguage = new(pattern: @"(?m)^(\s*```+\s*)(razorforge|suflae)\b",
        options: RegexOptions.Compiled);

    /// <summary>A manifest's <c>executable</c> naming a file with an extension from before 0.5.</summary>
    private static readonly Regex OldExecutableFile = new(pattern: @"(?m)^(\s*executable\s*=\s*""[^""]*?)\.(rf|sf)("")",
        options: RegexOptions.Compiled);

    /// <summary>A manifest's language version key from before 0.5 (<c>razorforge-version</c>).</summary>
    private static readonly Regex OldVersionKey = new(pattern: @"(?m)^(\s*)(razorforge|suflae)-version(?=\s*=)",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step extensions</c>: the sources of a program written before 0.5 get their language's new extension
    /// (<c>.rf</c> is <c>.rr</c>, <c>.sf</c> is <c>.mi</c>), and a manifest's <c>executable</c> names the renamed file
    /// (its <c>razorforge-version</c> and <c>suflae-version</c> are <c>revare-version</c> and <c>mielia-version</c>).
    /// In each renamed file the shared library's qualifier <c>RF::</c> is <c>RA::</c> and the binding annotation
    /// <c>@rf("...")</c> is <c>@ra("...")</c> (in code only), and a diagnostic code written with the old short name
    /// (<c>RF-S625</c> in a comment) takes the new one (<c>RA-S625</c>). Code blocks of the folder's pages opened with
    /// <c>```razorforge</c> or <c>```suflae</c> take the new names. A file whose new name is already taken is listed and
    /// left as it is. With <c>--check</c> nothing is renamed or written.
    /// </summary>
    private static int RunExtensionsStep(List<string> paths, bool check)
    {
        if (paths.Count == 0)
        {
            Console.Error.WriteLine(value: $"usage: {CliRules.ToolName} migrate --step extensions [--check] <file-or-dir>...");
            return 2;
        }

        int changed = RenameOldExtensions(paths: paths, check: check);
        Console.WriteLine(value: $"{changed} file(s) {(check ? "would change" : "changed")}");
        return check && changed > 0 ? 1 : 0;
    }

    /// <summary>The work of <c>--step extensions</c>: returns how many files it renamed or rewrote (or would, with
    /// <paramref name="check"/>). A plain <c>migrate</c> runs it first, so the other rewrites find the renamed files.</summary>
    private static int RenameOldExtensions(List<string> paths, bool check)
    {
        int changed = 0;
        foreach (string file in OldExtensionSources(paths: paths))
        {
            if (Languages.WithPreviousExtension(fileName: file) is not { } rules)
            {
                continue;
            }

            string renamed = Path.ChangeExtension(path: file, extension: rules.FileExtension);
            if (File.Exists(path: renamed))
            {
                Console.WriteLine(value: $"{file}: not renamed, {Path.GetFileName(path: renamed)} is there already");
                continue;
            }

            string source = File.ReadAllText(path: file);
            string rewritten = RewriteOldLanguageNames(source: source);
            Console.WriteLine(value: $"{file}: renamed to {Path.GetFileName(path: renamed)}" +
                                     (rewritten != source ? ", old names in it rewritten" : ""));
            changed++;
            if (check)
            {
                continue;
            }

            File.WriteAllText(path: renamed, contents: rewritten);
            File.Delete(path: file);
        }

        foreach (string file in FilesUnder(paths: paths, accept: f => Path.GetFileName(path: f) == "config.toml" ||
                                                                     f.EndsWith(value: ".md", comparisonType: StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(path: file);
            string rewritten = file.EndsWith(value: ".md", comparisonType: StringComparison.Ordinal)
                ? OldFenceLanguage.Replace(input: text,
                    evaluator: m => m.Groups[1].Value + (m.Groups[2].Value == "razorforge" ? "revare" : "mielia"))
                : OldVersionKey.Replace(input: OldExecutableFile.Replace(input: text,
                        evaluator: m => m.Groups[1].Value + (m.Groups[2].Value == "rf" ? ".rr" : ".mi") + m.Groups[3].Value),
                    evaluator: m => m.Groups[1].Value + (m.Groups[2].Value == "razorforge" ? "revare" : "mielia") + "-version");
            if (rewritten == text)
            {
                continue;
            }

            Console.WriteLine(value: $"{file}: old language names rewritten");
            changed++;
            if (!check)
            {
                File.WriteAllText(path: file, contents: rewritten);
            }
        }

        return changed;
    }

    /// <summary><c>RF::</c> and <c>@rf(</c> in code, and the old short names of diagnostic codes anywhere (they are
    /// written in comments).</summary>
    private static string RewriteOldLanguageNames(string source)
    {
        string text = ReplaceInCode(text: source, pattern: OldSharedRealm, replacement: _ => "RA::");
        text = ReplaceInCode(text: text, pattern: OldBindingAnnotation, replacement: _ => "@ra");
        return OldDiagnosticCode.Replace(input: text,
            evaluator: m => (m.Groups[1].Value == "RF" ? "RA" : "MI") + "-" + m.Groups[2].Value);
    }

    /// <summary>The files under <paramref name="paths"/> that have an extension from before 0.5.</summary>
    private static IEnumerable<string> OldExtensionSources(List<string> paths)
    {
        return FilesUnder(paths: paths, accept: f => Languages.WithPreviousExtension(fileName: f) != null);
    }

    /// <summary>The files given and the files under the folders given that <paramref name="accept"/> takes, outside
    /// <c>bin</c> and <c>obj</c>, in order.</summary>
    private static IEnumerable<string> FilesUnder(List<string> paths, Func<string, bool> accept)
    {
        foreach (string path in paths)
        {
            if (File.Exists(path: path))
            {
                if (accept(arg: path))
                {
                    yield return path;
                }

                continue;
            }

            if (!Directory.Exists(path: path))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(path: path, searchPattern: "*", searchOption: SearchOption.AllDirectories)
                                             .Where(predicate: f => accept(arg: f) &&
                                                                    !f.Contains(value: $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                                                    !f.Contains(value: $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                                             .Order())
            {
                yield return file;
            }
        }
    }
}
