using System.Text;
using Builder.Tokenizer;

namespace Builder.Formatting;

/// <summary>
/// The lexed source of one file, indexed for the formatter: the tokens by position, the logical lines they form
/// (a logical line ends at a <c>Newline</c>, <c>Indent</c> or <c>Dedent</c> token, so a line that continues inside
/// brackets or after a leading <c>and</c>/<c>or</c> belongs to the line it continues), the physical source lines,
/// and the comments. The syntax tree carries a start location only, so everything the formatter needs about where a
/// construct ends, which annotation tokens precede it, or how its header was spelled comes from here.
/// </summary>
internal sealed class SourceIndex
{
    private readonly int[] _tokenPositions;

    /// <summary>Indexes the normalized <paramref name="source"/>, its <paramref name="tokens"/> and its comments.</summary>
    public SourceIndex(string source, List<Token> tokens, List<CommentTrivia> comments)
    {
        Source = source;
        Tokens = tokens;
        Comments = comments;
        Lines = source.Split(separator: '\n');
        _tokenPositions = tokens.Select(selector: t => t.Position)
                                .ToArray();
    }

    /// <summary>The normalized source text (no BOM, LF line ends): token positions index into it.</summary>
    public string Source { get; }

    /// <summary>The token stream, ending with <c>Eof</c>.</summary>
    public List<Token> Tokens { get; }

    /// <summary>Every comment, in source order.</summary>
    public List<CommentTrivia> Comments { get; }

    /// <summary>The physical source lines (index 0 is line 1).</summary>
    public string[] Lines { get; }

    /// <summary>True for the tokens that end a logical line (and the end of file).</summary>
    public static bool IsSeparator(Token token)
    {
        return token.Type is TokenType.Newline or TokenType.Indent or TokenType.Dedent or TokenType.Eof;
    }

    /// <summary>
    /// The index of the token that starts at <paramref name="position"/>, or else of the last token that starts
    /// before it (a position inside a token maps to that token).
    /// </summary>
    public int TokenIndexAt(int position)
    {
        int index = Array.BinarySearch(array: _tokenPositions, value: position);
        if (index >= 0)
        {
            // Several structural tokens can share a position (a Dedent and the token after it): take the last
            // real one so the answer is the token actually written there.
            while (index + 1 < _tokenPositions.Length && _tokenPositions[index + 1] == position &&
                   IsSeparator(token: Tokens[index: index]))
            {
                index++;
            }

            return index;
        }

        int insertion = ~index;
        return Math.Max(val1: 0, val2: insertion - 1);
    }

    /// <summary>
    /// The index of the first token of the logical line that holds the token at <paramref name="tokenIndex"/>.
    /// A separator token belongs to the line it ends.
    /// </summary>
    public int LogicalLineStart(int tokenIndex)
    {
        int i = Math.Min(val1: tokenIndex, val2: Tokens.Count - 1);
        while (i > 0 && IsSeparator(token: Tokens[index: i]))
        {
            i--;
        }

        while (i > 0 && !IsSeparator(token: Tokens[index: i - 1]))
        {
            i--;
        }

        return i;
    }

    /// <summary>The index of the last token of the logical line that starts at <paramref name="startIndex"/>.</summary>
    public int LogicalLineEnd(int startIndex)
    {
        int i = startIndex;
        while (i + 1 < Tokens.Count && !IsSeparator(token: Tokens[index: i + 1]))
        {
            i++;
        }

        return i;
    }

    /// <summary>The position of the first token of the logical line holding <paramref name="position"/>.</summary>
    public int LineStartPosition(int position)
    {
        return Tokens[index: LogicalLineStart(tokenIndex: TokenIndexAt(position: position))].Position;
    }

    /// <summary>
    /// The index of the first token of the logical line before the one starting at <paramref name="startIndex"/>,
    /// or -1 when there is none.
    /// </summary>
    public int PreviousLogicalLineStart(int startIndex)
    {
        int i = startIndex - 1;
        while (i >= 0 && IsSeparator(token: Tokens[index: i]))
        {
            i--;
        }

        return i < 0
            ? -1
            : LogicalLineStart(tokenIndex: i);
    }

    /// <summary>
    /// The start of a declaration's unit: the logical line of <paramref name="keywordPosition"/>, widened over the
    /// annotation lines written right above it (but never over an <c>@target</c> directive, which belongs to the
    /// file).
    /// </summary>
    public int DeclarationStartIndex(int keywordPosition)
    {
        int start = LogicalLineStart(tokenIndex: TokenIndexAt(position: keywordPosition));
        while (true)
        {
            int previous = PreviousLogicalLineStart(startIndex: start);
            if (previous < 0 || Tokens[index: previous].Type != TokenType.At || IsTargetDirective(atIndex: previous))
            {
                return start;
            }

            start = previous;
        }
    }

    /// <summary>True when the <c>@</c> at <paramref name="atIndex"/> opens an <c>@target(...)</c> directive.</summary>
    public bool IsTargetDirective(int atIndex)
    {
        return atIndex + 1 < Tokens.Count && Tokens[index: atIndex].Type == TokenType.At &&
               Tokens[index: atIndex + 1].Type == TokenType.Identifier && Tokens[index: atIndex + 1].Text == "target";
    }

