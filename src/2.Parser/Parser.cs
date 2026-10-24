using Builder.Declaration;
using Builder.Diagnostics;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Parser;

/// <summary>
/// Unified parser for both RazorForge and Suflae languages.
/// Converts a stream of tokens into an Abstract Syntax Tree (AST).
/// Language-specific constructs are guarded by <see cref="_language"/> checks.
/// </summary>
public partial class Parser
{
    #region Base Parser Fields

    /// <summary>
    /// The list of tokens to parse.
    /// </summary>
    private readonly List<Token> _tokens;

    /// <summary>
    /// Current position in the token stream.
    /// </summary>
    private int _position;

    /// <summary>
    /// Collection of warnings generated during parsing.
    /// </summary>
    private readonly List<BuildWarning> _warnings = [];

    /// <summary>
    /// Collection of errors accumulated during error recovery.
    /// </summary>
    private readonly List<string> _errors = [];

    /// <summary>
    /// Structured form of the accumulated parse errors, preserving code + source position
    /// (the plain <see cref="_errors"/> strings are pre-formatted and lose the location fields).
    /// Consumed by the language server to emit positioned diagnostics.
    /// </summary>
    private readonly List<GrammarException> _structuredErrors = [];

    /// <summary>
    /// Returns true if any parse errors occurred during parsing.
    /// </summary>
    public bool HasErrors => _errors.Count > 0;

    /// <summary>
    /// Gets all parse errors encountered during parsing.
    /// </summary>
    public List<string> GetErrors()
    {
        return _errors;
    }

    /// <summary>
    /// Gets the accumulated parse errors with their code and source position preserved.
    /// </summary>
    public IReadOnlyList<GrammarException> GetStructuredErrors()
    {
        return _structuredErrors;
    }

    /// <summary>
    /// Whether a recovered error is printed to stderr the moment it is found. The build driver turns it off and
    /// reports the errors itself (from <see cref="GetStructuredErrors"/>), so each is printed once and stops the
    /// build.
    /// </summary>
    public bool PrintsErrors { get; init; } = true;

    /// <summary>Keeps an error the parser recovered from, and prints it unless the caller reports it.</summary>
    private void RecordError(GrammarException ex)
    {
        _errors.Add(item: ex.Message);
        _structuredErrors.Add(item: ex);
        if (PrintsErrors)
        {
            DiagnosticRenderer.Print(ex: ex, writer: Console.Error);
        }
    }

    /// <summary>
    /// The source file name for error reporting.
    /// </summary>
    public string FileName { get; set; } = "";

    /// <summary>
    /// The language being parsed (RazorForge or Suflae).
    /// Used to guard language-specific constructs.
    /// </summary>
    private readonly Language _language;

    /// <summary>The rules of the language being parsed.</summary>
    private Builder.Frontends.LanguageRules Rules => Builder.Frontends.Languages.For(language: _language);

    #endregion

    #region Indentation Fields

    /// <summary>
    /// Stack tracking indentation levels for block detection.
    /// </summary>
    private readonly Stack<int> _indentationStack = new();

    /// <summary>
    /// Current indentation level being parsed.
    /// </summary>
    private int _currentIndentationLevel;

    #endregion

    #region Guarded Parser State

    /// <summary>
    /// Prevents nested inline conditionals (if-then-else expressions).
    /// When true, 'if' at expression level is not parsed as inline conditional.
    /// This improves readability by forbidding constructs like:
    /// <c>if a then (if b then c else d) else e</c>
    /// </summary>
    private bool _parsingInlineConditional;

    /// <summary>
    /// Indicates whether we're currently parsing inside a type body (record, entity).
    /// When true, allows member variable declarations without var keywords.
    /// </summary>
    private bool _parsingTypeBody;

    /// <summary>
    /// Indicates whether we're parsing inside a record body (actual record, not entity).
    /// When true, only secret/posted/open modifiers are allowed (not external).
    /// Also var/preset keywords are disallowed (use 'name: Type' syntax).
    /// </summary>
    private bool _parsingStrictRecordBody;

    /// <summary>
    /// Indicates whether we're currently parsing inside a routine body.
    /// When true, nested routine declarations are rejected.
    /// </summary>
    private bool _inRoutineBody;

    /// <summary>The name of the routine whose body is being parsed, which a <c>todo</c> in it names when it
    /// crashes. Null outside a routine (a script's loose statements).</summary>
    private string? _routineBeingParsed;

    /// <summary>Whether the routine whose body is being parsed is declared <c>failable</c>: a <c>throw</c> in it
    /// goes to the caller, a <c>throw</c> anywhere else crashes where it stands.</summary>
    private bool _routineBeingParsedIsFailable;

    /// <summary>
    /// Indicates whether we are currently parsing within a 'when' pattern context.
    /// Used to disambiguate pattern matching syntax from regular expressions.
    /// </summary>
    private bool _inWhenPatternContext;

    /// <summary>
    /// Indicates we are parsing the condition of a subjectless (condition-based) 'when' arm.
    /// Suppresses bare-identifier lambda parsing so `flag => result` reads as
    /// condition `flag` + arm arrow, not lambda `flag => result`. Unlike
    /// _inWhenPatternContext it leaves the 'is' operator available, and it is
    /// suspended inside parentheses and argument lists so explicit lambdas there
    /// still parse.
    /// </summary>
    private bool _inWhenConditionContext;

    /// <summary>
    /// The reserved parameter name a single-hole <c>_</c> lambda desugars to. <c>xs.map(_ * 2)</c> parses the
    /// <c>_</c> as a reference to this name and wraps the whole argument in
    /// <c>LambdaExpression([hole], _*2)</c>.
    /// </summary>
    internal const string HoleParamName = "__rf_hole";

    /// <summary>
    /// Set by <c>ParsePrimary</c> when it parses a bare `_` placeholder (single-hole lambda). Read and
    /// reset per-argument by <c>ParseArgument</c>, which wraps the argument into a lambda when set — so
    /// the lambda boundary is the nearest enclosing argument.
    /// </summary>
    private bool _sawHole;

    /// <summary>
    /// Prevents 'is' expression parsing in when clause bodies.
    /// When true, 'is' is not treated as a pattern-matching operator.
    /// </summary>
    private bool _inWhenClauseBody;


    #endregion

    /// <summary>
    /// Creates a new unified parser for the given token stream and language.
    /// </summary>
    /// <param name="tokens">The tokens to parse.</param>
    /// <param name="language">The language being parsed (RazorForge or Suflae).</param>
    /// <param name="fileName">Optional source file name for error reporting.</param>
    public Parser(List<Token> tokens, Language language, string? fileName = null)
    {
        _tokens = tokens;
        ReadWordOperatorsAsNames(tokens: tokens);
        _language = language;
        FileName = fileName ?? "unknown";
        _indentationStack.Push(item: 0); // Base indentation level
    }

