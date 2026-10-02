using Builder.Diagnostics;
using Builder.Tokenizer;

namespace Builder.Parser;

/// <summary>
/// Partial class containing annotation parsing helpers for declarations.
/// </summary>
public partial class Parser
{
    private List<string> ParseAnnotations()
    {
        var annotations = new List<string>();

        // Handle @annotation and @[...] compound annotations
        while (Check(type: TokenType.At))
        {
            if (!CheckAndAdvance(type: TokenType.At))
            {
                break; // No more annotations
            }

            // Check for compound annotation syntax: @[attr1, attr2, ...]
            if (CheckAndAdvance(type: TokenType.LeftBracket))
            {
                ParseCompoundAnnotation(annotations: annotations);
            }
            else
            {
                ParseRegularAnnotation(annotations: annotations);
            }

            // Skip newlines between annotations (allows multiple @attr on separate lines)
            while (CheckAndAdvance(type: TokenType.Newline))
            {
                // Skip newlines
            }
        }

        return annotations;
    }

    /// <summary>
    /// Parses the body of a compound annotation <c>@[attr1, attr2, ...]</c> (the opening <c>@[</c> is
    /// already consumed) and appends each name (with optional arguments) to <paramref name="annotations"/>.
    /// </summary>
    private void ParseCompoundAnnotation(List<string> annotations)
    {
        // Parse comma-separated list of annotation names
        do
        {
            string compoundAnnot = ConsumeIdentifier(
                errorMessage: "Expected annotation name in compound annotation");

            // Check for optional arguments on each annotation
            if (CheckAndAdvance(type: TokenType.LeftParen))
            {
                compoundAnnot += "(" + ParseAnnotationArgumentList() + ")";
            }

            annotations.Add(item: compoundAnnot);
        } while (CheckAndAdvance(type: TokenType.Comma));

        Consume(type: TokenType.RightBracket,
            errorMessage: "Expected ']' after compound annotations");
    }

    /// <summary>
    /// Parses a regular annotation <c>@identifier</c> (the <c>@</c> is already consumed) with optional
    /// arguments and appends it to <paramref name="annotations"/>.
    /// </summary>
    private void ParseRegularAnnotation(List<string> annotations)
    {
        // Regular annotation: @identifier
        string annotName = ConsumeIdentifier(errorMessage: "Expected annotation name after '@'");

        // Check for annotation arguments: @something("size_of") or @deprecated(message: "text")
        if (CheckAndAdvance(type: TokenType.LeftParen))
        {
            annotName += "(" + ParseAnnotationArgumentList() + ")";
        }

        annotations.Add(item: annotName);
    }

    /// <summary>
    /// Parses the argument list for an annotation (the content inside parentheses).
    /// </summary>
    /// <returns>String representation of the argument list.</returns>
    private string ParseAnnotationArgumentList()
    {
        var arguments = new List<string>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Check for named argument: name: value or name = value
                TokenType nextToken = PeekToken(offset: 1)
                   .Type;
                if (Check(type: TokenType.Identifier) &&
                    nextToken is TokenType.Colon or TokenType.Assign)
                {
                    string argName = ConsumeIdentifier(errorMessage: "Expected argument name");
                    // Accept both ':' and '=' as separators
                    if (!CheckAndAdvance(TokenType.Colon, TokenType.Assign))
                    {
                        throw ThrowParseError(code: GrammarDiagnosticCode.UnexpectedToken,
                            message: "Expected ':' or '=' after argument name");
                    }

                    string argValue = ParseAnnotationValue();
                    arguments.Add(item: $"{argName}={argValue}");
                }
                else
                {
                    // Positional argument (string literal, number, identifier)
                    arguments.Add(item: ParseAnnotationValue());
                }
            } while (CheckAndAdvance(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen,
            errorMessage: "Expected ')' after annotation arguments");

        return string.Join(separator: ", ", values: arguments);
    }

