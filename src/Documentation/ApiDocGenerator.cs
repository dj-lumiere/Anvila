using System.Text;
using System.Text.RegularExpressions;
using Builder.Formatting;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Documentation;

/// <summary>What one run of the API reference generator did.</summary>
/// <param name="Files">The source files read.</param>
/// <param name="Pages">The pages written (a file with nothing public gets none).</param>
/// <param name="Problems">The files that could not be read, each with the reason.</param>
internal sealed record ApiDocResult(int Files, int Pages, List<string> Problems);

/// <summary>
/// Writes the API reference of a standard library as MkDocs pages: one page per source file, mirroring the
/// library's folders, with an index page per folder. Each type gets its signature in the canonical layout, its doc
/// comment, its member variables or cases, and the routines declared on it. Routines on a type declared in another
/// file, free routines, presets and aliases follow. A <c>{Name}</c> in a doc comment links to the type's section
/// when the library declares that type. Nothing marked <c>secret</c> is written.
/// </summary>
internal sealed class ApiDocGenerator
{
    private sealed class SourcePage
    {
        public required string SourcePath { get; init; }
        public required string PagePath { get; init; }
        public required string Title { get; init; }
        public required SyntaxTree.Program Program { get; init; }
        public required SourceIndex Index { get; init; }
        public required string[] Lines { get; init; }
        public Dictionary<string, string> Anchors { get; } = new(comparer: StringComparer.Ordinal);
        public string? Summary { get; set; }
    }

    private static readonly Regex BraceReference = new(pattern: @"\{([^{}\n]+)\}");
    private static readonly Regex CodeSpan = new(pattern: @"(`+)(.+?)\1");
    private static readonly Regex TypeReference = new(pattern: @"^([A-Za-z_]\w*)(\[[^\]]*\])?$");

    private readonly string _sourceRoot;
    private readonly Language _language;
    private readonly string _codeTag;
    private readonly List<SourcePage> _pages = [];

    /// <summary>Every public type of the library by name: the page declaring it and its anchor there.</summary>
    private readonly Dictionary<string, (string Page, string Anchor)> _types = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Creates a generator over the sources under <paramref name="sourceRoot"/> written in
    /// <paramref name="language"/>.
    /// </summary>
    public ApiDocGenerator(string sourceRoot, Language language)
    {
        _sourceRoot = Path.GetFullPath(path: sourceRoot);
        _language = language;
        _codeTag = language.ToString()
                           .ToLowerInvariant();
    }

    /// <summary>
    /// Writes the pages under <paramref name="outputRoot"/>. Every <c>.md</c> file already there is removed first,
    /// so a page whose source file is gone does not linger. Other files (stylesheets, scripts, images) are kept.
    /// </summary>
    public ApiDocResult Generate(string outputRoot)
    {
        var problems = new List<string>();
        string extension = Builder.Frontends.Languages.For(language: _language)
                                  .FileExtension;
        List<string> files = Directory.EnumerateFiles(path: _sourceRoot, searchPattern: "*" + extension,
                                          searchOption: SearchOption.AllDirectories)
                                      .Order(comparer: StringComparer.Ordinal)
                                      .ToList();
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(relativeTo: _sourceRoot, path: file)
                                  .Replace(oldChar: '\\', newChar: '/');
            string source = SourceFormatter.Normalize(source: File.ReadAllText(path: file));
            SourceFormatter.ParsedFile parsed;
            try
            {
                parsed = SourceFormatter.ParseFile(source: source, fileName: file, language: _language,
                    what: "The file");
            }
            catch (FormatRefusedException e)
            {
                problems.Add(item: $"{relative}: {e.Reason}");
                continue;
            }

            _pages.Add(item: new SourcePage
            {
                SourcePath = relative,
                PagePath = Path.ChangeExtension(path: relative, extension: ".md"),
                Title = Path.GetFileNameWithoutExtension(path: relative),
                Program = parsed.Program,
                Index = parsed.Index,
                Lines = source.Split(separator: '\n')
            });
        }

