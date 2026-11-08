using Builder.Formatting;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Execution;

/// <summary>
/// Doc comments written for you. Typing <c>###</c> alone on the line above a declaration fills in the frame: a summary
/// line, then for a routine a <c>:typeparam</c> line per type parameter of its own, a <c>:param</c> line per parameter
/// but <c>me</c>, a <c>:returns:</c> line unless it returns None and a <c>:throws:</c> line per crashable its body throws.
/// It comes as a completion item (a snippet, the cursor on the summary) and as on-type formatting, since editors offer
/// one or the other. A routine whose doc comment lacks some of those lines gets a quick fix that adds them where they
/// belong, and one whose doc comment names a parameter it doesn't have, or a result it doesn't give, gets a separate
/// quick fix that removes those. The declarations come from a fresh parse of the text.
/// </summary>
internal static class LspDocComments
{
    /// <summary>What a doc comment is held to: the routine's own type parameters, its parameters but <c>me</c>, whether
    /// it returns a value and the crashables its body throws. A type has none of them.</summary>
    internal sealed record Facts(List<string> TypeParams, List<string> Params, bool Returns, List<string> Throws);

    /// <summary>One doc line and the lines that continue it: a field (<c>:param x: ...</c>) with its kind and name, or a
    /// summary line.</summary>
    private sealed record Block(string? Kind, string? Name, int First, int Last);

    private static readonly Facts None = new(TypeParams: [], Params: [], Returns: false, Throws: []);

    /// <summary>How the kinds of a doc comment follow one another. Other kinds (<c>:note:</c>, <c>:see:</c>) come last.</summary>
    private static int Rank(string? kind)
    {
        return kind switch
        {
            null => 0,
            "typeparam" => 1,
            "param" => 2,
            "returns" => 3,
            "throws" => 4,
            _ => 5
        };
    }

    /// <summary>The declarations of the text by the 1-based line their header is on, with what a doc comment above
    /// them is held to.</summary>
    private static Dictionary<int, Facts> Targets(string text, string fileName, Language language)
    {
        var result = new Dictionary<int, Facts>();
        SourceFormatter.ParsedFile parsed;
        try
        {
            parsed = SourceFormatter.ParseFile(source: SourceFormatter.Normalize(source: text), fileName: fileName,
                language: language, what: "the file");
        }
        catch (FormatRefusedException)
        {
            return result;
        }

        AstWalker.Walk(root: parsed.Program,
            visit: node =>
            {
                switch (node)
                {
                    case RoutineDeclaration { IsScriptEntry: false } routine:
                        result.TryAdd(key: routine.Location.Line, value: FactsOf(routine: routine));
                        break;
                    case RecordDeclaration or EntityDeclaration or ChoiceDeclaration or FlagsDeclaration or
                        CrashableDeclaration or VariantDeclaration or ProtocolDeclaration or PresetDeclaration:
                        result.TryAdd(key: ((SyntaxTree.Declaration)node).Location.Line, value: None);
                        break;
                }
            });
        return result;
    }

    private static Facts FactsOf(RoutineDeclaration routine)
    {
        var throws = new List<string>();
        AstWalker.Walk(root: routine.Body,
            visit: node =>
            {
                string? error = node switch
                {
                    ThrowStatement { Error: CreatorExpression made } => made.TypeName,
                    ThrowStatement { Error: CallExpression { Callee: IdentifierExpression callee } } => callee.Name,
                    _ => null
                };
                if (error != null && !throws.Contains(item: error))
                {
                    throws.Add(item: error);
                }
            });
        // The receiver's parameters (`T` in `Box[T].pick[U]`) belong to the type and are documented there.
        var receiver = new HashSet<string>();
        void Names(TypeExpression? type)
        {
            if (type == null)
            {
                return;
            }

            receiver.Add(item: type.Name);
            foreach (TypeExpression argument in type.GenericArguments ?? [])
            {
                Names(type: argument);
            }
        }

        Names(type: routine.ReceiverType);
        return new Facts(TypeParams: (routine.GenericParameters ?? []).Where(predicate: g => !receiver.Contains(item: g))
                                                                       .ToList(),
            Params: routine.Parameters.Select(selector: p => p.Name)
                           .Where(predicate: p => p != "me")
                           .ToList(),
            Returns: routine.ReturnType != null && routine.ReturnType.Name != "None",
            Throws: throws);
    }

    /// <summary>The 1-based header line of the declaration the doc lines ending on 0-based line
    /// <paramref name="docEnd"/> belong to: the next line past annotation lines (<c>@readonly</c>).</summary>
    private static int HeaderBelow(string[] lines, int docEnd)
    {
        int at = docEnd + 1;
        while (at < lines.Length && lines[at].TrimStart().StartsWith(value: '@'))
        {
            at++;
        }

        return at + 1;
    }

