using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>An import of the <c>Signals</c> module or of a name in it (<c>import Signals</c>,
    /// <c>import Signals.when_interrupted</c>).</summary>
    private static readonly Regex SignalsImport = new(
        pattern: @"^[ \t]*import[ \t]+Signals(?:[./][^\r\n#]*)?[ \t]*(?:#[^\r\n]*)?\r?$",
        options: RegexOptions.Compiled);

    /// <summary>A call of <c>when_interrupted</c> / <c>when_terminated</c> as a free routine or qualified by
    /// <c>Signals</c>, up to its opening parenthesis.</summary>
    private static readonly Regex CpuEventCall = new(
        pattern: @"(?<![\w.`])(?:Signals\.)?(?<name>when_interrupted|when_terminated)(?=\s*\()",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step cpu-events</c>: the <c>Signals</c> module is <c>CpuEventManager</c> in <c>System/Cpu</c>, which every
    /// file sees without an import. An import of <c>Signals</c> goes away (with the blank line it leaves when it was
    /// the only one), and a call <c>when_interrupted(h)</c> / <c>when_terminated(h)</c> (or <c>Signals.</c> before
    /// it) is <c>CpuEventManager.when_interrupted(h)</c>. A call that passes a <c>context:</c> is left as it is and
    /// listed: the handler takes nothing now, and the state it changes is a <c>global</c>. Strings and comments stay
    /// as they are.
    /// </summary>
    private static string RewriteCpuEvents(string file, string source, Language language, int lineOffset)
    {
        var lines = source.Split(separator: '\n').ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!SignalsImport.IsMatch(input: lines[index: i]))
            {
                continue;
            }

            lines.RemoveAt(index: i);
            lineOffset++;
            bool blankBefore = i == 0 || lines[index: i - 1].Trim().Length == 0;
            if (blankBefore && i < lines.Count && lines[index: i].Trim().Length == 0)
            {
                lines.RemoveAt(index: i);
                lineOffset++;
            }

            i--;
        }

        string text = string.Join(separator: '\n', values: lines);
        var result = new System.Text.StringBuilder();
        int from = 0;
        foreach (Match call in CpuEventCall.Matches(input: text))
        {
            if (!IsCode(text: text, at: call.Index))
            {
                continue;
            }

            int line = lineOffset + 1 + text.AsSpan(start: 0, length: call.Index).Count(value: '\n');
            if (PassesContext(text: text, open: text.IndexOf(value: '(', startIndex: call.Index)))
            {
                string global = language == Language.Suflae ? "a global" : "a global (an Atomic or a Guarded)";
                Console.WriteLine(value: $"{file}:{line}: '{call.Groups["name"].Value}' passes a context: the " +
                                         $"handler takes nothing now. Keep the state it changes in {global} and " +
                                         $"write 'CpuEventManager.{call.Groups["name"].Value}(handler)' by hand");
                continue;
            }

            result.Append(value: text, startIndex: from, count: call.Index - from);
            result.Append(value: "CpuEventManager.").Append(value: call.Groups["name"].Value);
            from = call.Index + call.Length;
        }

        result.Append(value: text, startIndex: from, count: text.Length - from);
        return result.ToString();
    }

    /// <summary>Whether <paramref name="at"/> is code: not inside a string literal or after a <c>#</c> on its
    /// line.</summary>
    private static bool IsCode(string text, int at)
    {
        int start = text.LastIndexOf(value: '\n', startIndex: Math.Max(val1: at - 1, val2: 0)) + 1;
        bool inString = false;
        for (int i = start; i < at; i++)
        {
            char c = text[index: i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '#')
            {
                return false;
            }

            if (c == '"')
            {
                inString = true;
            }
        }

        return !inString;
    }

    /// <summary>Whether the call whose argument list opens at <paramref name="open"/> names a <c>context</c>
    /// argument at its own level.</summary>
    private static bool PassesContext(string text, int open)
    {
        if (open < 0)
        {
            return false;
        }

        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[index: i];
            switch (c)
            {
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    if (depth == 0)
                    {
                        return false;
                    }

                    break;
                case 'c' when depth == 1 && !char.IsLetterOrDigit(c: text[index: i - 1]) && text[index: i - 1] != '_':
                    if (string.CompareOrdinal(strA: text, indexA: i, strB: "context", indexB: 0, length: 7) == 0)
                    {
                        int after = i + 7;
                        while (after < text.Length && text[index: after] is ' ' or '\t')
                        {
                            after++;
                        }

                        if (after < text.Length && text[index: after] == ':')
                        {
                            return true;
                        }
                    }

                    break;
            }
        }

        return false;
    }
}