        foreach (SourcePage page in _pages)
        {
            RegisterTypes(page: page);
        }

        Directory.CreateDirectory(path: outputRoot);
        foreach (string old in Directory.EnumerateFiles(path: outputRoot, searchPattern: "*.md",
                     searchOption: SearchOption.AllDirectories))
        {
            File.Delete(path: old);
        }

        var written = new List<SourcePage>();
        foreach (SourcePage page in _pages)
        {
            string? text = RenderPage(page: page);
            if (text == null)
            {
                continue;
            }

            string target = Path.Combine(path1: outputRoot, path2: page.PagePath);
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.WriteAllText(path: target, contents: text);
            written.Add(item: page);
        }

        WriteIndexPages(outputRoot: outputRoot, pages: written);
        return new ApiDocResult(Files: files.Count, Pages: written.Count, Problems: problems);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TYPES
    // ═══════════════════════════════════════════════════════════════════════════

    private static string? TypeName(SyntaxTree.Declaration declaration)
    {
        return declaration switch
        {
            RecordDeclaration record => record.Name,
            EntityDeclaration entity => entity.Name,
            CrashableDeclaration crashable => crashable.Name,
            ChoiceDeclaration choice => choice.Name,
            FlagsDeclaration flags => flags.Name,
            VariantDeclaration variant => variant.Name,
            ProtocolDeclaration protocol => protocol.Name,
            _ => null
        };
    }

    private static List<string>? GenericParameters(SyntaxTree.Declaration declaration)
    {
        return declaration switch
        {
            RecordDeclaration record => record.GenericParameters,
            EntityDeclaration entity => entity.GenericParameters,
            VariantDeclaration variant => variant.GenericParameters,
            ProtocolDeclaration protocol => protocol.GenericParameters,
            _ => null
        };
    }

    private static bool IsPublic(SyntaxTree.Declaration declaration)
    {
        VisibilityModifier visibility = declaration switch
        {
            RecordDeclaration record => record.Visibility,
            EntityDeclaration entity => entity.Visibility,
            CrashableDeclaration crashable => crashable.Visibility,
            ChoiceDeclaration choice => choice.Visibility,
            FlagsDeclaration flags => flags.Visibility,
            ProtocolDeclaration protocol => protocol.Visibility,
            RoutineDeclaration routine => routine.Visibility,
            VariableDeclaration variable => variable.Visibility,
            PresetDeclaration preset => preset.IsSecret
                ? VisibilityModifier.Secret
                : VisibilityModifier.Open,
            _ => VisibilityModifier.Open
        };
        return visibility != VisibilityModifier.Secret;
    }

    private static string DisplayName(SyntaxTree.Declaration type)
    {
        string name = TypeName(declaration: type)!;
        return GenericParameters(declaration: type) is { Count: > 0 } parameters
            ? $"{name}[{string.Join(separator: ", ", values: parameters)}]"
            : name;
    }

    private void RegisterTypes(SourcePage page)
    {
        foreach (SyntaxTree.Declaration declaration in page.Program.Declarations.OfType<SyntaxTree.Declaration>())
        {
            if (TypeName(declaration: declaration) is not { } name || !IsPublic(declaration: declaration) ||
                _types.ContainsKey(key: name))
            {
                continue;
            }

            string anchor = name.ToLowerInvariant();
            int suffix = 2;
            while (page.Anchors.ContainsValue(value: anchor))
            {
                anchor = $"{name.ToLowerInvariant()}-{suffix++}";
            }

            page.Anchors[key: name] = anchor;
            _types[key: name] = (page.PagePath, anchor);
        }
    }

