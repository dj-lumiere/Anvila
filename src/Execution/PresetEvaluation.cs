using System.Diagnostics;
using System.Text;
using Builder.Diagnostics;
using Builder.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Execution;

/// <summary>
/// Computes at build time the presets whose initializers do more than spell a value (call a routine, do arithmetic,
/// fill in a text), so a preset is the same value everywhere and nothing about it runs in the program.
///
/// <para>The program is built a second time as an evaluator: the user files are copied into a temporary project,
/// the entry file's <c>start()</c> (or its top-level statements) is replaced by one that prints each such preset as
/// RazorForge source (<c>T.preset_literal()</c>), and the evaluator is built and run as a child builder process (a
/// crash or a runaway loop in a preset stays there). Each printed text is parsed back and becomes the preset's
/// initializer, a literal or a construction from literals, before the real build analyzes anything.</para>
/// </summary>
internal static class PresetEvaluation
{
    /// <summary>Set for the child build, whose presets are computed at run time as before (it is the evaluator).</summary>
    private const string ChildMarker = "RF_PRESET_EVALUATION";

    /// <summary>The line that comes before each printed preset value.</summary>
    private const string ValueMarker = "@@PRESET";

    private static readonly TimeSpan ChildTimeout = TimeSpan.FromMinutes(minutes: 5);

    /// <summary>
    /// In the evaluator build, its entry file: the one <c>start()</c> that runs there, so the build starts from it
    /// alone and leaves out what only the other files' <c>start()</c>s reach (a bundle of programs, each with its own,
    /// would otherwise be built whole a second time). Null in any other build.
    /// </summary>
    internal static string? EvaluatorEntry =>
        Environment.GetEnvironmentVariable(variable: ChildMarker) is { Length: > 1 } entry ? entry : null;

    private sealed record Computed(SyntaxTree.Program Program, string File, int Index, PresetDeclaration Preset, int Id);

    /// <summary>
    /// Replaces the initializer of every computed preset in <paramref name="orderedFiles"/> with the value it comes
    /// to. Returns 0, or 1 after printing the build errors when a preset cannot be computed.
    /// </summary>
    public static int Run(Language language, string entryFile, string projectRoot,
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles)
    {
        if (Environment.GetEnvironmentVariable(variable: ChildMarker) != null)
        {
            return 0;
        }

        var computed = new List<Computed>();
        foreach ((SyntaxTree.Program program, string file) in orderedFiles)
        {
            for (int i = 0; i < program.Declarations.Count; i++)
            {
                if (program.Declarations[index: i] is PresetDeclaration preset && IsComputed(expr: preset.Value))
                {
                    computed.Add(item: new Computed(Program: program, File: file, Index: i, Preset: preset,
                        Id: computed.Count));
                }
            }
        }

        if (computed.Count == 0)
        {
            return 0;
        }

        // The same program computes the same presets: a builder that stays up (the daemon) reuses what it computed.
        string key = ProgramKey(orderedFiles: orderedFiles);
        if (Cache.TryGetValue(key: key, value: out Dictionary<int, string>? known) &&
            Substitute(computed: computed, values: known, failures: []))
        {
            return 0;
        }

        string root = Path.Combine(path1: Path.GetTempPath(), path2: "rf_presets_" + Guid.NewGuid().ToString(format: "N"));
        try
        {
            string? evaluatorEntry = WriteEvaluator(root: root, entryFile: entryFile, projectRoot: projectRoot,
                orderedFiles: orderedFiles, computed: computed);
            if (evaluatorEntry == null)
            {
                return Fail(errors: computed.Take(count: 1)
                                            .Select(selector: c => Error(preset: c.Preset,
                                                 message: "Your presets are computed when the program is built, and that needs a " +
                                                          "'routine start()' (or top-level statements) in the entry file to " +
                                                          "replace."))
                                            .ToList());
            }

            (int exit, string output, string errors) = RunChild(evaluatorEntry: evaluatorEntry);
            Dictionary<int, string> values = ReadValues(output: output);

            // The program does not build: leave the presets as written, and the build itself reports why, at the
            // program's own lines.
            if (values.Count == 0 && (errors + output).Contains(value: "aborted due to errors"))
            {
                return 0;
            }

            // A message of the run names the program's files, not the temporary copies.
            string original = BaseDirectory(projectRoot: projectRoot, orderedFiles: orderedFiles);
            errors = errors.Replace(oldValue: root + Path.DirectorySeparatorChar,
                newValue: original + Path.DirectorySeparatorChar);
            output = output.Replace(oldValue: root + Path.DirectorySeparatorChar,
                newValue: original + Path.DirectorySeparatorChar);
            var failures = new List<SemanticError>();
            // The presets are printed in order and the run stops at the first one that fails: that one has its
            // marker and no value, and the ones after it were never reached.
            int? failed = computed.Select(selector: c => (int?)c.Id)
                                  .FirstOrDefault(predicate: id => !values.ContainsKey(key: id!.Value));
            foreach (Computed c in computed.Where(predicate: c => !values.ContainsKey(key: c.Id) && c.Id == failed))
            {
                failures.Add(item: Error(preset: c.Preset,
                    message: $"The preset '{c.Preset.Name}' is computed when the program is built, and computing " +
                             $"it failed: {CrashSummary(text: errors + "\n" + output, exit: exit)}"));
            }

            if (failures.Count == 0 && Substitute(computed: computed, values: values, failures: failures))
            {
                Cache[key: key] = values;
            }

            return failures.Count == 0 ? 0 : Fail(errors: failures);
        }
        finally
        {
            try
            {
                Directory.Delete(path: root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary project does no harm.
            }
            catch (UnauthorizedAccessException)
            {
                // A leftover temporary project does no harm.
            }
        }
    }

    private static readonly Dictionary<string, Dictionary<int, string>> Cache = new(comparer: StringComparer.Ordinal);

    /// <summary>The contents of every user file, hashed: the same program computes the same presets.</summary>
    private static string ProgramKey(List<(SyntaxTree.Program Program, string FilePath)> orderedFiles)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            hashAlgorithm: System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach ((_, string file) in orderedFiles)
        {
            hash.AppendData(data: Encoding.UTF8.GetBytes(s: Path.GetFullPath(path: file) + "\n"));
            hash.AppendData(data: File.ReadAllBytes(path: file));
        }

        return Convert.ToHexString(inArray: hash.GetHashAndReset());
    }

