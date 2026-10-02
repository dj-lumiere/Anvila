using Builder.Formatting;
using TypeModel.Enums;

namespace Builder.Execution;

/// <summary>The <c>fmt</c> command: formats source files in the language's canonical layout.</summary>
internal partial class Program
{
    /// <summary>
    /// Runs <c>fmt [--check] &lt;files or directories...&gt;</c>. Without <c>--check</c> each file that is not in
    /// the canonical layout is rewritten in place. With <c>--check</c> nothing is written: the files that would
    /// change are listed. Either way a file the formatter refuses is listed with the reason and left as it is.
    /// Returns 1 when a file would change (under <c>--check</c>) or was refused, 0 otherwise.
    /// </summary>
    private static int RunFormatCommand(string[] args)
    {
        bool check = args.Skip(count: 1)
                         .Any(predicate: a => a == "--check");
        List<string> targets = args.Skip(count: 1)
                                   .Where(predicate: a => a != "--check")
                                   .ToList();
        if (targets.Count == 0)
        {
            Console.WriteLine(value: "Error: fmt needs at least one file or directory");
            return 1;
        }

        var files = new List<string>();
        foreach (string target in targets)
        {
            if (Directory.Exists(path: target))
            {
                foreach (string glob in Builder.Frontends.Languages.SourceGlobs)
                {
                    files.AddRange(collection: Directory.EnumerateFiles(path: target,
                        searchPattern: glob,
                        searchOption: SearchOption.AllDirectories));
                }
            }
            else if (File.Exists(path: target))
            {
                files.Add(item: target);
            }
            else
            {
                Console.WriteLine(value: $"Error: no such file or directory: {target}");
                return 1;
            }
        }

        int unchanged = 0;
        int changed = 0;
        int refused = 0;
        foreach (string file in files.Distinct()
                                     .Order(comparer: StringComparer.Ordinal))
        {
            string source = File.ReadAllText(path: file);
            Language language = SourceLanguage(path: file);
            FormatResult result = SourceFormatter.Format(source: source, fileName: file, language: language);
            if (!result.Succeeded)
            {
                refused++;
                Console.WriteLine(value: $"refused: {file}: {result.Refusal}");
                continue;
            }

            if (result.Output == source)
            {
                unchanged++;
                continue;
            }

            changed++;
            if (check)
            {
                Console.WriteLine(value: $"would reformat: {file}");
            }
            else
            {
                File.WriteAllText(path: file, contents: result.Output);
                Console.WriteLine(value: $"reformatted: {file}");
            }
        }

        string verb = check
            ? "would be reformatted"
            : "reformatted";
        Console.WriteLine(value: $"{files.Count} files: {unchanged} already formatted, {changed} {verb}, {refused} refused");
        return refused > 0 || check && changed > 0
            ? 1
            : 0;
    }
}
