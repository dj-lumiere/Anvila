using Builder.Diagnostics;
using Builder.Frontends;
using Builder.Tokenizer;
using TypeModel.Enums;

namespace Builder.Formatting;

/// <summary>
/// The outcome of formatting one source file: the formatted text, or the reason the formatter refused it. A refusal
/// leaves the file as it is.
/// </summary>
/// <param name="Output">The formatted text (null when refused).</param>
/// <param name="Refusal">Why the file was not formatted (null when formatted).</param>
public sealed record FormatResult(string? Output, string? Refusal)
{
    /// <summary>True when the file was formatted.</summary>
    public bool Succeeded => Output != null;
}

/// <summary>
/// Thrown inside the formatter when a node or shape cannot be printed exactly. It ends the whole file: the
/// formatter never prints a guess.
/// </summary>
internal sealed class FormatRefusedException(string reason) : Exception(message: reason)
{
    /// <summary>The reason, naming the construct and its location.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// The RazorForge and Suflae source formatter: parses a file, prints its syntax tree in the one canonical layout,
/// and keeps the result only when it parses back to the same tree with the same comments. Language differences come
/// through <see cref="LanguageRules"/>.
/// </summary>
public static class SourceFormatter
{
    /// <summary>The line limit the layout breaks lists and conditions to stay within.</summary>
    public const int MaxLineWidth = 100;

    /// <summary>Formats <paramref name="source"/>, read from <paramref name="fileName"/>, written in
    /// <paramref name="language"/>.</summary>
    /// <param name="keepOrder">Keep the top-level declarations in source order (for code examples in
    /// documentation) instead of grouping them by kind.</param>
    public static FormatResult Format(string source, string fileName, Language language, bool keepOrder = false)
    {
        try
        {
            string output = FormatOrThrow(source: source, fileName: fileName, language: language, keepOrder: keepOrder);

            // The canonical layout is a fixed point: formatting the output again must change nothing.
            string again = FormatOrThrow(source: output, fileName: fileName, language: language, keepOrder: keepOrder);
            if (again != output)
            {
                int line = FirstDifferentLine(a: output, b: again);
                return new FormatResult(Output: null,
                    Refusal: $"formatting the formatted text changes line {line} again (a formatter bug)");
            }

            return new FormatResult(Output: output, Refusal: null);
        }
        catch (FormatRefusedException refusal)
        {
            return new FormatResult(Output: null, Refusal: refusal.Reason);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A formatter bug must not take the file (or a whole run over many files) down with it.
            return new FormatResult(Output: null,
                Refusal: $"the formatter failed ({e.GetType().Name}: {e.Message}) at " +
                         e.StackTrace?.Split(separator: '\n')
                          .FirstOrDefault()
                         ?.Trim());
        }
    }

    private static string FormatOrThrow(string source, string fileName, Language language, bool keepOrder)
    {
        string normalized = Normalize(source: source);
        ParsedFile input = ParseFile(source: normalized, fileName: fileName, language: language, what: "The file");

        var printer = new AstPrinter(index: input.Index, keepOrder: keepOrder);
        string output = printer.Print(program: input.Program);

        ParsedFile reparsed = ParseFile(source: output, fileName: fileName, language: language,
            what: "The formatted text (a formatter bug)");

        string? difference = AstComparer.Compare(expected: input.Program,
            actual: reparsed.Program,
            expectedTopLevelOrder: printer.TopLevelOrder);
        if (difference != null)
        {
            throw new FormatRefusedException(
                reason: $"the formatted text parses to a different tree ({difference}), so it is not written");
        }

        string? lostComment = CompareComments(original: input.Index.Comments, formatted: reparsed.Index.Comments);
        if (lostComment != null)
        {
            throw new FormatRefusedException(reason: lostComment);
        }

        return output;
    }

    private static int FirstDifferentLine(string a, string b)
    {
        string[] left = a.Split(separator: '\n');
        string[] right = b.Split(separator: '\n');
        int i = 0;
        while (i < left.Length && i < right.Length && left[i] == right[i])
        {
            i++;
        }

        return i + 1;
    }

    /// <summary>The source as the lexer reads it: no byte order mark, LF line ends.</summary>
    internal static string Normalize(string source)
    {
        if (source.Length > 0 && source[index: 0] == '\uFEFF')
        {
            source = source[1..];
        }

        return source.Replace(oldValue: "\r\n", newValue: "\n")
                     .Replace(oldChar: '\r', newChar: '\n');
    }

    private sealed record ParsedFile(SyntaxTree.Program Program, SourceIndex Index);

    private static ParsedFile ParseFile(string source, string fileName, Language language, string what)
    {
        List<Token> tokens;
        List<CommentTrivia> comments;
        try
        {
            (tokens, comments) = Languages.For(language: language)
                                          .TokenizeWithComments(source: source, fileName: fileName);
        }
        catch (GrammarException e)
        {
            throw new FormatRefusedException(reason: $"{what} does not lex: {e.Message}");
        }

        var parser = new Parser.Parser(tokens: tokens, language: language, fileName: fileName);
        SyntaxTree.Program program;
        try
        {
            program = parser.Parse();
        }
        catch (GrammarException e)
        {
            throw new FormatRefusedException(reason: $"{what} does not parse: {e.Message}");
        }

        if (parser.HasErrors)
        {
            throw new FormatRefusedException(reason: $"{what} does not parse: {parser.GetErrors()[index: 0]}");
        }

        return new ParsedFile(Program: program, Index: new SourceIndex(source: source, tokens: tokens,
            comments: comments));
    }

    /// <summary>Checks that every comment survived, by its normalized text. Returns the problem, or null.</summary>
    private static string? CompareComments(List<CommentTrivia> original, List<CommentTrivia> formatted)
    {
        List<string> before = original.Select(selector: c => CommentText.Normalize(text: c.Text))
                                      .Order(comparer: StringComparer.Ordinal)
                                      .ToList();
        List<string> after = formatted.Select(selector: c => CommentText.Normalize(text: c.Text))
                                      .Order(comparer: StringComparer.Ordinal)
                                      .ToList();
        if (before.SequenceEqual(second: after))
        {
            return null;
        }

        string? missing = before.Except(second: after)
                                .FirstOrDefault();
        return missing != null
            ? $"a comment would be lost or changed ({missing})"
            : "the comments of the formatted text differ from the file's";
    }
}

/// <summary>The canonical spelling of a comment's text.</summary>
internal static class CommentText
{
    /// <summary>
    /// <c>#comment</c> becomes <c># comment</c> and <c>###doc</c> becomes <c>### doc</c>. Text that already starts
    /// with a space keeps its own spacing (doc comments indent lists and code that way), and banner runs of
    /// <c>#</c> stay as they are. Trailing spaces are dropped.
    /// </summary>
    public static string Normalize(string text)
    {
        text = text.TrimEnd();
        int hashes = 0;
        while (hashes < text.Length && text[index: hashes] == '#')
        {
            hashes++;
        }

        // A doc comment is exactly three hashes. One or two hashes is a regular comment.
        int marker = hashes >= 3
            ? 3
            : hashes;
        string rest = text[marker..];
        if (rest.Length == 0 || rest[index: 0] == ' ' || rest[index: 0] == '#' || rest[index: 0] == '!')
        {
            return text;
        }

        return text[..marker] + " " + rest;
    }
}
