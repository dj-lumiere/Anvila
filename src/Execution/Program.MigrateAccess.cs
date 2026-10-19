using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>One access a <c>using</c> line opens: <c>x.view()</c>, <c>x.modify()</c>, or the <c>try_</c> forms, with
    /// the name after <c>as</c>.</summary>
    private static readonly Regex UsingAccessPart = new(
        pattern: @"^(?<target>[A-Za-z_][\w.]*(?:\[[^\]]*\])?(?:\.[A-Za-z_]\w*)*)\.(?<verb>try_view|try_modify|view|modify)\(\)\s+as\s+(?<name>[A-Za-z_]\w*)$",
        options: RegexOptions.Compiled);

    /// <summary><c>using multimodify(first: a, second: b) as (x, y)</c>.</summary>
    private static readonly Regex UsingMultimodify = new(
        pattern: @"^multimodify\(\s*first:\s*(?<first>[^,()]+?)\s*,\s*second:\s*(?<second>[^,()]+?)\s*\)\s+as\s+\(\s*(?<a>[A-Za-z_]\w*)\s*,\s*(?<b>[A-Za-z_]\w*)\s*\)$",
        options: RegexOptions.Compiled);

    /// <summary>The names the access types and the re-entry crash had before they were named for access.</summary>
    private static readonly Regex OldAccessName = new(pattern: @"(?<![\w.`])(LockPolicy|ReentrantLockError)(?![\w`])",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step access</c>: access to an entity is opened with a <c>viewing</c> / <c>modifying</c> block, not with
    /// <c>using</c>. <c>using x.view() as v</c> becomes <c>viewing x as v</c> (and <c>modifying</c> for
    /// <c>modify()</c>), several of them on one line become one block (<c>viewing a as x, modifying b as y</c>), a
    /// <c>using x.try_modify() as m</c> with its <c>fallback</c> becomes <c>try modifying x as m</c> with
    /// <c>else</c>, and <c>using multimodify(first: a, second: b) as (x, y)</c> becomes
    /// <c>modifying a as x, b as y</c> (listed for a look: a second argument that was a read token is written
    /// <c>viewing</c>). A name the block would give its own target (<c>viewing log as log</c>) loses its <c>as</c>.
    /// <c>LockPolicy</c> is <c>AccessPolicy</c> and <c>ReentrantLockError</c> is <c>ReenterDuringAccessError</c>.
    /// Strings and comments stay as they are.
    /// </summary>
    private static string RewriteAccess(string file, string source, Language language, int lineOffset)
    {
        if (language != Language.RazorForge)
        {
            return source;
        }

        string[] lines = source.Split(separator: '\n');
        // The indent of each `using ... try_*()` rewritten into `try ...`, whose `fallback` at that indent is `else`.
        var tryIndents = new Stack<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimEnd(trimChars: '\r');
            string carriage = line[trimmed.Length..];
            string indent = trimmed[..^trimmed.TrimStart().Length];
            string body = trimmed.TrimStart();

            if (body == "fallback" && tryIndents.Count > 0 && tryIndents.Peek() == indent)
            {
                tryIndents.Pop();
                lines[i] = indent + "else" + carriage;
                continue;
            }

            while (tryIndents.Count > 0 && indent.Length < tryIndents.Peek().Length && body.Length > 0)
            {
                tryIndents.Pop();
            }

            if (body.StartsWith(value: "using ", comparisonType: StringComparison.Ordinal) &&
                AccessBlockFor(resources: body["using ".Length..], line: lineOffset + i + 1, file: file) is
                    { } block)
            {
                if (block.StartsWith(value: "try ", comparisonType: StringComparison.Ordinal))
                {
                    tryIndents.Push(item: indent);
                }

                lines[i] = indent + block + carriage;
                continue;
            }

            lines[i] = OutsideStrings(line: line,
                rewrite: part => OldAccessName.Replace(input: part, evaluator: m => m.Value == "LockPolicy"
                    ? "AccessPolicy"
                    : "ReenterDuringAccessError"));
        }

        return string.Join(separator: '\n', values: lines);
    }

    /// <summary>The <c>viewing</c> / <c>modifying</c> head that takes the place of a <c>using</c> head whose every
    /// resource is an access (<paramref name="resources"/> is what follows <c>using </c>), or null when one isn't.</summary>
    private static string? AccessBlockFor(string resources, int line, string file)
    {
        string text = resources;
        int comment = text.IndexOf(value: " #", comparisonType: StringComparison.Ordinal);
        string trailing = "";
        if (comment >= 0)
        {
            trailing = text[comment..];
            text = text[..comment];
        }

        text = text.Trim();
        if (UsingMultimodify.Match(input: text) is { Success: true } multi)
        {
            Console.WriteLine(value: $"{file}:{line}: 'multimodify' is a 'modifying' block now: check whether " +
                                     $"'{multi.Groups["second"].Value.Trim()}' is only read, and open it with " +
                                     "'viewing' then");
            return $"modifying {Named(target: multi.Groups["first"].Value.Trim(), name: multi.Groups["a"].Value)}, " +
                   $"{Named(target: multi.Groups["second"].Value.Trim(), name: multi.Groups["b"].Value)}" +
                   trailing;
        }

        var parts = new List<(string Verb, string Target, string Name, bool Try)>();
        foreach (string part in SplitTopLevel(text: text))
        {
            if (UsingAccessPart.Match(input: part.Trim()) is not { Success: true } access)
            {
                return null;
            }

            string verb = access.Groups["verb"].Value;
            parts.Add(item: (verb.EndsWith(value: "view", comparisonType: StringComparison.Ordinal) ? "viewing" : "modifying",
                access.Groups["target"].Value, access.Groups["name"].Value,
                verb.StartsWith(value: "try_", comparisonType: StringComparison.Ordinal)));
        }

        if (parts.Count == 0 || parts.Any(predicate: p => p.Try) && parts.Count > 1)
        {
            return null;
        }

        var head = new List<string>();
        string? previous = null;
        foreach ((string verb, string target, string name, bool _) in parts)
        {
            string shown = Named(target: target, name: name);
            head.Add(item: previous == verb ? shown : $"{verb} {shown}");
            previous = verb;
        }

        return (parts[index: 0].Try ? "try " : "") + string.Join(separator: ", ", values: head) + trailing;
    }

    /// <summary><c>target as name</c>, or the target alone when the block would give it that name anyway.</summary>
    private static string Named(string target, string name)
    {
        return target == name
            ? target
            : $"{target} as {name}";
    }

    /// <summary>The comma-separated parts of a head, not splitting inside brackets or parentheses.</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0;
        int from = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[index: i])
            {
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(item: text[from..i]);
                    from = i + 1;
                    break;
            }
        }

        parts.Add(item: text[from..]);
        return parts;
    }
}