    /// <summary>
    /// Parses a single annotation argument value (string, number, bool, or identifier).
    /// </summary>
    /// <returns>String representation of the annotation value.</returns>
    private string ParseAnnotationValue()
    {
        // Annotation values are limited to build-time constants:
        // string, number, bool, or identifier (for enums/presets)

        // String literal — keep it quoted so the stored annotation round-trips as `@llvm("i64")`
        // rather than `@llvm(i64)`. All consumers strip the wrapping quotes (ExtractLlvmAnnotation /
        // llvm_ir template extraction / SA all Trim or strip one pair).
        if (Check(TokenType.TextLiteral, TokenType.BytesLiteral))
        {
            return $"\"{Advance().Text}\"";
        }

        // Boolean literals
        if (CheckAndAdvance(type: TokenType.True))
        {
            return "true";
        }

        if (CheckAndAdvance(type: TokenType.False))
        {
            return "false";
        }

        // Tuple of values: `@case(input: (1, 2), output: 3)`, stored as `(1, 2)`.
        if (CheckAndAdvance(type: TokenType.LeftParen))
        {
            var items = new List<string>();
            if (!Check(type: TokenType.RightParen))
            {
                do
                {
                    items.Add(item: ParseAnnotationValue());
                } while (CommaContinuesList(close: TokenType.RightParen));
            }

            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after the tuple in an annotation argument");
            return "(" + string.Join(separator: ", ", values: items) + ")";
        }

        // A negative number: `@case(input: (-1, 5))`.
        if (Check(type: TokenType.Minus) && IsAnnotationLiteral(type: PeekToken(offset: 1)
               .Type))
        {
            Advance();
            return "-" + AnnotationLiteralText(token: Advance());
        }

        // Any other literal the lexer knows, as written: numbers of every type, durations (`5s`), memory sizes
        // (`1mib`) and characters.
        if (IsAnnotationLiteral(type: CurrentToken.Type))
        {
            return AnnotationLiteralText(token: Advance());
        }

        // Identifier (for choice values or constant references)
        if (Check(type: TokenType.Identifier))
        {
            return Advance()
               .Text;
        }

        throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedAnnotationValue,
            message: $"Expected annotation value, got {CurrentToken.Type}");
    }

    /// <summary>Whether a token is a number, duration, memory-size or character literal.</summary>
    private static bool IsAnnotationLiteral(TokenType type)
    {
        return type is TokenType.UndecidedInteger or TokenType.UndecidedDecimal or TokenType.IntegerLiteral
            or TokenType.DecimalLiteral or TokenType.S8Literal or TokenType.S16Literal or TokenType.S32Literal
            or TokenType.S64Literal or TokenType.S128Literal or TokenType.S256Literal or TokenType.U8Literal
            or TokenType.U16Literal or TokenType.U32Literal or TokenType.U64Literal or TokenType.U128Literal
            or TokenType.U256Literal or TokenType.AddressLiteral or TokenType.B16Literal or TokenType.B32Literal
            or TokenType.B64Literal or TokenType.B128Literal or TokenType.D32Literal or TokenType.D64Literal
            or TokenType.D128Literal or TokenType.ImaginaryLiteral or TokenType.ByteLiteral
            or TokenType.KilobyteLiteral or TokenType.KibibyteLiteral or TokenType.MegabyteLiteral
            or TokenType.MebibyteLiteral or TokenType.GigabyteLiteral or TokenType.GibibyteLiteral
            or TokenType.WeekLiteral or TokenType.DayLiteral or TokenType.HourLiteral or TokenType.MinuteLiteral
            or TokenType.SecondLiteral or TokenType.MillisecondLiteral or TokenType.MicrosecondLiteral
            or TokenType.NanosecondLiteral or TokenType.CharacterLiteral or TokenType.ByteLetterLiteral;
    }

    /// <summary>
    /// A literal's text as written. The lexer keeps only the word of <c>inf_b64</c> / <c>nan_b64</c> and puts the
    /// width in the token type, so the suffix is spelled back on.
    /// </summary>
    private static string AnnotationLiteralText(Token token)
    {
        if (token.Text is not ("inf" or "nan"))
        {
            return token.Text;
        }

        string suffix = token.Type switch
        {
            TokenType.B16Literal => "b16",
            TokenType.B32Literal => "b32",
            TokenType.B64Literal => "b64",
            TokenType.B128Literal => "b128",
            TokenType.D32Literal => "d32",
            TokenType.D64Literal => "d64",
            _ => "d128"
        };
        return token.Text + "_" + suffix;
    }
}
