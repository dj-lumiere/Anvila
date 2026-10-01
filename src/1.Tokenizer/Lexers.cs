using TypeModel.Enums;

namespace Builder.Tokenizer;

/// <summary>
/// The lexer of each language, registered by its front end (the RazorForge and Suflae projects) when its
/// command line or test host starts. The builder core tokenizes every source file through here, so it
/// never depends on a particular language's lexer.
/// </summary>
public static class Lexers
{
    private static readonly Dictionary<Language, Func<string, string, List<Token>>> Registered = new();

    /// <summary>Registers <paramref name="tokenize"/> (source text, file name → tokens) as the lexer of
    /// <paramref name="language"/>.</summary>
    public static void Register(Language language, Func<string, string, List<Token>> tokenize)
    {
        lock (Registered)
        {
            Registered[key: language] = tokenize;
        }
    }

    /// <summary>The tokens of <paramref name="source"/> (read from <paramref name="fileName"/>) in
    /// <paramref name="language"/>.</summary>
    public static List<Token> Tokenize(string source, string fileName, Language language)
    {
        Func<string, string, List<Token>>? tokenize;
        lock (Registered)
        {
            Registered.TryGetValue(key: language, value: out tokenize);
        }

        return tokenize?.Invoke(arg1: source, arg2: fileName) ??
               throw new InvalidOperationException(
                   message: $"No {language} lexer is registered: the {language} front end registers it at startup.");
    }
}
