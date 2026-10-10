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
        HashSet<string> concurrentRoutines = ConcurrentRoutineNames(files: MigrationSources(paths: paths));
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

            List<MigrationEdit> edits = MigrationEdits(tokens: tokens, file: file, language: language,
                concurrentRoutines: concurrentRoutines, source: source);
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

    /// <summary>
    /// The names of the routines declared <c>suspended routine</c> or <c>threaded routine</c> in any of
    /// <paramref name="files"/>: a call of one built an Agent, which <c>agent</c> before the call does now.
    /// </summary>
    private static HashSet<string> ConcurrentRoutineNames(IEnumerable<string> files)
    {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (string file in files)
        {
            List<Token> tokens;
            try
            {
                tokens = Lexers.Tokenize(source: File.ReadAllText(path: file), fileName: file,
                    language: SourceLanguage(path: file));
            }
            catch (Exception)
            {
                continue;
            }

            for (int i = 0; i + 2 < tokens.Count; i++)
            {
                if (tokens[index: i] is { Type: TokenType.Identifier, Text: "suspended" or "threaded" } &&
                    tokens[index: i + 1].Type == TokenType.Routine &&
                    tokens[index: i + 2].Type == TokenType.Identifier)
                {
                    names.Add(item: tokens[index: i + 2].Text);
                }
            }
        }

        return names;
    }

    /// <summary>The integer a literal (with the minus written before it) spells, starting at <paramref name="index"/>,
    /// and the index after it, or null when no integer literal starts there.</summary>
    private static (long Value, int Next)? IntegerLiteralAt(List<Token> tokens, int index)
    {
        bool negative = index < tokens.Count && tokens[index: index].Type == TokenType.Minus;
        int at = negative
            ? index + 1
            : index;
        if (at >= tokens.Count || !tokens[index: at].Type.ToString().EndsWith(value: "Literal") &&
            tokens[index: at].Type != TokenType.UndecidedInteger)
        {
            return null;
        }

        string digits = new(value: tokens[index: at].Text.TakeWhile(predicate: c => char.IsDigit(c: c) || c == '_' || c == '-')
                                                         .Where(predicate: c => c != '_')
                                                         .ToArray());
        if (!long.TryParse(s: digits, result: out long value) || tokens[index: at].Text.Contains(value: '.'))
        {
            return null;
        }

        return (negative ? -value : value, at + 1);
    }

    /// <summary>
    /// The rewrite of a range with literal endpoints that counted down (its end below its start): with no step it
    /// gains <c>by -1</c>, with a positive literal step that step is negated. Null for any other range.
    /// </summary>
    private static MigrationEdit? DescendingLiteralRangeEdit(List<Token> tokens, int index)
    {
        // The start: a literal right before the operator, with a minus written before it.
        int startAt = index - 1;
        if (startAt < 0)
        {
            return null;
        }

        if (startAt > 0 && tokens[index: startAt - 1].Type == TokenType.Minus &&
            (startAt < 2 || tokens[index: startAt - 2].Type is not (TokenType.Identifier or TokenType.RightParen
                or TokenType.RightBracket) && !tokens[index: startAt - 2].Type.ToString().EndsWith(value: "Literal")))
        {
            startAt--;
        }

        if (IntegerLiteralAt(tokens: tokens, index: startAt) is not { Next: var afterStart } start || afterStart != index ||
            startAt > 0 && tokens[index: startAt - 1].Type is TokenType.Dot or TokenType.Identifier or TokenType.RightParen
                or TokenType.Star or TokenType.Plus or TokenType.Slash or TokenType.Percent or TokenType.Power ||
            IntegerLiteralAt(tokens: tokens, index: index + 1) is not { } end || end.Value >= start.Value)
        {
            return null;
        }

        Token endToken = tokens[index: end.Next - 1];
        if (end.Next < tokens.Count && tokens[index: end.Next].Type is TokenType.Plus or TokenType.Minus or
                TokenType.Star or TokenType.Slash or TokenType.Percent or TokenType.Power or TokenType.Dot)
        {
            return null;
        }

        if (end.Next < tokens.Count && tokens[index: end.Next].Type == TokenType.By)
        {
            if (IntegerLiteralAt(tokens: tokens, index: end.Next + 1) is not { Value: > 0 } step ||
                tokens[index: end.Next + 1].Position < 0)
            {
                return null;
            }

            Token stepToken = tokens[index: end.Next + 1];
            return new MigrationEdit(Start: stepToken.Position, Length: 0, Replacement: "-", Line: stepToken.Line,
                What: "a range that counted down gets a negative step");
        }

        // `by -1` goes right after the end literal as written.
        int afterEnd = end.Next < tokens.Count && tokens[index: end.Next].Position > endToken.Position
            ? tokens[index: end.Next].Position
            : endToken.Position + endToken.Text.Length;
        string source = _migrationSource;
        int comment = source.IndexOf(value: " #", startIndex: endToken.Position, count: afterEnd - endToken.Position,
            comparisonType: StringComparison.Ordinal);
        if (comment >= 0)
        {
            afterEnd = comment;
        }

        while (afterEnd > endToken.Position + 1 && char.IsWhiteSpace(c: source[index: afterEnd - 1]))
        {
            afterEnd--;
        }

        return new MigrationEdit(Start: afterEnd, Length: 0, Replacement: " by -1", Line: endToken.Line,
            What: "a range that counted down gets `by -1`");
    }

    /// <summary>One operand-or-operator unit of an expression at one bracket depth: a single token, or a whole
    /// bracketed group (<c>First</c> its opening token, <c>Last</c> its closing one).</summary>
    private sealed record PrecedenceAtom(int First, int Last);

    /// <summary>Tokens that end an expression (or a part of a statement that is an expression of its own).</summary>
    private static readonly HashSet<TokenType> ExpressionEnds =
    [
        TokenType.Newline, TokenType.Indent, TokenType.Dedent, TokenType.Comma, TokenType.Colon, TokenType.FatArrow,
        TokenType.Arrow, TokenType.Then, TokenType.Else, TokenType.If, TokenType.Elseif, TokenType.While,
        TokenType.Return, TokenType.When, TokenType.Each, TokenType.In, TokenType.Throw, TokenType.Eof,
        TokenType.Assign, TokenType.PlusAssign, TokenType.MinusAssign, TokenType.StarAssign, TokenType.SlashAssign,
        TokenType.PercentAssign, TokenType.AmpersandAssign, TokenType.PipeAssign, TokenType.CaretAssign,
        TokenType.LeftShiftAssign, TokenType.RightShiftAssign, TokenType.LogicalRightShiftAssign,
        TokenType.DivideAssign, TokenType.PowerAssign, TokenType.NoneCoalesceAssign
    ];

    /// <summary>Operators looser than bitwise ones: a bitwise expression ends at them.</summary>
    private static readonly HashSet<TokenType> BitwiseEnds =
    [
        TokenType.And, TokenType.Or, TokenType.Equal, TokenType.NotEqual, TokenType.IdentityEqual,
        TokenType.IdentityNotEqual, TokenType.Less, TokenType.LessEqual, TokenType.Greater, TokenType.GreaterEqual,
        TokenType.ThreeWayComparison, TokenType.To, TokenType.Til, TokenType.By, TokenType.Have, TokenType.Lack,
        TokenType.Is, TokenType.IsNot, TokenType.Obeys, TokenType.NoneCoalesce
    ];

    /// <summary>The bitwise operators from the tightest-binding group to the loosest, as they bound before they shared
    /// one level.</summary>
    private static readonly TokenType[][] BitwiseTiers =
    [
        [TokenType.LeftShift, TokenType.RightShift, TokenType.LogicalRightShift],
        [TokenType.Ampersand], [TokenType.Caret], [TokenType.Pipe]
    ];

    /// <summary>
    /// Parentheses that keep the grouping an expression had, where operators now share a level and mixing them needs
    /// parentheses: <c>a or b and c</c> is <c>a or (b and c)</c> (<c>and</c> bound tighter), and
    /// <c>a | b &amp; c</c> is <c>a | (b &amp; c)</c> (<c>&amp;</c> bound tighter than <c>^</c>, which bound tighter than
    /// <c>|</c>, with shifts tightest).
    /// </summary>
    private static List<MigrationEdit> PrecedenceEdits(List<Token> tokens, string source)
    {
        var edits = new List<MigrationEdit>();
        _migrationSource = source;
        GroupSegments(tokens: tokens, from: 0, to: tokens.Count, edits: edits);
        return edits;
    }

    /// <summary>The source of the file <see cref="PrecedenceEdits"/> works on, where a closing parenthesis is placed.</summary>
    private static string _migrationSource = "";

    /// <summary>Splits <c>tokens[from..to)</c> into expressions at one depth and groups each, going into every
    /// bracketed group too.</summary>
    private static void GroupSegments(List<Token> tokens, int from, int to, List<MigrationEdit> edits)
    {
        var segment = new List<PrecedenceAtom>();
        int i = from;
        while (i < to)
        {
            Token t = tokens[index: i];
            if (t.Type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace)
            {
                TokenType close = t.Type switch
                {
                    TokenType.LeftParen => TokenType.RightParen,
                    TokenType.LeftBracket => TokenType.RightBracket,
                    _ => TokenType.RightBrace
                };
                int after = SkipBalanced(tokens: tokens, start: i, open: t.Type, close: close);
                GroupSegments(tokens: tokens, from: i + 1, to: after - 1, edits: edits);
                segment.Add(item: new PrecedenceAtom(First: i, Last: Math.Min(val1: after - 1, val2: tokens.Count - 1)));
                i = after;
                continue;
            }

            if (ExpressionEnds.Contains(item: t.Type))
            {
                GroupExpression(tokens: tokens, atoms: segment, edits: edits);
                segment = [];
            }
            else
            {
                segment.Add(item: new PrecedenceAtom(First: i, Last: i));
            }

            i++;
        }

        GroupExpression(tokens: tokens, atoms: segment, edits: edits);
    }

    private static void GroupExpression(List<Token> tokens, List<PrecedenceAtom> atoms, List<MigrationEdit> edits)
    {
        if (atoms.Count == 0)
        {
            return;
        }

        // `and` inside `or`.
        if (HasOperator(tokens: tokens, atoms: atoms, ops: [TokenType.And]) &&
            HasOperator(tokens: tokens, atoms: atoms, ops: [TokenType.Or]))
        {
            foreach (List<PrecedenceAtom> piece in SplitAt(tokens: tokens, atoms: atoms, ops: [TokenType.Or]))
            {
                if (HasOperator(tokens: tokens, atoms: piece, ops: [TokenType.And]))
                {
                    Wrap(tokens: tokens, atoms: piece, edits: edits, what: "and inside or → (… and …)");
                }
            }
        }

        // Bitwise operators of different tiers, within each stretch between looser operators.
        foreach (List<PrecedenceAtom> stretch in SplitAt(tokens: tokens, atoms: atoms, ops: BitwiseEnds))
        {
            GroupBitwise(tokens: tokens, atoms: stretch, tier: BitwiseTiers.Length - 1, edits: edits);
        }
    }

    private static void GroupBitwise(List<Token> tokens, List<PrecedenceAtom> atoms, int tier, List<MigrationEdit> edits)
    {
        if (tier < 0)
        {
            return;
        }

        // Shifts of different kinds bound left to right: `a >>> b << c` is `(a >>> b) << c`.
        if (tier == 0)
        {
            TokenType? previous = null;
            for (int k = 0; k < atoms.Count; k++)
            {
                if (atoms[index: k].First != atoms[index: k].Last ||
                    !BitwiseTiers[0].Contains(value: tokens[index: atoms[index: k].First].Type))
                {
                    continue;
                }

                TokenType shift = tokens[index: atoms[index: k].First].Type;
                if (previous != null && shift != previous)
                {
                    Wrap(tokens: tokens, atoms: atoms.Take(count: k).ToList(), edits: edits,
                        what: "shifts grouped as they bound");
                }

                previous = shift;
            }

            return;
        }

        List<List<PrecedenceAtom>> pieces = SplitAt(tokens: tokens, atoms: atoms, ops: BitwiseTiers[tier]);
        TokenType[] tighter = BitwiseTiers.Take(count: tier).SelectMany(selector: t => t).ToArray();
        foreach (List<PrecedenceAtom> piece in pieces)
        {
            if (pieces.Count > 1 && HasOperator(tokens: tokens, atoms: piece, ops: tighter))
            {
                Wrap(tokens: tokens, atoms: piece, edits: edits, what: "bitwise operators grouped as they bound");
            }

            GroupBitwise(tokens: tokens, atoms: piece, tier: tier - 1, edits: edits);
        }
    }

    private static bool HasOperator(List<Token> tokens, List<PrecedenceAtom> atoms, ICollection<TokenType> ops)
    {
        return atoms.Any(predicate: a => a.First == a.Last && ops.Contains(item: tokens[index: a.First].Type));
    }

    private static List<List<PrecedenceAtom>> SplitAt(List<Token> tokens, List<PrecedenceAtom> atoms,
        ICollection<TokenType> ops)
    {
        var pieces = new List<List<PrecedenceAtom>> { new() };
        foreach (PrecedenceAtom atom in atoms)
        {
            if (atom.First == atom.Last && ops.Contains(item: tokens[index: atom.First].Type))
            {
                pieces.Add(item: []);
            }
            else
            {
                pieces[^1].Add(item: atom);
            }
        }

        return pieces.Where(predicate: p => p.Count > 0).ToList();
    }

    private static void Wrap(List<Token> tokens, List<PrecedenceAtom> atoms, List<MigrationEdit> edits, string what)
    {
        Token first = tokens[index: atoms[0].First];
        Token last = tokens[index: atoms[^1].Last];
        if (first.Position < 0 || last.Position < 0)
        {
            return;
        }

        edits.Add(item: new MigrationEdit(Start: first.Position, Length: 0, Replacement: "(", Line: first.Line,
            What: what));
        // The parenthesis closes right after the last token as written (a token's text may not be its spelling,
        // as a text literal's quotes show): just before whatever follows it.
        int end = atoms[^1].Last + 1 < tokens.Count && tokens[index: atoms[^1].Last + 1].Position > last.Position
            ? tokens[index: atoms[^1].Last + 1].Position
            : _migrationSource.Length;
        // A comment after the last token on its line stays after the parenthesis.
        int comment = _migrationSource.IndexOf(value: " #", startIndex: last.Position, count: end - last.Position,
            comparisonType: StringComparison.Ordinal);
        if (comment >= 0)
        {
            end = comment;
        }

        while (end > last.Position + 1 && char.IsWhiteSpace(c: _migrationSource[index: end - 1]))
        {
            end--;
        }

        edits.Add(item: new MigrationEdit(Start: end, Length: 0, Replacement: ")", Line: last.Line, What: what));
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
    private static List<MigrationEdit> MigrationEdits(List<Token> tokens, string file, Language language,
        HashSet<string> concurrentRoutines, string source)
    {
        bool inStandardLibrary = file.Replace(oldChar: '\\', newChar: '/').Contains(value: "/Standard/");
        // A name this file declares decides for itself: a `suspended`/`threaded` routine is called through
        // `agent`, a plain one is not, whatever another file declares under the same name.
        var ownConcurrent = new HashSet<string>(comparer: StringComparer.Ordinal);
        var ownPlain = new HashSet<string>(comparer: StringComparer.Ordinal);
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[index: i].Type == TokenType.Routine && tokens[index: i + 1].Type == TokenType.Identifier)
            {
                bool concurrent = i > 0 && tokens[index: i - 1] is
                    { Type: TokenType.Identifier, Text: "suspended" or "threaded" };
                (concurrent ? ownConcurrent : ownPlain).Add(item: tokens[index: i + 1].Text);
            }
        }

        var edits = new List<MigrationEdit>();
        edits.AddRange(collection: PrecedenceEdits(tokens: tokens, source: source));
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

            // A range counted down when its end was below its start: `10 to 1` is `10 to 1 by -1` now (it would be
            // empty), and `10 to 0 by 2` is `10 to 0 by -2`. Only literal endpoints say which way it went.
            if (token.Type is TokenType.To or TokenType.Til && DescendingLiteralRangeEdit(tokens: tokens, index: i) is
                    { } descending)
            {
                edits.Add(item: descending);
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

            // `suspended routine f()` / `threaded routine f()` is a plain routine: where it runs is the caller's choice.
            if (token.Text is "suspended" or "threaded" && next.Type == TokenType.Routine && next.Position >= 0)
            {
                edits.Add(item: new MigrationEdit(Start: token.Position, Length: next.Position - token.Position,
                    Replacement: "", Line: token.Line, What: $"{token.Text} routine → routine"));
                continue;
            }

            // A call of a routine that was `suspended`/`threaded` built an Agent: `agent` before the call does now.
            bool callsConcurrent = ownConcurrent.Contains(item: token.Text) ||
                                   !ownPlain.Contains(item: token.Text) && concurrentRoutines.Contains(item: token.Text);
            if (callsConcurrent && next.Type == TokenType.LeftParen &&
                (i == 0 || tokens[index: i - 1].Type is not (TokenType.Dot or TokenType.Routine or TokenType.Agent)))
            {
                // `agent` takes the whole call chain after it, so a chain that goes on (`f(x).retrieve()`) keeps only
                // the call inside: `(agent f(x)).retrieve()`.
                int afterCall = SkipBalanced(tokens: tokens, start: i + 1, open: TokenType.LeftParen,
                    close: TokenType.RightParen);
                bool chainGoesOn = afterCall < tokens.Count && tokens[index: afterCall].Type == TokenType.Dot &&
                                   tokens[index: afterCall - 1].Position >= 0;
                edits.Add(item: new MigrationEdit(Start: token.Position, Length: 0,
                    Replacement: chainGoesOn ? "(agent " : "agent ", Line: token.Line,
                    What: $"{token.Text}(…) → agent {token.Text}(…)"));
                if (chainGoesOn)
                {
                    edits.Add(item: new MigrationEdit(Start: tokens[index: afterCall - 1].Position + 1, Length: 0,
                        Replacement: ")", Line: token.Line, What: "(agent …) before the rest of the chain"));
                }

                continue;
            }

            // `job.execute()` (start and never collect) is `job.start_now()`: collect it later with `retrieve`/`gather`.
            if (token.Text == "execute" && i > 0 && tokens[index: i - 1].Type == TokenType.Dot &&
                next.Type == TokenType.LeftParen && i + 2 < tokens.Count && tokens[index: i + 2].Type == TokenType.RightParen)
            {
                edits.Add(item: new MigrationEdit(Start: token.Position, Length: token.Text.Length, Replacement: "start_now",
                    Line: token.Line, What: ".execute() → .start_now() (collect it with retrieve or gather)"));
                continue;
            }

            // `absent` (the old empty-handed failure) is `throw AbsentValueError()`.
            if (token.Text == "absent" && StartsStatement(tokens: tokens, index: i) &&
                next.Type is TokenType.Newline or TokenType.Dedent)
            {
                edits.Add(item: new MigrationEdit(Start: token.Position, Length: token.Text.Length,
                    Replacement: "throw AbsentValueError()", Line: token.Line, What: "absent → throw AbsentValueError()"));
                continue;
            }

            // `d.add(key: k, value: v)` on a statement of its own (after `drop` too) is `d[k] = v`.
            if (token.Text == "add" && i > 0 && tokens[index: i - 1].Type == TokenType.Dot &&
                next.Type == TokenType.LeftParen && i + 3 < tokens.Count && tokens[index: i + 2].Text == "key" &&
                tokens[index: i + 3].Type == TokenType.Colon)
            {
                edits.AddRange(collection: DictAddEdits(tokens: tokens, index: i));
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
            // `lookup f()` is `check f()`: its missing value is the AbsentValueError arm now (an `is None` arm on it
            // needs a hand rewrite to `is AbsentValueError`).
            if (token.Text == "lookup" && startsExpression)
            {
                edits.Add(item: new MigrationEdit(Start: token.Position, Length: token.Text.Length, Replacement: "check",
                    Line: token.Line, What: "lookup → check (rewrite an is None arm by hand)"));
                continue;
            }

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

    /// <summary>
    /// Rewrites <c>d.add(key: k, value: v)</c> at <paramref name="index"/> (the <c>add</c> token) as <c>d[k] = v</c>
    /// when the call is a whole statement (a leading <c>drop</c> goes too). A call whose result is used is left for
    /// a hand rewrite, since <c>d[k] = v</c> has no result.
    /// </summary>
    private static IEnumerable<MigrationEdit> DictAddEdits(List<Token> tokens, int index)
    {
        Token dot = tokens[index: index - 1];
        int open = index + 1;
        int close = SkipBalanced(tokens: tokens, start: open, open: TokenType.LeftParen, close: TokenType.RightParen) - 1;
        bool lineEnds = close + 1 >= tokens.Count || tokens[index: close + 1].Type is TokenType.Newline or TokenType.Dedent;
        int first = index - 1;
        while (first > 0 && tokens[index: first - 1].Line == dot.Line &&
               tokens[index: first - 1].Type is not (TokenType.Newline or TokenType.Indent or TokenType.Dedent))
        {
            first--;
        }

        bool dropped = tokens[index: first].Type == TokenType.Drop;
        int valueAt = -1;
        int depth = 0;
        for (int k = open; k <= close; k++)
        {
            TokenType t = tokens[index: k].Type;
            if (t is TokenType.LeftParen or TokenType.LeftBracket)
            {
                depth++;
            }
            else if (t is TokenType.RightParen or TokenType.RightBracket)
            {
                depth--;
            }
            else if (t == TokenType.Comma && depth == 1 && k + 2 <= close && tokens[index: k + 1].Text == "value" &&
                     tokens[index: k + 2].Type == TokenType.Colon)
            {
                valueAt = k;
            }
        }

        // Only a whole statement: the receiver starts the line (or follows `drop`) and the call ends it.
        int receiverStart = dropped ? first + 1 : first;
        bool receiverStartsStatement = true;
        for (int k = receiverStart; k < index - 1; k++)
        {
            if (tokens[index: k].Type is not (TokenType.Identifier or TokenType.Me or TokenType.Dot or
                TokenType.LeftBracket or TokenType.RightBracket or TokenType.LeftParen or TokenType.RightParen or
                TokenType.Comma or TokenType.Colon) && !tokens[index: k].Type.ToString().EndsWith(value: "Literal") &&
                !tokens[index: k].Type.ToString().StartsWith(value: "Undecided"))
            {
                receiverStartsStatement = false;
                break;
            }
        }
        if (!lineEnds || valueAt < 0 || !receiverStartsStatement)
        {
            yield break;
        }

        Token closing = tokens[index: close];
        if (dropped)
        {
            Token drop = tokens[index: first];
            yield return new MigrationEdit(Start: drop.Position, Length: tokens[index: first + 1].Position - drop.Position,
                Replacement: "", Line: drop.Line, What: "drop d.add(...) → d[k] = v");
        }

        // `.add(key: ` → `[`, `, value: ` → `] = `, `)` → nothing.
        yield return new MigrationEdit(Start: dot.Position, Length: tokens[index: index + 4].Position - dot.Position,
            Replacement: "[", Line: dot.Line, What: "d.add(key: k, value: v) → d[k] = v");
        int valueStart = tokens[index: valueAt + 3].Position;
        yield return new MigrationEdit(Start: tokens[index: valueAt].Position, Length: valueStart - tokens[index: valueAt].Position,
            Replacement: "] = ", Line: tokens[index: valueAt].Line, What: "d.add(key: k, value: v) → d[k] = v");
        yield return new MigrationEdit(Start: closing.Position, Length: 1, Replacement: "", Line: closing.Line,
            What: "d.add(key: k, value: v) → d[k] = v");
    }

    /// <summary>Whether <paramref name="token"/> can begin the expression a recovery keyword or <c>discard</c> takes.</summary>
    private static bool StartsMigratedExpression(Token token)
    {
        string kind = token.Type.ToString();
        return token.Type is TokenType.Identifier or TokenType.Me or TokenType.MyType or TokenType.LeftParen or
                   TokenType.LeftBracket or TokenType.Minus or TokenType.Not or TokenType.True or TokenType.False or
                   TokenType.InsertionStart or TokenType.Try or TokenType.Check or TokenType.Steal ||
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
