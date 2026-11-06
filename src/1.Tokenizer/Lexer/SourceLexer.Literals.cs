using System.Text;
using Builder.Diagnostics;

using Builder.Tokenizer;

namespace Builder.Tokenizer;

/// <summary>
/// Partial class containing string and character literal scanning memberRoutines for the source lexer.
/// </summary>
public partial class SourceLexer
{
    #region String Literals

    /// <summary>
    /// Scans a basic string literal (without prefix).
    /// </summary>
    private void ScanString()
    {
        ScanStringLiteral(isRaw: false,
            isFormatted: false,
            tokenType: TokenType.TextLiteral);
    }

    /// <summary>
    /// Attempts to parse a text prefix (r, f, rf) followed by a quoted string. A <c>b</c> or <c>br</c> prefix
    /// has no meaning and is a grammar error.
    /// </summary>
    /// <returns>
    /// <c>true</c> if a valid text prefix was found; <c>false</c> otherwise.
    /// </returns>
    private bool TryParseTextPrefix()
    {
        int startPos = _position - 1;
        int originalPos = _position;
        int originalCol = _column;

        char firstChar = _source[index: startPos];
        var prefixSb = new StringBuilder();
        prefixSb.Append(value: firstChar);

        // Greedy match: build the longest valid prefix
        while (!IsAtEnd() && char.IsLetterOrDigit(c: Peek()))
        {
            string testPrefix = prefixSb.ToString() + Peek();
            if (_textPrefixes.Any(predicate: p => p.StartsWith(value: testPrefix)))
            {
                prefixSb.Append(value: Advance());
            }
            else
            {
                break;
            }
        }

        string prefix = prefixSb.ToString();
        // Check if we found a valid prefix
        if (prefix is "b" or "br" && Peek() is '"')
        {
            throw ByteLiteralPrefixError();
        }

        if (!_textPrefixToTokenType.TryGetValue(key: prefix, value: out TokenType tokenType))
        {
            _position = originalPos;
            _column = originalCol;
            return false;
        }

        // Must be followed by a quote
        if (Peek() != '"')
        {
            _position = originalPos;
            _column = originalCol;
            return false;
        }

        Advance(); // consume opening quote
        bool isRaw = prefix.Contains(value: 'r');
        bool isFormatted = prefix.Contains(value: 'f');

        ScanStringLiteral(isRaw: isRaw,
            isFormatted: isFormatted,
            tokenType: tokenType);
        return true;
    }

    /// <summary>
    /// Scans a string literal with the specified properties.
    /// For formatted strings, delegates to ScanFormattedStringLiteral.
    /// </summary>
    private void ScanStringLiteral(bool isRaw, bool isFormatted, TokenType tokenType)
    {
        if (isFormatted)
        {
            ScanFormattedStringLiteral(isRaw: isRaw);
            return;
        }

        int startLine = _line;
        int startColumn = _column;
        var content = new StringBuilder();

        while (!IsAtEnd() && Peek() != '"')
        {
            ScanStringLiteralChar(isRaw: isRaw, content: content);
        }

        if (IsAtEnd())
        {
            throw new GrammarException(code: GrammarDiagnosticCode.UnterminatedString,
                message: $"Unterminated text starting at line {startLine}, column {startColumn}",
                fileName: _fileName,
                line: startLine,
                column: startColumn,
                language: _language);
        }

        Advance(); // consume closing quote
        AddToken(type: tokenType, text: content.ToString());
    }

    /// <summary>
    /// Scans a single character (or escape sequence) within a basic string literal, appending its
    /// value to <paramref name="content"/>. Handles literal newlines, escape sequences (with line
    /// continuation), and byte-literal ASCII validation.
    /// </summary>
    private void ScanStringLiteralChar(bool isRaw, StringBuilder content)
    {
        if (Peek() == '\n')
        {
            content.Append(value: '\n');
            Advance();
        }
        else if (!isRaw && Peek() == '\\')
        {
            int escapeStart = _position;
            Advance(); // consume backslash

            // Check for line continuation (\ followed by newline)
            if (Peek() == '\n' || Peek() == '\r')
            {
                // Line continuation: skip newline and leading whitespace, don't add to content
                ScanEscapeSequence(allowHex: true);
            }
            else
            {
                ScanEscapeSequence(allowHex: true);
                content.Append(value: ParseEscapeSequence(escapeStart: escapeStart));
            }
        }
        else
        {
            content.Append(value: Advance());
        }
    }