    /// <summary>
    /// Puts each preset's value in place of its initializer. Returns false (and adds to <paramref name="failures"/>)
    /// when a value is missing, has no spelling, or does not read back.
    /// </summary>
    private static bool Substitute(List<Computed> computed, Dictionary<int, string> values, List<SemanticError> failures)
    {
        int before = failures.Count;
        var parsed = new List<(Computed Computed, Expression Value)>();
        foreach (Computed c in computed)
        {
            if (!values.TryGetValue(key: c.Id, value: out string? text))
            {
                failures.Add(item: Error(preset: c.Preset,
                    message: $"The preset '{c.Preset.Name}' is computed when the program is built, and it was not reached."));
                continue;
            }

            if (text.StartsWith(value: '!'))
            {
                failures.Add(item: Error(preset: c.Preset,
                    message: $"The preset '{c.Preset.Name}' is computed when the program is built, but a " +
                             $"'{text[1..]}' value cannot be written into the program. A preset holds a number, a " +
                             "Bool, a Text, a Char, or a record, tuple or choice made of those."));
                continue;
            }

            if (ParseValue(text: text, file: c.File, at: c.Preset.Value.Location) is not { } value)
            {
                failures.Add(item: Error(preset: c.Preset,
                    message: $"The preset '{c.Preset.Name}' came to '{text}', which does not read back as a value."));
                continue;
            }

            parsed.Add(item: (c, value));
        }

        if (failures.Count > before)
        {
            return false;
        }

        foreach ((Computed c, Expression value) in parsed)
        {
            c.Program.Declarations[index: c.Index] = c.Preset with { Value = value };
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="expr"/> does more than spell a value: a literal, a negated literal, a name, a
    /// construction (a call of a type, whose name starts with a capital) or a tuple or array of those spells one.
    /// </summary>
    private static bool IsComputed(Expression expr)
    {
        return expr switch
        {
            LiteralExpression => false,
            UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression } => false,
            IdentifierExpression or MemberExpression { Object: IdentifierExpression } => false,
            NamedArgumentExpression named => IsComputed(expr: named.Value),
            TupleLiteralExpression tuple => tuple.Elements.Any(predicate: IsComputed),
            ListLiteralExpression list => list.Elements.Any(predicate: IsComputed),
            CallExpression { Callee: IdentifierExpression { Name: [var first, ..] } } call when char.IsUpper(c: first) =>
                call.Arguments.Any(predicate: IsComputed),
            _ => true
        };
    }

    /// <summary>
    /// Writes the evaluator project into <paramref name="root"/>: every user file at the same place under the
    /// project root (and the manifest), each file with computed presets given a routine that prints them, and the
    /// entry file's <c>start()</c> replaced by one that calls those routines. Returns the evaluator's entry file, or
    /// null when the entry file has no <c>start()</c> to replace.
    /// </summary>
    private static string? WriteEvaluator(string root, string entryFile, string projectRoot,
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles, List<Computed> computed)
    {
        string fullEntry = Path.GetFullPath(path: entryFile);
        // Everything keeps its place relative to the others (a manifest names its library directories relatively):
        // the copy is rooted at the folder that holds the project and every file.
        string fullRoot = Path.GetFullPath(path: projectRoot);
        string baseDir = BaseDirectory(projectRoot: projectRoot, orderedFiles: orderedFiles);
        string? manifest = Path.Combine(path1: fullRoot, path2: "config.toml");
        if (File.Exists(path: manifest))
        {
            string manifestTarget = Path.Combine(path1: root, path2: Path.GetRelativePath(relativeTo: baseDir, path: manifest));
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: manifestTarget)!);
            File.Copy(sourceFileName: manifest, destFileName: manifestTarget);
        }

