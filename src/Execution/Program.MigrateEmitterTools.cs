using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>The old module name <c>IterTools</c> as a whole word, in an import or as a qualifier.</summary>
    private static readonly Regex OldIterToolsModule = new(pattern: @"(?<![\w`$])IterTools(?![\w`])",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step emitter-tools</c>: the module <c>IterTools</c> is <c>EmitterTools</c>, so <c>import IterTools</c> and a
    /// name qualified by it (<c>IterTools.WhereEmitter</c>) take the new name, in code only (never inside a string or
    /// a comment).
    /// </summary>
    private static string RewriteEmitterTools(string file, string source, Language language, int lineOffset)
    {
        return ReplaceInCode(text: source, pattern: OldIterToolsModule, replacement: _ => "EmitterTools");
    }
}