    /// <summary>
    /// Scans a formatted string literal (f"..." or rf"..."), emitting a structured token sequence:
    /// InsertionStart, TextSegment*, (LeftBrace, expr tokens, RightBrace)*, InsertionEnd
    /// </summary>
    private void ScanFormattedStringLiteral(bool isRaw)
    {
        int startLine = _line;
        int startColumn = _column;

        // Emit InsertionStart token (text = "f\"" or "rf\"")
        string prefix = isRaw
            ? "rf\""
            : "f\"";
        AddToken(type: TokenType.InsertionStart, text: prefix);

        var textBuffer = new StringBuilder();

        while (!IsAtEnd())
        {
            // Returns true when the closing quote was consumed (f-string complete).
            if (ScanFormattedStringChar(isRaw: isRaw, textBuffer: textBuffer))
            {
                return;
            }
        }

        // Reached EOF without closing quote
        throw new GrammarException(code: GrammarDiagnosticCode.UnterminatedString,
            message:
            $"Unterminated formatted text starting at line {startLine}, column {startColumn}",
            fileName: _fileName,
            line: startLine,
            column: startColumn,
            language: _language);
    }

    /// <summary>
    /// Handles one character position within a formatted string literal: the closing quote,
    /// insertion braces (with <c>{{</c>/<c>}}</c> escapes), escape sequences, and literal text.
    /// </summary>
    /// <returns><c>true</c> if the closing quote was consumed and scanning is complete.</returns>
    private bool ScanFormattedStringChar(bool isRaw, StringBuilder textBuffer)
    {
        char c = Peek();

        if (c == '"')
        {
            // End of f-string — flush remaining text and emit InsertionEnd
            FlushTextSegment(textBuffer: textBuffer);
            Advance(); // consume closing quote
            _tokenStart = _position - 1;
            _tokenStartColumn = _column - 1;
            _tokenStartLine = _line;
            AddToken(type: TokenType.InsertionEnd, text: "\"");
            return true;
        }

        if (c == '{')
        {
            ScanFormattedStringOpenBrace(textBuffer: textBuffer);
            return false;
        }

        if (c == '}')
        {
            ScanFormattedStringCloseBrace(textBuffer: textBuffer);
            return false;
        }

        if (!isRaw && c == '\\')
        {
            // Process escape sequence
            int escapeStart = _position;
            Advance(); // consume backslash
            if (Peek() == '\n' || Peek() == '\r')
            {
                ScanEscapeSequence(allowHex: false);
            }
            else
            {
                ScanEscapeSequence(allowHex: false);
                textBuffer.Append(value: ParseEscapeSequence(escapeStart: escapeStart));
            }

            return false;
        }

        if (c == '\n')
        {
            textBuffer.Append(value: '\n');
            Advance();
            return false;
        }

        // Regular character
        textBuffer.Append(value: Advance());
        return false;
    }

    /// <summary>
    /// Handles a '{' in a formatted string: either an escaped <c>{{</c> → literal '{', or the
    /// start of an insertion expression (flush text, emit LeftBrace, scan the expression).
    /// </summary>
    private void ScanFormattedStringOpenBrace(StringBuilder textBuffer)
    {
        if (Peek(offset: 1) == '{')
        {
            // Doubled left-brace: escape sequence for a literal brace character in the output.
            Advance();
            Advance();
            textBuffer.Append(value: '{');
            return;
        }

        // Start of insertion expression — flush text, emit LeftBrace
        FlushTextSegment(textBuffer: textBuffer);
        _tokenStart = _position;
        _tokenStartColumn = _column;
        _tokenStartLine = _line;
        Advance(); // consume the opening brace
        AddToken(type: TokenType.LeftBrace, text: "{");
        _bracketDepth++;
        ScanInsertionExpression();
    }

