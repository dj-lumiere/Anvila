using Builder.Formatting;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Types;

namespace Builder.Execution;

/// <summary>
/// The refactors of the language servers that cut indentation, offered as <c>refactor.rewrite</c> code actions on an
/// <c>if</c>: invert it, turn it into an early exit, merge it with the <c>if</c> it holds. The structure comes from a
/// fresh parse of the text (the analyzed tree has its loops lowered) and the layout from the source lines: a moved
/// body keeps every comment and blank line because its lines are only re-indented, and only the condition is printed
/// by the formatter's expression printer. The analysis of the document, when it is current, tells which comparisons
/// are of integers and may be turned around. An action is offered only where it is correct, so a shape the code
/// does not understand gets none.
/// </summary>
internal static class LspRefactors
{
    private const int IndentWidth = 4;

    /// <summary>One refactor: the lines <see cref="StartLine"/> to <see cref="EndLine"/> (0-based, whole lines) become
    /// <see cref="NewText"/>.</summary>
    internal sealed record Refactor(string Title, int StartLine, int EndLine, string NewText);

    // Types whose comparisons have the usual order, so `<` turns into `>=`. A binary float or an IEEE decimal float
    // has `nan`, where `a < b` and `a >= b` are both false, so those are wrapped in `not (...)` instead. `Decimal`
    // is finite only.
    private static readonly HashSet<string> OrderedTypes = new(comparer: StringComparer.Ordinal)
    {
        "S8", "S16", "S32", "S64", "S128", "S256", "U8", "U16", "U32", "U64", "U128", "U256", "Int", "Integer",
        "Byte", "Char", "Decimal"
    };

    /// <summary>Where the statements of a block lead when they run out: the end of a routine (with or without a
    /// result) or the end of a loop round (with or without carried values).</summary>
    private enum ScopeKind
    {
        Routine,
        Loop
    }

    private sealed record Scope(ScopeKind Kind, bool Flag);

    /// <summary>
    /// The refactors for the <c>if</c> statements whose header lies on the 0-based lines <paramref name="firstLine"/>
    /// to <paramref name="lastLine"/>. <paramref name="analyzed"/> is the analyzed tree of this very text, or null.
    /// </summary>
    internal static List<Refactor> Compute(string text, string fileName, Language language, int firstLine, int lastLine,
        SyntaxTree.Program? analyzed)
    {
        var result = new List<Refactor>();
        string source = SourceFormatter.Normalize(source: text);
        SourceFormatter.ParsedFile parsed;
        try
        {
            parsed = SourceFormatter.ParseFile(source: source, fileName: fileName, language: language, what: "the file");
        }
        catch (FormatRefusedException)
        {
            return result;
        }

        var scopes = new Dictionary<Statement, Scope>(comparer: ReferenceEqualityComparer.Instance);
        var typeTable = new TypeTable(program: analyzed);
        var work = new Work(Index: parsed.Index, Printer: new AstPrinter(index: parsed.Index), Types: typeTable,
            First: firstLine + 1, Last: lastLine + 1);
        AstWalker.Walk(root: parsed.Program,
            visit: node =>
            {
                switch (node)
                {
                    case RoutineDeclaration { IsScriptEntry: false, Body: { } body } routine:
                        scopes[key: body] = new Scope(Kind: ScopeKind.Routine,
                            Flag: routine.ReturnType != null && routine.ReturnType.Name != "None");
                        break;
                    case WhileStatement loop:
                        scopes[key: loop.Body] = new Scope(Kind: ScopeKind.Loop, Flag: loop.With != null);
                        break;
                    case LoopStatement loop:
                        scopes[key: loop.Body] = new Scope(Kind: ScopeKind.Loop, Flag: loop.With != null);
                        break;
                    case ForStatement each:
                        scopes[key: each.Body] = new Scope(Kind: ScopeKind.Loop, Flag: each.With != null);
                        break;
                    case BlockStatement block:
                        scopes.TryGetValue(key: block, value: out Scope? scope);
                        for (int i = 0; i < block.Statements.Count; i++)
                        {
                            if (block.Statements[index: i] is IfStatement)
                            {
                                Consider(work: work, block: block, at: i, scope: scope, result: result);
                            }
                        }

                        break;
                }
            });
        string newline = text.Contains(value: "\r\n")
            ? "\r\n"
            : "\n";
        return newline == "\n"
            ? result
            : result.Select(selector: r => r with { NewText = r.NewText.Replace(oldValue: "\n", newValue: newline) })
                    .ToList();
    }

