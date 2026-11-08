using System.Text;
using System.Text.RegularExpressions;
using TypeModel.Enums;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>A reference type written by the builder's name for it: <c>Viewing[</c> or <c>Modifying[</c>, with the
    /// module qualifier it may carry.</summary>
    private static readonly Regex ReferenceTypeName = new(
        pattern: @"(?<![\w.])(?:System(?:/Memory)?\.)?(?<name>Viewing|Modifying)\[",
        options: RegexOptions.Compiled);

    /// <summary><c>.view()</c> / <c>.modify()</c> on a value.</summary>
    private static readonly Regex ReferenceVerbCall = new(pattern: @"\.(?<verb>view|modify)\(\)",
        options: RegexOptions.Compiled);

    /// <summary>The head of a <c>viewing</c> / <c>modifying</c> block (not a <c>try</c> one): its indent and its targets.</summary>
    private static readonly Regex AccessBlockHead = new(
        pattern: @"^(?<indent>[ \t]*)(?<verb>viewing|modifying)[ \t]+(?<targets>[^#]*?)[ \t]*(?<comment>#.*)?$",
        options: RegexOptions.Compiled);

    /// <summary>One target of a block head: an optional verb, the target, and an optional <c>as</c> name.</summary>
    private static readonly Regex AccessBlockTarget = new(
        pattern: @"^(?:(?<verb>viewing|modifying)[ \t]+)?(?<target>.+?)(?:[ \t]+as[ \t]+(?<name>[A-Za-z_]\w*))?$",
        options: RegexOptions.Compiled);

    /// <summary>A written <c>destruct</c> routine's header without <c>dangerous</c>.</summary>
    private static readonly Regex DestructHeader = new(
        pattern: @"^(?<indent>[ \t]*)(?<mods>(?:(?:open|secret|posted|internal|private|infallible|common)[ \t]+)*)routine[ \t]+(?<owner>\S.*?)\.destruct\(\)",
        options: RegexOptions.Compiled);

    /// <summary>A name declared with a type a block opens (<c>var g = Guarded(...)</c>, <c>ledger: Guarded[...]</c>,
    /// a <c>FileStorage</c>): its <c>viewing</c> / <c>modifying</c> blocks stay.</summary>
    private static readonly Regex OpenedTypeDeclaration = new(
        pattern: @"(?<![\w.])(?<name>[A-Za-z_]\w*)[ \t]*(?::|=)[^#\n]*?\b(?:Guarded|Witnessed|FileStorage|FileManager\.storage)\b",
        options: RegexOptions.Compiled);

    /// <summary>A type of the file's own that a block enters: it declares <c>view_enter</c> or <c>modify_enter</c>.</summary>
    private static readonly Regex EnterDeclaration = new(
        pattern: @"routine[ \t]+(?<name>[A-Za-z_]\w*)(?:\[[^\]]*\])?\.(?:view|modify)_enter\(",
        options: RegexOptions.Compiled);

    /// <summary>A name declared with a C string type, whose <c>view()</c> gives its pointer, not a reference.</summary>
    private static readonly Regex CStringDeclaration = new(
        pattern: @"(?<![\w.])(?<name>[A-Za-z_]\w*)[ \t]*(?::|=)[^#\n]*?\bC(?:W)?Str\b",
        options: RegexOptions.Compiled);

    /// <summary>A use of the removed <c>Stamped</c> / <c>PoolManager</c>.</summary>
    private static readonly Regex RemovedPoolName = new(pattern: @"\b(?:Stamped|PoolManager|StaleStampError)\b",
        options: RegexOptions.Compiled);

    /// <summary>
    /// <c>--step references</c>: references are written with their sign and reached without a verb.
    /// <list type="bullet">
    /// <item><c>Viewing[T]</c> is <c>%T</c> and <c>Modifying[T]</c> is <c>@T</c> (<c>Modifying[T]?</c> is
    /// <c>@T?</c>).</item>
    /// <item><c>.view()</c> / <c>.modify()</c> are dropped: an entity becomes a reference where one is expected. One on a
    /// name declared <c>Guarded</c> is listed (a Guarded is opened with a block now), and a <c>var v = x.view()</c> is
    /// listed for a look (<c>v</c> is a write reference now).</item>
    /// <item>A <c>viewing</c> / <c>modifying</c> block over entities or references becomes plain code: each <c>as</c>
    /// name is a local (<c>var m = a</c>) and the body moves out of the block. A block over a name declared
    /// <c>Guarded</c> (or a <c>FileStorage</c>, or a call) stays, and one that mixes both is listed.</item>
    /// <item>A written <c>destruct</c> is <c>dangerous</c>, and a <c>danger</c> block in it loses its head (the body
    /// is a danger context).</item>
    /// <item>A use of <c>Stamped</c> or <c>PoolManager</c> is listed to rewrite by hand with links.</item>
    /// </list>
    /// Strings and comments stay as they are.
    /// </summary>
    private static string RewriteReferences(string file, string source, Language language, int lineOffset)
    {
        if (language != Language.Revare)
        {
            return source;
        }

        string text = RewriteReferenceTypes(source: source);
        string[] lines = text.Split(separator: '\n');
        HashSet<string> opened = DeclaredNames(lines: lines, pattern: OpenedTypeDeclaration);
        // A type of the file's own with an enter / exit pair is opened with a block too.
        HashSet<string> withPair = DeclaredNames(lines: lines, pattern: EnterDeclaration);
        if (withPair.Count > 0)
        {
            opened.UnionWith(other: DeclaredNames(lines: lines,
                pattern: new Regex(pattern: $@"(?<![\w.])(?<name>[A-Za-z_]\w*)[ \t]*(?::|=)[^#\n]*?\b(?:{string.Join(separator: "|", values: withPair.Select(selector: Regex.Escape))})\b")));
        }

        HashSet<string> cStrings = DeclaredNames(lines: lines, pattern: CStringDeclaration);
        List<string> result = RewriteAccessBlocks(lines: lines, opened: opened, file: file, lineOffset: lineOffset);
        result = DropReferenceVerbs(lines: result, opened: opened, cStrings: cStrings, file: file, lineOffset: lineOffset);
        result = DangerousDestructs(lines: result);
        for (int i = 0; i < result.Count; i++)
        {
            foreach (Match use in RemovedPoolName.Matches(input: result[index: i]))
            {
                if (IsCode(text: result[index: i], at: use.Index))
                {
                    Console.WriteLine(value: $"{file}:{lineOffset + i + 1}: '{use.Value}' is gone: keep the entity where " +
                                             "it is held and point at it with a link (`parent: @Node?`), then rewrite this " +
                                             "by hand");
                    break;
                }
            }
        }

        return string.Join(separator: '\n', values: result);
    }

    /// <summary>The source with every reference type written by name rewritten with its sign, innermost first. A
    /// construction (<c>Viewing[T](...)</c>), a routine's receiver (<c>Viewing[T].access</c>) and a type's own declaration
    /// keep the name: those are the library's.</summary>
    private static string RewriteReferenceTypes(string source)
    {
        int from = 0;
        while (true)
        {
            Match match = ReferenceTypeName.Match(input: source, startat: from);
            if (!match.Success)
            {
                return source;
            }

            int open = match.Index + match.Length - 1;
            int close = ClosingBracket(text: source, open: open);
            string before = source[..match.Index].TrimEnd(' ', '\t');
            bool declares = before.EndsWith(value: "record", comparisonType: StringComparison.Ordinal) ||
                            before.EndsWith(value: "entity", comparisonType: StringComparison.Ordinal);
            if (close < 0 || !IsCode(text: source, at: match.Index) || declares ||
                close + 1 < source.Length && source[index: close + 1] is '(' or '.')
            {
                from = match.Index + match.Length;
                continue;
            }

            string inner = RewriteReferenceTypes(source: source[(open + 1)..close]);
            string sign = match.Groups[groupname: "name"].Value == "Viewing" ? "%" : "@";
            // The sign binds to the type right after it: a reference to an optional or to a variant written in place
            // keeps that type in parentheses (`%(Node?)`, not the optional reference `%Node?`).
            if (inner.TrimEnd().EndsWith(value: '?') || inner.Contains(value: " | "))
            {
                inner = $"({inner})";
            }

            source = source[..match.Index] + sign + inner + source[(close + 1)..];
            from = match.Index;
        }
    }

    /// <summary>The <c>]</c> that closes the <c>[</c> at <paramref name="open"/> on its line, or -1.</summary>
    private static int ClosingBracket(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length && text[index: i] != '\n'; i++)
        {
            if (text[index: i] == '[')
            {
                depth++;
            }
            else if (text[index: i] == ']' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The names <paramref name="pattern"/> finds declared in code on <paramref name="lines"/>.</summary>
    private static HashSet<string> DeclaredNames(string[] lines, Regex pattern)
    {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (string line in lines)
        {
            foreach (Match match in pattern.Matches(input: line))
            {
                if (IsCode(text: line, at: match.Index))
                {
                    names.Add(item: match.Groups[groupname: "name"].Value);
                }
            }
        }

        return names;
    }

    /// <summary>The leading spaces and tabs of <paramref name="line"/>.</summary>
    private static int IndentOf(string line)
    {
        return line.Length - line.TrimStart(' ', '\t').Length;
    }

    /// <summary>The lines with each block over entities or references turned into plain code (see
    /// <see cref="RewriteReferences"/>). A name the block gave a target written as a name or a member path is that path
    /// in the body (<c>modifying a as m</c> then <c>m.id</c> reads <c>a.id</c>), and one given to any other target is a
    /// local made from it.</summary>
    private static List<string> RewriteAccessBlocks(string[] lines, HashSet<string> opened, string file, int lineOffset)
    {
        var result = new List<string>(capacity: lines.Length);
        // The indent of each block whose head went away (its body moves left by 4), and the names it gave.
        var unwrapped = new Stack<(int Indent, Dictionary<string, string> Renames)>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimEnd(trimChars: '\r');
            string carriage = line[trimmed.Length..];
            while (unwrapped.Count > 0 && trimmed.Trim().Length > 0 && IndentOf(line: trimmed) <= unwrapped.Peek().Indent)
            {
                unwrapped.Pop();
            }

            int shift = unwrapped.Count * 4;
            string shifted = shift > 0 && IndentOf(line: trimmed) >= shift ? trimmed[shift..] : trimmed;
            foreach ((int _, Dictionary<string, string> renames) in unwrapped)
            {
                shifted = RenameNames(line: shifted, renames: renames);
            }

            Match head = AccessBlockHead.Match(input: shifted);
            if (!head.Success || i + 1 >= lines.Length || IndentOf(line: lines[i + 1]) <= IndentOf(line: trimmed) ||
                SplitTargets(text: head.Groups[groupname: "targets"].Value) is not { } targets)
            {
                result.Add(item: shifted + carriage);
                continue;
            }

            string indent = head.Groups[groupname: "indent"].Value;
            var locals = new List<string>();
            var renamed = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
            int kept = 0;
            foreach (string part in targets)
            {
                Match target = AccessBlockTarget.Match(input: part.Trim());
                string expr = target.Groups[groupname: "target"].Value;
                string root = Regex.Match(input: expr, pattern: @"^[A-Za-z_]\w*").Value;
                string last = Regex.Match(input: expr, pattern: @"[A-Za-z_]\w*$").Value;
                if (expr.Contains(value: '(') || opened.Contains(item: root) || opened.Contains(item: last))
                {
                    kept++;
                    continue;
                }

                if (!target.Groups[groupname: "name"].Success || target.Groups[groupname: "name"].Value == expr)
                {
                    continue;
                }

                string name = target.Groups[groupname: "name"].Value;
                if (Regex.IsMatch(input: expr, pattern: @"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*$"))
                {
                    renamed[key: name] = expr;
                }
                else
                {
                    locals.Add(item: $"{indent}var {name} = {expr}");
                }
            }

            if (kept == targets.Count)
            {
                result.Add(item: shifted + carriage);
                continue;
            }

            if (kept > 0)
            {
                Console.WriteLine(value: $"{file}:{lineOffset + i + 1}: this block opens a Guarded and an entity or a " +
                                         "reference together: keep the block for the Guarded only, and use the others " +
                                         "directly (rewrite it by hand)");
                result.Add(item: shifted + carriage);
                continue;
            }

            if (head.Groups[groupname: "comment"].Success)
            {
                result.Add(item: indent + head.Groups[groupname: "comment"].Value + carriage);
            }

            result.AddRange(collection: locals.Select(selector: l => l + carriage));
            unwrapped.Push(item: (IndentOf(line: trimmed), renamed));
        }

        return result;
    }

    /// <summary><paramref name="line"/> with each name of <paramref name="renames"/> written as what it stands for, in
    /// code and in the holes of f-texts.</summary>
    private static string RenameNames(string line, Dictionary<string, string> renames)
    {
        foreach ((string name, string path) in renames)
        {
            line = Regex.Replace(input: line, pattern: $@"(?<![\w.]){Regex.Escape(str: name)}(?![\w])(?![ \t]*:(?!:))",
                evaluator: m => IsCodeOrHole(text: line, at: m.Index) ? path : m.Value);
        }

        return line;
    }

    /// <summary>The targets of a block head split at its top-level commas, or null when the head does not read as one
    /// (an unbalanced bracket).</summary>
    private static List<string>? SplitTargets(string text)
    {
        var parts = new List<string>();
        int depth = 0;
        var current = new StringBuilder();
        foreach (char c in text)
        {
            depth += c is '(' or '[' ? 1 : c is ')' or ']' ? -1 : 0;
            if (c == ',' && depth == 0)
            {
                parts.Add(item: current.ToString());
                current.Clear();
                continue;
            }

            current.Append(value: c);
        }

        parts.Add(item: current.ToString());
        return depth == 0 && parts.All(predicate: p => p.Trim().Length > 0) ? parts : null;
    }

    /// <summary>The lines with each <c>.view()</c> / <c>.modify()</c> dropped (see <see cref="RewriteReferences"/>).</summary>
    private static List<string> DropReferenceVerbs(List<string> lines, HashSet<string> opened, HashSet<string> cStrings,
        string file, int lineOffset)
    {
        var result = new List<string>(capacity: lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[index: i];
            // A routine that declares a verb of its own (`routine Retained[T].view()`) is the library's.
            if (Regex.IsMatch(input: line, pattern: @"^\s*(?:\w+\s+)*routine\s"))
            {
                result.Add(item: line);
                continue;
            }

            int from = 0;
            while (ReferenceVerbCall.Match(input: line, startat: from) is { Success: true } call)
            {
                string receiver = ReceiverBefore(line: line, end: call.Index);
                string root = Regex.Match(input: receiver, pattern: @"^[A-Za-z_]\w*").Value;
                string last = Regex.Match(input: receiver, pattern: @"[A-Za-z_]\w*$").Value;
                if (!IsCodeOrHole(text: line, at: call.Index) || receiver.Length == 0 || cStrings.Contains(item: root))
                {
                    from = call.Index + call.Length;
                    continue;
                }

                if (opened.Contains(item: root) || opened.Contains(item: last))
                {
                    string verb = call.Groups[groupname: "verb"].Value;
                    Console.WriteLine(value: $"{file}:{lineOffset + i + 1}: '{receiver}.{verb}()' opens a Guarded for one " +
                                             $"call: open it with a block (`{(verb == "view" ? "viewing" : "modifying")} " +
                                             $"{receiver} as {(verb == "view" ? "v" : "m")}`) and use the name inside it");
                    from = call.Index + call.Length;
                    continue;
                }

                if (call.Groups[groupname: "verb"].Value == "view" &&
                    Regex.Match(input: line, pattern: @"^\s*var\s+(?<name>[A-Za-z_]\w*)\s*=\s*") is { Success: true } local &&
                    local.Length + receiver.Length == call.Index && line[(call.Index + call.Length)..].Trim().Length == 0)
                {
                    Console.WriteLine(value: $"{file}:{lineOffset + i + 1}: '{local.Groups[groupname: "name"].Value}' is a " +
                                             $"write reference now: write `var {local.Groups[groupname: "name"].Value}: %T = " +
                                             $"{receiver}` (its type for T) if it should only read");
                }

                line = line[..call.Index] + line[(call.Index + call.Length)..];
                from = call.Index;
            }

            result.Add(item: line);
        }

        return result;
    }

    /// <summary>The receiver written right before <paramref name="end"/> on <paramref name="line"/>: names, member
    /// reads, subscripts and calls back to where the expression starts.</summary>
    private static string ReceiverBefore(string line, int end)
    {
        int i = end;
        while (i > 0)
        {
            char c = line[index: i - 1];
            if (c is ')' or ']')
            {
                char opening = c == ')' ? '(' : '[';
                int depth = 0;
                int j = i - 1;
                for (; j >= 0; j--)
                {
                    if (line[index: j] == c)
                    {
                        depth++;
                    }
                    else if (line[index: j] == opening && --depth == 0)
                    {
                        break;
                    }
                }

                if (j < 0)
                {
                    break;
                }

                i = j;
                continue;
            }

            if (char.IsLetterOrDigit(c: c) || c is '_' or '.')
            {
                i--;
                continue;
            }

            break;
        }

        return line[i..end];
    }

    /// <summary>The lines with each written <c>destruct</c> marked <c>dangerous</c>, and the head of each <c>danger</c>
    /// block in its body taken away (its body moves left).</summary>
    private static List<string> DangerousDestructs(List<string> lines)
    {
        var result = new List<string>(capacity: lines.Count);
        int i = 0;
        while (i < lines.Count)
        {
            string line = lines[index: i];
            Match header = DestructHeader.Match(input: line);
            if (!header.Success || !IsCode(text: line, at: header.Index))
            {
                result.Add(item: line);
                i++;
                continue;
            }

            int at = header.Groups[groupname: "mods"].Index + header.Groups[groupname: "mods"].Length;
            result.Add(item: line[..at] + "dangerous " + line[at..]);
            int headerIndent = IndentOf(line: line);
            i++;
            var dangerIndents = new Stack<int>();
            while (i < lines.Count && (lines[index: i].Trim().Length == 0 || IndentOf(line: lines[index: i]) > headerIndent))
            {
                string body = lines[index: i];
                if (body.Trim().Length > 0)
                {
                    while (dangerIndents.Count > 0 && IndentOf(line: body) <= dangerIndents.Peek())
                    {
                        dangerIndents.Pop();
                    }
                }

                int shift = dangerIndents.Count * 4;
                string shifted = shift > 0 && IndentOf(line: body) >= shift ? body[shift..] : body;
                if (shifted.Trim().TrimEnd(trimChars: '\r') == "danger")
                {
                    dangerIndents.Push(item: IndentOf(line: body));
                    i++;
                    continue;
                }

                result.Add(item: shifted);
                i++;
            }
        }

        return result;
    }
}