    /// <summary>
    /// The word operators (<c>to</c> <c>til</c> <c>by</c> <c>have</c> <c>lack</c> <c>is</c> <c>isnot</c> <c>in</c>
    /// <c>as</c> <c>and</c> <c>or</c>) are keywords only where an operator stands: right after something that ends an
    /// expression (<c>0 to 10</c>, <c>xs have v</c>, <c>each x in xs</c>). Anywhere else the word is a name
    /// (<c>var to = 3</c>, <c>routine step(by: S64)</c>, <c>by + to</c>), so it is turned into an identifier here,
    /// before parsing. A pattern arm still starts with <c>is</c> (<c>is S64 as n =&gt;</c>) or a flags arm with
    /// <c>have</c>/<c>lack</c> (<c>have READ =&gt;</c>), and an <c>else</c> arm
    /// still binds with <c>as</c>, so those two stay keywords there. <c>not</c> starts an expression, so it stays a
    /// keyword everywhere.
    /// </summary>
    private static void ReadWordOperatorsAsNames(List<Token> tokens)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[index: i];
            if (token.Type is not (TokenType.To or TokenType.Til or TokenType.By
                or TokenType.Is or TokenType.IsNot or TokenType.In or TokenType.As or TokenType.And or TokenType.Or))
            {
                continue;
            }

