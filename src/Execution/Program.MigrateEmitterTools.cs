using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>The old module name <c>IterTools</c> as a whole word, in an import or as a qualifier.</summary>
    private static readonly Regex OldIterToolsModule = new(pattern: @"(?<![\w`$])IterTools(?![\w`])",
        options: RegexOptions.Compiled);

    /// <summary><c>select_many</c> and <c>SelectManyEmittable</c> / <c>SelectManyEmitter</c>, as whole words.</summary>
    private static readonly Regex OldSelectMany = new(pattern: @"(?<![\w`$])(?:select_many|SelectManyEmittable|SelectManyEmitter)(?![\w`])",
        options: RegexOptions.Compiled);

    /// <summary>A call of the old two-emittables join, <c>.extend(</c> or <c>.extend[T](</c>, and the types
    /// <c>ExtendEmittable</c> / <c>ExtendEmitter</c>.</summary>
    private static readonly Regex OldExtend = new(pattern: @"(?<=\.)extend(?=[(\[])|(?<![\w`$])Extend(?=Emit(?:table|ter)(?![\w`]))",
        options: RegexOptions.Compiled);

    /// <summary>A routine the file declares under the name <c>extend</c>, which is the program's own.</summary>
    private static readonly Regex DeclaresExtend = new(pattern: @"\broutine\s+(?:[\w\[\], ]+\.)?extend\b",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step emitter-tools</c>: the module <c>IterTools</c> is <c>EmitterTools</c>, so <c>import IterTools</c> and a
    /// name qualified by it (<c>IterTools.WhereEmitter</c>) take the new name. <c>select_many</c> is
    /// <c>select_chained</c> (with <c>SelectManyEmittable</c> as <c>SelectChainedEmittable</c>), and joining two
    /// emittables is <c>a.chain(b)</c> in place of <c>a.extend(b)</c> (<c>ExtendEmittable</c> is <c>ChainEmittable</c>).
    /// All in code only (never inside a string or a comment).
    /// </summary>
    private static string RewriteEmitterTools(string file, string source, Language language, int lineOffset)
    {
        string text = ReplaceInCode(text: source, pattern: OldIterToolsModule, replacement: _ => "EmitterTools");
        text = ReplaceInCode(text: text, pattern: OldSelectMany,
            replacement: m => m.Value.Replace(oldValue: "SelectMany", newValue: "SelectChained")
                               .Replace(oldValue: "select_many", newValue: "select_chained"));
        // A program that declares its own `extend` keeps it. The type names still change.
        bool ownExtend = DeclaresExtend.IsMatch(input: text);
        return ReplaceInCode(text: text, pattern: OldExtend,
            replacement: m => m.Value == "extend" ? (ownExtend ? m.Value : "chain") : "Chain");
    }
}
