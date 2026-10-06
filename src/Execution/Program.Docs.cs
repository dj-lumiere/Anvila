using Builder.Documentation;

namespace Builder.Execution;

/// <summary>The <c>docs</c> command: writes a library's API reference from its doc comments.</summary>
internal partial class Program
{
    /// <summary>
    /// Runs <c>docs &lt;source-dir&gt; &lt;output-dir&gt;</c>: reads every source of the tool's language under the
    /// source directory and writes one MkDocs page per file under the output directory, plus an index page per
    /// folder. The <c>.md</c> files already in the output directory are replaced. Returns 1 when a file could not be
    /// read (the others are still written), 0 otherwise.
    /// </summary>
    private static int RunDocsCommand(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine(value: "Error: docs needs a source directory and an output directory");
            return 1;
        }

        string source = args[1];
        string output = args[2];
        if (!Directory.Exists(path: source))
        {
            Console.WriteLine(value: $"Error: no such directory: {source}");
            return 1;
        }

        ApiDocResult result = new ApiDocGenerator(sourceRoot: source, language: CliLanguage).Generate(outputRoot: output);
        foreach (string problem in result.Problems)
        {
            Console.WriteLine(value: $"not documented: {problem}");
        }

        Console.WriteLine(value: $"{result.Files} files: {result.Pages} pages written to {output}, " +
                                 $"{result.Problems.Count} could not be read");
        return result.Problems.Count > 0
            ? 1
            : 0;
    }
}