            TokenType before = i > 0
                ? tokens[index: i - 1].Type
                : TokenType.Newline;
            bool afterExpression = before < TokenType.Identifier || before is TokenType.Identifier or TokenType.Me
                or TokenType.MyType or TokenType.True or TokenType.False or TokenType.None or TokenType.NoneValue
                or TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace or TokenType.Question;
            bool patternArm = token.Type is TokenType.Is or TokenType.IsNot &&
                              before is TokenType.Newline or TokenType.Indent or TokenType.Dedent;
            bool elseBinding = token.Type == TokenType.As && before == TokenType.Else;
            // A member variable of a record pattern is observed or named after its colon: `item: is Weapon as w`,
            // `y: as y`.
            bool fieldPattern = token.Type is TokenType.Is or TokenType.IsNot or TokenType.As &&
                                before == TokenType.Colon;
            if (!afterExpression && !patternArm && !elseBinding && !fieldPattern)
            {
                tokens[index: i] = token with { Type = TokenType.Identifier };
            }
        }
    }

    /// <summary>
    /// The tokens that open a top-level declaration (or a declaration's <c>needs</c>/<c>relates</c> line) rather
    /// than a statement: file-level declarations, the modifiers written before a declaration, and the declaration
    /// keywords. The build's line scan (<see cref="Builder.ManifestLoader"/>) reads a file's unindented lines with
    /// this set to tell a script from a module file without parsing it. <see cref="Parse"/> checks every top-level
    /// declaration it makes against the set, so a declaration keyword added to the parser without it stops the
    /// build loudly instead of turning files that use it into scripts.
    /// </summary>
    public static readonly IReadOnlySet<TokenType> DeclarationStarters = new HashSet<TokenType>
    {
        TokenType.Module, TokenType.Import, TokenType.Define, TokenType.Preset, TokenType.Global,
        TokenType.LateInit, TokenType.Var, TokenType.Routine, TokenType.Record, TokenType.Bundle,
        TokenType.Entity, TokenType.Choice, TokenType.Variant, TokenType.Flags, TokenType.Protocol,
        TokenType.Crashable, TokenType.Dangerous, TokenType.Failable, TokenType.Common, TokenType.Secret,
        TokenType.Posted, TokenType.Needs, TokenType.Relates
    };

    /// <summary>
    /// Words the language no longer has that still open a routine declaration (<c>suspended routine</c>): the
    /// parser reports them, so the build's line scan counts them as declaration starters too.
    /// </summary>
    public static readonly IReadOnlySet<string> RetiredDeclarationWords = new HashSet<string>(comparer: StringComparer.Ordinal)
    {
        "suspended", "threaded"
    };

    /// <summary>
    /// Parses the token stream into a complete program AST.
    /// Main entry point for parsing source files.
    /// </summary>
    /// <returns>A <see cref="SyntaxTree.Program"/> containing all top-level declarations.</returns>
    public Program Parse()
    {
        var declarations = new List<ISyntaxTreeNode>();

        // Doc-comment (### ...) lines accumulate here until the next declaration claims them.
        var pendingDoc = new List<string>();

        while (!IsAtEnd)
        {
            try
            {
                // Skip newlines at top level
                if (CheckAndAdvance(type: TokenType.Newline))
                {
                    continue;
                }

                // Accumulate doc-comment (### ...) lines; they attach to the NEXT declaration. A module
                // file may also end with a block of documentation that declares nothing (e.g. the
                // BuilderExpansion capability-gate module) — that trailing block simply never gets claimed
                // and is discarded when the loop ends, so it can't drive ParseDeclaration into an EOF error.
                if (Check(type: TokenType.DocComment))
                {
                    pendingDoc.Add(item: Advance()
                                        .Text
                                        .Trim());
                    continue;
                }

                // Handle dedent tokens (should not occur at top level, but be safe)
                if (Check(type: TokenType.Dedent))
                {
                    ProcessDedentTokens();
                    continue;
                }

                Token first = CurrentToken;
                ISyntaxTreeNode decl = ParseDeclaration();
                CheckDeclarationStarter(first: first, node: decl);
                if (pendingDoc.Count > 0 && decl is SyntaxTree.Declaration documented)
                {
                    documented.Documentation = string.Join(separator: "\n", values: pendingDoc);
                }

                pendingDoc.Clear();
                declarations.Add(item: decl);
            }
            catch (GrammarException ex)
            {
                // GrammarException.Message already contains formatted error:
                // error[RF-G150]: filename.rf:9:14: message
                // error[SF-G150]: filename.sf:9:14: message
                RecordError(ex: ex);
                Synchronize();
            }
        }

        declarations = WrapScriptStatementsIntoStart(nodes: declarations);

        return new Program(Declarations: declarations, Location: GetLocation());
    }

    /// <summary>
    /// A top-level declaration must open with a token of <see cref="DeclarationStarters"/> (or an annotation),
    /// because the build tells scripts from module files by that set. A declaration opened by any other token is
    /// a parser change that forgot the set: it is reported as a builder bug at the declaration. After a parse error
    /// the recovery may resume inside a declaration (a member variable line of a type body), so only a file parsed
    /// cleanly so far is checked.
    /// </summary>
    private void CheckDeclarationStarter(Token first, ISyntaxTreeNode node)
    {
        if (_errors.Count > 0 || node is not SyntaxTree.Declaration ||
            first.Type is TokenType.At or TokenType.DocComment ||
            DeclarationStarters.Contains(item: first.Type))
        {
            return;
        }

        throw new InvalidOperationException(
            message: $"Builder bug: {FileName}:{first.Line}:{first.Column}: a top-level declaration opens with " +
                     $"'{first.Text}' ({first.Type}), which Parser.DeclarationStarters does not list. Add it there, " +
                     "or the build's line scan reads files that start a line with it as scripts.");
    }

    /// <summary>
    /// Script mode (RazorForge and Suflae): a file whose top level has loose STATEMENTS (an expression, `each`/`while`/
    /// `if`, an assignment, …) needs no explicit entry point — those statements, together with any top-level
    /// runtime <c>var</c> declarations, become the body of an implicit <c>routine start()</c> (in source
    /// order; a trailing <c>return</c> is added). Hoistable declarations (module/import/type/routine/preset/
    /// define) stay as siblings and hoist as usual. A no-op for a normal module file (no loose statements).
    /// An explicit <c>start</c> alongside top-level statements is a conflict. Only the build's entry file
    /// may be a script (BuildDriver reports one anywhere else).
    /// </summary>
    private List<ISyntaxTreeNode> WrapScriptStatementsIntoStart(List<ISyntaxTreeNode> nodes)
    {
        // Trigger on a loose top-level STATEMENT — a pure module file (declarations only) is untouched. A file of only
        // `var` lines (`var total = 1 + 2`) is a script too, as long as it writes no `routine start()` of its own.
        bool varsOnlyScript = nodes.Any(predicate: n => n is VariableDeclaration { IsGlobal: false }) &&
                              !nodes.Any(predicate: n => n is RoutineDeclaration { Name: "start" });
        if (!nodes.Any(predicate: n => n is Statement) && !varsOnlyScript)
        {
            return nodes;
        }

        // Collect the executable top-level nodes (statements + runtime var decls) in source order.
        var body = new List<Statement>();
        var kept = new List<ISyntaxTreeNode>();
        SourceLocation startLoc = PartitionScriptNodes(nodes: nodes,
            body: body,
            kept: kept,
            explicitStart: out RoutineDeclaration? explicitStart);

        if (explicitStart != null)
        {
            return ReportScriptStartConflict(kept: kept, startLoc: startLoc);
        }

        if (body.Count == 0 || body[^1] is not ReturnStatement)
        {
            body.Add(item: new ReturnStatement(Value: null, Location: startLoc));
        }

        var scriptVariables = new HashSet<string>(
            collection: body.OfType<DeclarationStatement>()
                            .Select(selector: d => d.Declaration)
                            .OfType<VariableDeclaration>()
                            .Select(selector: v => v.Name),
            comparer: StringComparer.Ordinal);
        kept.Add(item: new RoutineDeclaration(Name: "start",
            Parameters: [],
            ReturnType: null,
            Body: new BlockStatement(Statements: body, Location: startLoc),
            Visibility: VisibilityModifier.Open,
            Annotations: [],
            Location: startLoc)
        {
            IsScriptEntry = true,
            ScriptVariableNames = scriptVariables
        });
        return kept;
    }

    /// <summary>
    /// Splits the top-level nodes into the implicit-<c>start</c> body (loose statements + runtime var
    /// declarations, wrapped) and the kept hoistable declarations, in source order. Records any explicit
    /// <c>routine start()</c> found via <paramref name="explicitStart"/>. Returns the source location of
    /// the first executable node (or a default location when there is none).
    /// </summary>
    private SourceLocation PartitionScriptNodes(List<ISyntaxTreeNode> nodes, List<Statement> body,
        List<ISyntaxTreeNode> kept, out RoutineDeclaration? explicitStart)
    {
        SourceLocation startLoc = GetLocation();
        bool locSet = false;
        explicitStart = null;
        foreach (ISyntaxTreeNode n in nodes)
        {
            switch (n)
            {
                case Statement s:
                    if (!locSet)
                    {
                        startLoc = s.Location;
                        locSet = true;
                    }

                    body.Add(item: s);
                    break;
                // A Suflae `global` is module-level state, not a statement: it stays a sibling
                // declaration so ModuleGlobalsSynthesisPass moves it onto the __ModuleGlobals singleton.
                case VariableDeclaration { IsGlobal: false } vd:
                    if (!locSet)
                    {
                        startLoc = vd.Location;
                        locSet = true;
                    }

                    body.Add(
                        item: new DeclarationStatement(Declaration: vd, Location: vd.Location));
                    break;
                default:
                    if (n is RoutineDeclaration { Name: "start" } rd) { explicitStart = rd; }

                    kept.Add(item: n);
                    break;
            }
        }

        return startLoc;
    }

    /// <summary>
    /// Reports the conflict between top-level statements and an explicit <c>routine start()</c> as a
    /// clean diagnostic (matching the per-statement error path) rather than throwing out of the parser,
    /// and returns the kept declarations so the Program stays well-formed.
    /// </summary>
    private List<ISyntaxTreeNode> ReportScriptStartConflict(List<ISyntaxTreeNode> kept,
        SourceLocation startLoc)
    {
        var ex = new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
            message:
            "A file cannot mix top-level statements with an explicit `routine start()`. " +
            "Either move the top-level statements into start(), or remove the explicit start().",
            fileName: FileName,
            line: startLoc.Line,
            column: startLoc.Column,
            language: _language);
        RecordError(ex: ex);
        return kept;
    }

    /// <summary>
    /// Parses a single top-level or nested declaration.
    /// Handles: module, import, define, using, var, routine, entity, record, choice, variant, protocol, impl.
    /// RazorForge-only: external, dangerous modifier, threaded async status.
    /// </summary>
    /// <remarks>
    /// Declaration parsing order (checked in sequence):
    ///
    /// FILE-LEVEL DECLARATIONS (must appear first):
    ///   module       - Module declaration
    ///   import       - Import external modules
    ///   define       - Type alias/redefinition
    ///   preset       - Build-time constant
    ///
    /// MODIFIERS (optional, parsed before declaration):
    ///   annotations   - @crash_only, @inline, @llvm("i32"), etc.
    ///   visibility   - secret, posted, open, external
    ///   storage      - common, global
    ///
    /// RF-ONLY MODIFIERS:
    ///   dangerous    - Marks routine as unsafe (RazorForge only)
    ///
    /// TYPE/VALUE DECLARATIONS:
    ///   external     - FFI routine declaration (RazorForge only)
    ///   name: Type  - Member variable declaration (inside type bodies)
    ///   var          - Variable declarations
    ///   nothing      - The body of a type with no member variables (RazorForge only)
    ///   routine      - FreeRoutine declaration
    ///   entity       - Heap-allocated reference type
    ///   record       - Stack-allocated value type
    ///   choice       - Simple enumeration
    ///   variant      - Tagged union (sum type)
    ///   protocol     - Interface/trait definition
    ///
    /// SPECIAL DECLARATION:
    ///   using        - Resource management (declaration form, no body block)
    ///
    /// If no declaration keyword matches, falls through to ParseStatement.
    /// </remarks>
    /// <returns>The parsed declaration node.</returns>
    /// <exception cref="GrammarException">Thrown when no valid declaration or statement can be parsed.</exception>
    private ISyntaxTreeNode ParseDeclaration()
    {
        SkipDocCommentsAndTargetAnnotation();

        ISyntaxTreeNode? fileLevelDecl = TryParseFileLevelDeclaration();
        if (fileLevelDecl != null)
        {
            return fileLevelDecl;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PARSE MODIFIERS (annotations, visibility, storage class)
        // ═══════════════════════════════════════════════════════════════════════════

        // Parse annotations (e.g., @inline, @crash_only, @llvm("i32"))
        List<string> annotations = ParseAnnotations();

        // Skip newlines between annotations and the declaration they modify
        // e.g., @readonly\nroutine foo() should work
        if (annotations.Count > 0)
        {
            while (CheckAndAdvance(type: TokenType.Newline))
            {
                // Skip newlines
            }
        }

        // Parse visibility and storage class modifiers
        (VisibilityModifier visibility, bool isCommon) = ParseModifiers();

        // Define declaration with annotations (e.g., @llvm("i32") define MyInt as S32)
        if (CheckAndAdvance(type: TokenType.Define))
        {
            return ParseDefineDeclaration(annotations: annotations);
        }

        // Check for dangerous modifier: dangerous routine foo(), dangerous external("C") routine bar()
        bool isDangerous = CheckAndAdvance(type: TokenType.Dangerous);

        Token beforeVarOrField = CurrentToken;
        ISyntaxTreeNode? varOrField = TryParseTypeBodyOrVariableDeclaration(
            visibility: visibility,
            annotations: annotations);
        if (varOrField != null)
        {
            RejectNoEffect(when: isDangerous, at: beforeVarOrField,
                message: "'dangerous' marks a routine whose signature exposes raw unsafety, and has no effect on a " +
                         "variable, a member variable or a preset. Remove it (an unsafe operation goes in a 'danger' block).");
            RejectNoEffect(when: _failable, at: beforeVarOrField,
                message: "'failable' marks a routine that can fail, and has no effect on a variable, a member " +
                         "variable or a preset. Remove it.");
            RejectNoEffect(when: isCommon, at: beforeVarOrField,
                message: "'common' marks a routine that belongs to its type, not to a value, and has no effect on a " +
                         "variable, a member variable or a preset. Remove it.");
            return varOrField;
        }

        ISyntaxTreeNode declaration = ParseRoutineOrTypeDeclaration(visibility: visibility,
            annotations: annotations,
            isCommon: isCommon,
            isDangerous: isDangerous);
        if (_failable)
        {
            // Nothing took the modifier: it stood before a type or another declaration that can't fail.
            _failable = false;
            throw ThrowParseError(code: GrammarDiagnosticCode.FailableNotARoutine,
                message: "'failable' marks a routine that can fail, and has no effect here. Remove it.",
                token: _failableToken ?? CurrentToken);
        }

        return declaration;
    }

    /// <summary>
    /// Skips leading doc-comment tokens (and their trailing newlines) and the optional file-granularity
    /// <c>@target(...)</c> build directive, which was already consumed by the build's file gate and only
    /// needs to be discarded by the parser.
    /// </summary>
    private void SkipDocCommentsAndTargetAnnotation()
    {
        // Doc comments are preserved in the token stream but currently not attached to declarations.
        // Skip them to prevent "Unexpected token" errors.
        while (CheckAndAdvance(type: TokenType.DocComment))
        {
            while (CheckAndAdvance(type: TokenType.Newline))
            {
                /* consume trailing newlines after doc comment */
            }
        }

        // The @target(...) annotation is read pre-parse by the build's file gate; discard it here.
        // Keeping it a real @-annotation (not a comment) gives it editor highlighting.
        if (Check(type: TokenType.At) && PeekToken(offset: 1)
               .Type == TokenType.Identifier && PeekToken(offset: 1)
               .Text == "target")
        {
            ParseAnnotations();
            while (CheckAndAdvance(type: TokenType.Newline))
            {
                /* consume trailing newlines after @target */
            }
        }
    }

    /// <summary>
    /// Attempts to parse a file-level declaration (module/import/define/preset) that requires no
    /// leading modifiers. Returns the parsed node when matched, or null to signal the caller to
    /// continue with modifier-prefixed declaration parsing.
    /// </summary>
    private ISyntaxTreeNode? TryParseFileLevelDeclaration()
    {
        if (CheckAndAdvance(type: TokenType.Module))
        {
            return ParseModuleDeclaration();
        }

        if (CheckAndAdvance(type: TokenType.Import))
        {
            return ParseImportDeclaration();
        }

        if (CheckAndAdvance(type: TokenType.Define))
        {
            return ParseDefineDeclaration();
        }

        if (CheckAndAdvance(type: TokenType.Preset))
        {
            return ParsePresetDeclaration();
        }

        return null;
    }

    /// <summary>
    /// Attempts to parse a type-body field, variable, or `nothing` body line. Returns the parsed node when
    /// matched, or null to signal the caller to continue with routine/type declaration parsing.
    /// </summary>
    private ISyntaxTreeNode? TryParseTypeBodyOrVariableDeclaration(VisibilityModifier visibility,
        List<string> annotations)
    {
        // Decl-position expand: `expand m in allmemvarof(T)` inside a record/entity body generates one
        // member-variable column per member of the (concrete-at-instantiation) source type.
        if (_parsingTypeBody && Check(type: TokenType.Expand))
        {
            return ParseExpandMemberDeclaration();
        }

        // Field declaration in type bodies: name: Type — identifier followed by colon, no var keyword.
        if (_parsingTypeBody && Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            RejectNoEffect(when: annotations.Count > 0, at: CurrentToken,
                message: $"An annotation ({string.Join(separator: ", ", values: annotations.Select(selector: a => "@" + a))}) " +
                         "has no effect on a member variable. Remove it.");
            return ParseTypeBodyFieldDeclaration(visibility: visibility);
        }

        // Variable declarations — optionally prefixed with `lateinit`
        bool declLateInit = false;
        if (Check(type: TokenType.LateInit) && PeekToken(offset: 1)
               .Type == TokenType.Var)
        {
            Advance(); // consume 'lateinit'
            declLateInit = true;
        }

        // `secret preset NAME` — route to preset parser so it carries the secret flag.
        if (Check(type: TokenType.Preset))
        {
            RejectNoEffect(when: annotations.Count > 0, at: CurrentToken,
                message: $"An annotation ({string.Join(separator: ", ", values: annotations.Select(selector: a => "@" + a))}) " +
                         "has no effect on a preset. Remove it.");
            return ParsePresetInDeclarationPosition(visibility: visibility);
        }

        // `global NAME: Type = value` — module-level mutable global (Suflae only).
        if (Check(type: TokenType.Global))
        {
            return ParseGlobalInDeclarationPosition(visibility: visibility,
                annotations: annotations);
        }

        if (CheckAndAdvance(type: TokenType.Var))
        {
            if (_parsingTypeBody)
            {
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                    message: "Type member variables cannot use 'var' or 'preset'. " +
                             "Use 'name: Type' syntax instead",
                    fileName: FileName,
                    line: CurrentToken.Line,
                    column: CurrentToken.Column,
                    language: _language);
            }

            // `var (a, b) = pair` at the top of a script: a destructuring statement, which the script wrap
            // carries into the synthesized start() with the other loose statements.
            if (Check(type: TokenType.LeftParen) && !declLateInit && annotations.Count == 0 &&
                visibility == VisibilityModifier.Open)
            {
                return ParseDestructuringDeclaration();
            }

            return ParseVariableDeclaration(visibility: visibility,
                annotations: annotations,
                isLateInit: declLateInit);
        }

        // `nothing`: a line with no work, or the body of a type with no member variables.
        if (Rules.HasNothingStatement && AcceptNothing())
        {
            ConsumeStatementTerminator();
            return _parsingTypeBody
                ? new NothingDeclaration(Location: GetLocation())
                : (ISyntaxTreeNode)new NothingStatement(Location: GetLocation());
        }

        return null;
    }

    /// <summary>
    /// Parses a routine declaration (with optional async modifiers) or a named type declaration
    /// (entity/record/choice/flags/crashable/variant/protocol), falling through to statement parsing
    /// when no keyword matches and no modifiers were consumed. Validates that any consumed visibility
    /// or annotation modifiers are not left dangling.
    /// </summary>
    private ISyntaxTreeNode ParseRoutineOrTypeDeclaration(VisibilityModifier visibility,
        List<string> annotations, bool isCommon, bool isDangerous)
    {
        RejectRetiredConcurrencyModifier();

        if (CheckAndAdvance(type: TokenType.Routine))
        {
            return ParseRoutineOrForeignDeclaration(visibility: visibility,
                annotations: annotations,
                isCommon: isCommon,
                isDangerous: isDangerous);
        }

        RejectCommonOnTypeDeclaration(isCommon: isCommon);
        RejectNoEffectOnTypeDeclaration(annotations: annotations, visibility: visibility, isDangerous: isDangerous);

        // An `@rf("...")` binding may sit on any type declaration: it is kept on the node for the standard library
        // loader, which takes the declaration out (see RfBindingCheck).
        // The standard library's `@self_locking` on an entity is kept the same way (the type registration reads it).
        // `@reflectable` and a program's own annotations (`@Tagged`) are kept the same way for ObjectHacker.
        List<string> bindings = annotations.Where(predicate: a => a.StartsWith(value: "rf(",
                                                     comparisonType: StringComparison.Ordinal) ||
                                                 a == RuntimeContract.RoamedMemberRoutine.SelfLockingAnnotation ||
                                                 a == "reflectable" ||
                                                 IsProgramAnnotation(name: AnnotationName(annotation: a),
                                                     language: _language))
                                           .ToList();
        SyntaxTree.Declaration Bound(SyntaxTree.Declaration declaration)
        {
            return bindings.Count > 0
                ? declaration with { LeadingAnnotations = bindings }
                : declaration;
        }

        if (CheckAndAdvance(type: TokenType.Entity))
        {
            return Bound(declaration: ParseEntityDeclaration(visibility: visibility));
        }

        if (CheckAndAdvance(type: TokenType.Record))
        {
            return ParseRecordDeclaration(visibility: visibility, annotations: annotations);
        }

        if (CheckAndAdvance(type: TokenType.Bundle))
        {
            return ParseRecordDeclaration(visibility: visibility, annotations: annotations) with { IsBundle = true };
        }

        if (CheckAndAdvance(type: TokenType.Choice))
        {
            return Bound(declaration: ParseChoiceDeclaration(visibility: visibility));
        }

        if (CheckAndAdvance(type: TokenType.Flags))
        {
            return Bound(declaration: ParseFlagsDeclaration(visibility: visibility));
        }

        if (CheckAndAdvance(type: TokenType.Crashable))
        {
            return Bound(declaration: ParseCrashableDeclaration(visibility: visibility));
        }

        if (CheckAndAdvance(type: TokenType.Variant))
        {
            return Bound(declaration: ParseVariantDeclaration());
        }

        if (CheckAndAdvance(type: TokenType.Protocol))
        {
            return Bound(declaration: ParseProtocolDeclaration(visibility: visibility));
        }

        if (visibility != VisibilityModifier.Open)
        {
            const string validDeclarations =
                "routine, entity, record, choice, variant, protocol, preset, or var";
            throw ThrowParseError(code: GrammarDiagnosticCode.VisibilityWithoutDeclaration,
                message: $"Visibility modifier '{visibility}' must be followed by a declaration " +
                         $"({validDeclarations})");
        }

        if (annotations.Count > 0)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.AnnotationsWithoutDeclaration,
                message:
                "Annotations must be followed by a declaration (routine, entity, record, etc.)");
        }

        return ParseStatement();
    }

    /// <summary>
    /// Parses a member-variable field declaration (<c>name: Type</c>) inside a type body, after the
    /// leading identifier+colon has been detected. Rejects the <c>external</c> visibility inside a
    /// strict record body.
    /// </summary>
    private VariableDeclaration ParseTypeBodyFieldDeclaration(VisibilityModifier visibility)
    {
        // In record bodies, external is not allowed
        if (_parsingStrictRecordBody && visibility is VisibilityModifier.External)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message:
                $"'{visibility.ToString().ToLower()}' is not valid for record member variables. " +
                "Record member variables can use 'secret', 'posted', or 'open'",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }

        return ParseMemberVariableDeclaration(visibility: visibility);
    }

    /// <summary>
    /// Handles a <c>preset</c> keyword encountered in declaration position (after modifiers). Rejects
    /// <c>preset</c> inside a type body and routes to <see cref="ParsePresetDeclaration"/>, carrying the
    /// secret (file-private) flag from the visibility modifier.
    /// </summary>
    private PresetDeclaration ParsePresetInDeclarationPosition(VisibilityModifier visibility)
    {
        if (_parsingTypeBody)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message: "Type member variables cannot use 'var' or 'preset'. " +
                         "Use 'name: Type' syntax instead",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }

        Advance(); // consume 'preset'
        return ParsePresetDeclaration(isSecret: visibility == VisibilityModifier.Secret);
    }

    /// <summary>
    /// Handles a <c>global</c> keyword encountered in declaration position. Rejects <c>global</c> in
    /// RazorForge (no module-level mutable state) and inside a type body, then routes to
    /// <see cref="ParseGlobalDeclaration"/>.
    /// </summary>
    private VariableDeclaration ParseGlobalInDeclarationPosition(VisibilityModifier visibility,
        List<string> annotations)
    {
        if (!Rules.HasModuleGlobals)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message: $"{Rules.Name} has no 'global': there is no module-level mutable state " +
                         "(thread state through parameters or a heap entity; use 'preset' for constants)",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }

        if (_parsingTypeBody)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message:
                "Type member variables cannot use 'global'. Use 'name: Type' syntax instead",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }

        Advance(); // consume 'global'
        return ParseGlobalDeclaration(visibility: visibility, annotations: annotations);
    }

    /// <summary>
    /// Reports a routine declaration written with the concurrency words the language no longer has
    /// (<c>suspended routine</c>, <c>threaded routine</c>): any routine runs concurrently when its caller converts it
    /// with <c>f.to[Work](...)</c>.
    /// </summary>
    private void RejectRetiredConcurrencyModifier()
    {
        if (CurrentToken.Type != TokenType.Identifier ||
            !RetiredDeclarationWords.Contains(item: CurrentToken.Text) ||
            PeekToken(offset: 1).Type != TokenType.Routine)
        {
            return;
        }

        throw new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
            message: $"You wrote '{CurrentToken.Text} routine', but a routine declares nothing about where it runs: " +
                     "any routine can run concurrently. Remove " +
                     $"'{CurrentToken.Text}', and convert the routine to get a Work you start: " +
                     "'work.to[Work](x: 1)'.",
            fileName: FileName,
            line: CurrentToken.Line,
            column: CurrentToken.Column,
            language: _language);
    }

    /// <summary>
    /// Parses a routine declaration after the <c>routine</c> keyword has been consumed, dispatching to
    /// a realm-qualified FOREIGN routine (<c>routine C::malloc(...)</c> / <c>routine LLVM::sqrt(...)</c>)
    /// when a realm tag before <c>::</c> is present, otherwise an ordinary routine declaration.
    /// </summary>
    /// <summary>
    /// Reports an annotation or modifier written where it has no effect. The parser used to read and drop it, so
    /// the writer saw nothing happen and nothing said.
    /// </summary>
    private void RejectNoEffect(bool when, Token at, string message)
    {
        if (!when)
        {
            return;
        }

        // Recorded, not thrown: the code is well-formed, so parsing goes on and the rest of the body is not lost.
        RecordError(ex: new GrammarException(code: GrammarDiagnosticCode.ModifierHasNoEffect,
            message: message,
            fileName: FileName,
            line: at.Line,
            column: at.Column,
            language: _language));
    }

    /// <summary>
    /// The annotations and modifiers a type declaration other than a record does not take: annotations on an
    /// entity, choice, flags, crashable, variant or protocol; `dangerous` on any type; a visibility on a variant.
    /// </summary>
    /// <summary>An annotation's name without its arguments (<c>Route</c> for <c>Route(path="/")</c>).</summary>
    internal static string AnnotationName(string annotation)
    {
        int open = annotation.IndexOf(value: '(');
        return open < 0 ? annotation : annotation[..open];
    }

    private void RejectNoEffectOnTypeDeclaration(List<string> annotations, VisibilityModifier visibility,
        bool isDangerous)
    {
        string? kind = CurrentToken.Type switch
        {
            TokenType.Entity => "an entity",
            TokenType.Choice => "a choice",
            TokenType.Flags => "a flags type",
            TokenType.Crashable => "a crashable",
            TokenType.Variant => "a variant",
            TokenType.Protocol => "a protocol",
            TokenType.Record => "a record",
            TokenType.Bundle => "a bundle",
            _ => null
        };
        if (kind == null)
        {
            return;
        }

        List<string> ineffective = annotations.Where(predicate: a => !a.StartsWith(value: "rf(",
                                                      comparisonType: StringComparison.Ordinal) &&
                                                  !(a == RuntimeContract.RoamedMemberRoutine.SelfLockingAnnotation &&
                                                    CurrentToken.Type == TokenType.Entity) &&
                                                  !((a == "reflectable" ||
                                                     IsProgramAnnotation(name: AnnotationName(annotation: a),
                                                         language: _language)) &&
                                                    CurrentToken.Type == TokenType.Entity))
                                              .ToList();
        RejectNoEffect(when: ineffective.Count > 0 && CurrentToken.Type is not (TokenType.Record or TokenType.Bundle),
            at: CurrentToken,
            message: $"An annotation ({string.Join(separator: ", ", values: ineffective.Select(selector: a => "@" + a))}) " +
                     $"has no effect on {kind}. Remove it.");
        RejectNoEffect(when: isDangerous, at: CurrentToken,
            message: $"'dangerous' marks a routine whose signature exposes raw unsafety, and has no effect on {kind}. " +
                     "Remove it.");
        RejectNoEffect(when: visibility != VisibilityModifier.Open && CurrentToken.Type == TokenType.Variant,
            at: CurrentToken,
            message: $"A variant takes no visibility modifier yet ('{visibility.ToString().ToLowerInvariant()}' would " +
                     "be ignored). Remove it.");
    }

    private ISyntaxTreeNode ParseRoutineOrForeignDeclaration(VisibilityModifier visibility,
        List<string> annotations, bool isCommon,
        bool isDangerous)
    {
        // Realm-qualified FOREIGN routine: `routine C::malloc(...)` / `routine LLVM::sqrt(...)`. The
        // realm tag before `::` picks the calling convention; the declaration is an ExternalDeclaration
        // (no body, foreign impl) — the modern spelling of `external("C"|"llvm") routine ...`.
        if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.DoubleColon)
        {
            string? conv = CurrentToken.Text switch
            {
                "C" => "C",
                "LLVM" => "llvm",
                _ => null
            };
            if (conv != null)
            {
                RejectNoEffect(when: visibility != VisibilityModifier.Open, at: CurrentToken,
                    message: $"A foreign routine ({CurrentToken.Text}::) takes no visibility modifier " +
                             $"('{visibility.ToString().ToLowerInvariant()}' would be ignored). Remove it.");
                RejectNoEffect(when: isCommon, at: CurrentToken,
                    message: $"'common' has no effect on a foreign routine ({CurrentToken.Text}::). Remove it.");
                Advance(); // realm tag (C / LLVM)
                Advance(); // ::
                return ParseExternalDeclaration(callingConvention: conv,
                    annotations: annotations,
                    isDangerous: isDangerous);
            }
        }

        return ParseRoutineDeclaration(visibility: visibility,
            annotations: annotations,
            isCommon: isCommon,
            isDangerous: isDangerous);
    }

    /// <summary>
    /// Rejects a storage-class modifier (<c>common</c>/<c>global</c>) placed before a type-declaration
    /// keyword (entity/record/choice/flags/crashable/variant/protocol), which is not valid.
    /// </summary>
    private void RejectCommonOnTypeDeclaration(bool isCommon)
    {
        if (!isCommon)
        {
            return;
        }

        bool isTypeKeyword = Check(TokenType.Entity,
            TokenType.Record,
            TokenType.Bundle,
            TokenType.Choice,
            TokenType.Flags,
            TokenType.Crashable,
            TokenType.Variant,
            TokenType.Protocol);

        if (isTypeKeyword)
        {
            throw new GrammarException(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message: "'common' storage class is not valid for type declarations",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
        }
    }

    /// <summary>
    /// Parses a single statement within a block or function body.
    /// Handles: if, while, for, when, return, throw, absent, break, continue, and expression statements.
    /// RazorForge-only: danger block, steal expression, release statement, block statements.
    /// </summary>
    /// <remarks>
    /// Statement types (checked in sequence):
    ///
    /// INDENTATION HANDLING:
    ///   dedent       - Process block end
    ///   newlines     - Skip empty lines
    ///
    /// CONTROL FLOW:
    ///   if/unless    - Conditional branching
    ///   while/loop   - Loop constructs
    ///   for          - Iteration over ranges/collections
    ///   when         - Pattern matching (switch-like)
    ///
    /// JUMP STATEMENTS:
    ///   return       - Return from routine (with optional value)
    ///   becomes      - argument assign with if-elseif-else
    ///   break        - Exit loop
    ///   continue     - Skip to next iteration
    ///
    /// SPECIAL STATEMENTS:
    ///   throw        - Throw error (in failable routines)
    ///   absent       - Return none (in failable routines)
    ///   nothing      - A line with no work
    ///   using        - Resource management (declaration form)
    ///
    /// MEMORY BLOCKS (RazorForge only):
    ///   danger      - Unsafe block (raw pointers, FFI)
    ///   release      - Early resource cleanup
    ///
    /// DECLARATIONS IN STATEMENT CONTEXT:
    ///   var          - Variable declarations (including destructuring)
    ///
    /// BLOCK/EXPRESSION:
    ///   { ... }      - Block statement (RazorForge only)
    ///   expr         - Expression statement (fallback)
    /// </remarks>
    /// <returns>The parsed statement, or null if at end of block.</returns>
    private Statement ParseStatement()
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // INDENTATION HANDLING
        // ═══════════════════════════════════════════════════════════════════════════

        // Handle dedent tokens
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }

        // Skip newlines
        while (Check(type: TokenType.Newline))
        {
            Advance();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CONTROL FLOW STATEMENTS
        // ═══════════════════════════════════════════════════════════════════════════

        Statement? keywordStatement = ParseKeywordStatement();
        if (keywordStatement != null)
        {
            return keywordStatement;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // RF-ONLY: MEMORY/SCOPE BLOCKS
        // ═══════════════════════════════════════════════════════════════════════════

        // Danger block (unsafe operations)
        if (CheckAndAdvance(type: TokenType.Danger))
        {
            return ParseDangerStatement();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // DECLARATIONS IN STATEMENT CONTEXT
        // ═══════════════════════════════════════════════════════════════════════════

        // Variable declarations (can appear in statement context)
        bool stmtLateInit = false;
        if (Check(type: TokenType.LateInit) && PeekToken(offset: 1)
               .Type == TokenType.Var)
        {
            Advance(); // consume 'lateinit'
            stmtLateInit = true;
        }

        Token bindingKeyword = CurrentToken;
        if (CheckAndAdvance(TokenType.Var, TokenType.Preset))
        {
            bool isPreset = bindingKeyword.Type == TokenType.Preset;

            // Check if this is destructuring: var (a, b) = expr
            if (Check(type: TokenType.LeftParen))
            {
                if (isPreset)
                {
                    throw ThrowParseError(message: "A preset binds one name: preset NAME: T = value. To take a " +
                                                   "value apart, use 'var (a, b) = ...'.");
                }

                return ParseDestructuringDeclaration();
            }

            VariableDeclaration varDecl = ParseVariableDeclaration(isLateInit: stmtLateInit) with { IsPreset = isPreset };
            // Wrap the variable declaration as a declaration statement
            return new DeclarationStatement(Declaration: varDecl, Location: varDecl.Location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // EXPRESSION STATEMENT (FALLBACK)
        // ═══════════════════════════════════════════════════════════════════════════

        return ParseExpressionStatement();
    }
    /// <summary>
    /// Set only by <c>migrate --step until</c> while it asks analysis about the loops it rewrites: a loop written with
    /// <c>while</c> is then read without an error.
    /// </summary>
    public static bool ReadsWhileLoops { get; set; }

    /// <summary>Whether the statement starts <c>while c</c>: the name <c>while</c> followed on its line by the start
    /// of a value, which no statement that uses <c>while</c> as a name does.</summary>
    private bool AtWhileLoop()
    {
        if (CurrentToken is not { Type: TokenType.Identifier, Text: "while" })
        {
            return false;
        }

        Token next = PeekToken(offset: 1);
        if (next.Line != CurrentToken.Line)
        {
            return false;
        }

        // `while(x)` is a call of a routine named while, `while (x)` a condition.
        return next.Type is TokenType.Not or TokenType.Minus ||
               next.Type == TokenType.LeftParen && next.Column > CurrentToken.Column + CurrentToken.Text.Length ||
               next.Type is not (TokenType.LeftParen or TokenType.LeftBracket or TokenType.Minus) && StartsInfixOperand(token: next);
    }

    /// <summary>
    /// <c>while c</c>: a loop is written <c>until</c> with the condition that ends it, or <c>loop</c>, so this is
    /// RF-G224. The loop is read as written, so the rest of the file is still checked.
    /// </summary>
    private Statement ParseWrittenWhile()
    {
        Token word = CurrentToken;
        Token next = PeekToken(offset: 1);
        if (!ReadsWhileLoops)
        {
            Token after = PeekToken(offset: 2);
            SuggestedFix? fix = null;
            bool trueAlone = next.Type == TokenType.True && (after.Line != next.Line ||
                                                             after.Type is TokenType.Newline or TokenType.Indent or TokenType.Eof or TokenType.With);
            if (trueAlone)
            {
                fix = new SuggestedFix(Title: "Write 'loop'",
                    Edits: [new TextEdit(StartLine: word.Line, StartColumn: word.Column, EndLine: next.Line,
                        EndColumn: next.Column + next.Text.Length, NewText: "loop")]);
            }
            else if (next.Type == TokenType.Not)
            {
                fix = new SuggestedFix(Title: "Write 'until'",
                    Edits: [new TextEdit(StartLine: word.Line, StartColumn: word.Column, EndLine: after.Line,
                        EndColumn: after.Column, NewText: "until ")]);
            }

            RecordError(ex: ThrowParseError(code: GrammarDiagnosticCode.WhileLoop,
                message: "A loop is written `until` with the condition that ends it (`until i >= count`), or `loop` " +
                         "for a loop that ends only at a `break`, `return` or `throw`: `while` is not a keyword. " +
                         "`migrate --step until` rewrites these loops.",
                fix: fix));
        }

        Advance();
        return ParseWhileStatement();
    }

    /// <summary>Whether the line is the old <c>pass</c>: the name <c>pass</c> alone up to the end of its line.</summary>
    private bool AtWrittenPass()
    {
        if (CurrentToken is not { Type: TokenType.Identifier, Text: "pass" })
        {
            return false;
        }

        Token next = PeekToken(offset: 1);
        return next.Line != CurrentToken.Line ||
               next.Type is TokenType.Newline or TokenType.Dedent or TokenType.Eof;
    }

    /// <summary>
    /// Takes a <c>nothing</c> line. The old <c>pass</c> is taken too, after RF-G225 with the quick fix
    /// <c>nothing</c>, so the rest of the file is still checked.
    /// </summary>
    private bool AcceptNothing()
    {
        if (CheckAndAdvance(type: TokenType.Nothing))
        {
            return true;
        }

        if (!AtWrittenPass())
        {
            return false;
        }

        Token word = CurrentToken;
        RecordError(ex: ThrowParseError(code: GrammarDiagnosticCode.PassLine,
            message: "A line with no work is written `nothing`: `pass` is not a keyword. `migrate --step nothing` " +
                     "rewrites these lines.",
            fix: new SuggestedFix(Title: "Write 'nothing'",
                Edits: [new TextEdit(StartLine: word.Line, StartColumn: word.Column, EndLine: word.Line,
                    EndColumn: word.Column + word.Text.Length, NewText: "nothing")])));
        Advance();
        return true;
    }

    /// <summary>Whether the line is the old <c>pierce X</c>: the name <c>pierce</c> followed on its line by the
    /// error.</summary>
    private bool AtWrittenPierce()
    {
        if (CurrentToken is not { Type: TokenType.Identifier, Text: "pierce" })
        {
            return false;
        }

        Token next = PeekToken(offset: 1);
        return next.Line == CurrentToken.Line && next.Type == TokenType.Identifier;
    }

    /// <summary>Takes the old <c>pierce X</c> as a <c>throw X</c> after RF-G227, so the rest of the file is still
    /// checked. The quick fix writes <c>throw</c> only in a routine that is not failable, where it means the
    /// same.</summary>
    private ThrowStatement ParseWrittenPierce()
    {
        Token word = CurrentToken;
        SuggestedFix? fix = _routineBeingParsedIsFailable
            ? null
            : new SuggestedFix(Title: "Write 'throw'",
                Edits: [new TextEdit(StartLine: word.Line, StartColumn: word.Column, EndLine: word.Line,
                    EndColumn: word.Column + word.Text.Length, NewText: "throw")]);
        RecordError(ex: ThrowParseError(code: GrammarDiagnosticCode.PierceStatement,
            message: _routineBeingParsedIsFailable
                ? "`pierce` is not a keyword. You are in a failable routine, where a `throw` goes to the caller: to " +
                  "crash here, throw from a routine that is not failable, where a `throw` crashes where it stands."
                : "`pierce` is not a keyword: a `throw` in a routine that is not failable crashes where it stands, " +
                  "so write `throw`. `migrate --step pierce` rewrites these lines.",
            fix: fix));
        Advance();
        return ParseThrowStatement();
    }

    private Statement? ParseKeywordStatement()
    {
        if (AtWhileLoop())
        {
            return ParseWrittenWhile();
        }

        if (AtWrittenPierce())
        {
            return ParseWrittenPierce();
        }

        if (AcceptNothing())
        {
            return ParseNothingStatement();
        }

        switch (CurrentToken.Type)
        {
            case TokenType.If:
                Advance();
                return ParseIfStatement();
            case TokenType.Loop:
                Advance();
                return ParseLoopStatement();
            case TokenType.Each:
                Advance();
                return ParseEachStatement();
            case TokenType.Expand:
                Advance();
                return ParseExpandStatement();
            case TokenType.When:
                Advance();
                return ParseWhenStatement();
            case TokenType.Return:
                Advance();
                return ParseReturnStatement();
            case TokenType.Break:
                Advance();
                return ParseBreakStatement();
            case TokenType.Continue:
                Advance();
                return ParseContinueStatement();
            case TokenType.Throw:
                Advance();
                return ParseThrowStatement();
            case TokenType.Todo:
                Advance();
                return ParseTodoStatement();
            case TokenType.Until:
                Advance();
                return ParseUntilStatement();
            case TokenType.Becomes:
            {
                SourceLocation becomesAt = GetLocation();
                Advance();
                Expression value = ParseExpression();
                ConsumeStatementTerminator();
                return new ExpressionStatement(Expression: value, Location: becomesAt) { IsBecomes = true };
            }
            case TokenType.Using:
                Advance();
                return ParseUsingStatement();
            case TokenType.Viewing or TokenType.Modifying:
                return ParseAccessStatement(isTry: false);
            case TokenType.Try when PeekToken(offset: 1).Type is TokenType.Viewing or TokenType.Modifying:
                Advance();
                return ParseAccessStatement(isTry: true);
            case TokenType.Drop:
                Advance();
                return ParseDropStatement();
            case TokenType.Steal when StealStatementAhead():
                Advance();
                return ParseStealStatement();
            default:
                return null;
        }
    }
}
