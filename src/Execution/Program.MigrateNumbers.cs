using System.Text;
using System.Text.RegularExpressions;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>A call that may build a number from one value, <c>U8(</c>: the files without one are not analyzed.</summary>
    private static readonly Regex NumberTypeCall = new(
        pattern: @"(?<![\w.])(S8|S16|S32|S64|S128|S256|U8|U16|U32|U64|U128|U256|B16|BF16|B32|B64|B128|D32|D64|D128|Decimal)\(",
        options: RegexOptions.Compiled);

    /// <summary>A receiver that takes <c>.to[T]()</c> as written: a name with member reads, calls and subscripts.</summary>
    private static readonly Regex PlainReceiver = new(pattern: @"^[A-Za-z_]\w*(\.[A-Za-z_]\w*|\(\)|\[[^\[\]]*\])*$",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>migrate --step numbers</c>: a number is not built from another value with <c>T(x)</c> any more, because that
    /// read the value's bits (<c>U8(300)</c> was 44, <c>Decimal(2)</c> was not 2). Analysis finds each such call and
    /// the value's type. Between two binary integer or float types the call was the number's conversion, so it becomes
    /// <c>x.to[T]()</c>, which keeps the number and fails when it does not fit. A literal that starts a declaration
    /// without a type gives the type to the declaration (<c>var b = U8(200)</c> is <c>var b: U8 = 200</c>). Every other
    /// call (a decimal type, <c>BF16</c>, a literal inside an expression) is listed by file and line for a rewrite by
    /// hand. With <c>--check</c> nothing is written and every call is listed.
    /// </summary>
    private static int RunNumbersStep(List<string> paths, bool check)
    {
        List<string> files = SuffixStepFiles(paths: paths)
                            .Where(predicate: f => StandardRootOf(file: f) == null &&
                                                   NumberTypeCall.IsMatch(input: File.ReadAllText(path: f)))
                            .ToList();
        if (files.Count == 0)
        {
            Console.WriteLine(value: "0 file(s) changed");
            return 0;
        }

        Dictionary<(string File, int Position), HashSet<string>> observed;
        try
        {
            observed = ProbeAnalysisTypes(files: files.Select(selector: f => (f, (string?)null)).ToList(),
                mode: "--numbers-probe");
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
            // Back to front, so each rewrite leaves the offsets of the calls before it as they were.
            foreach ((int position, string seen) in observed
                                                    .Where(predicate: o => o.Key.File == file)
                                                    .Select(selector: o => (o.Key.Position, o.Value.First()))
                                                    .OrderByDescending(keySelector: o => o.Position))
            {
                int line = 1 + source.AsSpan(start: 0, length: Math.Min(val1: position, val2: source.Length))
                                     .Count(value: '\n');
                string[] parts = seen.Split(separator: '|');
                (string target, string from, bool literal) = (parts[0], parts[1], parts[2] == "1");
                (string? replacement, int start, int length, string why) =
                    DecideNumberCall(source: source, position: position, target: target, from: from, literal: literal);
                if (replacement == null || check)
                {
                    byHand += replacement == null ? 1 : 0;
                    Console.WriteLine(value: $"{file}:{line}: " +
                                             (replacement == null ? $"rewrite by hand: {why}" : $"'{target}(...)' → {replacement}"));
                    continue;
                }

                text.Remove(startIndex: start, length: length).Insert(index: start, value: replacement);
                rewritten++;
            }

            string result = text.ToString();
            if (result != source)
            {
                changedFiles++;
                File.WriteAllText(path: file, contents: result);
            }
        }

        if (check)
        {
            int total = observed.Count;
            Console.WriteLine(value: $"{total} number(s) built from bits with T(x)");
            return total > 0 ? 1 : 0;
        }

        Console.WriteLine(value: $"{changedFiles} file(s) changed: {rewritten} call(s) rewritten, {byHand} to rewrite " +
                                 "by hand");
        return byHand > 0 ? 1 : 0;
    }

    /// <summary>
    /// What one <c>T(x)</c> at <paramref name="position"/> becomes: the replacement and the span it replaces, or a
    /// null replacement and why it is left for a person.
    /// </summary>
    internal static (string? Replacement, int Start, int Length, string Why) DecideNumberCall(string source,
        int position, string target, string from, bool literal)
    {
        string head = target + "(";
        if (position < 0 || position + head.Length > source.Length ||
            string.CompareOrdinal(strA: source, indexA: position, strB: head, indexB: 0, length: head.Length) != 0)
        {
            return (null, 0, 0, $"'{target}(...)' is not where analysis saw it");
        }

        int open = position + target.Length;
        int close = ClosingParenthesis(text: source, open: open);
        if (close < 0)
        {
            return (null, 0, 0, $"'{target}(...)' has no closing parenthesis this step can find");
        }

        string argument = source[(open + 1)..close].Trim();
        if (literal)
        {
            // `var b = U8(200)` with nothing after it on the line: the declaration takes the type.
            int lineStart = source.LastIndexOf(value: '\n', startIndex: Math.Max(val1: position - 1, val2: 0)) + 1;
            int lineEnd = source.IndexOf(value: '\n', startIndex: close);
            lineEnd = lineEnd < 0 ? source.Length : lineEnd;
            Match declaration = Regex.Match(input: source[lineStart..position],
                pattern: @"^(?<lead>\s*(var|let|preset)\s+(?<name>[A-Za-z_]\w*))\s*=\s*$");
            string rest = source[(close + 1)..lineEnd];
            if (declaration.Success && (rest.Trim().Length == 0 || rest.TrimStart().StartsWith(value: '#')))
            {
                string lead = declaration.Groups["lead"].Value;
                return ($"{lead}: {target} = {argument}", lineStart, close + 1 - lineStart, "");
            }

            return (null, 0, 0, $"'{target}({argument})': write the number where {Builder.Diagnostics.Article.For(name: target)} " +
                                $"'{target}' is expected ('var n: {target} = {argument}'), and the builder checks that it fits");
        }

        if (NumberClass(type: from) is not ('i' or 'f') || NumberClass(type: target) is not ('i' or 'f'))
        {
            return (null, 0, 0, $"'{target}({argument})' read the bits of {Builder.Diagnostics.Article.For(name: from)} " +
                                $"'{from}' as {Builder.Diagnostics.Article.For(name: target)} '{target}': " +
                                $"'.to[{target}]()' gives the number instead, decide which one you meant");
        }

        string receiver = PlainReceiver.IsMatch(input: argument) ? argument : $"({argument})";
        return ($"{receiver}.to[{target}]()", position, close + 1 - position, "");
    }

    /// <summary>'i' for a binary integer type, 'f' for a binary float type, '-' for any other (a decimal type,
    /// <c>BF16</c>, a type that is no number).</summary>
    private static char NumberClass(string type)
    {
        return type switch
        {
            "S8" or "S16" or "S32" or "S64" or "S128" or "S256" or "U8" or "U16" or "U32" or "U64" or "U128" or "U256" => 'i',
            "B16" or "B32" or "B64" or "B128" => 'f',
            _ => '-'
        };
    }

    /// <summary>The offset of the parenthesis that closes the one at <paramref name="open"/>, skipping text and
    /// character literals, or -1.</summary>
    private static int ClosingParenthesis(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[index: i];
            if (c is '"' or '\'')
            {
                int end = text.IndexOf(value: c, startIndex: i + 1);
                if (end < 0)
                {
                    return -1;
                }

                i = end;
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
            else if (c == '\n')
            {
                return -1;
            }
        }

        return -1;
    }
}
