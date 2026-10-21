using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>The old <c>pass</c> line: <c>pass</c> alone on its line, or after a <c>when</c> arm's <c>=&gt;</c>,
    /// with only a comment after it.</summary>
    private static readonly Regex PassLine = new(pattern: @"(?m)(?<=^[ \t]*|=>[ \t]*)pass(?=[ \t]*(?:#[^\n]*)?\r?$)",
        options: RegexOptions.Compiled);

    /// <summary>A name <c>nothing</c>: a binding, a parameter, a member variable or a routine of that name.</summary>
    private static readonly Regex NothingName = new(
        pattern: @"(?<=\bvar[ \t]+|\broutine[ \t]+|\.)nothing\b|(?<![\w.`$])nothing(?=[ \t]*(?::|=[^=>]|\())",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step nothing</c>: a line with no work is <c>nothing</c>, so every <c>pass</c> line (and <c>=&gt; pass</c>)
    /// is rewritten. <c>nothing</c> is a keyword now, so a name <c>nothing</c> is listed for a rename by hand. Strings and
    /// comments stay as they are.
    /// </summary>
    private static string RewriteNothing(string file, string source, Language language, int lineOffset)
    {
        foreach (Match name in NothingName.Matches(input: source))
        {
            if (IsCode(text: source, at: name.Index))
            {
                int line = lineOffset + source[..name.Index].Count(predicate: c => c == '\n') + 1;
                Console.WriteLine(value: $"{file}:{line}: 'nothing' is a keyword now: rename it");
            }
        }

        return ReplaceInCode(text: source, pattern: PassLine, replacement: _ => "nothing");
    }
}