    /// <summary>
    /// The frame for the declaration right below the 0-based line <paramref name="line"/>, which holds <c>###</c>
    /// alone. Null when no declaration starts there or a doc line already sits above the <c>###</c>. With
    /// <paramref name="snippet"/>, the summary and each description is a tab stop.
    /// </summary>
    internal static string? Frame(string text, string fileName, Language language, int line, bool snippet)
    {
        string[] lines = SourceFormatter.Normalize(source: text).Split(separator: '\n');
        if (line < 0 || line >= lines.Length || lines[line].Trim() != "###" ||
            line > 0 && lines[line - 1].TrimStart().StartsWith(value: "###", comparisonType: StringComparison.Ordinal))
        {
            return null;
        }

        if (!Targets(text: text, fileName: fileName, language: language)
               .TryGetValue(key: HeaderBelow(lines: lines, docEnd: line), value: out Facts? facts))
        {
            return null;
        }

        string indent = lines[line][..(lines[line].Length - lines[line].TrimStart().Length)];
        int stop = 1;
        string Stop() => snippet ? $"${stop++}" : "";
        string Plain(string s) => snippet
            ? s.Replace(oldValue: "\\", newValue: "\\\\").Replace(oldValue: "$", newValue: "\\$").Replace(oldValue: "}", newValue: "\\}")
            : s;
        var frame = new List<string> { $"{indent}### {Stop()}" };
        frame.AddRange(collection: facts.TypeParams.Select(selector: t => $"{indent}### :typeparam {Plain(s: t)}: {Stop()}"));
        frame.AddRange(collection: facts.Params.Select(selector: p => $"{indent}### :param {Plain(s: p)}: {Stop()}"));
        if (facts.Returns)
        {
            frame.Add(item: $"{indent}### :returns: {Stop()}");
        }

        frame.AddRange(collection: facts.Throws.Select(selector: e => $"{indent}### :throws: {{{Plain(s: e)}}} {Stop()}"));
        return string.Join(separator: "\n", values: frame.Select(selector: l => snippet ? l : l.TrimEnd())) +
               (snippet ? "$0" : "");
    }

    /// <summary>
    /// The quick fixes for doc comments that don't match their routine, offered on the routine's header or doc lines
    /// (0-based <paramref name="first"/> to <paramref name="last"/>): one adds the lines it lacks, one removes the lines
    /// that name nothing it has.
    /// </summary>
    internal static List<LspRefactors.Refactor> Fixes(string text, string fileName, Language language, int first, int last,
        bool korean)
    {
        var fixes = new List<LspRefactors.Refactor>();
        string[] lines = SourceFormatter.Normalize(source: text).Split(separator: '\n');
        Dictionary<int, Facts> targets = Targets(text: text, fileName: fileName, language: language);
        for (int bottom = 0; bottom < lines.Length; bottom++)
        {
            // The last doc line of a comment, right above a declaration's annotations or header.
            if (!lines[bottom].TrimStart().StartsWith(value: "###", comparisonType: StringComparison.Ordinal) ||
                bottom + 1 < lines.Length &&
                lines[bottom + 1].TrimStart().StartsWith(value: "###", comparisonType: StringComparison.Ordinal))
            {
                continue;
            }

            int header = HeaderBelow(lines: lines, docEnd: bottom);
            if (!targets.TryGetValue(key: header, value: out Facts? facts))
            {
                continue;
            }

            int top = bottom;
            while (top > 0 && lines[top - 1].TrimStart().StartsWith(value: "###", comparisonType: StringComparison.Ordinal))
            {
                top--;
            }

            if (last < top || first > header - 1)
            {
                continue;
            }

            List<Block> blocks = Blocks(lines: lines, top: top, bottom: bottom);
            string indent = lines[top][..(lines[top].Length - lines[top].TrimStart().Length)];
            if (Filled(lines: lines, blocks: blocks, facts: facts, indent: indent) is { } filled)
            {
                fixes.Add(item: new LspRefactors.Refactor(
                    Title: korean ? "문서 주석 채우기" : "Fill in the doc comment",
                    StartLine: top, EndLine: bottom, NewText: filled));
            }

            if (Pruned(lines: lines, blocks: blocks, facts: facts) is { } pruned)
            {
                fixes.Add(item: new LspRefactors.Refactor(
                    Title: korean ? "맞지 않는 문서 줄 지우기" : "Remove doc lines that match nothing",
                    StartLine: top, EndLine: bottom, NewText: pruned));
            }
        }

        return fixes;
    }

