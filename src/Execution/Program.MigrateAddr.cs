using System.Text;
using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>The type names that became <c>Addr</c>: the address (<c>Address</c>) and the C pointer
    /// (<c>CPtr</c>).</summary>
    private static readonly Regex OldAddressType = new(pattern: @"(?<![\w.`$])(?:Address|CPtr)(?![\w`])",
        options: RegexOptions.Compiled);

    /// <summary>The C pointer's null, <c>cptr_none()</c> and the old handle spellings of it.</summary>
    private static readonly Regex OldNullAddress = new(
        pattern: @"(?<![\w.`$])(?:cptr_none|bigint_handle_none|bigdec_handle_none)\s*\(\s*\)",
        options: RegexOptions.Compiled);

    /// <summary>A name declared with an address type: <c>x: Address</c> (a variable, a parameter, a field).</summary>
    private static readonly Regex AddressTypedName = new(
        pattern: @"(?<![\w.`$])(?<name>[A-Za-z_]\w*)\s*:\s*(?:Address|CPtr|Addr)(?![\w`\[])",
        options: RegexOptions.Compiled);

    /// <summary>A variable that starts as an address: <c>var x = ....get_address()</c>, <c>= cptr_none()</c>,
    /// <c>= y.to[Address]()</c>.</summary>
    private static readonly Regex AddressInitializedName = new(
        pattern:
        @"(?<![\w.`$])(?:var|let|preset)\s+(?<name>[A-Za-z_]\w*)\s*=\s*[^\n#]*?(?:\.get_address\(\)|\.address\(\)|cptr_none\(\)|\.to\[(?:Address|CPtr)\]\(\))\s*(?:#[^\n]*)?$",
        options: RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>One side of a comparison simple enough to type by its spelling: a name or a member chain, a call
    /// without arguments at its end allowed (<c>me.ctrl</c>, <c>p.get_address()</c>), or a whole number.</summary>
    private const string ComparedOperand = @"(?:[A-Za-z_]\w*(?:\.[A-Za-z_]\w*(?:\[[\w, ]+\])?(?:\(\))?)*|\d[\w]*)";

    /// <summary>A comparison with <c>==</c> or <c>!=</c> between two simple operands.</summary>
    private static readonly Regex SimpleEquality = new(
        pattern: $@"(?<![\w.])(?<left>{ComparedOperand})\s*(?<op>==|!=)\s*(?<right>{ComparedOperand})(?![\w.(\[])",
        options: RegexOptions.Compiled);

    /// <summary><c>x.is_none()</c> on a name or member chain.</summary>
    private static readonly Regex IsNoneCall = new(
        pattern: @"(?<![\w.])(?<operand>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*(?:\(\))?)*)\.is_none\(\)",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step addr</c>: the address type <c>Address</c> and the C pointer <c>CPtr</c> are one type, <c>Addr</c>.
    /// Both names become <c>Addr</c> in code (never inside a string or a comment), and <c>cptr_none()</c> becomes
    /// <c>Addr.hollow()</c>. An address names a place, so two are compared with <c>===</c> / <c>!==</c> and an
    /// address against 0 with <c>is_hollow()</c>: where a side of an <c>==</c> / <c>!=</c> is known to be an address
    /// from this file (a name declared or started as one, a <c>get_address()</c> / <c>address()</c> call) the
    /// comparison is rewritten, and <c>x.is_none()</c> on such a name is <c>x.is_hollow()</c>. Comparisons and
    /// arithmetic it cannot type are listed: an address has no ordering and no <c>+%</c> / <c>-%</c>, it moves with
    /// <c>offset(bytes:)</c> and is measured through <c>to[U64]()</c>.
    /// </summary>
    private static string RewriteAddr(string file, string source, Language language, int lineOffset)
    {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (Match match in AddressTypedName.Matches(input: source))
        {
            if (IsCode(text: source, at: match.Index))
            {
                names.Add(item: match.Groups[groupname: "name"].Value);
            }
        }

        foreach (Match match in AddressInitializedName.Matches(input: source))
        {
            if (IsCode(text: source, at: match.Index))
            {
                names.Add(item: match.Groups[groupname: "name"].Value);
            }
        }

        string text = ReplaceInCode(text: source, pattern: OldNullAddress, replacement: _ => "Addr.hollow()");
        text = ReplaceInCode(text: text, pattern: OldAddressType, replacement: _ => "Addr");
        text = ReplaceInCode(text: text, pattern: IsNoneCall,
            replacement: m => IsAddressOperand(operand: m.Groups[groupname: "operand"].Value, names: names)
                ? $"{m.Groups[groupname: "operand"].Value}.is_hollow()"
                : m.Value);
        text = ReplaceInCode(text: text, pattern: SimpleEquality, replacement: m =>
        {
            string left = m.Groups[groupname: "left"].Value;
            string right = m.Groups[groupname: "right"].Value;
            bool equal = m.Groups[groupname: "op"].Value == "==";
            bool leftAddress = IsAddressOperand(operand: left, names: names);
            bool rightAddress = IsAddressOperand(operand: right, names: names);
            if (leftAddress && right == "0" || rightAddress && left == "0")
            {
                string place = leftAddress ? left : right;
                return equal ? $"{place}.is_hollow()" : $"{place}.is_not_hollow()";
            }

            return leftAddress || rightAddress
                ? $"{left} {(equal ? "===" : "!==")} {right}"
                : m.Value;
        });

        ListAddressLeftovers(file: file, text: text, names: names, lineOffset: lineOffset);
        return text;
    }

    /// <summary>Whether a comparison operand is an address by its spelling: a name this file declares or starts as
    /// one (alone or at the end of a member chain), or a call that gives one.</summary>
    private static bool IsAddressOperand(string operand, HashSet<string> names)
    {
        if (operand.EndsWith(value: ".get_address()", comparisonType: StringComparison.Ordinal) ||
            operand.EndsWith(value: ".address()", comparisonType: StringComparison.Ordinal) ||
            operand.EndsWith(value: ".to[Addr]()", comparisonType: StringComparison.Ordinal) ||
            operand is "Addr.hollow()")
        {
            return true;
        }

        string last = operand[(operand.LastIndexOf(value: '.') + 1)..];
        return !last.EndsWith(value: ')') && names.Contains(item: last);
    }

    /// <summary>Lists, as <c>file:line: ...</c>, each line of code that still compares or does arithmetic with a
    /// name known to be an address: what is left for a person to write.</summary>
    private static void ListAddressLeftovers(string file, string text, HashSet<string> names, int lineOffset)
    {
        if (names.Count == 0)
        {
            return;
        }

        var word = new Regex(pattern: $@"(?<![\w$`])(?:{string.Join(separator: "|", values: names.Select(selector: Regex.Escape))})(?![\w`])");
        string[] lines = text.Split(separator: '\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string code = CodePart(line: lines[i]);
            if (!word.IsMatch(input: code))
            {
                continue;
            }

            string? what = Regex.IsMatch(input: code, pattern: @"(?<![=!<>])(?:==|!=)(?!=)")
                ? "compares with '==' / '!=': if a side is an Addr, write '===' / '!==' (or 'is_hollow()' against 0)"
                : Regex.IsMatch(input: code, pattern: @"\+%|-%|\badd_wrap\b|\bsub_wrap\b|(?<![=!<>-])(?:<=?|>=?)(?![=>])")
                    ? "does arithmetic or ordering: if a side is an Addr, move it with 'offset(bytes:)' and measure " +
                      "it with 'to[U64]()'"
                    : null;
            if (what != null)
            {
                Console.WriteLine(value: $"{file}:{lineOffset + i + 1}: {what}");
            }
        }
    }

    /// <summary>The code of a line: what comes before a comment, with string literals blanked out.</summary>
    private static string CodePart(string line)
    {
        var code = new StringBuilder(capacity: line.Length);
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[index: i];
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

                code.Append(value: ' ');
                continue;
            }

            if (c == '#')
            {
                break;
            }

            inString = c == '"';
            code.Append(value: inString ? ' ' : c);
        }

        return code.ToString();
    }

    /// <summary>Replaces each match of <paramref name="pattern"/> that starts in code (not in a string or a
    /// comment).</summary>
    private static string ReplaceInCode(string text, Regex pattern, Func<Match, string> replacement)
    {
        return pattern.Replace(input: text,
            evaluator: m => IsCode(text: text, at: m.Index) ? replacement(arg: m) : m.Value);
    }
}
