using System.Diagnostics;
using System.Globalization;
using System.Text;
using Builder.Declaration;
using TypeModel.Enums;

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

        int passed = 0, failed = 0, skipped = 0;
        void Report(string name, string? failure)
        {
            if (failure == null)
            {
                passed++;
                Console.WriteLine(value: $"ok    {name}");
            }
            else
            {
                failed++;
                Console.WriteLine(value: $"FAIL  {name}: {failure}");
            }
        }

        foreach (string program in programs)
        {
            string name = Path.GetFileNameWithoutExtension(path: program);
            (List<RoutineTest> routineTests, bool hasProgram) = FindRoutineTests(program: program);

            // The program itself runs when it has a start (written or as top-level statements), or when a file next
            // to it says what it should do. A file of @test routines only is tested routine by routine.
            string stem = Path.ChangeExtension(path: program, extension: null);
            if (hasProgram || routineTests.Count == 0 ||
                new[] { ".expected", ".exit", ".error", ".input" }.Any(predicate: e => File.Exists(path: stem + e)))
            {
                Report(name: name, failure: RunOneTest(program: program));
            }

            foreach (RoutineTest test in routineTests)
            {
                string testName = $"{name}::{test.Name}";
                if (test.Skip.Skipped)
                {
                    skipped++;
                    Console.WriteLine(value: test.Skip.Reason == null
                        ? $"skip  {testName}"
                        : $"skip  {testName}: {test.Skip.Reason}");
                    continue;
                }

                Report(name: testName, failure: test.Problem ?? RunRoutineTest(program: program, test: test));
            }
        }

        Console.WriteLine(value: skipped > 0
            ? $"{passed} passed, {failed} failed, {skipped} skipped"
            : $"{passed} passed, {failed} failed");
        return failed == 0
            ? 0
            : 1;
    }

    /// <summary>The <c>@test</c> routines of <paramref name="program"/>, and whether it has a program to run of its
    /// own (a <c>start()</c>, written or made from top-level statements). A file that doesn't parse has neither:
    /// running it as a program reports the error.</summary>
    private static (List<RoutineTest> Tests, bool HasProgram) FindRoutineTests(string program)
    {
        try
        {
            Language language = Builder.Frontends.Languages.OfFile(fileName: program);
            List<Builder.Tokenizer.Token> tokens = Builder.Tokenizer.Lexers.Tokenize(
                source: File.ReadAllText(path: program), fileName: program, language: language);
            var parser = new Builder.Parser.Parser(tokens: tokens, language: language, fileName: program)
            {
                PrintsErrors = false
            };
            SyntaxTree.Program ast = parser.Parse();
            if (parser.HasErrors)
            {
                return ([], true);
            }

            bool hasStart = ast.Declarations.OfType<SyntaxTree.RoutineDeclaration>()
                               .Any(predicate: r => r.Name == "start" && r.Parameters.Count == 0);
            return (RoutineTests.Find(program: ast), hasStart);
        }
        catch (Exception)
        {
            return ([], true);
        }
    }

    /// <summary>Builds <paramref name="program"/> with a <c>start()</c> that runs <paramref name="test"/>, runs it
    /// (stopping it at its <c>@time_limit</c>), and returns null when every case gave its output, or what went
    /// wrong.</summary>
    private static string? RunRoutineTest(string program, RoutineTest test)
    {
        (int buildExit, string buildOut, string buildErr, bool buildTimedOut) = RunSelf(
            arguments: [BuildCommand, program],
            input: "",
            environment: new Dictionary<string, string> { [key: TestEntryVariable] = test.Name });
        if (buildTimedOut)
        {
            return $"the build did not finish within {TestTimeout.TotalMinutes} minutes";
        }

        const string written = "Executable written to: ";
        string? exe = buildOut.Split(separator: '\n')
                              .Select(selector: l => l.Trim())
                              .Where(predicate: l => l.StartsWith(value: written, comparisonType: StringComparison.Ordinal))
                              .Select(selector: l => l[written.Length..])
                              .LastOrDefault();
        if (buildExit != 0 || exe == null || !File.Exists(path: exe))
        {
            return "the build failed\n" + (buildOut + buildErr).Trim();
        }

        TimeSpan limit = test.TimeLimit ?? TestTimeout;
        // A memory limit is checked by the runtime, which counts the heap when RF_MEMORY_LIMIT is set.
        Dictionary<string, string>? environment = test.MemoryLimit is { } bytes
            ? new Dictionary<string, string> { [key: "RF_MEMORY_LIMIT"] = bytes.ToString(provider: CultureInfo.InvariantCulture) }
            : null;
        (int exit, _, string stderr, bool timedOut) = RunProgram(fileName: exe, arguments: [], input: "",
            environment: environment, timeout: limit);
        if (timedOut)
        {
            return test.TimeLimit != null
                ? $"took longer than its @time_limit of {FormatSpan(span: limit)}"
                : $"did not finish within {TestTimeout.TotalMinutes} minutes";
        }

        if (test.MemoryLimit != null && stderr.Contains(value: "Memory limit error", comparisonType: StringComparison.Ordinal))
        {
            return "used more memory than its @memory_limit\n" + stderr.Trim();
        }

        if (exit == 1 && test.Cases.Count > 0)
        {
            return "a case gave a different result\n" + stderr.TrimEnd();
        }

        return exit == 0
            ? null
            : $"exit code {exit}" + (string.IsNullOrWhiteSpace(value: stderr)
                ? ""
                : $"\n{stderr.TrimEnd()}");
    }

    private static string FormatSpan(TimeSpan span)
    {
        return span.TotalSeconds >= 1
            ? $"{span.TotalSeconds:0.###}s"
            : $"{span.TotalMilliseconds:0.###}ms";
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
    private static (int Exit, string Stdout, string Stderr, bool TimedOut) RunSelf(string[] arguments, string input,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        // Run as `dotnet <dll>` when this process is the dotnet host, else as the apphost itself.
        string host = Environment.ProcessPath ?? throw new InvalidOperationException(message: "No process path.");
        var self = new List<string>();
        if (Path.GetFileNameWithoutExtension(path: host)
                .Equals(value: "dotnet", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            self.Add(item: Environment.GetCommandLineArgs()[0]);
        }

        self.AddRange(collection: arguments);
        return RunProgram(fileName: host, arguments: self, input: input, environment: environment, timeout: TestTimeout);
    }

    /// <summary>Runs <paramref name="fileName"/> with <paramref name="input"/> as its standard input, stopping it after
    /// <paramref name="timeout"/>, and returns its exit code and output.</summary>
    private static (int Exit, string Stdout, string Stderr, bool TimedOut) RunProgram(string fileName,
        IEnumerable<string> arguments, string input, IReadOnlyDictionary<string, string>? environment, TimeSpan timeout)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(item: argument);
        }

        foreach ((string key, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key: key] = value;
        }

        using Process process = Process.Start(startInfo: start) ??
                                throw new InvalidOperationException(message: "Could not start the test program.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(value: input);
        process.StandardInput.Close();
        if (!process.WaitForExit(timeout: timeout))
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
