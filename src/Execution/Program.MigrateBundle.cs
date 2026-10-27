using System.Text;
using System.Text.RegularExpressions;
using Builder.Declaration;

namespace Builder.Execution;

internal partial class Program
{
    /// <summary>A <c>bundle</c> declaration header: its indent, name and type parameters.</summary>
    private static readonly Regex BundleHeader = new(pattern: @"^(?<indent>[ \t]*)bundle[ \t]+(?<name>[A-Za-z_]\w*)(\[(?<params>[^\]]*)\])?",
        options: RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>A declaration of a named type, with its kind: what a type argument is made of.</summary>
    private static readonly Regex KindDeclaration = new(
        pattern: @"^[ \t]*(?:secret[ \t]+|posted[ \t]+)?(?<kind>record|entity|choice|flags|variant|crashable|bundle)[ \t]+(?<name>[A-Za-z_]\w*)",
        options: RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The values every program has: a type argument named by one of them holds no entity.</summary>
    private static readonly HashSet<string> BuiltInValues =
    [
        "S8", "S16", "S32", "S64", "S128", "S256", "U8", "U16", "U32", "U64", "U128", "U256", "B16", "BF16", "B32",
        "B64", "B128", "D32", "D64", "D128", "Decimal", "Int", "Integer", "Bool", "Char", "Byte", "Text", "Bytes",
        "Address", "None", "Duration", "ByteSize"
    ];

    /// <summary>
    /// <c>migrate --step bundle</c>: there is no <c>bundle</c>, a named type is a <c>record</c> or an <c>entity</c>.
    /// A bundle that holds none of its type parameters by value becomes a <c>record</c>. Otherwise every use of it
    /// with type arguments (<c>Pair[S64]</c>) is read: when some argument held by value is an entity (declared
    /// <c>entity</c> in the files or the standard library) the bundle becomes an <c>entity</c>, and when every one is a
    /// value it becomes a <c>record</c> with <c>needs RecordType T</c> on those parameters. A bundle with no such use,
    /// or with an argument that is a type parameter or a type the step does not know, is listed for a decision by
    /// hand. With <c>--check</c> nothing is written.
    /// </summary>
    private static int RunBundleStep(List<string> paths, bool check)
    {
        List<string> files = SuffixStepFiles(paths: paths)
                            .Where(predicate: f => StandardRootOf(file: f) == null)
                            .ToList();
        var sources = files.ToDictionary(keySelector: f => f, elementSelector: File.ReadAllText);
        Dictionary<string, string> kinds = TypeKinds(texts: sources.Values);

        int changedFiles = 0;
        int rewritten = 0;
        int byHand = 0;
        foreach (string file in files)
        {
            string source = sources[key: file];
            List<Match> headers = BundleHeader.Matches(input: source).ToList();
            if (headers.Count == 0)
            {
                continue;
            }

            var text = new StringBuilder(value: source);
            // Back to front, so each rewrite leaves the offsets of the headers before it as they were.
            foreach (Match header in Enumerable.Reverse(source: headers))
            {
                int line = 1 + source.AsSpan(start: 0, length: header.Index).Count(value: '\n');
                string name = header.Groups[groupname: "name"].Value;
                List<string> parameters = SplitTypeArguments(text: header.Groups[groupname: "params"].Value);
                (string? kind, List<string> needs, string why) =
                    DecideBundle(source: source, header: header, parameters: parameters, kinds: kinds);
                if (kind == null)
                {
                    byHand++;
                    Console.WriteLine(value: $"{file}:{line}: decide 'bundle {name}' by hand: {why}");
                    continue;
                }

                string what = needs.Count > 0
                    ? $"record with {string.Join(separator: ", ", values: needs.Select(selector: p => $"needs RecordType {p}"))}"
                    : kind;
                Console.WriteLine(value: $"{file}:{line}: 'bundle {name}' → {what} ({why})");
                if (check)
                {
                    continue;
                }

                if (needs.Count > 0)
                {
                    int bodyAt = HeaderEnd(source: source, header: header);
                    string indent = header.Groups[groupname: "indent"].Value;
                    text.Insert(index: bodyAt,
                        value: string.Concat(values: needs.Select(selector: p => $"{indent}needs RecordType {p}\n")));
                }

                int keyword = header.Index + header.Groups[groupname: "indent"].Length;
                text.Remove(startIndex: keyword, length: "bundle".Length).Insert(index: keyword, value: kind);
                rewritten++;
            }

            string result = text.ToString();
            if (result == source)
            {
                continue;
            }

            changedFiles++;
            if (!check)
            {
                File.WriteAllText(path: file, contents: result);
            }
        }

        Console.WriteLine(value: $"{changedFiles} file(s) changed: {rewritten} bundle(s) rewritten, {byHand} to decide by hand");
        return byHand > 0 ? 1 : 0;
    }

    /// <summary>
    /// The kind a bundle becomes, the parameters that take <c>needs RecordType</c> when it becomes a record, and the
    /// reason, or a null kind and why it is left for a decision by hand.
    /// </summary>
    private static (string? Kind, List<string> Needs, string Why) DecideBundle(string source, Match header,
        List<string> parameters, Dictionary<string, string> kinds)
    {
        string name = header.Groups[groupname: "name"].Value;
        List<string> held = HeldByValue(source: source, header: header, parameters: parameters);
        if (held.Count == 0)
        {
            return ("record", [], "it holds no type parameter by value");
        }

        // Every written use with type arguments in the bundle's own file (another file's type of the same name is
        // another type).
        var use = new Regex(pattern: $@"(?<![\w.]){Regex.Escape(str: name)}\[");
        bool anyUse = false;
        foreach (string text in (string[])[source])
        {
            foreach (Match at in use.Matches(input: text))
            {
                if (IsInsideHeader(text: text, at: at.Index, name: name) ||
                    BracketContent(text: text, open: at.Index + at.Length - 1) is not { } content)
                {
                    continue;
                }

                List<string> arguments = SplitTypeArguments(text: content);
                // A routine header's own parameters (`Pair[EntityType A, U64 B].f`) are not a use.
                if (arguments.Count != parameters.Count || arguments.Any(predicate: a => a.Contains(value: ' ')))
                {
                    continue;
                }

                anyUse = true;
                foreach (string parameter in held)
                {
                    string argument = arguments[index: parameters.IndexOf(item: parameter)];
                    switch (ArgumentKind(argument: argument, kinds: kinds))
                    {
                        case "entity":
                            return ("entity", [], $"'{name}[{content}]' holds the entity '{argument}'");
                        case null:
                            return (null, [], $"'{name}[{content}]': the step can't tell whether '{argument}' is an entity");
                    }
                }
            }
        }

        return anyUse
            ? ("record", held, "every use holds values")
            : (null, [], $"no use of '{name}' with type arguments, so the step can't tell what it holds");
    }

    /// <summary>The type parameters a bundle's member variables hold by value: written as the member's type, inside a
    /// tuple or an optional, and not already kept to values with <c>needs RecordType</c>.</summary>
    private static List<string> HeldByValue(string source, Match header, List<string> parameters)
    {
        int bodyAt = HeaderEnd(source: source, header: header);
        string indent = header.Groups[groupname: "indent"].Value;
        string headerText = source[header.Index..bodyAt];
        var held = new List<string>();
        foreach (string line in source[bodyAt..].Split(separator: '\n'))
        {
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith(value: '#'))
            {
                continue;
            }

            if (line.Length - line.TrimStart().Length <= indent.Length)
            {
                break;
            }

            int colon = line.IndexOf(value: ':');
            if (colon < 0)
            {
                continue;
            }

            string type = line[(colon + 1)..];
            foreach (string parameter in parameters)
            {
                if (!held.Contains(item: parameter) &&
                    Regex.IsMatch(input: type, pattern: $@"^[\s(]*{Regex.Escape(str: parameter)}\??\s*(,|\)|$)|,\s*{Regex.Escape(str: parameter)}\??\s*(,|\))") &&
                    !Regex.IsMatch(input: headerText, pattern: $@"needs[^\n]*RecordType\s+{Regex.Escape(str: parameter)}\b"))
                {
                    held.Add(item: parameter);
                }
            }
        }

        return held;
    }

    /// <summary>Where a declaration's header ends: after its own line and the <c>relates</c>, <c>obeys</c> and
    /// <c>needs</c> lines under it.</summary>
    private static int HeaderEnd(string source, Match header)
    {
        int at = source.IndexOf(value: '\n', startIndex: header.Index);
        if (at < 0)
        {
            return source.Length;
        }

        at++;
        while (at < source.Length)
        {
            int end = source.IndexOf(value: '\n', startIndex: at);
            string line = end < 0 ? source[at..] : source[at..end];
            if (!Regex.IsMatch(input: line, pattern: @"^[ \t]*(relates|obeys|needs)\b"))
            {
                break;
            }

            at = end < 0 ? source.Length : end + 1;
        }

        return at;
    }

    /// <summary>Whether the use at <paramref name="at"/> is the bundle's own header.</summary>
    private static bool IsInsideHeader(string text, int at, string name)
    {
        int lineStart = text.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: at - 1)) + 1;
        return Regex.IsMatch(input: text[lineStart..at], pattern: @"^[ \t]*bundle[ \t]+$") ||
               Regex.IsMatch(input: text[lineStart..at], pattern: $@"routine[ \t]+$") && text[at..].StartsWith(value: name);
    }

    /// <summary>The text between the bracket at <paramref name="open"/> and its matching close, or null.</summary>
    private static string? BracketContent(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            switch (text[index: i])
            {
                case '[' or '(':
                    depth++;
                    break;
                case ']' or ')':
                    depth--;
                    if (depth == 0)
                    {
                        return text[(open + 1)..i];
                    }

                    break;
                case '\n':
                    return null;
            }
        }

        return null;
    }

    /// <summary>The type arguments written between brackets, split at the commas outside nested brackets.</summary>
    private static List<string> SplitTypeArguments(string text)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[index: i])
            {
                case '[' or '(':
                    depth++;
                    break;
                case ']' or ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(item: text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        if (text.Trim().Length > 0)
        {
            parts.Add(item: text[start..].Trim());
        }

        return parts;
    }

    /// <summary>"entity" when the type argument is or holds an entity, "value" when it holds none, null when the
    /// step can't tell (a type parameter, a type it has not seen declared).</summary>
    private static string? ArgumentKind(string argument, Dictionary<string, string> kinds)
    {
        string bare = argument.TrimEnd('?').Trim();
        if (bare.StartsWith(value: '(') && bare.EndsWith(value: ')'))
        {
            List<string?> items = SplitTypeArguments(text: bare[1..^1]).Select(selector: a => ArgumentKind(argument: a, kinds: kinds)).ToList();
            return items.Contains(item: "entity") ? "entity" : items.Contains(item: null) ? null : "value";
        }

        int bracket = bare.IndexOf(value: '[');
        string head = bracket < 0 ? bare : bare[..bracket];
        if (BuiltInValues.Contains(item: head))
        {
            return "value";
        }

        return kinds.TryGetValue(key: head, value: out string? kind)
            ? kind switch
            {
                "entity" => "entity",
                // A record holds only values. A variant or a bundle follows what it holds, which the step does not read.
                "record" or "choice" or "flags" or "crashable" => "value",
                _ => null
            }
            : null;
    }

    /// <summary>The kind of every type declared in <paramref name="texts"/> and in the standard library.</summary>
    private static Dictionary<string, string> TypeKinds(IEnumerable<string> texts)
    {
        var kinds = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        IEnumerable<string> library = [];
        try
        {
            string standard = StdlibLoader.GetDefaultStdlibPath();
            if (Directory.Exists(path: standard))
            {
                library = Directory.EnumerateFiles(path: standard, searchPattern: "*.rf", searchOption: SearchOption.AllDirectories)
                                   .Select(selector: File.ReadAllText);
            }
        }
        catch (IOException)
        {
            // Without the library, only the files' own types are known: the rest is listed for a decision by hand.
        }

        foreach (string text in library.Concat(second: texts))
        {
            foreach (Match declared in KindDeclaration.Matches(input: text))
            {
                kinds.TryAdd(key: declared.Groups[groupname: "name"].Value, value: declared.Groups[groupname: "kind"].Value);
            }
        }

        return kinds;
    }
}
