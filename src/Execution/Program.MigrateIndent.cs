using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>
    /// <c>--step indent</c>: a block is indented in steps of 4 spaces. A file indented in steps of 2 (the previous
    /// release's layout) gets the leading spaces of each line doubled, and a file indented in steps of 4 is left as
    /// it is. The step reads the text only (the lexer rejects the old layout), so it keeps track of strings itself:
    /// a line that starts inside a text literal spread over several lines is part of that text and keeps its spaces.
    /// </summary>
    private static string RewriteIndent(string file, string source, Language language, int lineOffset)
    {
        return ReindentToSteps(source: source, file: file, lineOffset: lineOffset) ?? source;
    }

    /// <summary>
    /// <paramref name="source"/> with its indent step made 4 spaces, or null when it already is one (or has no
    /// indented line). A file whose smallest indent is neither 2 nor a multiple of 4 is reported and left alone.
    /// </summary>
    private static string? ReindentToSteps(string source, string file, int lineOffset, bool report = true)
    {
        string[] lines = source.Split(separator: '\n');
        var inText = new bool[lines.Length];
        var inBrackets = new bool[lines.Length];
        var scan = new IndentScan();
        for (int i = 0; i < lines.Length; i++)
        {
            inText[i] = scan.InText;
            inBrackets[i] = scan.Depth > 0;
            scan.Line(line: lines[i]);
        }

        // The step is the smallest indent of a line that opens a statement: not inside a text, not inside brackets
        // (a broken argument list may be aligned anywhere), not blank and not a comment.
        int step = int.MaxValue;
        for (int i = 0; i < lines.Length; i++)
        {
            int spaces = LeadingSpaces(line: lines[i]);
            string rest = lines[i][spaces..].TrimEnd(trimChars: '\r');
            if (inText[i] || inBrackets[i] || spaces == 0 || rest.Length == 0 || rest[index: 0] is '#' or '\t')
            {
                continue;
            }

            step = Math.Min(val1: step, val2: spaces);
        }

        if (step == int.MaxValue || step % 4 == 0)
        {
            return null;
        }

        if (step != 2)
        {
            if (report)
            {
                Console.WriteLine(value: $"{file}:{lineOffset + 1}: indented in steps of {step} spaces: indent it in " +
                                         "steps of 4 by hand");
            }

            return null;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            int spaces = LeadingSpaces(line: lines[i]);
            if (!inText[i] && spaces > 0 && lines[i][spaces..].Trim().Length > 0)
            {
                lines[i] = new string(c: ' ', count: spaces) + lines[i];
            }
        }

        return string.Join(separator: '\n', values: lines);
    }

    /// <summary>The number of spaces a line starts with (a tab ends the count).</summary>
    private static int LeadingSpaces(string line)
    {
        int n = 0;
        while (n < line.Length && line[index: n] == ' ')
        {
            n++;
        }

        return n;
    }

    /// <summary>
    /// What <see cref="ReindentToSteps"/> needs to know at the start of each line: whether a text literal is still
    /// open (a text may hold line breaks) and how deep the open brackets are. A comment runs to the end of its line,
    /// a raw text (<c>r"..."</c>) has no escapes, and a character literal (<c>'"'</c>) is skipped whole.
    /// </summary>
    private sealed class IndentScan
    {
        /// <summary>Whether a text literal is open.</summary>
        public bool InText { get; private set; }

        /// <summary>How many brackets are open outside text literals.</summary>
        public int Depth { get; private set; }

        private bool _raw;

        /// <summary>Reads one line (without its line break).</summary>
        public void Line(string line)
        {
            int i = 0;
            while (i < line.Length)
            {
                char c = line[index: i];
                if (InText)
                {
                    if (c == '\\' && !_raw)
                    {
                        i += 2;
                        continue;
                    }

                    if (c == '"')
                    {
                        InText = false;
                    }

                    i++;
                    continue;
                }

                switch (c)
                {
                    case '#':
                        return;
                    case '"':
                        InText = true;
                        _raw = RawPrefix(line: line, quote: i);
                        break;
                    case '\'':
                        i = CharacterLiteralEnd(line: line, open: i);
                        break;
                    case '(' or '[' or '{':
                        Depth++;
                        break;
                    case ')' or ']' or '}':
                        Depth = Math.Max(val1: 0, val2: Depth - 1);
                        break;
                }

                i++;
            }
        }

        /// <summary>Whether the letters right before the quote at <paramref name="quote"/> mark a raw text
        /// (<c>r"</c>, <c>rf"</c>, <c>fr"</c>).</summary>
        private static bool RawPrefix(string line, int quote)
        {
            int j = quote - 1;
            while (j >= 0 && char.IsLetter(c: line[index: j]))
            {
                if (line[index: j] == 'r')
                {
                    return true;
                }

                j--;
            }

            return false;
        }

        /// <summary>The index of the quote that closes a character literal opened at <paramref name="open"/>, or
        /// <paramref name="open"/> when none closes it on the line.</summary>
        private static int CharacterLiteralEnd(string line, int open)
        {
            for (int j = open + 1; j < line.Length && j <= open + 12; j++)
            {
                if (line[index: j] == '\\')
                {
                    j++;
                    continue;
                }

                if (line[index: j] == '\'')
                {
                    return j;
                }
            }

            return open;
        }
    }
}
