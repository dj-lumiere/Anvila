using TypeModel.Enums;
using Builder.Tokenizer;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>
    /// One rewrite <c>migrate</c> makes: <paramref name="Start"/> and <paramref name="Length"/> are the source span it
    /// replaces with <paramref name="Replacement"/>, and <paramref name="What"/> says what changed, for the report.
    /// </summary>
    private sealed record MigrationEdit(int Start, int Length, string Replacement, int Line, string What);

    /// <summary>
    /// <c>migrate [--check] &lt;file-or-dir&gt;...</c>: moves sources written for the previous release to this one. It
    /// reads each file with this release's lexer and rewrites only tokens (never the inside of a string or a
    /// comment): the renamed keywords, and the keywords that went away. A name that became a keyword cannot be
    /// renamed for you (only you know the better name), so it is listed instead. With <c>--check</c> nothing is
    /// written and the exit code is 1 when a file would change.
    /// </summary>
    private static int RunMigrateCommand(string[] args)
    {
        bool check = args.Contains(value: "--check");
        var paths = args.Skip(count: 1).Where(predicate: a => a != "--check").ToList();
        if (paths.Count == 0)
        {
            Console.Error.WriteLine(value: $"usage: {CliRules.ToolName} migrate [--check] <file-or-dir>...");
            return 2;
        }

        int changedFiles = 0;
        int namesToRename = 0;
        foreach (string file in MigrationSources(paths: paths))
        {
            string source = File.ReadAllText(path: file);
            Language language = SourceLanguage(path: file);
            List<Token> tokens;
            try
            {
                tokens = Lexers.Tokenize(source: source, fileName: file, language: language);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(value: $"{file}: cannot read it: {e.Message}");
                continue;
            }

            List<MigrationEdit> edits = MigrationEdits(tokens: tokens, file: file, language: language);
            foreach (Token name in KeywordsUsedAsNames(tokens: tokens))
            {
                Console.WriteLine(value: $"{file}:{name.Line}: '{name.Text}' is a keyword now, rename it by hand");
                namesToRename++;
            }

            if (edits.Count == 0)
            {
                continue;
            }

            changedFiles++;
            foreach (MigrationEdit edit in edits)
            {
                Console.WriteLine(value: $"{file}:{edit.Line}: {edit.What}");
            }

            if (!check)
            {
                File.WriteAllText(path: file, contents: ApplyMigrationEdits(source: source, edits: edits));
            }
        }

        string verb = check ? "would change" : "changed";
        Console.WriteLine(value: $"{changedFiles} file(s) {verb}, {namesToRename} name(s) to rename by hand");
        return check && changedFiles > 0 || namesToRename > 0 ? 1 : 0;
    }

    /// <summary>The source files of this language under <paramref name="paths"/> (files as given, directories walked).</summary>
    private static IEnumerable<string> MigrationSources(List<string> paths)
    {
        foreach (string path in paths)
        {
            if (File.Exists(path: path))
            {
                yield return path;
                continue;
            }

            if (!Directory.Exists(path: path))
            {
                Console.Error.WriteLine(value: $"{path}: no such file or directory");
                continue;
            }

            foreach (string file in Directory
                        .EnumerateFiles(path: path, searchPattern: "*" + CliRules.FileExtension,
                             searchOption: SearchOption.AllDirectories)
                        .Where(predicate: f => !f.Contains(value: $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                               !f.Contains(value: $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                        .Order())
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// The rewrites for one file. A retired keyword reads as a plain name now, so each rule recognizes it by its
    /// spelling and its place: <c>grab</c> in front of an expression is <c>check</c>, and <c>discard</c> at the start of
    /// a statement is <c>drop</c> in RazorForge and goes away in Suflae (a result is dropped without a marker there).
    /// A <c>.share()</c> call goes too, because <c>=</c> does what it did.
    /// </summary>
    private static List<MigrationEdit> MigrationEdits(List<Token> tokens, string file, Language language)
    {
        bool inStandardLibrary = file.Replace(oldChar: '\\', newChar: '/').Contains(value: "/Standard/");
        var edits = new List<MigrationEdit>();
        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[index: i];
            // `is Weapon w` is `is Weapon as w`, and `else p =>` is `else as p =>`.
            if (token.Type is TokenType.Is or TokenType.Else && PatternBindingAfter(tokens: tokens, index: i) is { } bound)
            {
                edits.Add(item: new MigrationEdit(Start: bound.Position, Length: 0, Replacement: "as ", Line: bound.Line,
                    What: token.Type == TokenType.Is ? "is T x → is T as x" : "else p → else as p"));
                continue;
            }

            if (token.Type is TokenType.FormattedText or TokenType.RawFormattedText && token.Position >= 0)
            {
                edits.AddRange(collection: InsertionEdits(token: token, keepShare: inStandardLibrary));
                continue;
            }

            if (token.Type != TokenType.Identifier || token.Position < 0 || i + 1 >= tokens.Count)
            {
                continue;
            }

            Token next = tokens[index: i + 1];
            // `obeys P onlyif (T obeys A, U obeys B)` is `obeys P when T obeys A and U obeys B`.
            if (token.Text == "onlyif")
            {
                edits.AddRange(collection: ConditionalConformanceEdits(tokens: tokens, index: i));
                continue;
            }

            // The teardown routine `destroy` is `destruct` (`x.destroy()`, `routine T.destroy()`).
            if (token.Text == "destroy" && i > 0 && tokens[index: i - 1].Type == TokenType.Dot)
            {
                edits.Add(item: new MigrationEdit(Start: token.Position, Length: token.Text.Length, Replacement: "destruct",
                    Line: token.Line, What: "destroy → destruct"));
                continue;
            }

            // `unless c` (the old inverted `if`) is `if not (c)`.
            if (token.Text == "unless" && StartsStatement(tokens: tokens, index: i))
            {
                edits.AddRange(collection: UnlessEdits(tokens: tokens, index: i));
                continue;
            }

            // A routine value's type `Routine[(…), R]` is `Callable[(…), R]`.
            if (token.Text == "Routine" && next.Type == TokenType.LeftBracket)
            {
                edits.Add(item: new MigrationEdit(Start: token.Position, Length: token.Text.Length, Replacement: "Callable",
                    Line: token.Line, What: "Routine[…] → Callable[…]"));
                continue;
            }

            // The type `Character` is `Char`.
            if (token.Text == "Character")
            {
                edits.Add(item: new MigrationEdit(Start: token.Position, Length: token.Text.Length, Replacement: "Char",
                    Line: token.Line, What: "Character → Char"));
                continue;
            }

            // `x.share()` is what `=` does now: the call goes, the value stays. The standard library keeps
            // calling the copy step (it reads values out of raw memory), and a definition is left alone.
            if (token.Text == "share" && !inStandardLibrary && !OnDefinitionLine(tokens: tokens, index: i) && i > 0 && tokens[index: i - 1].Type == TokenType.Dot &&
                next.Type == TokenType.LeftParen && i + 2 < tokens.Count &&
                tokens[index: i + 2].Type == TokenType.RightParen && tokens[index: i - 1].Position >= 0)
            {
                Token dot = tokens[index: i - 1];
                Token close = tokens[index: i + 2];
                edits.Add(item: new MigrationEdit(Start: dot.Position, Length: close.Position + 1 - dot.Position,
                    Replacement: "", Line: token.Line, What: ".share() removed ('=' shares a handle)"));
                continue;
            }

            bool startsExpression = next.Line == token.Line && StartsMigratedExpression(token: next);
            if (!startsExpression)
            {
                continue;
            }

            if (token.Text == "grab")
            {
                edits.Add(item: new MigrationEdit(Start: token.Position, Length: token.Text.Length,
                    Replacement: "check", Line: token.Line, What: "grab → check"));
            }
            else if (token.Text == "discard" && StartsStatement(tokens: tokens, index: i))
            {
                if (language == Language.Suflae)
                {
                    // The space after the word goes too, so the call keeps its place.
                    int length = next.Position > token.Position ? next.Position - token.Position : token.Text.Length;
                    edits.Add(item: new MigrationEdit(Start: token.Position, Length: length, Replacement: "",
                        Line: token.Line, What: "discard removed (a result is dropped without a marker)"));
                }
                else
                {
                    edits.Add(item: new MigrationEdit(Start: token.Position, Length: token.Text.Length,
                        Replacement: "drop", Line: token.Line, What: "discard → drop"));
                }
            }
        }

        return edits;
    }

    /// <summary>
    /// The rewrites inside the insertions of an f-text (<c>f"{grab f(x)}"</c>): the text around them is output and
    /// stays as it is. An insertion is the part between a lone <c>{</c> and its <c>}</c>.
    /// </summary>
    private static IEnumerable<MigrationEdit> InsertionEdits(Token token, bool keepShare)
    {
        string text = token.Text;
        int depth = 0;
        int start = -1;
        for (int k = 0; k < text.Length; k++)
        {
            char c = text[index: k];
            if (c == '{' && depth == 0 && k + 1 < text.Length && text[index: k + 1] == '{')
            {
                k++;
                continue;
            }

            if (c == '{')
            {
                if (depth++ == 0)
                {
                    start = k + 1;
                }
            }
            else if (c == '}' && depth > 0 && --depth == 0)
            {
                string insertion = text[start..k];
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                             input: insertion, pattern: @"\bgrab(?=\s)|\.share\(\)"))
                {
                    bool isGrab = m.Value == "grab";
                    if (!isGrab && keepShare)
                    {
                        continue;
                    }

                    yield return new MigrationEdit(Start: token.Position + start + m.Index, Length: m.Length,
                        Replacement: isGrab ? "check" : "", Line: token.Line,
                        What: isGrab ? "grab → check (in an f-text)" : ".share() removed (in an f-text)");
                }
            }
        }
    }

    /// <summary>
    /// Rewrites an <c>onlyif</c> conformance condition at <paramref name="index"/> to the <c>when</c> form: the word
    /// becomes <c>when</c>, and a parenthesized list loses its parentheses with its commas becoming <c>and</c>.
    /// </summary>
    private static IEnumerable<MigrationEdit> ConditionalConformanceEdits(List<Token> tokens, int index)
    {
        Token word = tokens[index: index];
        yield return new MigrationEdit(Start: word.Position, Length: word.Text.Length, Replacement: "when",
            Line: word.Line, What: "onlyif → when");
        if (index + 1 >= tokens.Count || tokens[index: index + 1].Type != TokenType.LeftParen)
        {
            yield break;
        }

        Token open = tokens[index: index + 1];
        int depth = 0;
        for (int j = index + 1; j < tokens.Count; j++)
        {
            Token t = tokens[index: j];
            if (t.Type is TokenType.LeftParen or TokenType.LeftBracket)
            {
                depth++;
            }
            else if (t.Type is TokenType.RightParen or TokenType.RightBracket)
            {
                depth--;
                if (depth == 0)
                {
                    yield return new MigrationEdit(Start: open.Position, Length: 1, Replacement: "", Line: open.Line,
                        What: "onlyif parentheses removed");
                    yield return new MigrationEdit(Start: t.Position, Length: 1, Replacement: "", Line: t.Line,
                        What: "onlyif parentheses removed");
                    yield break;
                }
            }
            else if (t.Type == TokenType.Comma && depth == 1)
            {
                yield return new MigrationEdit(Start: t.Position, Length: 1, Replacement: " and", Line: t.Line,
                    What: "onlyif comma → and");
            }
        }
    }

    /// <summary>
    /// The name a pattern binds the old way, written straight after the type of <c>is</c> (<c>is Weapon w</c>,
    /// <c>is RF::Core.List[S64] xs</c>, <c>is $typeof(m) v</c>) or straight after <c>else</c> (<c>else p =></c>), or
    /// null. The type is skipped over: names joined by <c>.</c> or <c>::</c>, a <c>$</c> splice with its
    /// parentheses, and a bracketed argument list.
    /// </summary>
    private static Token? PatternBindingAfter(List<Token> tokens, int index)
    {
        int line = tokens[index: index].Line;
        int j = index + 1;
        if (tokens[index: index].Type == TokenType.Is)
        {
            if (j < tokens.Count && tokens[index: j].Type == TokenType.Dollar)
            {
                j += 2;
                j = SkipBalanced(tokens: tokens, start: j, open: TokenType.LeftParen, close: TokenType.RightParen);
            }
            else if (j < tokens.Count && tokens[index: j].Type is TokenType.Identifier or TokenType.None)
            {
                j++;
                while (j + 1 < tokens.Count && tokens[index: j].Type is TokenType.Dot or TokenType.DoubleColon &&
                       tokens[index: j + 1].Type == TokenType.Identifier)
                {
                    j += 2;
                }

                j = SkipBalanced(tokens: tokens, start: j, open: TokenType.LeftBracket, close: TokenType.RightBracket);
            }
            else
            {
                return null;
            }
        }

        if (j >= tokens.Count || tokens[index: j].Type != TokenType.Identifier || tokens[index: j].Line != line)
        {
            return null;
        }

        // `else p` binds only in a `when` arm: the arrow follows, or the line ends with `else` starting it (an
        // `if … then a else b` expression has `else` mid-line), and `is T x` whatever follows the name.
        Token? after = j + 1 < tokens.Count ? tokens[index: j + 1] : null;
        if (tokens[index: index].Type == TokenType.Else && after?.Type is not TokenType.FatArrow &&
            !(after?.Type == TokenType.Newline && StartsStatement(tokens: tokens, index: index)))
        {
            return null;
        }

        return after?.Type is TokenType.Dot or TokenType.LeftParen or TokenType.LeftBracket ? null : tokens[index: j];
    }

    /// <summary>Skips one balanced <paramref name="open"/>…<paramref name="close"/> group starting at
    /// <paramref name="start"/>, or returns <paramref name="start"/> when none starts there.</summary>
    private static int SkipBalanced(List<Token> tokens, int start, TokenType open, TokenType close)
    {
        if (start >= tokens.Count || tokens[index: start].Type != open)
        {
            return start;
        }

        int depth = 0;
        for (int k = start; k < tokens.Count; k++)
        {
            if (tokens[index: k].Type == open)
            {
                depth++;
            }
            else if (tokens[index: k].Type == close && --depth == 0)
            {
                return k + 1;
            }
        }

        return tokens.Count;
    }

    /// <summary>
    /// Rewrites <c>unless c</c> at <paramref name="index"/> as <c>if not (c)</c>: the condition runs to the end of the
    /// line.
    /// </summary>
    private static IEnumerable<MigrationEdit> UnlessEdits(List<Token> tokens, int index)
    {
        Token word = tokens[index: index];
        int last = index;
        while (last + 1 < tokens.Count && tokens[index: last + 1].Line == word.Line &&
               tokens[index: last + 1].Type is not (TokenType.Newline or TokenType.Indent or TokenType.Dedent))
        {
            last++;
        }

        if (last == index)
        {
            yield break;
        }

        Token end = tokens[index: last];
        yield return new MigrationEdit(Start: word.Position, Length: word.Text.Length + 1, Replacement: "if not (",
            Line: word.Line, What: "unless c → if not (c)");
        yield return new MigrationEdit(Start: end.Position + end.Text.Length, Length: 0, Replacement: ")",
            Line: word.Line, What: "unless c → if not (c)");
    }

    /// <summary>Whether <paramref name="token"/> can begin the expression a recovery keyword or <c>discard</c> takes.</summary>
    private static bool StartsMigratedExpression(Token token)
    {
        string kind = token.Type.ToString();
        return token.Type is TokenType.Identifier or TokenType.Me or TokenType.MyType or TokenType.LeftParen or
                   TokenType.LeftBracket or TokenType.Minus or TokenType.Not or TokenType.True or TokenType.False or
                   TokenType.InsertionStart or TokenType.Try or TokenType.Check or TokenType.Lookup or TokenType.Steal ||
               kind.EndsWith(value: "Literal", comparisonType: StringComparison.Ordinal) ||
               kind.StartsWith(value: "Undecided", comparisonType: StringComparison.Ordinal);
    }

    /// <summary>Whether the token at <paramref name="index"/> sits on a line that declares a routine.</summary>
    private static bool OnDefinitionLine(List<Token> tokens, int index)
    {
        for (int j = index - 1; j >= 0 && tokens[index: j].Line == tokens[index: index].Line; j--)
        {
            if (tokens[index: j].Type == TokenType.Routine)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the token at <paramref name="index"/> is the first on its line (a statement starts there).</summary>
    private static bool StartsStatement(List<Token> tokens, int index)
    {
        for (int j = index - 1; j >= 0; j--)
        {
            Token before = tokens[index: j];
            if (before.Type is TokenType.Newline or TokenType.Indent or TokenType.Dedent)
            {
                return true;
            }

            if (before.Line != tokens[index: index].Line)
            {
                return true;
            }

            if (before.Type is not TokenType.DocComment)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The keywords of this release written where a name goes: after <c>var</c>, as a parameter or argument label
    /// (<c>drop: 3</c>), or as a member after a dot. Only the newest keywords are looked at.
    /// </summary>
    private static IEnumerable<Token> KeywordsUsedAsNames(List<Token> tokens)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[index: i];
            if (token.Type is not (TokenType.Check or TokenType.Drop or TokenType.ReservedWord))
            {
                continue;
            }

            Token? before = i > 0 ? tokens[index: i - 1] : null;
            Token? after = i + 1 < tokens.Count ? tokens[index: i + 1] : null;
            // A call written right against the name (`check(name: ...)`) is a routine called that, not the
            // keyword in front of a parenthesized expression.
            bool calledByName = after is { Type: TokenType.LeftParen } && after.Position == token.Position + token.Text.Length;
            bool named = before?.Type is TokenType.Var or TokenType.Dot or TokenType.Routine || calledByName ||
                         after?.Type is TokenType.Colon or TokenType.Assign or TokenType.Comma or TokenType.RightParen;
            if (named)
            {
                yield return token;
            }
        }
    }

    /// <summary>Applies <paramref name="edits"/> to <paramref name="source"/>, last first so the earlier spans stay put.</summary>
    private static string ApplyMigrationEdits(string source, List<MigrationEdit> edits)
    {
        var text = new System.Text.StringBuilder(value: source);
        foreach (MigrationEdit edit in edits.OrderByDescending(keySelector: e => e.Start))
        {
            text.Remove(startIndex: edit.Start, length: edit.Length);
            text.Insert(index: edit.Start, value: edit.Replacement);
        }

        return text.ToString();
    }
}
