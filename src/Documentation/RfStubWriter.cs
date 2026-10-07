using System.Text;
using System.Text.RegularExpressions;
using Builder.Declaration;
using Builder.Formatting;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Documentation;

/// <summary>What one run of the stub writer did.</summary>
/// <param name="Files">The stub files written.</param>
/// <param name="Types">The types bound.</param>
/// <param name="Routines">The routines bound.</param>
/// <param name="Problems">The files that could not be read, each with the reason.</param>
internal sealed record RfStubResult(int Files, int Types, int Routines, List<string> Problems);

/// <summary>
/// Writes <c>@rf("...")</c> stub files for the shared RazorForge declarations a Suflae program sees and the Suflae
/// library does not show yet: one file per RazorForge source file, at the same relative path, in the same module.
/// Each stub carries RazorForge's doc comment as a first draft, to be reworded in Suflae's words. A declaration the
/// Suflae library already binds, or a routine it replaces with one of the same call shape, is left out, so a later
/// run writes only what RazorForge added since.
/// </summary>
internal sealed class RfStubWriter
{
    /// <summary>RazorForge folders whose types a Suflae program never works with: buildtime reflection (Suflae reflects
    /// at runtime through ObjectHacker), raw memory and its handles, C structures, and SIMD lanes.</summary>
    private static readonly string[] UnsharedFolders =
        ["BuilderExpansion", "BuilderQuery", "Core/Memory", "Core/CSubsystem", "Simd"];

    /// <summary>Public RazorForge routines that only the builder's own code calls (the size of an Agent recipe
    /// slot, used when the builder lays a recipe out).</summary>
    private static readonly HashSet<string> UnsharedRoutines = new(comparer: StringComparer.Ordinal)
    {
        "recipe_slot_size"
    };

    /// <summary>RazorForge files that hold only C declarations and the builder's own helpers.</summary>
    private static readonly string[] UnsharedFiles = ["Core/NativeDeclarations.rf"];

    /// <summary>
    /// RazorForge types a Suflae program does not use: access tokens and raw memory, protocols about them, the walks
    /// and nodes of collections Suflae does not have, and the library's own helpers that RazorForge leaves public.
    /// </summary>
    private static readonly HashSet<string> UnsharedTypes = new(comparer: StringComparer.Ordinal)
    {
        "Accessing", "Controlling", "Assignable", "AtomicType", "Splittable", "CPtr", "Address", "IngridWords",
        "Hijacked", "Viewing", "Modifying", "Retained", "Tracked", "Roamed", "Guarded", "Witnessed", "Watching",
        "Consulting", "Amending", "ListEmittable", "CircularListEmittable", "SplitListEmittable",
        "SplitArrayEmittable", "BTreeDictNode", "BTreeListNode", "BTreeSetNode", "WordQuoRem", "KStep", "UDR256",
        "DD", "FloatDigits", "FloatParsed", "D32Parts", "D64Parts", "D128Parts", "ComplexTextParts",
        "IntegerMagnitude", "IntegerLimbs", "IntegerLimbView", "IntV", "Exposed", "Wielded",
        // Suflae has its own lists and no structure-of-arrays layouts.
        "SplitList", "SplitArray", "CircularList"
    };

    /// <summary>The protocols a Suflae program has no use for, left out of an <c>obeys</c> list.</summary>
    private static readonly HashSet<string> UnsharedProtocols = new(comparer: StringComparer.Ordinal)
    {
        "Assignable", "AtomicType", "Splittable", "ConstCompatible", "Accessing", "Controlling"
    };

    private static readonly Regex Word = new(pattern: @"[A-Za-z_]\w*");

    private sealed class Source
    {
        public required string Path { get; init; }
        public required SyntaxTree.Program Program { get; init; }
        public required SourceIndex Index { get; init; }
        public required string[] Lines { get; init; }
        public string? Module => Program.Declarations.OfType<ModuleDeclaration>()
                                        .FirstOrDefault()
                                       ?.Path;
    }

    private readonly string _sharedRoot;
    private readonly string _ownRoot;
    private readonly List<string> _problems = [];

    /// <summary>Creates a writer over the RazorForge library at <paramref name="sharedRoot"/> and the Suflae library
    /// at <paramref name="ownRoot"/>.</summary>
    public RfStubWriter(string sharedRoot, string ownRoot)
    {
        _sharedRoot = System.IO.Path.GetFullPath(path: sharedRoot);
        _ownRoot = System.IO.Path.GetFullPath(path: ownRoot);
    }

