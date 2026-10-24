using Builder.Declaration;
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
            bool knownCompound = IsKnownAnnotation(name: compoundAnnot, at: PeekToken(offset: -1));

            // Check for optional arguments on each annotation
            if (CheckAndAdvance(type: TokenType.LeftParen))
            {
                compoundAnnot += "(" + ParseAnnotationArgumentList() + ")";
            }

            if (knownCompound)
            {
                annotations.Add(item: compoundAnnot);
            }
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
        bool known = IsKnownAnnotation(name: annotName, at: PeekToken(offset: -1));

        // Check for annotation arguments: @something("size_of") or @deprecated(message: "text")
        if (CheckAndAdvance(type: TokenType.LeftParen))
        {
            annotName += "(" + ParseAnnotationArgumentList() + ")";
        }

        if (known)
        {
            annotations.Add(item: annotName);
        }
    }

    /// <summary>The annotations the builder gives an effect. Any other name is a build error, so a misspelled one
    /// (`@readonyl`) is not quietly ignored.</summary>
    private static readonly HashSet<string> KnownAnnotations =
    [
        "readonly", "writable", "reshaping", "discardable", "positional", "infix", "inline", "no_optimize",
        "override", "overridable", "innate", "generated", "crash_only", "untraced", "blocking",
        RuntimeContract.RoamedMemberRoutine.SelfLockingAnnotation, "layout", "link", "target", "llvm", "llvm_ir", "rf",
        "test", "case", "time_limit", "memory_limit", "skip", "throws", "reflectable"
    ];

    /// <summary>Whether <paramref name="name"/> names an annotation a Suflae program declares: a record that obeys
    /// <c>Annotation</c>, named like a type (<c>@Memoize(limit: 100)</c>). The builder's own annotations are lowercase.
    /// Whether such a record exists is checked once every file is read.</summary>
    public static bool IsProgramAnnotation(string name, TypeModel.Enums.Language language)
    {
        return language == TypeModel.Enums.Language.Suflae && name.Length > 0 && char.IsUpper(c: name[index: 0]);
    }

    /// <summary>The arguments of a program's annotation as a call writes them (<c>limit: 100, name: "x"</c>), from the
    /// form an annotation is kept in (<c>Memoize(limit=100, name="x")</c>). Empty when it has none.</summary>
    public static string ProgramAnnotationArguments(string annotation)
    {
        int open = annotation.IndexOf(value: '(');
        if (open < 0 || !annotation.EndsWith(value: ')'))
        {
            return "";
        }

        string inner = annotation[(open + 1)..^1];
        var pieces = new List<string>();
        int depth = 0;
        bool inText = false;
        int start = 0;
        for (int i = 0; i <= inner.Length; i++)
        {
            char c = i < inner.Length ? inner[index: i] : ',';
            if (inText)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inText = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inText = true;
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    string piece = inner[start..i].Trim();
                    if (piece.Length > 0)
                    {
                        // A named argument is kept as `name=value`.
                        int equals = piece.IndexOf(value: '=');
                        bool named = equals > 0 && piece[..equals].All(predicate: ch => char.IsLetterOrDigit(c: ch) || ch == '_');
                        pieces.Add(item: named ? $"{piece[..equals]}: {piece[(equals + 1)..]}" : piece);
                    }

                    start = i + 1;
                    break;
            }
        }

        return string.Join(separator: ", ", values: pieces);
    }

    /// <summary>Whether <paramref name="name"/> is an annotation the builder knows. An unknown one is reported and left
    /// off the declaration, so nothing reports it a second time.</summary>
    private bool IsKnownAnnotation(string name, Token at)
    {
        if (KnownAnnotations.Contains(item: name) || IsProgramAnnotation(name: name, language: _language))
        {
            return true;
        }

        string? near = Builder.Verification.NameSuggestions.SuggestSimilarName(target: name,
            candidates: KnownAnnotations);
        // Recorded, not thrown: the declaration it sits on still parses, so one misspelled name reports once.
        RecordError(ex: new GrammarException(code: GrammarDiagnosticCode.UnknownAnnotation,
            message: near != null
                ? $"There is no annotation '@{name}'. Did you mean '@{near}'?"
                : $"There is no annotation '@{name}', and the builder would ignore it. Remove it.",
            fileName: FileName,
            line: at.Line,
            column: at.Column,
            language: _language));
        return false;
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

        // A list of names: `@throws([NotFoundError, ParseError])`, stored as `[NotFoundError, ParseError]`.
        if (CheckAndAdvance(type: TokenType.LeftBracket))
        {
            var names = new List<string>();
            if (!Check(type: TokenType.RightBracket))
            {
                do
                {
                    names.Add(item: ParseAnnotationValue());
                } while (CommaContinuesList(close: TokenType.RightBracket));
            }

            Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after the list in an annotation argument");
            return "[" + string.Join(separator: ", ", values: names) + "]";
        }

        // A negative number: `@case(input: (-1, 5))`.
        if (Check(type: TokenType.Minus) && IsAnnotationLiteral(type: PeekToken(offset: 1)
               .Type))
        {
            Advance();
            return "-" + Advance().Text;
        }

        // Any other literal the lexer knows, as written: numbers of every type, durations (`5s`), memory sizes
        // (`1mib`) and characters.
        if (IsAnnotationLiteral(type: CurrentToken.Type))
        {
            return Advance().Text;
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
}
