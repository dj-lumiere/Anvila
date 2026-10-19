using System.Text;
using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>
    /// The routine names <c>--step names</c> rewrites, old to new: the math routines take the names the three
    /// languages share (<c>expm1</c> is <c>exp_m1</c>, the natural <c>log</c> is <c>ln</c>, <c>tgamma</c> is
    /// <c>gamma</c>), and the operator routines are <c>compare</c> for <c>&lt;=&gt;</c>, <c>shift_left</c> for
    /// <c>&lt;&lt;</c>, <c>arith_shift_right</c> for <c>&gt;&gt;</c> and <c>logical_shift_right</c> for
    /// <c>&gt;&gt;&gt;</c>. The float routines named after C take the shared names too: <c>fma</c> is
    /// <c>mul_add</c>, <c>fmod</c> is <c>mod_trunc</c>, <c>remainder</c> is <c>rem</c>, <c>fmax</c> / <c>fmin</c> are
    /// <c>max</c> / <c>min</c>, <c>frac</c> is <c>fract</c>, <c>total_order</c> is <c>total_compare</c> and
    /// <c>signbit</c> is <c>is_sign_negative</c>. Each math name's <c>_unchecked</c> form follows it.
    /// </summary>
    private static readonly Dictionary<string, string> RenamedRoutines = BuildRenamedRoutines();

    private static Dictionary<string, string> BuildRenamedRoutines()
    {
        var math = new Dictionary<string, string>(comparer: StringComparer.Ordinal)
        {
            [key: "expm1"] = "exp_m1", [key: "exp2m1"] = "exp2_m1", [key: "exp10m1"] = "exp10_m1",
            [key: "log1p"] = "ln_1p", [key: "log2p1"] = "log2_1p", [key: "log10p1"] = "log10_1p",
            [key: "sinpi"] = "sin_pi", [key: "cospi"] = "cos_pi", [key: "tanpi"] = "tan_pi",
            [key: "asinpi"] = "asin_pi", [key: "acospi"] = "acos_pi", [key: "atanpi"] = "atan_pi",
            [key: "atan2pi"] = "atan2_pi", [key: "rsqrt"] = "recip_sqrt", [key: "sincos"] = "sin_cos",
            [key: "lgamma"] = "ln_gamma", [key: "tgamma"] = "gamma",
            [key: "fma"] = "mul_add", [key: "fmod"] = "mod_trunc", [key: "remainder"] = "rem",
            [key: "fmax"] = "max", [key: "fmin"] = "min", [key: "frac"] = "fract",
            [key: "total_order"] = "total_compare", [key: "signbit"] = "is_sign_negative"
        };
        var all = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        foreach ((string old, string now) in math)
        {
            all[key: old] = now;
            all[key: old + "_unchecked"] = now + "_unchecked";
        }

        all[key: "log_unchecked"] = "ln_unchecked";
        all[key: "cmp"] = "compare";
        all[key: "ashl"] = "shift_left";
        all[key: "shl"] = "shift_left";
        all[key: "ashr"] = "arith_shift_right";
        all[key: "shr"] = "arith_shift_right";
        all[key: "lshr"] = "logical_shift_right";
        all[key: "iashl"] = "ishift_left";
        all[key: "iashr"] = "iarith_shift_right";
        all[key: "ilshr"] = "ilogical_shift_right";
        return all;
    }

    /// <summary>A routine name after a dot (a call, a declaration <c>routine T.name</c>, a protocol's
    /// <c>Me.name</c>), never one after <c>::</c> (a C or LLVM name). The natural <c>log</c> is renamed only where
    /// it takes no argument (<c>x.log()</c>), so a <c>log(message)</c> of your own keeps its name.</summary>
    private static readonly Regex RenamedMember = new(
        pattern: @"(?<=\.)(?<name>[a-z][a-z0-9_]*)(?![\w])(?<call>\s*\(\s*\))?",
        options: RegexOptions.Compiled);

    /// <summary>An old name in a comment: written as code there (<c>`expm1`</c>, <c>.cmp(</c>) or as a word that is
    /// only ever the routine. The natural <c>log</c>, the Integer-only <c>shl</c> / <c>shr</c> and the float names
    /// that are also English words or C names (<c>remainder</c>, <c>frac</c>, <c>fma</c>, ...) are renamed in a
    /// comment only between backticks or after a dot, since the plain words have other meanings.</summary>
    private static readonly Regex RenamedCommentWord = new(
        pattern: @"(?<![\w$])(?<name>[a-z][a-z0-9_]*)(?![\w])", options: RegexOptions.Compiled);

    /// <summary><c>--step names</c>: the math and operator routines are renamed where they are called, declared
    /// and quoted in comments, and in the <c>@rf("Type.name")</c> bindings of a Suflae library.</summary>
    /// <summary>Old names a comment keeps as plain words: English words and C function names.</summary>
    private static readonly HashSet<string> PlainWordKept = new(collection:
    [
        "fma", "fma_unchecked", "fmod", "remainder", "fmax", "fmin", "frac", "total_order", "signbit"
    ], comparer: StringComparer.Ordinal);

    private static string RewriteRoutineNames(string file, string source, Language language, int lineOffset)
    {
        string[] lines = source.Split(separator: '\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = RewriteNamesInLine(line: lines[i]);
        }

        return string.Join(separator: '\n', value: lines);
    }

    private static string RewriteNamesInLine(string line)
    {
        var result = new StringBuilder();
        int from = 0;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '#')
            {
                result.Append(value: RenameCode(code: line[from..i]));
                result.Append(value: RenameComment(comment: line[i..]));
                return result.ToString();
            }

            if (c == '"')
            {
                result.Append(value: RenameCode(code: line[from..i]));
                bool formatted = i > 0 && (line[i - 1] == 'f' || line[i - 1] == 'F');
                int end = StringEnd(line: line, start: i, formatted: formatted);
                string literal = line[i..end];
                bool binding = Regex.IsMatch(input: line[..i], pattern: @"@rf\(\s*$");
                result.Append(value: binding ? RenameBinding(literal: literal)
                    : formatted ? RenameHoles(literal: literal) : literal);
                from = end;
                i = end;
                continue;
            }

            if (c == '\'' && i + 2 < line.Length && (i == 0 || !char.IsLetterOrDigit(c: line[i - 1])))
            {
                // A character literal: 'a', '\n'.
                int end = line[i + 1] == '\\' ? i + 4 : i + 3;
                if (end <= line.Length && line[end - 1] == '\'')
                {
                    result.Append(value: RenameCode(code: line[from..i]));
                    result.Append(value: line[i..end]);
                    from = end;
                    i = end;
                    continue;
                }
            }

            i++;
        }

        result.Append(value: RenameCode(code: line[from..]));
        return result.ToString();
    }

    /// <summary>Where the string literal that opens at <paramref name="start"/> ends, past its closing quote. An
    /// f-text's holes are code, so a string inside one (<c>{"9".to[BF16]()}</c>) doesn't end it.</summary>
    private static int StringEnd(string line, int start, bool formatted)
    {
        int depth = 0;
        int j = start + 1;
        while (j < line.Length)
        {
            char c = line[j];
            if (c == '\\')
            {
                j += 2;
                continue;
            }

            if (formatted && c == '{' && depth == 0 && j + 1 < line.Length && line[j + 1] == '{')
            {
                j += 2;
                continue;
            }

            if (formatted && c == '{')
            {
                depth++;
            }
            else if (formatted && c == '}' && depth > 0)
            {
                depth--;
            }
            else if (c == '"' && depth == 0)
            {
                break;
            }
            else if (c == '"')
            {
                j = StringEnd(line: line, start: j, formatted: line[j - 1] is 'f' or 'F');
                continue;
            }

            j++;
        }

        return Math.Min(val1: j + 1, val2: line.Length);
    }

    /// <summary>The code of an f-text's <c>{...}</c> holes renamed, its text left alone.</summary>
    private static string RenameHoles(string literal)
    {
        var result = new StringBuilder();
        int depth = 0;
        int holeStart = 0;
        for (int i = 0; i < literal.Length; i++)
        {
            char c = literal[i];
            if (c == '{' && depth == 0 && i + 1 < literal.Length && literal[i + 1] == '{')
            {
                result.Append(value: "{{");
                i++;
                continue;
            }

            if (c == '{')
            {
                if (depth == 0)
                {
                    result.Append(value: c);
                    holeStart = i + 1;
                }

                depth++;
                continue;
            }

            if (c == '}' && depth > 0)
            {
                depth--;
                if (depth == 0)
                {
                    result.Append(value: RenameCode(code: literal[holeStart..i]));
                    result.Append(value: c);
                }

                continue;
            }

            if (depth == 0)
            {
                result.Append(value: c);
            }
        }

        return result.ToString();
    }

    /// <summary>A Suflae binding's <c>"Type.name"</c>, the name renamed.</summary>
    private static string RenameBinding(string literal)
    {
        Match m = Regex.Match(input: literal, pattern: @"^""(?<owner>[^""]*\.)(?<name>[a-z][a-z0-9_]*)""$");
        if (!m.Success)
        {
            return literal;
        }

        string name = m.Groups[groupname: "name"].Value;
        string? now = name == "log" ? "ln" : RenamedRoutines.GetValueOrDefault(key: name);
        return now == null ? literal : $"\"{m.Groups[groupname: "owner"].Value}{now}\"";
    }

    private static string RenameCode(string code)
    {
        return RenamedMember.Replace(input: code, evaluator: m =>
        {
            string name = m.Groups[groupname: "name"].Value;
            string call = m.Groups[groupname: "call"].Value;
            if (name == "log")
            {
                return call.Length > 0 ? "ln" + call : m.Value;
            }

            return RenamedRoutines.TryGetValue(key: name, value: out string? now) ? now + call : m.Value;
        });
    }

    private static string RenameComment(string comment)
    {
        return RenamedCommentWord.Replace(input: comment, evaluator: m =>
        {
            string name = m.Groups[groupname: "name"].Value;
            int at = m.Index;
            bool quoted = at > 0 && comment[at - 1] == '`' && at + name.Length < comment.Length &&
                          comment[at + name.Length] is '`' or '(';
            bool member = at > 0 && comment[at - 1] == '.' && (at < 2 || comment[at - 2] != ':');
            bool afterColons = at > 1 && comment[at - 1] == ':' && comment[at - 2] == ':';
            int end = at + name.Length;
            bool inPath = at > 0 && comment[at - 1] == '/' || end < comment.Length &&
                (comment[end] == '/' || comment[end] == '.' && end + 1 < comment.Length && char.IsLetter(c: comment[end + 1]));
            if (afterColons || inPath)
            {
                return m.Value;
            }

            if (name == "log")
            {
                bool emptyCall = comment.Length >= at + 5 && comment.Substring(startIndex: at + 3, length: 2) == "()";
                return quoted || member && emptyCall ? "ln" : m.Value;
            }

            if (!RenamedRoutines.TryGetValue(key: name, value: out string? now))
            {
                return m.Value;
            }

            bool plainWordIsAmbiguous = name is "shl" or "shr" || PlainWordKept.Contains(item: name);
            return !plainWordIsAmbiguous || quoted || member ? now : m.Value;
        });
    }
}