    /// <summary>The index of the bracket that closes the one at <paramref name="openIndex"/>.</summary>
    public int MatchingClose(int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < Tokens.Count; i++)
        {
            TokenType type = Tokens[index: i].Type;
            if (type is TokenType.LeftParen or TokenType.LeftBracket or TokenType.LeftBrace or TokenType.SpliceOpen)
            {
                depth++;
            }
            else if (type is TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return Tokens.Count - 1;
    }

    /// <summary>The 1-based source line of the token at <paramref name="tokenIndex"/>.</summary>
    public int LineOf(int tokenIndex)
    {
        return Tokens[index: tokenIndex].Line;
    }

    /// <summary>True when 1-based source line <paramref name="line"/> exists and holds only spaces.</summary>
    public bool IsBlankLine(int line)
    {
        return line >= 1 && line <= Lines.Length && Lines[line - 1]
                                                         .Trim()
                                                         .Length == 0;
    }

    /// <summary>True when the comment is alone on its line (only spaces before its <c>#</c>).</summary>
    public bool IsOwnLine(CommentTrivia comment)
    {
        string line = Lines[comment.Line - 1];
        return line[..Math.Min(val1: comment.Column - 1, val2: line.Length)]
              .Trim()
              .Length == 0;
    }

    /// <summary>The source text from the start of the token at <paramref name="first"/> to the end of the token at
    /// <paramref name="last"/>, exactly as written.</summary>
    public string SourceSlice(int first, int last)
    {
        int start = Tokens[index: first].Position;
        int end = TokenEnd(tokenIndex: last);
        return Source[start..end];
    }

    /// <summary>
    /// The source offset just past the token at <paramref name="tokenIndex"/>. A string literal's token text is its
    /// decoded value, so the end of a quoted literal is found by scanning the source to its closing quote.
    /// </summary>
    public int TokenEnd(int tokenIndex)
    {
        Token token = Tokens[index: tokenIndex];
        if (token.Type is TokenType.TextLiteral or TokenType.RawText or TokenType.BytesLiteral
            or TokenType.BytesRawLiteral)
        {
            int quote = Source.IndexOf(value: '"', startIndex: token.Position);
            bool raw = token.Type is TokenType.RawText or TokenType.BytesRawLiteral;
            int i = quote + 1;
            while (i < Source.Length && Source[index: i] != '"')
            {
                i += !raw && Source[index: i] == '\\'
                    ? 2
                    : 1;
            }

            return Math.Min(val1: i + 1, val2: Source.Length);
        }

        return token.Position + token.Text.Length;
    }

    /// <summary>
    /// Re-spaces the tokens <paramref name="first"/>..<paramref name="last"/> (inclusive) in the canonical style:
    /// one space between words, none inside brackets or around <c>.</c>, <c>::</c> and <c>/</c>, one space after a
    /// comma. Used for the parts of a header the syntax tree keeps only partly (a routine's receiver and generic
    /// brackets, a type's generic parameter list).
    /// </summary>
    public string Respace(int first, int last)
    {
        var text = new StringBuilder();
        for (int i = first; i <= last; i++)
        {
            Token token = Tokens[index: i];
            if (IsSeparator(token: token))
            {
                continue;
            }

            if (text.Length > 0 && NeedsSpace(before: Tokens[index: PreviousReal(index: i)], after: token))
            {
                text.Append(value: ' ');
            }

            text.Append(value: TokenSourceText(tokenIndex: i));
        }

        return text.ToString();
    }

    /// <summary>The token's text as written in the source.</summary>
    public string TokenSourceText(int tokenIndex)
    {
        Token token = Tokens[index: tokenIndex];
        return token.Type switch
        {
            TokenType.TextLiteral or TokenType.RawText or TokenType.BytesLiteral or TokenType.BytesRawLiteral =>
                Source[token.Position..TokenEnd(tokenIndex: tokenIndex)],
            _ => token.Text
        };
    }

    private int PreviousReal(int index)
    {
        int i = index - 1;
        while (i > 0 && IsSeparator(token: Tokens[index: i]))
        {
            i--;
        }

        return i;
    }

    private static bool NeedsSpace(Token before, Token after)
    {
        if (before.Type is TokenType.LeftBracket or TokenType.LeftParen or TokenType.Dot or TokenType.Dollar
            or TokenType.DoubleColon or TokenType.At or TokenType.Slash or TokenType.SpliceOpen or TokenType.Bang)
        {
            return false;
        }

        if (after.Type is TokenType.RightBracket or TokenType.RightParen or TokenType.Comma or TokenType.Dot
            or TokenType.DoubleColon or TokenType.Slash or TokenType.Question or TokenType.Bang
            or TokenType.DotDotDot or TokenType.Colon)
        {
            return false;
        }

        if (after.Type == TokenType.RightBrace)
        {
            return false;
        }

        if (after.Type is TokenType.LeftBracket or TokenType.LeftParen)
        {
            // `Name[`, `name(` and `x](` attach. After a keyword (`in [`, `is (`) the bracket is its own word.
            return !(before.Type is TokenType.Identifier or TokenType.MyType or TokenType.None or TokenType.Me
                or TokenType.RightBracket or TokenType.RightParen or TokenType.NoneValue);
        }

        return true;
    }
}
