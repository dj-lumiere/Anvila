using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>A routine's header up to its parameter list: <c>routine make(</c>, <c>failable routine T.f(</c>.</summary>
    private static readonly Regex RoutineHeaderStart = new(pattern: @"^\s*(?:[a-z]+\s+)*routine\s+[^(\n]*\(",
        options: RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>A <c>return</c> at the start of a line.</summary>
    private static readonly Regex ReturnLine = new(pattern: @"^[ ]*return\b", options: RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary><c>steal name</c> handed over as it is (not <c>steal x.field</c>, <c>steal xs[i]</c> or a call).</summary>
    private static readonly Regex StealOfName = new(pattern: @"\bsteal\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b(?!\s*[.\[(])",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step return-steal</c> (RazorForge): a local handed over inside a <c>return</c> expression ends at that
    /// return, so it moves without <c>steal</c>: <c>return Board(rows: steal rows)</c> is <c>return Board(rows:
    /// rows)</c> and <c>return steal xs</c> is <c>return xs</c>. A parameter, <c>me</c>, and a <c>steal</c> outside a
    /// return keep the word. Strings and comments stay as they are.
    /// </summary>
    private static string RewriteReturnSteal(string file, string source, Language language, int lineOffset)
    {
        if (language != Language.RazorForge)
        {
            return source;
        }

        var removals = new List<(int Start, int Length)>();
        foreach (Match ret in ReturnLine.Matches(input: source))
        {
            int end = StatementEnd(text: source, from: ret.Index);
            HashSet<string> parameters = EnclosingParameters(text: source, at: ret.Index);
            foreach (Match steal in StealOfName.Matches(input: source[..end], startat: ret.Index))
            {
                string name = steal.Groups["name"].Value;
                if (name == "me" || parameters.Contains(item: name) || !IsCode(text: source, at: steal.Index))
                {
                    continue;
                }

                removals.Add(item: (steal.Index, steal.Groups["name"].Index - steal.Index));
            }
        }

        if (removals.Count == 0)
        {
            return source;
        }

        var result = new System.Text.StringBuilder(value: source);
        foreach ((int start, int length) in removals.OrderByDescending(keySelector: r => r.Start))
        {
            result.Remove(startIndex: start, length: length);
        }

        return result.ToString();
    }

    /// <summary>Where the statement that starts at <paramref name="from"/> ends: the first line end outside
    /// parentheses and brackets.</summary>
    private static int StatementEnd(string text, int from)
    {
        int depth = 0;
        bool inString = false;
        for (int i = from; i < text.Length; i++)
        {
            char c = text[index: i];
            if (c == '"' && (i == 0 || text[index: i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (!inString && c is '(' or '[')
            {
                depth++;
            }
            else if (!inString && c is ')' or ']')
            {
                depth--;
            }
            else if (!inString && c == '#' && depth >= 0)
            {
                int lineEnd = text.IndexOf(value: '\n', startIndex: i);
                if (lineEnd < 0)
                {
                    return text.Length;
                }

                i = lineEnd - 1;
            }
            else if (c == '\n' && depth <= 0)
            {
                return i;
            }
        }

        return text.Length;
    }

    /// <summary>The parameter names of the last routine header above <paramref name="at"/>.</summary>
    private static HashSet<string> EnclosingParameters(string text, int at)
    {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        Match? header = null;
        foreach (Match m in RoutineHeaderStart.Matches(input: text[..at]))
        {
            header = m;
        }

        if (header is null)
        {
            return names;
        }

        int depth = 1;
        int start = header.Index + header.Length;
        int i = start;
        for (; i < text.Length && depth > 0; i++)
        {
            if (text[index: i] is '(' or '[')
            {
                depth++;
            }
            else if (text[index: i] is ')' or ']')
            {
                depth--;
            }
        }

        foreach (Match parameter in Regex.Matches(input: text[start..Math.Max(val1: start, val2: i - 1)],
                     pattern: @"(?:^|[,(])\s*(?:\.\.\.)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\.\.\.)?\s*:"))
        {
            names.Add(item: parameter.Groups["name"].Value);
        }

        return names;
    }
}
