using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>The old <c>pierce</c> statement: <c>pierce</c> at the start of a line, or after a <c>when</c> arm's
    /// <c>=&gt;</c>, followed by the error's name. <c>pierce</c> is an ordinary name now, so a line that uses it as
    /// one (<c>pierce = 3</c>, <c>pierce.run()</c>, <c>pierce and ready</c>) is left alone: only a name right after
    /// it, which no expression allows, is the old statement.</summary>
    private static readonly Regex PierceStatement = new(
        pattern: @"(?m)(?<=^[ \t]*|=>[ \t]*)\bpierce(?=[ \t]+(?!(?:and|or|not|is|isnot|in|to|til|by|as|if|then|else|" +
                 @"has|lacks|follows|obeys)\b)[A-Za-z_])",
        options: RegexOptions.Compiled);

    /// <summary>A routine header line: its indentation, and <c>failable</c> when the routine is declared so.</summary>
    private static readonly Regex RoutineHeader = new(
        pattern: @"^(?<indent>[ \t]*)(?:(?:open|secret|posted|internal|private)[ \t]+)?(?<failable>failable[ \t]+)?" +
                 @"(?:(?:dangerous|common)[ \t]+)*routine\b",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step pierce</c>: a <c>throw</c> in a routine that is not failable crashes where it stands, which is what
    /// <c>pierce</c> did, so <c>pierce X</c> there is <c>throw X</c>. In a failable routine a <c>throw</c> goes to the
    /// caller instead, so such a <c>pierce</c> is left as it is and listed: move the check into a routine that is not
    /// failable, or let the caller receive it. Strings and comments stay as they are.
    /// </summary>
    private static string RewritePierce(string file, string source, Language language, int lineOffset)
    {
        string[] lines = source.Split(separator: '\n');
        return PierceStatement.Replace(input: source, evaluator: m =>
        {
            if (!IsCode(text: source, at: m.Index))
            {
                return m.Value;
            }

            int lineIndex = source[..m.Index].Count(predicate: c => c == '\n');
            if (!InFailableRoutine(lines: lines, lineIndex: lineIndex))
            {
                return "throw";
            }

            Console.WriteLine(value: $"{file}:{lineOffset + lineIndex + 1}: 'pierce' in a failable routine: a 'throw' " +
                                     "here goes to the caller, so rewrite it by hand (a throw in a routine that is " +
                                     "not failable crashes where it stands)");
            return m.Value;
        });
    }

    /// <summary>Whether line <paramref name="lineIndex"/> is in the body of a routine declared <c>failable</c>: the
    /// nearest routine header above it that is indented less.</summary>
    private static bool InFailableRoutine(string[] lines, int lineIndex)
    {
        int indent = lines[lineIndex].Length - lines[lineIndex].TrimStart(' ', '\t').Length;
        for (int i = lineIndex - 1; i >= 0; i--)
        {
            Match header = RoutineHeader.Match(input: lines[i]);
            if (header.Success && header.Groups[groupname: "indent"].Length < indent)
            {
                return header.Groups[groupname: "failable"].Success;
            }
        }

        return false;
    }
}
