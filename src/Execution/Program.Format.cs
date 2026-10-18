using Builder.Formatting;
using Builder.Declaration;
using Builder.Diagnostics;
using Builder.Verification;
using Builder.Verification.Results;
using TypeModel.Enums;

namespace Builder.Execution;

/// <summary>The <c>fmt</c> command: formats source files in the language's canonical layout.</summary>
internal partial class Program
{
    /// <summary>
    /// Runs <c>fmt [--check] [--keep-order] &lt;files or directories...&gt;</c>. <c>--keep-order</c> keeps top-level
    /// declarations in source order (for code examples in documentation). Without <c>--check</c> each file that is not in
    /// the canonical layout is rewritten in place. With <c>--check</c> nothing is written: the files that would
    /// change are listed. Either way a file the formatter refuses is listed with the reason and left as it is.
    /// Returns 1 when a file would change (under <c>--check</c>) or was refused, 0 otherwise.
    /// </summary>
    private static int RunFormatCommand(string[] args)
    {
        bool check = args.Skip(count: 1)
                         .Any(predicate: a => a == "--check");
        bool keepOrder = args.Skip(count: 1)
                             .Any(predicate: a => a == "--keep-order");
        List<string> targets = args.Skip(count: 1)
                                   .Where(predicate: a => a is not ("--check" or "--keep-order"))
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
            FormatResult result = SourceFormatter.Format(source: source, fileName: file, language: language,
                keepOrder: keepOrder);
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

    /// <summary>
    /// Runs <c>lint &lt;files or directories...&gt;</c>: reports every file that is not in its language's canonical
    /// layout (the one <c>fmt</c> writes), at the first line that differs, with what the line should read. Nothing is
    /// written. Returns 1 when a file has a style error or cannot be read as source, 0 otherwise.
    /// </summary>
    /// <summary>Prints a hint for each name in <paramref name="source"/> that carries a kind or scope prefix
    /// (<see cref="NamePrefixLint"/>), and returns how many. A file that does not parse has its own error.</summary>
    private static int ReportPrefixedNames(string file, string source)
    {
        SourceFormatter.ParsedFile parsed;
        try
        {
            parsed = SourceFormatter.ParseFile(source: source, fileName: file, language: SourceLanguage(path: file),
                what: "the file");
        }
        catch (FormatRefusedException)
        {
            return 0;
        }

        List<(SyntaxTree.SourceLocation At, string Name, string Plain)> prefixed = NamePrefixLint.Find(program: parsed.Program);
        foreach ((SyntaxTree.SourceLocation at, string name, string plain) in prefixed)
        {
            Console.WriteLine(value: $"hint[name]: {file}:{at.Line}:{at.Column}: '{name}' carries a prefix for its kind " +
                                     $"or scope, which the code already shows: name it '{plain}'");
        }

        return prefixed.Count;
    }

    /// <summary>
    /// Prints the notices of <paramref name="file"/> (likely slips that build as written, such as a Suflae result
    /// left unused) and returns how many there were. Only a language that reports some is analyzed, so linting a
    /// RazorForge file stays a style check.
    /// </summary>
    private static int ReportNotices(string file)
    {
        if (Builder.Frontends.Languages.For(language: SourceLanguage(path: file)).MarksDroppedResults)
        {
            return 0;
        }

        ResolvedEntry resolved = ResolveExplicitEntry(explicitEntry: file, outputFile: null);
        if (resolved.EntryFile == null)
        {
            return 0;
        }

        string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();
        var driver = new BuildDriver(projectRoot: resolved.ProjectRoot ?? Path.GetDirectoryName(path: Path.GetFullPath(path: file)) ?? ".",
            stdlibRoot: stdlibRoot,
            language: SourceLanguage(path: file),
            libraryRoots: resolved.LibraryRoots);
        BuildResult built = driver.CompileFile(entryFile: Path.GetFullPath(path: file));
        if (built.Errors.Count > 0)
        {
            return 0;
        }

        var analyzer = new SemanticVerifier(language: SourceLanguage(path: file));
        analyzer.Registry.UseModuleResolver(resolver: driver.Resolver);
        AnalysisResult result = analyzer.AnalyzeMultiple(files: OrderUserFiles(
            userUnits: FilterUserUnits(buildResult: built, stdlibRoot: stdlibRoot),
            initializationOrder: built.InitializationOrder));
        // Only the file's own notices: a file it imports is linted on its own.
        List<SemanticWarning> own = result.Notices
                                          .Where(predicate: n => Path.GetFullPath(path: n.Location.FileName) ==
                                                                 Path.GetFullPath(path: file))
                                          .ToList();
        foreach (SemanticWarning notice in own)
        {
            DiagnosticRenderer.Print(warning: notice);
        }

        return own.Count;
    }

    private static int RunLintCommand(string[] args)
    {
        List<string> targets = args.Skip(count: 1).ToList();
        if (targets.Count == 0)
        {
            Console.WriteLine(value: "Error: lint needs at least one file or directory");
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

        int problems = 0;
        int hints = 0;
        int notes = 0;
        foreach (string file in files.Distinct()
                                     .Order(comparer: StringComparer.Ordinal))
        {
            string source = File.ReadAllText(path: file);
            hints += ReportPrefixedNames(file: file, source: source);
            notes += ReportNotices(file: file);
            FormatResult result = SourceFormatter.Format(source: source, fileName: file,
                language: SourceLanguage(path: file), keepOrder: false);
            if (!result.Succeeded)
            {
                problems++;
                Console.WriteLine(value: $"error[style]: {file}: the formatter cannot read it: {result.Refusal}");
                continue;
            }

            if (result.Output == source)
            {
                continue;
            }

            problems++;
            string[] have = source.Replace(oldValue: "\r\n", newValue: "\n").Split(separator: '\n');
            string[] want = result.Output.Replace(oldValue: "\r\n", newValue: "\n").Split(separator: '\n');
            int line = 0;
            while (line < have.Length && line < want.Length && have[line] == want[line])
            {
                line++;
            }

            string should = line < want.Length ? want[line] : "(nothing)";
            string actual = line < have.Length ? have[line] : "(nothing)";
            Console.WriteLine(value: $"error[style]: {file}:{line + 1}: this is not in the canonical layout");
            Console.WriteLine(value: $"  it reads:       {actual}");
            Console.WriteLine(value: $"  it should read: {should}");
        }

        Console.WriteLine(value: problems == 0
            ? $"{files.Count} files: no style errors"
            : $"{files.Count} files: {problems} with style errors (fmt fixes them)");
        if (hints > 0)
        {
            Console.WriteLine(value: $"{hints} names carry a prefix (hints, not errors)");
        }

        if (notes > 0)
        {
            Console.WriteLine(value: $"{notes} notes (likely slips that build as written, not errors)");
        }
        return problems == 0
            ? 0
            : 1;
    }
}