    /// <summary>Writes the stub files under <paramref name="outputRoot"/>. An existing file is not overwritten.</summary>
    public RfStubResult Write(string outputRoot)
    {
        List<Source> own = Read(root: _ownRoot, language: Language.Suflae);
        List<Source> shared = Read(root: _sharedRoot, language: Language.RazorForge)
                             .Where(predicate: s => !UnsharedFolders.Any(predicate: f =>
                                                        s.Path.StartsWith(value: f + "/",
                                                            comparisonType: StringComparison.Ordinal)) &&
                                                    !UnsharedFiles.Contains(value: s.Path))
                             .ToList();

        // What the Suflae library already shows: its own types (which stand in for shared ones of the same name),
        // its bindings, and its own routines by call shape.
        var ownTypes = new HashSet<string>(comparer: StringComparer.Ordinal);
        var bound = new HashSet<string>(comparer: StringComparer.Ordinal);
        var ownShapes = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (SyntaxTree.Declaration declaration in own.SelectMany(selector: s =>
                     s.Program.Declarations.OfType<SyntaxTree.Declaration>()))
        {
            if (RfBindingCheck.TargetOf(declaration: declaration) is { } target)
            {
                bound.Add(item: declaration is RoutineDeclaration r
                    ? Shape(target: target, routine: r)
                    : target);
                continue;
            }

            if (TypeName(declaration: declaration) is { } typeName)
            {
                ownTypes.Add(item: typeName);
            }
            else if (declaration is RoutineDeclaration routine)
            {
                ownShapes.Add(item: Shape(target: TargetFor(routine: routine), routine: routine));
            }
        }

        var types = new Dictionary<string, (Source Source, SyntaxTree.Declaration Declaration)>(
            comparer: StringComparer.Ordinal);
        // Every type the shared sources declare, shown or not: a creator of a type that is not shown is not shown
        // either.
        var declaredTypes = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (Source source in shared)
        {
            foreach (SyntaxTree.Declaration declaration in source.Program.Declarations.OfType<SyntaxTree.Declaration>())
            {
                if (TypeName(declaration: declaration) is not { } name)
                {
                    continue;
                }

                declaredTypes.Add(item: name);
                if (IsPublic(declaration: declaration) && !ownTypes.Contains(item: name) &&
                    !UnsharedTypes.Contains(item: name) && !types.ContainsKey(key: name))
                {
                    types[key: name] = (source, declaration);
                }
            }
        }

        int typeCount = 0;
        int routineCount = 0;
        int fileCount = 0;
        foreach (Source source in shared)
        {
            var text = new StringBuilder();
            var printer = new AstPrinter(index: source.Index);
            foreach (SyntaxTree.Declaration declaration in source.Program.Declarations.OfType<SyntaxTree.Declaration>())
            {
                if (TypeName(declaration: declaration) is { } name && types.TryGetValue(key: name, value: out var entry) &&
                    ReferenceEquals(objA: entry.Declaration, objB: declaration) && !bound.Contains(item: name))
                {
                    WriteType(text: text, source: source, printer: printer, type: declaration, name: name);
                    typeCount++;
                }
                else if (declaration is RoutineDeclaration routine && IsPublic(declaration: routine) &&
                         !routine.Name.StartsWith(value: "__") && !routine.IsDangerous &&
                         OwnerOf(routine: routine, types: types) is var owner &&
                         (owner == null || types.ContainsKey(key: owner)) &&
                         !(owner == null && routine.OwnerName != null) &&
                         !(routine.OwnerName == null && declaredTypes.Contains(item: routine.Name) &&
                           !types.ContainsKey(key: routine.Name)))
                {
                    string target = TargetFor(routine: routine);
                    string shape = Shape(target: target, routine: routine);
                    string signature = Signature(source: source, printer: printer, declaration: routine);
                    if (bound.Contains(item: shape) || ownShapes.Contains(item: shape) ||
                        UnsharedRoutines.Contains(item: target) ||
                        Word.Matches(input: signature)
                            .Any(predicate: m => UnsharedTypes.Contains(item: m.Value)) ||
                        signature.Contains(value: "steal ", comparisonType: StringComparison.Ordinal) ||
                        signature.Contains(value: "::", comparisonType: StringComparison.Ordinal))
                    {
                        continue;
                    }

                    AppendDoc(text: text, source: source, line: routine.Location.Line, indent: "");
                    text.Append(value: $"@rf(\"{target}\")\n{CleanSignature(signature: signature)}\n\n");
                    routineCount++;
                }
            }

            if (text.Length == 0)
            {
                continue;
            }

            string file = System.IO.Path.Combine(path1: outputRoot,
                path2: System.IO.Path.ChangeExtension(path: source.Path, extension: ".sf"));
            if (File.Exists(path: file))
            {
                _problems.Add(item: $"{file}: already there, not overwritten");
                continue;
            }

            Directory.CreateDirectory(path: System.IO.Path.GetDirectoryName(path: file)!);
            File.WriteAllText(path: file,
                contents: $"module {source.Module}\n\n# Each declaration below creates nothing: `@rf` names the shared " +
                          "declaration it shows, and validate-stdlib\n# checks that the two still match.\n\n" +
                          text.ToString()
                                                                    .TrimEnd() + "\n");
            fileCount++;
        }

        return new RfStubResult(Files: fileCount, Types: typeCount, Routines: routineCount, Problems: _problems);
    }