        string? evaluatorEntry = null;
        var dumps = new List<(string Routine, string? Module, bool InEntry)>();
        foreach ((SyntaxTree.Program program, string file) in orderedFiles)
        {
            string full = Path.GetFullPath(path: file);
            string relative = Path.GetRelativePath(relativeTo: baseDir, path: full);

            string target = Path.Combine(path1: root, path2: relative);
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            var lines = File.ReadAllLines(path: full).ToList();
            bool isEntry = string.Equals(a: full, b: fullEntry, comparisonType: StringComparison.Ordinal);

            List<Computed> here = computed.Where(predicate: c => ReferenceEquals(objA: c.Program, objB: program)).ToList();
            if (isEntry && !RemoveStart(program: program, lines: lines))
            {
                return null;
            }

            if (here.Count > 0)
            {
                string routine = $"preset_dump_{dumps.Count}";
                var body = new List<string> { "", $"routine {routine}()" };
                foreach (Computed c in here)
                {
                    body.Add(item: $"    show(\"{ValueMarker} {c.Id}\")");
                    body.Add(item: $"    show({c.Preset.Name}.preset_literal())");
                }

                body.Add(item: "    return");
                InsertImports(lines: lines, modules: ["IO/Console"]);
                lines.AddRange(collection: body);
                string? module = program.Declarations.OfType<ModuleDeclaration>().FirstOrDefault()?.Path;
                dumps.Add(item: (routine, module, isEntry));
            }

            if (isEntry)
            {
                evaluatorEntry = target;
            }

            File.WriteAllLines(path: target, contents: lines);
        }

        if (evaluatorEntry == null)
        {
            return null;
        }

