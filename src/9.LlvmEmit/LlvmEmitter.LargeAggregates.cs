using System.Text;
using System.Text.RegularExpressions;

namespace Builder.LlvmEmit;

/// <summary>
/// Copies of large aggregates. A record holding an array of thousands of elements (<c>PartialItems[S64, 20000]</c>) is
/// read into a value and written out again by a <c>load</c> and <c>store</c> of the whole type, which LLVM's optimizer
/// splits into one load and one store per element: the build time grows with the array's length. The pair is replaced
/// by one <c>llvm.memmove</c> of the type's size. Implemented as a post-process over the finished IR text, like
/// <c>ApplyTbaa</c>.
/// </summary>
public partial class LlvmEmitter
{
    /// <summary>An array with at least this many elements makes its type large.</summary>
    private const int LargeArrayElements = 128;

    private static readonly Regex TypeDefinitionLine = new(
        pattern: @"^(?<name>%(?:""[^""]+""|[\w.$]+)) = type (?<body>.+)$", options: RegexOptions.Compiled);

    private static readonly Regex ArrayInType = new(pattern: @"\[(?<count>\d+) x (?<elem>[^\]]*)\]",
        options: RegexOptions.Compiled);

    private static readonly Regex NamedTypeRef = new(pattern: @"%(?:""[^""]+""|[\w.$]+)",
        options: RegexOptions.Compiled);

    private static readonly Regex LoadLine = new(
        pattern: @"^(?<indent>\s*)(?<value>%[\w.$]+) = load (?<type>.+?), ptr (?<from>%[\w.$]+|@[\w.$]+|@""[^""]+"")(?<meta>(?:,\s*![\w.]+\s*!\d+)*)\s*$",
        options: RegexOptions.Compiled);

    private static readonly Regex StoreLine = new(
        pattern: @"^\s*store (?<type>.+?) (?<value>%[\w.$]+), ptr (?<to>%[\w.$]+|@[\w.$]+|@""[^""]+"")(?:,\s*![\w.]+\s*!\d+)*\s*$",
        options: RegexOptions.Compiled);

    private static readonly Regex ValueToken = new(pattern: @"%[\w.$]+", options: RegexOptions.Compiled);

    private static string ApplyLargeAggregateCopies(string ir)
    {
        var definitions = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        string[] lines = ir.Split(separator: '\n');
        foreach (string line in lines)
        {
            if (line.Length > 0 && line[0] == '%' && TypeDefinitionLine.Match(input: line) is { Success: true } def)
            {
                definitions[key: def.Groups["name"].Value] = def.Groups["body"].Value;
            }
        }

        var largeMemo = new Dictionary<string, bool>(comparer: StringComparer.Ordinal);
        bool IsLarge(string type, int depth)
        {
            if (depth > 16)
            {
                return false;
            }

            foreach (Match array in ArrayInType.Matches(input: type))
            {
                if (long.Parse(s: array.Groups["count"].Value) >= LargeArrayElements ||
                    IsLarge(type: array.Groups["elem"].Value, depth: depth + 1))
                {
                    return true;
                }
            }

            foreach (Match named in NamedTypeRef.Matches(input: type))
            {
                string name = named.Value;
                if (!definitions.TryGetValue(key: name, value: out string? body))
                {
                    continue;
                }

                if (!largeMemo.TryGetValue(key: name, value: out bool large))
                {
                    largeMemo[key: name] = false;
                    large = IsLarge(type: body, depth: depth + 1);
                    largeMemo[key: name] = large;
                }

                if (large)
                {
                    return true;
                }
            }

            return false;
        }

        var result = new StringBuilder(capacity: ir.Length);
        bool usedMemmove = false;
        int functionStart = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith(value: "define ", comparisonType: StringComparison.Ordinal))
            {
                functionStart = i;
            }

