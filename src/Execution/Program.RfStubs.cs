using Builder.Documentation;

namespace Builder.Execution;

/// <summary>The <c>rf-stubs</c> command: drafts the Suflae library's <c>@rf</c> declarations.</summary>
internal partial class Program
{
    /// <summary>
    /// Runs <c>rf-stubs &lt;razorforge-library&gt; &lt;suflae-library&gt; &lt;output-dir&gt;</c>: writes an
    /// <c>@rf("...")</c> stub file for every RazorForge source whose shared declarations a Suflae program sees and the
    /// Suflae library does not show yet. Existing files are kept. Returns 1 when a source could not be read.
    /// </summary>
    private static int RunRfStubsCommand(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine(value: "Error: rf-stubs needs the RazorForge library, the Suflae library and an output directory");
            return 1;
        }

        RfStubResult result = new RfStubWriter(sharedRoot: args[1], ownRoot: args[2]).Write(outputRoot: args[3]);
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
