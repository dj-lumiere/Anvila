using System.Text;
using System.Text.Json;
using Builder.Diagnostics;
using Builder.Parser;
using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Builder.Verification;
using Builder.Verification.Results;

namespace Builder.Execution;

/// <summary>
/// The Language Server Protocol engine RazorForge and Suflae share, spoken over stdio. Each language runs its own
/// server (<c>razorforge lsp</c>, <c>suflae lsp</c>) with its own <see cref="Builder.Frontends.LanguageServerProfile"/>
/// (keywords, names, formatter); the engine serves only that language's files. On every document open/change it runs the real tokenizer → parser →
/// semantic analyzer and reports positioned diagnostics; the analyzed AST + tokens are then reused
/// to serve hover (type / routine signature / variable binding), go-to-definition (routines
/// cross-file, variables/parameters scope-precise), references and rename (binding-precise via the
/// stamped <see cref="IdentifierExpression.ResolvedVariable"/>), completion (members after <c>.</c>,
/// else keywords / free routines / file declarations) with resolve, signature help, and semantic
/// tokens. Analysis reuses a pre-analyzed stdlib snapshot (captured once per language), so each
/// keystroke re-analyzes only the user file in microseconds rather than reloading the whole stdlib —
/// the same in-RAM-stdlib model the fast-rebuild dev loop needs.
///
/// Framing is LSP's <c>Content-Length</c>-delimited JSON-RPC 2.0. stdout carries ONLY protocol
/// bytes; every other write (the pipeline's own Console output) is redirected to stderr so it
/// cannot corrupt the channel.
/// </summary>
public static class LspServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Repeated JSON property name constants (avoids repeated string literals flagged by S1192).
    private const string PropParams = "params";
    private const string PropTextDocument = "textDocument";
    private const string PropMarkdown = "markdown";
    private const string PropValue = "value";
    private const string PropRange = "range";
    private const string PropStart = "start";
    private const string PropCharacter = "character";
    private const string PropLabel = "label";
    private const string PropDocumentation = "documentation";
    private const string SuffixDeclaration = "Declaration";
    private const string NodeRoutineDeclaration = "RoutineDeclaration";

    /// <summary>A language's pre-analyzed stdlib: the snapshot user files are analyzed against, and the analyzed
    /// syntax tree of every stdlib file analyzed so far, by its full path (a stdlib file is shown through it).</summary>
    private sealed record StdlibCapture(
        TypeRegistry.StdlibSnapshot Snapshot,
        System.Collections.Concurrent.ConcurrentDictionary<string, SyntaxTree.Program> Programs);

    // One pre-analyzed stdlib per language, captured on first use.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Language,
        Lazy<StdlibCapture>> Captures = new();

    private static StdlibCapture CaptureFor(Language language)
    {
        return Captures.GetOrAdd(key: language,
            valueFactory: lang => new Lazy<StdlibCapture>(valueFactory: () =>
            {
                (TypeRegistry.StdlibSnapshot snapshot, List<(SyntaxTree.Program Program, string FilePath)> programs) =
                    SemanticVerifier.CaptureStdlibSnapshotWithPrograms(language: lang);
                var byPath = new System.Collections.Concurrent.ConcurrentDictionary<string, SyntaxTree.Program>(
                    comparer: StringComparer.OrdinalIgnoreCase);
                foreach ((SyntaxTree.Program program, string filePath) in programs)
                {
                    byPath.TryAdd(key: Path.GetFullPath(path: filePath), value: program);
                }

                return new StdlibCapture(Snapshot: snapshot, Programs: byPath);
            })).Value;
    }

    private static TypeRegistry.StdlibSnapshot SnapshotFor(Language language)
    {
        return CaptureFor(language: language)
           .Snapshot;
    }

    /// <summary>
    /// The analyzed syntax tree of a stdlib file (by the path of the copy the builder reads). The capture holds the
    /// files it analyzed, the always-loaded ones. Any other module is analyzed once on first need, by analyzing an
    /// import of it against the snapshot, the way a user file's import loads it.
    /// </summary>
    private static SyntaxTree.Program? AnalyzedStdlibProgram(Language language, string copyPath, string? module)
    {
        StdlibCapture capture = CaptureFor(language: language);
        if (capture.Programs.TryGetValue(key: copyPath, value: out SyntaxTree.Program? known) || module == null)
        {
            return known;
        }

        var importer = new SemanticVerifier(language: language, snapshot: capture.Snapshot) { SaOnly = true };
        const string importerName = "__lsp_stdlib_import__";
        List<Token> tokens = Builder.Tokenizer.Lexers.Tokenize(source: $"module {importerName}\nimport {module}\n",
            fileName: importerName,
            language: language);
        importer.Analyze(program: new Builder.Parser.Parser(tokens: tokens, language: language, fileName: importerName)
                                    .Parse());
        foreach ((SyntaxTree.Program program, string filePath, _) in importer.Registry.StdlibPrograms)
        {
            capture.Programs.TryAdd(key: Path.GetFullPath(path: filePath), value: program);
        }

        return capture.Programs.GetValueOrDefault(key: copyPath);
    }

    /// <summary>The last analyzed state of an open document, kept so hover/definition/completion reuse it.</summary>
    private sealed record DocState(
        SyntaxTree.Program Program,
        List<Token> Tokens,
        Language Lang,
        TypeRegistry Registry);

    // Open documents by URI: their most recent typed AST + token stream (for hover).
    private static readonly Dictionary<string, DocState> Docs = new();

    // The language this server serves (set by Run): its keywords, names, and which files are its own.
    private static Builder.Frontends.LanguageServerProfile _profile = null!;

    /// <summary>The rules of the language this server serves.</summary>
    private static Builder.Frontends.LanguageRules Rules => Builder.Frontends.Languages.For(language: _profile.Language);

    /// <summary>
    /// A type as its reader writes it: short names without module paths (<c>List[S64]</c>, not
    /// <c>List[Core.S64]</c>), in the words of the served language (a Suflae entity without its handle), at every
    /// depth of its arguments.
    /// </summary>
    private static string TypeText(TypeSymbol type)
    {
        TypeSymbol shown = Rules.SurfaceType(type: type);
        if (shown.TypeArguments is { Count: > 0 } args)
        {
            return $"{ShortName(name: shown.BareName)}[{string.Join(separator: ", ", values: args.Select(selector: TypeText))}]";
        }

        // A type whose arguments are spelled only in its name (a tuple, a routine type) loses its module paths there.
        return shown.Name.Contains(value: '[')
            ? Surface(text: ModulePath.Replace(input: shown.Name, replacement: ""))
            : ShortName(name: shown.Name);
    }

    /// <summary>A type name without its module path (<c>Core.S64</c> reads <c>S64</c>).</summary>
    private static string ShortName(string name)
    {
        return name[(name.LastIndexOf(value: '.') + 1)..];
    }

    // The module path in front of a type name inside a spelled-out type (`Core.` in `Tuple[Core.S64, Text]`).
    private static readonly System.Text.RegularExpressions.Regex ModulePath =
        new(pattern: @"(?<![\w.])(?:[A-Za-z_]\w*\.)+(?=[A-Za-z_])");

    /// <summary>Text shown to the user, in the words of the served language (see
    /// <see cref="Builder.Frontends.LanguageRules.SurfaceText"/>): a Suflae user never sees the handle type an
    /// entity is carried in.</summary>
    private static string Surface(string text)
    {
        return Rules.SurfaceText(text: text);
    }

    // The text of each open document as the client last sent it (formatting rewrites from it).
    private static readonly Dictionary<string, string> Texts = new();

    /// <summary>
    /// Runs the LSP server, reading JSON-RPC 2.0 messages from stdin and writing responses to
    /// stdout. Redirects all pipeline <c>Console.Write</c> output to stderr so it cannot corrupt
    /// the LSP framing. Returns 0 on a clean shutdown (client sent <c>shutdown</c> then
    /// <c>exit</c>), or 1 if <c>exit</c> arrives without a prior <c>shutdown</c>.
    /// </summary>
    public static int Run(Builder.Frontends.LanguageServerProfile profile)
    {
        // The protocol owns the real stdout; send stray pipeline output to stderr instead.
        Stream stdout = Console.OpenStandardOutput();
        Stream stdin = Console.OpenStandardInput();
        Console.SetOut(newOut: Console.Error);
        return Run(stdin: stdin, stdout: stdout, profile: profile);
    }

    /// <summary>
    /// Core JSON-RPC loop, parameterized over the transport streams so tests can drive the full
    /// request/response surface in-process by feeding LSP-framed messages through a
    /// <see cref="MemoryStream"/> and reading the framed replies back. The public
    /// <see cref="Run()"/> binds these to the process stdin/stdout.
    /// </summary>
    internal static int Run(Stream stdin, Stream stdout, Builder.Frontends.LanguageServerProfile profile)
    {
        // Fresh transport = fresh document set; a real server starts with none open, and clearing
        // here keeps successive in-process test sessions from leaking state into one another.
        Docs.Clear();
        Texts.Clear();
        _profile = profile;

        bool shutdownRequested = false;
        while (true)
        {
            byte[]? body = ReadMessage(stdin: stdin);
            if (body == null)
            {
                break; // EOF
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(utf8Json: body);
            }
            catch (JsonException)
            {
                continue; // malformed frame — skip
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                string method =
                    root.TryGetProperty(propertyName: "method", value: out JsonElement m)
                        ? m.GetString() ?? ""
                        : "";
                bool hasId = root.TryGetProperty(propertyName: "id", value: out JsonElement id);

                switch (method)
                {
                    case "initialize":
                        WriteResult(stdout: stdout,
                            id: id,
                            result: new Dictionary<string, object?>
                            {
                                [key: "capabilities"] = new Dictionary<string, object?>
                                {
                                    // 1 = Full document sync: didChange carries the whole text.
                                    [key: "textDocumentSync"] =
                                        new Dictionary<string, object?>
                                        {
                                            [key: "openClose"] = true, [key: "change"] = 1
                                        },
                                    [key: "hoverProvider"] = true,
                                    [key: "definitionProvider"] = true,
                                    [key: "referencesProvider"] = true,
                                    [key: "renameProvider"] =
                                        new Dictionary<string, object?>
                                        {
                                            [key: "prepareProvider"] = true
                                        },
                                    [key: "completionProvider"] =
                                        new Dictionary<string, object?>
                                        {
                                            [key: "triggerCharacters"] = new List<object?> { "." },
                                            [key: "resolveProvider"] = true
                                        },
                                    [key: "signatureHelpProvider"] =
                                        new Dictionary<string, object?>
                                        {
                                            [key: "triggerCharacters"] =
                                                new List<object?> { "(", "," }
                                        },
                                    [key: "documentSymbolProvider"] = true,
                                    [key: "workspaceSymbolProvider"] = true,
                                    [key: "inlayHintProvider"] = true,
                                    [key: "codeActionProvider"] = true,
                                    [key: "documentFormattingProvider"] = true,
                                    [key: "semanticTokensProvider"] =
                                        new Dictionary<string, object?>
                                        {
                                            [key: "legend"] = new Dictionary<string, object?>
                                            {
                                                [key: "tokenTypes"] = SemanticTokenTypes,
                                                [key: "tokenModifiers"] = SemanticTokenModifiers
                                            },
                                            [key: "full"] = true
                                        }
                                },
                                [key: "serverInfo"] = new Dictionary<string, object?>
                                {
                                    [key: "name"] = _profile.ServerName, [key: "version"] = "0.1"
                                }
                            });
                        break;

                    case "shutdown":
                        shutdownRequested = true;
                        WriteResult(stdout: stdout, id: id, result: null);
                        break;

                    case "exit":
                        return shutdownRequested
                            ? 0
                            : 1;

                    case "textDocument/didOpen":
                        HandleDidOpenOrChange(stdout: stdout, root: root, isOpen: true);
                        break;

                    case "textDocument/didChange":
                        HandleDidOpenOrChange(stdout: stdout, root: root, isOpen: false);
                        break;

                    case "textDocument/didClose":
                        HandleDidClose(stdout: stdout, root: root);
                        break;

                    case "textDocument/hover":
                        HandleHover(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/definition":
                        HandleDefinition(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/references":
                        HandleReferences(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/completion":
                        HandleCompletion(stdout: stdout, id: id, root: root);
                        break;

                    case "completionItem/resolve":
                        HandleCompletionResolve(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/signatureHelp":
                        HandleSignatureHelp(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/prepareRename":
                        HandlePrepareRename(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/rename":
                        HandleRename(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/documentSymbol":
                        HandleDocumentSymbol(stdout: stdout, id: id, root: root);
                        break;

                    case "workspace/symbol":
                        HandleWorkspaceSymbol(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/inlayHint":
                        HandleInlayHint(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/codeAction":
                        HandleCodeAction(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/semanticTokens/full":
                        HandleSemanticTokens(stdout: stdout, id: id, root: root);
                        break;

                    case "textDocument/formatting":
                        HandleFormatting(stdout: stdout, id: id, root: root);
                        break;

                    default:
                        // Unknown REQUEST (has id) — answer with an empty result so the client
                        // does not stall. Unknown NOTIFICATIONS are simply ignored.
                        if (hasId)
                        {
                            WriteResult(stdout: stdout, id: id, result: null);
                        }

                        break;
                }
            }
        }

        return 0;
    }

    private static void HandleDidOpenOrChange(Stream stdout, JsonElement root, bool isOpen)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl))
        {
            return;
        }

        string uri = uriEl.GetString() ?? "";
        // A file of the other language belongs to the other language's server.
        if (!_profile.Serves(uriOrFileName: uri))
        {
            return;
        }

        string? text;
        if (isOpen)
        {
            text = td.TryGetProperty(propertyName: "text", value: out JsonElement t)
                ? t.GetString()
                : null;
        }
        else
        {
            text = ExtractFullChangeText(paramsEl: p);
        }

        if (text == null)
        {
            return;
        }

        Texts[key: uri] = text;
        List<Dictionary<string, object?>> diagnostics = Analyze(uri: uri, text: text);
        PublishDiagnostics(stdout: stdout, uri: uri, diagnostics: diagnostics);
    }

    /// <summary>
    /// <c>textDocument/formatting</c>: the whole document in the language's canonical layout, as one edit
    /// replacing all of it. No edits when the text is already formatted, or when the language's formatter
    /// refuses the file (it reproduces a file exactly or not at all).
    /// </summary>
    private static void HandleFormatting(Stream stdout, JsonElement id, JsonElement root)
    {
        string uri = root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) &&
                     p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) &&
                     td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl)
            ? uriEl.GetString() ?? ""
            : "";
        var edits = new List<object?>();
        if (Texts.TryGetValue(key: uri, value: out string? text) &&
            _profile.Format(text: text, fileName: UriToFileName(uri: uri)) is { } formatted && formatted != text)
        {
            // An end past the last line clamps to the document's end, so the edit covers all of it.
            int lines = text.Count(predicate: c => c == '\n') + 1;
            edits.Add(item: new Dictionary<string, object?>
            {
                [key: PropRange] = new Dictionary<string, object?>
                {
                    [key: PropStart] = new Dictionary<string, object?> { [key: "line"] = 0, [key: PropCharacter] = 0 },
                    [key: "end"] = new Dictionary<string, object?> { [key: "line"] = lines, [key: PropCharacter] = 0 }
                },
                [key: "newText"] = formatted
            });
        }

        WriteResult(stdout: stdout, id: id, result: edits);
    }

    private static void HandleDidClose(Stream stdout, JsonElement root)
    {
        if (root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) &&
            p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) &&
            td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl))
        {
            string uri = uriEl.GetString() ?? "";
            Docs.Remove(key: uri);
            Texts.Remove(key: uri);
            PublishDiagnostics(stdout: stdout,
                uri: uri,
                diagnostics: new List<Dictionary<string, object?>>());
        }
    }

    /// <summary>
    /// <c>textDocument/hover</c>: report the resolved type of the expression under the cursor. Reuses
    /// the last analysis of the document (no re-parse) — finds the token at the position, then the
    /// typed expression anchored at it, and renders its type as a RazorForge code block.
    /// </summary>
    private static void HandleHover(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        if (DocReferenceAt(doc: doc, uri: uri, line0: line0, char0: char0) is { } reference)
        {
            string referenceValue = $"```{_profile.CodeBlockLanguage}\n{Surface(text: reference.Label)}\n```";
            if (!string.IsNullOrWhiteSpace(value: reference.Documentation))
            {
                referenceValue += $"\n\n{Surface(text: RenderDoc(doc: reference.Documentation))}";
            }

            WriteResult(stdout: stdout,
                id: id,
                result: new Dictionary<string, object?>
                {
                    [key: "contents"] = new Dictionary<string, object?>
                    {
                        [key: "kind"] = PropMarkdown, [key: PropValue] = referenceValue
                    },
                    [key: PropRange] = new Dictionary<string, object?>
                    {
                        [key: PropStart] = new Dictionary<string, object?>
                        {
                            [key: "line"] = reference.Line, [key: PropCharacter] = reference.Start
                        },
                        [key: "end"] = new Dictionary<string, object?>
                        {
                            [key: "line"] = reference.Line, [key: PropCharacter] = reference.End
                        }
                    }
                });
            return;
        }

        Token? hit = TokenAt(doc: doc, line0: line0, char0: char0);
        if (hit == null)
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        Expression? best = BestTypedExpression(program: doc.Program,
            line1: line0 + 1,
            col1: char0 + 1,
            hit: hit);

        // Prefer a richer label when the token names a known kind of symbol (a routine, a type, a case, a field, a
        // variable), with the doc comment written above its declaration; otherwise the expression's resolved type.
        string? label = null;
        string? documentation = null;
        string? notes = null;
        if (IsIdentifierText(text: hit.Text))
        {
            (label, documentation, notes) = SymbolHoverLabel(doc: doc, hit: hit);
        }

        if (label == null)
        {
            string? resolved = ResolveHoverFallbackLabel(doc: doc, hit: hit, best: best);
            if (resolved == null)
            {
                WriteResult(stdout: stdout, id: id, result: null);
                return;
            }

            label = resolved;
        }

        int endCol0 = hit.Column - 1 + hit.Text.Length;
        string hoverValue = $"```{_profile.CodeBlockLanguage}\n{Surface(text: label)}\n```";

        // What each generic parameter stands for belongs with the signature, before the doc that explains it.
        List<(string Parameter, TypeSymbol Argument)> bindings = GenericBindingsAt(doc: doc, hit: hit, best: best)
                                                                .Where(predicate: b => !(b.Argument is GenericParameterTypeSymbol self &&
                                                                    self.Name == b.Parameter))
                                                                .ToList();
        if (bindings.Count > 0)
        {
            hoverValue += "\n\n" + string.Join(separator: "  \n",
                values: bindings.Select(selector: b =>
                    Surface(text: $"`{b.Parameter}` is `{TypeText(type: b.Argument)}`")));
        }

        if (!string.IsNullOrWhiteSpace(value: documentation))
        {
            hoverValue += $"\n\n{Surface(text: RenderDoc(doc: documentation))}";
        }

        if (!string.IsNullOrWhiteSpace(value: notes))
        {
            hoverValue += $"\n\n{Surface(text: notes)}";
        }

        WriteResult(stdout: stdout,
            id: id,
            result: new Dictionary<string, object?>
            {
                [key: "contents"] =
                    new Dictionary<string, object?>
                    {
                        [key: "kind"] = PropMarkdown, [key: PropValue] = hoverValue
                    },
                [key: PropRange] = new Dictionary<string, object?>
                {
                    [key: PropStart] =
                        new Dictionary<string, object?>
                        {
                            [key: "line"] = line0,
                            [key: PropCharacter] = hit.Column - 1
                        },
                    [key: "end"] = new Dictionary<string, object?>
                    {
                        [key: "line"] = line0, [key: PropCharacter] = endCol0
                    }
                }
            });
    }

    /// <summary>The best typed expression on the cursor line: the one anchored exactly at the hit token,
    /// or the closest-starting expression at or before the cursor column if no exact match.</summary>
    private static Expression? BestTypedExpression(SyntaxTree.Program program, int line1, int col1,
        Token hit)
    {
        var typed = AllNodes(program: program)
                   .OfType<Expression>()
                   .Where(predicate: e => e.ResolvedType != null && e.Location.Line == line1)
                   .ToList();

        // `me.balance` and `me` start at the same column: hovering `me` means the identifier.
        if (typed.OfType<IdentifierExpression>()
                 .FirstOrDefault(predicate: e => e.Location.Column == hit.Column && e.Name == hit.Text) is { } named)
        {
            return named;
        }

        Expression? best = null;
        foreach (Expression e in typed)
        {
            if (e.Location.Column == hit.Column)
            {
                return e; // exact anchor — the identifier/call at the cursor
            }

            if (e.Location.Column <= col1 &&
                (best == null || e.Location.Column > best.Location.Column))
            {
                best = e; // closest-starting enclosing expression as a fallback
            }
        }

        return best;
    }

    /// <summary>When no symbol-specific label was produced, falls back to the expression's resolved type.
    /// Returns null if the token is a realm qualifier (which has no value type) or if the best expression
    /// has no resolved type.</summary>
    private static string? ResolveHoverFallbackLabel(DocState doc, Token hit, Expression? best)
    {
        // A realm tag (`C`/`LLVM`/`RF`/`SF` before `::`) is a qualifier, not a value — don't report it
        // as the return type. If the qualified call resolved, the routine branch already handled it.
        if (IsRealmQualifier(doc: doc, hit: hit))
        {
            return null;
        }

        if (best?.ResolvedType == null)
        {
            return null;
        }

        string typeName = TypeText(type: best.ResolvedType);
        return hit.Type == TokenType.Identifier
            ? $"{hit.Text}: {typeName}"
            : typeName;
    }

    /// <summary>
    /// The richer hover label for an identifier token, the doc comment of what it names (raw, rendered by the
    /// caller), and notes (markdown): a routine's signature; a type, by its kind; a case of a choice or flags; a
    /// field; a variable or parameter (a parameter's doc is its <c>:param:</c> line in its routine's doc). All null
    /// when the token names none of them (the caller then falls back to the resolved expression type).
    /// </summary>
    private static (string? Label, string? Documentation, string? Notes) SymbolHoverLabel(DocState doc, Token hit)
    {
        // A creator call (`List[S64]()`, `Point(x: 1)`) shows the type it makes, below, as C# shows a constructor's type.
        RoutineInfo? routine = RoutineReferencedByToken(doc: doc, hit: hit) ?? RoutineDeclaredAtToken(doc: doc, hit: hit);
        if (routine is { Kind: not RoutineKind.Creator })
        {
            return (RoutineHeader(routine: routine), routine.Documentation ?? DocAbove(location: routine.Location), null);
        }

        SemanticRoles roles = BuildSemanticRoles(nodes: AllNodes(program: doc.Program), tokens: doc.Tokens,
            registry: doc.Registry);
        (int, int) key = (hit.Line, hit.Column);
        if (roles.Cases.TryGetValue(key: key, value: out (TypeSymbol Owner, string Case) found))
        {
            return ($"{found.Case}: {TypeText(type: found.Owner)}{CaseValue(owner: found.Owner, name: found.Case)}",
                CaseDoc(owner: found.Owner, name: found.Case), null);
        }

        if (roles.Symbols.TryGetValue(key: key, value: out TypeSymbol? type))
        {
            (string typeLabel, string? typeDoc, _) = TypeHoverLabel(type: type);
            return (typeLabel, typeDoc, null);
        }

        if (FieldAtToken(doc: doc, hit: hit) is { } field)
        {
            return ($"{field.Name}: {TypeText(type: field.Type)}", DocAbove(location: field.Location), null);
        }

        // A parameter where its routine's header declares it (a parameter's own location is not its name's).
        foreach (RoutineDeclaration declaring in AllNodes(program: doc.Program)
                    .OfType<RoutineDeclaration>()
                    .Where(predicate: r => r.Location.Line == hit.Line && hit.Type == TokenType.Identifier))
        {
            if (declaring.Parameters.FirstOrDefault(predicate: p => p.Name == hit.Text) is { } parameter &&
                declaring.ResolvedInfo?.Parameters.FirstOrDefault(predicate: p => p.Name == parameter.Name) is { } info)
            {
                string? routineDoc = declaring.ResolvedInfo.Documentation ?? declaring.Documentation ??
                                     DocAbove(location: declaring.Location);
                string? paramDoc = routineDoc == null
                    ? null
                    : ParseDoc(doc: routineDoc)
                     .Params.FirstOrDefault(predicate: x => x.Name == parameter.Name)
                     .Desc;
                return ($"{parameter.Name}: {TypeText(type: info.Type)}", null,
                    string.IsNullOrWhiteSpace(value: paramDoc)
                        ? null
                        : paramDoc);
            }
        }

        VariableInfo? bound = VariableBoundAtToken(doc: doc, hit: hit);
        if (bound == null)
        {
            return DeclaredVariableAtToken(doc: doc, hit: hit) is { } declared
                ? ($"{declared.Name}: {TypeText(type: declared.Type)}", DocAbove(location: declared.Location), null)
                : (null, null, null);
        }

        string kindNote = bound.IsPreset
            ? "preset "
            : "";
        string label = $"{kindNote}{bound.Name}: {TypeText(type: bound.Type)}";

        // Ownership state: is this exact occurrence dead (moved out by an earlier steal)?
        bool deadHere = AllNodes(program: doc.Program)
                       .OfType<IdentifierExpression>()
                       .Any(predicate: e => e.IsDeadUse && e.Name == hit.Text &&
                                            e.Location.Line == hit.Line &&
                                            e.Location.Column == hit.Column);
        var notes = new List<string>();
        if (bound.IsParameter && ParameterDoc(doc: doc, parameter: bound) is { } described)
        {
            notes.Add(item: described);
        }

        if (deadHere)
        {
            notes.Add(item: "⚠️ **moved out** — this value's ownership was transferred by an " +
                            "earlier `steal`; it is dead here (use-after-steal) until re-assigned.");
        }

        if (OwnershipNote(type: bound.Type) is { } own)
        {
            notes.Add(item: own);
        }

        return (label,
            bound.IsParameter
                ? null
                : DocAbove(location: bound.Location),
            notes.Count > 0
                ? string.Join(separator: "\n\n", values: notes)
                : null);
    }

    /// <summary>A type's hover: its declaration's header as written (or its kind and name), the doc above it, and
    /// where it is declared.</summary>
    private static (string Label, string? Documentation, SourceLocation? Location) TypeHoverLabel(TypeSymbol type)
    {
        TypeSymbol shown = Rules.SurfaceType(type: type);
        TypeSymbol definition = DefinitionOf(type: shown);
        string? kind = KindWord(type: definition);
        string name = HeaderText(type: definition);
        SourceLocation? declaredAt = DeclarationOf(type: definition);
        return (SourceHeader(location: declaredAt) ??
                (kind != null
                    ? $"{kind} {name}"
                    : name) + DeclarationClauses(type: definition),
            DocAbove(location: declaredAt), declaredAt);
    }

    /// <summary>What a <c>{Name}</c> or <c>{Owner.member}</c> reference in a <c>###</c> doc comment names: the hover
    /// label and doc of the declaration, where it is, and the reference's span on its line (0-based).</summary>
    private sealed record DocReferenceTarget(
        string Label,
        string? Documentation,
        SourceLocation? Location,
        int Line,
        int Start,
        int End);

    /// <summary>The doc reference under the cursor, resolved as the document's module sees its names.</summary>
    private static DocReferenceTarget? DocReferenceAt(DocState doc, string uri, int line0, int char0)
    {
        if (!Texts.TryGetValue(key: uri, value: out string? text))
        {
            return null;
        }

        string[] lines = text.ReplaceLineEndings(replacementText: "\n")
                             .Split(separator: '\n');
        if (line0 >= lines.Length)
        {
            return null;
        }

        string line = lines[line0];
        int marker = line.IndexOf(value: "###", comparisonType: StringComparison.Ordinal);
        if (marker < 0 || line[..marker].Trim().Length > 0)
        {
            return null;
        }

        foreach (System.Text.RegularExpressions.Match m in DocReference.Matches(input: line, startat: marker + 3))
        {
            if (m.Index <= char0 && char0 < m.Index + m.Length)
            {
                return ResolveDocReference(doc: doc, reference: m.Value[1..^1].Trim()) is { } found
                    ? found with { Line = line0, Start = m.Index, End = m.Index + m.Length }
                    : null;
            }
        }

        return null;
    }

    /// <summary><c>Name</c> (a type or a free routine) or <c>Owner.member</c> (a member routine, a case, or a field
    /// of the owner type; <c>List[T].add_last</c>).</summary>
    private static DocReferenceTarget? ResolveDocReference(DocState doc, string reference)
    {
        int dot = -1;
        int depth = 0;
        for (int i = reference.Length - 1; i >= 0 && dot < 0; i--)
        {
            depth += reference[index: i] switch
            {
                ']' => 1,
                '[' => -1,
                _ => 0
            };
            if (reference[index: i] == '.' && depth == 0)
            {
                dot = i;
            }
        }

        string ownerText = dot < 0
            ? reference
            : reference[..dot];
        string ownerName = ownerText.Split(separator: '[')[0]
                                    .Trim();
        string? module = AllNodes(program: doc.Program)
                        .OfType<ModuleDeclaration>()
                        .FirstOrDefault()
                       ?.Path;
        TypeSymbol? owner = StdlibLoader.ResolveWrittenType(registry: doc.Registry,
            typeExpr: new TypeExpression(Name: ownerName, GenericArguments: null,
                Location: new SourceLocation(FileName: "", Line: 0, Column: 0, Position: 0)),
            genericParams: null, moduleName: module) is { } resolved and not ErrorTypeSymbol
            ? resolved
            : null;

        if (dot < 0)
        {
            if (owner != null)
            {
                (string label, string? documentation, SourceLocation? location) = TypeHoverLabel(type: owner);
                return new DocReferenceTarget(Label: label, Documentation: documentation, Location: location, Line: 0,
                    Start: 0, End: 0);
            }

            return doc.Registry.LookupRoutineByName(name: ownerName) is { } routine
                ? new DocReferenceTarget(Label: RoutineHeader(routine: routine),
                    Documentation: routine.Documentation ?? DocAbove(location: routine.Location),
                    Location: routine.Location, Line: 0, Start: 0, End: 0)
                : null;
        }

        if (owner == null)
        {
            return null;
        }

        string member = reference[(dot + 1)..]
                       .Split(separator: '(')[0]
                       .Trim();
        TypeSymbol definition = DefinitionOf(type: Rules.SurfaceType(type: owner));
        if (doc.Registry.GetMemberRoutinesForType(type: definition)
               .FirstOrDefault(predicate: r => r.Name == member) is { } method)
        {
            return new DocReferenceTarget(Label: RoutineHeader(routine: method),
                Documentation: method.Documentation ?? DocAbove(location: method.Location), Location: method.Location,
                Line: 0, Start: 0, End: 0);
        }

        if (CaseNames(type: definition)
              ?.Contains(value: member) == true)
        {
            return new DocReferenceTarget(
                Label: $"{member}: {TypeText(type: definition)}{CaseValue(owner: definition, name: member)}",
                Documentation: CaseDoc(owner: definition, name: member), Location: DeclarationOf(type: definition),
                Line: 0, Start: 0, End: 0);
        }

        return MemberVariableSignatures(type: definition, includeSecret: true)
              .FirstOrDefault(predicate: f => f.Name == member) is { Name: not null } field
            ? new DocReferenceTarget(Label: $"{field.Name}: {field.Type}", Documentation: DocAbove(location: field.Location),
                Location: field.Location, Line: 0, Start: 0, End: 0)
            : null;
    }

    /// <summary>
    /// A routine as its declaration reads: the type it belongs to (<c>routine Account.deposit</c>, a generic owner
    /// with its parameters, <c>Array[T, N].getitem</c>), its own generic parameters, its signature, and the
    /// <c>needs</c> clauses it declares.
    /// </summary>
    private static string RoutineHeader(RoutineInfo routine)
    {
        RoutineInfo shown = routine.GenericDefinition ?? routine;
        TypeSymbol? ownerType = routine.OwnerType ?? shown.OwnerType;
        string owner = ownerType != null
            ? HeaderText(type: Rules.SurfaceType(type: ownerType)) + "."
            : "";
        List<string> ownerParameters = ownerType != null
            ? DefinitionOf(type: Rules.SurfaceType(type: ownerType)).GenericParameters ?? []
            : [];
        List<string> own = (shown.GenericParameters ?? [])
                          .Where(predicate: g => !g.StartsWith(value: "__", comparisonType: StringComparison.Ordinal) &&
                                                 !ownerParameters.Contains(item: g))
                          .ToList();
        string generics = own.Count > 0
            ? $"[{string.Join(separator: ", ", values: own)}]"
            : "";
        return $"{RoutineModifiers(routine: shown)}routine {owner}{routine.Name}{generics}{RoutineDetail(r: routine)}" +
               NeedsClauses(constraints: shown.GenericConstraints);
    }

    /// <summary>The words a routine's declaration puts before <c>routine</c> to say what kind it is, in source order:
    /// <c>dangerous</c>, <c>common</c>, <c>suspended</c> or <c>threaded</c>.</summary>
    private static string RoutineModifiers(RoutineInfo routine)
    {
        var words = new List<string>();
        if (routine.IsDangerous)
        {
            words.Add(item: "dangerous");
        }

        if (routine.IsCommon)
        {
            words.Add(item: "common");
        }

        if (routine.IsSuspended)
        {
            words.Add(item: "suspended");
        }
        else if (routine.IsThreaded)
        {
            words.Add(item: "threaded");
        }

        return words.Count > 0
            ? string.Join(separator: " ", values: words) + " "
            : "";
    }

    /// <summary>A type's name with its own generic parameters when it is a definition (<c>Array[T, N]</c>), else the
    /// type as written (<c>Array[S32, 4]</c>).</summary>
    private static string HeaderText(TypeSymbol type)
    {
        return type.TypeArguments is not { Count: > 0 } && type.GenericParameters is { Count: > 0 } parameters
            ? $"{ShortName(name: type.BareName)}[{string.Join(separator: ", ", values: parameters)}]"
            : TypeText(type: type);
    }

    /// <summary>
    /// A type declaration's header exactly as written: its name line and the <c>obeys</c> / <c>needs</c> /
    /// <c>relates</c> lines right below it (conditions included), so hover shows what the declaration says rather
    /// than the protocols the build confers on its own. Null when the source can't be read.
    /// </summary>
    private static string? SourceHeader(SourceLocation? location)
    {
        if (location is not { FileName: { Length: > 0 } file, Line: > 0 })
        {
            return null;
        }

        string full = Path.GetFullPath(path: file);
        string? text = Texts.FirstOrDefault(predicate: kv => string.Equals(
                                 a: Path.GetFullPath(path: UriToFileName(uri: kv.Key)), b: full,
                                 comparisonType: StringComparison.OrdinalIgnoreCase))
                            .Value ?? (File.Exists(path: full)
                            ? File.ReadAllText(path: full)
                            : null);
        if (text == null)
        {
            return null;
        }

        string[] lines = text.ReplaceLineEndings(replacementText: "\n")
                             .Split(separator: '\n');
        if (location.Line > lines.Length)
        {
            return null;
        }

        var header = new List<string> { lines[location.Line - 1].Trim() };
        for (int i = location.Line; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            bool clause = line.Length == trimmed.Length &&
                          (trimmed.StartsWith(value: "obeys ", comparisonType: StringComparison.Ordinal) ||
                           trimmed.StartsWith(value: "needs ", comparisonType: StringComparison.Ordinal) ||
                           trimmed.StartsWith(value: "relates ", comparisonType: StringComparison.Ordinal));

            // A clause broken across lines ends its line with a comma and continues one level in.
            bool continuation = trimmed.Length > 0 && line.Length > trimmed.Length &&
                                header[^1].EndsWith(value: ',');
            if (clause || continuation)
            {
                header.Add(item: (continuation
                    ? "    "
                    : "") + trimmed.TrimEnd());
                continue;
            }

            break;
        }

        return string.Join(separator: "\n", values: header);
    }

    /// <summary>The clause lines a type declaration carries below its header: <c>obeys</c> and <c>needs</c>.</summary>
    private static string DeclarationClauses(TypeSymbol type)
    {
        List<TypeSymbol> protocols = type switch
        {
            EntityTypeSymbol entity => entity.ImplementedProtocols,
            RecordTypeSymbol record => record.ImplementedProtocols,
            _ => []
        };
        string obeys = protocols.Count > 0
            ? "\nobeys " + string.Join(separator: ", ", values: protocols.Select(selector: p => HeaderOrText(type: p)))
            : "";
        return obeys + NeedsClauses(constraints: type.GenericConstraints);
    }

    /// <summary>A protocol as written in an <c>obeys</c> list: its arguments as resolved (<c>Iterable[T]</c>).</summary>
    private static string HeaderOrText(TypeSymbol type)
    {
        return type.TypeArguments is { Count: > 0 }
            ? TypeText(type: type)
            : ShortName(name: type.BareName);
    }

    /// <summary>
    /// The <c>needs</c> clauses of a declaration, laid out as the formatter writes them: one line of the kind and
    /// const-type classifiers (<c>needs U64 N, EntityType A</c>), one line per protocol or type constraint
    /// (<c>needs K obeys Hashable, Equatable</c>), then the <c>everywhere</c> gates. A parameter the build invented
    /// for a protocol-typed parameter (<c>__T0</c>) is not written in the source and is left out.
    /// </summary>
    private static string NeedsClauses(List<GenericConstraintDeclaration>? constraints)
    {
        List<GenericConstraintDeclaration> written = (constraints ?? [])
                                                    .Where(predicate: c => !c.ParameterName.StartsWith(value: "__",
                                                         comparisonType: StringComparison.Ordinal))
                                                    .ToList();
        var lines = new List<string>();
        List<string> kinds = written.Where(predicate: c => c.ConstraintType is not (ConstraintKind.Obeys or
                                        ConstraintKind.TypeEquality or ConstraintKind.Everywhere))
                                    .Select(selector: ConstraintText)
                                    .ToList();
        if (kinds.Count > 0)
        {
            lines.Add(item: "needs " + string.Join(separator: ", ", values: kinds));
        }

        lines.AddRange(collection: written.Where(predicate: c => c.ConstraintType is ConstraintKind.Obeys or
                                              ConstraintKind.TypeEquality)
                                          .Select(selector: c => "needs " + ConstraintText(constraint: c)));
        List<string> everywhere = written.Where(predicate: c => c.ConstraintType == ConstraintKind.Everywhere)
                                         .SelectMany(selector: c => c.ConstraintTypes ?? [])
                                         .Select(selector: WrittenTypeText)
                                         .ToList();
        if (everywhere.Count > 0)
        {
            lines.Add(item: "needs " + string.Join(separator: ", ", values: everywhere) + " everywhere");
        }

        return string.Concat(values: lines.Select(selector: l => "\n" + l));
    }

    /// <summary>One constraint as written after <c>needs</c>.</summary>
    private static string ConstraintText(GenericConstraintDeclaration constraint)
    {
        List<TypeExpression> types = constraint.ConstraintTypes ?? [];
        string list = string.Join(separator: ", ", values: types.Select(selector: WrittenTypeText));
        return constraint.ConstraintType switch
        {
            ConstraintKind.Obeys => $"{constraint.ParameterName} obeys {list}",
            ConstraintKind.TypeEquality when types.Count == 1 => $"{constraint.ParameterName} is {list}",
            ConstraintKind.TypeEquality => $"{constraint.ParameterName} in [{list}]",
            ConstraintKind.ConstGeneric when types.Count == 1 => $"{list} {constraint.ParameterName}",
            _ when ConstraintKindTokens.TryGetValue(key: constraint.ConstraintType,
                value: out (string Spelling, string Kind) named) => $"{named.Spelling} {constraint.ParameterName}",
            _ => constraint.ParameterName
        };
    }

    /// <summary>A written type as source spells it (<c>Iterable[T]</c>).</summary>
    private static string WrittenTypeText(TypeExpression type)
    {
        return type.GenericArguments is { Count: > 0 } args
            ? $"{ShortName(name: type.Name)}[{string.Join(separator: ", ", values: args.Select(selector: WrittenTypeText))}]"
            : ShortName(name: type.Name);
    }

    /// <summary>The routine whose declaration names the token (its name in <c>routine name(...)</c>).</summary>
    private static RoutineInfo? RoutineDeclaredAtToken(DocState doc, Token hit)
    {
        return AllNodes(program: doc.Program)
              .OfType<RoutineDeclaration>()
              .Select(selector: r => r.ResolvedInfo)
              .FirstOrDefault(predicate: r => r != null && r.Name == hit.Text && r.Location?.Line == hit.Line);
    }

    /// <summary>
    /// Where a type is declared. A user type's symbol keeps its declaration's position. A stdlib type's does not (the
    /// build reads a missing position as "stdlib" to skip its protocol checks), so its file comes from the module
    /// index the build resolves imports with, and its line from the declaration keyword that names it there.
    /// </summary>
    private static SourceLocation? DeclarationOf(TypeSymbol type)
    {
        if (type.Location is { FileName.Length: > 0 } known)
        {
            return known;
        }

        string name = ShortName(name: type.BareName);
        string stdlibRoot = Path.GetFullPath(path: StdlibLoader.GetDefaultStdlibPath());
        if (type.Module is not { } module ||
            !StdlibIndexFor(language: _profile.Language, stdlibRoot: stdlibRoot, libraryRoots: [])
               .TryGetValue(key: $"{module}.{name}", value: out string? file) ||
            TokensOf(fileName: file) is not { } tokens)
        {
            return null;
        }

        // The index keeps the first file to claim `module.Name`, which can be another file of the module (one with a
        // conversion routine named after the type): then the declaration is looked for in the module's other files.
        return DeclarationIn(file: file, tokens: tokens, name: name) ??
               StdlibDeclarations.GetOrAdd(key: $"{module}.{name}",
                   valueFactory: _ => FindStdlibDeclaration(stdlibRoot: stdlibRoot, module: module, name: name));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SourceLocation?>
        StdlibDeclarations = new(comparer: StringComparer.Ordinal);

    /// <summary>The type declaration named <paramref name="name"/> in a file's tokens: the declaration keyword just
    /// before the name.</summary>
    private static SourceLocation? DeclarationIn(string file, List<Token> tokens, string name)
    {
        for (int i = 1; i < tokens.Count; i++)
        {
            if (tokens[index: i].Type == TokenType.Identifier && tokens[index: i].Text == name &&
                tokens[index: i - 1].Type is TokenType.Record or TokenType.Bundle or TokenType.Entity or TokenType.Choice or
                    TokenType.Flags or TokenType.Variant or TokenType.Protocol or TokenType.Crashable)
            {
                return new SourceLocation(FileName: file, Line: tokens[index: i - 1].Line,
                    Column: tokens[index: i - 1].Column, Position: 0);
            }
        }

        return null;
    }

    /// <summary>Looks through the stdlib files of a module (by their <c>module</c> header) for a type's declaration,
    /// reading only the files whose text names it after a declaration keyword.</summary>
    private static SourceLocation? FindStdlibDeclaration(string stdlibRoot, string module, string name)
    {
        string[] keywords = ["record ", "entity ", "choice ", "flags ", "variant ", "protocol ", "crashable "];
        foreach (string file in Directory.EnumerateFiles(path: stdlibRoot, searchPattern: "*.*",
                     searchOption: SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(path: file);
            if (!keywords.Any(predicate: k => text.Contains(value: k + name, comparisonType: StringComparison.Ordinal)) ||
                TokensOf(fileName: file) is not { } tokens ||
                tokens.SkipWhile(predicate: t => t.Type != TokenType.Module)
                      .Skip(count: 1)
                      .TakeWhile(predicate: t => t.Type != TokenType.Newline)
                      .Select(selector: t => t.Text) is var header && string.Concat(values: header) != module)
            {
                continue;
            }

            if (DeclarationIn(file: file, tokens: tokens, name: name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The generic definition a type was instantiated from (where its doc comment is), or the type.</summary>
    private static TypeSymbol DefinitionOf(TypeSymbol type)
    {
        return type switch
        {
            RecordTypeSymbol { GenericDefinition: { } definition } => definition,
            EntityTypeSymbol { GenericDefinition: { } definition } => definition,
            _ => type
        };
    }

    /// <summary>The declaration keyword of a type's kind, as hover shows it before its name (none for a tuple, a
    /// routine type or an unresolved one).</summary>
    private static string? KindWord(TypeSymbol type)
    {
        return type switch
        {
            CrashableTypeSymbol => "crashable",
            EntityTypeSymbol => "entity",
            VariantTypeSymbol => "variant",
            ChoiceTypeSymbol => "choice",
            FlagsTypeSymbol => "flags",
            TupleTypeSymbol => null,
            RecordTypeSymbol => "record",
            ProtocolTypeSymbol => "protocol",
            GenericParameterTypeSymbol or AssociatedProjectionTypeSymbol or ProtocolSelfTypeSymbol => "type parameter",
            _ => null
        };
    }

    /// <summary>The field a member access names at the token (<c>p.x</c>), from its receiver's resolved type.</summary>
    private static MemberVariableInfo? FieldAtToken(DocState doc, Token hit)
    {
        foreach (MemberExpression member in AllNodes(program: doc.Program)
                    .OfType<MemberExpression>())
        {
            if (member.MemberName != hit.Text || member.Location.Line != hit.Line ||
                MemberNameToken(tokens: doc.Tokens, member: member) is not { } name || name.Column != hit.Column ||
                member.Object.ResolvedType is not { } receiver)
            {
                continue;
            }

            List<MemberVariableInfo> fields = Rules.SurfaceType(type: receiver) switch
            {
                EntityTypeSymbol entity => entity.MemberVariables,
                RecordTypeSymbol record => record.MemberVariables,
                _ => []
            };
            if (fields.FirstOrDefault(predicate: f => f.Name == member.MemberName) is { } field)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>A case's value as hover shows it after its type: a choice case's number (written or assigned), a
    /// flags member's bit (<c>= 4 (1 &lt;&lt; 2)</c>).</summary>
    private static string CaseValue(TypeSymbol owner, string name)
    {
        return Rules.SurfaceType(type: owner) switch
        {
            ChoiceTypeSymbol choice when choice.Cases.FirstOrDefault(predicate: c => c.Name == name) is { } choiceCase =>
                $" = {choiceCase.ComputedValue}",
            FlagsTypeSymbol flags when flags.Members.FirstOrDefault(predicate: m => m.Name == name) is { } member =>
                $" = {1UL << member.BitPosition} (1 << {member.BitPosition})",
            _ => ""
        };
    }

    /// <summary>The doc of a case: the doc comment above it in its choice or flags declaration.</summary>
    private static string? CaseDoc(TypeSymbol owner, string name)
    {
        TypeSymbol shown = Rules.SurfaceType(type: owner);
        if (shown is ChoiceTypeSymbol choice)
        {
            if (choice.Cases.FirstOrDefault(predicate: c => c.Name == name)?.Location is { FileName.Length: > 0 } caseAt)
            {
                return DocAbove(location: caseAt);
            }
        }

        // A flags member keeps no position: it is the first token spelled so below the declaration.
        if (DeclarationOf(type: shown) is { FileName: { Length: > 0 } file } at && TokensOf(fileName: file) is { } tokens &&
            tokens.Where(predicate: t => t.Line > at.Line && t.Type == TokenType.Identifier && t.Text == name)
                  .MinBy(keySelector: t => (t.Line, t.Column)) is { } member)
        {
            return DocAbove(location: new SourceLocation(FileName: file, Line: member.Line, Column: member.Column,
                Position: 0));
        }

        return null;
    }

    /// <summary>A parameter's line in the doc comment of the routine it belongs to (<c>:param name: …</c>).</summary>
    private static string? ParameterDoc(DocState doc, VariableInfo parameter)
    {
        RoutineDeclaration? routine = AllNodes(program: doc.Program)
                                     .OfType<RoutineDeclaration>()
                                     .Where(predicate: r => r.Parameters.Any(predicate: p =>
                                          p.Location.Line == parameter.Location?.Line &&
                                          p.Location.Column == parameter.Location?.Column))
                                     .FirstOrDefault();
        string? routineDoc = routine?.ResolvedInfo?.Documentation ?? routine?.Documentation ??
                             DocAbove(location: routine?.Location);
        if (routineDoc == null)
        {
            return null;
        }

        string description = ParseDoc(doc: routineDoc)
                            .Params.FirstOrDefault(predicate: x => x.Name == parameter.Name)
                            .Desc;
        return string.IsNullOrWhiteSpace(value: description)
            ? null
            : description;
    }

    // Tokens of files that declare something hovered, by full path, kept while the file is unchanged.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, List<Token> Tokens)>
        FileTokens = new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>The tokens of a file: an open document as it stands in the editor, any other one as saved.</summary>
    private static List<Token>? TokensOf(string fileName)
    {
        string full = Path.GetFullPath(path: fileName);
        foreach ((string uri, DocState open) in Docs)
        {
            if (string.Equals(a: Path.GetFullPath(path: UriToFileName(uri: uri)), b: full,
                    comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                return open.Tokens;
            }
        }

        if (!File.Exists(path: full))
        {
            return null;
        }

        DateTime stamp = File.GetLastWriteTimeUtc(path: full);
        if (FileTokens.TryGetValue(key: full, value: out (DateTime Stamp, List<Token> Tokens) cached) &&
            cached.Stamp == stamp)
        {
            return cached.Tokens;
        }

        List<Token> tokens = Builder.Tokenizer.Lexers.Tokenize(source: File.ReadAllText(path: full), fileName: full,
            language: Builder.Frontends.Languages.OfFile(fileName: full));
        FileTokens[key: full] = (stamp, tokens);
        return tokens;
    }

    /// <summary>
    /// The doc comment written directly above a declaration: the <c>###</c> lines right above its line, past any
    /// annotation lines (<c>@readonly</c>), joined as the parser joins a top-level declaration's. It reads the
    /// declaring file's own tokens, so it finds the doc of a field, a case or a member as well as of a top-level
    /// declaration, in this file or any other (the stdlib included).
    /// </summary>
    private static string? DocAbove(SourceLocation? location)
    {
        if (location is not { FileName: { Length: > 0 } file, Line: > 1 } || TokensOf(fileName: file) is not { } tokens)
        {
            return null;
        }

        Dictionary<int, List<Token>> byLine = tokens.Where(predicate: t =>
                                                         t.Type is not (TokenType.Newline or TokenType.Indent or
                                                             TokenType.Dedent or TokenType.Eof))
                                                    .GroupBy(keySelector: t => t.Line)
                                                    .ToDictionary(keySelector: g => g.Key,
                                                         elementSelector: g => g.OrderBy(keySelector: t => t.Column)
                                                                                .ToList());
        var lines = new List<string>();
        for (int line = location.Line - 1; line > 0 && byLine.TryGetValue(key: line, value: out List<Token>? onLine); line--)
        {
            if (onLine.All(predicate: t => t.Type == TokenType.DocComment))
            {
                lines.AddRange(collection: onLine.Select(selector: t => t.Text.Trim()));
            }
            else if (!onLine[index: 0].Text.StartsWith(value: '@'))
            {
                break;
            }
        }

        lines.Reverse();
        return lines.Count > 0
            ? string.Join(separator: "\n", values: lines)
            : null;
    }

    /// <summary>
    /// If the identifier token names a routine CALL or reference, the resolved routine — matched through
    /// the analyzer's <c>ResolvedRoutine</c> so it works cross-file. A bare-identifier callee must sit
    /// exactly at the token; a member callee (<c>x.foo()</c>) matches by name + line.
    /// </summary>
    private static RoutineInfo? RoutineReferencedByToken(DocState doc, Token hit)
    {
        return CallReferencedByToken(doc: doc, hit: hit)?.ResolvedRoutine;
    }

    /// <summary>The resolved call whose callee is the hit token (see <see cref="RoutineReferencedByToken"/>).</summary>
    private static CallExpression? CallReferencedByToken(DocState doc, Token hit)
    {
        return AllNodes(program: doc.Program)
              .OfType<CallExpression>()
              .FirstOrDefault(predicate: call =>
                   call.ResolvedRoutine != null && CalleeMatchesHit(callee: call.Callee, hit: hit));
    }

    /// <summary>
    /// What each generic parameter stands for at the hovered token, like C#'s "T is string": for a call, the
    /// receiver type's parameters and the routine's own (as instantiated, or else as its arguments bind them),
    /// for a variable or an expression, its type's parameters.
    /// </summary>
    private static List<(string Parameter, TypeSymbol Argument)> GenericBindingsAt(DocState doc, Token hit,
        Expression? best)
    {
        if (IsIdentifierText(text: hit.Text) && CallReferencedByToken(doc: doc, hit: hit) is { } call)
        {
            return call.ResolvedRoutine is { Kind: RoutineKind.Creator } && call.ResolvedType is { } made
                ? TypeBindings(type: Rules.SurfaceType(type: made))
                : CallBindings(call: call);
        }

        TypeSymbol? type = IsIdentifierText(text: hit.Text)
            ? VariableBoundAtToken(doc: doc, hit: hit)?.Type ?? best?.ResolvedType
            : best?.ResolvedType;
        return type != null
            ? TypeBindings(type: Rules.SurfaceType(type: type))
            : [];
    }

    /// <summary>The generic parameters of a call: the receiver's (<c>List[T].add_last</c> called on a
    /// <c>List[S32]</c> binds <c>T</c> to <c>S32</c>), then the routine's own.</summary>
    private static List<(string Parameter, TypeSymbol Argument)> CallBindings(CallExpression call)
    {
        var bindings = new List<(string Parameter, TypeSymbol Argument)>();
        if (call.Callee is MemberExpression { Object.ResolvedType: { } receiver })
        {
            bindings.AddRange(collection: TypeBindings(type: Rules.SurfaceType(type: receiver)));
        }

        RoutineInfo routine = call.ResolvedRoutine!;
        List<string>? names = routine.GenericDefinition?.GenericParameters ?? routine.GenericParameters;
        if (names is not { Count: > 0 })
        {
            return bindings;
        }

        if (routine.TypeArguments is { } args && args.Count == names.Count)
        {
            bindings.AddRange(collection: names.Zip(second: args));
            return bindings;
        }

        // Not instantiated in this analysis: a parameter declared as a bare generic parameter takes the
        // type of the argument passed to it.
        for (int i = 0; i < routine.Parameters.Count; i++)
        {
            if (routine.Parameters[index: i].Type is GenericParameterTypeSymbol param &&
                names.Contains(item: param.Name) &&
                bindings.All(predicate: b => b.Parameter != param.Name) &&
                ArgumentFor(call: call, position: i, name: routine.Parameters[index: i].Name)?.ResolvedType is { } argType)
            {
                bindings.Add(item: (param.Name, argType));
            }
        }

        return bindings;
    }

    /// <summary>The argument a call passes to a parameter: by name when the call names it, else by position.</summary>
    private static Expression? ArgumentFor(CallExpression call, int position, string name)
    {
        if (call.Arguments.OfType<NamedArgumentExpression>()
                .FirstOrDefault(predicate: a => a.Name == name) is { } named)
        {
            return named.Value;
        }

        return position < call.Arguments.Count && call.Arguments[index: position] is not NamedArgumentExpression
            ? call.Arguments[index: position]
            : null;
    }

    /// <summary>The generic parameters of an instantiated type, paired with its arguments
    /// (<c>Dict[K, V]</c> as <c>Dict[Text, S64]</c> gives <c>K</c> = Text and <c>V</c> = S64).</summary>
    private static List<(string Parameter, TypeSymbol Argument)> TypeBindings(TypeSymbol type)
    {
        List<string>? names = type switch
        {
            RecordTypeSymbol { GenericDefinition: { } definition } => definition.GenericParameters,
            EntityTypeSymbol { GenericDefinition: { } definition } => definition.GenericParameters,
            _ => null
        };
        return names != null && type.TypeArguments is { } args && args.Count == names.Count
            ? names.Zip(second: args)
                   .ToList()
            : [];
    }

    /// <summary>Whether a call's callee expression matches the hit token — used to resolve which routine
    /// a hovered/referenced identifier denotes. Handles both bare/realm-qualified identifiers and member
    /// call expressions.</summary>
    private static bool CalleeMatchesHit(Expression callee, Token hit)
    {
        switch (callee)
        {
            // A bare or realm-qualified callee (`foo` or `C::foo`). The IdentifierExpression is
            // anchored at its START — the realm tag when one is present — so `C::foo` spans the realm
            // token AND the name token. CheckAndAdvance EITHER, at its exact column, so hovering the `C` in
            // `C::rf_x()` resolves the routine instead of mistaking `C` for a value of the return type.
            case IdentifierExpression cid when cid.Location.Line == hit.Line:
            {
                int realmCol = cid.Location.Column;
                if (cid.Realm != null)
                {
                    int nameCol = realmCol + cid.Realm.Length + 2; // realm tag + "::"
                    return hit.Text == cid.Realm && hit.Column == realmCol ||
                           hit.Text == cid.Name && hit.Column == nameCol;
                }

                return hit.Text == cid.Name && hit.Column == realmCol;
            }

            // A member callee (`x.foo()`): match the member name by name + line.
            case MemberExpression m:
                return m.MemberName == hit.Text && m.Location.Line == hit.Line;

            default:
                return false;
        }
    }

    /// <summary>True when the token is a realm tag (<c>C</c>/<c>LLVM</c>/<c>RF</c>/<c>SF</c>) — i.e. the
    /// very next token on the line is the <c>::</c> separator. Such a token is a qualifier, not a value,
    /// so hover must not fall back to reporting it as a typed expression.</summary>
    private static bool IsRealmQualifier(DocState doc, Token hit)
    {
        int nextCol = hit.Column + hit.Text.Length;
        return doc.Tokens.Any(predicate: t => t.Type == TokenType.DoubleColon &&
                                              t.Line == hit.Line && t.Column == nextCol);
    }

    /// <summary>
    /// A one-line note on the ownership nature of a variable's type, for hover — so the ownership model is
    /// visible where it bites: an <c>entity</c> is single-owner (needs <c>steal</c> to hand off), a
    /// <c>Retained[T]</c> is a storable hand-off, the <c>Viewing</c>/<c>Modifying</c> tokens are temporary
    /// access links. Null for ordinary value types.
    /// </summary>
    private static string? OwnershipNote(TypeSymbol type)
    {
        if (!Rules.ChecksOwnership)
        {
            return null;
        }

        if (type is EntityTypeSymbol)
        {
            return
                "🔒 **entity** — single owner. Hand it off with `steal` (a plain `=` is RF-S413); after " +
                "that the source binding is dead.";
        }

        return type.BareName switch
        {
            "Retained" => "📦 **Retained** — a persistent, storable ownership hand-off.",
            "Viewing" or "Modifying" =>
                "👁 **temporary access link** — not storable or returnable, valid only for this scope.",
            "Controlling" or "Accessing" => "🔗 a reference protocol, not a pass-currency.",
            _ => null
        };
    }

    /// <summary>The name a <c>var</c> declaration at the token declares, with its type (written or inferred).</summary>
    private static (string Name, TypeSymbol Type, SourceLocation Location)? DeclaredVariableAtToken(DocState doc, Token hit)
    {
        foreach (VariableDeclaration vd in AllNodes(program: doc.Program)
                    .OfType<VariableDeclaration>())
        {
            if (vd.Name != hit.Text || vd.Location.Line != hit.Line)
            {
                continue;
            }

            TypeSymbol? type = vd.Type?.ResolvedType ?? vd.Initializer?.ResolvedType;
            if (type != null && !type.Name.StartsWith(value: '<'))
            {
                return (vd.Name, type, vd.Location);
            }
        }

        return null;
    }

    /// <summary>The variable/parameter binding an identifier token resolved to (via the stamped
    /// <see cref="IdentifierExpression.ResolvedVariable"/>), or null.</summary>
    private static VariableInfo? VariableBoundAtToken(DocState doc, Token hit)
    {
        foreach (IdentifierExpression e in AllNodes(program: doc.Program)
                    .OfType<IdentifierExpression>())
        {
            if (e.ResolvedVariable != null && e.Name == hit.Text && e.Location.Line == hit.Line &&
                e.Location.Column == hit.Column)
            {
                return e.ResolvedVariable;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>textDocument/definition</c>: jump to where the symbol under the cursor is defined. A routine
    /// call resolves through the analyzer's <c>ResolvedRoutine</c> — so it can jump CROSS-FILE, e.g. into
    /// the stdlib. Otherwise the name is matched against the declarations in this file (routine / type /
    /// variable). Parameters are not yet targets.
    /// </summary>
    private static void HandleDefinition(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        if (DocReferenceAt(doc: doc, uri: uri, line0: line0, char0: char0) is { Location: { } referenced })
        {
            WriteResult(stdout: stdout, id: id, result: LocationToLsp(loc: referenced));
            return;
        }

        Token? hit = TokenAt(doc: doc, line0: line0, char0: char0);
        if (hit == null || !IsIdentifierText(text: hit.Text))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        // 0. A variable / parameter use — jump to its binding site (scope-precise via ResolvedVariable).
        VariableInfo? bound = VariableBoundAtToken(doc: doc, hit: hit);
        if (bound?.Location is { } vloc)
        {
            WriteResult(stdout: stdout, id: id, result: LocationToLsp(loc: vloc));
            return;
        }

        List<ISyntaxTreeNode> nodes = AllNodes(program: doc.Program);

        // 1. A routine call — jump to the resolved routine's definition (cross-file capable).
        SourceLocation? routineLoc = FindCallDefinitionLocation(nodes: nodes, hit: hit);
        if (routineLoc != null)
        {
            WriteResult(stdout: stdout, id: id, result: LocationToLsp(loc: routineLoc));
            return;
        }

        // 2. A declaration in this file with the matching name (routine / type / variable). For variables,
        //    prefer the nearest declaration at or above the use.
        SyntaxTreeNode? bestDecl = FindBestDeclarationNode(nodes: nodes, hit: hit);
        WriteResult(stdout: stdout,
            id: id,
            result: bestDecl != null
                ? LocationToLsp(loc: bestDecl.Location)
                : null);
    }

    /// <summary>Finds the definition location of the resolved routine call whose callee matches the hit
    /// token, or null if none matches. Checks both exact-column match (bare identifier) and
    /// name+line match (member callee).</summary>
    private static SourceLocation? FindCallDefinitionLocation(List<ISyntaxTreeNode> nodes,
        Token hit)
    {
        foreach (CallExpression call in nodes.OfType<CallExpression>())
        {
            if (call.ResolvedRoutine?.Location is not { } rloc)
            {
                continue;
            }

            (string? cname, SourceLocation? cloc) = CalleeName(callee: call.Callee);
            if (cname != hit.Text || cloc == null || cloc.Line != hit.Line)
            {
                continue;
            }

            // A bare identifier callee must sit exactly at the token; a member callee matches by name + line.
            if (call.Callee is IdentifierExpression && cloc.Column != hit.Column)
            {
                continue;
            }

            return rloc;
        }

        return null;
    }

    /// <summary>Finds the declaration node in this file whose name matches the hit token. For variables,
    /// prefers the nearest declaration at or above the use site.</summary>
    private static SyntaxTreeNode? FindBestDeclarationNode(List<ISyntaxTreeNode> nodes, Token hit)
    {
        SyntaxTreeNode? bestDecl = null;
        foreach (ISyntaxTreeNode node in nodes)
        {
            if (node is not SyntaxTreeNode sn || !node.GetType()
                                                      .Name
                                                      .EndsWith(value: SuffixDeclaration,
                                                           comparisonType:
                                                           StringComparison.Ordinal) ||
                GetNameProp(node: node) != hit.Text)
            {
                continue;
            }

            if (bestDecl == null || sn.Location.Line <= hit.Line &&
                sn.Location.Line > bestDecl.Location.Line)
            {
                bestDecl = sn;
            }
        }

        return bestDecl;
    }

    // Semantic-tokens legend (order defines the indices sent in the delta stream). Indices are
    // referenced by name through SemTok(...) so the emitter can never drift out of sync with this list.
    private static readonly List<object?> SemanticTokenTypes = new()
    {
        "function", // 0
        "variable", // 1
        "parameter", // 2
        "type", // 3
        "property", // 4
        "keyword", // 5
        "string", // 6
        "number", // 7
        "comment", // 8
        "namespace", // 9: a module path
        "operator", // 10
        // The two kinds of type, which a reader tells apart by color. A record (also a choice, a flags, a
        // crashable, and a variant of records) is a copied value, an entity (also a variant holding an
        // entity) is an object with an identity.
        "recordType", // 11
        "entityType", // 12
        "interface", // 13: a protocol
        "typeParameter", // 14
        "constant", // 15: a preset or a global
        "decorator", // 16: an annotation, its `@` and its name
        // The structure of a `###` doc comment: a field (`:param`, `:returns:`) and a field's name or a `{Reference}`.
        "docTag", // 17
        "docValue" // 18
    };

    private static int SemTok(string name)
    {
        return SemanticTokenTypes.IndexOf(item: name);
    }

    // Token modifiers. `deprecated` (bit 0) marks a DEAD use — a variable read after its ownership was
    // moved out by `steal` — which editors render struck-through / faded (the ownership grey-out).
    private static readonly List<object?> SemanticTokenModifiers = new() { "deprecated" };
    private const int ModDeprecated = 1; // 1 << 0

    /// <summary>
    /// <c>textDocument/references</c>: all occurrences of the symbol under the cursor, in this file.
    /// A variable/parameter binds by <see cref="IdentifierExpression.ResolvedVariable"/> identity, so
    /// only the SAME binding is returned (a shadowing same-name local is excluded); a routine binds by
    /// its resolved identity. Types and unresolved names fall back to same-name identifier matching.
    /// </summary>
    private static void HandleReferences(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        Token? hit = TokenAt(doc: doc, line0: line0, char0: char0);
        if (hit == null || !IsIdentifierText(text: hit.Text))
        {
            WriteResult(stdout: stdout, id: id, result: new List<object?>());
            return;
        }

        var locs = new List<object?>();
        foreach ((int line, int col, int len) in SymbolOccurrences(doc: doc, hit: hit))
        {
            locs.Add(item: RangeLsp(uri: uri,
                line1: line,
                col1: col,
                length: len));
        }

        WriteResult(stdout: stdout, id: id, result: locs);
    }

    /// <summary>
    /// The 1-based (line, column, length) spans of every occurrence of the symbol the cursor is on,
    /// within this document. Shared by references and rename. Resolution order:
    /// <list type="number">
    /// <item>a variable/parameter binding (reference-identity on <c>ResolvedVariable</c>, plus its
    /// declaration site), then</item>
    /// <item>a routine (identity by <c>RegistryKey</c> across call sites, plus a same-file definition),
    /// then</item>
    /// <item>a same-name identifier text match (types / unresolved names).</item>
    /// </list>
    /// </summary>
    private static List<(int Line, int Col, int Len)> SymbolOccurrences(DocState doc, Token hit)
    {
        int len = hit.Text.Length;
        var result = new List<(int, int, int)>();
        var seen = new HashSet<(int, int)>();

        void Add(int line, int col)
        {
            if (line > 0 && col > 0 && seen.Add(item: (line, col)))
            {
                result.Add(item: (line, col, len));
            }
        }

        List<ISyntaxTreeNode> nodes = AllNodes(program: doc.Program);
        var idents = nodes.OfType<IdentifierExpression>()
                          .ToList();

        // 1. Variable / parameter binding. The cursor may be on a use OR on the declaration name.
        VariableInfo? binding = FindVariableBinding(doc: doc, hit: hit, idents: idents);
        if (binding != null)
        {
            CollectVariableOccurrences(binding: binding, idents: idents, add: Add);
            return result;
        }

        // 2. Routine identity — same-file call sites + a same-file definition.
        RoutineInfo? routine = RoutineReferencedByToken(doc: doc, hit: hit) ??
                               RoutineDefinedAtToken(doc: doc, hit: hit, nodes: nodes);
        if (routine != null)
        {
            CollectRoutineOccurrences(routine: routine,
                hit: hit,
                nodes: nodes,
                add: Add);
            if (result.Count > 0)
            {
                return result;
            }
        }

        // 3. Fallback — same-name identifier tokens (types, unresolved names).
        foreach (Token t in doc.Tokens.Where(predicate: t =>
                     t.Type == TokenType.Identifier && t.Text == hit.Text))
        {
            Add(line: t.Line, col: t.Column);
        }

        return result;
    }

    /// <summary>Finds the variable/parameter binding that the hit token refers to. Checks the cursor token
    /// first (via <see cref="VariableBoundAtToken"/>), then scans all identifier expressions to find one
    /// whose binding declaration is at the cursor position (handles clicking the declaration site).</summary>
    private static VariableInfo? FindVariableBinding(DocState doc, Token hit,
        List<IdentifierExpression> idents)
    {
        VariableInfo? binding = VariableBoundAtToken(doc: doc, hit: hit);
        if (binding != null)
        {
            return binding;
        }

        return idents.Select(selector: e => e.ResolvedVariable)
                     .FirstOrDefault(predicate: v => v?.Location is { } l &&
                                                     l.Line == hit.Line && l.Column == hit.Column);
    }

    /// <summary>Adds all occurrence positions of a variable binding to the accumulator: every use site
    /// (via ResolvedVariable identity) and the declaration site itself.</summary>
    private static void CollectVariableOccurrences(VariableInfo binding,
        List<IdentifierExpression> idents, Action<int, int> add)
    {
        foreach (SourceLocation loc in idents
                                      .Where(predicate: e =>
                                           ReferenceEquals(objA: e.ResolvedVariable,
                                               objB: binding))
                                      .Select(selector: e => e.Location))
        {
            add(arg1: loc.Line, arg2: loc.Column);
        }

        if (binding.Location is { } decl)
        {
            add(arg1: decl.Line, arg2: decl.Column);
        }
    }

    /// <summary>Adds all occurrence positions of a routine (call sites + declaration) to the accumulator.</summary>
    private static void CollectRoutineOccurrences(RoutineInfo routine, Token hit,
        List<ISyntaxTreeNode> nodes, Action<int, int> add)
    {
        string key = routine.RegistryKey;
        foreach (CallExpression call in nodes.OfType<CallExpression>())
        {
            if (call.ResolvedRoutine?.RegistryKey != key)
            {
                continue;
            }

            (string? cn, SourceLocation? cl) = CalleeName(callee: call.Callee);
            if (cn == hit.Text && cl != null)
            {
                add(arg1: cl.Line, arg2: cl.Column);
            }
        }

        foreach (ISyntaxTreeNode node in nodes)
        {
            if (node is SyntaxTreeNode sn && node.GetType()
                                                 .Name == NodeRoutineDeclaration &&
                GetNameProp(node: node) == hit.Text)
            {
                add(arg1: sn.Location.Line, arg2: sn.Location.Column);
            }
        }
    }

    /// <summary>If the token sits on a routine's declaration name, that routine (matched by name).</summary>
    private static RoutineInfo? RoutineDefinedAtToken(DocState doc, Token hit,
        List<ISyntaxTreeNode> nodes)
    {
        bool onDecl = nodes.Any(predicate: n => n is SyntaxTreeNode sn && n.GetType()
                                                   .Name == NodeRoutineDeclaration &&
                                                GetNameProp(node: n) == hit.Text &&
                                                sn.Location.Line == hit.Line);
        if (!onDecl)
        {
            return null;
        }

        return doc.Registry
                  .GetAllRoutines()
                  .FirstOrDefault(predicate: r => r.Name == hit.Text && r.Location is { } l &&
                                                  l.Line == hit.Line);
    }

    /// <summary>
    /// <c>textDocument/completion</c>: after a <c>.</c>, the receiver type's members; otherwise keywords,
    /// visible free routines, and this file's declarations.
    /// </summary>
    private static void HandleCompletion(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout,
                id: id,
                result: new Dictionary<string, object?>
                {
                    [key: "isIncomplete"] = false, [key: "items"] = new List<object?>()
                });
            return;
        }

        var items = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>();
        PopulateCompletionItems(doc: doc,
            line0: line0,
            char0: char0,
            items: items,
            seen: seen);

        WriteResult(stdout: stdout,
            id: id,
            result: new Dictionary<string, object?>
            {
                [key: "isIncomplete"] = false, [key: "items"] = items
            });
    }

    /// <summary>Fills the completion item list based on context: realm qualifier → realm-only routines;
    /// member dot → receiver type members; otherwise → global keywords / routines / declarations.</summary>
    private static void PopulateCompletionItems(DocState doc, int line0, int char0,
        List<Dictionary<string, object?>> items, HashSet<string> seen)
    {
        // After a `Realm::` (C / LLVM / RF / SF) list ONLY that realm's routines.
        string? realm = RealmQualifierBefore(doc: doc, line0: line0, char0: char0);
        if (realm != null)
        {
            AddRealmCompletions(doc: doc,
                items: items,
                seen: seen,
                realm: realm);
            return;
        }

        // After a `receiver.` commit to members only — if the receiver type can't be resolved,
        // return an empty list rather than spilling globals.
        Token? receiver = MemberReceiverToken(doc: doc, line0: line0, char0: char0);
        if (receiver != null)
        {
            TypeSymbol? receiverType = ReceiverType(doc: doc, receiver: receiver) is { } handled
                ? Rules.SurfaceType(type: handled)
                : null;
            if (receiverType != null)
            {
                AddMemberCompletions(doc: doc,
                    items: items,
                    seen: seen,
                    receiver: receiver,
                    receiverType: receiverType);
            }

            return;
        }

        AddGlobalCompletions(doc: doc, items: items, seen: seen);
    }

    /// <summary>Completions after a <c>Realm::</c> qualifier: only that realm's free routines.</summary>
    private static void AddRealmCompletions(DocState doc, List<Dictionary<string, object?>> items,
        HashSet<string> seen, string realm)
    {
        RoutineRealm? want = realm switch
        {
            "C" => RoutineRealm.C,
            "LLVM" => RoutineRealm.LLVM,
            "RF" => RoutineRealm.RF,
            "SF" => RoutineRealm.SF,
            _ => null
        };
        if (want is not { } wr)
        {
            return;
        }

        foreach (RoutineInfo r in doc.Registry.GetAllRoutines())
        {
            if (r.Realm == wr && r.OwnerType == null && !r.Name.StartsWith(value: '$'))
            {
                AddItem(items: items,
                    seen: seen,
                    label: r.Name,
                    kind: 3,
                    detail: RoutineDetail(r: r),
                    documentation: r.Documentation);
            }
        }
    }

    /// <summary>Completions after a <c>receiver.</c>: the receiver type's member variables and applicable
    /// member routines (wired internals, file-private secrets, and specialized-receiver mismatches filtered).</summary>
    private static void AddMemberCompletions(DocState doc, List<Dictionary<string, object?>> items,
        HashSet<string> seen, Token receiver, TypeSymbol receiverType)
    {
        // `secret` members are file-private — hide them from an outside `x.` completion, but show
        // them for `me.` (inside the type's own body they are accessible).
        bool includeSecret = receiver.Text == "me";

        foreach ((string name, string type, SourceLocation? location) in MemberVariableSignatures(type: receiverType,
                     includeSecret: includeSecret))
        {
            AddItem(items: items,
                seen: seen,
                label: name,
                kind: 5, // Field
                detail: $": {type}",
                documentation: DocAbove(location: location));
        }

        // Resolved own member routines — GetOwnMemberRoutinesResolved substitutes the generic
        // definition's methods for a concrete instantiation (so `List[FaceDraw].` shows `add_last`,
        // which the raw GetMemberRoutinesForType misses because methods register under `List[T]`).
        var ownMethods = doc.Registry
                            .GetOwnMemberRoutinesResolved(type: receiverType)
                            .ToList();

        // Methods whose SPECIALIZED receiver doesn't accept this instantiation (e.g.
        // `List[Agent[V]].gather` on a `List[FaceDraw]`). The builder-generated failable variants
        // (the try/grab/lookup variants of `gather`) carry no MeType, so key the rejection on the BASE name
        // and let a variant inherit its base's (in)applicability.
        var rejected = new HashSet<string>(collection: ownMethods
                                                      .Where(predicate: mr =>
                                                           !ReceiverAcceptsMethod(mr: mr,
                                                               receiverType: receiverType))
                                                      .Select(selector: mr => mr.Name),
            comparer: StringComparer.Ordinal);

        foreach (RoutineInfo mr in ownMethods.Where(predicate: mr =>
                     !mr.Name.StartsWith(value: '$') &&
                     (includeSecret || mr.Visibility != VisibilityModifier.Secret) &&
                     !IsMethodRejected(name: mr.Name, rejected: rejected)))
        {
            AddItem(items: items,
                seen: seen,
                label: mr.Name,
                kind: 2, // Method
                detail: RoutineDetail(r: mr),
                documentation: mr.Documentation);
        }
    }

    /// <summary>Returns true when the method name is in the rejected set.</summary>
    private static bool IsMethodRejected(string name, HashSet<string> rejected)
    {
        return rejected.Contains(item: name);
    }

    /// <summary>Global (non-member) completions: keywords, visible free routines, and this file's
    /// top-level declarations.</summary>
    private static void AddGlobalCompletions(DocState doc, List<Dictionary<string, object?>> items,
        HashSet<string> seen)
    {
        foreach (string kw in _profile.Keywords)
        {
            AddItem(items: items,
                seen: seen,
                label: kw,
                kind: 14); // Keyword
        }

        foreach (RoutineInfo r in doc.Registry.GetAllRoutines())
        {
            if (r.OwnerType == null && !r.Name.StartsWith(value: '$'))
            {
                AddItem(items: items,
                    seen: seen,
                    label: r.Name,
                    kind: 3, // FreeRoutine
                    detail: RoutineDetail(r: r),
                    documentation: r.Documentation);
            }
        }

        foreach (ISyntaxTreeNode node in AllNodes(program: doc.Program))
        {
            string tn = node.GetType()
                            .Name;
            if (tn.EndsWith(value: SuffixDeclaration, comparisonType: StringComparison.Ordinal) &&
                GetNameProp(node: node) is { } dn)
            {
                int kind = tn switch // Var=6, Func=3, Class/type=7
                {
                    "VariableDeclaration" => 6,
                    NodeRoutineDeclaration => 3,
                    _ => 7
                };
                AddItem(items: items,
                    seen: seen,
                    label: dn,
                    kind: kind,
                    documentation: node is SyntaxTree.Declaration declaration
                        ? declaration.Documentation ?? DocAbove(location: declaration.Location)
                        : null);
            }
        }
    }

    /// <summary>
    /// <c>completionItem/resolve</c>: promote the item's signature <c>detail</c> into a rendered
    /// markdown documentation panel. (There is no stored docstring to attach yet.)
    /// </summary>
    private static void HandleCompletionResolve(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement item))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        var resolved = new Dictionary<string, object?>();
        string? label = null;
        string? detail = null;
        if (item.TryGetProperty(propertyName: PropLabel, value: out JsonElement lbl))
        {
            label = lbl.GetString();
            resolved[key: PropLabel] = label;
        }

        if (item.TryGetProperty(propertyName: "kind", value: out JsonElement k) &&
            k.ValueKind == JsonValueKind.Number)
        {
            resolved[key: "kind"] = k.GetInt32();
        }

        if (item.TryGetProperty(propertyName: "detail", value: out JsonElement d))
        {
            detail = d.GetString();
            resolved[key: "detail"] = detail;
        }

        if (item.TryGetProperty(propertyName: "insertText", value: out JsonElement it))
        {
            resolved[key: "insertText"] = it.GetString();
        }

        // Keep a real doc-comment if completion already attached one; otherwise promote the signature
        // detail into a rendered panel so at least the type shows.
        if (item.TryGetProperty(propertyName: PropDocumentation,
                value: out JsonElement existingDoc))
        {
            string? docValue;
            if (existingDoc.ValueKind == JsonValueKind.String)
            {
                docValue = existingDoc.GetString();
            }
            else if (existingDoc.TryGetProperty(propertyName: PropValue,
                         value: out JsonElement dv))
            {
                docValue = dv.GetString();
            }
            else
            {
                docValue = null;
            }

            resolved[key: PropDocumentation] = new Dictionary<string, object?>
            {
                [key: "kind"] = PropMarkdown, [key: PropValue] = docValue ?? ""
            };
        }
        else if (!string.IsNullOrEmpty(value: detail))
        {
            resolved[key: PropDocumentation] = new Dictionary<string, object?>
            {
                [key: "kind"] = PropMarkdown,
                [key: PropValue] = $"```{_profile.CodeBlockLanguage}\n{label}{detail}\n```"
            };
        }

        WriteResult(stdout: stdout, id: id, result: resolved);
    }

    /// <summary>
    /// <c>textDocument/signatureHelp</c>: while the cursor is inside a call's argument list, show the
    /// callee's signature and highlight the active parameter. The enclosing call and the active-argument
    /// index are found from the token stream (balanced parens + comma count), then the callee name is
    /// resolved to a routine through the analyzed AST (falling back to a same-name free routine).
    /// </summary>
    /// <summary>Scans the tokens before the cursor (balanced parens + comma count) to find the call whose
    /// argument list the cursor is inside: its callee name, the callee's line, and the active-argument
    /// index. Returns null when the cursor is not inside any call's argument list.</summary>
    private static (string? Callee, int Line, int Commas)? EnclosingCall(DocState doc, int line0,
        int char0)
    {
        int line1 = line0 + 1;
        int col1 = char0 + 1;
        var pre = doc.Tokens
                     .Where(predicate: t =>
                          t.Type != TokenType.Newline && t.Type != TokenType.Eof &&
                          t.Text.Length > 0 &&
                          (t.Line < line1 || t.Line == line1 && t.Column < col1))
                     .OrderBy(keySelector: t => t.Line)
                     .ThenBy(keySelector: t => t.Column)
                     .ToList();

        var stack = new Stack<(string? Callee, int Line, int Commas)>();
        Token? prev = null;
        foreach (Token t in pre)
        {
            switch (t.Text)
            {
                case "(":
                    stack.Push(item: (prev is { Type: TokenType.Identifier }
                        ? prev.Text
                        : null, prev?.Line ?? t.Line, 0));
                    break;
                case ")" when stack.Count > 0:
                    stack.Pop();
                    break;
                case "," when stack.Count > 0:
                    (string? Callee, int Line, int Commas) top = stack.Pop();
                    stack.Push(item: (top.Callee, top.Line, top.Commas + 1));
                    break;
            }

            prev = t;
        }

        return stack.Count > 0
            ? stack.Peek()
            : null;
    }

    private static void HandleSignatureHelp(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        if (EnclosingCall(doc: doc, line0: line0, char0: char0) is not
            { Callee: not null } enclosing)
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        (string? calleeName, int calleeLine, int activeParam) = enclosing;
        RoutineInfo? routine =
            ResolveSignatureRoutine(doc: doc, calleeName: calleeName, calleeLine: calleeLine);
        if (routine == null)
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        WriteResult(stdout: stdout,
            id: id,
            result: BuildSignatureHelpResult(routine: routine, activeParam: activeParam));
    }

    /// <summary>Resolves the routine named by the callee at the given line, first from resolved call
    /// expressions (preferring the exact line), then from free routines in the registry.</summary>
    private static RoutineInfo? ResolveSignatureRoutine(DocState doc, string calleeName,
        int calleeLine)
    {
        RoutineInfo? routine = null;
        foreach (CallExpression call in AllNodes(program: doc.Program)
                    .OfType<CallExpression>())
        {
            if (call.ResolvedRoutine == null)
            {
                continue;
            }

            (string? cn, SourceLocation? cl) = CalleeName(callee: call.Callee);
            if (cn == calleeName)
            {
                routine = call.ResolvedRoutine;
                if (cl?.Line == calleeLine)
                {
                    return routine; // exact call at this line — best match
                }
            }
        }

        return routine ?? doc.Registry
                             .GetAllRoutines()
                             .FirstOrDefault(predicate: r =>
                                  r.Name == calleeName && r.OwnerType == null);
    }

    /// <summary>Builds the LSP <c>SignatureHelp</c> response object for a resolved routine.</summary>
    private static Dictionary<string, object?> BuildSignatureHelpResult(RoutineInfo routine,
        int activeParam)
    {
        // Pull per-parameter descriptions from the routine's `:param name:` doc fields.
        DocInfo? sigDoc = string.IsNullOrWhiteSpace(value: routine.Documentation)
            ? null
            : ParseDoc(doc: routine.Documentation);

        List<object?> parameters = BuildSignatureParameters(routine: routine, sigDoc: sigDoc);

        int active = routine.Parameters.Count == 0
            ? 0
            : Math.Min(val1: activeParam, val2: routine.Parameters.Count - 1);

        object? docEntry = string.IsNullOrWhiteSpace(value: sigDoc?.Summary)
            ? null
            : (object?)new Dictionary<string, object?>
            {
                [key: "kind"] = PropMarkdown, [key: PropValue] = sigDoc.Summary
            };

        return new Dictionary<string, object?>
        {
            [key: "signatures"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    [key: PropLabel] = Surface(text: $"{routine.Name}{RoutineDetail(r: routine)}"),
                    [key: "parameters"] = parameters,
                    // Signature-level doc = the SUMMARY only; per-parameter `:param:` text is attached to
                    // each parameter above, and Returns/Throws render in hover, so don't repeat them here.
                    [key: PropDocumentation] = docEntry
                }
            },
            [key: "activeSignature"] = 0,
            [key: "activeParameter"] = active
        };
    }

    /// <summary>Builds the parameter array for a signature help response, annotating each parameter
    /// with its doc-comment description when available.</summary>
    private static List<object?> BuildSignatureParameters(RoutineInfo routine, DocInfo? sigDoc)
    {
        return routine.Parameters
                      .Select(selector: p =>
                       {
                           var pdict = new Dictionary<string, object?>
                           {
                               [key: PropLabel] = Surface(text: $"{p.Name}: {TypeText(type: p.Type)}")
                           };
                           string? pdesc = sigDoc
                                         ?.Params.FirstOrDefault(predicate: x => x.Name == p.Name)
                                          .Desc;
                           if (!string.IsNullOrWhiteSpace(value: pdesc))
                           {
                               pdict[key: PropDocumentation] = pdesc;
                           }

                           return (object?)pdict;
                       })
                      .ToList();
    }

    /// <summary><c>textDocument/prepareRename</c>: the identifier span under the cursor, or null.</summary>
    private static void HandlePrepareRename(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        Token? hit = TokenAt(doc: doc, line0: line0, char0: char0);
        if (hit == null || !IsIdentifierText(text: hit.Text))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        int c = Math.Max(val1: 0, val2: hit.Column - 1);
        WriteResult(stdout: stdout,
            id: id,
            result: new Dictionary<string, object?>
            {
                [key: PropStart] =
                    new Dictionary<string, object?>
                    {
                        [key: "line"] = hit.Line - 1, [key: PropCharacter] = c
                    },
                [key: "end"] = new Dictionary<string, object?>
                {
                    [key: "line"] = hit.Line - 1,
                    [key: PropCharacter] = c + hit.Text.Length
                }
            });
    }

    /// <summary>
    /// <c>textDocument/rename</c>: rewrite every occurrence of the symbol under the cursor (same-file,
    /// binding-precise via <see cref="SymbolOccurrences"/>) to the new name, as a single WorkspaceEdit.
    /// </summary>
    private static void HandleRename(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!TryReadPosition(root: root,
                uri: out string uri,
                line0: out int line0,
                char0: out int char0) || !Docs.TryGetValue(key: uri, value: out DocState? doc) ||
            !root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: "newName", value: out JsonElement nn))
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        string newName = nn.GetString() ?? "";
        Token? hit = TokenAt(doc: doc, line0: line0, char0: char0);
        if (hit == null || !IsIdentifierText(text: hit.Text) || newName.Length == 0)
        {
            WriteResult(stdout: stdout, id: id, result: null);
            return;
        }

        var edits = new List<object?>();
        foreach ((int line, int col, int len) in SymbolOccurrences(doc: doc, hit: hit))
        {
            int l = Math.Max(val1: 0, val2: line - 1);
            int c = Math.Max(val1: 0, val2: col - 1);
            edits.Add(item: new Dictionary<string, object?>
            {
                [key: PropRange] = new Dictionary<string, object?>
                {
                    [key: PropStart] =
                        new Dictionary<string, object?>
                        {
                            [key: "line"] = l, [key: PropCharacter] = c
                        },
                    [key: "end"] =
                        new Dictionary<string, object?>
                        {
                            [key: "line"] = l, [key: PropCharacter] = c + len
                        }
                },
                [key: "newText"] = newName
            });
        }

        WriteResult(stdout: stdout,
            id: id,
            result: new Dictionary<string, object?>
            {
                [key: "changes"] = new Dictionary<string, object?> { [key: uri] = edits }
            });
    }

    /// <summary>
    /// <c>textDocument/semanticTokens/full</c>: every token's role, delta-encoded per the LSP spec. A type name
    /// is colored by its kind (a record or an entity, a protocol, a generic parameter), a routine wherever it is
    /// declared or called, a module path, a preset or global, and an operator. See <see cref="SemanticTokenTypes"/>.
    /// </summary>
    private static void HandleSemanticTokens(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl) ||
            !Docs.TryGetValue(key: uriEl.GetString() ?? "", value: out DocState? doc))
        {
            WriteResult(stdout: stdout,
                id: id,
                result: new Dictionary<string, object?> { [key: "data"] = new List<object?>() });
            return;
        }

        SemanticRoles roles = BuildSemanticRoles(nodes: AllNodes(program: doc.Program), tokens: doc.Tokens,
            registry: doc.Registry);
        List<object?> data = BuildDeltaEncodedTokens(doc: doc, roles: roles,
            text: Texts.GetValueOrDefault(key: uriEl.GetString() ?? ""));

        WriteResult(stdout: stdout,
            id: id,
            result: new Dictionary<string, object?> { [key: "data"] = data });
    }

    /// <summary>
    /// What the analyzed AST says about identifier positions: call callees, variables, presets and globals, dead
    /// (moved-out) uses, and the token kind of every position where a type is named, taken from what the analysis
    /// resolved there (never from looking a name up).
    /// </summary>
    private sealed record SemanticRoles(
        HashSet<(int, int)> Function,
        HashSet<(int, int)> Variable,
        HashSet<(int, int)> Constant,
        HashSet<(int, int)> Dead,
        Dictionary<(int, int), string> Types,
        Dictionary<(int, int), TypeSymbol> Symbols,
        Dictionary<(int, int), (TypeSymbol Owner, string Case)> Cases);

    /// <summary>Colors a position as the type resolved there, and keeps the type for hover.</summary>
    private static void AddType(SemanticRoles roles, (int, int) key, TypeSymbol type, bool replace = false)
    {
        string kind = TypeTokenKind(type: type);
        if (replace)
        {
            roles.Types[key: key] = kind;
            roles.Symbols[key: key] = type;
        }
        else if (roles.Types.TryAdd(key: key, value: kind))
        {
            roles.Symbols[key: key] = type;
        }
    }

    /// <summary>Colors a position as a case of a choice or a flags (a constant), and keeps whose case it is.</summary>
    private static void AddCase(SemanticRoles roles, (int, int) key, TypeSymbol owner, string name)
    {
        roles.Constant.Add(item: key);
        roles.Cases.TryAdd(key: key, value: (owner, name));
    }

    /// <summary>The case names of a choice or a flags type, or null for any other type.</summary>
    private static IEnumerable<string>? CaseNames(TypeSymbol? type)
    {
        return Rules.SurfaceType(type: type ?? ErrorTypeSymbol.Instance) switch
        {
            ChoiceTypeSymbol choice => choice.Cases.Select(selector: c => c.Name),
            FlagsTypeSymbol flags => flags.Members.Select(selector: m => m.Name),
            _ => null
        };
    }

    private static SemanticRoles BuildSemanticRoles(List<ISyntaxTreeNode> nodes, List<Token> tokens,
        TypeRegistry registry)
    {
        var roles = new SemanticRoles(Function: [], Variable: [], Constant: [], Dead: [], Types: [], Symbols: [],
            Cases: []);

        // The analysis keeps some resolutions on the symbol it built rather than on the syntax: a stdlib
        // signature, a generic routine's parameters, a field. Each such written type is paired with the slot of
        // that symbol it was resolved for, found by where it was declared.
        var declared = new Dictionary<(string File, int Line, int Column), TypeSymbol>();
        foreach (TypeSymbol type in registry.GetAllTypes()
                                            .Where(predicate: t => !t.IsGenericResolution))
        {
            if (type.Location is { FileName: { Length: > 0 } file } at)
            {
                declared.TryAdd(key: (Path.GetFullPath(path: file).ToUpperInvariant(), at.Line, at.Column), value: type);
            }
        }

        string? module = nodes.OfType<ModuleDeclaration>()
                              .FirstOrDefault()
                             ?.Path;
        foreach (ISyntaxTreeNode node in nodes)
        {
            PairDeclaredTypes(node: node, declared: declared, tokens: tokens, registry: registry, module: module,
                roles: roles);
        }

        // Inside a generic declaration its parameters shadow every other type (the resolver's own rule), also
        // where the shared tree carries the type of the last instance analyzed through it.
        List<(SyntaxTree.Declaration Declaration, List<string> Parameters, List<TypeExpression> Written)> scopes =
            nodes.OfType<SyntaxTree.Declaration>()
                 .Select(selector: d => (Declaration: d,
                      Parameters: ScopeParameters(declaration: d, registry: registry, module: module),
                      Written: WrittenTypesIn(declaration: d)))
                 .Where(predicate: scope => scope.Written.Count > 0)
                 .ToList();
        foreach ((_, List<string> parameters, List<TypeExpression> written) in scopes)
        {
            foreach (TypeExpression type in written.Where(predicate: w => parameters.Contains(item: w.Name)))
            {
                roles.Types.TryAdd(key: (type.Location.Line, type.Location.Column), value: "typeParameter");
            }
        }

        // A type written in a type position (`x: S64`, `List[T]`) that the analysis resolved in place.
        foreach (TypeExpression written in nodes.OfType<TypeExpression>())
        {
            PairWrittenType(written: written, resolved: written.ResolvedType, roles: roles);
        }

        // A type called to make a value (`List[S64]()`, `Point(x: 1)`) is colored as the type, as C# colors a
        // constructor.
        foreach (CreatorExpression creator in nodes.OfType<CreatorExpression>())
        {
            if (creator.ResolvedType is { } type)
            {
                AddType(roles: roles, key: (creator.Location.Line, creator.Location.Column), type: type);
            }
        }

        foreach (CallExpression call in nodes.OfType<CallExpression>())
        {
            if (call.Callee is not IdentifierExpression cid)
            {
                continue;
            }

            (int Line, int Column) key = (cid.Location.Line, cid.Location.Column);

            // Making a value: a creator, a record built field by field (no routine behind it), or a type's
            // conversion routine (`CStr(from: text)`). The callee names the type that was made.
            TypeSymbol? made = call.ResolvedRoutine switch
            {
                { Kind: RoutineKind.Creator } creator => call.ResolvedType ?? creator.OwnerType,
                null => call.ResolvedType,
                { OwnerType: { } owner } => owner,
                _ => null
            };
            if (made != null && ShortName(name: Rules.SurfaceType(type: made).BareName) == cid.Name)
            {
                AddType(roles: roles, key: key, type: made);
            }
            else
            {
                roles.Function.Add(item: key);
            }
        }

        foreach (IdentifierExpression ide in nodes.OfType<IdentifierExpression>())
        {
            (int Line, int Column) key = (ide.Location.Line, ide.Location.Column);
            if (ide.IsDeadUse)
            {
                roles.Dead.Add(item: key); // read after its ownership was moved out — grey it out
            }

            if (ide.ResolvedVariable == null && ide.ResolvedType is { } caseOwner &&
                (ide.ResolvedFlagsBit != null || CaseNames(type: caseOwner)?.Contains(value: ide.Name) == true))
            {
                AddCase(roles: roles, key: key, owner: caseOwner, name: ide.Name);
            }
            else if (ide.IsModuleGlobal || ide.ResolvedVariable is { IsPreset: true } or { IsGlobal: true })
            {
                roles.Constant.Add(item: key);
            }
            else if (ide.ResolvedVariable != null)
            {
                roles.Variable.Add(item: key);
            }
            else if (ide.ResolvedType is { } type && ShortName(name: Rules.SurfaceType(type: type).BareName) == ide.Name)
            {
                // The type itself used as a value (`S64.MAX`, `Text.from(...)`).
                AddType(roles: roles, key: key, type: type);
            }
        }

        foreach (MemberExpression member in nodes.OfType<MemberExpression>())
        {
            if (member.ResolvedType is { } owner && CaseNames(type: owner)?.Contains(value: member.MemberName) == true &&
                MemberNameToken(tokens: tokens, member: member) is { } name)
            {
                AddCase(roles: roles, key: (name.Line, name.Column), owner: owner, name: member.MemberName);
            }
        }

        foreach (RoutineDeclaration routine in nodes.OfType<RoutineDeclaration>())
        {
            AddUnresolvedCases(routine: routine, roles: roles);
        }

        foreach (ISyntaxTreeNode node in nodes)
        {
            AddDeclarationRoles(node: node, tokens: tokens, roles: roles);
        }

        // What is left was never resolved by the build (a generic definition's body is analyzed per instance):
        // resolved here the way the build resolves a stdlib signature, in the declaration's generic scope.
        foreach ((SyntaxTree.Declaration declaration, List<string> parameters, List<TypeExpression> written) in scopes)
        {
            foreach (TypeExpression type in written.Where(predicate: w =>
                         !roles.Types.ContainsKey(key: (w.Location.Line, w.Location.Column))))
            {
                PairWrittenType(written: type,
                    resolved: StdlibLoader.ResolveWrittenType(registry: registry, typeExpr: type,
                        genericParams: parameters, moduleName: module),
                    roles: roles);
            }

            // `Copyable onlyif T obeys Assignable`: the parameter a conformance condition is about.
            foreach (TypeExpression type in written.Where(predicate: w => w.ConformanceConditions != null))
            {
                foreach (GenericConstraintDeclaration condition in type.ConformanceConditions!)
                {
                    if (TokenOnLine(tokens: tokens, line: condition.Location?.Line ?? type.Location.Line,
                            text: condition.ParameterName) is { } parameter)
                    {
                        roles.Types.TryAdd(key: (parameter.Line, parameter.Column), value: "typeParameter");
                    }
                }
            }

            AddTypesUsedAsValues(declaration: declaration, parameters: parameters, registry: registry, module: module,
                roles: roles);
        }

        return roles;
    }

    /// <summary>
    /// A type named where a value goes, in a body the build never analyzed (a generic definition's): the receiver
    /// of a member access or call (<c>T.data_size()</c>, <c>U128.from_bytes_le(...)</c>, <c>List[T]()</c>) or a
    /// callee (<c>S128(...)</c>). A generic parameter is one by scope, any other name by the resolver a stdlib signature
    /// goes through.
    /// </summary>
    private static void AddTypesUsedAsValues(SyntaxTree.Declaration declaration, List<string> parameters,
        TypeRegistry registry, string? module, SemanticRoles roles)
    {
        var acc = new List<ISyntaxTreeNode>();
        CollectAllNodes(node: declaration, acc: acc,
            seen: new HashSet<object>(comparer: ReferenceEqualityComparer.Instance));
        IEnumerable<IdentifierExpression> named = acc.OfType<MemberExpression>()
                                                     .Select(selector: m => m.Object)
                                                     .Concat(second: acc.OfType<GenericMemberRoutineCallExpression>()
                                                                        .Select(selector: g => g.Object))
                                                     .Concat(second: acc.OfType<CallExpression>()
                                                                        .Select(selector: c => c.Callee))
                                                     .OfType<IdentifierExpression>()
                                                     .Where(predicate: e => e.ResolvedVariable == null &&
                                                                            e.ResolvedType == null && e.Realm == null);
        // `List[T](...)` in a generic definition's body: a creator nothing resolved.
        foreach (CreatorExpression creator in acc.OfType<CreatorExpression>()
                                                 .Where(predicate: c => c.ResolvedType == null))
        {
            (int Line, int Column) key = (creator.Location.Line, creator.Location.Column);
            if (!roles.Types.ContainsKey(key: key) &&
                StdlibLoader.ResolveWrittenType(registry: registry,
                    typeExpr: new TypeExpression(Name: creator.TypeName, GenericArguments: creator.TypeArguments,
                        Location: creator.Location),
                    genericParams: parameters, moduleName: module) is { } made and not ErrorTypeSymbol)
            {
                AddType(roles: roles, key: key, type: made, replace: true);
            }
        }

        foreach (IdentifierExpression name in named)
        {
            (int Line, int Column) key = (name.Location.Line, name.Location.Column);
            if (roles.Types.ContainsKey(key: key))
            {
                continue;
            }

            if (parameters.Contains(item: name.Name))
            {
                roles.Types[key: key] = "typeParameter";
            }
            else if (StdlibLoader.ResolveWrittenType(registry: registry,
                         typeExpr: new TypeExpression(Name: name.Name, GenericArguments: null, Location: name.Location),
                         genericParams: parameters, moduleName: module) is { } type and not ErrorTypeSymbol)
            {
                AddType(roles: roles, key: key, type: type, replace: true);
            }
        }
    }

    /// <summary>
    /// The generic parameters in scope inside a declaration: its own, and for a member routine its owner's
    /// (<c>routine List[T].add_last</c> sees <c>T</c>): a receiver argument is a parameter unless it names a type
    /// (<c>Iterable[Text].join</c> specializes), the rule the build resolves receivers by.
    /// </summary>
    private static List<string> ScopeParameters(SyntaxTree.Declaration declaration, TypeRegistry registry,
        string? module)
    {
        var parameters = new List<string>();
        if (declaration.GetType()
                       .GetProperty(name: "GenericParameters")
                      ?.GetValue(obj: declaration) is List<string> own)
        {
            parameters.AddRange(collection: own);
        }

        if (declaration is RoutineDeclaration { ReceiverType.GenericArguments: { } receiverArgs })
        {
            parameters.AddRange(collection: receiverArgs
                                           .Where(predicate: a => a.GenericArguments is not { Count: > 0 } &&
                                                                  StdlibLoader.ResolveWrittenType(registry: registry,
                                                                      typeExpr: a, genericParams: null,
                                                                      moduleName: module) == null)
                                           .Select(selector: a => a.Name));
        }

        if (declaration is RoutineDeclaration { ResolvedInfo.OwnerType: { } owner })
        {
            parameters.AddRange(collection: owner switch
            {
                RecordTypeSymbol { GenericDefinition: { } definition } => definition.GenericParameters ?? [],
                EntityTypeSymbol { GenericDefinition: { } definition } => definition.GenericParameters ?? [],
                _ => owner.GenericParameters ?? []
            });
        }

        return parameters;
    }

    /// <summary>Every type a declaration writes, in its header, its constraints, its members and its body (a nested
    /// declaration is also a scope of its own, and its parameters were claimed before any outer resolution).</summary>
    private static List<TypeExpression> WrittenTypesIn(SyntaxTree.Declaration declaration)
    {
        var acc = new List<ISyntaxTreeNode>();
        CollectAllNodes(node: declaration, acc: acc,
            seen: new HashSet<object>(comparer: ReferenceEqualityComparer.Instance));
        return acc.OfType<TypeExpression>()
                  .ToList();
    }

    /// <summary>
    /// Colors a written type and, slot by slot, its written generic arguments by what the analysis resolved for
    /// them (<c>List[T]</c> against <c>List[T]</c>: the list, then the parameter).
    /// </summary>
    private static void PairWrittenType(TypeExpression? written, TypeSymbol? resolved, SemanticRoles roles)
    {
        if (written == null || resolved == null || resolved is ErrorTypeSymbol)
        {
            return;
        }

        TypeSymbol shown = Rules.SurfaceType(type: resolved);

        // Desugaring wraps a written type at its own position (a variadic `items...: T` becomes the array of its
        // arguments, `Array[T, N]`, and `T?` a `Maybe[T]`): only the wrapped type was written there.
        if (written.GenericArguments is [var wrapped, ..] &&
            wrapped.Location.Line == written.Location.Line && wrapped.Location.Column == written.Location.Column)
        {
            PairWrittenType(written: wrapped,
                resolved: shown.TypeArguments is [var wrappedType, ..] ? wrappedType : null,
                roles: roles);
            return;
        }

        AddType(roles: roles, key: (written.Location.Line, written.Location.Column), type: shown);
        if (written.GenericArguments is { Count: > 0 } writtenArgs && shown.TypeArguments is { } resolvedArgs &&
            writtenArgs.Count == resolvedArgs.Count)
        {
            for (int i = 0; i < writtenArgs.Count; i++)
            {
                PairWrittenType(written: writtenArgs[index: i], resolved: resolvedArgs[index: i], roles: roles);
            }
        }
    }

    /// <summary>
    /// Pairs the types a declaration writes with what the analysis resolved for them on its symbol: a routine's
    /// parameters and result (and its owner's generic parameters), a type's fields, protocols, associated-type
    /// bindings and variant members.
    /// </summary>
    private static void PairDeclaredTypes(ISyntaxTreeNode node,
        Dictionary<(string File, int Line, int Column), TypeSymbol> declared, List<Token> tokens, TypeRegistry registry,
        string? module, SemanticRoles roles)
    {
        if (node is RoutineDeclaration { ResolvedInfo: { } routine } routineDecl)
        {
            foreach (Parameter parameter in routineDecl.Parameters)
            {
                PairWrittenType(written: parameter.Type,
                    resolved: routine.Parameters.FirstOrDefault(predicate: p => p.Name == parameter.Name)?.Type,
                    roles: roles);
            }

            PairWrittenType(written: routineDecl.ReturnType, resolved: routine.ReturnType, roles: roles);

            // `routine List[T].add_last`: the owner's own parameters, written in the header.
            TypeSymbol? owner = routine.OwnerType;
            List<string>? ownerParameters = owner switch
            {
                RecordTypeSymbol { GenericDefinition: { } definition } => definition.GenericParameters,
                EntityTypeSymbol { GenericDefinition: { } definition } => definition.GenericParameters,
                _ => owner?.GenericParameters
            };
            if (ownerParameters != null)
            {
                foreach (Token parameter in tokens.Where(predicate: t =>
                             t.Line == routineDecl.Location.Line && t.Type == TokenType.Identifier &&
                             ownerParameters.Contains(item: t.Text)))
                {
                    roles.Types.TryAdd(key: (parameter.Line, parameter.Column), value: "typeParameter");
                }
            }

            return;
        }

        if (node is SyntaxTree.Declaration obeying &&
            obeying.GetType()
                   .GetProperty(name: "Protocols")
                  ?.GetValue(obj: obeying) is List<TypeExpression> obeyedProtocols)
        {
            foreach (TypeExpression protocol in obeyedProtocols)
            {
                // The protocol as the resolver reads it from here, for hover; an opt-in one (`obeys Equatable`) is
                // not among the protocols the type's symbol lists.
                PairWrittenType(written: protocol,
                    resolved: StdlibLoader.ResolveWrittenType(registry: registry, typeExpr: protocol,
                        genericParams: null, moduleName: module),
                    roles: roles);
                roles.Types.TryAdd(key: (protocol.Location.Line, protocol.Location.Column), value: "interface");
            }
        }

        if (node is not SyntaxTree.Declaration { Location.FileName: { Length: > 0 } file } typeDecl ||
            !declared.TryGetValue(key: (Path.GetFullPath(path: file).ToUpperInvariant(), typeDecl.Location.Line,
                typeDecl.Location.Column), value: out TypeSymbol? symbol))
        {
            return;
        }

        if (typeDecl.GetType()
                    .GetProperty(name: "Name")
                   ?.GetValue(obj: typeDecl) is string name &&
            TokenOnLine(tokens: tokens, line: typeDecl.Location.Line, text: name) is { } nameToken)
        {
            AddType(roles: roles, key: (nameToken.Line, nameToken.Column), type: symbol, replace: true);
        }

        (List<MemberVariableInfo> fields, List<TypeSymbol> protocols, Dictionary<string, TypeSymbol> bindings) = symbol switch
        {
            EntityTypeSymbol entity => (entity.MemberVariables, entity.ImplementedProtocols, entity.AssociatedTypeBindings),
            RecordTypeSymbol record => (record.MemberVariables, record.ImplementedProtocols, record.AssociatedTypeBindings),
            _ => ([], [], [])
        };

        List<SyntaxTree.Declaration> members = typeDecl switch
        {
            EntityDeclaration entityDecl => entityDecl.Members,
            RecordDeclaration recordDecl => recordDecl.Members,
            CrashableDeclaration crashableDecl => crashableDecl.Members,
            _ => []
        };
        foreach (VariableDeclaration field in members.OfType<VariableDeclaration>())
        {
            PairWrittenType(written: field.Type,
                resolved: fields.FirstOrDefault(predicate: f => f.Name == field.Name)?.Type,
                roles: roles);
        }

        if (typeDecl.GetType()
                    .GetProperty(name: "Protocols")
                   ?.GetValue(obj: typeDecl) is List<TypeExpression> obeyed)
        {
            foreach (TypeExpression protocol in obeyed)
            {
                PairWrittenType(written: protocol,
                    resolved: protocols.FirstOrDefault(predicate: p =>
                                  ShortName(name: p.BareName) == ShortName(name: protocol.Name)) ??
                              StdlibLoader.ResolveWrittenType(registry: registry, typeExpr: protocol,
                                  genericParams: symbol.GenericParameters, moduleName: module),
                    roles: roles);
                roles.Types.TryAdd(key: (protocol.Location.Line, protocol.Location.Column), value: "interface");
            }
        }

        if (typeDecl.GetType()
                    .GetProperty(name: "AssociatedTypes")
                   ?.GetValue(obj: typeDecl) is List<AssociatedTypeDeclaration> associated)
        {
            foreach (AssociatedTypeDeclaration slot in associated)
            {
                PairWrittenType(written: slot.Binding, resolved: bindings.GetValueOrDefault(key: slot.Name), roles: roles);
                if (slot.Constraint != null)
                {
                    roles.Types.TryAdd(key: (slot.Constraint.Location.Line, slot.Constraint.Location.Column),
                        value: "interface");
                }
            }
        }

        if (typeDecl is ChoiceDeclaration choiceDecl)
        {
            foreach (ChoiceCase choiceCase in choiceDecl.Cases)
            {
                if (TokenOnLine(tokens: tokens, line: choiceCase.Location.Line, text: choiceCase.Name) is { } caseToken)
                {
                    AddCase(roles: roles, key: (caseToken.Line, caseToken.Column), owner: symbol, name: choiceCase.Name);
                }
            }
        }

        if (typeDecl is FlagsDeclaration flagsDecl)
        {
            foreach (Token member in FlagsMemberTokens(tokens: tokens, flags: flagsDecl))
            {
                AddCase(roles: roles, key: (member.Line, member.Column), owner: symbol, name: member.Text);
            }
        }

        if (typeDecl is VariantDeclaration variantDecl && symbol is VariantTypeSymbol variant)
        {
            List<VariantMemberInfo> arms = variant.Members.Where(predicate: m => !m.IsNone)
                                                  .ToList();
            List<VariantMember> writtenArms = variantDecl.Members.Where(predicate: m => m.Type.Name != "None")
                                                         .ToList();
            for (int i = 0; i < Math.Min(val1: arms.Count, val2: writtenArms.Count); i++)
            {
                PairWrittenType(written: writtenArms[index: i].Type, resolved: arms[index: i].Type, roles: roles);
            }
        }
    }

    /// <summary>
    /// The names a declaration introduces: a type's name (its kind is the declaration's own), its generic
    /// parameters and associated types, the kind a constraint names, and for a routine its name and the type it
    /// belongs to (<c>routine Account.deposit</c>).
    /// </summary>
    private static void AddDeclarationRoles(ISyntaxTreeNode node, List<Token> tokens, SemanticRoles roles)
    {
        if (node is not SyntaxTree.Declaration declaration)
        {
            return;
        }

        int line = declaration.Location.Line;

        // `needs T obeys Comparable`, `T is EntityType`: the parameter, and the kind a kind constraint names.
        if (declaration.GetType()
                       .GetProperty(name: "GenericConstraints")
                      ?.GetValue(obj: declaration) is List<GenericConstraintDeclaration> constraints)
        {
            foreach (GenericConstraintDeclaration constraint in constraints)
            {
                int at = constraint.Location?.Line ?? line;
                if (TokenOnLine(tokens: tokens, line: at, text: constraint.ParameterName) is { } parameter)
                {
                    roles.Types.TryAdd(key: (parameter.Line, parameter.Column), value: "typeParameter");
                }

                if (ConstraintKindTokens.TryGetValue(key: constraint.ConstraintType,
                        value: out (string Spelling, string Kind) named) &&
                    TokenOnLine(tokens: tokens, line: at, text: named.Spelling) is { } spelled)
                {
                    roles.Types.TryAdd(key: (spelled.Line, spelled.Column), value: named.Kind);
                }
            }
        }

        // `relates ListEmittable[T] as Iter`: the associated-type slot stands for a type like a parameter.
        if (declaration.GetType()
                       .GetProperty(name: "AssociatedTypes")
                      ?.GetValue(obj: declaration) is List<AssociatedTypeDeclaration> associated)
        {
            foreach (AssociatedTypeDeclaration slot in associated)
            {
                if (TokenOnLine(tokens: tokens, line: slot.Location?.Line ?? line, text: slot.Name) is { } name)
                {
                    roles.Types.TryAdd(key: (name.Line, name.Column), value: "typeParameter");
                }
            }
        }
        string? kind = declaration switch
        {
            RecordDeclaration or ChoiceDeclaration or FlagsDeclaration or CrashableDeclaration => "recordType",
            EntityDeclaration => "entityType",
            ProtocolDeclaration => "interface",
            VariantDeclaration variant => variant.Members.Any(predicate: m =>
                m.Type.ResolvedType is { } armType && TypeTokenKind(type: armType) == "entityType")
                ? "entityType"
                : "recordType",
            _ => null
        };
        string? typeName = declaration.GetType()
                                      .GetProperty(name: "Name")
                                     ?.GetValue(obj: declaration) as string;
        if (kind != null && typeName != null && TokenOnLine(tokens: tokens, line: line, text: typeName) is { } declared)
        {
            roles.Types.TryAdd(key: (declared.Line, declared.Column), value: kind);
        }

        if (declaration is RoutineDeclaration { ResolvedInfo: { } routine })
        {
            // `routine Account.deposit`: the first name after `routine` is the owner when there is one.
            List<Token> header = tokens.Where(predicate: t => t.Line == line && t.Type == TokenType.Identifier)
                                       .OrderBy(keySelector: t => t.Column)
                                       .ToList();
            if (routine.OwnerType is { } owner && header.Count > 1)
            {
                AddType(roles: roles, key: (header[index: 0].Line, header[index: 0].Column), type: owner);
            }

            if (header.FirstOrDefault(predicate: t => t.Text == routine.Name &&
                                                      (routine.OwnerType == null || t != header[index: 0])) is { } name)
            {
                roles.Function.Add(item: (name.Line, name.Column));
            }
        }

        if (declaration.GetType()
                       .GetProperty(name: "GenericParameters")
                      ?.GetValue(obj: declaration) is List<string> parameters)
        {
            foreach (Token parameter in tokens.Where(predicate: t =>
                         t.Line == line && t.Type == TokenType.Identifier && parameters.Contains(item: t.Text)))
            {
                roles.Types.TryAdd(key: (parameter.Line, parameter.Column), value: "typeParameter");
            }
        }
    }

    /// <summary>The member names of a flags declaration, as written in its body (the lines below its header, up to
    /// the next line that starts at the header's own indentation or less).</summary>
    private static IEnumerable<Token> FlagsMemberTokens(List<Token> tokens, FlagsDeclaration flags)
    {
        int header = flags.Location.Line;
        int end = tokens.Where(predicate: t => t.Line > header && t.Type == TokenType.Identifier &&
                                               t.Column <= flags.Location.Column)
                        .Select(selector: t => t.Line)
                        .DefaultIfEmpty(defaultValue: int.MaxValue)
                        .Min();
        return tokens.Where(predicate: t => t.Line > header && t.Line < end && t.Type == TokenType.Identifier &&
                                            flags.Members.Contains(item: t.Text));
    }

    /// <summary>The token of a member access's member name: the first one spelled so after the object starts.</summary>
    private static Token? MemberNameToken(List<Token> tokens, MemberExpression member)
    {
        return tokens.Where(predicate: t => t.Line == member.Location.Line && t.Column > member.Location.Column &&
                                            t.Type == TokenType.Identifier && t.Text == member.MemberName)
                     .MinBy(keySelector: t => t.Column);
    }

    /// <summary>
    /// In a body the build never analyzed (a generic definition's), a bare name that is a case of a choice or flags
    /// the routine's own signature names (its result, a parameter, its owner): what the build resolves it to by the
    /// expected type.
    /// </summary>
    private static void AddUnresolvedCases(RoutineDeclaration routine, SemanticRoles roles)
    {
        if (routine.ResolvedInfo is not { } info)
        {
            return;
        }

        List<TypeSymbol> signature = info.Parameters.Select(selector: p => p.Type)
                                         .Append(element: info.ReturnType ?? ErrorTypeSymbol.Instance)
                                         .Append(element: info.OwnerType ?? ErrorTypeSymbol.Instance)
                                         .Where(predicate: t => CaseNames(type: t) != null)
                                         .ToList();
        if (signature.Count == 0)
        {
            return;
        }

        var acc = new List<ISyntaxTreeNode>();
        CollectAllNodes(node: routine.Body, acc: acc,
            seen: new HashSet<object>(comparer: ReferenceEqualityComparer.Instance));
        foreach (IdentifierExpression name in acc.OfType<IdentifierExpression>()
                                                 .Where(predicate: e => e.ResolvedVariable == null && e.ResolvedType == null))
        {
            if (signature.FirstOrDefault(predicate: t => CaseNames(type: t)!.Contains(value: name.Name)) is { } owner)
            {
                AddCase(roles: roles, key: (name.Location.Line, name.Location.Column), owner: owner, name: name.Name);
            }
        }
    }

    /// <summary>The first identifier token on a line spelled <paramref name="text"/>.</summary>
    private static Token? TokenOnLine(List<Token> tokens, int line, string text)
    {
        return tokens.Where(predicate: t => t.Line == line && t.Type == TokenType.Identifier && t.Text == text)
                     .MinBy(keySelector: t => t.Column);
    }

    // A kind constraint (`needs T is RecordType`) names a kind of type: it takes that kind's color. The others
    // (`AnyType`, `RedirectType`) bound a parameter the way a protocol does.
    private static readonly Dictionary<ConstraintKind, (string Spelling, string Kind)> ConstraintKindTokens = new()
    {
        [key: ConstraintKind.RecordType] = ("RecordType", "recordType"),
        [key: ConstraintKind.OwningValueType] = ("OwningValueType", "owningValueType"),
        [key: ConstraintKind.ChoiceType] = ("ChoiceType", "recordType"),
        [key: ConstraintKind.FlagsType] = ("FlagsType", "recordType"),
        [key: ConstraintKind.Crashable] = ("CrashableType", "recordType"),
        [key: ConstraintKind.RoutineType] = ("RoutineType", "recordType"),
        [key: ConstraintKind.TupleType] = ("TupleType", "recordType"),
        [key: ConstraintKind.VariantType] = ("VariantType", "recordType"),
        [key: ConstraintKind.EntityType] = ("EntityType", "entityType"),
        [key: ConstraintKind.AnyType] = ("AnyType", "interface"),
        [key: ConstraintKind.RedirectType] = ("RedirectType", "interface")
    };

    /// <summary>Encodes the document's tokens as a delta-encoded LSP semantic token data array
    /// (deltaLine, deltaChar, length, tokenType, modifiers per token).</summary>
    private static List<object?> BuildDeltaEncodedTokens(DocState doc, SemanticRoles roles, string? text)
    {
        var toks = doc.Tokens
                      .Where(predicate: t =>
                           t.Type != TokenType.Newline && t.Type != TokenType.Eof &&
                           t.Text.Length > 0)
                      .OrderBy(keySelector: t => t.Line)
                      .ThenBy(keySelector: t => t.Column)
                      .ToList();

        var spans = new List<(int Line, int Column, int Length, int Type, int Mods)>();
        int modulePathLine = -1; // the line of an `import` / `module` header: its names are a module path
        for (int i = 0; i < toks.Count; i++)
        {
            Token t = toks[index: i];
            if (t.Type is TokenType.Import or TokenType.Module)
            {
                modulePathLine = t.Line;
            }

            // `@` only opens an annotation, and the word right after it (a keyword too, `@inline`) is its name.
            bool annotation = t.Type == TokenType.At ||
                              i > 0 && toks[index: i - 1].Type == TokenType.At &&
                              toks[index: i - 1].Line == t.Line && toks[index: i - 1].Column + 1 == t.Column;
            int type = annotation
                ? SemTok(name: "decorator")
                : t.Type == TokenType.Identifier
                    ? ClassifyIdentifierToken(doc: doc, toks: toks, at: i, roles: roles,
                        modulePathLine: modulePathLine)
                    : ClassifyNonIdentifierToken(t: t);
            if (type < 0)
            {
                continue; // punctuation, text, comments — left to the TextMate grammar
            }

            int mods = roles.Dead.Contains(item: (t.Line, t.Column))
                ? ModDeprecated
                : 0;
            spans.Add(item: (t.Line, t.Column, t.Text.Length, type, mods));
        }

        if (text != null)
        {
            spans.AddRange(collection: DocCommentSpans(text: text)
               .Select(selector: d => (d.Line, d.Column, d.Length, SemTok(name: d.Kind), 0)));
        }

        var data = new List<object?>();
        int prevLine = 0;
        int prevChar = 0;
        foreach ((int line, int column, int length, int type, int mods) in spans.OrderBy(keySelector: x => x.Line)
                                                                                .ThenBy(keySelector: x => x.Column))
        {
            int line0 = line - 1;
            int char0 = column - 1;
            int deltaLine = line0 - prevLine;
            int deltaChar = deltaLine == 0
                ? char0 - prevChar
                : char0;
            data.Add(item: deltaLine);
            data.Add(item: deltaChar);
            data.Add(item: length);
            data.Add(item: type);
            data.Add(item: mods);
            prevLine = line0;
            prevChar = char0;
        }

        return data;
    }

    // A field: `:param name:` and `:typeparam Name:` name what they describe, the others (`:returns:`) don't.
    private static readonly System.Text.RegularExpressions.Regex DocField =
        new(pattern: @"^\s*(:(?:param|typeparam)(?=\s)|:(?:returns|throws|absent|note|see)(?=:))(?:\s+(\S+?))?(:)");

    private static readonly System.Text.RegularExpressions.Regex DocReference = new(pattern: @"\{[^{}\s][^{}]*\}");

    /// <summary>
    /// The parts of a <c>###</c> doc comment that are its structure rather than its prose: a field
    /// (<c>:param name:</c>, <c>:returns:</c>) and a reference in braces (<c>{List[T].add_last}</c>). The comment's
    /// token holds its text without the marker, so they are found in the document's lines.
    /// </summary>
    private static IEnumerable<(int Line, int Column, int Length, string Kind)> DocCommentSpans(string text)
    {
        string[] lines = text.ReplaceLineEndings(replacementText: "\n")
                             .Split(separator: '\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int at = line.IndexOf(value: "###", comparisonType: StringComparison.Ordinal);
            if (at < 0 || line[..at].Trim().Length > 0)
            {
                continue;
            }

            int start = at + 3;
            string content = line[start..];
            if (DocField.Match(input: content) is { Success: true } field)
            {
                yield return (i + 1, start + field.Groups[groupnum: 1].Index + 1, field.Groups[groupnum: 1].Length, "docTag");
                if (field.Groups[groupnum: 2].Success)
                {
                    yield return (i + 1, start + field.Groups[groupnum: 2].Index + 1, field.Groups[groupnum: 2].Length,
                        "docValue");
                }

                yield return (i + 1, start + field.Groups[groupnum: 3].Index + 1, 1, "docTag");
            }

            foreach (System.Text.RegularExpressions.Match reference in DocReference.Matches(input: content))
            {
                yield return (i + 1, start + reference.Index + 1, reference.Length, "docValue");
            }
        }
    }

    /// <summary>
    /// Classifies an <c>Identifier</c> token. In order: a module path (and a realm before <c>::</c>); a preset or
    /// global; a variable; a type, by the kind the analysis resolved at that position; a routine (a resolved
    /// callee or declaration, or a name followed by its argument list, which covers member calls). A name the
    /// analysis resolved to nothing is left to the TextMate grammar.
    /// </summary>
    private static int ClassifyIdentifierToken(DocState doc, List<Token> toks, int at, SemanticRoles roles,
        int modulePathLine)
    {
        Token t = toks[index: at];
        (int, int) key = (t.Line, t.Column);
        if (t.Line == modulePathLine ||
            at + 1 < toks.Count && toks[index: at + 1].Type == TokenType.DoubleColon)
        {
            return SemTok(name: "namespace");
        }

        if (roles.Constant.Contains(item: key) ||
            at > 0 && toks[index: at - 1].Type is TokenType.Preset or TokenType.Global)
        {
            return SemTok(name: "constant");
        }

        if (roles.Variable.Contains(item: key))
        {
            return SemTok(name: "variable");
        }

        if (roles.Types.TryGetValue(key: key, value: out string? kind))
        {
            return SemTok(name: kind);
        }

        if (roles.Function.Contains(item: key) || OpensArgumentList(toks: toks, at: at))
        {
            return SemTok(name: "function");
        }

        // A preset the analysis inlined leaves no variable behind at its use.
        if (doc.Registry.LookupVariable(name: t.Text) is { IsPreset: true } or { IsGlobal: true })
        {
            return SemTok(name: "constant");
        }

        return -1;
    }

    /// <summary>Whether the name at <paramref name="at"/> is followed by an argument list, directly
    /// (<c>name(</c>) or after its generic arguments (<c>name[T](</c>).</summary>
    private static bool OpensArgumentList(List<Token> toks, int at)
    {
        int next = at + 1;
        if (next < toks.Count && toks[index: next].Type == TokenType.LeftBracket)
        {
            int depth = 0;
            for (; next < toks.Count; next++)
            {
                if (toks[index: next].Type == TokenType.LeftBracket)
                {
                    depth++;
                }
                else if (toks[index: next].Type == TokenType.RightBracket && --depth == 0)
                {
                    next++;
                    break;
                }
            }
        }

        return next < toks.Count && toks[index: next].Type == TokenType.LeftParen &&
               toks[index: next].Line == toks[index: at].Line;
    }

    /// <summary>
    /// The token kind of a type, as the served language's user knows it. A crashable reads as a record (it is
    /// checked before entity, which it extends), a variant is an entity when one of its members is, a routine type
    /// is a value like a record, and <c>Me</c> or an associated type stands for a type the way a generic
    /// parameter does.
    /// </summary>
    private static string TypeTokenKind(TypeSymbol type)
    {
        return Rules.SurfaceType(type: type) switch
        {
            CrashableTypeSymbol => "recordType",
            EntityTypeSymbol => "entityType",
            VariantTypeSymbol variant => variant.Members.Any(predicate: m =>
                m.Type != null && Rules.SurfaceType(type: m.Type) is EntityTypeSymbol)
                ? "entityType"
                : "recordType",
            RecordTypeSymbol or RoutineTypeSymbol => "recordType",
            ProtocolTypeSymbol => "interface",
            GenericParameterTypeSymbol or ProtocolSelfTypeSymbol or AssociatedProjectionTypeSymbol => "typeParameter",
            _ => "type"
        };
    }

    /// <summary>Classifies a non-identifier token as comment, string, number, keyword, operator, or -1
    /// (punctuation, left to the TextMate grammar).</summary>
    private static int ClassifyNonIdentifierToken(Token t)
    {
        // A comment or text token holds its content without its markers (`###`, the quotes), so its span can't be
        // taken from it: the TextMate grammar colors comments and text, interpolation included.
        string tn = t.Type.ToString();
        if (tn.Contains(value: "Comment") || tn.Contains(value: "Text") || t.Type == TokenType.CharacterLiteral ||
            t.Text[index: 0] is '"' or '\'')
        {
            return -1;
        }

        // Numeric literals: the suffixed *Literal kinds AND the pre-resolution "UndecidedInteger" /
        // "UndecidedFloat" a bare literal starts as (its width is only chosen later, in SA).
        if (tn.EndsWith(value: "Literal", comparisonType: StringComparison.Ordinal) ||
            tn.StartsWith(value: "Undecided", comparisonType: StringComparison.Ordinal) ||
            char.IsDigit(c: t.Text[index: 0]))
        {
            return SemTok(name: "number");
        }

        // A word-shaped non-identifier token is a keyword (routine, entity, if, each, true, ...).
        if (char.IsLetter(c: t.Text[index: 0]) || t.Text[index: 0] == '_')
        {
            return SemTok(name: "keyword");
        }

        // Brackets, separators and the `:` of a binding are punctuation. Every other symbol is an operator.
        return t.Text is "(" or ")" or "[" or "]" or "{" or "}" or "," or "." or ":" or "::" or ";"
            ? -1
            : SemTok(name: "operator");
    }

    // LSP SymbolKind numbers used below: File=1 Module=2 Namespace=3 Class=5 Method=6 Property=7 Field=8
    // Enum=10 Interface=11 FreeRoutine=12 Variable=13 Constant=14 Struct=23.
    private static (int Kind, bool IsType) SymbolKindOf(string declTypeName)
    {
        return declTypeName switch
        {
            NodeRoutineDeclaration => (12, false),
            "RecordDeclaration" => (23, true),
            "EntityDeclaration" => (5, true),
            "ChoiceDeclaration" => (10, true),
            "VariantDeclaration" => (10, true),
            "FlagsDeclaration" => (10, true),
            "CrashableDeclaration" => (10, true),
            "ProtocolDeclaration" => (11, true),
            "VariableDeclaration" => (13, false),
            _ => (12, false)
        };
    }

    /// <summary><c>textDocument/documentSymbol</c>: this file's declarations as an outline (flat
    /// DocumentSymbol list — routines, types, top-level variables), each ranged at its name.</summary>
    private static void HandleDocumentSymbol(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl) ||
            !Docs.TryGetValue(key: uriEl.GetString() ?? "", value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: new List<object?>());
            return;
        }

        // Walk ONLY the top-level declarations (not AllNodes, which would descend into routine bodies and
        // list every local `var` in the outline). Type members (fields, cases) are nested as children.
        var syms = new List<object?>();
        foreach (ISyntaxTreeNode node in doc.Program.Declarations)
        {
            Dictionary<string, object?>? sym = MakeDocSymbol(node: node);
            if (sym == null)
            {
                continue;
            }

            var children = new List<object?>();
            foreach (object? member in MembersOf(node: node))
            {
                if (member is ISyntaxTreeNode mn && MakeDocSymbol(node: mn) is { } childSym)
                {
                    children.Add(item: childSym);
                }
            }

            if (children.Count > 0)
            {
                sym[key: "children"] = children;
            }

            syms.Add(item: sym);
        }

        WriteResult(stdout: stdout, id: id, result: syms);
    }

    /// <summary>A DocumentSymbol for a declaration node, or null if it is not a named declaration.</summary>
    private static Dictionary<string, object?>? MakeDocSymbol(ISyntaxTreeNode node)
    {
        string tn = node.GetType()
                        .Name;
        if (!tn.EndsWith(value: SuffixDeclaration, comparisonType: StringComparison.Ordinal) ||
            node is not SyntaxTreeNode sn || GetNameProp(node: node) is not { Length: > 0 } name)
        {
            return null;
        }

        (int kind, _) = SymbolKindOf(declTypeName: tn);
        int l = Math.Max(val1: 0, val2: sn.Location.Line - 1);
        int c = Math.Max(val1: 0, val2: sn.Location.Column - 1);
        var range = new Dictionary<string, object?>
        {
            [key: PropStart] =
                new Dictionary<string, object?>
                {
                    [key: "line"] = l, [key: PropCharacter] = c
                },
            [key: "end"] = new Dictionary<string, object?>
            {
                [key: "line"] = l, [key: PropCharacter] = c + name.Length
            }
        };
        return new Dictionary<string, object?>
        {
            [key: "name"] = name,
            [key: "kind"] = kind,
            [key: PropRange] = range,
            [key: "selectionRange"] = range
        };
    }

    /// <summary>The member declarations of a type node (its <c>Members</c> / <c>Cases</c> list), or empty.</summary>
    private static IEnumerable<object?> MembersOf(ISyntaxTreeNode node)
    {
        foreach (string prop in new[]
                 {
                     "Members",
                     "Cases"
                 })
        {
            object? value = node.GetType()
                                .GetProperty(name: prop,
                                     bindingAttr: System.Reflection.BindingFlags.Public |
                                                  System.Reflection.BindingFlags.Instance)
                               ?.GetValue(obj: node);
            if (value is System.Collections.IEnumerable seq and not string)
            {
                foreach (object? item in seq)
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary><c>workspace/symbol</c>: registry routines + open-file declarations across all open
    /// documents whose name contains the query (case-insensitive), as located SymbolInformation.</summary>
    private static void HandleWorkspaceSymbol(Stream stdout, JsonElement id, JsonElement root)
    {
        string query = root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) &&
                       p.TryGetProperty(propertyName: "query", value: out JsonElement q)
            ? q.GetString() ?? ""
            : "";

        var syms = new List<object?>();
        var seen = new HashSet<string>();

        foreach (DocState doc in Docs.Values)
        {
            foreach (RoutineInfo r in doc.Registry
                                         .GetAllRoutines()
                                         .Where(predicate: r => !r.Name.StartsWith(value: '$')))
            {
                TryAddWorkspaceSymbol(syms: syms,
                    seen: seen,
                    query: query,
                    name: r.Name,
                    kind: r.OwnerType != null
                        ? 6
                        : 12,
                    loc: r.Location);
            }

            foreach (ISyntaxTreeNode node in AllNodes(program: doc.Program))
            {
                string tn = node.GetType()
                                .Name;
                if (tn.EndsWith(value: SuffixDeclaration,
                        comparisonType: StringComparison.Ordinal) && node is SyntaxTreeNode sn &&
                    GetNameProp(node: node) is { } n)
                {
                    (int kind, _) = SymbolKindOf(declTypeName: tn);
                    TryAddWorkspaceSymbol(syms: syms,
                        seen: seen,
                        query: query,
                        name: n,
                        kind: kind,
                        loc: sn.Location);
                }
            }
        }

        WriteResult(stdout: stdout, id: id, result: syms);
    }

    /// <summary>Adds a workspace symbol to the accumulator if its name matches the query, it hasn't been
    /// seen before, and the cap of 300 symbols hasn't been reached.</summary>
    private static void TryAddWorkspaceSymbol(List<object?> syms, HashSet<string> seen,
        string query, string name, int kind,
        SourceLocation? loc)
    {
        if (loc == null || name.Length == 0 || query.Length > 0 &&
            !name.Contains(value: query, comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string key = $"{name}@{loc.FileName}:{loc.Line}";
        if (!seen.Add(item: key) || syms.Count >= 300)
        {
            return;
        }

        syms.Add(item: new Dictionary<string, object?>
        {
            [key: "name"] = name,
            [key: "kind"] = kind,
            [key: "location"] = LocationToLsp(loc: loc)
        });
    }

    /// <summary><c>textDocument/inlayHint</c>: for a <c>var x = expr</c> with no written type, an inferred
    /// <c>: Type</c> hint after the name (from the initializer's resolved type).</summary>
    private static void HandleInlayHint(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl) ||
            !Docs.TryGetValue(key: uriEl.GetString() ?? "", value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: new List<object?>());
            return;
        }

        var hints = new List<object?>();

        void AddHint(int line1, int col1, string labelText,
            int kind, bool padLeft)
        {
            hints.Add(item: new Dictionary<string, object?>
            {
                [key: "position"] = new Dictionary<string, object?>
                {
                    [key: "line"] = line1 - 1, [key: PropCharacter] = col1 - 1
                },
                [key: PropLabel] = Surface(text: labelText),
                [key: "kind"] = kind,
                [key: "paddingLeft"] = padLeft
            });
        }

        foreach (ISyntaxTreeNode node in AllNodes(program: doc.Program))
        {
            switch (node)
            {
                // `var x = expr` with no written type → an inferred `: Type` hint after the name. Skip a
                // failed inference (`<error>`, e.g. reading a dead value) — a bogus hint is worse than none.
                case VariableDeclaration { Type: null, Initializer: { ResolvedType: { } vt } } vd
                    when !vt.Name.StartsWith(value: '<'):
                {
                    Token? nameTok = doc.Tokens.FirstOrDefault(predicate: t =>
                        t.Type == TokenType.Identifier && t.Text == vd.Name &&
                        t.Line == vd.Location.Line);
                    if (nameTok != null)
                    {
                        AddHint(line1: nameTok.Line,
                            col1: nameTok.Column + nameTok.Text.Length,
                            labelText: $": {TypeText(type: vt)}",
                            kind: 1,
                            padLeft: false);
                    }

                    break;
                }

                // `steal x` → a "moved" marker after the stolen variable, so the ownership hand-off is
                // visible at the point the source binding dies.
                case StealExpression { Operand: IdentifierExpression sid }:
                    AddHint(line1: sid.Location.Line,
                        col1: sid.Location.Column + sid.Name.Length,
                        labelText: " ⟶ moved",
                        kind: 2,
                        padLeft: true);
                    break;
            }
        }

        WriteResult(stdout: stdout, id: id, result: hints);
    }

    /// <summary>
    /// <c>textDocument/codeAction</c>: quick-fixes for the diagnostics in range. Currently one — an
    /// unused Bool-returning call (RF-W007) gets a "Prepend discard" edit. The structure takes more.
    /// </summary>
    private static void HandleCodeAction(Stream stdout, JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl) ||
            !Docs.TryGetValue(key: uriEl.GetString() ?? "", value: out DocState? doc))
        {
            WriteResult(stdout: stdout, id: id, result: new List<object?>());
            return;
        }

        string uri = uriEl.GetString() ?? "";
        var actions = new List<object?>();

        if (p.TryGetProperty(propertyName: "context", value: out JsonElement ctx) &&
            ctx.TryGetProperty(propertyName: "diagnostics", value: out JsonElement diags) &&
            diags.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement diag in diags.EnumerateArray())
            {
                TryAddDiscardCodeAction(actions: actions,
                    doc: doc,
                    uri: uri,
                    diag: diag);
            }
        }

        WriteResult(stdout: stdout, id: id, result: actions);
    }

    /// <summary>If <paramref name="diag"/> is an RF-W007 (unused Bool-returning call) diagnostic and the
    /// first token on its line can be found, adds a "Prepend 'discard'" quick-fix code action.</summary>
    private static void TryAddDiscardCodeAction(List<object?> actions, DocState doc, string uri,
        JsonElement diag)
    {
        string code;
        if (!diag.TryGetProperty(propertyName: "code", value: out JsonElement cd))
        {
            code = "";
        }
        else if (cd.ValueKind == JsonValueKind.String)
        {
            code = cd.GetString() ?? "";
        }
        else
        {
            code = cd.ToString();
        }

        if (!code.Contains(value: "W007") ||
            !diag.TryGetProperty(propertyName: PropRange, value: out JsonElement dr) ||
            !dr.TryGetProperty(propertyName: PropStart, value: out JsonElement ds) ||
            !ds.TryGetProperty(propertyName: "line", value: out JsonElement dl))
        {
            return;
        }

        int line1 = dl.GetInt32() + 1;
        Token? first = doc.Tokens
                          .Where(predicate: t => t.Line == line1 && t.Text.Length > 0 &&
                                                 t.Type != TokenType.Newline)
                          .OrderBy(keySelector: t => t.Column)
                          .FirstOrDefault();
        if (first == null)
        {
            return;
        }

        var pos = new Dictionary<string, object?>
        {
            [key: "line"] = line1 - 1, [key: PropCharacter] = first.Column - 1
        };
        actions.Add(item: new Dictionary<string, object?>
        {
            [key: "title"] = "Prepend 'discard'",
            [key: "kind"] = "quickfix",
            [key: "diagnostics"] = new List<object?> { JsonElementToObject(el: diag) },
            [key: "edit"] = new Dictionary<string, object?>
            {
                [key: "changes"] = new Dictionary<string, object?>
                {
                    [key: uri] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            [key: PropRange] = new Dictionary<string, object?>
                            {
                                [key: PropStart] = pos, [key: "end"] = pos
                            },
                            [key: "newText"] = "discard "
                        }
                    }
                }
            }
        });
    }

    /// <summary>Shallow-copies a diagnostic JsonElement back into a serializable dictionary (so a code
    /// action can echo the diagnostic it fixes).</summary>
    private static Dictionary<string, object?> JsonElementToObject(JsonElement el)
    {
        var d = new Dictionary<string, object?>();
        if (el.TryGetProperty(propertyName: PropRange, value: out JsonElement r) &&
            r.TryGetProperty(propertyName: PropStart, value: out JsonElement s) &&
            r.TryGetProperty(propertyName: "end", value: out JsonElement e))
        {
            d[key: PropRange] = new Dictionary<string, object?>
            {
                [key: PropStart] = new Dictionary<string, object?>
                {
                    [key: "line"] = s.GetProperty(propertyName: "line")
                                     .GetInt32(),
                    [key: PropCharacter] = s.GetProperty(propertyName: PropCharacter)
                                            .GetInt32()
                },
                [key: "end"] = new Dictionary<string, object?>
                {
                    [key: "line"] = e.GetProperty(propertyName: "line")
                                     .GetInt32(),
                    [key: PropCharacter] = e.GetProperty(propertyName: PropCharacter)
                                            .GetInt32()
                }
            };
        }

        if (el.TryGetProperty(propertyName: "message", value: out JsonElement m))
        {
            d[key: "message"] = m.GetString();
        }

        if (el.TryGetProperty(propertyName: "severity", value: out JsonElement sev) &&
            sev.ValueKind == JsonValueKind.Number)
        {
            d[key: "severity"] = sev.GetInt32();
        }

        return d;
    }

    private static void AddItem(List<Dictionary<string, object?>> items, HashSet<string> seen,
        string label, int kind, string? detail = null,
        string? documentation = null)
    {
        if (label.Length == 0 || !seen.Add(item: label))
        {
            return;
        }

        var item =
            new Dictionary<string, object?> { [key: PropLabel] = label, [key: "kind"] = kind };
        if (detail != null)
        {
            item[key: "detail"] = Surface(text: detail);
        }

        if (!string.IsNullOrWhiteSpace(value: documentation))
        {
            item[key: PropDocumentation] = new Dictionary<string, object?>
            {
                [key: "kind"] = PropMarkdown, [key: PropValue] = RenderDoc(doc: documentation)
            };
        }

        items.Add(item: item);
    }

    /// <summary>A doc-comment parsed into its summary prose and reStructuredText-style field lists.</summary>
    private sealed record DocInfo(
        string Summary,
        List<(string Name, string Desc)> Params,
        List<(string Name, string Desc)> TypeParams,
        string? Returns,
        string? Throws,
        string? Absent,
        List<string> Notes,
        List<string> Sees);

    /// <summary>
    /// Parses a stored <c>###</c> doc-comment into a summary plus the field lists the docs use:
    /// <c>:param name:</c>, <c>:typeparam Name:</c>, <c>:returns:</c>, <c>:throws:</c>, <c>:absent:</c>,
    /// <c>:note:</c>, <c>:see:</c>. Lines before the first field are the summary; a line that does not
    /// open a new <c>:field:</c> continues the previous field's (or the summary's) text.
    /// </summary>
    private static DocInfo ParseDoc(string doc)
    {
        var summary = new List<string>();
        var pars = new List<(string, string)>();
        var typePars = new List<(string, string)>();
        string? returns = null, throws = null, absent = null;
        var notes = new List<string>();
        var sees = new List<string>();

        // Where the last field's continuation text goes; null = still in the summary.
        Action<string>? append = null;

        foreach (string raw in doc.Replace(oldValue: "\r", newValue: "")
                                  .Split(separator: '\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(value: ':') &&
                line.IndexOf(value: ':', startIndex: 1) is var sc and > 0)
            {
                string spec = line[1..sc]
                   .Trim();
                string desc = line[(sc + 1)..]
                   .Trim();
                string[] parts = spec.Split(separator: ' ',
                    count: 2,
                    options: StringSplitOptions.RemoveEmptyEntries);
                string kind = parts[0]
                   .ToLowerInvariant();
                string? name = parts.Length > 1
                    ? parts[1]
                    : null;

                switch (kind)
                {
                    case "param" when name != null:
                        pars.Add(item: (name, desc));
                        int pi = pars.Count - 1;
                        append = s => pars[index: pi] = (pars[index: pi].Item1,
                            $"{pars[index: pi].Item2} {s}".Trim());
                        break;
                    case "typeparam" when name != null:
                        typePars.Add(item: (name, desc));
                        int ti = typePars.Count - 1;
                        append = s => typePars[index: ti] = (typePars[index: ti].Item1,
                            $"{typePars[index: ti].Item2} {s}".Trim());
                        break;
                    case "returns":
                        returns = desc;
                        append = s => returns = $"{returns} {s}".Trim();
                        break;
                    case "throws":
                        throws = desc;
                        append = s => throws = $"{throws} {s}".Trim();
                        break;
                    case "absent":
                        absent = desc;
                        append = s => absent = $"{absent} {s}".Trim();
                        break;
                    case "note":
                        notes.Add(item: desc);
                        int ni = notes.Count - 1;
                        append = s => notes[index: ni] = $"{notes[index: ni]} {s}".Trim();
                        break;
                    case "see":
                        sees.Add(item: desc);
                        int si = sees.Count - 1;
                        append = s => sees[index: si] = $"{sees[index: si]} {s}".Trim();
                        break;
                    default:
                        // Unknown `:field:` — keep it verbatim in the summary so nothing is lost.
                        summary.Add(item: line);
                        append = null;
                        break;
                }
            }
            else if (append != null && line.Length > 0)
            {
                append(obj: line);
            }
            else
            {
                summary.Add(item: line);
            }
        }

        return new DocInfo(Summary: string.Join(separator: "\n", values: summary)
                                          .Trim(),
            Params: pars,
            TypeParams: typePars,
            Returns: returns,
            Throws: throws,
            Absent: absent,
            Notes: notes,
            Sees: sees);
    }

    /// <summary>Renders a parsed doc-comment as hover/completion markdown: the summary prose, then a
    /// bulleted parameters/type-parameters block and labelled Returns/Throws/Absent/Note/See lines.</summary>
    private static string RenderDoc(string doc)
    {
        DocInfo d = ParseDoc(doc: doc);
        var sb = new StringBuilder();
        if (d.Summary.Length > 0)
        {
            sb.Append(value: d.Summary);
        }

        void Section(string title, IEnumerable<(string Name, string Desc)> entries)
        {
            var list = entries.ToList();
            if (list.Count == 0)
            {
                return;
            }

            if (sb.Length > 0)
            {
                sb.Append(value: "\n\n");
            }

            sb.Append(value: $"**{title}**");
            foreach ((string name, string desc) in list)
            {
                sb.Append(value: desc.Length > 0
                    ? $"\n- `{name}` — {desc}"
                    : $"\n- `{name}`");
            }
        }

        void Line(string label, string? text)
        {
            if (string.IsNullOrWhiteSpace(value: text))
            {
                return;
            }

            sb.Append(value: sb.Length > 0
                ? "\n\n"
                : "");
            sb.Append(value: $"**{label}** — {text}");
        }

        Section(title: "Type parameters", entries: d.TypeParams);
        Section(title: "Parameters", entries: d.Params);
        Line(label: "Returns", text: d.Returns);
        Line(label: "Throws", text: d.Throws);
        Line(label: "Absent", text: d.Absent);
        foreach (string note in d.Notes)
        {
            Line(label: "Note", text: note);
        }

        foreach (string see in d.Sees)
        {
            Line(label: "See", text: see);
        }

        return sb.ToString();
    }

    /// <summary>A routine's signature for the completion detail: <c>(a: T, b: U) -> R</c> (with `!` if failable).</summary>
    private static string RoutineDetail(RoutineInfo r)
    {
        string ps = string.Join(separator: ", ",
            values: r.Parameters.Select(selector: p => $"{p.Name}: {TypeText(type: p.Type)}"));
        string ret = r.ReturnType != null
            ? $" -> {TypeText(type: r.ReturnType)}"
            : "";
        string bang = r.IsFailable
            ? "!"
            : "";
        return $"{bang}({ps}){ret}";
    }

    /// <summary>
    /// If the cursor is in a <c>receiver.</c> member position (right after the dot, or partway through a
    /// member name), the receiver identifier token — else null. This is a pure token check, so it holds
    /// even when the line is mid-edit (<c>p.</c> with nothing after the dot yet), which is exactly when
    /// completion fires. Callers use "in member context" to suppress the global fallback after a dot.
    /// </summary>
    private static Token? MemberReceiverToken(DocState doc, int line0, int char0)
    {
        int line1 = line0 + 1;
        int col1 = char0 + 1;
        var before = doc.Tokens
                        .Where(predicate: t => t.Line == line1 && t.Column < col1)
                        .OrderBy(keySelector: t => t.Column)
                        .ToList();
        if (before.Count == 0)
        {
            return null;
        }

        // `receiver.`  (cursor right after the dot) or `receiver.parti` (typing a member name).
        Token? receiver = null;
        if (before[^1].Text == "." && before.Count >= 2)
        {
            receiver = before[^2];
        }
        else if (before.Count >= 3 && before[^2].Text == ".")
        {
            receiver = before[^3];
        }

        // A plain identifier receiver, or the `me` keyword (`me.` completes the enclosing type's members).
        return receiver is { Type: TokenType.Identifier } or { Type: TokenType.Me }
            ? receiver
            : null;
    }

    /// <summary>
    /// If the cursor is right after a <c>Realm::</c> qualifier (<c>C::</c>, <c>LLVM::</c>, …) — after the
    /// <c>::</c> or partway through the qualified name — the realm tag text; else null. Used to scope
    /// completion to that realm's routines instead of the global dump.
    /// </summary>
    private static string? RealmQualifierBefore(DocState doc, int line0, int char0)
    {
        int line1 = line0 + 1;
        int col1 = char0 + 1;
        var before = doc.Tokens
                        .Where(predicate: t => t.Line == line1 && t.Column < col1)
                        .OrderBy(keySelector: t => t.Column)
                        .ToList();

        // `Realm::` (cursor after the ::) or `Realm::par` (typing the qualified name).
        if (before.Count >= 2 && before[^1].Type == TokenType.DoubleColon &&
            before[^2].Type == TokenType.Identifier)
        {
            return before[^2].Text;
        }

        if (before.Count >= 3 && before[^2].Type == TokenType.DoubleColon &&
            before[^3].Type == TokenType.Identifier)
        {
            return before[^3].Text;
        }

        return null;
    }

    /// <summary>
    /// The resolved type of a receiver identifier token, for member completion. Tries, in order: the
    /// typed <see cref="IdentifierExpression"/> at that exact position; then — since a half-typed
    /// <c>p.</c> may leave that node untyped — any same-named identifier's stamped variable binding or
    /// resolved type elsewhere in the file. Returns null only if the name has no known type at all.
    /// </summary>
    private static TypeSymbol? ReceiverType(DocState doc, Token receiver)
    {
        // `me.` → the enclosing type. Resolve it from the nearest routine DECLARED above the cursor in
        // THIS file that has an owner — robust even on a half-typed `me.` line where the `me` node itself
        // may not have been analyzed. (`me`'s type is never a local binding, so the identifier searches
        // below would miss it.)
        if (receiver.Text == "me")
        {
            string? file = doc.Tokens.FirstOrDefault()
                             ?.FileName;
            RoutineInfo? enclosing = doc.Registry
                                        .GetAllRoutines()
                                        .Where(predicate: r =>
                                             r.OwnerType != null && r.Location is { } l &&
                                             l.FileName == file && l.Line <= receiver.Line)
                                        .OrderByDescending(keySelector: r => r.Location!.Line)
                                        .FirstOrDefault();
            if (enclosing?.OwnerType is { } owner)
            {
                return owner;
            }
        }

        var idents = AllNodes(program: doc.Program)
                    .OfType<IdentifierExpression>()
                    .ToList();

        // 1. The receiver identifier at exactly this position (NOT the enclosing MemberExpression, which
        //    shares the column but carries the MEMBER's type).
        TypeSymbol? exactType = idents.FirstOrDefault(predicate: e =>
                                         e.ResolvedType != null && e.Name == receiver.Text &&
                                         e.Location.Line == receiver.Line &&
                                         e.Location.Column == receiver.Column)
                                   ?.ResolvedType;
        if (exactType != null)
        {
            return exactType;
        }

        // 2. Fallback for a mid-edit line: the same name's binding (or any typed use) elsewhere.
        TypeSymbol? bindingType = idents
                               .FirstOrDefault(predicate: e =>
                                    e.Name == receiver.Text && e.ResolvedVariable != null)
                              ?.ResolvedVariable?.Type;
        if (bindingType != null)
        {
            return bindingType;
        }

        return idents
              .FirstOrDefault(predicate: e => e.Name == receiver.Text && e.ResolvedType != null)
             ?.ResolvedType;
    }

    /// <summary>
    /// Whether a resolved member routine actually applies to this receiver. A method declared on a
    /// SPECIALIZED receiver — e.g. <c>routine List[Agent[V]].gather()</c> — registers under the generic
    /// <c>List</c> owner but its <see cref="RoutineInfo.MeType"/> pins the element to a concrete type. It
    /// must NOT be offered for an unrelated instantiation like <c>List[FaceDraw]</c>. A method whose
    /// receiver pattern is the bare generic (element is a generic parameter) applies to any instantiation.
    /// </summary>
    private static bool ReceiverAcceptsMethod(RoutineInfo mr, TypeSymbol receiverType)
    {
        if (mr.MeType is not { TypeArguments: { Count: > 0 } meArgs } ||
            receiverType.TypeArguments is not { Count: > 0 } recvArgs ||
            recvArgs.Count != meArgs.Count)
        {
            return true; // no comparable specialization — don't over-filter
        }

        for (int i = 0; i < meArgs.Count; i++)
        {
            // A generic-parameter slot in the receiver pattern (e.g. the `T` of `List[T]`) matches
            // anything. A CONCRETE pattern element (e.g. `Agent[V]`) requires the receiver's element to
            // be the same base type.
            if (meArgs[index: i] is not GenericParameterTypeSymbol &&
                meArgs[index: i].BareName != recvArgs[index: i].BareName)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<(string Name, string Type, SourceLocation? Location)> MemberVariableSignatures(
        TypeSymbol type, bool includeSecret)
    {
        IEnumerable<MemberVariableInfo> members = type switch
        {
            EntityTypeSymbol en => en.MemberVariables,
            RecordTypeSymbol re => re.MemberVariables,
            _ => Enumerable.Empty<MemberVariableInfo>()
        };

        // `secret` fields are file-private (e.g. List's internal data/count/capacity buffer) — never offer
        // them to an outside `x.` completion. `posted` (open read / secret write) stays visible.
        return members
              .Where(predicate: v => includeSecret || v.Visibility != VisibilityModifier.Secret)
              .Select(selector: v => (v.Name, TypeText(type: v.Type), v.Location));
    }

    /// <summary>An LSP Location for a 1-based (line, column) span of <paramref name="length"/> chars.</summary>
    private static Dictionary<string, object?> RangeLsp(string uri, int line1, int col1,
        int length)
    {
        int l = Math.Max(val1: 0, val2: line1 - 1);
        int c = Math.Max(val1: 0, val2: col1 - 1);
        return new Dictionary<string, object?>
        {
            [key: "uri"] = uri,
            [key: PropRange] = new Dictionary<string, object?>
            {
                [key: PropStart] =
                    new Dictionary<string, object?>
                    {
                        [key: "line"] = l, [key: PropCharacter] = c
                    },
                [key: "end"] = new Dictionary<string, object?>
                {
                    [key: "line"] = l, [key: PropCharacter] = c + length
                }
            }
        };
    }

    private static (string? Name, SourceLocation? Location) CalleeName(Expression callee)
    {
        return callee switch
        {
            IdentifierExpression id => (id.Name, id.Location),
            MemberExpression m => (m.MemberName, m.Location),
            _ => (null, null)
        };
    }

    private static string? GetNameProp(ISyntaxTreeNode node)
    {
        System.Reflection.PropertyInfo? p = node.GetType()
                                                .GetProperty(name: "Name",
                                                     bindingAttr: System.Reflection.BindingFlags
                                                        .Public | System.Reflection.BindingFlags
                                                        .Instance);
        return p != null && p.PropertyType == typeof(string)
            ? p.GetValue(obj: node) as string
            : null;
    }

    private static Dictionary<string, object?> LocationToLsp(SourceLocation loc)
    {
        int l = Math.Max(val1: 0, val2: loc.Line - 1);
        int c = Math.Max(val1: 0, val2: loc.Column - 1);
        return new Dictionary<string, object?>
        {
            [key: "uri"] = FileNameToUri(fileName: loc.FileName),
            [key: PropRange] = new Dictionary<string, object?>
            {
                [key: PropStart] =
                    new Dictionary<string, object?>
                    {
                        [key: "line"] = l, [key: PropCharacter] = c
                    },
                [key: "end"] = new Dictionary<string, object?>
                {
                    [key: "line"] = l, [key: PropCharacter] = c
                }
            }
        };
    }

    /// <summary>
    /// The source a standard library file was copied from. The build lays the stdlib out next to the executable
    /// (<c>bin/.../Standard/RazorForge/Core/X.rf</c>), and the editor runs the server from a copy of that folder
    /// (whose build folder it names in <c>ANVILA_BUILD_DIR</c>). A location in either copy is shown in the project's
    /// <c>Standard/Core/X.rf</c>, so go-to-definition opens the file that is edited, not one a build overwrites. Any
    /// other file, or one whose source isn't there, is itself.
    /// </summary>
    private static string SourceFileOf(string fileName)
    {
        string here = Path.GetFullPath(path: AppContext.BaseDirectory);
        string copied = Path.Combine(path1: here, path2: "Standard") + Path.DirectorySeparatorChar;
        if (!fileName.StartsWith(value: copied, comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        // Past `Standard/<Language>/`: the file's path inside the language's own Standard folder.
        string inStandard = fileName[copied.Length..];
        int languageEnd = inStandard.IndexOf(value: Path.DirectorySeparatorChar);
        if (languageEnd < 0)
        {
            return fileName;
        }

        string build = Environment.GetEnvironmentVariable(variable: "ANVILA_BUILD_DIR") is { Length: > 0 } named
            ? Path.GetFullPath(path: named)
            : here;
        for (DirectoryInfo? dir = new(path: build); dir != null; dir = dir.Parent)
        {
            if (dir.Name.Equals(value: "bin", comparisonType: StringComparison.OrdinalIgnoreCase) && dir.Parent != null)
            {
                string source = Path.Combine(path1: dir.Parent.FullName, path2: "Standard",
                    path3: inStandard[(languageEnd + 1)..]);
                return File.Exists(path: source)
                    ? source
                    : fileName;
            }
        }

        return fileName;
    }

    private static string FileNameToUri(string fileName)
    {
        try
        {
            if (fileName.StartsWith(value: "file:",
                    comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }

            return new Uri(uriString: SourceFileOf(fileName: Path.GetFullPath(path: fileName))).AbsoluteUri;
        }
        catch
        {
            return fileName;
        }
    }

    /// <summary>Reflectively gathers every syntax-tree node reachable from <paramref name="node"/>.</summary>
    private static void CollectAllNodes(object? node, List<ISyntaxTreeNode> acc,
        HashSet<object> seen)
    {
        if (node == null || !seen.Add(item: node))
        {
            return;
        }

        if (node is ISyntaxTreeNode n)
        {
            acc.Add(item: n);
        }

        foreach (System.Reflection.PropertyInfo prop in node.GetType()
                                                            .GetProperties(
                                                                 bindingAttr: System.Reflection
                                                                    .BindingFlags.Public |
                                                                 System.Reflection.BindingFlags
                                                                    .Instance))
        {
            if (prop.GetIndexParameters()
                    .Length > 0)
            {
                continue; // skip indexers
            }

            object? value;
            try
            {
                value = prop.GetValue(obj: node);
            }
            catch
            {
                continue;
            }

            TraversePropertyValue(value: value, acc: acc, seen: seen);
        }
    }

    /// <summary>Descends into a single property value: recursing if it is a node, or iterating and
    /// recursing into each element if it is a non-string enumerable.</summary>
    private static void TraversePropertyValue(object? value, List<ISyntaxTreeNode> acc,
        HashSet<object> seen)
    {
        switch (value)
        {
            case ISyntaxTreeNode child:
                CollectAllNodes(node: child, acc: acc, seen: seen);
                break;
            case System.Collections.IEnumerable seq and not string:
                foreach (object? item in seq)
                {
                    if (item is ISyntaxTreeNode or { } and not string && IsSyntaxRecord(value: item))
                    {
                        CollectAllNodes(node: item, acc: acc, seen: seen);
                    }
                }

                break;
            case { } record when IsSyntaxRecord(value: record):
                CollectAllNodes(node: record, acc: acc, seen: seen);
                break;
        }
    }

    /// <summary>A part of the syntax tree that is not a node itself but holds nodes: a parameter, a variant
    /// member, a generic constraint, an associated-type clause.</summary>
    private static bool IsSyntaxRecord(object value)
    {
        Type type = value.GetType();
        return type is { IsEnum: false, IsPrimitive: false } && type.Namespace == nameof(SyntaxTree);
    }

    /// <summary>All syntax-tree nodes of a document, computed once per hover/definition request.</summary>
    private static List<ISyntaxTreeNode> AllNodes(SyntaxTree.Program program)
    {
        var acc = new List<ISyntaxTreeNode>();
        CollectAllNodes(node: program,
            acc: acc,
            seen: new HashSet<object>(comparer: ReferenceEqualityComparer.Instance));
        return acc;
    }

    /// <summary>The token whose 1-based span contains the cursor (0-based LSP line/character), or null.</summary>
    private static Token? TokenAt(DocState doc, int line0, int char0)
    {
        int line1 = line0 + 1;
        int col1 = char0 + 1;
        foreach (Token tok in doc.Tokens)
        {
            if (tok.Line == line1 && tok.Column <= col1 &&
                col1 < tok.Column + Math.Max(val1: 1, val2: tok.Text.Length))
            {
                return tok;
            }
        }

        return null;
    }

    private static bool IsIdentifierText(string text)
    {
        if (text.Length == 0 || !(char.IsLetter(c: text[index: 0]) || text[index: 0] == '_'))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!(char.IsLetterOrDigit(c: c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadPosition(JsonElement root, out string uri, out int line0,
        out int char0)
    {
        uri = "";
        line0 = 0;
        char0 = 0;
        if (!root.TryGetProperty(propertyName: PropParams, value: out JsonElement p) ||
            !p.TryGetProperty(propertyName: PropTextDocument, value: out JsonElement td) ||
            !td.TryGetProperty(propertyName: "uri", value: out JsonElement uriEl) ||
            !p.TryGetProperty(propertyName: "position", value: out JsonElement pos))
        {
            return false;
        }

        uri = uriEl.GetString() ?? "";
        line0 = pos.TryGetProperty(propertyName: "line", value: out JsonElement l)
            ? l.GetInt32()
            : 0;
        char0 = pos.TryGetProperty(propertyName: PropCharacter, value: out JsonElement c)
            ? c.GetInt32()
            : 0;
        return true;
    }

    /// <summary>Full-sync didChange: the last content change holds the entire document text.</summary>
    private static string? ExtractFullChangeText(JsonElement paramsEl)
    {
        if (!paramsEl.TryGetProperty(propertyName: "contentChanges",
                value: out JsonElement changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? last = null;
        foreach (JsonElement change in changes.EnumerateArray())
        {
            if (change.TryGetProperty(propertyName: "text", value: out JsonElement ct))
            {
                last = ct.GetString();
            }
        }

        return last;
    }

    /// <summary>
    /// Analyzes a document the way <c>check</c> builds it, so the editor reports exactly what the builder would:
    /// the build driver reads the file and everything it imports (the language's prelude, sibling files of its
    /// module, the project's other modules, with every open document read as it stands in the editor), then the
    /// project's files are analyzed together against the cached stdlib snapshot. Only this document's own
    /// diagnostics are reported. A standard library file is already part of that snapshot, so analyzing it again
    /// as user code would only report its own declarations as duplicates: it gets grammar errors alone.
    /// Any unexpected exception in the pipeline becomes a single diagnostic rather than crashing the server.
    /// </summary>
    private static List<Dictionary<string, object?>> Analyze(string uri, string text)
    {
        var diagnostics = new List<Dictionary<string, object?>>();
        Language lang = Builder.Frontends.Languages.OfFile(fileName: uri);
        string fileName = Path.GetFullPath(path: UriToFileName(uri: uri));

        try
        {
            List<Token> tokens = Builder.Tokenizer.Lexers.Tokenize(source: text, fileName: fileName, language: lang);

            var parser = new Builder.Parser.Parser(tokens: tokens, language: lang, fileName: fileName);
            SyntaxTree.Program program = parser.Parse();

            // Underline the whole token at the reported position, not a single caret column.
            int SpanLen(int line, int col)
            {
                return tokens.FirstOrDefault(predicate: t => t.Line == line && t.Column == col)
                            ?.Text.Length ?? 1;
            }

            IReadOnlyList<GrammarException> grammarErrors = parser.GetStructuredErrors();
            foreach (GrammarException pe in grammarErrors)
            {
                diagnostics.Add(item: MakeDiagnostic(line: pe.Line,
                    column: pe.Column,
                    severity: 1,
                    code: pe.Code.ToCodeString(language: lang),
                    message: pe.RawMessage,
                    length: SpanLen(line: pe.Line, col: pe.Column)));
            }

            TypeRegistry.StdlibSnapshot snapshot = SnapshotFor(language: lang);
            var verifier = new SemanticVerifier(language: lang, snapshot: snapshot) { SaOnly = true };
            string stdlibRoot = Path.GetFullPath(path: StdlibLoader.GetDefaultStdlibPath());
            if (StdlibCopyOf(fileName: fileName, stdlibRoot: stdlibRoot) is { } copy)
            {
                // Shown through its analyzed tree while the text is the one the builder reads. Mid-edit, the
                // positions no longer match it, so the file shows as parsed.
                string? module = program.Declarations.OfType<ModuleDeclaration>()
                                        .FirstOrDefault()
                                       ?.Path;
                SyntaxTree.Program shown = SameText(a: File.ReadAllText(path: copy), b: text) &&
                                           AnalyzedStdlibProgram(language: lang, copyPath: copy, module: module) is
                                               { } analyzed
                    ? analyzed
                    : program;
                Docs[key: uri] = new DocState(Program: shown, Tokens: tokens, Lang: lang, Registry: verifier.Registry);
                return diagnostics;
            }

            (string projectRoot, IReadOnlyList<string> libraryRoots) = ProjectOf(fileName: fileName);
            var driver = new BuildDriver(projectRoot: projectRoot,
                stdlibRoot: stdlibRoot,
                language: lang,
                libraryRoots: libraryRoots,
                cachedStdlibIndex: StdlibIndexFor(language: lang, stdlibRoot: stdlibRoot, libraryRoots: libraryRoots))
            {
                SourceOverrides = OpenSources()
            };
            BuildResult build = driver.CompileFile(entryFile: fileName);

            // Grammar errors in this file were reported from its own parse above.
            foreach (SemanticError e in build.Errors.Where(predicate: e =>
                         IsThisFile(location: e.Location, fileName: fileName) &&
                         !(grammarErrors.Count > 0 && e.Code == SemanticDiagnosticCode.ParseError)))
            {
                diagnostics.Add(item: MakeDiagnostic(line: e.Location.Line,
                    column: e.Location.Column,
                    severity: 1,
                    code: e.CodeString,
                    message: e.SurfaceMessage,
                    length: SpanLen(line: e.Location.Line, col: e.Location.Column)));
            }

            FileBuildUnit? unit = build.Units.FirstOrDefault(predicate: u =>
                string.Equals(a: Path.GetFullPath(path: u.FilePath), b: fileName,
                    comparisonType: StringComparison.OrdinalIgnoreCase));
            if (build.Errors.Count > 0 || unit == null)
            {
                // Like check, a project that doesn't build is not analyzed: its build errors are the ones to fix.
                Docs[key: uri] = new DocState(Program: program, Tokens: tokens, Lang: lang, Registry: verifier.Registry);
                return diagnostics;
            }

            List<(SyntaxTree.Program Program, string FilePath)> files = Program.OrderUserFiles(
                userUnits: Program.FilterUserUnits(buildResult: build, stdlibRoot: stdlibRoot),
                initializationOrder: build.InitializationOrder);
            verifier.Registry.UseModuleResolver(resolver: driver.Resolver);
            AnalysisResult result = verifier.AnalyzeMultiple(files: files);

            foreach (SemanticError e in result.Errors.Where(predicate: e =>
                         IsThisFile(location: e.Location, fileName: fileName)))
            {
                diagnostics.Add(item: MakeDiagnostic(line: e.Location.Line,
                    column: e.Location.Column,
                    severity: 1,
                    code: e.CodeString,
                    message: e.SurfaceMessage,
                    length: SpanLen(line: e.Location.Line, col: e.Location.Column)));
            }

            foreach (SemanticWarning w in result.Warnings.Where(predicate: w =>
                         IsThisFile(location: w.Location, fileName: fileName)))
            {
                diagnostics.Add(item: MakeDiagnostic(line: w.Location.Line,
                    column: w.Location.Column,
                    severity: 2,
                    code: w.CodeString,
                    message: w.SurfaceMessage,
                    length: SpanLen(line: w.Location.Line, col: w.Location.Column)));
            }

            // Keep the typed AST + tokens + registry so hover/definition/completion reuse this analysis.
            Docs[key: uri] = new DocState(Program: unit.Ast,
                Tokens: tokens,
                Lang: lang,
                Registry: verifier.Registry);
        }
        catch (GrammarException ex)
        {
            diagnostics.Add(item: MakeDiagnostic(line: ex.Line,
                column: ex.Column,
                severity: 1,
                code: ex.Code.ToCodeString(language: lang),
                message: ex.RawMessage));
        }
        catch (Exception ex)
        {
            // Never let an analyzer bug take down the server — surface it at the file head.
            diagnostics.Add(item: MakeDiagnostic(line: 1,
                column: 1,
                severity: 1,
                code: $"{Rules.ShortName}-LSP",
                message: $"internal analyzer error: {ex.Message}"));
        }

        return diagnostics;
    }

    /// <summary>Whether <paramref name="path"/> lies inside <paramref name="folder"/> (both full paths).</summary>
    private static bool IsUnder(string path, string folder)
    {
        string prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
        return path.StartsWith(value: prefix, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a diagnostic points into the analyzed document.</summary>
    private static bool IsThisFile(SourceLocation location, string fileName)
    {
        return !string.IsNullOrEmpty(value: location.FileName) &&
               string.Equals(a: Path.GetFullPath(path: location.FileName),
                   b: fileName,
                   comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The project a file belongs to, found the way <c>check</c> finds it: the folder of the nearest
    /// <c>config.toml</c> above it with that manifest's library folders, or the file's own folder when there is no
    /// manifest (or it can't be read).
    /// </summary>
    private static (string Root, IReadOnlyList<string> Libraries) ProjectOf(string fileName)
    {
        string folder = Path.GetDirectoryName(path: fileName) ?? ".";
        string? manifestPath = ManifestLoader.FindManifest(startDir: folder);
        if (manifestPath == null)
        {
            return (folder, []);
        }

        try
        {
            ProjectManifest manifest = ManifestLoader.Load(tomlPath: manifestPath, resolveExecutable: false);
            return (manifest.ManifestDirectory, manifest.Target.Libraries);
        }
        catch (Exception)
        {
            return (folder, []);
        }
    }

    /// <summary>The documents open in the editor, by full path, as the build driver reads them.</summary>
    private static Dictionary<string, string> OpenSources()
    {
        var sources = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);
        foreach ((string openUri, string openText) in Texts)
        {
            sources[key: Path.GetFullPath(path: UriToFileName(uri: openUri))] = openText;
        }

        return sources;
    }

    // The stdlib import index per language and library set, built once: re-parsing every stdlib file to find
    // its modules on each keystroke would cost most of a second.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        Lazy<IReadOnlyDictionary<string, string>>> StdlibIndexes = new();

    private static IReadOnlyDictionary<string, string> StdlibIndexFor(Language language, string stdlibRoot,
        IReadOnlyList<string> libraryRoots)
    {
        string key = $"{language}|{string.Join(separator: "|", values: libraryRoots)}";
        return StdlibIndexes.GetOrAdd(key: key,
            valueFactory: _ => new Lazy<IReadOnlyDictionary<string, string>>(valueFactory: () =>
                BuildDriver.BuildStdlibIndex(stdlibRoot: stdlibRoot, language: language,
                    libraryRoots: libraryRoots))).Value;
    }

    /// <summary>
    /// Builds an LSP Diagnostic. LSP positions are 0-based; our locations are 1-based. The range
    /// underlines a single character at the reported column (a token-length range is a later refinement).
    /// </summary>
    private static Dictionary<string, object?> MakeDiagnostic(int line, int column, int severity,
        string code, string message, int length = 1)
    {
        int l = Math.Max(val1: 0, val2: line - 1);
        int c = Math.Max(val1: 0, val2: column - 1);
        return new Dictionary<string, object?>
        {
            [key: PropRange] = new Dictionary<string, object?>
            {
                [key: PropStart] =
                    new Dictionary<string, object?>
                    {
                        [key: "line"] = l, [key: PropCharacter] = c
                    },
                [key: "end"] = new Dictionary<string, object?>
                {
                    [key: "line"] = l,
                    [key: PropCharacter] = c + Math.Max(val1: 1, val2: length)
                }
            },
            [key: "severity"] = severity, // 1 = Error, 2 = Warning
            [key: "code"] = code,
            [key: "source"] = _profile.ServerName,
            [key: "message"] = message
        };
    }

    private static void PublishDiagnostics(Stream stdout, string uri,
        List<Dictionary<string, object?>> diagnostics)
    {
        WriteNotification(stdout: stdout,
            method: "textDocument/publishDiagnostics",
            @params: new Dictionary<string, object?>
            {
                [key: "uri"] = uri, [key: "diagnostics"] = diagnostics
            });
    }

    // ── JSON-RPC framing ────────────────────────────────────────────────────────────────────

    private static void WriteResult(Stream stdout, JsonElement id, object? result)
    {
        WriteMessage(stdout: stdout,
            payload: new Dictionary<string, object?>
            {
                [key: "jsonrpc"] = "2.0", [key: "id"] = id, [key: "result"] = result
            });
    }

    private static void WriteNotification(Stream stdout, string method, object @params)
    {
        WriteMessage(stdout: stdout,
            payload: new Dictionary<string, object?>
            {
                [key: "jsonrpc"] = "2.0", [key: "method"] = method, [key: PropParams] = @params
            });
    }

    private static void WriteMessage(Stream stdout, object payload)
    {
        string json = JsonSerializer.Serialize(value: payload, options: JsonOptions);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(s: json);
        byte[] header = Encoding.ASCII.GetBytes(s: $"Content-Length: {bodyBytes.Length}\r\n\r\n");
        stdout.Write(buffer: header, offset: 0, count: header.Length);
        stdout.Write(buffer: bodyBytes, offset: 0, count: bodyBytes.Length);
        stdout.Flush();
    }

    /// <summary>Reads one Content-Length-framed message body, or null at EOF.</summary>
    private static byte[]? ReadMessage(Stream stdin)
    {
        int contentLength = -1;
        while (true)
        {
            string? line = ReadHeaderLine(stdin: stdin);
            if (line == null)
            {
                return null; // EOF mid-headers
            }

            if (line.Length == 0)
            {
                break; // blank line ends the header block
            }

            int colon = line.IndexOf(value: ':');
            if (colon > 0 && line[..colon]
                            .Trim()
                            .Equals(value: "Content-Length",
                                 comparisonType: StringComparison.OrdinalIgnoreCase) &&
                !int.TryParse(s: line[(colon + 1)..]
                       .Trim(),
                    result: out contentLength))
            {
                contentLength = -1; // malformed Content-Length header — treat as absent
            }
        }

        if (contentLength < 0)
        {
            return null;
        }

        byte[] buffer = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = stdin.Read(buffer: buffer, offset: read, count: contentLength - read);
            if (n <= 0)
            {
                return null; // EOF mid-body
            }

            read += n;
        }

        return buffer;
    }

    /// <summary>Reads one CRLF-terminated header line (byte by byte), stripped of the trailing CRLF.</summary>
    private static string? ReadHeaderLine(Stream stdin)
    {
        var sb = new StringBuilder();
        int prev = -1;
        while (true)
        {
            int b = stdin.ReadByte();
            if (b < 0)
            {
                return sb.Length == 0
                    ? null
                    : sb.ToString();
            }

            if (prev == '\r' && b == '\n')
            {
                sb.Length -= 1; // drop the '\r' already appended
                return sb.ToString();
            }

            sb.Append(value: (char)b);
            prev = b;
        }
    }

    /// <summary>file:// URI → a plain path for the tokenizer/parser (best-effort; used only for messages).</summary>
    private static string UriToFileName(string uri)
    {
        if (!uri.StartsWith(value: "file:", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        // Clients escape the drive colon (JetBrains IDEs send `file:///l%3A/x.rf`), which Uri.LocalPath leaves as
        // `/l:/x.rf`. Unescape, drop the slashes, and give a path without a drive letter its root back.
        string path = Uri.UnescapeDataString(stringToUnescape: uri["file:".Length..])
                         .TrimStart('/');
        if (!(path.Length >= 2 && path[index: 1] == ':'))
        {
            path = "/" + path;
        }

        return path.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// The copy of a standard library file the builder reads, or null when the file is not part of the stdlib. A
    /// file under that stdlib is its own copy. The stdlib's sources are edited in a language project's
    /// <c>Standard</c> folder and copied next to the builder as <c>Standard/&lt;Language&gt;/...</c>.
    /// </summary>
    private static string? StdlibCopyOf(string fileName, string stdlibRoot)
    {
        if (IsUnder(path: fileName, folder: stdlibRoot))
        {
            return fileName;
        }

        if (!Directory.Exists(path: stdlibRoot))
        {
            return null;
        }

        for (string? folder = Path.GetDirectoryName(path: fileName);
             folder != null;
             folder = Path.GetDirectoryName(path: folder))
        {
            if (!string.Equals(a: Path.GetFileName(path: folder), b: "Standard",
                    comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = Path.GetRelativePath(relativeTo: folder, path: fileName);
            string? copy = Directory.EnumerateDirectories(path: stdlibRoot)
                                    .Select(selector: languageFolder =>
                                         Path.GetFullPath(path: Path.Combine(path1: languageFolder, path2: relative)))
                                    .FirstOrDefault(predicate: File.Exists);
            if (copy != null)
            {
                return copy;
            }
        }

        return null;
    }

    /// <summary>Whether two texts are the same apart from line endings.</summary>
    private static bool SameText(string a, string b)
    {
        return string.Equals(a: a.ReplaceLineEndings(), b: b.ReplaceLineEndings(), comparisonType: StringComparison.Ordinal);
    }
}