    private List<Source> Read(string root, Language language)
    {
        var sources = new List<Source>();
        string extension = Builder.Frontends.Languages.For(language: language)
                                  .FileExtension;
        foreach (string file in Directory.EnumerateFiles(path: root, searchPattern: "*" + extension,
                                             searchOption: SearchOption.AllDirectories)
                                         .Order(comparer: StringComparer.Ordinal))
        {
            string relative = System.IO.Path.GetRelativePath(relativeTo: root, path: file)
                                    .Replace(oldChar: '\\', newChar: '/');
            string text = SourceFormatter.Normalize(source: File.ReadAllText(path: file));
            try
            {
                SourceFormatter.ParsedFile parsed = SourceFormatter.ParseFile(source: text, fileName: file,
                    language: language, what: "The file");
                sources.Add(item: new Source
                {
                    Path = relative,
                    Program = parsed.Program,
                    Index = parsed.Index,
                    Lines = text.Split(separator: '\n')
                });
            }
            catch (FormatRefusedException e)
            {
                _problems.Add(item: $"{relative}: {e.Reason}");
            }
        }

        return sources;
    }

    private void WriteType(StringBuilder text, Source source, AstPrinter printer, SyntaxTree.Declaration type,
        string name)
    {
        AppendDoc(text: text, source: source, line: type.Location.Line, indent: "");
        text.Append(value: $"@rf(\"{name}\")\n");
        string header = CleanSignature(signature: Signature(source: source, printer: printer, declaration: type));
        text.Append(value: header)
            .Append(value: '\n');

        var body = new List<string>();
        List<SyntaxTree.Declaration>? members = type switch
        {
            RecordDeclaration record => record.Members,
            EntityDeclaration entity => entity.Members,
            CrashableDeclaration crashable => crashable.Members,
            _ => null
        };
        foreach (VariableDeclaration field in members?.OfType<VariableDeclaration>()
                                                     .Where(predicate: f => IsPublic(declaration: f)) ?? [])
        {
            body.AddRange(collection: DocLines(source: source, line: field.Location.Line, indent: "    "));
            body.Add(item: "    " + StripComment(line: source.Lines[field.Location.Line - 1]
                                                              .Trim()));
        }

        switch (type)
        {
            case ChoiceDeclaration choice:
                foreach (ChoiceCase choiceCase in choice.Cases)
                {
                    body.AddRange(collection: DocLines(source: source, line: choiceCase.Location.Line, indent: "    "));
                    body.Add(item: "    " + StripComment(line: source.Lines[choiceCase.Location.Line - 1]
                                                                       .Trim()));
                }

                break;
            case FlagsDeclaration flags:
                body.AddRange(collection: flags.Members.Select(selector: f => "    " + f));
                break;
            case VariantDeclaration variant:
                body.AddRange(collection: variant.Members.Select(selector: m =>
                    "    " + StripComment(line: source.Lines[m.Location.Line - 1]
                                                    .Trim())));
                break;
            case ProtocolDeclaration protocol:
                foreach (RoutineSignature signature in protocol.MemberRoutines)
                {
                    body.AddRange(collection: DocLines(source: source, line: signature.Location.Line, indent: "    "));
                    string printed;
                    try
                    {
                        printed = printer.PrintSignature(signature: signature);
                    }
                    catch (FormatRefusedException)
                    {
                        printed = source.Lines[signature.Location.Line - 1]
                                        .Trim();
                    }

                    body.AddRange(collection: CleanSignature(signature: printed)
                                             .Split(separator: '\n')
                                             .Select(selector: l => "    " + l));
                }

                break;
        }

        if (body.Count == 0)
        {
            body.Add(item: "    pass");
        }

        text.Append(value: string.Join(separator: "\n", values: body))
            .Append(value: "\n\n");
    }