    private sealed record Work(SourceIndex Index, AstPrinter Printer, TypeTable Types, int First, int Last)
    {
        public string[] Lines => Index.Lines;
    }

    /// <summary>The types the analysis gave the expressions of a document, found again by where an expression starts
    /// and what kind it is (the fresh parse the refactors work on has none).</summary>
    private sealed class TypeTable
    {
        private readonly Dictionary<(int Line, int Column, Type Kind), List<TypeSymbol?>> _types = [];

        public TypeTable(SyntaxTree.Program? program)
        {
            if (program == null)
            {
                return;
            }

            AstWalker.WalkExpressions(root: program,
                visit: e =>
                {
                    var key = (e.Location.Line, e.Location.Column, e.GetType());
                    if (!_types.TryGetValue(key: key, value: out List<TypeSymbol?>? list))
                    {
                        _types[key: key] = list = [];
                    }

                    list.Add(item: e.ResolvedType);
                });
        }

        /// <summary>The type of <paramref name="expression"/>, or null when it is not known or the analysis is not
        /// sure which expression is meant.</summary>
        public TypeSymbol? Of(Expression expression)
        {
            var key = (expression.Location.Line, expression.Location.Column, expression.GetType());
            if (!_types.TryGetValue(key: key, value: out List<TypeSymbol?>? list) || list.Count == 0 ||
                list[index: 0] is not { } first)
            {
                return null;
            }

            return list.All(predicate: t => t != null && t.Name == first.Name)
                ? first
                : null;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // The statement
    // ------------------------------------------------------------------------------------------------------------

    private static void Consider(Work work, BlockStatement block, int at, Scope? scope, List<Refactor> result)
    {
        var statement = (IfStatement)block.Statements[index: at];
        int header = statement.Location.Line;
        if (header < work.First || header > work.Last || !StartsItsLine(work: work, statement: statement) ||
            statement.ThenStatement is not BlockStatement thenBlock)
        {
            return;
        }

        int headerEnd = thenBlock.Location.Line;
        int indent = IndentOf(line: work.Lines[header - 1]);
        if (headerEnd < header ||
            !HeaderIsPlain(work: work, statement: statement, thenBlock: thenBlock, headerEnd: headerEnd))
        {
            return;
        }

        int thenEnd = BodyEnd(work: work, from: headerEnd + 1, indent: indent);
        if (thenEnd < headerEnd + 1 || !IsPlainBody(work: work, from: headerEnd + 1, to: thenEnd) ||
            thenBlock.Statements is [NothingStatement])
        {
            return;
        }

        string trailing = TrailingComment(work: work, line: headerEnd);
        if (TryInvert(work: work, statement: statement, indent: indent, header: header, headerEnd: headerEnd,
                thenEnd: thenEnd, trailing: trailing) is { } inverted)
        {
            result.Add(item: inverted);
        }

        if (TryGuard(work: work, block: block, at: at, scope: scope, thenBlock: thenBlock, indent: indent,
                header: header, headerEnd: headerEnd, thenEnd: thenEnd, trailing: trailing) is { } guard)
        {
            result.Add(item: guard);
        }

        if (TryMerge(work: work, statement: statement, thenBlock: thenBlock, indent: indent, header: header,
                headerEnd: headerEnd, thenEnd: thenEnd, trailing: trailing) is { } merged)
        {
            result.Add(item: merged);
        }
    }

    /// <summary>Whether the <c>if</c> is the first thing on its line (the shape of every <c>if</c> statement).</summary>
    private static bool StartsItsLine(Work work, IfStatement statement)
    {
        int position = statement.Location.Position;
        if (position < 0 || position >= work.Index.Source.Length ||
            work.Index.Tokens[index: work.Index.TokenIndexAt(position: position)].Type != TokenType.If)
        {
            return false;
        }

        int lineStart = position == 0
            ? 0
            : work.Index.Source.LastIndexOf(value: '\n', startIndex: position - 1) + 1;
        return work.Index.Source.AsSpan(start: lineStart, length: position - lineStart)
                   .Trim(trimChar: ' ')
                   .Length == 0;
    }

    /// <summary>
    /// Whether the header can be written again: it holds no name a pattern binds (that name would not reach where
    /// the body goes), and no comment but one at the end of its last line.
    /// </summary>
    private static bool HeaderIsPlain(Work work, IfStatement statement, BlockStatement thenBlock, int headerEnd)
    {
        foreach (Token token in work.Index.Tokens)
        {
            if (token.Position >= statement.Location.Position && token.Position < thenBlock.Location.Position &&
                token.Type == TokenType.As)
            {
                return false;
            }
        }

        return work.Index.Comments.All(predicate: c => c.Line < statement.Location.Line || c.Line > headerEnd ||
                                                       c.Line == headerEnd);
    }

    /// <summary>The comment at the end of the line (with the two spaces before it), or an empty text.</summary>
    private static string TrailingComment(Work work, int line)
    {
        CommentTrivia? comment = work.Index.Comments.FirstOrDefault(predicate: c => c.Line == line);
        return comment == null
            ? ""
            : "  " + comment.Text.TrimEnd();
    }

    /// <summary>The number of spaces a line starts with.</summary>
    private static int IndentOf(string line)
    {
        int n = 0;
        while (n < line.Length && line[index: n] == ' ')
        {
            n++;
        }

        return n;
    }

    private static bool IsBlank(string line)
    {
        return line.Trim().Length == 0;
    }

    /// <summary>
    /// The last line (1-based) of the block that starts on line <paramref name="from"/> below a header with
    /// <paramref name="indent"/> spaces: the last line with text before the first line that is not indented deeper.
    /// A comment line at the header's level ends the block, so it stays where it is. One less than
    /// <paramref name="from"/> when the block has no line.
    /// </summary>
    private static int BodyEnd(Work work, int from, int indent)
    {
        int last = from - 1;
        for (int k = from; k <= work.Lines.Length; k++)
        {
            string line = work.Lines[k - 1];
            if (IsBlank(line: line))
            {
                continue;
            }

            if (IndentOf(line: line) <= indent)
            {
                break;
            }

            last = k;
        }

        return last;
    }

    /// <summary>Whether the lines hold no token that spans lines (re-indenting would change it).</summary>
    private static bool IsPlainBody(Work work, int from, int to)
    {
        foreach (Token token in work.Index.Tokens)
        {
            if (token.Line >= from && token.Line <= to && token.Text.Contains(value: '\n'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Spaces(int n)
    {
        return new string(c: ' ', count: n);
    }

    /// <summary>The lines (1-based, both included) moved by <paramref name="shift"/> spaces: negative takes spaces off
    /// the front, positive puts them on. Blank lines stay empty.</summary>
    private static IEnumerable<string> Shifted(Work work, int from, int to, int shift)
    {
        for (int k = from; k <= to; k++)
        {
            string line = work.Lines[k - 1];
            if (IsBlank(line: line))
            {
                yield return "";
            }
            else if (shift >= 0)
            {
                yield return Spaces(n: shift) + line;
            }
            else
            {
                yield return line[-shift..];
            }
        }
    }

    private static Refactor Make(string title, Work work, int fromLine, int toLine, IEnumerable<string> lines)
    {
        _ = work;
        return new Refactor(Title: title, StartLine: fromLine - 1, EndLine: toLine - 1,
            NewText: string.Join(separator: "\n", values: lines));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Invert
    // ------------------------------------------------------------------------------------------------------------

    /// <summary><c>if c A else B</c> becomes <c>if not c B else A</c>.</summary>
    private static Refactor? TryInvert(Work work, IfStatement statement, int indent, int header, int headerEnd,
        int thenEnd, string trailing)
    {
        if (statement.ElseStatement is not BlockStatement elseBlock)
        {
            return null;
        }

        int elseLine = elseBlock.Location.Line;
        if (elseLine <= thenEnd || elseLine > work.Lines.Length || work.Lines[elseLine - 1].Trim() != "else" ||
            IndentOf(line: work.Lines[elseLine - 1]) != indent)
        {
            return null;
        }

        for (int k = thenEnd + 1; k < elseLine; k++)
        {
            if (!IsBlank(line: work.Lines[k - 1]))
            {
                return null;
            }
        }

        int elseEnd = BodyEnd(work: work, from: elseLine + 1, indent: indent);
        if (elseEnd < elseLine + 1 || !IsPlainBody(work: work, from: elseLine + 1, to: elseEnd))
        {
            return null;
        }

        string? condition = NegatedText(work: work, condition: statement.Condition);
        if (condition == null)
        {
            return null;
        }

        var lines = new List<string> { Spaces(n: indent) + "if " + condition + trailing };
        lines.AddRange(collection: Shifted(work: work, from: elseLine + 1, to: elseEnd, shift: 0));
        lines.Add(item: Spaces(n: indent) + "else");
        lines.AddRange(collection: Shifted(work: work, from: headerEnd + 1, to: thenEnd, shift: 0));
        return Make(title: "Invert 'if'", work: work, fromLine: header, toLine: elseEnd, lines: lines);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Early exit
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>if c { body }</c> followed by nothing but the end of the routine (or its final <c>return</c>) or the end of
    /// the loop round becomes <c>if not c { return }</c> (<c>continue</c> in a loop) and the body one level out. With a
    /// result, <c>if c { ...; return a }</c> then <c>return b</c> becomes <c>if not c { return b }</c> then the body.
    /// </summary>
    private static Refactor? TryGuard(Work work, BlockStatement block, int at, Scope? scope, BlockStatement thenBlock,
        int indent, int header, int headerEnd, int thenEnd, string trailing)
    {
        var statement = (IfStatement)block.Statements[index: at];
        if (scope == null || statement.ElseStatement != null)
        {
            return null;
        }

        List<Statement> tail = block.Statements.Skip(count: at + 1)
                                     .ToList();
        bool bodyEndsInReturn = thenBlock.Statements is [.., ReturnStatement];
        string guard;
        // The line after which the replaced text ends, and whether the tail statement moves into the guard.
        int regionEnd = thenEnd;
        int tailStart = 0;
        int tailEnd = 0;
        if (scope.Kind == ScopeKind.Loop)
        {
            if (tail.Count != 0 || scope.Flag)
            {
                return null;
            }

            guard = "continue";
        }
        else
        {
            switch (tail)
            {
                case []:
                    if (scope.Flag)
                    {
                        return null;
                    }

                    guard = "return";
                    break;
                case [ReturnStatement { Value: null } bare]:
                    if (scope.Flag)
                    {
                        return null;
                    }

                    guard = "return";
                    if (bodyEndsInReturn)
                    {
                        (tailStart, tailEnd) = TailLines(work: work, tail: bare, indent: indent);
                    }

                    break;
                case [ReturnStatement { Value: not null } valued]:
                    if (!bodyEndsInReturn || thenBlock.Statements is not [.., ReturnStatement { Value: not null }])
                    {
                        return null;
                    }

                    guard = "";
                    (tailStart, tailEnd) = TailLines(work: work, tail: valued, indent: indent);
                    break;
                default:
                    return null;
            }

            if (tailStart > 0)
            {
                if (tailStart <= thenEnd || tailEnd < tailStart ||
                    !IsPlainBody(work: work, from: tailStart, to: tailEnd))
                {
                    return null;
                }

                for (int k = thenEnd + 1; k < tailStart; k++)
                {
                    if (!IsBlank(line: work.Lines[k - 1]))
                    {
                        return null;
                    }
                }

                regionEnd = tailEnd;
            }
        }

        string? condition = NegatedText(work: work, condition: statement.Condition);
        if (condition == null)
        {
            return null;
        }

        var lines = new List<string> { Spaces(n: indent) + "if " + condition + trailing };
        if (guard.Length > 0)
        {
            lines.Add(item: Spaces(n: indent + IndentWidth) + guard);
        }
        else
        {
            lines.AddRange(collection: Shifted(work: work, from: tailStart, to: tailEnd, shift: IndentWidth));
        }

        lines.AddRange(collection: Shifted(work: work, from: headerEnd + 1, to: thenEnd, shift: -IndentWidth));
        return Make(title: scope.Kind == ScopeKind.Loop
                ? "Exit early with 'continue'"
                : "Exit early with 'return'",
            work: work, fromLine: header, toLine: regionEnd, lines: lines);
    }

    /// <summary>The lines (1-based) of the last statement of a block, a <c>return</c>: from its own line to the last
    /// line indented deeper than it.</summary>
    private static (int Start, int End) TailLines(Work work, ReturnStatement tail, int indent)
    {
        int start = tail.Location.Line;
        if (start < 1 || start > work.Lines.Length || IndentOf(line: work.Lines[start - 1]) != indent ||
            !work.Lines[start - 1].AsSpan()
                 .TrimStart(trimChar: ' ')
                 .StartsWith(value: "return", comparisonType: StringComparison.Ordinal))
        {
            return (0, 0);
        }

        return (start, BodyEnd(work: work, from: start + 1, indent: indent));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Merge
    // ------------------------------------------------------------------------------------------------------------

    /// <summary><c>if a</c> holding only <c>if b</c> (no <c>else</c> on either) becomes <c>if a and b</c>.</summary>
    private static Refactor? TryMerge(Work work, IfStatement statement, BlockStatement thenBlock, int indent,
        int header, int headerEnd, int thenEnd, string trailing)
    {
        if (statement.ElseStatement != null || thenBlock.Statements is not [IfStatement inner] ||
            inner.ElseStatement != null || inner.ThenStatement is not BlockStatement innerBlock ||
            inner.Location.Line != headerEnd + 1 || !StartsItsLine(work: work, statement: inner) ||
            IndentOf(line: work.Lines[inner.Location.Line - 1]) != indent + IndentWidth)
        {
            return null;
        }

        int innerHeaderEnd = innerBlock.Location.Line;
        if (innerHeaderEnd < inner.Location.Line ||
            !HeaderIsPlain(work: work, statement: inner, thenBlock: innerBlock, headerEnd: innerHeaderEnd))
        {
            return null;
        }

        int innerEnd = BodyEnd(work: work, from: innerHeaderEnd + 1, indent: indent + IndentWidth);
        // Nothing may follow the inner `if` in the outer body, not even a comment.
        if (innerEnd < innerHeaderEnd + 1 || innerEnd != thenEnd ||
            !IsPlainBody(work: work, from: headerEnd + 1, to: innerEnd))
        {
            return null;
        }

        string innerTrailing = TrailingComment(work: work, line: innerHeaderEnd);
        if (trailing.Length > 0 && innerTrailing.Length > 0)
        {
            return null;
        }

        var merged = new BinaryExpression(Left: statement.Condition, Operator: BinaryOperator.And,
            Right: inner.Condition, Location: statement.Condition.Location);
        string condition;
        try
        {
            condition = work.Printer.PrintCondition(condition: merged);
        }
        catch (FormatRefusedException)
        {
            return null;
        }

        var lines = new List<string> { Spaces(n: indent) + "if " + condition + trailing + innerTrailing };
        lines.AddRange(collection: Shifted(work: work, from: innerHeaderEnd + 1, to: innerEnd, shift: -IndentWidth));
        return Make(title: "Merge nested 'if'", work: work, fromLine: header, toLine: innerEnd, lines: lines);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Negation
    // ------------------------------------------------------------------------------------------------------------

    private static string? NegatedText(Work work, Expression condition)
    {
        try
        {
            return work.Printer.PrintCondition(condition: Negate(work: work, e: condition));
        }
        catch (FormatRefusedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The condition that holds exactly when <paramref name="e"/> does not. <c>==</c> and <c>!=</c>, <c>is</c> and
    /// <c>isnot</c>, <c>has</c> and <c>lacks</c> (and the key forms) swap, <c>not x</c> drops its <c>not</c>,
    /// <c>and</c> and <c>or</c> go through De Morgan, and an order comparison turns around only between integers
    /// or Decimal. Anything else, a float comparison, a chained one and what the analysis does not type, is wrapped
    /// in <c>not (...)</c>: with <c>nan</c> both <c>a &lt; b</c> and <c>a &gt;= b</c> are false.
    /// </summary>
    private static Expression Negate(Work work, Expression e)
    {
        switch (e)
        {
            case UnaryExpression { Operator: UnaryOperator.Not } not:
                return not.Operand;
            case LiteralExpression { LiteralType: TokenType.True } literal:
                return literal with { Value = false, LiteralType = TokenType.False };
            case LiteralExpression { LiteralType: TokenType.False } literal:
                return literal with { Value = true, LiteralType = TokenType.True };
            case IsPatternExpression pattern:
                return pattern with { IsNegated = !pattern.IsNegated };
            case BinaryExpression binary:
                switch (binary.Operator)
                {
                    case BinaryOperator.Equal:
                        return binary with { Operator = BinaryOperator.NotEqual };
                    case BinaryOperator.NotEqual:
                        return binary with { Operator = BinaryOperator.Equal };
                    case BinaryOperator.IdentityEqual:
                        return binary with { Operator = BinaryOperator.IdentityNotEqual };
                    case BinaryOperator.IdentityNotEqual:
                        return binary with { Operator = BinaryOperator.IdentityEqual };
                    case BinaryOperator.And:
                        return new BinaryExpression(Left: Negate(work: work, e: binary.Left),
                            Operator: BinaryOperator.Or, Right: Negate(work: work, e: binary.Right),
                            Location: binary.Location);
                    case BinaryOperator.Or:
                        return new BinaryExpression(Left: Negate(work: work, e: binary.Left),
                            Operator: BinaryOperator.And, Right: Negate(work: work, e: binary.Right),
                            Location: binary.Location);
                    case BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater
                        or BinaryOperator.GreaterEqual when ComparesOrdered(work: work, binary: binary):
                        return binary with
                        {
                            Operator = binary.Operator switch
                            {
                                BinaryOperator.Less => BinaryOperator.GreaterEqual,
                                BinaryOperator.LessEqual => BinaryOperator.Greater,
                                BinaryOperator.Greater => BinaryOperator.LessEqual,
                                _ => BinaryOperator.Less
                            }
                        };
                }

                break;
            case CallExpression { Callee: MemberExpression member, IsFailable: false, TypeArguments: null } call
                when OppositeName(name: member.MemberName) is { } opposite && CanSwapContainment(work: work, member: member):
                return call with { Callee = member with { MemberName = opposite } };
        }

        return new UnaryExpression(Operator: UnaryOperator.Not, Operand: e, Location: e.Location);
    }

    private static string? OppositeName(string name)
    {
        return name switch
        {
            "has" => "lacks",
            "lacks" => "has",
            "has_key" => "lacks_key",
            "lacks_key" => "has_key",
            _ => null
        };
    }

    /// <summary>Whether <c>has</c> and <c>lacks</c> on this receiver are each other's opposite: not for flags, where
    /// <c>has</c> asks for all and <c>lacks</c> for none, and not when the analysis does not know the receiver.</summary>
    private static bool CanSwapContainment(Work work, MemberExpression member)
    {
        TypeSymbol? receiver = work.Types.Of(expression: member.Object);
        return receiver != null && receiver is not FlagsTypeSymbol;
    }

    /// <summary>Whether both sides of a comparison are integers or Decimal. A bare integer literal takes the type of
    /// the other side.</summary>
    private static bool ComparesOrdered(Work work, BinaryExpression binary)
    {
        bool left = IsOrdered(work: work, side: binary.Left, out bool leftKnown);
        bool right = IsOrdered(work: work, side: binary.Right, out bool rightKnown);
        return left && right && (leftKnown || rightKnown);
    }

    private static bool IsOrdered(Work work, Expression side, out bool known)
    {
        known = false;
        if (side is LiteralExpression { LiteralType: TokenType.UndecidedInteger })
        {
            return true;
        }

        if (side is UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression { LiteralType: TokenType.UndecidedInteger } })
        {
            return true;
        }

        TypeSymbol? type = work.Types.Of(expression: side);
        known = type != null && OrderedTypes.Contains(item: type.Name);
        return known;
    }
}
