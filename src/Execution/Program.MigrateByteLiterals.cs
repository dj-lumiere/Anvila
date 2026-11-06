using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>A binding that starts from a byte string: <c>var header = b"GET"</c>.</summary>
    private static readonly Regex ByteStringBinding = new(
        pattern: @"(?<=\bvar[ \t]+)(?<name>\w+)(?<eq>[ \t]*=[ \t]*)b(?<raw>r?)(?<text>""(?:[^""\\\n]|\\.)*"")",
        options: RegexOptions.Compiled);

    /// <summary>A byte string anywhere else: <c>b"abc"</c>, <c>br"a\n"</c>.</summary>
    private static readonly Regex ByteString = new(
        pattern: @"(?<![\w`])b(?<raw>r?)(?<text>""(?:[^""\\\n]|\\.)*"")",
        options: RegexOptions.Compiled);

    /// <summary>A byte character in a place a <c>Byte</c> is declared: <c>var c: Byte = b'A'</c>.</summary>
    private static readonly Regex ByteCharacterBinding = new(
        pattern: @"(?<=:[ \t]*Byte[ \t]*=[ \t]*)b(?<text>'(?:[^'\\\n]|\\.)')",
        options: RegexOptions.Compiled);

    /// <summary>A byte character: <c>b'A'</c>, <c>b'\x0A'</c>.</summary>
    private static readonly Regex ByteCharacter = new(
        pattern: @"(?<![\w`])b(?<text>'(?:[^'\\\n]|\\.)')",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step byte-literals</c>: a literal has no <c>b</c> prefix, the place it goes decides whether it is bytes.
    /// <c>b'A'</c> is <c>Byte('A')</c> (just <c>'A'</c> after <c>: Byte =</c>), and <c>var h = b"GET"</c> is <c>var h: Bytes = "GET"</c>. Any other byte string
    /// loses its <c>b</c> and is listed: it is bytes where a <c>Bytes</c> goes (a parameter, a typed binding, a
    /// return), so a place that holds something else needs a hand edit. Strings and comments stay as they are.
    /// </summary>
    private static string RewriteByteLiterals(string file, string source, Language language, int lineOffset)
    {
        string text = ByteStringBinding.Replace(input: source,
            evaluator: m => IsCodeOrHole(text: source, at: m.Index)
                ? $"{m.Groups["name"].Value}: Bytes{m.Groups["eq"].Value}{m.Groups["raw"].Value}{m.Groups["text"].Value}"
                : m.Value);
        text = ByteCharacterBinding.Replace(input: text,
            evaluator: m => IsCodeOrHole(text: text, at: m.Index)
                ? m.Groups["text"].Value
                : m.Value);
        text = ByteCharacter.Replace(input: text,
            evaluator: m => IsCodeOrHole(text: text, at: m.Index)
                ? $"Byte({m.Groups["text"].Value})"
                : m.Value);
        return ByteString.Replace(input: text,
            evaluator: m =>
            {
                if (!IsCodeOrHole(text: text, at: m.Index))
                {
                    return m.Value;
                }

                int line = lineOffset + 1 + text.AsSpan(start: 0, length: m.Index).Count(value: '\n');
                Console.WriteLine(value: $"{file}:{line}: {m.Value} is {m.Groups["raw"].Value}{m.Groups["text"].Value} " +
                                         "now, bytes where a Bytes goes: check the place");
                return m.Groups["raw"].Value + m.Groups["text"].Value;
            });
    }
}