    /// <summary>The doc lines <paramref name="top"/> to <paramref name="bottom"/> as blocks: each field line with the
    /// lines after it that aren't fields.</summary>
    private static List<Block> Blocks(string[] lines, int top, int bottom)
    {
        var blocks = new List<Block>();
        for (int i = top; i <= bottom; i++)
        {
            string body = lines[i].Trim()[3..].Trim();
            if (body.StartsWith(value: ':') && body.IndexOf(value: ':', startIndex: 1) is var end and > 0)
            {
                string[] spec = body[1..end].Trim().Split(separator: ' ', count: 2, options: StringSplitOptions.RemoveEmptyEntries);
                if (spec.Length > 0)
                {
                    blocks.Add(item: new Block(Kind: spec[0].ToLowerInvariant(),
                        Name: spec.Length > 1 ? spec[1].Trim() : null, First: i, Last: i));
                    continue;
                }
            }

            // A line that continues the field above it, else a summary line.
            if (blocks.Count > 0 && blocks[^1].Kind != null)
            {
                blocks[^1] = blocks[^1] with { Last = i };
            }
            else
            {
                blocks.Add(item: new Block(Kind: null, Name: null, First: i, Last: i));
            }
        }

        return blocks;
    }

    /// <summary>The doc lines with the lines they lack added where they belong, or null when nothing is missing.</summary>
    private static string? Filled(string[] lines, List<Block> blocks, Facts facts, string indent)
    {
        bool Has(string kind, string? name) => blocks.Any(predicate: b => b.Kind == kind && (name == null || b.Name == name));
        bool Thrown(string error) => blocks.Any(predicate: b => b.Kind == "throws" &&
                                                                Enumerable.Range(start: b.First, count: b.Last - b.First + 1)
                                                                          .Any(predicate: i => lines[i].Contains(
                                                                               value: $"{{{error}}}",
                                                                               comparisonType: StringComparison.Ordinal)));
        var missing = new List<(int Rank, int Order, string Line)>();
        for (int i = 0; i < facts.TypeParams.Count; i++)
        {
            if (!Has(kind: "typeparam", name: facts.TypeParams[i]))
            {
                missing.Add(item: (1, i, $"{indent}### :typeparam {facts.TypeParams[i]}:"));
            }
        }

        for (int i = 0; i < facts.Params.Count; i++)
        {
            if (!Has(kind: "param", name: facts.Params[i]))
            {
                missing.Add(item: (2, i, $"{indent}### :param {facts.Params[i]}:"));
            }
        }

        if (facts.Returns && !Has(kind: "returns", name: null))
        {
            missing.Add(item: (3, 0, $"{indent}### :returns:"));
        }

        missing.AddRange(collection: facts.Throws.Where(predicate: e => !Thrown(error: e))
                                          .Select(selector: e => (4, 0, $"{indent}### :throws: {{{e}}}")));
        if (missing.Count == 0)
        {
            return null;
        }

        // Each new line goes before the first block that comes after it: a later kind, or a later parameter.
        int OrderOf(Block b) => b.Kind switch
        {
            "typeparam" => facts.TypeParams.IndexOf(item: b.Name ?? ""),
            "param" => facts.Params.IndexOf(item: b.Name ?? ""),
            _ => -1
        };
        var output = new List<string>();
        var pending = new Queue<(int Rank, int Order, string Line)>(
            collection: missing.OrderBy(keySelector: m => m.Rank).ThenBy(keySelector: m => m.Order));
        foreach (Block b in blocks)
        {
            int rank = Rank(kind: b.Kind);
            while (pending.Count > 0 && rank > 0 &&
                   (pending.Peek().Rank < rank || pending.Peek().Rank == rank && OrderOf(b: b) > pending.Peek().Order))
            {
                output.Add(item: pending.Dequeue().Line);
            }

            for (int i = b.First; i <= b.Last; i++)
            {
                output.Add(item: lines[i]);
            }
        }

        output.AddRange(collection: pending.Select(selector: p => p.Line));
        return string.Join(separator: "\n", values: output);
    }

    /// <summary>The doc lines without the fields that name nothing the routine has, or null when every field matches.</summary>
    private static string? Pruned(string[] lines, List<Block> blocks, Facts facts)
    {
        bool Stale(Block b) => b.Kind switch
        {
            "param" => b.Name == null || !facts.Params.Contains(item: b.Name),
            "typeparam" => b.Name == null || !facts.TypeParams.Contains(item: b.Name),
            "returns" => !facts.Returns,
            _ => false
        };
        if (!blocks.Any(predicate: Stale))
        {
            return null;
        }

        return string.Join(separator: "\n",
            values: blocks.Where(predicate: b => !Stale(b: b))
                          .SelectMany(selector: b => Enumerable.Range(start: b.First, count: b.Last - b.First + 1))
                          .Select(selector: i => lines[i]));
    }
}
