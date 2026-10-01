using Builder.Frontends;
using TypeModel.Enums;

namespace Builder.Tokenizer;

/// <summary>Tokenizes source text with the lexer of a registered language (see <see cref="Languages"/>).</summary>
public static class Lexers
{
    /// <summary>The tokens of <paramref name="source"/> (read from <paramref name="fileName"/>) in
    /// <paramref name="language"/>.</summary>
    public static List<Token> Tokenize(string source, string fileName, Language language)
    {
        return Languages.For(language: language).Tokenize(source: source, fileName: fileName);
    }
}