            if (functionStart >= 0 && i + 1 < lines.Length && line.Contains(value: " = load ") &&
                LoadLine.Match(input: line) is { Success: true } load &&
                StoreLine.Match(input: lines[i + 1]) is { Success: true } store &&
                store.Groups["value"].Value == load.Groups["value"].Value &&
                store.Groups["type"].Value == load.Groups["type"].Value &&
                IsLarge(type: load.Groups["type"].Value, depth: 0) &&
                UsedOnce(lines: lines, from: functionStart, token: load.Groups["value"].Value))
            {
                string type = load.Groups["type"].Value;
                result.Append(value: load.Groups["indent"].Value)
                      .Append(value: "call void @llvm.memmove.p0.p0.i64(ptr ")
                      .Append(value: store.Groups["to"].Value)
                      .Append(value: ", ptr ")
                      .Append(value: load.Groups["from"].Value)
                      .Append(value: ", i64 ptrtoint (ptr getelementptr (")
                      .Append(value: type)
                      .Append(value: ", ptr null, i32 1) to i64), i1 false)")
                      .Append(value: DebugPart(meta: load.Groups["meta"].Value))
                      .Append(value: '\n');
                usedMemmove = true;
                i++;
                continue;
            }

            if (line.StartsWith(value: "}", comparisonType: StringComparison.Ordinal))
            {
                functionStart = -1;
            }

