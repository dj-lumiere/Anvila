using System.Diagnostics;
using System.Text;

namespace Builder.Execution;

/// <summary>The <c>test</c> command: builds and runs a set of programs and compares what each does with what it
/// should do, the same way <c>tessera test</c> does.</summary>
internal partial class Program
{
    /// <summary>How long one test program may take to build and run before it counts as failed.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(minutes: 5);

    /// <summary>
    /// Runs <c>test &lt;dir-or-file&gt;...</c>: every source of the running language in each directory (and each file
    /// named) is built and run as its own program. Next to <c>&lt;name&gt;.rf</c> (or <c>.sf</c>):
    /// <c>&lt;name&gt;.expected</c> is what its standard output must be, <c>&lt;name&gt;.exit</c> the exit code it must
    /// end with (0 when there is none), <c>&lt;name&gt;.error</c> a text the build must fail with, and
    /// <c>&lt;name&gt;.input</c> its standard input (an empty one when there is none). Returns 1 when any test fails.
    /// </summary>
    private static int RunTestCommand(string[] args)
    {
        List<string> targets = args.Skip(count: 1).ToList();
        if (targets.Count == 0)
        {
            Console.WriteLine(value: "Error: test needs at least one file or directory");
            return 1;
        }

        string extension = CliRules.FileExtension;
        var programs = new List<string>();
        foreach (string target in targets)
        {
            if (Directory.Exists(path: target))
            {
                programs.AddRange(collection: Directory.GetFiles(path: target, searchPattern: $"*{extension}")
                                                       .Order(comparer: StringComparer.Ordinal));
            }
            else if (File.Exists(path: target))
            {
                programs.Add(item: target);
            }
            else
            {
                Console.WriteLine(value: $"Error: '{target}' is neither a file nor a directory");
                return 1;
            }
        }

        int failed = 0;
        foreach (string program in programs)
        {
            string? failure = RunOneTest(program: program);
            string name = Path.GetFileNameWithoutExtension(path: program);
            if (failure == null)
            {
                Console.WriteLine(value: $"ok    {name}");
            }
            else
            {
                failed++;
                Console.WriteLine(value: $"FAIL  {name}: {failure}");
            }
        }

        Console.WriteLine(value: $"{programs.Count - failed} passed, {failed} failed");
        return failed == 0
            ? 0
            : 1;
    }

    /// <summary>Builds and runs one test program, or null when it did what its sibling files say it should.
    /// Otherwise, what went wrong.</summary>
    private static string? RunOneTest(string program)
    {
        string stem = Path.ChangeExtension(path: program, extension: null);
        string? expectedError = ReadIfExists(path: stem + ".error");
        string? expectedOutput = ReadIfExists(path: stem + ".expected");
        string? exitText = ReadIfExists(path: stem + ".exit");
        int expectedExit = exitText != null && int.TryParse(s: exitText.Trim(), result: out int code)
            ? code
            : 0;

        (int exit, string stdout, string stderr, bool timedOut) = RunSelf(
            arguments: [RunCommand, program],
            input: ReadIfExists(path: stem + ".input") ?? "");
        if (timedOut)
        {
            return $"did not finish within {TestTimeout.TotalMinutes} minutes";
        }

        if (expectedError != null)
        {
            string all = stdout + stderr;
            return exit != 0 && all.Contains(value: expectedError.Trim(), comparisonType: StringComparison.Ordinal)
                ? null
                : $"the build should fail with \"{expectedError.Trim()}\"";
        }

        if (expectedOutput != null && Normalize(text: stdout) != Normalize(text: expectedOutput))
        {
            return FirstDifference(expected: Normalize(text: expectedOutput), actual: Normalize(text: stdout));
        }

        return exit == expectedExit
            ? null
            : $"exit code {exit}, expected {expectedExit}" +
              (string.IsNullOrWhiteSpace(value: stderr)
                  ? ""
                  : $"\n{stderr.TrimEnd()}");
    }

    /// <summary>Runs this same tool on <paramref name="arguments"/> with <paramref name="input"/> as its standard
    /// input, and returns its exit code and output.</summary>
    private static (int Exit, string Stdout, string Stderr, bool TimedOut) RunSelf(string[] arguments, string input)
    {
        // Run as `dotnet <dll>` when this process is the dotnet host, else as the apphost itself.
        string host = Environment.ProcessPath ?? throw new InvalidOperationException(message: "No process path.");
        var start = new ProcessStartInfo
        {
            FileName = host,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (Path.GetFileNameWithoutExtension(path: host)
                .Equals(value: "dotnet", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(item: Environment.GetCommandLineArgs()[0]);
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(item: argument);
        }

        using Process process = Process.Start(startInfo: start) ??
                                throw new InvalidOperationException(message: "Could not start the test program.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(value: input);
        process.StandardInput.Close();
        if (!process.WaitForExit(timeout: TestTimeout))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "", "", true);
        }

        return (process.ExitCode, stdout.Result, stderr.Result, false);
    }

    private static string? ReadIfExists(string path)
    {
        return File.Exists(path: path)
            ? File.ReadAllText(path: path)
            : null;
    }

    /// <summary>Output compared line by line, whatever the line endings, without trailing blank lines.</summary>
    private static string Normalize(string text)
    {
        return text.Replace(oldValue: "\r\n", newValue: "\n").TrimEnd(trimChar: '\n');
    }

    private static string FirstDifference(string expected, string actual)
    {
        string[] e = expected.Split(separator: '\n');
        string[] a = actual.Split(separator: '\n');
        for (int i = 0; i < Math.Max(val1: e.Length, val2: a.Length); i++)
        {
            string want = i < e.Length ? e[i] : "(no line)";
            string got = i < a.Length ? a[i] : "(no line)";
            if (want != got)
            {
                return new StringBuilder().Append(value: $"output differs at line {i + 1}\n")
                                          .Append(value: $"  expected: {want}\n")
                                          .Append(value: $"  actual:   {got}")
                                          .ToString();
            }
        }

        return "output differs";
    }
}
