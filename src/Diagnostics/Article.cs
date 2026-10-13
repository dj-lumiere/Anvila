namespace Builder.Diagnostics;

/// <summary>
/// The indefinite article a message puts before a name it fills in: "an 'Account'", "a 'List[S64]'", "an 'S64'",
/// "an 8-bit". It goes by how the name is said, so a name read letter by letter ("S64", "LLVM") or as a number ("8",
/// "11") takes the article its first sound needs, not its first letter.
/// </summary>
public static class Article
{
    /// <summary>"a" or "an" for <paramref name="name"/>.</summary>
    public static string For(string? name)
    {
        string word = (name ?? "").TrimStart('\'', '`', '"', '(', '[', '@', '-');
        if (word.Length == 0)
        {
            return "a";
        }

        if (char.IsAsciiDigit(c: word[0]))
        {
            return StartsWithVowelSoundNumber(digits: word) ? "an" : "a";
        }

        // A name read letter by letter: an initial capital followed by a digit or another capital ("S64", "LLVM",
        // "UTF8"). Letters whose name starts with a vowel sound take "an".
        bool spelledOut = word.Length > 1 && char.IsAsciiLetterUpper(c: word[0]) &&
                          (char.IsAsciiDigit(c: word[1]) || char.IsAsciiLetterUpper(c: word[1]));
        if (spelledOut)
        {
            return "AEFHILMNORSX".Contains(value: word[0]) ? "an" : "a";
        }

        char first = char.ToLowerInvariant(c: word[0]);
        if (first is 'a' or 'e' or 'i' or 'o')
        {
            return "an";
        }

        // A word starting with u takes "an" unless it is said with a leading "you" ("Unit", "User", "Usual").
        if (first == 'u')
        {
            string lower = word.ToLowerInvariant();
            return lower.StartsWith(value: "uni") || lower.StartsWith(value: "use") || lower.StartsWith(value: "usu") ||
                   lower.StartsWith(value: "uti") || lower.StartsWith(value: "uri")
                ? "a"
                : "an";
        }

        return "a";
    }

    /// <summary>"A" or "An" for <paramref name="name"/>, starting a sentence.</summary>
    public static string Capital(string? name)
    {
        return For(name: name) == "an" ? "An" : "A";
    }

    /// <summary>Whether a number is said with a vowel first: 8, 11, 18, 80 to 89, 800 and so on.</summary>
    private static bool StartsWithVowelSoundNumber(string digits)
    {
        int end = 0;
        while (end < digits.Length && char.IsAsciiDigit(c: digits[end]))
        {
            end++;
        }

        string number = digits[..end];
        return number[0] == '8' || number is "11" or "18";
    }
}
