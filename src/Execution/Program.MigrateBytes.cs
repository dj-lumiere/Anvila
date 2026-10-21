using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>The conversion of a number to a byte or a character, <c>.to[Byte]()</c> / <c>.to[Char]()</c>.</summary>
    private static readonly Regex ByteCharConversion = new(pattern: @"\.to\[(?<target>Byte|Char)\]\(\)",
        options: RegexOptions.Compiled);

    /// <summary>A character's byte, <c>.byte_value()</c>.</summary>
    private static readonly Regex CharByteValue = new(pattern: @"\.byte_value\(\)", options: RegexOptions.Compiled);

    /// <summary>A digit's value compared with the -1 it used to give for no digit.</summary>
    private static readonly Regex DigitValueSentinel = new(pattern: @"digit_value\([^)]*\)\s*(==|!=|<|>=)\s*(-1|0\b)",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step bytes</c>: the number of a byte or a character is read with a named routine, not a conversion.
    /// <c>v.to[Byte]()</c> is <c>Byte(v)</c> (only a <c>U8</c> had it) and <c>c.byte_value()</c> is
    /// <c>c.codepoint().to[U8]()</c>. What depends on a type the source doesn't show is listed: <c>n.to[Char]()</c>
    /// is <c>Char.from_codepoint(n:)</c>, which takes a <c>U32</c> and fails on a value that is no character, and a
    /// <c>digit_value()</c> compared with -1 throws now. <c>b.to[U8]()</c> / <c>c.to[U32]()</c> are build errors
    /// that name <c>b.value()</c> / <c>c.codepoint()</c>. Strings and comments stay as they are.
    /// </summary>
    private static string RewriteBytes(string file, string source, Language language, int lineOffset)
    {
        var result = new System.Text.StringBuilder();
        int from = 0;
        foreach (Match conversion in ByteCharConversion.Matches(input: source))
        {
            if (!IsCode(text: source, at: conversion.Index))
            {
                continue;
            }

            int line = lineOffset + 1 + source.AsSpan(start: 0, length: conversion.Index).Count(value: '\n');
            int start = ReceiverStart(text: source, end: conversion.Index);
            if (start < from || start == conversion.Index)
            {
                Console.WriteLine(value: $"{file}:{line}: '.to[{conversion.Groups["target"].Value}]()' has a receiver " +
                                         "this step can't take apart: rewrite it by hand");
                continue;
            }

            string receiver = source[start..conversion.Index];
            if (conversion.Groups["target"].Value == "Char")
            {
                Console.WriteLine(value: $"{file}:{line}: '{receiver}.to[Char]()' is " +
                                         $"'Char.from_codepoint(n: {receiver})' now, which takes a U32 (convert a U8 " +
                                         "with '.to[U32]()') and fails on a surrogate or a value above 0x10FFFF: " +
                                         "rewrite it by hand");
                continue;
            }

            result.Append(value: source, startIndex: from, count: start - from);
            result.Append(value: "Byte(").Append(value: receiver).Append(value: ')');
            from = conversion.Index + conversion.Length;
        }

        result.Append(value: source, startIndex: from, count: source.Length - from);
        string text = ReplaceInCode(text: result.ToString(), pattern: CharByteValue,
            replacement: _ => ".codepoint().to[U8]()");
        foreach (Match sentinel in DigitValueSentinel.Matches(input: text))
        {
            if (IsCode(text: text, at: sentinel.Index))
            {
                int line = lineOffset + 1 + text.AsSpan(start: 0, length: sentinel.Index).Count(value: '\n');
                Console.WriteLine(value: $"{file}:{line}: 'digit_value()' throws for a character that is no digit " +
                                         "now, instead of giving -1: recover with 'try' and 'when', by hand");
            }
        }

        return text;
    }

    /// <summary>Where the receiver of a call that starts at <paramref name="end"/> begins: a name, a number or a
    /// character, followed by member reads, calls and subscripts (<c>xs[i].low()</c>), or one parenthesized group.
    /// Returns <paramref name="end"/> when there is none.</summary>
    private static int ReceiverStart(string text, int end)
    {
        int i = end;
        while (i > 0)
        {
            char c = text[index: i - 1];
            if (c is ')' or ']')
            {
                char open = c == ')' ? '(' : '[';
                int depth = 0;
                int j = i - 1;
                for (; j >= 0; j--)
                {
                    if (text[index: j] == c)
                    {
                        depth++;
                    }
                    else if (text[index: j] == open && --depth == 0)
                    {
                        break;
                    }
                }

                if (j < 0)
                {
                    return end;
                }

                i = j;
                continue;
            }

            if (char.IsLetterOrDigit(c: c) || c is '_' or '.' or '\'' or '`')
            {
                i--;
                continue;
            }

            break;
        }

        return i;
    }
}
