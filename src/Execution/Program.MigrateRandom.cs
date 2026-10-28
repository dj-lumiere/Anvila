using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>A draw named after its type: <c>.next_u64(</c>, <c>.next_u32(</c> or <c>.next_b64(</c>.</summary>
    private static readonly Regex TypedDraw = new(pattern: @"\.next_(?<type>u64|u32|b64)(?=\s*\()",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step random</c>: a random draw names its type as a type argument, so <c>rng.next_u64()</c> is
    /// <c>rng.next[U64]()</c>, <c>next_u32()</c> is <c>next[U32]()</c>, <c>next_b64()</c> is <c>next[B64]()</c>, and
    /// <c>Entropy.next_u64()</c> is <c>Entropy.next[U64]()</c>. A draw in an f-text's <c>{...}</c> hole is code and is
    /// rewritten, the rest of a string and comments stay as they are.
    /// </summary>
    private static string RewriteRandom(string file, string source, Language language, int lineOffset)
    {
        return TypedDraw.Replace(input: source,
            evaluator: m => IsCodeOrHole(text: source, at: m.Index)
                ? $".next[{m.Groups[groupname: "type"].Value.ToUpperInvariant()}]"
                : m.Value);
    }

    /// <summary>Whether <paramref name="at"/> is in code: outside a comment, and outside a string or inside an
    /// f-text's <c>{...}</c> hole (strings nested in a hole count as strings).</summary>
    private static bool IsCodeOrHole(string text, int at)
    {
        int start = text.LastIndexOf(value: '\n', startIndex: Math.Max(val1: at - 1, val2: 0)) + 1;
        // Each open string: whether it is an f-text, and how deep its holes are opened.
        var strings = new Stack<(bool Formatted, int Depth)>();
        for (int i = start; i < at; i++)
        {
            char c = text[index: i];
            bool inText = strings.Count > 0 && strings.Peek().Depth == 0;
            if (inText)
            {
                (bool formatted, _) = strings.Peek();
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    strings.Pop();
                }
                else if (formatted && c == '{' && i + 1 < text.Length && text[index: i + 1] == '{')
                {
                    i++;
                }
                else if (formatted && c == '{')
                {
                    strings.Push(item: (strings.Pop().Formatted, 1));
                }

                continue;
            }

            if (c == '#' && strings.Count == 0)
            {
                return false;
            }

            if (c == '"')
            {
                strings.Push(item: (i > 0 && text[index: i - 1] is 'f' or 'F', 0));
            }
            else if (strings.Count > 0 && c == '{')
            {
                (bool formatted, int depth) = strings.Pop();
                strings.Push(item: (formatted, depth + 1));
            }
            else if (strings.Count > 0 && c == '}')
            {
                (bool formatted, int depth) = strings.Pop();
                strings.Push(item: (formatted, depth - 1));
            }
        }

        return strings.Count == 0 || strings.Peek().Depth > 0;
    }
}
