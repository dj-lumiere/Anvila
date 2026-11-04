using Builder.Documentation;

namespace Builder.Execution;

/// <summary>The <c>ra-stubs</c> command: drafts the Mielia library's <c>@ra</c> declarations.</summary>
internal partial class Program
{
    /// <summary>
    /// Runs <c>ra-stubs &lt;revare-library&gt; &lt;mielia-library&gt; &lt;output-dir&gt;</c>: writes an
    /// <c>@ra("...")</c> stub file for every Revare source whose shared declarations a Mielia program sees and the
    /// Mielia library does not show yet. Existing files are kept. Returns 1 when a source could not be read.
    /// </summary>
    private static int RunRaStubsCommand(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine(value: "Error: ra-stubs needs the Revare library, the Mielia library and an output directory");
            return 1;
        }

        RaStubResult result = new RaStubWriter(sharedRoot: args[1], ownRoot: args[2]).Write(outputRoot: args[3]);
        foreach (string problem in result.Problems)
        {
            Console.WriteLine(value: $"skipped: {problem}");
        }

        Console.WriteLine(value: $"{result.Files} files: {result.Types} types and {result.Routines} routines bound");
        return result.Problems.Any(predicate: p => !p.EndsWith(value: "not overwritten"))
            ? 1
            : 0;
    }
}
