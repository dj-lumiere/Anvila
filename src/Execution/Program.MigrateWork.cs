using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>The type names a piece of handed-off work had before it was <c>Work</c>: <c>Agent</c> (in
    /// <c>to[Agent]</c> and <c>Agent[T]</c>) and <c>AgentStartError</c>.</summary>
    private static readonly Regex OldWorkName = new(pattern: @"(?<![\w.`$])Agent(?<error>StartError)?(?![\w`])",
        options: RegexOptions.Compiled);

    /// <summary>A Suflae start verb other than <c>start_now</c>: <c>.start_here()</c> or <c>.start_apart()</c>.</summary>
    private static readonly Regex OtherStartVerb = new(pattern: @"\.start_(?:here|apart)(?=\s*\(\s*\))",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step work</c>: a piece of handed-off work is a <c>Work</c>, so <c>Agent</c> becomes <c>Work</c> and
    /// <c>AgentStartError</c> becomes <c>WorkStartError</c> in code (never inside a string or a comment). A Suflae
    /// Work starts one way, so its <c>start_here()</c> and <c>start_apart()</c> become <c>start_now()</c>.
    /// </summary>
    private static string RewriteWork(string file, string source, Language language, int lineOffset)
    {
        string text = ReplaceInCode(text: source, pattern: OldWorkName,
            replacement: m => m.Groups[groupname: "error"].Success ? "WorkStartError" : "Work");
        return language == Language.Suflae
            ? ReplaceInCode(text: text, pattern: OtherStartVerb, replacement: _ => ".start_now")
            : text;
    }
}