        // The entry's start() calls every file's routine, importing the modules they are in.
        var entryLines = File.ReadAllLines(path: evaluatorEntry).ToList();
        InsertImports(lines: entryLines,
            modules: dumps.Where(predicate: d => !d.InEntry && d.Module != null).Select(selector: d => d.Module!).Distinct()
                          .ToList());
        entryLines.Add(item: "");
        entryLines.Add(item: "routine start()");
        entryLines.AddRange(collection: dumps.Select(selector: d => $"    {d.Routine}()"));
        entryLines.Add(item: "    return");
        File.WriteAllLines(path: evaluatorEntry, contents: entryLines);
        return evaluatorEntry;
    }

    /// <summary>The folder that holds the project and every one of its files.</summary>
    private static string BaseDirectory(string projectRoot,
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles)
    {
        return orderedFiles.Select(selector: f => Path.GetDirectoryName(path: Path.GetFullPath(path: f.FilePath))!)
                           .Aggregate(seed: Path.GetFullPath(path: projectRoot), func: CommonDirectory);
    }

    /// <summary>The deepest folder that holds both <paramref name="a"/> and <paramref name="b"/>.</summary>
    private static string CommonDirectory(string a, string b)
    {
        string[] left = a.TrimEnd(Path.DirectorySeparatorChar).Split(separator: Path.DirectorySeparatorChar);
        string[] right = b.TrimEnd(Path.DirectorySeparatorChar).Split(separator: Path.DirectorySeparatorChar);
        int shared = 0;
        while (shared < left.Length && shared < right.Length && left[shared] == right[shared])
        {
            shared++;
        }

        string common = string.Join(separator: Path.DirectorySeparatorChar, values: left.Take(count: shared));
        return common.Length == 0 ? Path.DirectorySeparatorChar.ToString() : common;
    }

    /// <summary>
    /// Takes the entry file's <c>routine start()</c> (with the annotations and documentation above it), or its
    /// top-level statements, out of <paramref name="lines"/>. Returns false when it has neither.
    /// </summary>
    private static bool RemoveStart(SyntaxTree.Program program, List<string> lines)
    {
        if (program.Declarations.OfType<RoutineDeclaration>().FirstOrDefault(predicate: r => r.Name == "start") is not
            { } start)
        {
            return false;
        }

        var drop = new SortedSet<int>();
        IEnumerable<int> heads = start.IsScriptEntry && start.Body is BlockStatement script
            ? script.Statements.Select(selector: s => s.Location.Line)
            : [start.Location.Line];
        foreach (int head in heads.Where(predicate: h => h >= 1 && h <= lines.Count))
        {
            int first = head - 1;
            while (!start.IsScriptEntry && first > 0 &&
                   lines[first - 1].TrimStart() is var above &&
                   (above.StartsWith(value: '@') || above.StartsWith(value: "###", comparisonType: StringComparison.Ordinal)))
            {
                first--;
            }

            int last = head;
            while (last < lines.Count && (lines[last].Length == 0 || char.IsWhiteSpace(c: lines[last][0])))
            {
                last++;
            }

            for (int i = first; i < last; i++)
            {
                drop.Add(item: i);
            }
        }

        foreach (int i in drop.Reverse())
        {
            lines.RemoveAt(index: i);
        }

        return true;
    }

    /// <summary>Adds an <c>import</c> line for each of <paramref name="modules"/> after the file's header.</summary>
    private static void InsertImports(List<string> lines, List<string> modules)
    {
        int at = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(value: "module ", comparisonType: StringComparison.Ordinal) ||
                lines[i].StartsWith(value: "import ", comparisonType: StringComparison.Ordinal))
            {
                at = i + 1;
            }
        }

        foreach (string module in modules.Where(predicate: m => !lines.Contains(item: $"import {m}")).Reverse())
        {
            lines.Insert(index: at, item: $"import {module}");
        }
    }

    /// <summary>Builds and runs the evaluator in a child builder process.</summary>
    private static (int Exit, string Output, string Errors) RunChild(string evaluatorEntry)
    {
        // The language's own builder, next to this library (the entry assembly may be a test host).
        string here = Path.GetDirectoryName(path: typeof(PresetEvaluation).Assembly.Location)!;
        string builder = Path.Combine(path1: here,
            path2: Builder.Frontends.Languages.OfFile(fileName: evaluatorEntry) == Language.Suflae
                ? "Suflae.dll"
                : "RazorForge.dll");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { builder, "buildandrun", evaluatorEntry },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(path: evaluatorEntry)!,
            // The marker names the evaluator's entry file: only its start() runs there (EvaluatorEntry).
            Environment = { [key: ChildMarker] = Path.GetFullPath(path: evaluatorEntry), [key: "RF_NO_DAEMON"] = "1" }
        };
        using Process child = Process.Start(startInfo: psi) ??
                              throw new InvalidOperationException(message: "The preset evaluator could not start.");
        child.StandardInput.Close();
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> errors = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(timeout: ChildTimeout))
        {
            child.Kill(entireProcessTree: true);
            return (-1, output.Result, "it did not finish within " + ChildTimeout.TotalMinutes + " minutes");
        }

        return (child.ExitCode, output.Result, errors.Result);
    }

    /// <summary>The printed values, by preset number.</summary>
    private static Dictionary<int, string> ReadValues(string output)
    {
        var values = new Dictionary<int, string>();
        string[] lines = output.Replace(oldValue: "\r\n", newValue: "\n").Split(separator: '\n');
        for (int i = 0; i + 1 < lines.Length; i++)
        {
            if (lines[i].StartsWith(value: ValueMarker + " ", comparisonType: StringComparison.Ordinal) &&
                int.TryParse(s: lines[i][(ValueMarker.Length + 1)..], result: out int id) &&
                i + 1 < lines.Length - 1 &&
                !lines[i + 1].StartsWith(value: ValueMarker + " ", comparisonType: StringComparison.Ordinal))
            {
                values[key: id] = lines[i + 1];
            }
        }

        return values;
    }

    /// <summary>The printed source text as an expression, or null when it does not parse.</summary>
    private static Expression? ParseValue(string text, string file, SourceLocation at)
    {
        string source = $"routine preset_value()\n    var value = {text}\n    return\n";
        try
        {
            Language language = Builder.Frontends.Languages.OfFile(fileName: file);
            var parser = new Builder.Parser.Parser(
                tokens: Builder.Tokenizer.Lexers.Tokenize(source: source, fileName: file, language: language),
                language: language,
                fileName: file) { PrintsErrors = false };
            SyntaxTree.Program parsed = parser.Parse();
            if (parser.HasErrors ||
                parsed.Declarations.OfType<RoutineDeclaration>().FirstOrDefault()?.Body is not BlockStatement
                {
                    Statements: [DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } value } }, ..]
                })
            {
                return null;
            }

            return Relocate(expr: value, at: at);
        }
        catch (GrammarException)
        {
            return null;
        }
    }

    /// <summary>The parsed value placed where the initializer it replaces was written, for any diagnostic.</summary>
    private static Expression Relocate(Expression expr, SourceLocation at)
    {
        return new LocationRewriter(at: at).VisitExpression(expr: expr);
    }

    private sealed class LocationRewriter(SourceLocation at) : AstRewriter
    {
        public override Expression VisitExpression(Expression expr)
        {
            return base.VisitExpression(expr: expr) with { Location = at };
        }
    }

    private static SemanticError Error(PresetDeclaration preset, string message)
    {
        return new SemanticError(Code: SemanticDiagnosticCode.PresetNotConstant, Message: message,
            Location: preset.Value.Location);
    }

    private static int Fail(List<SemanticError> errors)
    {
        Console.Error.WriteLine(value: $"=== BUILD ERRORS ({errors.Count}) ===");
        DiagnosticRenderer.PrintAll(errors: errors);
        return 1;
    }

    /// <summary>What a failed run said, for a message: the crash report's title and where it was thrown, without
    /// the terminal colors and the evaluator's own frames, or the exit status when it said nothing.</summary>
    private static string CrashSummary(string text, int exit)
    {
        string plain = System.Text.RegularExpressions.Regex.Replace(input: text, pattern: "\u001B\\[[0-9;]*m",
            replacement: "");
        string[] lines = plain.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries)
                              .Select(selector: l => l.Trim())
                              .Where(predicate: l => l.Length > 0 && !l.StartsWith(value: "===", comparisonType: StringComparison.Ordinal) &&
                                                     !l.StartsWith(value: "Stack trace", comparisonType: StringComparison.Ordinal) &&
                                                     !l.StartsWith(value: ValueMarker, comparisonType: StringComparison.Ordinal) &&
                                                     !System.Text.RegularExpressions.Regex.IsMatch(input: l, pattern: "^[0-9]+: at ") &&
                                                     // The crash location: the diagnostic already points at the preset.
                                                     !l.StartsWith(value: "at ", comparisonType: StringComparison.Ordinal) &&
                                                     !l.Contains(value: "preset_dump_", comparisonType: StringComparison.Ordinal) &&
                                                     !l.Contains(value: ".start()", comparisonType: StringComparison.Ordinal))
                              .Take(count: 4)
                              .ToArray();
        return lines.Length == 0
            ? exit == -1 ? "it did not finish" : $"it stopped with exit status {exit}"
            : string.Join(separator: " ", value: lines);
    }
}