    /// <summary>The type a routine is declared on: its receiver, or the type it creates (a creator is named after
    /// its type). Null for a free routine.</summary>
    private string? OwnerOf(RoutineDeclaration routine)
    {
        if (routine.OwnerName != null)
        {
            return routine.OwnerName;
        }

        return _types.ContainsKey(key: routine.Name)
            ? routine.Name
            : null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PAGES
    // ═══════════════════════════════════════════════════════════════════════════

    private string? RenderPage(SourcePage page)
    {
        var printer = new AstPrinter(index: page.Index);
        List<SyntaxTree.Declaration> declarations = page.Program.Declarations.OfType<SyntaxTree.Declaration>()
                                             .Where(predicate: IsPublic)
                                             .ToList();
        List<SyntaxTree.Declaration> types = declarations.Where(predicate: d => TypeName(declaration: d) != null)
                                              .ToList();
        var declaredHere = new HashSet<string>(collection: types.Select(selector: t => TypeName(declaration: t)!));
        List<RoutineDeclaration> routines = declarations.OfType<RoutineDeclaration>()
                                                        .Where(predicate: r => !r.Name.StartsWith(value: "__"))
                                                        .ToList();
        List<PresetDeclaration> presets = declarations.OfType<PresetDeclaration>()
                                                      .ToList();
        List<VariableDeclaration> globals = declarations.OfType<VariableDeclaration>()
                                                        .ToList();
        List<DefineDeclaration> defines = declarations.OfType<DefineDeclaration>()
                                                      .ToList();

        var body = new StringBuilder();
        foreach (SyntaxTree.Declaration type in types)
        {
            List<RoutineDeclaration> own = routines.Where(predicate: r => OwnerOf(routine: r) == TypeName(declaration: type))
                                                   .ToList();
            RenderType(sb: body, page: page, printer: printer, type: type, routines: own);
        }

        foreach (IGrouping<string, RoutineDeclaration> group in routines
                    .Where(predicate: r => OwnerOf(routine: r) is { } owner && !declaredHere.Contains(item: owner))
                    .GroupBy(keySelector: r => OwnerOf(routine: r)!))
        {
            string owner = _types.TryGetValue(key: group.Key, value: out (string Page, string Anchor) target)
                ? $"[`{group.Key}`]({LinkTo(from: page, page: target.Page, anchor: target.Anchor)})"
                : $"`{group.Key}`";
            body.Append(value: $"## Routines on {owner}\n\n");
            RenderRoutines(sb: body, page: page, printer: printer, routines: group.ToList(), owner: group.Key);
        }

        List<RoutineDeclaration> free = routines.Where(predicate: r => OwnerOf(routine: r) == null)
                                                .ToList();
        if (free.Count > 0)
        {
            body.Append(value: "## Routines\n\n");
            RenderRoutines(sb: body, page: page, printer: printer, routines: free, owner: null);
        }

        if (presets.Count > 0 || globals.Count > 0)
        {
            body.Append(value: "## Presets\n\n");
            foreach (SyntaxTree.Declaration value in presets.Cast<SyntaxTree.Declaration>()
                                                 .Concat(second: globals))
            {
                string name = value is PresetDeclaration preset
                    ? preset.Name
                    : ((VariableDeclaration)value).Name;
                body.Append(value: $"### `{name}`\n\n");
                AppendSignature(sb: body, signature: Signature(page: page, printer: printer, declaration: value));
                AppendDoc(sb: body, page: page, doc: DocAbove(page: page, line: value.Location.Line));
            }
        }

        if (defines.Count > 0)
        {
            body.Append(value: "## Aliases\n\n");
            foreach (DefineDeclaration define in defines)
            {
                body.Append(value: $"- `{define.NewName}` is `{define.OldName}`");
                string? doc = DocAbove(page: page, line: define.Location.Line);
                if (doc != null)
                {
                    body.Append(value: " — " + OneLine(page: page, doc: doc));
                }

                body.Append(value: '\n');
            }

            body.Append(value: '\n');
        }

        if (body.Length == 0)
        {
            return null;
        }

        page.Summary = types.Select(selector: t => DocAbove(page: page, line: t.Location.Line))
                            .Concat(second: routines.Select(selector: r => DocAbove(page: page, line: r.Location.Line)))
                            .FirstOrDefault(predicate: d => d != null) is { } first
            ? FirstSentence(page: page, doc: first)
            : null;

        string module = page.Program.Declarations.OfType<ModuleDeclaration>()
                            .FirstOrDefault()
                           ?.Path is { } path
            ? $" · module `{path}`"
            : "";
        var text = new StringBuilder();
        text.Append(value: $"# {page.Title}\n\n");
        text.Append(value: $"Source: `{page.SourcePath}`{module}\n\n");
        text.Append(value: body);
        return text.ToString()
                   .TrimEnd() + "\n";
    }

    private void RenderType(StringBuilder sb, SourcePage page, AstPrinter printer, SyntaxTree.Declaration type,
        List<RoutineDeclaration> routines)
    {
        string name = TypeName(declaration: type)!;
        string anchor = page.Anchors.TryGetValue(key: name, value: out string? own)
            ? own
            : name.ToLowerInvariant();
        sb.Append(value: $"## `{DisplayName(type: type)}` {{ #{anchor} }}\n\n");
        AppendSignature(sb: sb, signature: Signature(page: page, printer: printer, declaration: type));
        AppendDoc(sb: sb, page: page, doc: DocAbove(page: page, line: type.Location.Line));

        List<SyntaxTree.Declaration>? members = type switch
        {
            RecordDeclaration record => record.Members,
            EntityDeclaration entity => entity.Members,
            CrashableDeclaration crashable => crashable.Members,
            _ => null
        };
        if (members != null)
        {
            List<VariableDeclaration> fields = members.OfType<VariableDeclaration>()
                                                      .Where(predicate: IsPublic)
                                                      .ToList();
            if (fields.Count > 0)
            {
                sb.Append(value: "**Member variables**\n\n");
                foreach (VariableDeclaration field in fields)
                {
                    AppendItem(sb: sb, page: page, text: page.Lines[field.Location.Line - 1]
                                                          .Trim(), docLine: field.Location.Line);
                }

                sb.Append(value: '\n');
            }

            routines = members.OfType<RoutineDeclaration>()
                              .Where(predicate: IsPublic)
                              .Concat(second: routines)
                              .ToList();
        }

        switch (type)
        {
            case ChoiceDeclaration choice:
                sb.Append(value: "**Cases**\n\n");
                foreach (ChoiceCase choiceCase in choice.Cases)
                {
                    AppendItem(sb: sb, page: page, text: page.Lines[choiceCase.Location.Line - 1]
                                                             .Trim(), docLine: choiceCase.Location.Line);
                }

                sb.Append(value: '\n');
                routines = choice.MemberRoutines.Where(predicate: IsPublic)
                                 .Concat(second: routines)
                                 .ToList();
                break;
            case FlagsDeclaration flags:
                sb.Append(value: "**Flags**\n\n");
                foreach (string flag in flags.Members)
                {
                    int line = FlagLine(page: page, flags: flags, flag: flag);
                    AppendItem(sb: sb, page: page, text: flag, docLine: line);
                }

                sb.Append(value: '\n');
                break;
            case VariantDeclaration variant:
                sb.Append(value: "**Members**\n\n");
                foreach (VariantMember member in variant.Members)
                {
                    AppendItem(sb: sb, page: page, text: page.Lines[member.Location.Line - 1]
                                                             .Trim(), docLine: member.Location.Line);
                }

                sb.Append(value: '\n');
                break;
            case ProtocolDeclaration protocol:
                foreach (RoutineSignature signature in protocol.MemberRoutines)
                {
                    sb.Append(value: $"### `{signature.Name}`\n\n");
                    string text;
                    try
                    {
                        text = printer.PrintSignature(signature: signature);
                    }
                    catch (FormatRefusedException)
                    {
                        text = page.Lines[signature.Location.Line - 1]
                                   .Trim();
                    }

                    AppendSignature(sb: sb, signature: text);
                    AppendDoc(sb: sb, page: page, doc: DocAbove(page: page, line: signature.Location.Line));
                }

                break;
        }

        RenderRoutines(sb: sb, page: page, printer: printer, routines: routines, owner: name);
    }

    private void RenderRoutines(StringBuilder sb, SourcePage page, AstPrinter printer,
        List<RoutineDeclaration> routines, string? owner)
    {
        foreach (RoutineDeclaration routine in routines)
        {
            string heading = routine.MemberRoutineName ?? routine.Name;
            if (owner != null && routine.OwnerName == null && routine.Name == owner)
            {
                heading = $"{owner}()";
            }

            sb.Append(value: $"### `{heading}`\n\n");
            AppendSignature(sb: sb, signature: Signature(page: page, printer: printer, declaration: routine));
            AppendDoc(sb: sb, page: page, doc: DocAbove(page: page, line: routine.Location.Line));
        }
    }

    /// <summary>The source line of a flag in its <c>flags</c> body, for its doc comment. 0 when not found.</summary>
    private static int FlagLine(SourcePage page, FlagsDeclaration flags, string flag)
    {
        for (int line = flags.Location.Line; line < page.Lines.Length; line++)
        {
            string text = page.Lines[line];
            if (text.Length > 0 && !char.IsWhiteSpace(c: text[index: 0]))
            {
                break;
            }

            if (text.Trim() == flag)
            {
                return line + 1;
            }
        }

        return 0;
    }

    private static string Signature(SourcePage page, AstPrinter printer, SyntaxTree.Declaration declaration)
    {
        try
        {
            return printer.PrintSignature(declaration: declaration);
        }
        catch (FormatRefusedException)
        {
            return page.Lines[declaration.Location.Line - 1]
                       .Trim();
        }
    }

    private void AppendSignature(StringBuilder sb, string signature)
    {
        // `@rf("...")` says which RazorForge declaration this one shows: how the library is built, not what the
        // reader calls.
        signature = string.Join(separator: "\n", values: signature.Split(separator: '\n')
                                                              .Where(predicate: line => !line.TrimStart()
                                                                  .StartsWith(value: "@rf(",
                                                                      comparisonType: StringComparison.Ordinal)));
        sb.Append(value: $"```{_codeTag}\n{signature}\n```\n\n");
    }

    private void AppendItem(StringBuilder sb, SourcePage page, string text, int docLine)
    {
        sb.Append(value: $"- `{text}`");
        if (docLine > 0 && DocAbove(page: page, line: docLine) is { } doc)
        {
            sb.Append(value: " — " + OneLine(page: page, doc: doc));
        }

        sb.Append(value: '\n');
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DOC COMMENTS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The doc comment written directly above the declaration on <paramref name="line"/> (1-based): the
    /// <c>###</c> lines right above it, past any annotation lines. Null when there is none.
    /// </summary>
    private static string? DocAbove(SourcePage page, int line)
    {
        var lines = new List<string>();
        for (int at = line - 2; at >= 0; at--)
        {
            string text = page.Lines[at]
                              .Trim();
            if (text.StartsWith(value: "###"))
            {
                string content = text[3..];
                lines.Add(item: content.StartsWith(value: ' ')
                    ? content[1..]
                    : content);
            }
            else if (!text.StartsWith(value: '@'))
            {
                break;
            }
        }

        lines.Reverse();
        return lines.Count > 0
            ? string.Join(separator: "\n", values: lines)
            : null;
    }

    private void AppendDoc(StringBuilder sb, SourcePage page, string? doc)
    {
        if (doc == null)
        {
            return;
        }

        DocComment parsed = DocComment.Parse(doc: doc);
        if (parsed.Summary.Length > 0)
        {
            sb.Append(value: SeparateBlocks(text: Linkify(page: page, text: parsed.Summary)))
              .Append(value: "\n\n");
        }

        AppendList(sb: sb, page: page, title: "Type parameters", entries: parsed.TypeParams);
        AppendList(sb: sb, page: page, title: "Parameters", entries: parsed.Params);
        AppendLine(sb: sb, page: page, label: "Returns", text: parsed.Returns);
        foreach (string throws in parsed.Throws)
        {
            AppendLine(sb: sb, page: page, label: "Throws", text: throws);
        }

        foreach (string absent in parsed.Absent)
        {
            AppendLine(sb: sb, page: page, label: "Absent", text: absent);
        }

        foreach (string note in parsed.Notes)
        {
            AppendLine(sb: sb, page: page, label: "Note", text: note);
        }

        foreach (string see in parsed.Sees)
        {
            AppendLine(sb: sb, page: page, label: "See also", text: see);
        }
    }

    private void AppendList(StringBuilder sb, SourcePage page, string title, List<(string Name, string Desc)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        sb.Append(value: $"**{title}**\n\n");
        foreach ((string name, string desc) in entries)
        {
            sb.Append(value: desc.Length > 0
                ? $"- `{name}` — {Linkify(page: page, text: desc)}\n"
                : $"- `{name}`\n");
        }

        sb.Append(value: '\n');
    }

    private void AppendLine(StringBuilder sb, SourcePage page, string label, string? text)
    {
        if (string.IsNullOrWhiteSpace(value: text))
        {
            return;
        }

        sb.Append(value: $"**{label}** — {Linkify(page: page, text: text)}\n\n");
    }

    /// <summary>A doc comment's summary on one line, for a list item.</summary>
    private string OneLine(SourcePage page, string doc)
    {
        string summary = DocComment.Parse(doc: doc)
                                   .Summary;
        return Linkify(page: page, text: string.Join(separator: " ",
            values: summary.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries)));
    }

    /// <summary>The first sentence of a doc comment's summary, for an index page.</summary>
    private string FirstSentence(SourcePage page, string doc)
    {
        string line = OneLine(page: page, doc: doc);
        int end = line.IndexOf(value: ". ", comparisonType: StringComparison.Ordinal);
        return end >= 0
            ? line[..(end + 1)]
            : line;
    }

    /// <summary>
    /// Puts a blank line in front of a list, a table or a fenced block that follows a line of prose. Doc comments
    /// write them right under their lead-in line, which MkDocs would read as more of the paragraph.
    /// </summary>
    private static string SeparateBlocks(string text)
    {
        var output = new List<string>();
        BlockKind previous = BlockKind.Blank;
        bool fenced = false;
        foreach (string line in text.Split(separator: '\n'))
        {
            BlockKind kind = KindOf(line: line);
            if (kind == BlockKind.Fence)
            {
                fenced = !fenced;
                if (fenced && previous != BlockKind.Blank)
                {
                    output.Add(item: "");
                }
            }
            else if (!fenced && kind is BlockKind.List or BlockKind.Table && previous == BlockKind.Prose)
            {
                output.Add(item: "");
            }

            output.Add(item: line);
            previous = fenced
                ? BlockKind.Fence
                : kind;
        }

        return string.Join(separator: "\n", values: output);
    }

    private enum BlockKind
    {
        Blank,
        Prose,
        List,
        Table,
        Fence
    }

    private static BlockKind KindOf(string line)
    {
        string text = line.TrimStart();
        if (text.Length == 0)
        {
            return BlockKind.Blank;
        }

        if (text.StartsWith(value: "```"))
        {
            return BlockKind.Fence;
        }

        if (text.StartsWith(value: '|'))
        {
            return BlockKind.Table;
        }

        if (text.StartsWith(value: "- ") || text.StartsWith(value: "* ") ||
            Regex.IsMatch(input: text, pattern: @"^\d+\. "))
        {
            return BlockKind.List;
        }

        return BlockKind.Prose;
    }

    /// <summary>
    /// Turns each <c>{Name}</c> outside code into a link to the type's section when the library declares a type of
    /// that name, and into inline code otherwise (a parameter, an expression). Code spans and fenced blocks are left
    /// as written.
    /// </summary>
    private string Linkify(SourcePage page, string text)
    {
        var output = new List<string>();
        bool fenced = false;
        foreach (string line in text.Split(separator: '\n'))
        {
            if (line.TrimStart()
                    .StartsWith(value: "```"))
            {
                fenced = !fenced;
                output.Add(item: line);
                continue;
            }

            if (fenced)
            {
                output.Add(item: line);
                continue;
            }

            var built = new StringBuilder();
            int at = 0;
            foreach (Match span in CodeSpan.Matches(input: line))
            {
                built.Append(value: LinkifyProse(page: page, text: line[at..span.Index]));
                built.Append(value: span.Value);
                at = span.Index + span.Length;
            }

            built.Append(value: LinkifyProse(page: page, text: line[at..]));
            output.Add(item: built.ToString());
        }

        return string.Join(separator: "\n", values: output);
    }

    private string LinkifyProse(SourcePage page, string text)
    {
        return BraceReference.Replace(input: text, evaluator: match =>
        {
            string inner = match.Groups[groupnum: 1].Value.Trim();
            Match type = TypeReference.Match(input: inner);
            if (type.Success && _types.TryGetValue(key: type.Groups[groupnum: 1].Value,
                    value: out (string Page, string Anchor) target))
            {
                return $"[`{inner}`]({LinkTo(from: page, page: target.Page, anchor: target.Anchor)})";
            }

            return $"`{inner}`";
        });
    }

    private static string LinkTo(SourcePage from, string page, string anchor)
    {
        if (page == from.PagePath)
        {
            return "#" + anchor;
        }

        string directory = Path.GetDirectoryName(path: from.PagePath) is { Length: > 0 } dir
            ? dir
            : ".";
        string relative = Path.GetRelativePath(relativeTo: directory, path: page)
                              .Replace(oldChar: '\\', newChar: '/');
        return $"{relative}#{anchor}";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // INDEX PAGES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Writes an <c>index.md</c> in every folder holding pages: its subfolders, then its pages, each with
    /// the first sentence of its first doc comment.</summary>
    private void WriteIndexPages(string outputRoot, List<SourcePage> pages)
    {
        var folders = new SortedSet<string>(comparer: StringComparer.Ordinal) { "" };
        foreach (SourcePage page in pages)
        {
            string folder = Folder(path: page.PagePath);
            while (folder.Length > 0)
            {
                folders.Add(item: folder);
                folder = Folder(path: folder);
            }
        }

        string library = $"{_language} Standard Library";
        foreach (string folder in folders)
        {
            var text = new StringBuilder();
            text.Append(value: folder.Length == 0
                ? $"# {library}\n\nThe API reference of the {_language} standard library, generated from the doc comments in its sources.\n\n"
                : $"# {folder.Replace(oldChar: '/', newChar: ' ')}\n\n");

            List<string> subfolders = folders.Where(predicate: f => f.Length > 0 && Folder(path: f) == folder)
                                             .ToList();
            if (subfolders.Count > 0)
            {
                text.Append(value: "## Folders\n\n");
                foreach (string sub in subfolders)
                {
                    string name = sub[(sub.LastIndexOf(value: '/') + 1)..];
                    text.Append(value: $"- [{name}]({name}/index.md)\n");
                }

                text.Append(value: '\n');
            }

            List<SourcePage> here = pages.Where(predicate: p => Folder(path: p.PagePath) == folder)
                                         .ToList();
            if (here.Count > 0)
            {
                text.Append(value: "## Pages\n\n");
                foreach (SourcePage page in here)
                {
                    string file = Path.GetFileName(path: page.PagePath);
                    text.Append(value: page.Summary is { Length: > 0 } summary
                        ? $"- [{page.Title}]({file}) — {RelinkForIndex(summary: summary, page: page)}\n"
                        : $"- [{page.Title}]({file})\n");
                }

                text.Append(value: '\n');
            }

            string target = Path.Combine(path1: outputRoot, path2: folder, path3: "index.md");
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.WriteAllText(path: target, contents: text.ToString()
                                                         .TrimEnd() + "\n");
        }
    }

    /// <summary>A page summary sits on its folder's index, which is in the page's own folder, so its links already
    /// resolve there. A link to the same page (<c>#anchor</c>) is pointed at the page.</summary>
    private static string RelinkForIndex(string summary, SourcePage page)
    {
        return summary.Replace(oldValue: "](#", newValue: $"]({Path.GetFileName(path: page.PagePath)}#");
    }

    private static string Folder(string path)
    {
        int slash = path.LastIndexOf(value: '/');
        return slash >= 0
            ? path[..slash]
            : "";
    }
}
