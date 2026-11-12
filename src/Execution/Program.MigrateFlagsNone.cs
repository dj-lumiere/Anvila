using System.Text;
using System.Text.RegularExpressions;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>A <c>none</c> written as a word: the files without one are not analyzed.</summary>
    private static readonly Regex NoneWord = new(pattern: @"(?<![\w.`])none(?![\w`])", options: RegexOptions.Compiled);

    /// <summary>The word <c>empty</c> outside text: a file that names a value so is rewritten by hand.</summary>
    private static readonly Regex EmptyWord = new(pattern: @"(?<![\w.`""])empty(?![\w`""])", options: RegexOptions.Compiled);

    /// <summary>
    /// <c>migrate --step flags-none</c>: <c>none</c> is the None value only, and the flags value with no member on is
    /// written <c>empty</c>. Analysis finds each <c>none</c> written where a flags value goes, and the step writes
    /// <c>empty</c> in its place. With <c>--check</c> nothing is written and every one is listed.
    /// </summary>
    private static int RunFlagsNoneStep(List<string> paths, bool check)
    {
        List<string> files = SuffixStepFiles(paths: paths)
                            .Where(predicate: f => NoneWord.IsMatch(input: File.ReadAllText(path: f)))
                            .ToList();
        if (files.Count == 0)
        {
            Console.WriteLine(value: "0 file(s) changed");
            return 0;
        }

        Dictionary<(string File, int Position), HashSet<string>> observed;
        try
        {
            observed = ProbeAnalysisTypes(files: files.Select(selector: f => (f, StandardRootOf(file: f))).ToList(),
                mode: "--flags-none-probe");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(value: $"error: migrate: analysis did not run, nothing was changed: {e.Message}");
            return 1;
        }

        int changedFiles = 0;
        int rewritten = 0;
        int byHand = 0;
        foreach (string file in files)
        {
            string source = File.ReadAllText(path: file);
            var text = new StringBuilder(value: source);
            // A value the file names `empty` would take the keyword's place where it is visible, so such a file is
            // listed for a rewrite by hand.
            if (EmptyWord.IsMatch(input: source) && observed.Keys.Any(predicate: o => o.File == file))
            {
                Console.WriteLine(value: $"{file}: rewrite by hand: it names something 'empty', which would take the place " +
                                         "of the keyword where it is visible");
                byHand++;
                continue;
            }

            // Back to front, so each rewrite leaves the offsets of the ones before it as they were.
            foreach (int position in observed.Keys.Where(predicate: o => o.File == file).Select(selector: o => o.Position)
                                             .OrderByDescending(keySelector: p => p))
            {
                if (position < 0 || position + 4 > source.Length || string.CompareOrdinal(strA: source, indexA: position,
                        strB: "none", indexB: 0, length: 4) != 0)
                {
                    continue;
                }

                int line = 1 + source.AsSpan(start: 0, length: position).Count(value: '\n');
                if (check)
                {
                    Console.WriteLine(value: $"{file}:{line}: 'none' where a flags value goes → empty");
                    rewritten++;
                    continue;
                }

                text.Remove(startIndex: position, length: 4).Insert(index: position, value: "empty");
                rewritten++;
            }

            string result = text.ToString();
            if (!check && result != source)
            {
                changedFiles++;
                File.WriteAllText(path: file, contents: result);
            }
        }

        if (check)
        {
            Console.WriteLine(value: $"{rewritten} 'none' written where a flags value goes");
            return rewritten > 0 ? 1 : 0;
        }

        Console.WriteLine(value: $"{changedFiles} file(s) changed: {rewritten} 'none' rewritten to 'empty', {byHand} file(s) " +
                                 "to rewrite by hand");
        return byHand > 0 ? 1 : 0;
    }
}
