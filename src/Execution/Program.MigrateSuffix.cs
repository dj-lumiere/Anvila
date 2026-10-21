using System.Diagnostics;
using System.Text;
using Builder.Declaration;
using Builder.Diagnostics;
using Builder.Tokenizer;
using Builder.Verification;
using Builder.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>What <c>migrate --step suffix</c> does with one number that names its type.</summary>
    private enum SuffixFix
    {
        /// <summary>The place the number goes gives it that type already, so the suffix just goes.</summary>
        Strip,

        /// <summary>The number starts a declaration without a type, so the type moves onto the declaration.</summary>
        Annotate,

        /// <summary>Nothing around the number gives it that type, so it is listed for a hand rewrite.</summary>
        Keep
    }

    /// <summary>One number with a type-naming suffix: where the suffix and the number sit in the file as written, the
    /// type the suffix named, and what the step does with it.</summary>
    private sealed class SuffixedNumber
    {
        public int SuffixStart { get; init; }
        public int SuffixLength { get; init; }
        public string TypeName { get; init; } = "";
        public int Line { get; init; }
        public int NumberStart { get; set; } = -1;
        public bool BaseTen { get; set; }
        public bool Decimal { get; set; }
        public int DeclaredNameEnd { get; set; } = -1;
        public bool ShiftAmount { get; set; }
        public SuffixFix Fix { get; set; } = SuffixFix.Strip;
        public string Why { get; set; } = "";
    }

    /// <summary>One source file the step reads: its text as written, the language, whether it is a standard library
    /// file, and its numbers with suffixes.</summary>
    private sealed class SuffixSource
    {
        public string Path { get; init; } = "";
        public string Text { get; init; } = "";
        public Language Language { get; init; }
        public string? StandardRoot { get; init; }
        public List<SuffixedNumber> Numbers { get; } = [];
    }

    /// <summary>
    /// <c>migrate --step suffix</c>: a number no longer names its type (<c>5_u8</c>, <c>10n</c>, <c>2.5dn</c>). The step
    /// reads every such number, then builds the files with the suffixes taken off and asks analysis which type each
    /// bare number took. A number that took the suffix's type loses the suffix. A number that starts a declaration
    /// without a type gives its type to the declaration (<c>var x = 5_u8</c> is <c>var x: U8 = 5</c>). Any other number
    /// keeps its suffix and is listed by file and line, because only a person can say where its type should come
    /// from. With <c>--check</c> nothing is written and every number that still has a suffix is listed.
    /// </summary>
    private static int RunSuffixStep(List<string> paths, bool check)
    {
        var sources = new List<SuffixSource>();
        foreach (string file in SuffixStepFiles(paths: paths))
        {
            SuffixSource? source = ReadSuffixedNumbers(file: file);
            if (source is { Numbers.Count: > 0 })
            {
                sources.Add(item: source);
            }
        }

        int total = sources.Sum(selector: s => s.Numbers.Count);
        if (check)
        {
            foreach (SuffixSource source in sources)
            {
                foreach (SuffixedNumber number in source.Numbers)
                {
                    Console.WriteLine(value: $"{source.Path}:{number.Line}: a number names its type ({number.TypeName})");
                }
            }

            Console.WriteLine(value: $"{total} number(s) in {sources.Count} file(s) name their type");
            return total > 0 ? 1 : 0;
        }

        if (total == 0)
        {
            Console.WriteLine(value: "0 file(s) changed");
            return 0;
        }

        foreach (SuffixSource source in sources)
        {
            FindSuffixDeclarations(source: source);
        }

        // Every file is written with its suffixes off (and the declarations typed), analysis runs once over all of
        // them in a child process, and the types it reports decide each remaining number.
        foreach (SuffixSource source in sources)
        {
            File.WriteAllText(path: source.Path, contents: SuffixCandidate(source: source));
        }

        Dictionary<(string File, int Position), HashSet<string>> observed;
        try
        {
            observed = ProbeSuffixTypes(sources: sources);
        }
        catch (Exception e)
        {
            foreach (SuffixSource source in sources)
            {
                File.WriteAllText(path: source.Path, contents: source.Text);
            }

            Console.Error.WriteLine(value: $"error: migrate: analysis did not run, nothing was changed: {e.Message}");
            return 1;
        }

        int stripped = 0;
        int annotated = 0;
        int kept = 0;
        foreach (SuffixSource source in sources)
        {
            // Where each number sat in the analyzed file, taken before any decision changes the fixes the offsets
            // are counted from.
            List<(SuffixedNumber Number, int At)> analyzed = source.Numbers
                .Where(predicate: n => n.Fix == SuffixFix.Strip)
                .Select(selector: n => (n, CandidateOffset(source: source, originalOffset: n.NumberStart)))
                .ToList();
            foreach ((SuffixedNumber number, int at) in analyzed)
            {
                DecideSuffixFix(source: source, number: number, at: at, observed: observed);
            }

            File.WriteAllText(path: source.Path, contents: SuffixCandidate(source: source));
            foreach (SuffixedNumber number in source.Numbers)
            {
                switch (number.Fix)
                {
                    case SuffixFix.Strip:
                        stripped++;
                        break;
                    case SuffixFix.Annotate:
                        annotated++;
                        break;
                    default:
                        kept++;
                        Console.WriteLine(value: $"{source.Path}:{number.Line}: rewrite by hand: {number.Why}");
                        break;
                }
            }
        }

        Console.WriteLine(value: $"{sources.Count} file(s) changed: {stripped} suffix(es) dropped, {annotated} " +
                                 $"declaration(s) typed, {kept} number(s) to rewrite by hand");
        return kept > 0 ? 1 : 0;
    }

    /// <summary>The source files of this tool's language under <paramref name="paths"/> (files as given, directories
    /// walked). The other language's files are migrated by its own tool.</summary>
    private static IEnumerable<string> SuffixStepFiles(List<string> paths)
    {
        foreach (string path in paths)
        {
            if (File.Exists(path: path))
            {
                yield return Path.GetFullPath(path: path);
                continue;
            }

            if (!Directory.Exists(path: path))
            {
                Console.Error.WriteLine(value: $"{path}: no such file or directory");
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(path: path, searchPattern: "*", searchOption: SearchOption.AllDirectories)
                        .Where(predicate: f => f.EndsWith(value: CliRules.FileExtension, comparisonType: StringComparison.Ordinal))
                        .Where(predicate: f => !f.Contains(value: $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                               !f.Contains(value: $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                        .Order())
            {
                yield return Path.GetFullPath(path: file);
            }
        }
    }

    /// <summary>
    /// Reads the numbers with type-naming suffixes in <paramref name="file"/>. The lexer stops at the first one with
    /// its own error, whose caret sits on the suffix, so the file is read again with each suffix taken off until it
    /// reads through. Null when the file cannot be read for another reason.
    /// </summary>
    private static SuffixSource? ReadSuffixedNumbers(string file)
    {
        string text = File.ReadAllText(path: file);
        Language language = SourceLanguage(path: file);
        var source = new SuffixSource
        {
            Path = file,
            Text = text,
            Language = language,
            StandardRoot = StandardRootOf(file: file)
        };

        string working = text;
        int removed = 0;
        var endsInWorking = new List<int>();
        while (true)
        {
            try
            {
                List<Token> tokens = Lexers.Tokenize(source: working, fileName: file, language: language);
                LocateSuffixedNumbers(source: source, working: working, tokens: tokens, endsInWorking: endsInWorking);
                return source;
            }
            catch (GrammarException e) when (e.Code == GrammarDiagnosticCode.TypeNamingSuffix)
            {
                int at = OffsetOf(text: working, line: e.Line, column: e.Column);
                int length = working[index: at] == '_' ? 1 : 0;
                while (at + length < working.Length && char.IsLetterOrDigit(c: working[index: at + length]))
                {
                    length++;
                }

                string suffix = working.Substring(startIndex: at, length: length).TrimStart(trimChar: '_');
                source.Numbers.Add(item: new SuffixedNumber
                {
                    SuffixStart = at + removed,
                    SuffixLength = length,
                    TypeName = SuffixTypeName(suffix: suffix),
                    Line = e.Line
                });
                endsInWorking.Add(item: at);
                working = working.Remove(startIndex: at, count: length);
                removed += length;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(value: $"{file}: cannot read it: {e.Message}");
                return null;
            }
        }
    }

    /// <summary>The type a type-naming suffix named.</summary>
    private static string SuffixTypeName(string suffix)
    {
        return suffix switch
        {
            "n" => "Integer",
            "dn" => "Decimal",
            "addr" => "Addr",
            _ => suffix.ToUpperInvariant()
        };
    }

    /// <summary>The offset of a 1-based <paramref name="line"/> and <paramref name="column"/> in <paramref name="text"/>.</summary>
    private static int OffsetOf(string text, int line, int column)
    {
        int offset = 0;
        for (int current = 1; current < line; current++)
        {
            offset = text.IndexOf(value: '\n', startIndex: offset) + 1;
        }

        return offset + column - 1;
    }

    /// <summary>Finds where each number starts, from the tokens of the file with every suffix off: the number token
    /// that ends where its suffix was. <paramref name="endsInWorking"/> holds those ends, in the order found.</summary>
    private static void LocateSuffixedNumbers(SuffixSource source, string working, List<Token> tokens,
        List<int> endsInWorking)
    {
        var numberTokens = new Dictionary<int, Token>();
        foreach (Token token in tokens.Where(predicate: t => t.Type is TokenType.UndecidedInteger or TokenType.UndecidedDecimal))
        {
            numberTokens[key: token.Position + token.Text.Length] = token;
        }

        int removedBefore = 0;
        for (int i = 0; i < source.Numbers.Count; i++)
        {
            SuffixedNumber number = source.Numbers[index: i];
            int end = endsInWorking[index: i];
            int start;
            if (numberTokens.TryGetValue(key: end, value: out Token? token))
            {
                start = token.Position;
                number.Decimal = token.Type == TokenType.UndecidedDecimal;
            }
            else
            {
                start = end;
                while (start > 0 && (char.IsLetterOrDigit(c: working[index: start - 1]) || working[index: start - 1] is '_' or '.' ||
                                     working[index: start - 1] is '+' or '-' && start > 2 &&
                                     working[index: start - 2] is 'e' or 'E' or 'p' or 'P' && char.IsDigit(c: working[index: start - 3])))
                {
                    start--;
                }

                number.Decimal = working.Substring(startIndex: start, length: end - start).Contains(value: '.');
            }

            string digits = working.Substring(startIndex: start, length: end - start);
            number.BaseTen = !NumericLiteralParser.HasBasePrefix(text: digits);
            if (!number.BaseTen && digits.Contains(value: 'p', comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                number.Decimal = true;
            }

            number.NumberStart = start + removedBefore;
            removedBefore += number.SuffixLength;
        }
    }

    /// <summary>
    /// Marks the numbers that are the whole initializer of a <c>var</c> or <c>preset</c> without a type: the type
    /// moves onto the declaration, or just goes when it is the type the bare number takes anyway. A shift amount is
    /// marked too: it takes any integer type, so its suffix always goes.
    /// </summary>
    private static void FindSuffixDeclarations(SuffixSource source)
    {
        string bare = SuffixCandidate(source: source);
        List<Token> tokens = Lexers.Tokenize(source: bare, fileName: source.Path, language: source.Language);
        var byStart = new Dictionary<int, int>();
        for (int i = 0; i < tokens.Count; i++)
        {
            byStart[key: tokens[index: i].Position] = i;
        }

        int removedBefore = 0;
        foreach (SuffixedNumber number in source.Numbers)
        {
            int at = number.NumberStart - removedBefore;
            removedBefore += number.SuffixLength;
            if (!byStart.TryGetValue(key: at, value: out int index))
            {
                continue;
            }

            // A shift amount takes any integer type, so an integer suffix on one just goes.
            if (index > 0 && tokens[index: index - 1].Type is TokenType.LeftShift or TokenType.RightShift
                    or TokenType.LogicalRightShift or TokenType.LeftShiftAssign or TokenType.RightShiftAssign
                    or TokenType.LogicalRightShiftAssign &&
                number.TypeName[index: 0] is 'S' or 'U')
            {
                number.ShiftAmount = true;
                continue;
            }

            int before = index - 1;
            if (before >= 0 && tokens[index: before].Type == TokenType.Minus)
            {
                before--;
            }

            bool wholeInitializer = before >= 2 && tokens[index: before].Type == TokenType.Assign &&
                                    tokens[index: before - 1].Type == TokenType.Identifier &&
                                    tokens[index: before - 2].Type is TokenType.Var or TokenType.Preset &&
                                    index + 1 < tokens.Count &&
                                    tokens[index: index + 1].Type is TokenType.Newline or TokenType.Eof or TokenType.Dedent;
            if (!wholeInitializer)
            {
                continue;
            }

            // `inf` and `nan` have no default type: their declaration always states one.
            bool special = string.CompareOrdinal(strA: source.Text, indexA: number.NumberStart, strB: "inf", indexB: 0,
                               length: 3) == 0 ||
                           string.CompareOrdinal(strA: source.Text, indexA: number.NumberStart, strB: "nan", indexB: 0,
                               length: 3) == 0;
            if (!special && number.TypeName == BareNumberType(source: source, number: number))
            {
                continue;
            }

            Token name = tokens[index: before - 1];
            number.Fix = SuffixFix.Annotate;
            number.DeclaredNameEnd = OriginalOffset(source: source, candidateOffset: name.Position + name.Text.Length);
        }
    }

    /// <summary>The type a bare number takes with nothing around it: <c>S64</c> and <c>B64</c> in RazorForge,
    /// <c>Integer</c> and <c>Decimal</c> in Suflae, and <c>B64</c> for a hex float in both.</summary>
    private static string BareNumberType(SuffixSource source, SuffixedNumber number)
    {
        if (number.Decimal && !number.BaseTen)
        {
            return "B64";
        }

        bool suflae = source.Language == Language.Suflae;
        return number.Decimal
            ? suflae ? "Decimal" : "B64"
            : suflae ? "Integer" : "S64";
    }

    /// <summary>The offset in the file as written of <paramref name="candidateOffset"/> in the file with every
    /// suffix off.</summary>
    private static int OriginalOffset(SuffixSource source, int candidateOffset)
    {
        int offset = candidateOffset;
        foreach (SuffixedNumber number in source.Numbers)
        {
            if (number.SuffixStart < offset)
            {
                offset += number.SuffixLength;
            }
        }

        return offset;
    }

    /// <summary>The file with each number's fix applied: a dropped suffix, a typed declaration, or the suffix kept.</summary>
    private static string SuffixCandidate(SuffixSource source)
    {
        var edits = new List<MigrationEdit>();
        foreach (SuffixedNumber number in source.Numbers)
        {
            if (number.Fix == SuffixFix.Keep)
            {
                continue;
            }

            edits.Add(item: new MigrationEdit(Start: number.SuffixStart, Length: number.SuffixLength, Replacement: "",
                Line: number.Line, What: "suffix"));
            if (number.Fix == SuffixFix.Annotate)
            {
                edits.Add(item: new MigrationEdit(Start: number.DeclaredNameEnd, Length: 0,
                    Replacement: $": {number.TypeName}", Line: number.Line, What: "type"));
            }
        }

        return ApplyMigrationEdits(source: source.Text, edits: edits);
    }

    /// <summary>Decides a number that analysis typed, found at <paramref name="at"/> in the analyzed file: the suffix
    /// goes when every analysis of the bare number gave it the suffix's type, and stays (listed) otherwise.</summary>
    private static void DecideSuffixFix(SuffixSource source, SuffixedNumber number, int at,
        Dictionary<(string File, int Position), HashSet<string>> observed)
    {
        if (number.ShiftAmount)
        {
            return;
        }

        if (!observed.TryGetValue(key: (source.Path, at), value: out HashSet<string>? types) &&
            !(at > 0 && observed.TryGetValue(key: (source.Path, at - 1), value: out types)))
        {
            number.Fix = SuffixFix.Keep;
            number.Why = $"analysis did not reach this {number.TypeName} number, so where its type comes from is " +
                         "unknown";
            return;
        }

        if (types.Count == 1 && types.Contains(item: number.TypeName))
        {
            return;
        }

        number.Fix = SuffixFix.Keep;
        number.Why = $"bare, this {number.TypeName} number would be {string.Join(separator: " or ", values: types.Order())}: " +
                     $"write the type where the value is declared (var x: {number.TypeName} = ...)";
    }

    /// <summary>The offset in the candidate file (every suffix off but the kept ones, declarations typed) of
    /// <paramref name="originalOffset"/> in the file as written.</summary>
    private static int CandidateOffset(SuffixSource source, int originalOffset)
    {
        int offset = originalOffset;
        foreach (SuffixedNumber number in source.Numbers)
        {
            if (number.Fix != SuffixFix.Keep && number.SuffixStart < originalOffset)
            {
                offset -= number.SuffixLength;
            }

            if (number.Fix == SuffixFix.Annotate && number.DeclaredNameEnd <= originalOffset)
            {
                offset += number.TypeName.Length + 2;
            }
        }

        return offset;
    }

    /// <summary>The standard library folder (<c>&lt;Language&gt;/Standard</c>) <paramref name="file"/> is in, or null.</summary>
    private static string? StandardRootOf(string file)
    {
        DirectoryInfo? directory = new FileInfo(fileName: file).Directory;
        while (directory?.Parent != null)
        {
            if (directory.Name == "Standard" && directory.Parent.Name is "RazorForge" or "Suflae")
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Runs analysis over the candidate files in a child process (a fresh one, so nothing analyzed before the files
    /// changed is reused) and returns the types it gave each bare number, by file and offset. A standard library
    /// file is analyzed by validating that library, read from the source folders through a temporary
    /// <c>Standard</c> folder of links, and every other file is checked as a program.
    /// </summary>
    private static Dictionary<(string File, int Position), HashSet<string>> ProbeSuffixTypes(List<SuffixSource> sources)
    {
        return ProbeAnalysisTypes(files: sources.Select(selector: s => (s.Path, s.StandardRoot)).ToList(),
            mode: "--suffix-probe");
    }

    /// <summary>
    /// Runs analysis over <paramref name="files"/> in a child process and reads back what it observed, by file and
    /// offset: the type of each bare number (<c>--suffix-probe</c>), or the operand types of each comparison as
    /// <c>left|right</c> (<c>--until-probe</c>).
    /// </summary>
    private static Dictionary<(string File, int Position), HashSet<string>> ProbeAnalysisTypes(
        List<(string Path, string? StandardRoot)> files, string mode)
    {
        string work = Path.Combine(path1: Path.GetTempPath(), path2: $"migrate-probe-{Environment.ProcessId}");
        Directory.CreateDirectory(path: work);
        try
        {
            string? standard = null;
            List<string> roots = files.Select(selector: s => s.StandardRoot).OfType<string>().Distinct().ToList();
            if (roots.Count > 0)
            {
                // Both libraries sit side by side under the workspace: <workspace>/RazorForge/Standard and
                // <workspace>/Suflae/Standard. The builder reads them as Standard/RazorForge and Standard/Suflae.
                string workspace = Path.GetDirectoryName(path: Path.GetDirectoryName(path: roots[index: 0])!)!;
                standard = Path.Combine(path1: work, path2: "Standard");
                Directory.CreateDirectory(path: standard);
                foreach (string name in new[] { "RazorForge", "Suflae" })
                {
                    string target = Path.Combine(path1: workspace, path2: name, path3: "Standard");
                    if (Directory.Exists(path: target))
                    {
                        Directory.CreateSymbolicLink(path: Path.Combine(path1: standard, path2: name), pathToTarget: target);
                    }
                }
            }

            string list = Path.Combine(path1: work, path2: "files.txt");
            File.WriteAllLines(path: list, contents: files.Select(selector: s =>
                (s.StandardRoot != null ? "stdlib\t" : "program\t") + s.Path +
                (s.StandardRoot != null ? "\t" + s.StandardRoot : "")));
            string output = Path.Combine(path1: work, path2: "types.txt");

            var start = new ProcessStartInfo(fileName: Environment.ProcessPath!)
            {
                UseShellExecute = false
            };
            string? entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (entry != null && entry.EndsWith(value: ".dll", comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(item: entry);
            }

            start.ArgumentList.Add(item: "migrate");
            start.ArgumentList.Add(item: mode);
            start.ArgumentList.Add(item: list);
            start.ArgumentList.Add(item: output);
            if (standard != null)
            {
                start.Environment[key: "FORGE_STDLIB"] = standard;
            }

            start.Environment[key: "RF_NO_DAEMON"] = "1";
            using Process child = Process.Start(startInfo: start)!;
            child.WaitForExit();
            if (!File.Exists(path: output))
            {
                throw new InvalidOperationException(message: $"the analysis child exited with {child.ExitCode} and reported nothing");
            }

            var observed = new Dictionary<(string File, int Position), HashSet<string>>();
            foreach (string line in File.ReadLines(path: output))
            {
                string[] parts = line.Split(separator: '\t');
                (string, int) key = (parts[0], int.Parse(s: parts[1]));
                if (!observed.TryGetValue(key: key, value: out HashSet<string>? types))
                {
                    types = [];
                    observed[key: key] = types;
                }

                types.Add(item: parts[2]);
            }

            return observed;
        }
        finally
        {
            Directory.Delete(path: work, recursive: true);
        }
    }

    /// <summary>
    /// The child side of <see cref="ProbeSuffixTypes"/>: analyzes the files listed in <paramref name="list"/> and writes
    /// one line per bare number analysis typed in them (file, offset, type) to <paramref name="output"/>.
    /// </summary>
    private static int RunSuffixProbe(string list, string output, bool comparisons = false)
    {
        var programs = new List<string>();
        var stdlibFiles = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path: list))
        {
            string[] parts = line.Split(separator: '\t');
            if (parts[0] == "stdlib")
            {
                stdlibFiles[key: parts[1]] = parts[2];
            }
            else
            {
                programs.Add(item: parts[1]);
            }
        }

        var wanted = new HashSet<string>(collection: programs.Concat(second: stdlibFiles.Keys), comparer: StringComparer.Ordinal);
        string stdlibPath = StdlibLoader.GetDefaultStdlibPath();
        // What analysis observed, read at the next flush: a bare number's type is read then (analysis may set it more
        // than once), a comparison's operand types when it typed them.
        var observedNow = new List<(SourceLocation At, int Position, Func<string> Type)>();
        if (comparisons)
        {
            // The loops `until` replaces are read as they were written.
            Builder.Parser.Parser.ReadsWhileLoops = true;
            BinaryExpression.OperandsTyped = (binary, left, right) =>
            {
                if (binary.OperatorPosition >= 0)
                {
                    string both = $"{left.Name}|{right.Name}";
                    observedNow.Add(item: (binary.Location, binary.OperatorPosition, () => both));
                }
            };
        }
        else
        {
            var seen = new HashSet<LiteralExpression>(comparer: ReferenceEqualityComparer.Instance);
            LiteralExpression.TypeAssigned = literal =>
            {
                if (literal.LiteralType is TokenType.UndecidedInteger or TokenType.UndecidedDecimal && seen.Add(item: literal))
                {
                    observedNow.Add(item: (literal.Location, literal.Location.Position,
                        () => literal.ResolvedType?.Name ?? "?"));
                }
            };
        }

        // A standard library file is read through the temporary Standard folder: its path there maps back to the
        // source folder it links to.
        string MapBack(string file)
        {
            string full = Path.GetFullPath(path: file);
            foreach (string name in new[] { "RazorForge", "Suflae" })
            {
                string linked = Path.Combine(path1: stdlibPath, path2: name) + Path.DirectorySeparatorChar;
                if (full.StartsWith(value: linked, comparisonType: StringComparison.Ordinal))
                {
                    string root = stdlibFiles.Values.FirstOrDefault(predicate: r =>
                        Path.GetFileName(path: Path.GetDirectoryName(path: r)) == name) ?? "";
                    return root.Length == 0 ? full : Path.Combine(path1: root, path2: full[linked.Length..]);
                }
            }

            return full;
        }

        // A program's analysis reaches the library too, but the library's own validation already typed every number
        // there, so a program reports only its own files. Each line is written once. A library file is reported by
        // one analysis only: the first that reaches it.
        var written = new HashSet<string>(comparer: StringComparer.Ordinal);
        var reported = new HashSet<string>(comparer: StringComparer.Ordinal);
        void Flush(StreamWriter writer, bool library)
        {
            var reachedNow = new HashSet<string>(comparer: StringComparer.Ordinal);
            foreach ((SourceLocation at, int position, Func<string> type) in observedNow)
            {
                string file = MapBack(file: at.FileName);
                if (wanted.Contains(item: file) && stdlibFiles.ContainsKey(key: file) == library &&
                    !(library && reported.Contains(item: file)))
                {
                    reachedNow.Add(item: file);
                    string line = $"{file}\t{position}\t{type()}";
                    if (written.Add(item: line))
                    {
                        writer.WriteLine(value: line);
                    }
                }
            }

            observedNow.Clear();
            reported.UnionWith(other: reachedNow);
        }

        using var writer = new StreamWriter(path: output);
        foreach (Language language in stdlibFiles.Values
                    .Select(selector: r => Path.GetFileName(path: Path.GetDirectoryName(path: r)) == "Suflae" ? Language.Suflae : Language.RazorForge)
                    .Distinct())
        {
            try
            {
                var analyzer = new SemanticVerifier(language: language);
                analyzer.ValidateStdlibBodies();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(value: $"validating the {language} library stopped: {e.Message}");
            }

            Flush(writer: writer, library: true);

            // Validation covers System and what System imports. The other modules (IO, Math3D, Random, ...) are loaded
            // first and validated the same way.
            try
            {
                var everything = new SemanticVerifier(language: language);
                var at = new SourceLocation(FileName: "<migrate>", Line: 0, Column: 0, Position: 0);
                foreach (string module in new StdlibLoader(stdlibRoot: stdlibPath, language: language).ScanModuleNames())
                {
                    everything.Registry.LoadModule(importPath: module, currentFile: "<migrate>", location: at,
                        effectiveModule: out _);
                }

                everything.ValidateStdlibBodies();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(value: $"analyzing every {language} module stopped: {e.Message}");
            }

            Flush(writer: writer, library: true);
        }

        foreach (string program in programs)
        {
            try
            {
                ProbeProgram(entryFile: program);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(value: $"{program}: analysis stopped: {e.Message}");
            }

            Flush(writer: writer, library: false);
        }

        LiteralExpression.TypeAssigned = null;
        BinaryExpression.OperandsTyped = null;
        Builder.Parser.Parser.ReadsWhileLoops = false;
        return 0;
    }

    /// <summary>Analyzes one program the way <c>check</c> does, without reporting anything.</summary>
    private static void ProbeProgram(string entryFile)
    {
        ResolvedEntry resolved = ResolveEntryFile(args: ["check", entryFile], needsOutputArg: false);
        if (resolved.EntryFile == null)
        {
            return;
        }

        Language language = SourceLanguage(path: resolved.EntryFile);
        string projectRoot = resolved.ProjectRoot ?? Path.GetDirectoryName(path: Path.GetFullPath(path: entryFile)) ?? ".";
        string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();
        var driver = new BuildDriver(projectRoot: projectRoot,
            stdlibRoot: stdlibRoot,
            language: language,
            libraryRoots: resolved.LibraryRoots);
        BuildResult buildResult = driver.CompileFile(entryFile: Path.GetFullPath(path: resolved.EntryFile));
        if (buildResult.Errors.Count > 0)
        {
            Console.Error.WriteLine(value: $"{entryFile}: does not parse, so analysis cannot type it " +
                                           $"({buildResult.Errors[index: 0].Message})");
            return;
        }

        List<FileBuildUnit> userUnits = FilterUserUnits(buildResult: buildResult, stdlibRoot: stdlibRoot);
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles =
            OrderUserFiles(userUnits: userUnits, initializationOrder: buildResult.InitializationOrder);
        var analyzer = new SemanticVerifier(language: language);
        analyzer.Registry.UseModuleResolver(resolver: driver.Resolver);
        AnalysisResult _ = analyzer.AnalyzeMultiple(files: orderedFiles);
    }
}
