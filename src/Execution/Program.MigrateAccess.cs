using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>One access a <c>using</c> line opens: <c>x.view()</c>, <c>x.modify()</c>, or the <c>try_</c> forms, with
    /// the name after <c>as</c>.</summary>
    private static readonly Regex UsingAccessPart = new(
        pattern: @"^(?<target>[A-Za-z_][\w.]*(?:\[[^\]]*\])?(?:\.[A-Za-z_]\w*)*)\.(?<verb>try_view|try_modify|try_consult|try_amend|view|modify|consult|amend)\(\)\s+as\s+(?<name>[A-Za-z_]\w*)$",
        options: RegexOptions.Compiled);

    /// <summary><c>using multimodify(first: a, second: b) as (x, y)</c>.</summary>
    private static readonly Regex UsingMultimodify = new(
        pattern: @"^multimodify\(\s*first:\s*(?<first>[^,()]+?)\s*,\s*second:\s*(?<second>[^,()]+?)\s*\)\s+as\s+\(\s*(?<a>[A-Za-z_]\w*)\s*,\s*(?<b>[A-Za-z_]\w*)\s*\)$",
        options: RegexOptions.Compiled);

    /// <summary>The names the access types and the re-entry crash had before they were named for access, and the
    /// Guarded's old token types.</summary>
    private static readonly Regex OldAccessName = new(
        pattern: @"(?<![\w.`])(LockPolicy|ReentrantLockError|Consulting|Amending)(?![\w`])",
        options: RegexOptions.Compiled);

    /// <summary>A Guarded's old access calls, <c>g.consult()</c> / <c>g.amend()</c>: <c>g.view()</c> /
    /// <c>g.modify()</c> now.</summary>
    private static readonly Regex OldGuardedAccess = new(pattern: @"\.(?<verb>consult|amend)\(\)",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step access</c>: access to an entity is opened with a <c>viewing</c> / <c>modifying</c> block, not with
    /// <c>using</c>. <c>using x.view() as v</c> becomes <c>viewing x as v</c> (and <c>modifying</c> for
    /// <c>modify()</c>), several of them on one line become one block (<c>viewing a as x, modifying b as y</c>), a
    /// <c>using x.try_modify() as m</c> with its <c>fallback</c> (and a <c>try modifying x as m</c> with an
    /// <c>else</c>) becomes a <c>try modifying x as m_entered</c> block that branches with <c>when m_entered</c>
    /// (<c>is None =&gt;</c> the old <c>else</c>, <c>else as m =&gt;</c> the old body), and
    /// <c>using multimodify(first: a, second: b) as (x, y)</c> becomes <c>modifying a as x, b as y</c> (listed for a
    /// look: a second argument that was a read token is written <c>viewing</c>). A name the block would give its own
    /// target (<c>viewing log as log</c>) loses its <c>as</c>. A Guarded's old <c>consult()</c> / <c>amend()</c> are
    /// <c>view()</c> / <c>modify()</c> (<c>using g.consult() as c</c> is <c>viewing g as c</c>), <c>Consulting</c> /
    /// <c>Amending</c> are <c>Viewing</c> / <c>Modifying</c>, <c>LockPolicy</c> is <c>AccessPolicy</c> and
    /// <c>ReentrantLockError</c> is <c>ReenterDuringAccessError</c>. Strings and comments stay as they are.
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
                rewrite: part => OldGuardedAccess.Replace(
                    input: OldAccessName.Replace(input: part, evaluator: m => m.Value switch
                    {
                        "LockPolicy" => "AccessPolicy",
                        "ReentrantLockError" => "ReenterDuringAccessError",
                        "Consulting" => "Viewing",
                        _ => "Modifying"
                    }),
                    evaluator: m => m.Groups["verb"].Value == "consult" ? ".view()" : ".modify()"));
        }

        return string.Join(separator: '\n', values: TryBlocksWithoutElse(lines: lines, file: file, lineOffset: lineOffset));
    }

    /// <summary>The head of a <c>try viewing</c> / <c>try modifying</c> block with one target.</summary>
    private static readonly Regex TryAccessHead = new(
        pattern: @"^(?<indent>[ \t]*)try (?<verb>viewing|modifying) (?<target>[A-Za-z_][\w.]*(?:\[[^\]]*\])?(?:\.[A-Za-z_]\w*)*)(?: as (?<name>[A-Za-z_]\w*))?(?<trailing>\s*#.*)?$",
        options: RegexOptions.Compiled);

    /// <summary>
    /// The lines with each <c>try viewing</c> / <c>try modifying</c> block that still has an <c>else</c> written the way
    /// a <c>try</c> block is now: it has no <c>else</c>, its name is a <c>Maybe</c> of the token, and the body branches
    /// with <c>when</c>. <c>try modifying s as c</c> / body / <c>else</c> / other becomes <c>try modifying s as
    /// c_entered</c> with <c>when c_entered</c>, <c>is None =&gt;</c> other and <c>else as c =&gt;</c> body. A block with
    /// several targets is listed to rewrite by hand.
    /// </summary>
    private static string[] TryBlocksWithoutElse(string[] lines, string file, int lineOffset)
    {
        var result = new List<string>(capacity: lines.Length);
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i].TrimEnd(trimChars: '\r');
            string carriage = lines[i][line.Length..];
            bool isTry = line.TrimStart().StartsWith(value: "try viewing ", comparisonType: StringComparison.Ordinal) ||
                           line.TrimStart().StartsWith(value: "try modifying ", comparisonType: StringComparison.Ordinal);
            Match head = TryAccessHead.Match(input: line);
            if (!isTry)
            {
                result.Add(item: lines[i]);
                i++;
                continue;
            }

            string indent = line[..^line.TrimStart().Length];
            int bodyEnd = BlockEnd(lines: lines, from: i + 1, indent: indent);
            bool hasElse = bodyEnd < lines.Length && lines[bodyEnd].Trim() == "else" &&
                           lines[bodyEnd].TrimEnd(trimChars: '\r')[..^lines[bodyEnd].TrimEnd(trimChars: '\r').TrimStart().Length] == indent;
            if (!hasElse)
            {
                result.Add(item: lines[i]);
                i++;
                continue;
            }

            int elseEnd = BlockEnd(lines: lines, from: bodyEnd + 1, indent: indent);
            string? name = head.Success
                ? head.Groups["name"].Success ? head.Groups["name"].Value
                : Regex.IsMatch(input: head.Groups["target"].Value, pattern: @"^[A-Za-z_]\w*$") ? head.Groups["target"].Value : null
                : null;
            if (name == null)
            {
                Console.WriteLine(value: $"{file}:{lineOffset + i + 1}: a 'try' block has no 'else' now and its names are " +
                                         "Maybes of the tokens: branch with 'when' inside it, by hand");
                result.Add(item: lines[i]);
                i++;
                continue;
            }

            string maybe = $"{name}_entered";
            result.Add(item: $"{indent}try {head.Groups["verb"].Value} {head.Groups["target"].Value} as {maybe}" +
                             head.Groups["trailing"].Value + carriage);
            result.Add(item: $"{indent}    when {maybe}{carriage}");
            result.Add(item: $"{indent}        is None =>{carriage}");
            result.AddRange(collection: Deeper(lines: lines, from: bodyEnd + 1, to: elseEnd, by: "        "));
            result.Add(item: $"{indent}        else as {name} =>{carriage}");
            result.AddRange(collection: Deeper(lines: lines, from: i + 1, to: bodyEnd, by: "        "));
            i = elseEnd;
        }

        return result.ToArray();
    }

    /// <summary>The index of the first line from <paramref name="from"/> that is no longer inside a block whose head is
    /// at <paramref name="indent"/>: a line that isn't blank and is indented no deeper.</summary>
    private static int BlockEnd(string[] lines, int from, string indent)
    {
        int j = from;
        while (j < lines.Length)
        {
            string text = lines[j].TrimEnd(trimChars: '\r');
            if (text.Trim().Length > 0 && text.Length - text.TrimStart().Length <= indent.Length)
            {
                break;
            }

            j++;
        }

        // Blank lines at the end belong after the block.
        while (j > from && lines[j - 1].Trim().Length == 0)
        {
            j--;
        }

        return j;
    }

    /// <summary>The lines <paramref name="from"/> to <paramref name="to"/>, each that isn't blank indented by
    /// <paramref name="by"/> more.</summary>
    private static IEnumerable<string> Deeper(string[] lines, int from, int to, string by)
    {
        for (int j = from; j < to; j++)
        {
            yield return lines[j].Trim().Length == 0 ? lines[j] : by + lines[j];
        }
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
            parts.Add(item: (verb.EndsWith(value: "view", comparisonType: StringComparison.Ordinal) ||
                             verb.EndsWith(value: "consult", comparisonType: StringComparison.Ordinal)
                    ? "viewing"
                    : "modifying",
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
