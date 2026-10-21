using Builder.Tokenizer;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>
    /// Routines of the standard library whose names start with <c>try_</c> / <c>check_</c> / <c>lookup_</c> for
    /// their own reason: they are not the recovery forms of another routine, so a call of one is left alone.
    /// </summary>
    private static readonly HashSet<string> PrefixedStandardRoutines =
    [
        "try_acquire", "try_add_claimer", "try_add_inspector", "try_enter", "try_view", "try_modify", "try_consult",
        "try_amend", "check_radix"
    ];

    /// <summary>
    /// The default rewrites for changes whose old spelling still builds or reads as an unknown name, so that
    /// nothing changes meaning without a word:
    /// <list type="bullet">
    /// <item><c>-x ** n</c> was <c>(-x) ** n</c> and is <c>-(x ** n)</c> now: the parentheses go in, keeping what the
    /// line computed.</item>
    /// <item><c>C64</c> / <c>C128</c> were two <c>B64</c> / <c>B128</c> parts and are two <c>B32</c> / <c>B64</c> now:
    /// they become <c>C128</c> / <c>C256</c> in a file known to be the previous release's code
    /// (<paramref name="previousRelease"/>), and are listed in any other.</item>
    /// <item>A range counts down only with a negative step: one whose start is not a small literal is listed.</item>
    /// <item><c>routine T.create(...)</c> is the constructor <c>routine T(...)</c>, and a call <c>T.create(...)</c> of
    /// one this file declares is <c>T(...)</c>.</item>
    /// <item><c>try_f(x)</c> / <c>check_f(x)</c> / <c>lookup_f(x)</c> (and <c>r.try_f(x)</c>) are <c>try f(x)</c> /
    /// <c>check f(x)</c> (<c>try r.f(x)</c>).</item>
    /// </list>
    /// </summary>
    private static List<MigrationEdit> ReleaseFiveEdits(List<Token> tokens, string file, string source,
        bool previousRelease, ref int listed)
    {
        var edits = new List<MigrationEdit>();
        var declared = new HashSet<string>(comparer: StringComparer.Ordinal);
        var constructorOwners = new HashSet<string>(comparer: StringComparer.Ordinal);
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[index: i].Type != TokenType.Routine || tokens[index: i + 1].Type != TokenType.Identifier)
            {
                continue;
            }

            declared.Add(item: tokens[index: i + 1].Text);
            int dot = SkipBalanced(tokens: tokens, start: i + 2, open: TokenType.LeftBracket, close: TokenType.RightBracket);
            if (dot + 2 < tokens.Count && tokens[index: dot].Type == TokenType.Dot &&
                tokens[index: dot + 1].Type == TokenType.Identifier)
            {
                declared.Add(item: tokens[index: dot + 1].Text);
                if (tokens[index: dot + 1].Text == "create" &&
                    tokens[index: dot + 2].Type is TokenType.LeftParen or TokenType.LeftBracket &&
                    tokens[index: dot].Position >= 0)
                {
                    constructorOwners.Add(item: tokens[index: i + 1].Text);
                    edits.Add(item: new MigrationEdit(Start: tokens[index: dot].Position,
                        Length: tokens[index: dot + 1].Position + "create".Length - tokens[index: dot].Position,
                        Replacement: "", Line: tokens[index: dot].Line,
                        What: $"routine {tokens[index: i + 1].Text}.create(…) → routine {tokens[index: i + 1].Text}(…), the constructor"));
                }
            }
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[index: i];
            if (token.Position < 0)
            {
                continue;
            }

            Token? before = i > 0 ? tokens[index: i - 1] : null;
            Token? after = i + 1 < tokens.Count ? tokens[index: i + 1] : null;

            // `-x ** n` keeps the meaning it had: `(-x) ** n`.
            if (token.Type == TokenType.Minus && !EndsOperand(token: before) &&
                UnaryOperandEnd(tokens: tokens, first: i + 1) is { } operandEnd &&
                operandEnd + 1 < tokens.Count && tokens[index: operandEnd + 1].Type == TokenType.Power)
            {
                edits.Add(item: new MigrationEdit(Start: token.Position, Length: 0, Replacement: "(", Line: token.Line,
                    What: NegatedPowerNote));
                edits.Add(item: new MigrationEdit(Start: EndOffset(tokens: tokens, index: operandEnd, source: source),
                    Length: 0, Replacement: ")", Line: token.Line, What: NegatedPowerNote));
                continue;
            }

            if (token.Type is TokenType.To or TokenType.Til && ListDescendingRange(tokens: tokens, index: i))
            {
                Console.WriteLine(value: $"{file}:{token.Line}: a range counts up only now: if this one should count " +
                                         $"down (its start above its end), add 'by -1' after its end");
                listed++;
                continue;
            }

            if (token.Type != TokenType.Identifier || after == null)
            {
                continue;
            }

            if (token.Text is "C64" or "C128" && before?.Type != TokenType.Dot)
            {
                string wider = token.Text == "C64" ? "C128" : "C256";
                string parts = token.Text == "C64" ? "B32" : "B64";
                if (previousRelease)
                {
                    edits.Add(item: new MigrationEdit(Start: token.Position, Length: token.Text.Length, Replacement: wider,
                        Line: token.Line, What: $"{token.Text} → {wider} (a {token.Text} is two {parts} parts now)"));
                }
                else
                {
                    Console.WriteLine(value: $"{file}:{token.Line}: '{token.Text}' is two {parts} parts now: if this is " +
                                             $"code for the previous release, write '{wider}'");
                    listed++;
                }

                continue;
            }

            // A call of a constructor this file declared with `routine T.create(...)`: `T.create(x)` is `T(x)`.
            if (constructorOwners.Contains(item: token.Text) && after.Type == TokenType.Dot && i + 3 < tokens.Count &&
                tokens[index: i + 2].Text == "create" && tokens[index: i + 3].Type == TokenType.LeftParen &&
                before?.Type != TokenType.Routine)
            {
                edits.Add(item: new MigrationEdit(Start: after.Position,
                    Length: tokens[index: i + 2].Position + "create".Length - after.Position, Replacement: "",
                    Line: token.Line, What: $"{token.Text}.create(…) → {token.Text}(…)"));
                continue;
            }

            if (RecoveryPrefixEdits(tokens: tokens, index: i, file: file, declared: declared, listed: ref listed) is
                { } recovery)
            {
                edits.AddRange(collection: recovery);
            }
        }

        return edits;
    }

    /// <summary>What the report says about <c>-x ** n</c>.</summary>
    private const string NegatedPowerNote =
        "-x ** n → (-x) ** n, what it computed before ('**' binds tighter than a leading '-' now): check that this is " +
        "what you want";

    /// <summary>Whether <paramref name="token"/> ends an operand, so a <c>-</c> after it subtracts.</summary>
    private static bool EndsOperand(Token? token)
    {
        return token != null && (token.Type is TokenType.RightParen or TokenType.RightBracket ||
                                 token.Type != TokenType.Dot && IsChainToken(token: token));
    }

    /// <summary>The last token of the operand a leading <c>-</c> takes when it starts at <paramref name="first"/>: a
    /// parenthesized group, or a name or literal with its members, calls and indexes. Null for anything else.</summary>
    private static int? UnaryOperandEnd(List<Token> tokens, int first)
    {
        if (first >= tokens.Count)
        {
            return null;
        }

        if (tokens[index: first].Type == TokenType.LeftParen)
        {
            return SkipBalanced(tokens: tokens, start: first, open: TokenType.LeftParen, close: TokenType.RightParen) - 1;
        }

        if (tokens[index: first].Type == TokenType.Dot || !IsChainToken(token: tokens[index: first]))
        {
            return null;
        }

        int end = first;
        while (end + 1 < tokens.Count)
        {
            Token next = tokens[index: end + 1];
            if (next.Type == TokenType.Dot && end + 2 < tokens.Count && tokens[index: end + 2].Type == TokenType.Identifier)
            {
                end += 2;
            }
            else if (next.Type == TokenType.LeftParen && next.Line == tokens[index: end].Line)
            {
                end = SkipBalanced(tokens: tokens, start: end + 1, open: TokenType.LeftParen, close: TokenType.RightParen) - 1;
            }
            else if (next.Type == TokenType.LeftBracket && next.Line == tokens[index: end].Line)
            {
                end = SkipBalanced(tokens: tokens, start: end + 1, open: TokenType.LeftBracket, close: TokenType.RightBracket) - 1;
            }
            else
            {
                break;
            }
        }

        return end;
    }

    /// <summary>
    /// Whether the <c>to</c> / <c>til</c> at <paramref name="index"/> is a range that may have counted down before:
    /// it has no <c>by</c>, and its start is not a literal (a range from a literal to a literal is rewritten, and one
    /// from 0 or 1 counts up).
    /// </summary>
    private static bool ListDescendingRange(List<Token> tokens, int index)
    {
        if (index == 0 || index + 1 >= tokens.Count || tokens[index: index - 1].Type is TokenType.Dot or
                TokenType.LeftParen or TokenType.Comma || tokens[index: index + 1].Type is TokenType.Colon)
        {
            return false;
        }

        // A `by` after the end gives the direction already.
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
                if (--depth < 0)
                {
                    break;
                }
            }
            else if (depth == 0 && t.Type == TokenType.By)
            {
                return false;
            }
            else if (depth == 0 && (ExpressionEnds.Contains(item: t.Type) || BitwiseEnds.Contains(item: t.Type)))
            {
                break;
            }
        }

        // The start: the tokens back to where the expression begins.
        int first = index - 1;
        depth = 0;
        for (int j = index - 1; j >= 0; j--)
        {
            Token t = tokens[index: j];
            if (t.Type is TokenType.RightParen or TokenType.RightBracket)
            {
                depth++;
            }
            else if (t.Type is TokenType.LeftParen or TokenType.LeftBracket)
            {
                if (--depth < 0)
                {
                    break;
                }
            }
            else if (depth == 0 && (ExpressionEnds.Contains(item: t.Type) || BitwiseEnds.Contains(item: t.Type)))
            {
                break;
            }

            first = j;
        }

        if (IntegerLiteralAt(tokens: tokens, index: first) is not { } start || start.Next != index)
        {
            return true;
        }

        // A literal start: from 0 or 1 a range counts up, and to a literal end the literal rewrite decides.
        if (start.Value <= 1)
        {
            return false;
        }

        return IntegerLiteralAt(tokens: tokens, index: index + 1) is not { } end ||
               end.Next < tokens.Count && tokens[index: end.Next].Type is TokenType.Plus or TokenType.Minus or
                   TokenType.Star or TokenType.Slash or TokenType.Percent or TokenType.Power or TokenType.Dot or
                   TokenType.LeftParen or TokenType.LeftBracket;
    }

    /// <summary>
    /// The rewrite of a call of the generated recovery routines the previous release had: <c>try_f(x)</c> is
    /// <c>try f(x)</c>, <c>check_f(x)</c> and <c>lookup_f(x)</c> are <c>check f(x)</c>, and <c>r.try_f(x)</c> is
    /// <c>try r.f(x)</c>. A routine of that name declared in this file or in the standard library is left alone, and a
    /// call the chain goes on after (<c>try_f(x).g()</c>, where <c>try</c> would take in <c>.g()</c> too) is listed.
    /// Null when the token at <paramref name="index"/> is not such a call.
    /// </summary>
    private static List<MigrationEdit>? RecoveryPrefixEdits(List<Token> tokens, int index, string file,
        HashSet<string> declared, ref int listed)
    {
        Token token = tokens[index: index];
        string? prefix = new[] { "try_", "check_", "lookup_" }.FirstOrDefault(predicate: p =>
            token.Text.StartsWith(value: p, comparisonType: StringComparison.Ordinal) && token.Text.Length > p.Length);
        if (prefix == null || declared.Contains(item: token.Text) || PrefixedStandardRoutines.Contains(item: token.Text) ||
            index + 1 >= tokens.Count || tokens[index: index + 1].Type is not (TokenType.LeftParen or TokenType.LeftBracket))
        {
            return null;
        }

        Token? before = index > 0 ? tokens[index: index - 1] : null;
        if (before?.Type is TokenType.Routine or TokenType.DoubleColon or TokenType.Try or TokenType.Check)
        {
            return null;
        }

        string name = token.Text[prefix.Length..];
        string keyword = prefix == "try_" ? "try" : "check";
        int open = index + 1;
        if (tokens[index: open].Type == TokenType.LeftBracket)
        {
            open = SkipBalanced(tokens: tokens, start: open, open: TokenType.LeftBracket, close: TokenType.RightBracket);
        }

        int afterCall = SkipBalanced(tokens: tokens, start: open, open: TokenType.LeftParen, close: TokenType.RightParen);
        if (afterCall < tokens.Count && tokens[index: afterCall].Type is TokenType.Dot or TokenType.LeftBracket)
        {
            Console.WriteLine(value: $"{file}:{token.Line}: '{token.Text}(…)' is '{keyword} {name}(…)' now, and the " +
                                     $"chain after it would move inside the '{keyword}': rewrite it by hand");
            listed++;
            return null;
        }

        string note = prefix == "lookup_" ? " (rewrite an is None arm to is AbsentValueError by hand)" : "";
        var edits = new List<MigrationEdit>();
        if (before?.Type == TokenType.Dot)
        {
            int receiver = OperandStart(tokens: tokens, last: index - 2);
            if (receiver < 0 || tokens[index: receiver].Position < 0)
            {
                return null;
            }

            edits.Add(item: new MigrationEdit(Start: tokens[index: receiver].Position, Length: 0,
                Replacement: keyword + " ", Line: token.Line, What: $".{token.Text}(…) → {keyword} ….{name}(…){note}"));
            edits.Add(item: new MigrationEdit(Start: token.Position, Length: token.Text.Length, Replacement: name,
                Line: token.Line, What: $".{token.Text}(…) → {keyword} ….{name}(…){note}"));
            return edits;
        }

        edits.Add(item: new MigrationEdit(Start: token.Position, Length: token.Text.Length,
            Replacement: $"{keyword} {name}", Line: token.Line, What: $"{token.Text}(…) → {keyword} {name}(…){note}"));
        return edits;
    }
}
