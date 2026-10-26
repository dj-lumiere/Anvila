using Builder.Tokenizer;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>The integer types: a comparison of two of them can be turned around (<c>not (a &lt; b)</c> is
    /// <c>a &gt;= b</c>), which a float comparison can't (a NaN makes both false).</summary>
    private static readonly HashSet<string> UntilIntegerTypes =
    [
        "S8", "S16", "S32", "S64", "S128", "S256", "U8", "U16", "U32", "U64", "U128", "U256", "Int", "Integer", "Byte", "Char"
    ];

    /// <summary>What <c>migrate --step until</c> did, counted over every file.</summary>
    private sealed class UntilCounts
    {
        public int Rewritten { get; set; }
        public int Listed { get; set; }
    }

    /// <summary>
    /// <c>migrate --step until</c>: a loop is <c>until</c> with the condition that ends it, or <c>loop</c>, and
    /// <c>while</c> is gone. <c>while true</c> is <c>loop</c>, <c>while not c</c> is <c>until c</c>, and a comparison
    /// is turned around when analysis says it compares integers (<c>while i &lt; n</c> is <c>until i &gt;= n</c>), or
    /// any values for <c>==</c> / <c>!=</c>. An <c>and</c> / <c>or</c> of such parts is turned around part by part.
    /// A float comparison stays as written, and so does any other condition: it becomes <c>until not (c)</c> and is
    /// listed, so a person can write a clearer one.
    /// </summary>
    private static int RunUntilStep(List<string> paths, bool check)
    {
        var files = new List<string>();
        foreach (string path in paths)
        {
            if (File.Exists(path: path))
            {
                files.Add(item: Path.GetFullPath(path: path));
            }
            else if (Directory.Exists(path: path))
            {
                files.AddRange(collection: Directory.EnumerateFiles(path: path, searchPattern: "*", searchOption: SearchOption.AllDirectories)
                    // The other language's sources are read by its own tool, whose analysis types them.
                    .Where(predicate: f => f.EndsWith(value: CliRules.FileExtension, comparisonType: StringComparison.Ordinal) ||
                                           f.EndsWith(value: ".md", comparisonType: StringComparison.Ordinal))
                    .Where(predicate: f => !f.Contains(value: $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                           !f.Contains(value: $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    .Select(selector: Path.GetFullPath)
                    .Order());
            }
            else
            {
                Console.Error.WriteLine(value: $"{path}: no such file or directory");
            }
        }

        files = files.Distinct().ToList();
        var texts = files.ToDictionary(keySelector: f => f, elementSelector: File.ReadAllText);

        // Analysis types the comparisons of the sources that have a while loop. A page's code blocks are not
        // programs, so their comparisons are decided from the source alone.
        List<(string Path, string? StandardRoot)> analyzed = files
            .Where(predicate: f => !f.EndsWith(value: ".md", comparisonType: StringComparison.Ordinal) &&
                                   texts[key: f].Contains(value: "while", comparisonType: StringComparison.Ordinal))
            .Select(selector: f => (f, StandardRootOf(file: f)))
            .ToList();
        Dictionary<(string File, int Position), HashSet<string>> observed = [];
        if (analyzed.Count > 0)
        {
            try
            {
                observed = ProbeAnalysisTypes(files: analyzed, mode: "--until-probe");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(value: $"warning: migrate: analysis did not run, so no comparison is turned around: {e.Message}");
            }
        }

        var counts = new UntilCounts();
        int changedFiles = 0;
        foreach (string file in files)
        {
            string source = texts[key: file];
            string rewritten = file.EndsWith(value: ".md", comparisonType: StringComparison.Ordinal)
                ? RewriteInMarkdown(file: file, text: source, rewrite: (f, s, l, o) =>
                    RewriteUntil(file: f, source: s, language: l, lineOffset: o, observed: null, counts: counts))
                : RewriteUntil(file: file, source: source, language: SourceLanguage(path: file), lineOffset: 0,
                    observed: observed, counts: counts);
            if (rewritten == source)
            {
                continue;
            }

            changedFiles++;
            if (check)
            {
                Console.WriteLine(value: file);
            }
            else
            {
                File.WriteAllText(path: file, contents: rewritten);
            }
        }

        Console.WriteLine(value: $"{changedFiles} file(s) {(check ? "would change" : "changed")}: {counts.Rewritten} loop(s) " +
                                 $"rewritten, {counts.Listed} listed to read by hand");
        return check && changedFiles > 0 ? 1 : 0;
    }

    /// <summary>The <c>while</c> loops of one source rewritten (see <see cref="RunUntilStep"/>). <paramref name="observed"/>
    /// holds the operand types analysis gave each comparison, by file and operator offset, or is null when the source
    /// was not analyzed.</summary>
    private static string RewriteUntil(string file, string source, Language language, int lineOffset,
        Dictionary<(string File, int Position), HashSet<string>>? observed, UntilCounts counts)
    {
        // A number with a type suffix is read without it (see TokenizeAroundSuffixes), and the edits are put back
        // in place around the suffixes.
        List<Token> tokens;
        string original = source;
        List<(int At, int Length)> suffixes;
        try
        {
            (tokens, source, suffixes) = TokenizeAroundSuffixes(source: original, file: file, language: language);
        }
        catch (Exception e)
        {
            ReportUnreadable(file: file, error: e);
            return original;
        }

        var edits = new List<MigrationEdit>();
        for (int w = 0; w < tokens.Count; w++)
        {
            if (tokens[index: w] is not { Type: TokenType.Identifier, Text: "while" } word || word.Position < 0 ||
                w > 0 && tokens[index: w - 1].Type is not (TokenType.Newline or TokenType.Indent or TokenType.Dedent) ||
                w + 1 >= tokens.Count || tokens[index: w + 1].Line != word.Line ||
                tokens[index: w + 1].Type is TokenType.Assign or TokenType.Dot or TokenType.Colon or TokenType.Comma or
                    TokenType.LeftBracket || tokens[index: w + 1].Type == TokenType.LeftParen &&
                tokens[index: w + 1].Position == word.Position + word.Text.Length)
            {
                continue;
            }

            int end = ConditionEnd(tokens: tokens, from: w + 1);
            if (end == w + 1)
            {
                continue;
            }

            if (end == w + 2 && tokens[index: w + 1].Type is TokenType.True)
            {
                Token truth = tokens[index: w + 1];
                edits.Add(item: new MigrationEdit(Start: word.Position, Length: truth.Position + truth.Text.Length - word.Position,
                    Replacement: "loop", Line: word.Line, What: "loop"));
                counts.Rewritten++;
                continue;
            }

            var own = new List<MigrationEdit>
            {
                new(Start: word.Position, Length: word.Text.Length, Replacement: "until", Line: word.Line, What: "until")
            };
            bool plain = InvertUntilCondition(tokens: tokens, from: w + 1, to: end, source: source, file: file,
                observed: observed, edits: own, top: true);

            edits.AddRange(collection: own);
            if (plain)
            {
                counts.Rewritten++;
            }
            else
            {
                counts.Listed++;
                string written = source[word.Position..TokenEnd(tokens: tokens, index: end - 1, source: source)];
                string now = ApplyMigrationEdits(source: written,
                    edits: own.Select(selector: e => e with { Start = e.Start - word.Position }).ToList());
                Console.WriteLine(value: $"{file}:{word.Line + lineOffset}: {string.Join(separator: ' ', values: now.Split(separator: (char[]?)null, options: StringSplitOptions.RemoveEmptyEntries))}: " +
                                         "read it, a clearer condition may say the same" + Typed(tokens: tokens, from: w + 1, to: end, file: file, observed: observed));
            }
        }

        return edits.Count == 0
            ? original
            : ApplyMigrationEdits(source: original,
                edits: edits.Select(selector: e => AroundSuffixes(edit: e, suffixes: suffixes)).ToList());
    }

    /// <summary>Where a loop's condition that starts at <paramref name="from"/> ends: the first token, outside
    /// brackets, that ends the line or starts the carried values (<c>with</c>).</summary>
    private static int ConditionEnd(List<Token> tokens, int from)
    {
        int depth = 0;
        for (int i = from; i < tokens.Count; i++)
        {
            TokenType type = tokens[index: i].Type;
            if (type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace)
            {
                depth++;
            }
            else if (type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace)
            {
                depth--;
            }
            else if (depth <= 0 && type is TokenType.Newline or TokenType.Indent or TokenType.Dedent or TokenType.Eof
                         or TokenType.With)
            {
                return i;
            }
        }

        return tokens.Count;
    }

    /// <summary>The operand types analysis gave the comparisons of a listed condition, to say why one wasn't turned
    /// around, or nothing.</summary>
    private static string Typed(List<Token> tokens, int from, int to, string file,
        Dictionary<(string File, int Position), HashSet<string>>? observed)
    {
        if (observed == null)
        {
            return "";
        }

        var types = new List<string>();
        for (int i = from; i < to; i++)
        {
            if (observed.TryGetValue(key: (file, tokens[index: i].Position), value: out HashSet<string>? seen) &&
                tokens[index: i].Type is TokenType.Less or TokenType.LessEqual or TokenType.Greater or TokenType.GreaterEqual)
            {
                types.Add(item: $"'{tokens[index: i].Text}' compares {string.Join(separator: " or ", values: seen.Select(selector: s => s.Replace(oldValue: "|", newValue: " with ")))}");
            }
        }

        return types.Count == 0 ? "" : $" ({string.Join(separator: ", ", values: types)})";
    }

    /// <summary>Whether analysis typed the comparison at <paramref name="op"/> as one of unsigned integers, every
    /// time.</summary>
    private static bool ComparesUnsigned(Token op, string file, Dictionary<(string File, int Position), HashSet<string>>? observed)
    {
        return observed != null && observed.TryGetValue(key: (file, op.Position), value: out HashSet<string>? types) &&
               types.Count > 0 && types.All(predicate: pair => pair.Split(separator: '|').All(predicate: name =>
                   name is "U8" or "U16" or "U32" or "U64" or "U128" or "U256" or "Byte"));
    }

    /// <summary>The integer compare intrinsic that holds exactly when <paramref name="name"/> doesn't, or null.</summary>
    private static string? UntilOppositeIntrinsic(string name)
    {
        return name switch
        {
            "signed_lt" => "signed_ge",
            "signed_ge" => "signed_lt",
            "signed_gt" => "signed_le",
            "signed_le" => "signed_gt",
            "unsigned_lt" => "unsigned_ge",
            "unsigned_ge" => "unsigned_lt",
            "unsigned_gt" => "unsigned_le",
            "unsigned_le" => "unsigned_gt",
            "int_eq" => "int_ne",
            "int_ne" => "int_eq",
            _ => null
        };
    }

    /// <summary>The index of the bracket that closes the one at <paramref name="open"/>, or -1.</summary>
    private static int MatchingClose(List<Token> tokens, int open)
    {
        int depth = 0;
        for (int i = open; i < tokens.Count; i++)
        {
            if (tokens[index: i].Type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace)
            {
                depth++;
            }
            else if (tokens[index: i].Type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace &&
                     --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Where the token at <paramref name="index"/> ends in <paramref name="source"/>. A literal token keeps
    /// its contents, not its spelling, so its end is where the next token starts, spaces left out.</summary>
    private static int TokenEnd(List<Token> tokens, int index, string source)
    {
        Token token = tokens[index: index];
        if (token.Position + token.Text.Length <= source.Length &&
            string.CompareOrdinal(strA: source, indexA: token.Position, strB: token.Text, indexB: 0, length: token.Text.Length) == 0)
        {
            return token.Position + token.Text.Length;
        }

        int next = index + 1 < tokens.Count && tokens[index: index + 1].Position > token.Position
            ? tokens[index: index + 1].Position
            : source.Length;
        int end = next;
        while (end > token.Position && char.IsWhiteSpace(c: source[index: end - 1]))
        {
            end--;
        }

        return end;
    }

    /// <summary>
    /// Adds the edits that turn the condition in tokens <paramref name="from"/> to <paramref name="to"/> (exclusive)
    /// into the condition that ends the loop: a group's inside, each part of an <c>and</c> / <c>or</c> (which swap), a
    /// <c>not</c> taken off, a comparison turned around, <c>true</c> / <c>false</c> swapped. False when a part was
    /// neither and got a <c>not</c> in front.
    /// </summary>
    private static bool InvertUntilCondition(List<Token> tokens, int from, int to, string source, string file,
        Dictionary<(string File, int Position), HashSet<string>>? observed, List<MigrationEdit> edits, bool top = false)
    {
        Token first = tokens[index: from];
        if (first.Type == TokenType.LeftParen && MatchingClose(tokens: tokens, open: from) == to - 1)
        {
            return InvertUntilCondition(tokens: tokens, from: from + 1, to: to - 1, source: source, file: file,
                observed: observed, edits: edits);
        }

        // The parts of an `and` / `or`: and and or don't mix without brackets, so one of them joins them.
        var joins = new List<int>();
        int depth = 0;
        for (int i = from; i < to; i++)
        {
            TokenType type = tokens[index: i].Type;
            if (type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace)
            {
                depth++;
            }
            else if (type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace)
            {
                depth--;
            }
            else if (depth == 0 && type is TokenType.And or TokenType.Or)
            {
                joins.Add(item: i);
            }
        }

        if (joins.Count > 0)
        {
            bool plain = true;
            int start = from;
            foreach (int join in joins.Append(element: to))
            {
                plain &= InvertUntilCondition(tokens: tokens, from: start, to: join, source: source, file: file,
                    observed: observed, edits: edits);
                if (join < to)
                {
                    Token joiner = tokens[index: join];
                    edits.Add(item: new MigrationEdit(Start: joiner.Position, Length: joiner.Text.Length,
                        Replacement: joiner.Type == TokenType.And ? "or" : "and", Line: joiner.Line, What: "join"));
                }

                start = join + 1;
            }

            return plain;
        }

        if (first.Type == TokenType.Not && from + 1 < to)
        {
            edits.Add(item: new MigrationEdit(Start: first.Position, Length: tokens[index: from + 1].Position - first.Position,
                Replacement: "", Line: first.Line, What: "not"));
            // `while not (a and b)` is `until a and b`.
            if (top && tokens[index: from + 1].Type == TokenType.LeftParen && MatchingClose(tokens: tokens, open: from + 1) == to - 1)
            {
                edits.Add(item: new MigrationEdit(Start: tokens[index: from + 1].Position, Length: 1, Replacement: "",
                    Line: first.Line, What: "("));
                edits.Add(item: new MigrationEdit(Start: tokens[index: to - 1].Position, Length: 1, Replacement: "",
                    Line: first.Line, What: ")"));
            }

            return true;
        }

        if (to == from + 1 && first.Type is TokenType.True or TokenType.False)
        {
            edits.Add(item: new MigrationEdit(Start: first.Position, Length: first.Text.Length,
                Replacement: first.Type == TokenType.True ? "false" : "true", Line: first.Line, What: "truth"));
            return true;
        }

        // One comparison at the top of the part.
        var operators = new List<int>();
        bool simple = true;
        depth = 0;
        for (int i = from; i < to; i++)
        {
            TokenType type = tokens[index: i].Type;
            if (type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace)
            {
                depth++;
            }
            else if (type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace)
            {
                depth--;
            }
            else if (depth == 0)
            {
                if (type is TokenType.Less or TokenType.LessEqual or TokenType.Greater or TokenType.GreaterEqual
                    or TokenType.Equal or TokenType.NotEqual or TokenType.IdentityEqual or TokenType.IdentityNotEqual
                    or TokenType.Is or TokenType.IsNot)
                {
                    operators.Add(item: i);
                }

                if (type is not (TokenType.Identifier or TokenType.Me or TokenType.Dot or TokenType.True or TokenType.False
                    or TokenType.None or TokenType.NoneValue or TokenType.UndecidedInteger or TokenType.UndecidedDecimal) &&
                    !type.ToString().EndsWith(value: "Literal", comparisonType: StringComparison.Ordinal))
                {
                    simple = false;
                }
            }
        }

        if (operators.Count == 1)
        {
            Token op = tokens[index: operators[index: 0]];
            string? turned = op.Type switch
            {
                TokenType.Equal => "!=",
                TokenType.NotEqual => "==",
                TokenType.IdentityEqual => "!==",
                TokenType.IdentityNotEqual => "===",
                TokenType.Less when ComparesIntegers(tokens: tokens, op: operators[index: 0], from: from, to: to, file: file, observed: observed) => ">=",
                TokenType.LessEqual when ComparesIntegers(tokens: tokens, op: operators[index: 0], from: from, to: to, file: file, observed: observed) => ">",
                TokenType.Greater when ComparesIntegers(tokens: tokens, op: operators[index: 0], from: from, to: to, file: file, observed: observed) => "<=",
                TokenType.GreaterEqual when ComparesIntegers(tokens: tokens, op: operators[index: 0], from: from, to: to, file: file, observed: observed) => "<",
                // `x isnot None` is `x is None`: a pattern that binds a name can't be turned around.
                TokenType.Is or TokenType.IsNot when operators[index: 0] + 2 == to &&
                                                     tokens[index: to - 1].Type is TokenType.Identifier or TokenType.None or TokenType.NoneValue =>
                    op.Type == TokenType.Is ? "isnot" : "is",
                _ => null
            };
            // An unsigned count is never below 0: `n > 0` ends at `n == 0`.
            if (turned == "<=" && operators[index: 0] + 2 == to && tokens[index: to - 1] is { Type: TokenType.UndecidedInteger, Text: "0" } &&
                ComparesUnsigned(op: op, file: file, observed: observed))
            {
                turned = "==";
            }

            if (turned != null)
            {
                edits.Add(item: new MigrationEdit(Start: op.Position, Length: op.Text.Length, Replacement: turned,
                    Line: op.Line, What: "compare"));
                return true;
            }
        }

        // An integer compare of the LLVM intrinsics has its opposite: `LLVM::signed_lt(a:, b:)` ends where
        // `LLVM::signed_ge(a:, b:)` holds. A float compare doesn't (NaN).
        if (to - from >= 4 && first is { Type: TokenType.Identifier, Text: "LLVM" } &&
            tokens[index: from + 1].Text == "::" && tokens[index: from + 2] is { Type: TokenType.Identifier } intrinsic &&
            UntilOppositeIntrinsic(name: intrinsic.Text) is { } opposite &&
            (tokens[index: from + 3].Type == TokenType.LeftParen && MatchingClose(tokens: tokens, open: from + 3) == to - 1 ||
             tokens[index: from + 3].Type == TokenType.LeftBracket &&
             MatchingClose(tokens: tokens, open: from + 3) is var typeArgsEnd and > 0 && typeArgsEnd + 1 < to &&
             tokens[index: typeArgsEnd + 1].Type == TokenType.LeftParen &&
             MatchingClose(tokens: tokens, open: typeArgsEnd + 1) == to - 1))
        {
            edits.Add(item: new MigrationEdit(Start: intrinsic.Position, Length: intrinsic.Text.Length, Replacement: opposite,
                Line: intrinsic.Line, What: "intrinsic"));
            return true;
        }

        // Anything else gets a `not` in front, in brackets unless it is one name, call or member chain.
        int partEnd = TokenEnd(tokens: tokens, index: to - 1, source: source);
        if (simple)
        {
            edits.Add(item: new MigrationEdit(Start: first.Position, Length: 0, Replacement: "not ", Line: first.Line,
                What: "not"));
        }
        else
        {
            edits.Add(item: new MigrationEdit(Start: first.Position, Length: 0, Replacement: "not (", Line: first.Line,
                What: "not"));
            edits.Add(item: new MigrationEdit(Start: partEnd, Length: 0, Replacement: ")", Line: first.Line, What: ")"));
        }

        return false;
    }

    /// <summary>
    /// Whether the comparison at <paramref name="op"/> compares integers: analysis said so for every time it typed
    /// it, or, when analysis didn't type it, one side counts something (<c>xs.count()</c>, <c>me.length</c>) and no
    /// side holds a number with a point.
    /// </summary>
    private static bool ComparesIntegers(List<Token> tokens, int op, int from, int to, string file,
        Dictionary<(string File, int Position), HashSet<string>>? observed)
    {
        if (observed != null && observed.TryGetValue(key: (file, tokens[index: op].Position), value: out HashSet<string>? types))
        {
            return types.Count > 0 && types.All(predicate: pair =>
                pair.Split(separator: '|').All(predicate: name => UntilIntegerTypes.Contains(item: name)));
        }

        List<Token> part = tokens.GetRange(index: from, count: to - from);
        if (part.Any(predicate: t => t.Type == TokenType.UndecidedDecimal || t.Text.Contains(value: '.') &&
                         t.Type is not (TokenType.Dot or TokenType.Identifier) && char.IsDigit(c: t.Text[index: 0])))
        {
            return false;
        }

        for (int i = from + 1; i < to; i++)
        {
            if (tokens[index: i - 1].Type == TokenType.Dot && tokens[index: i].Text is "count" or "length")
            {
                return true;
            }
        }

        return false;
    }
}