    /// <summary>A printed signature without annotations, without <c>dangerous</c>, and with the protocols a Suflae
    /// program has no use for taken out of its <c>obeys</c> list.</summary>
    private static string CleanSignature(string signature)
    {
        var lines = new List<string>();
        foreach (string raw in signature.Split(separator: '\n'))
        {
            string trimmed = raw.TrimStart();
            if (trimmed.StartsWith(value: '@'))
            {
                continue;
            }

            string line = raw.Replace(oldValue: "dangerous ", newValue: "");
            // A bundle is RazorForge's spelling of a record whose members are laid out in place: to Suflae a record.
            if (trimmed.StartsWith(value: "bundle ", comparisonType: StringComparison.Ordinal))
            {
                line = raw[..(raw.Length - trimmed.Length)] + "record " + trimmed["bundle ".Length..];
            }
            if (trimmed.StartsWith(value: "obeys ", comparisonType: StringComparison.Ordinal))
            {
                string indent = raw[..(raw.Length - trimmed.Length)];
                List<string> kept = trimmed["obeys ".Length..]
                                   .Split(separator: ',')
                                   .Select(selector: p => p.Trim())
                                   .Where(predicate: p => !UnsharedProtocols.Contains(item: Word.Match(input: p).Value))
                                   .ToList();
                if (kept.Count == 0)
                {
                    continue;
                }

                line = $"{indent}obeys {string.Join(separator: ", ", values: kept)}";
            }

            lines.Add(item: line);
        }

        return string.Join(separator: "\n", values: lines);
    }

    private static string StripComment(string line)
    {
        int hash = line.IndexOf(value: " #", comparisonType: StringComparison.Ordinal);
        return hash >= 0
            ? line[..hash]
                .TrimEnd()
            : line;
    }

    private static void AppendDoc(StringBuilder text, Source source, int line, string indent)
    {
        foreach (string doc in DocLines(source: source, line: line, indent: indent))
        {
            text.Append(value: doc)
                .Append(value: '\n');
        }
    }

    /// <summary>The <c>###</c> lines right above <paramref name="line"/>, past annotation lines, re-indented.</summary>
    private static List<string> DocLines(Source source, int line, string indent)
    {
        var lines = new List<string>();
        for (int at = line - 2; at >= 0; at--)
        {
            string text = source.Lines[at]
                                .Trim();
            if (text.StartsWith(value: "###"))
            {
                lines.Add(item: indent + text);
            }
            else if (!text.StartsWith(value: '@'))
            {
                break;
            }
        }

        lines.Reverse();
        return lines;
    }

    private static string Signature(Source source, AstPrinter printer, SyntaxTree.Declaration declaration)
    {
        try
        {
            return printer.PrintSignature(declaration: declaration);
        }
        catch (FormatRefusedException)
        {
            return source.Lines[declaration.Location.Line - 1]
                         .Trim();
        }
    }

    /// <summary>The name an <c>@rf</c> binding of the routine gives: <c>Type.name</c> for a member routine, the type's
    /// name for a creator, the name alone for a free routine.</summary>
    private static string TargetFor(RoutineDeclaration routine)
    {
        return routine.OwnerName != null
            ? $"{TypeSymbolName(name: routine.OwnerName)}.{routine.MemberRoutineName ?? routine.Name}"
            : routine.Name;
    }

    private static string TypeSymbolName(string name)
    {
        int bracket = name.IndexOf(value: '[');
        return bracket >= 0
            ? name[..bracket]
            : name;
    }

    /// <summary>A routine's call shape under its binding name: the name and the parameter names without <c>me</c>.</summary>
    private static string Shape(string target, RoutineDeclaration routine)
    {
        IEnumerable<string> names = routine.Parameters.Where(predicate: p => p.Name != "me")
                                           .Select(selector: p => p.Name);
        return $"{target}({string.Join(separator: ",", values: names)})";
    }

    private static string? OwnerOf(RoutineDeclaration routine,
        Dictionary<string, (Source Source, SyntaxTree.Declaration Declaration)> types)
    {
        if (routine.OwnerName != null)
        {
            string owner = TypeSymbolName(name: routine.OwnerName);
            return types.ContainsKey(key: owner)
                ? owner
                : null;
        }

        return types.ContainsKey(key: routine.Name)
            ? routine.Name
            : null;
    }

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
            _ => VisibilityModifier.Open
        };
        return visibility != VisibilityModifier.Secret;
    }
}
