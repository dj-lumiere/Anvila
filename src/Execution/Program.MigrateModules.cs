using System.Text;
using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>An import of a module that is gone: <c>Core</c> and what was under it (<c>Core/Memory</c>,
    /// <c>Core.Text</c>, <c>Core.[Text]</c>) is <c>System</c>, and <c>Numerics</c> (<c>Numerics.Int</c>) is
    /// <c>System/Numerics</c>. Every file sees both without an import.</summary>
    private static readonly Regex GoneModuleImport = new(
        pattern: @"^[ \t]*import[ \t]+(?:Core|Numerics)(?:[./][^\r\n#]*)?[ \t]*(?:#[^\r\n]*)?\r?$",
        options: RegexOptions.Compiled);

    /// <summary>The module name <c>Core</c> in a qualified name (<c>Core.S64</c>, <c>RF::Core.List</c>).</summary>
    private static readonly Regex CoreQualifier = new(pattern: @"(?<![\w.$])Core(?=\.[A-Za-z_\[]|::)|(?<=::)Core\b",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step modules</c>: the language's base module is <c>System</c>, and every file sees it and every module
    /// under it (<c>System/Numerics</c>, <c>System/Text</c>, ...) without an import. An import of <c>Core</c> or of
    /// <c>Numerics</c> goes away (with the blank line it leaves when it was the only one), and a name qualified by
    /// <c>Core</c> (<c>Core.S64</c>, <c>RF::Core.List</c>) is qualified by <c>System</c>. Text in strings stays.
    /// </summary>
    private static string RewriteModules(string file, string source, Language language, int lineOffset)
    {
        var lines = source.Split(separator: '\n').ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!GoneModuleImport.IsMatch(input: lines[index: i]))
            {
                continue;
            }

            lines.RemoveAt(index: i);
            bool blankBefore = i == 0 || lines[index: i - 1].Trim().Length == 0;
            if (blankBefore && i < lines.Count && lines[index: i].Trim().Length == 0)
            {
                lines.RemoveAt(index: i);
            }

            i--;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            lines[index: i] = OutsideStrings(line: lines[index: i],
                rewrite: part => CoreQualifier.Replace(input: part, replacement: "System"));
        }

        return string.Join(separator: '\n', values: lines);
    }

    /// <summary>A line with <paramref name="rewrite"/> applied to everything but its string literals.</summary>
    private static string OutsideStrings(string line, Func<string, string> rewrite)
    {
        var result = new StringBuilder();
        int from = 0;
        int i = 0;
        while (i < line.Length)
        {
            if (line[index: i] == '#')
            {
                break;
            }

            if (line[index: i] != '"')
            {
                i++;
                continue;
            }

            result.Append(value: rewrite(arg: line[from..i]));
            int end = i + 1;
            while (end < line.Length && line[index: end] != '"')
            {
                end += line[index: end] == '\\' ? 2 : 1;
            }

            end = Math.Min(val1: end + 1, val2: line.Length);
            result.Append(value: line[i..end]);
            from = i = end;
        }

        result.Append(value: rewrite(arg: line[from..]));
        return result.ToString();
    }
}
