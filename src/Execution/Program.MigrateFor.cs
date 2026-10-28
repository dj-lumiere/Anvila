using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>The word <c>each</c> written as a word of code. It is a reserved word, so in code it is always the
    /// loop keyword.</summary>
    private static readonly Regex EachKeyword = new(pattern: @"(?<![\w`$.])each(?![\w`])", options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step for</c>: the loop over a range or a collection is <c>for x in xs</c>, so every <c>each</c> loop
    /// (<c>each x in xs</c>, <c>each (i, x) in xs.enumerate()</c>, <c>each i in 0 til n by 2</c>) is rewritten to
    /// <c>for</c>. Strings and comments stay as they are.
    /// </summary>
    private static string RewriteForKeyword(string file, string source, Language language, int lineOffset)
    {
        return ReplaceInCode(text: source, pattern: EachKeyword, replacement: _ => "for");
    }
}