    /// <summary>
    /// Handles a '}' in a formatted string: an escaped <c>}}</c> → literal '}', otherwise an
    /// unmatched brace error.
    /// </summary>
    private void ScanFormattedStringCloseBrace(StringBuilder textBuffer)
    {
        if (Peek(offset: 1) == '}')
        {
            // Escaped brace }} → literal }
            Advance();
            Advance();
            textBuffer.Append(value: '}');
            return;
        }

        // Unmatched } outside insertion — treat as error
        throw new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
            message: "Unmatched '}' in formatted text. Use '}}' for a literal brace.",
            fileName: _fileName,
            line: _line,
            column: _column,
            language: _language);
    }

    /// <summary>
    /// Scans the tokens inside an insertion expression ({...}) within a formatted string.
    /// Delegates to ScanToken() for each token until the matching } is found.
    /// </summary>
    private void ScanInsertionExpression()
    {
        int entryDepth = _bracketDepth;

        while (!IsAtEnd())
        {
            SkipInsertionWhitespace();

            if (IsAtEnd())
            {
                break;
            }

            _tokenStart = _position;
            _tokenStartColumn = _column;
            _tokenStartLine = _line;

            // Check for } at entry depth — end of insertion
            if (Peek() == '}' && _bracketDepth == entryDepth)
            {
                Advance(); // consume }
                AddToken(type: TokenType.RightBrace, text: "}");
                _bracketDepth--;
                return;
            }

            // Check for : at entry depth — format specifier. A `::` is a REALM qualifier
            // (`f"{LLVM::int_eq[U128](...)}"`), NOT a format-spec start, so only a lone `:`
            // (not followed by another `:`) begins the format specifier.
            if (Peek() == ':' && Peek(offset: 1) != ':' && _bracketDepth == entryDepth)
            {
                ScanFormatSpec(entryDepth: entryDepth);
                continue;
            }

            // Otherwise, scan a regular token
            ScanInsertionToken(c: Advance());
        }
    }

    /// <summary>
    /// Skips whitespace (including newlines) inside an insertion expression, updating line/column
    /// tracking for consumed newlines.
    /// </summary>
    private void SkipInsertionWhitespace()
    {
        while (!IsAtEnd() && (Peek() == ' ' || Peek() == '\t' || Peek() == '\r' || Peek() == '\n'))
        {
            if (Peek() == '\n')
            {
                _line++;
                _column = 0;
            }

            Advance();
        }
    }

    /// <summary>
    /// Decrements the bracket nesting depth when currently inside at least one open bracket,
    /// preventing underflow below zero for unmatched closing brackets.
    /// </summary>
    private void DecrementBracketDepth()
    {
        if (_bracketDepth > 0)
        {
            _bracketDepth--;
        }
    }

    /// <summary>
    /// Scans a single token inside an insertion expression given its already-consumed first
    /// character, dispatching to the shared operator/literal scanners.
    /// </summary>
    private void ScanInsertionToken(char c)
    {
        switch (c)
        {
            case '(':
                AddToken(type: TokenType.LeftParen);
                _bracketDepth++;
                break;
            case ')':
                AddToken(type: TokenType.RightParen);
                DecrementBracketDepth();
                break;
            case '[':
                AddToken(type: TokenType.LeftBracket);
                _bracketDepth++;
                break;
            case ']':
                AddToken(type: TokenType.RightBracket);
                DecrementBracketDepth();
                break;
            case '{':
                AddToken(type: TokenType.LeftBrace);
                _bracketDepth++;
                break;
            case '}':
                AddToken(type: TokenType.RightBrace);
                DecrementBracketDepth();
                break;
            case ',':
                AddToken(type: TokenType.Comma);
                break;
            case '.':
                AddToken(type: TokenType.Dot);
                break;
            case '+':
                ScanPlusOperator();
                break;
            case '-':
                ScanInsertionMinusToken();
                break;
            case '*':
                ScanStarOperator();
                break;
            case '/':
                ScanSlashOperator();
                break;
            case '%':
                ScanPercentOperator();
                break;
            case ':':
                // A double colon is a realm qualifier (used in realm-qualified calls inside
                // f-string interpolation holes). A lone colon at entry depth is already handled
                // as a format-spec start before we get here. Reaching this case with a lone
                // colon means we are inside nested parens or brackets, so emit a plain Colon
                // to support named arguments.
                AddToken(type: Match(expected: ':')
                    ? TokenType.DoubleColon
                    : TokenType.Colon);
                break;
            case '=':
                ScanInsertionEqualsToken();
                break;
            case '!':
                ScanInsertionBangToken();
                break;
            case '<':
                ScanLessThanOperator();
                break;
            case '>':
                ScanGreaterThanOperator();
                break;
            case '&':
                AddToken(type: Match(expected: '=')
                    ? TokenType.AmpersandAssign
                    : TokenType.Ampersand);
                break;
            case '|':
                AddToken(type: Match(expected: '=')
                    ? TokenType.PipeAssign
                    : TokenType.Pipe);
                break;
            case '^':
                AddToken(type: Match(expected: '=')
                    ? TokenType.CaretAssign
                    : TokenType.Caret);
                break;
            case '~':
                AddToken(type: TokenType.Tilde);
                break;
            case '?':
                ScanInsertionQuestionToken();
                break;
            case '"':
                ScanString();
                break;
            case '\'':
                ScanCharacter();
                break;
            case '`':
                ScanBacktickName();
                break;
            // Prefixed string literals (b"..", r"..", f"..", rf"..", br"..") —
            // mirror Tokenizer.Scanning so nested literals work inside f-string
            // interpolation holes, e.g. f"{try parse(bytes: b"42")}".
            case 'r' or 'f':
                ScanInsertionTextPrefixOrIdentifier();
                break;
            case 'b':
                ScanInsertionByteOrIdentifier();
                break;
            default:
                ScanInsertionDefaultToken(c: c);
                break;
        }
    }

    /// <summary>Scans a '-' token inside an insertion expression: arrow or minus operator.</summary>
    private void ScanInsertionMinusToken()
    {
        if (Match(expected: '>'))
        {
            AddToken(type: TokenType.Arrow);
        }
        else
        {
            ScanMinusOperator();
        }
    }

    /// <summary>Scans an '=' token inside an insertion expression: identity-equal, fat-arrow, or assign.</summary>
    private void ScanInsertionEqualsToken()
    {
        if (Match(expected: '='))
        {
            AddToken(type: Match(expected: '=')
                ? TokenType.IdentityEqual
                : TokenType.Equal);
        }
        else if (Match(expected: '>'))
        {
            AddToken(type: TokenType.FatArrow);
        }
        else
        {
            AddToken(type: TokenType.Assign);
        }
    }

    /// <summary>Scans a '!' token inside an insertion expression: identity-not-equal, bang-bang, or bang.</summary>
    private void ScanInsertionBangToken()
    {
        if (Match(expected: '='))
        {
            AddToken(type: Match(expected: '=')
                ? TokenType.IdentityNotEqual
                : TokenType.NotEqual);
        }
        else if (Match(expected: '!'))
        {
            AddToken(type: TokenType.BangBang);
        }
        else
        {
            AddToken(type: TokenType.Bang);
        }
    }

    /// <summary>Scans a '?' token inside an insertion expression: none-coalesce, or question.</summary>
    private void ScanInsertionQuestionToken()
    {
        if (Match(expected: '?'))
        {
            AddToken(type: Match(expected: '=')
                ? TokenType.NoneCoalesceAssign
                : TokenType.NoneCoalesce);
        }
        else
        {
            AddToken(type: TokenType.Question);
        }
    }

    /// <summary>
    /// Handles 'r' or 'f' characters inside an insertion expression: tries a prefixed text literal
    /// (rf"...", r"...", f"..."), falling back to scanning as an identifier.
    /// </summary>
    private void ScanInsertionTextPrefixOrIdentifier()
    {
        if (!TryParseTextPrefix())
        {
            ScanIdentifier();
        }
    }

    /// <summary>
    /// Handles 'b' characters inside an insertion expression: a name, or a grammar error for a `b` prefix on a
    /// literal (b"...", b'x').
    /// </summary>
    private void ScanInsertionByteOrIdentifier()
    {
        if (!TryParseTextPrefix() && !TryParseByteLiteralPrefix())
        {
            ScanIdentifier();
        }
    }

    /// <summary>
    /// Handles the default insertion-token case: prefixed numbers (hex/binary/octal), decimal
    /// numbers, and a permissive identifier fallback for any other character.
    /// </summary>
    private void ScanInsertionDefaultToken(char c)
    {
        if (c == '0' && (Peek() == 'x' || Peek() == 'X'))
        {
            Advance(); // consume 'x'/'X'
            ScanPrefixedNumber(isHex: true);
        }
        else if (c == '0' && (Peek() == 'b' || Peek() == 'B') && (Peek(offset: 1) == '0' ||
                     Peek(offset: 1) == '1' || Peek(offset: 1) == '_'))
        {
            Advance(); // consume 'b'/'B'
            ScanPrefixedNumber(isHex: false);
        }
        else if (c == '0' && (Peek() == 'o' || Peek() == 'O') &&
                 (Peek(offset: 1) >= '0' && Peek(offset: 1) <= '7' || Peek(offset: 1) == '_'))
        {
            Advance(); // consume 'o'/'O'
            ScanOctalNumber();
        }
        else if (char.IsDigit(c: c))
        {
            ScanNumber();
        }
        else
        {
            // Permissive fallback: anything that isn't a recognised
            // bracket/punct/operator above is treated as identifier
            // start. Lets sigils like `$` (and future ones) flow
            // through without per-char allow-listing.
            ScanIdentifier();
        }
    }

    /// <summary>
    /// Scans a format specifier after ':' inside an insertion expression.
    /// Consumes everything from ':' until '}' at entry depth as a single FormatSpec token.
    /// </summary>
    private void ScanFormatSpec(int entryDepth)
    {
        Advance(); // consume ':'
        _tokenStart = _position;
        _tokenStartColumn = _column;
        _tokenStartLine = _line;

        var spec = new StringBuilder();
        int depth = _bracketDepth;

        while (!IsAtEnd())
        {
            char c = Peek();
            if (c == '}' && depth == entryDepth)
            {
                break;
            }

            if (c == '{')
            {
                depth++;
            }

            if (c == '}')
            {
                depth--;
            }

            spec.Append(value: Advance());
        }

        if (spec.Length > 0)
        {
            AddToken(type: TokenType.FormatSpec, text: spec.ToString());
        }
    }

    /// <summary>
    /// Flushes accumulated text in the buffer as a TextSegment token.
    /// </summary>
    private void FlushTextSegment(StringBuilder textBuffer)
    {
        if (textBuffer.Length <= 0)
        {
            return;
        }

        _tokenStart = _position;
        _tokenStartColumn = _column;
        _tokenStartLine = _line;
        AddToken(type: TokenType.TextSegment, text: textBuffer.ToString());
        textBuffer.Clear();
    }

    #endregion

    #region Character Literals

    /// <summary>
    /// Scans a basic character literal (single-quoted).
    /// </summary>
    private void ScanCharacter()
    {
        ScanCharacterLiteral();
    }

    /// <summary>
    /// A <c>b</c> directly before a quote has no meaning: raises the grammar error that says what to write instead.
    /// Returns false for any other <c>b</c>, which starts a name.
    /// </summary>
    private bool TryParseByteLiteralPrefix()
    {
        if (Peek() != '\'')
        {
            return false;
        }

        throw ByteLiteralPrefixError();
    }

    /// <summary>
    /// The grammar error for <c>b"..."</c>, <c>br"..."</c> and <c>b'x'</c>: a literal has no prefix that makes it
    /// bytes, since the place it goes decides that.
    /// </summary>
    private GrammarException ByteLiteralPrefixError()
    {
        return new GrammarException(code: GrammarDiagnosticCode.ByteLiteralPrefix,
            message: "A literal has no 'b' prefix. Write a plain \"...\" where a Bytes goes and a plain 'x' where a " +
                     "Byte goes (\"\\xE9\" is the byte E9 there), or write Byte('x') for one byte.",
            fileName: _fileName,
            line: _line,
            column: _column,
            language: _language);
    }

    /// <summary>
    /// Scans a character literal. A <c>\xHH</c> escape is kept for the place to decide (a Byte takes it, a Char refuses).
    /// </summary>
    private void ScanCharacterLiteral()
    {
        if (Peek() == '\\')
        {
            Advance(); // consume backslash

            ScanEscapeSequence(allowHex: true);
        }
        else
        {
            char c = Peek();
            Advance(); // consume the character
            // Handle UTF-16 surrogate pairs for non-BMP codepoints (Character is UTF-32)
            if (char.IsHighSurrogate(c: c) && !IsAtEnd() && char.IsLowSurrogate(c: Peek()))
            {
                Advance(); // consume low surrogate
            }
        }

        if (!Match(expected: '\''))
        {
            throw new GrammarException(code: GrammarDiagnosticCode.UnterminatedString,
                message: "Unterminated character literal",
                fileName: _fileName,
                line: _line,
                column: _column,
                language: _language);
        }

        AddToken(type: TokenType.CharacterLiteral);
    }

    #endregion

    #region Escape Sequences

    /// <summary>
    /// Scans and validates an escape sequence.
    /// </summary>
    private void ScanEscapeSequence(bool allowHex)
    {
        if (IsAtEnd())
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidEscapeSequence,
                message: "Unterminated escape sequence",
                fileName: _fileName,
                line: _line,
                column: _column,
                language: _language);
        }

        char escapeChar = Peek();
        switch (escapeChar)
        {
            case 'n' or 't' or 'r' or '\\' or '"' or '\'' or '0':
                Advance();
                break;
            case 'x':
                if (!allowHex)
                {
                    throw new GrammarException(code: GrammarDiagnosticCode.InvalidEscapeSequence,
                        message:
                        "Inserted text has no \\x escape. Write the character with \\u followed by six hex digits " +
                        "(\\u0000FF).",
                        fileName: _fileName,
                        line: _line,
                        column: _column,
                        language: _language);
                }

                Advance(); // consume 'x'
                ScanHexByteEscape();
                break;
            case 'u':
                Advance(); // consume 'u'
                ScanUnicodeEscape();
                break;
            case '\r':
                // Line continuation: \ followed by CRLF
                Advance();
                if (Peek() == '\n')
                {
                    Advance();
                }

                while (Peek() == ' ' || Peek() == '\t')
                {
                    Advance();
                }

                break;
            case '\n':
                // Line continuation: \ followed by LF
                Advance();
                while (Peek() == ' ' || Peek() == '\t')
                {
                    Advance();
                }

                break;
            default:
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidEscapeSequence,
                    message: $"Invalid escape sequence '\\{escapeChar}'",
                    fileName: _fileName,
                    line: _line,
                    column: _column,
                    language: _language);
        }
    }

    /// <summary>
    /// Scans and validates a hex byte escape sequence (\xFF).
    /// </summary>
    private void ScanHexByteEscape()
    {
        for (int i = 0; i < 2; i += 1)
        {
            if (!IsHexDigit(c: Peek()))
            {
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidEscapeSequence,
                    message: "Invalid hex byte escape: expected 2 hex digits (\\xFF)",
                    fileName: _fileName,
                    line: _line,
                    column: _column,
                    language: _language);
            }

            Advance();
        }
    }

    /// <summary>
    /// Scans and validates a Unicode escape sequence (\uXXXXXX).
    /// </summary>
    private void ScanUnicodeEscape()
    {
        int digitsStart = _position;
        for (int i = 0; i < 6; i += 1)
        {
            if (!IsHexDigit(c: Peek()))
            {
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidEscapeSequence,
                    message: "Invalid Unicode escape: expected 6 hex digits (\\uXXXXXX)",
                    fileName: _fileName,
                    line: _line,
                    column: _column,
                    language: _language);
            }

            Advance();
        }

        int codePoint = Convert.ToInt32(value: _source.Substring(startIndex: digitsStart, length: 6), fromBase: 16);
        if (codePoint > 0x10FFFF)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidEscapeSequence,
                message: $"U+{codePoint:X} is above U+10FFFF, the last Unicode code point. Write a code point " +
                         "from \\u000000 to \\u10FFFF.",
                fileName: _fileName,
                line: _line,
                column: _column,
                language: _language);
        }

        if (codePoint is >= 0xD800 and <= 0xDFFF)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidEscapeSequence,
                message: $"U+{codePoint:X4} is a surrogate, not a character. Write a code point outside " +
                         "U+D800 to U+DFFF.",
                fileName: _fileName,
                line: _line,
                column: _column,
                language: _language);
        }
    }

    /// <summary>
    /// A <c>\xHH</c> escape stays in a literal's value as the lone low surrogate U+DC00 + HH. No source text can
    /// spell one (a <c>\u</c> escape refuses a surrogate and a UTF-8 source has none), so the place the literal goes
    /// reads each as the byte HH, or reports it where the place is text.
    /// </summary>
    public const int HexEscapeBase = 0xDC00;

    /// <summary>
    /// Parses an escape sequence and returns the text it stands for (one character, or the surrogate pair of a code
    /// point above U+FFFF).
    /// </summary>
    private string ParseEscapeSequence(int escapeStart)
    {
        char c = _source[index: escapeStart + 1];

        if (c == 'x')
        {
            string hexStr = _source.Substring(startIndex: escapeStart + 2, length: 2);
            int byteValue = Convert.ToInt32(value: hexStr, fromBase: 16);
            return ((char)(HexEscapeBase + byteValue)).ToString();
        }

        if (c == 'u')
        {
            string hexStr = _source.Substring(startIndex: escapeStart + 2, length: 6);
            int codePoint = Convert.ToInt32(value: hexStr, fromBase: 16);

            // The scan already refused a surrogate and anything above U+10FFFF. A code point above U+FFFF is two
            // UTF-16 units (a surrogate pair).
            return char.ConvertFromUtf32(utf32: codePoint);
        }

        return EscapeCharacter(c: c)
           .ToString();
    }

    /// <summary>
    /// Converts a simple escape character to its actual value.
    /// </summary>
    private static char EscapeCharacter(char c)
    {
        return c switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '\\' => '\\',
            '"' => '"',
            '\'' => '\'',
            '0' => '\0',
            _ => c
        };
    }

    #endregion
}