            result.Append(value: line);
            if (i + 1 < lines.Length)
            {
                result.Append(value: '\n');
            }
        }

        if (usedMemmove && !ir.Contains(value: "declare void @llvm.memmove.p0.p0.i64"))
        {
            result.Append(value: "\ndeclare void @llvm.memmove.p0.p0.i64(ptr, ptr, i64, i1 immarg)\n");
        }

        return result.ToString();
    }

    private static readonly Regex DebugMeta = new(pattern: @",\s*!dbg\s*!\d+", options: RegexOptions.Compiled);

    /// <summary>The <c>, !dbg !N</c> part of a line's metadata, or nothing.</summary>
    private static string DebugPart(string meta)
    {
        return DebugMeta.Match(input: meta) is { Success: true } dbg ? dbg.Value : "";
    }


    private static readonly Regex InsertValueLine = new(
        pattern: @"^(?<indent>\s*)(?<result>%[\w.$]+) = insertvalue (?<rest>.+?)(?<meta>(?:,\s*![\w.]+\s*!\d+)*)\s*$",
        options: RegexOptions.Compiled);

    private static readonly Regex StoreWholeLine = new(
        pattern: @"^(?<indent>\s*)store (?<body>.+?)(?<meta>(?:,\s*![\w.]+\s*!\d+)*)\s*$",
        options: RegexOptions.Compiled);

    /// <summary>
    /// A large aggregate built from zero by a chain of <c>insertvalue</c> and stored whole (<c>PartialItems[S64, 100000]()</c>
    /// is <c>{ [100000 x i64] zeroinitializer, i64 0 }</c>) is stored by the instruction selector one element at a time,
    /// which is slow and for a big enough array overflows its stack. The chain becomes one <c>memset</c> of the place and a
    /// store of each field that is not zero.
    /// </summary>
    private static string ApplyLargeAggregateBuilds(string ir)
    {
        string[] lines = ir.Split(separator: '\n');
        var definitions = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (line.Length > 0 && line[0] == '%' && TypeDefinitionLine.Match(input: line) is { Success: true } def)
            {
                definitions[key: def.Groups["name"].Value] = def.Groups["body"].Value;
            }
        }

        bool IsLargeType(string type)
        {
            return ArrayInType.Matches(input: type).Any(predicate: a => long.Parse(s: a.Groups["count"].Value) >= LargeArrayElements) ||
                   NamedTypeRef.Matches(input: type)
                               .Any(predicate: n => definitions.TryGetValue(key: n.Value, value: out string? body) &&
                                                    ArrayInType.Matches(input: body)
                                                               .Any(predicate: a => long.Parse(s: a.Groups["count"].Value) >= LargeArrayElements));
        }

        var removed = new HashSet<int>();
        var inserted = new Dictionary<int, string>();
        bool usedMemset = false;
        int counter = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(value: "store ") || StoreWholeLine.Match(input: lines[i]) is not { Success: true } store)
            {
                continue;
            }

            string body = store.Groups["body"].Value;
            int split = body.LastIndexOf(value: ", ptr ", comparisonType: StringComparison.Ordinal);
            if (split < 0)
            {
                continue;
            }

            string target = body[(split + ", ptr ".Length)..];
            string valueText = body[..split];
            int space = valueText.LastIndexOf(value: ' ');
            if (space < 0 || valueText[(space + 1)..] is not { Length: > 1 } last || last[0] != '%')
            {
                continue;
            }

            string type = valueText[..space];
            if (!IsLargeType(type: type))
            {
                continue;
            }

            // The chain: consecutive insertvalue lines ending in `last`, the first one inserting into zeroinitializer.
            var steps = new List<(int Line, string FieldType, string Value, string Index)>();
            string wanted = last;
            int at = i - 1;
            bool ok = false;
            while (at >= 0 && InsertValueLine.Match(input: lines[at]) is { Success: true } insert &&
                   insert.Groups["result"].Value == wanted)
            {
                string rest = insert.Groups["rest"].Value;
                string prefix = type + " ";
                if (!rest.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal))
                {
                    break;
                }

                rest = rest[prefix.Length..];
                int comma = rest.IndexOf(value: ", ", comparisonType: StringComparison.Ordinal);
                int lastComma = rest.LastIndexOf(value: ", ", comparisonType: StringComparison.Ordinal);
                if (comma < 0 || lastComma <= comma)
                {
                    break;
                }

                string baseValue = rest[..comma];
                string field = rest[(comma + 2)..lastComma];
                string index = rest[(lastComma + 2)..];
                int fieldSpace = field.LastIndexOf(value: ' ');
                if (fieldSpace < 0 || field.Contains(value: '(') || !int.TryParse(s: index, result: out _))
                {
                    break;
                }

                steps.Add(item: (at, field[..fieldSpace], field[(fieldSpace + 1)..], index));
                if (baseValue == "zeroinitializer")
                {
                    ok = true;
                    break;
                }

                wanted = baseValue;
                at--;
            }

            if (!ok || steps.Count == 0)
            {
                continue;
            }

            var code = new StringBuilder();
            string indent = store.Groups["indent"].Value;
            code.Append(value: indent)
                .Append(value: "call void @llvm.memset.p0.i64(ptr ")
                .Append(value: target)
                .Append(value: ", i8 0, i64 ptrtoint (ptr getelementptr (")
                .Append(value: type)
                .Append(value: ", ptr null, i32 1) to i64), i1 false)")
                .Append(value: DebugPart(meta: store.Groups["meta"].Value))
                .Append(value: '\n');
            for (int k = steps.Count - 1; k >= 0; k--)
            {
                var (_, fieldType, fieldValue, index) = steps[index: k];
                if (fieldValue is "zeroinitializer" or "0" or "null" or "false")
                {
                    continue;
                }

                string place = $"%agg.{counter++}";
                code.Append(value: indent).Append(value: $"{place} = getelementptr {type}, ptr {target}, i32 0, i32 {index}\n");
                code.Append(value: indent).Append(value: $"store {fieldType} {fieldValue}, ptr {place}\n");
            }

            foreach (var step in steps)
            {
                removed.Add(item: step.Line);
            }

            removed.Add(item: i);
            inserted[key: i] = code.ToString().TrimEnd(trimChar: '\n');
            usedMemset = true;
        }

        if (!usedMemset)
        {
            return ir;
        }

        var result = new StringBuilder(capacity: ir.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (inserted.TryGetValue(key: i, value: out string? replacement))
            {
                result.Append(value: replacement).Append(value: '\n');
            }
            else if (!removed.Contains(item: i))
            {
                result.Append(value: lines[i]);
                if (i + 1 < lines.Length)
                {
                    result.Append(value: '\n');
                }
            }
        }

        if (!ir.Contains(value: "declare void @llvm.memset.p0.i64"))
        {
            result.Append(value: "\ndeclare void @llvm.memset.p0.i64(ptr, i8, i64, i1 immarg)\n");
        }

        return result.ToString();
    }


    private static readonly Regex ExtractValueLine = new(
        pattern: @"^(?<indent>\s*)(?<result>%[\w.$]+) = extractvalue (?<type>.+?) (?<from>%[\w.$]+), (?<index>\d+)(?<meta>(?:,\s*![\w.]+\s*!\d+)*)\s*$",
        options: RegexOptions.Compiled);

    /// <summary>
    /// A field read out of a large record that was loaded whole (<c>load T</c> then <c>extractvalue T, k</c>) reads the field
    /// where it lies: the instruction selector would split the whole load into one piece per array element.
    /// </summary>
    private static string ApplyLargeAggregateFieldReads(string ir)
    {
        string[] lines = ir.Split(separator: '\n');
        var definitions = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (line.Length > 0 && line[0] == '%' && TypeDefinitionLine.Match(input: line) is { Success: true } def)
            {
                definitions[key: def.Groups["name"].Value] = def.Groups["body"].Value;
            }
        }

        var changed = false;
        var result = new StringBuilder(capacity: ir.Length);
        int functionStart = -1;
        int gep = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith(value: "define ", comparisonType: StringComparison.Ordinal))
            {
                functionStart = i;
            }

            if (functionStart >= 0 && i + 1 < lines.Length && line.Contains(value: " = load ") &&
                lines[i + 1].Contains(value: " = extractvalue ") &&
                LoadLine.Match(input: line) is { Success: true } load &&
                ExtractValueLine.Match(input: lines[i + 1]) is { Success: true } extract &&
                extract.Groups["from"].Value == load.Groups["value"].Value &&
                extract.Groups["type"].Value == load.Groups["type"].Value &&
                definitions.TryGetValue(key: load.Groups["type"].Value, value: out string? body) &&
                ArrayInType.Matches(input: body).Any(predicate: a => long.Parse(s: a.Groups["count"].Value) >= LargeArrayElements) &&
                FieldTypeOf(body: body, index: int.Parse(s: extract.Groups["index"].Value)) is { } fieldType &&
                UsedOnce(lines: lines, from: functionStart, token: load.Groups["value"].Value))
            {
                string place = $"%fld.{gep++}";
                string indent = load.Groups["indent"].Value;
                result.Append(value: indent)
                      .Append(value: $"{place} = getelementptr {load.Groups["type"].Value}, ptr {load.Groups["from"].Value}, i32 0, i32 {extract.Groups["index"].Value}\n");
                result.Append(value: indent)
                      .Append(value: $"{extract.Groups["result"].Value} = load {fieldType}, ptr {place}{extract.Groups["meta"].Value}\n");
                changed = true;
                i++;
                continue;
            }

            if (line.StartsWith(value: "}", comparisonType: StringComparison.Ordinal))
            {
                functionStart = -1;
            }

            result.Append(value: line);
            if (i + 1 < lines.Length)
            {
                result.Append(value: '\n');
            }
        }

        return changed ? result.ToString() : ir;
    }

    /// <summary>The type of field <paramref name="index"/> of a struct body <c>{ A, B, C }</c>, or null.</summary>
    private static string? FieldTypeOf(string body, int index)
    {
        body = body.Trim();
        if (!body.StartsWith(value: '{') || !body.EndsWith(value: '}'))
        {
            return null;
        }

        int depth = 0;
        int start = 1;
        int field = 0;
        for (int i = 1; i < body.Length - 1; i++)
        {
            char c = body[index: i];
            if (c is '[' or '{' or '<' or '(')
            {
                depth++;
            }
            else if (c is ']' or '}' or '>' or ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                if (field == index)
                {
                    return body[start..i].Trim();
                }

                field++;
                start = i + 1;
            }
        }

        return field == index ? body[start..^1].Trim() : null;
    }

    /// <summary>Whether <paramref name="token"/> is named exactly twice in the function that starts at line
    /// <paramref name="from"/>: where it is loaded and where it is stored.</summary>
    private static bool UsedOnce(string[] lines, int from, string token)
    {
        int uses = 0;
        for (int i = from; i < lines.Length; i++)
        {
            string line = lines[i];
            if (i > from && line.StartsWith(value: "}", comparisonType: StringComparison.Ordinal))
            {
                break;
            }

            if (!line.Contains(value: token))
            {
                continue;
            }

            foreach (Match match in ValueToken.Matches(input: line))
            {
                if (match.Value == token)
                {
                    uses++;
                }
            }
        }

        return uses == 2;
    }
}
