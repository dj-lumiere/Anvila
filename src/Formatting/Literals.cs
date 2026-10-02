using System.Globalization;
using System.Text;

namespace Builder.Formatting;

/// <summary>
/// The canonical spelling of literals. The parser keeps a text literal's decoded value and a number's text as
/// written, so the formatter re-escapes text with the standard escapes and spells a hexadecimal prefix in lowercase
/// with uppercase digits.
/// </summary>
internal static class Literals
{
    /// <summary>
    /// <paramref name="value"/> as a quoted, escaped text literal (without a prefix). Byte strings take
    /// <c>\xHH</c> for the bytes that need an escape, text takes <c>\uXXXXXX</c>.
    /// </summary>
    public static string QuoteText(string value, bool bytes)
    {
        var text = new StringBuilder(value: "\"");
        AppendEscaped(text: text, value: value, bytes: bytes, formatted: false);
        return text.Append(value: '"')
                   .ToString();
    }

    /// <summary>Appends <paramref name="value"/> escaped for a quoted literal (a formatted one doubles braces).</summary>
    public static void AppendEscaped(StringBuilder text, string value, bool bytes, bool formatted)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[index: i];
            switch (c)
            {
                case '\\':
                    text.Append(value: "\\\\");
                    continue;
                case '"':
                    text.Append(value: "\\\"");
                    continue;
                case '\n':
                    text.Append(value: "\\n");
                    continue;
                case '\t':
                    text.Append(value: "\\t");
                    continue;
                case '\r':
                    text.Append(value: "\\r");
                    continue;
                case '\0':
                    text.Append(value: "\\0");
                    continue;
                case '{' when formatted:
                    text.Append(value: "{{");
                    continue;
                case '}' when formatted:
                    text.Append(value: "}}");
                    continue;
            }

            if (char.IsHighSurrogate(c: c) && i + 1 < value.Length && char.IsLowSurrogate(c: value[index: i + 1]))
            {
                text.Append(value: c)
                    .Append(value: value[index: i + 1]);
                i++;
                continue;
            }

            if (NeedsEscape(c: c, bytes: bytes))
            {
                if (bytes)
                {
                    if (c > 0xFF)
                    {
                        throw new FormatRefusedException(reason: $"a byte string holds the character U+{(int)c:X4}");
                    }

                    text.Append(value: $"\\x{(int)c:X2}");
                }
                else
                {
                    text.Append(value: $"\\u{(int)c:X6}");
                }

                continue;
            }

            text.Append(value: c);
        }
    }

    /// <summary>Whether a character cannot stand in a quoted literal as itself (the lexer rejects it in source).</summary>
    private static bool NeedsEscape(char c, bool bytes)
    {
        return c < 0x20 || c == 0x7F || bytes && c > 0x7F || char.IsSurrogate(c: c) ||
               char.IsWhiteSpace(c: c) && c != ' ' ||
               CharUnicodeInfo.GetUnicodeCategory(ch: c) is UnicodeCategory.Format or UnicodeCategory.Control;
    }

    /// <summary>
    /// The canonical spelling of a number as written: a hexadecimal literal gets a lowercase <c>0x</c> and
    /// uppercase digits (the exponent marker, separators and suffix stay as written). Other numbers are unchanged.
    /// </summary>
    public static string CanonicalNumber(string text)
    {
        string sign = text.StartsWith(value: '-')
            ? "-"
            : "";
        string body = text[sign.Length..];
        if (body.Length < 2 || body[index: 0] != '0' || body[index: 1] is not ('x' or 'X'))
        {
            return text;
        }

        var result = new StringBuilder(value: sign + "0x");
        int i = 2;
        // Mantissa digits (and a fraction when a binary exponent follows), uppercased.
        while (i < body.Length && (Uri.IsHexDigit(character: body[index: i]) || body[index: i] is '_' or '.'))
        {
            if (body[index: i] == '_' && IsSuffixAfterUnderscore(body: body, at: i))
            {
                break;
            }

            result.Append(value: char.ToUpperInvariant(c: body[index: i]));
            i++;
        }

        result.Append(value: body[i..]);
        return result.ToString();
    }

    /// <summary>Whether the underscore at <paramref name="at"/> introduces a type suffix (as the lexer reads it).</summary>
    private static bool IsSuffixAfterUnderscore(string body, int at)
    {
        int end = at + 1;
        while (end < body.Length && char.IsLetterOrDigit(c: body[index: end]))
        {
            end++;
        }

        return NumericSuffixes.Contains(item: body[(at + 1)..end]);
    }

    /// <summary>The numeric type suffixes the lexers know (an underscore before one starts the suffix).</summary>
    private static readonly HashSet<string> NumericSuffixes =
    [
        "s8", "s16", "s32", "s64", "s128", "s256", "u8", "u16", "u32", "u64", "u128", "u256", "addr", "b16", "b32",
        "b64", "b128", "d32", "d64", "d128", "i", "n", "dn"
    ];

    /// <summary>True when a type-argument name is a number written as a const-generic argument.</summary>
    public static bool LooksNumeric(string name)
    {
        return name.Length > 0 && (char.IsDigit(c: name[index: 0]) ||
                                   name.Length > 1 && name[index: 0] == '-' && char.IsDigit(c: name[index: 1]));
    }
}

/// <summary>The spelling of build-time splices.</summary>
internal static class Splices
{
    /// <summary>
    /// Whether splices are written in the braced form <c>${...}</c> everywhere. Off: the brace-less form
    /// (<c>$nameof(m)</c>, <c>$typeof(m)</c>) is printed wherever it exists, and the braced form only for an inner
    /// expression the brace-less form cannot hold.
    /// </summary>
    public const bool PreferBraced = false;

    /// <summary>The member-name splice of an expand handle in a member-variable template.</summary>
    public static string NameOf(string handle)
    {
        return PreferBraced
            ? "${" + handle + ".name}"
            : "$nameof(" + handle + ")";
    }

    /// <summary>The type splice of an expand handle.</summary>
    public static string TypeOf(string handle)
    {
        return PreferBraced
            ? "${" + handle + ".type}"
            : "$typeof(" + handle + ")";
    }
}
