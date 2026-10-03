using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Formatting;

/// <summary>Printing of the file and its declarations.</summary>
internal sealed partial class AstPrinter
{
    // ═══════════════════════════════════════════════════════════════════════════
    // FILE
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>The kind order declarations are printed in: module, imports, aliases, presets, module state,
    /// types, routines, and <c>start</c> last.</summary>
    private static int KindRank(ISyntaxTreeNode node)
    {
        return node switch
        {
            ModuleDeclaration => 0,
            ImportDeclaration => 1,
            DefineDeclaration => 2,
            PresetDeclaration => 3,
            VariableDeclaration => 4,
            RecordDeclaration or EntityDeclaration or ChoiceDeclaration or FlagsDeclaration or VariantDeclaration
                or CrashableDeclaration or ProtocolDeclaration => 5,
            RoutineDeclaration { Name: "start", OwnerName: null } => 7,
            RoutineDeclaration or ExternalDeclaration => 6,
            _ => 4
        };
    }

    private void PrintProgram(SyntaxTree.Program program)
    {
        PrintTargetDirectives(program: program);

        RoutineDeclaration? script = program.Declarations.OfType<RoutineDeclaration>()
                                            .FirstOrDefault(predicate: r => r.IsScriptEntry);
        List<ISyntaxTreeNode> declarations = program.Declarations
                                                    .Where(predicate: d => !ReferenceEquals(objA: d, objB: script))
                                                    .ToList();

        if (script != null)
        {
            PrintScript(declarations: declarations, script: script);
            TopLevelOrder = program.Declarations;
            return;
        }

        // Stable sort by kind, imports alphabetically within theirs. A file with a top-level `var` that is not a
        // `global` is a script of declarations (one with no other loose statement), and like any script it keeps
        // its source order after the module line and the imports, as does a file formatted with keepOrder.
        bool sourceOrder = _keepOrder ||
                           declarations.Any(predicate: d => d is VariableDeclaration { IsGlobal: false });
        List<ISyntaxTreeNode> ordered = declarations
                                       .Select(selector: (node, i) => (node, i))
                                       .OrderBy(keySelector: p => sourceOrder
                                            ? Math.Min(val1: KindRank(node: p.node), val2: 2)
                                            : KindRank(node: p.node))
                                       .ThenBy(keySelector: p => p.node is ImportDeclaration import
                                            ? ImportText(import: import)
                                            : "",
                                            comparer: StringComparer.Ordinal)
                                       .ThenBy(keySelector: p => p.i)
                                       .Select(selector: p => p.node)
                                       .ToList();
        TopLevelOrder = ordered;

        ISyntaxTreeNode? previous = null;
        foreach (ISyntaxTreeNode node in ordered)
        {
            BlankPolicy policy = previous == null || GroupsWith(previous: previous, node: node)
                ? BlankPolicy.NoneBefore
                : BlankPolicy.One;
            PrintTopLevel(node: node, policy: policy, first: previous == null);
            previous = node;
        }
    }

    /// <summary>
    /// Whether <paramref name="node"/> follows <paramref name="previous"/> with no blank line between: imports
    /// form one group with what follows them, and consecutive presets, like consecutive globals or consecutive
    /// top-level `var`s, form a group.
    /// </summary>
    private static bool GroupsWith(ISyntaxTreeNode previous, ISyntaxTreeNode node)
    {
        return previous switch
        {
            ImportDeclaration => true,
            PresetDeclaration => node is PresetDeclaration,
            VariableDeclaration { IsGlobal: true } => node is VariableDeclaration { IsGlobal: true },
            // Top-level `var`s of a script of declarations read as consecutive statements.
            VariableDeclaration { IsGlobal: false } => node is VariableDeclaration { IsGlobal: false },
            _ => false
        };
    }

    /// <summary>
    /// A script: its loose statements (the body of the <c>start</c> the parser made for them) printed at the top
    /// level in source order, between the declarations they were written among.
    /// </summary>
    private void PrintScript(List<ISyntaxTreeNode> declarations, RoutineDeclaration script)
    {
        if (script.Body is not BlockStatement body)
        {
            throw Unsupported(node: script.Body, at: script.Location);
        }

        List<Statement> statements = body.Statements;
        if (statements.Count >= 2 && statements[^1] is ReturnStatement { Value: null } synthesized &&
            synthesized.Location == statements[index: 0].Location)
        {
            statements = statements.Take(count: statements.Count - 1)
                                   .ToList();
        }

        var items = new List<(int Start, ISyntaxTreeNode Node)>();
        foreach (ISyntaxTreeNode node in declarations)
        {
            items.Add(item: (TopLevelStart(node: node), node));
        }

        foreach (Statement statement in statements)
        {
            items.Add(item: (StatementStart(statement: statement), statement));
        }

        ISyntaxTreeNode? previous = null;
        foreach ((int _, ISyntaxTreeNode node) in items.OrderBy(keySelector: i => i.Start))
        {
            BlankPolicy policy = previous switch
            {
                null => BlankPolicy.NoneBefore,
                Statement when node is Statement => BlankPolicy.Preserve,
                _ when GroupsWith(previous: previous, node: node) => BlankPolicy.NoneBefore,
                _ => BlankPolicy.One
            };
            if (node is Statement statement)
            {
                PrintStatement(statement: statement, depth: 0, first: previous == null, policy: policy);
            }
            else
            {
                PrintTopLevel(node: node, policy: policy, first: previous == null);
            }

            previous = node;
        }

        EmitBlockTrailing(depth: 0);
    }

    /// <summary>The <c>@target(...)</c> directives, kept exactly as written (the build reads them as text).</summary>
    private void PrintTargetDirectives(SyntaxTree.Program program)
    {
        int firstDeclaration = program.Declarations.Count > 0
            ? TopLevelStart(node: program.Declarations.MinBy(keySelector: n => n.Location.Position)!)
            : int.MaxValue;
        for (int i = 0; i < _index.Tokens.Count; i++)
        {
            if (!_index.IsTargetDirective(atIndex: i) || i > 0 && !SourceIndex.IsSeparator(token: _index.Tokens[index: i - 1]))
            {
                continue;
            }

            if (_index.Tokens[index: i].Position > firstDeclaration)
            {
                throw Refuse(line: _index.Tokens[index: i].Line,
                    reason: "an @target directive after the first declaration is not kept by the parser");
            }

            int open = i + 2;
            if (open >= _index.Tokens.Count || _index.Tokens[index: open].Type != TokenType.LeftParen)
            {
                throw Refuse(line: _index.Tokens[index: i].Line, reason: "an @target directive without arguments");
            }

            int close = _index.MatchingClose(openIndex: open);
            int start = _index.Tokens[index: i].Position;
            EmitPrefix(start: start, depth: 0, first: true, policy: BlankPolicy.None);
            if (!_collecting)
            {
                WriteVerbatim(text: _index.SourceSlice(first: i, last: close));
                if (_trailing.TryGetValue(key: start, value: out CommentTrivia? trailing))
                {
                    throw Refuse(line: trailing.Line,
                        reason: "a comment after an @target directive (the build reads that line as text)");
                }
            }
        }
    }

    /// <summary>The source start of a top-level node's unit.</summary>
    private int TopLevelStart(ISyntaxTreeNode node)
    {
        return node switch
        {
            Statement statement => StatementStart(statement: statement),
            SyntaxTree.Declaration declaration => DeclarationStart(keyword: declaration.Location),
            _ => throw Unsupported(node: node, at: node.Location)
        };
    }

    private void PrintTopLevel(ISyntaxTreeNode node, BlankPolicy policy, bool first)
    {
        if (node is not SyntaxTree.Declaration declaration)
        {
            throw Unsupported(node: node, at: node.Location);
        }

        PrintDeclaration(declaration: declaration, depth: 0, first: first, policy: policy);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DECLARATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    private void PrintDeclaration(SyntaxTree.Declaration declaration, int depth, bool first, BlankPolicy policy)
    {
        int prefixStart = DeclarationStart(keyword: declaration.Location);
        EmitPrefix(start: prefixStart, depth: depth, first: first, policy: policy);
        PrintAnnotations(start: prefixStart, keyword: declaration.Location, depth: depth);

        // The header's own line is a unit of its own, so a comment after it stays on it rather than moving up to
        // the annotation lines.
        int start = LineStart(location: declaration.Location);

        switch (declaration)
        {
            case ModuleDeclaration module:
                EmitHead(start: start, depth: depth, head: Doc.Text(text: "module " + module.Path));
                break;
            case ImportDeclaration import:
                EmitHead(start: start, depth: depth, head: Doc.Text(text: ImportText(import: import)));
                break;
            case DefineDeclaration define:
                EmitHead(start: start, depth: depth,
                    head: Doc.Text(text: $"define {define.OldName} as {define.NewName}"));
                break;
            case PresetDeclaration preset:
                EmitHead(start: start, depth: depth, head: Doc.Concat(
                    Doc.Text(text: (preset.IsSecret
                        ? "secret "
                        : "") + "preset " + preset.Name + ": "),
                    TypeDoc(type: preset.Type),
                    Doc.Text(text: " = "),
                    Expr(expression: preset.Value, context: ExprContext.Statement)));
                break;
            case VariableDeclaration variable:
                PrintVariableDeclaration(variable: variable, start: start, depth: depth);
                break;
            case RoutineDeclaration routine:
                PrintRoutine(routine: routine, start: start, depth: depth);
                break;
            case ExternalDeclaration external:
                PrintExternal(external: external, start: start, depth: depth);
                break;
            case RecordDeclaration record:
                PrintTypeHeader(keyword: record.IsBundle ? "bundle" : "record", declaration: record, start: start,
                    depth: depth,
                    visibility: record.Visibility, constraints: record.GenericConstraints,
                    obeys: record.Protocols, relates: record.AssociatedTypes,
                    genericParameters: record.GenericParameters);
                PrintMembers(keyword: record.Location, members: record.Members, hasPass: record.HasPassBody, depth: depth + 1);
                break;
            case EntityDeclaration entity:
                PrintTypeHeader(keyword: "entity", declaration: entity, start: start, depth: depth,
                    visibility: entity.Visibility, constraints: entity.GenericConstraints,
                    obeys: entity.Protocols, relates: entity.AssociatedTypes,
                    genericParameters: entity.GenericParameters);
                PrintMembers(keyword: entity.Location, members: entity.Members, hasPass: entity.HasPassBody, depth: depth + 1);
                break;
            case CrashableDeclaration crashable:
                PrintTypeHeader(keyword: "crashable", declaration: crashable, start: start, depth: depth,
                    visibility: crashable.Visibility, constraints: null, obeys: [], relates: null);
                PrintMembers(keyword: crashable.Location, members: crashable.Members, hasPass: crashable.Members.Count == 0, depth: depth + 1);
                break;
            case ChoiceDeclaration choice:
                PrintChoice(choice: choice, start: start, depth: depth);
                break;
            case FlagsDeclaration flags:
                PrintFlags(flags: flags, start: start, depth: depth);
                break;
            case VariantDeclaration variant:
                PrintVariant(variant: variant, start: start, depth: depth);
                break;
            case ProtocolDeclaration protocol:
                PrintProtocol(protocol: protocol, start: start, depth: depth);
                break;
            case ExpandMemberDeclaration expand:
                PrintExpandMember(expand: expand, start: start, depth: depth);
                break;
            default:
                throw Unsupported(node: declaration, at: declaration.Location);
        }
    }

    private static string VisibilityPrefix(VisibilityModifier visibility, SourceLocation at, AstPrinter printer)
    {
        return visibility switch
        {
            VisibilityModifier.Open => "",
            VisibilityModifier.Secret => "secret ",
            VisibilityModifier.Posted => "posted ",
            _ => throw printer.Refuse(at: at, reason: $"the visibility '{visibility}' has no surface spelling")
        };
    }

    private static string ImportText(ImportDeclaration import)
    {
        var text = new System.Text.StringBuilder(value: "import " + import.ModulePath);
        if (import.SpecificImports != null)
        {
            text.Append(value: ".[")
                .Append(value: string.Join(separator: ", ", values: import.SpecificImports))
                .Append(value: ']');
        }

        if (import.RealmImports != null)
        {
            foreach ((string realm, string name) in import.RealmImports)
            {
                text.Append(value: $".{realm}::{name}");
            }
        }

        if (import.Alias != null)
        {
            text.Append(value: " as " + import.Alias);
        }

        return text.ToString();
    }

    private void PrintVariableDeclaration(VariableDeclaration variable, int start, int depth)
    {
        string keyword = variable.IsGlobal
            ? "global "
            : variable.IsPreset
                ? "preset "
                : IsTypeMember(variable: variable)
                    ? ""
                    : (variable.IsLateInit
                        ? "lateinit "
                        : "") + "var ";
        var parts = new List<Doc>
        {
            Doc.Text(text: VisibilityPrefix(visibility: variable.Visibility, at: variable.Location, printer: this) +
                           keyword + variable.Name)
        };
        if (variable.Type != null)
        {
            parts.Add(item: Doc.Text(text: ": "));
            parts.Add(item: TypeDoc(type: variable.Type));
        }

        if (variable.Initializer != null)
        {
            parts.Add(item: Doc.Text(text: " = "));
            parts.Add(item: Expr(expression: variable.Initializer, context: ExprContext.Statement));
        }

        EmitHead(start: start, depth: depth, head: Doc.Concat(parts: parts));
    }

    /// <summary>True when the variable is a member variable written <c>name: Type</c> (no keyword).</summary>
    private bool IsTypeMember(VariableDeclaration variable)
    {
        // A `var` declaration is located at its keyword, a member variable at its name.
        Token at = _index.Tokens[index: _index.TokenIndexAt(position: variable.Location.Position)];
        return at.Type == TokenType.Identifier && at.Text == variable.Name;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ANNOTATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes the annotations written above a declaration, one per line (a compound <c>@[a, b]</c> becomes two
    /// lines). They are printed from the tokens: the tree keeps them as joined strings, which lose how a string
    /// argument was spelled.
    /// </summary>
    private void PrintAnnotations(int start, SourceLocation keyword, int depth)
    {
        int keywordLine = _index.LogicalLineStart(tokenIndex: _index.TokenIndexAt(position: keyword.Position));
        int i = _index.TokenIndexAt(position: start);
        while (i < keywordLine)
        {
            if (_index.Tokens[index: i].Type != TokenType.At)
            {
                i++;
                continue;
            }

            int end = _index.LogicalLineEnd(startIndex: i);
            foreach (string annotation in AnnotationsOnLine(first: i, last: end))
            {
                EmitHead(start: _index.Tokens[index: i].Position, depth: depth, head: Doc.Text(text: annotation));
            }

            i = end + 1;
        }
    }

    /// <summary>The canonical spelling of each annotation in the token range of one annotation line.</summary>
    private List<string> AnnotationsOnLine(int first, int last)
    {
        var result = new List<string>();
        int i = first;
        while (i <= last)
        {
            if (_index.Tokens[index: i].Type != TokenType.At)
            {
                throw Refuse(line: _index.Tokens[index: i].Line, reason: "an annotation line holds something else");
            }

            i++;
            if (_index.Tokens[index: i].Type == TokenType.LeftBracket)
            {
                int close = _index.MatchingClose(openIndex: i);
                i++;
                while (i < close)
                {
                    (string text, int next) = OneAnnotation(at: i, limit: close);
                    result.Add(item: "@" + text);
                    i = next;
                    if (_index.Tokens[index: i].Type == TokenType.Comma)
                    {
                        i++;
                    }
                }

                i = close + 1;
                continue;
            }

            (string single, int after) = OneAnnotation(at: i, limit: last + 1);
            result.Add(item: "@" + single);
            i = after;
        }

        return result;
    }

    /// <summary>One annotation (a name with optional arguments) starting at token <paramref name="at"/>.</summary>
    private (string Text, int Next) OneAnnotation(int at, int limit)
    {
        string name = _index.Tokens[index: at].Text;
        int i = at + 1;
        if (i >= limit || _index.Tokens[index: i].Type != TokenType.LeftParen)
        {
            return (name, i);
        }

        int close = _index.MatchingClose(openIndex: i);
        var arguments = new List<string>();
        int argument = i + 1;
        while (argument < close)
        {
            // An argument ends at a comma outside the tuples it may hold.
            int end = argument;
            int depth = 0;
            while (end < close && (depth > 0 || _index.Tokens[index: end].Type != TokenType.Comma))
            {
                depth += _index.Tokens[index: end].Type switch
                {
                    TokenType.LeftParen => 1,
                    TokenType.RightParen => -1,
                    _ => 0
                };
                end++;
            }

            arguments.Add(item: AnnotationArgument(first: argument, last: end - 1));
            argument = end + 1;
        }

        return ($"{name}({string.Join(separator: ", ", values: arguments)})", close + 1);
    }

    private string AnnotationArgument(int first, int last)
    {
        if (last >= first + 2 && _index.Tokens[index: first].Type == TokenType.Identifier &&
            _index.Tokens[index: first + 1].Type is TokenType.Colon or TokenType.Assign)
        {
            return _index.Tokens[index: first].Text + ": " + AnnotationValue(first: first + 2, last: last);
        }

        return AnnotationValue(first: first, last: last);
    }

    /// <summary>
    /// An annotation argument's value: a literal, a name, a negative number or a tuple of values, spelled with the
    /// canonical spacing (<c>(1, -2)</c>).
    /// </summary>
    private string AnnotationValue(int first, int last)
    {
        var text = new System.Text.StringBuilder();
        for (int i = first; i <= last; i++)
        {
            Token token = _index.Tokens[index: i];
            if (token.Type == TokenType.Comma)
            {
                text.Append(value: ", ");
                continue;
            }

            text.Append(value: token.Type switch
            {
                TokenType.TextLiteral => Literals.QuoteText(value: token.Text, bytes: false),
                TokenType.BytesLiteral => "b" + Literals.QuoteText(value: token.Text, bytes: true),
                _ when token.Text is "inf" or "nan" => LiteralValueText(value: token.Text, type: token.Type,
                    at: new SourceLocation(FileName: token.FileName, Line: token.Line, Column: token.Column,
                        Position: token.Position)),
                _ => token.Text
            });
        }

        return text.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ROUTINES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The index of the <c>(</c> that opens a declaration's parameter list, searching from
    /// <paramref name="from"/> and skipping brackets.
    /// </summary>
    private int ParameterListOpen(int from, SourceLocation at)
    {
        int depth = 0;
        for (int i = from; i < _index.Tokens.Count; i++)
        {
            switch (_index.Tokens[index: i].Type)
            {
                case TokenType.LeftParen when depth == 0:
                    return i;
                case TokenType.LeftParen or TokenType.LeftBracket or TokenType.SpliceOpen or TokenType.LeftBrace:
                    depth++;
                    break;
                case TokenType.RightParen or TokenType.RightBracket or TokenType.RightBrace:
                    depth--;
                    break;
                case TokenType.Newline or TokenType.Indent or TokenType.Dedent or TokenType.Eof when depth == 0:
                    throw Refuse(at: at, reason: "a routine header without a parameter list");
            }
        }

        throw Refuse(at: at, reason: "a routine header without a parameter list");
    }

    /// <summary>
    /// The generic constraints printed as <c>needs</c> clauses: all of them except those inside a header bracket
    /// the formatter keeps as written (<paramref name="kept"/>, source ranges).
    /// </summary>
    private List<GenericConstraintDeclaration> NeedsConstraints(List<GenericConstraintDeclaration>? constraints,
        List<(int From, int To)> kept, SourceLocation at)
    {
        var needs = new List<GenericConstraintDeclaration>();
        foreach (GenericConstraintDeclaration constraint in constraints ?? [])
        {
            if (constraint.Location == null)
            {
                throw Refuse(at: at, reason: "a generic constraint without a location");
            }

            if (!kept.Any(predicate: span => constraint.Location.Position >= span.From &&
                                              constraint.Location.Position < span.To))
            {
                needs.Add(item: constraint);
            }
        }

        return needs;
    }

    /// <summary>
    /// A routine's name as written from <paramref name="first"/> to <paramref name="last"/> (receiver, dots,
    /// <c>!</c>), with each generic parameter bracket reduced to its bare parameter names: their kinds and
    /// constraints move to <c>needs</c> clauses. A bracket of type arguments (<c>List[DictEntry[K, V]]</c>) stays
    /// as written, and its source range goes into <paramref name="kept"/>.
    /// </summary>
    private string RoutineNameText(int first, int last, List<(int From, int To)> kept)
    {
        var text = new System.Text.StringBuilder();
        int segment = first;
        for (int i = first; i <= last; i++)
        {
            if (_index.Tokens[index: i].Type != TokenType.LeftBracket)
            {
                continue;
            }

            int close = _index.MatchingClose(openIndex: i);
            List<string>? names = BareParameterNames(open: i, close: close);
            if (names == null)
            {
                kept.Add(item: (_index.Tokens[index: i].Position, _index.TokenEnd(tokenIndex: close)));
            }
            else
            {
                if (i > segment)
                {
                    text.Append(value: _index.Respace(first: segment, last: i - 1));
                }

                text.Append(value: "[" + string.Join(separator: ", ", values: names) + "]");
                segment = close + 1;
            }

            i = close;
        }

        if (segment <= last)
        {
            text.Append(value: _index.Respace(first: segment, last: last));
        }

        return text.ToString();
    }

    /// <summary>
    /// The parameter names of a generic parameter bracket, read the way the parser reads one (<c>[T]</c>,
    /// <c>[AnyType T]</c>, <c>[U64 N]</c>, <c>[T obeys A, B]</c>, <c>[T is S32]</c>, <c>[T in [A, B]]</c>), or null
    /// when the bracket holds something else (type arguments with brackets of their own).
    /// </summary>
    private List<string>? BareParameterNames(int open, int close)
    {
        for (int k = open + 1; k < close; k++)
        {
            if (_index.Tokens[index: k].Type == TokenType.LeftBracket &&
                _index.Tokens[index: k - 1].Type != TokenType.In)
            {
                return null;
            }
        }

        var names = new List<string>();
        int i = open + 1;
        while (true)
        {
            Token current = _index.Tokens[index: i];
            if (current.Type != TokenType.Identifier)
            {
                return null;
            }

            if (_index.Tokens[index: i + 1].Type == TokenType.Identifier)
            {
                names.Add(item: _index.Tokens[index: i + 1].Text);
                i += 2;
            }
            else
            {
                names.Add(item: current.Text);
                i++;
                switch (_index.Tokens[index: i].Type)
                {
                    case TokenType.Obeys:
                        i = SkipSimpleType(at: i + 1);
                        while (i > 0 && _index.Tokens[index: i].Type == TokenType.Comma &&
                               _index.Tokens[index: i + 1].Type != TokenType.RightBracket &&
                               !(_index.Tokens[index: i + 1].Type == TokenType.Identifier &&
                                 _index.Tokens[index: i + 2].Type is TokenType.Obeys or TokenType.Is
                                     or TokenType.In))
                        {
                            i = SkipSimpleType(at: i + 1);
                        }

                        break;
                    case TokenType.Is:
                        i = SkipSimpleType(at: i + 1);
                        break;
                    case TokenType.In:
                        if (_index.Tokens[index: i + 1].Type != TokenType.LeftBracket)
                        {
                            return null;
                        }

                        i = _index.MatchingClose(openIndex: i + 1) + 1;
                        break;
                }
            }

            if (i <= 0 || i > close)
            {
                return null;
            }

            if (_index.Tokens[index: i].Type == TokenType.Comma)
            {
                i++;
                continue;
            }

            return i == close
                ? names
                : null;
        }
    }

    /// <summary>The index after a bracket-free type (<c>Name</c>, <c>a/B</c>, <c>Name?</c>) at
    /// <paramref name="at"/>, or -1.</summary>
    private int SkipSimpleType(int at)
    {
        int i = at;
        if (_index.Tokens[index: i].Type is not (TokenType.Identifier or TokenType.MyType or TokenType.None))
        {
            return -1;
        }

        i++;
        while (_index.Tokens[index: i].Type is TokenType.Slash or TokenType.Dot or TokenType.DoubleColon &&
               _index.Tokens[index: i + 1].Type == TokenType.Identifier)
        {
            i += 2;
        }

        if (_index.Tokens[index: i].Type == TokenType.Question)
        {
            i++;
        }

        return i;
    }

    private void PrintRoutine(RoutineDeclaration routine, int start, int depth)
    {
        int keyword = _index.TokenIndexAt(position: routine.Location.Position);
        if (_index.Tokens[index: keyword].Type != TokenType.Routine)
        {
            throw Refuse(at: routine.Location, reason: "a routine whose 'routine' keyword cannot be found");
        }

        int open = ParameterListOpen(from: keyword + 1, at: routine.Location);
        string modifiers = VisibilityPrefix(visibility: routine.Visibility, at: routine.Location, printer: this) +
                           (routine.IsCommon
                               ? "common "
                               : "") + (routine.IsDangerous
                               ? "dangerous "
                               : "") + routine.Async switch
                           {
                               AsyncStatus.Suspended => "suspended ",
                               AsyncStatus.Threaded => "threaded ",
                               _ => ""
                           };
        var kept = new List<(int From, int To)>();
        Doc head = Doc.Concat(Doc.Text(text: modifiers + "routine " + RoutineNameText(first: keyword + 1,
                last: open - 1, kept: kept)),
            ParameterList(parameters: routine.Parameters, typeOnly: null, variadicTail: false),
            ReturnTypeDoc(type: routine.ReturnType));
        EmitHead(start: start, depth: depth, head: head);
        PrintNeeds(needs: NeedsConstraints(constraints: routine.GenericConstraints, kept: kept, at: routine.Location),
            depth: depth);

        if (routine.Body is not BlockStatement body)
        {
            throw Unsupported(node: routine.Body, at: routine.Location);
        }

        if (body.Statements.Count == 0 && routine.Annotations.Contains(item: "innate"))
        {
            return;
        }

        PrintBlock(statements: body.Statements, depth: depth + 1, at: routine.Location);
    }

    private void PrintExternal(ExternalDeclaration external, int start, int depth)
    {
        int realm = _index.TokenIndexAt(position: external.Location.Position);
        int open = ParameterListOpen(from: realm, at: external.Location);
        var kept = new List<(int From, int To)>();
        Doc head = Doc.Concat(Doc.Text(text: (external.IsDangerous
                ? "dangerous "
                : "") + "routine " + RoutineNameText(first: realm, last: open - 1, kept: kept)),
            ParameterList(parameters: external.Parameters, typeOnly: TypeOnlyParameters(open: open),
                variadicTail: external.IsVariadic),
            ReturnTypeDoc(type: external.ReturnType));
        EmitHead(start: start, depth: depth, head: head);
        PrintNeeds(needs: NeedsConstraints(constraints: external.GenericConstraints, kept: kept,
            at: external.Location), depth: depth);
    }

    private Doc ReturnTypeDoc(TypeExpression? type)
    {
        return type == null
            ? Doc.Empty
            : Doc.Concat(Doc.Text(text: " -> "), TypeDoc(type: type));
    }

    /// <summary>
    /// The positions of a foreign routine's parameters written as a type alone (the parser names them
    /// <c>arg0</c>, <c>arg1</c>, ...): those items of the list at <paramref name="open"/> without a colon.
    /// </summary>
    private HashSet<int> TypeOnlyParameters(int open)
    {
        var typeOnly = new HashSet<int>();
        int close = _index.MatchingClose(openIndex: open);
        int item = 0;
        int itemStart = open + 1;
        int depth = 0;
        for (int i = open + 1; i <= close; i++)
        {
            TokenType type = _index.Tokens[index: i].Type;
            if (i < close && type is TokenType.LeftParen or TokenType.LeftBracket)
            {
                depth++;
                continue;
            }

            if (i < close && type is TokenType.RightParen or TokenType.RightBracket)
            {
                depth--;
                continue;
            }

            if (i == close || depth == 0 && type == TokenType.Comma)
            {
                if (itemStart + 1 <= close && _index.Tokens[index: itemStart + 1].Type != TokenType.Colon)
                {
                    typeOnly.Add(item: item);
                }

                item++;
                itemStart = i + 1;
            }
        }

        return typeOnly;
    }

    /// <summary>A parameter list that breaks one parameter per line when it does not fit.</summary>
    private Doc ParameterList(List<Parameter> parameters, HashSet<int>? typeOnly, bool variadicTail)
    {
        var items = new List<Doc>();
        for (int i = 0; i < parameters.Count; i++)
        {
            items.Add(item: ParameterDoc(parameter: parameters[index: i],
                typeOnly: typeOnly?.Contains(item: i) == true));
        }

        if (variadicTail)
        {
            items.Add(item: Doc.Text(text: "..."));
        }

        return BracketList(open: "(", close: ")", items: items, trailingComma: !variadicTail);
    }

    private Doc ParameterDoc(Parameter parameter, bool typeOnly)
    {
        if (typeOnly && parameter.Type != null)
        {
            // A foreign parameter written as a type alone, which the parser gave a placeholder name.
            return TypeDoc(type: parameter.Type);
        }

        var parts = new List<Doc>
        {
            Doc.Text(text: parameter.Name + (parameter.IsVariadic
                ? "..."
                : ""))
        };
        if (parameter.Type != null)
        {
            parts.Add(item: Doc.Text(text: ": "));
            parts.Add(item: TypeDoc(type: parameter.Type));
        }

        if (parameter.DefaultValue != null)
        {
            parts.Add(item: Doc.Text(text: " = "));
            parts.Add(item: Expr(expression: parameter.DefaultValue, context: ExprContext.ListItem));
        }

        return Doc.Concat(parts: parts);
    }

    /// <summary>
    /// <c>open item, item close</c>, or, when it does not fit, the open bracket ending the line, each item on its
    /// own line one level in, and the close bracket alone at the opening line's indent.
    /// </summary>
    /// <param name="open">The opening bracket.</param>
    /// <param name="close">The closing bracket.</param>
    /// <param name="items">The items.</param>
    /// <param name="trailingComma">Whether the broken form ends the last item with a comma (not after a
    /// variadic <c>...</c>, which must close a foreign parameter list).</param>
    private static Doc BracketList(string open, string close, List<Doc> items, bool trailingComma = true)
    {
        if (items.Count == 0)
        {
            return Doc.Text(text: open + close);
        }

        // One item that cannot break itself gains nothing from a line of its own.
        if (items.Count == 1 && !CanBreak(doc: items[index: 0]))
        {
            return Doc.Concat(Doc.Text(text: open), items[index: 0], Doc.Text(text: close));
        }

        var inner = new List<Doc> { Doc.SoftLine };
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                inner.Add(item: Doc.Text(text: ","));
                inner.Add(item: Doc.Line);
            }

            inner.Add(item: items[index: i]);
        }

        // The broken form ends each item with a comma, the last one included. The flat form has none after the
        // last item.
        if (trailingComma)
        {
            inner.Add(item: Doc.IfBreak(broken: Doc.Text(text: ","), flat: Doc.Empty));
        }

        return Doc.Group(content: Doc.Concat(Doc.Text(text: open),
            Doc.Nest(indent: IndentWidth, content: Doc.Concat(parts: inner)),
            Doc.SoftLine,
            Doc.Text(text: close)));
    }

    /// <summary>
    /// A collection literal: flat when it fits, and otherwise the open bracket ending the line, the elements
    /// packed as many to a line as fit, one level in, a comma after the last, and the close bracket alone at the
    /// opening line's indent.
    /// </summary>
    private static Doc PackedList(string open, string close, List<Doc> items)
    {
        if (items.Count == 0)
        {
            return Doc.Text(text: open + close);
        }

        var inner = new List<Doc> { Doc.SoftLine };
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                // The next element and the comma after it (the last one's comma is printed broken only).
                inner.Add(item: Doc.FillLine(nextWidth: items[index: i]
                                                      .Flat()
                                                      .Length + 1));
            }

            inner.Add(item: items[index: i]);
            inner.Add(item: i < items.Count - 1
                ? Doc.Text(text: ",")
                : Doc.IfBreak(broken: Doc.Text(text: ","), flat: Doc.Empty));
        }

        return Doc.Group(content: Doc.Concat(Doc.Text(text: open),
            Doc.Nest(indent: IndentWidth, content: Doc.Concat(parts: inner)),
            Doc.SoftLine,
            Doc.Text(text: close)));
    }

    private static bool CanBreak(Doc doc)
    {
        return doc switch
        {
            LineDoc or FillLineDoc => true,
            ConcatDoc concat => concat.Parts.Any(predicate: CanBreak),
            NestDoc nest => CanBreak(doc: nest.Content),
            GroupDoc group => group.AlwaysBroken || CanBreak(doc: group.Content),
            IfBreakDoc choice => CanBreak(doc: choice.Broken),
            _ => false
        };
    }

    /// <summary>
    /// Writes the <c>needs</c> clauses: first one line of the kind and const-type classifiers
    /// (<c>needs EntityType A, U64 B</c>), then one line per protocol or type constraint
    /// (<c>needs A obeys P1, P2</c>), then the <c>everywhere</c> gates. A line longer than the limit continues one
    /// level in, filled.
    /// </summary>
    private void PrintNeeds(List<GenericConstraintDeclaration> needs, int depth)
    {
        List<GenericConstraintDeclaration> kinds = needs.Where(predicate: IsKindConstraint)
                                                        .ToList();
        if (kinds.Count > 0)
        {
            EmitClause(location: kinds[index: 0].Location!, depth: depth, head: ClauseDoc(prefix: "needs ",
                items: kinds.Select(selector: ConstraintDoc)
                            .ToList()));
        }

        foreach (GenericConstraintDeclaration constraint in needs.Where(predicate: c =>
                     !IsKindConstraint(constraint: c) && c.ConstraintType != ConstraintKind.Everywhere))
        {
            List<TypeExpression> types = constraint.ConstraintTypes ?? [];
            Doc clause = constraint.ConstraintType == ConstraintKind.Obeys
                ? ClauseDoc(prefix: "needs " + constraint.ParameterName + " obeys ",
                    items: types.Select(selector: TypeDoc)
                                .ToList())
                : Doc.Concat(Doc.Text(text: "needs "), ConstraintDoc(constraint: constraint));
            EmitClause(location: constraint.Location!, depth: depth, head: clause);
        }

        // Every `everywhere` gate on one line: `needs P1, P2 everywhere`.
        List<GenericConstraintDeclaration> everywhere = needs.Where(predicate: c =>
                                                                 c.ConstraintType == ConstraintKind.Everywhere)
                                                             .ToList();
        if (everywhere.Count > 0)
        {
            var protocols = new List<Doc>();
            foreach (GenericConstraintDeclaration gate in everywhere)
            {
                if (gate.ParameterName != "Me" || gate.ConstraintTypes is not [var protocol])
                {
                    throw Refuse(at: gate.Location, reason: "an everywhere constraint of this shape");
                }

                protocols.Add(item: TypeDoc(type: protocol));
            }

            EmitMergedClause(locations: everywhere.Select(selector: c => c.Location!)
                                                  .ToList(),
                depth: depth,
                head: Doc.Concat(ClauseDoc(prefix: "needs ", items: protocols), Doc.Text(text: " everywhere")));
        }
    }

    /// <summary>A kind or const-type classifier constraint (<c>EntityType A</c>, <c>U64 N</c>).</summary>
    private static bool IsKindConstraint(GenericConstraintDeclaration constraint)
    {
        return constraint.ConstraintType is not (ConstraintKind.Obeys or ConstraintKind.TypeEquality
            or ConstraintKind.Everywhere);
    }

    /// <summary>
    /// Writes one clause line that stands for several written ones (each <c>relates</c> or <c>everywhere</c>
    /// line), with the comments written before each of them, in source order.
    /// </summary>
    private void EmitMergedClause(List<SourceLocation> locations, int depth, Doc head)
    {
        List<int> starts = locations.Select(selector: LineStart)
                                    .Distinct()
                                    .Order()
                                    .ToList();
        foreach (int start in starts)
        {
            EmitPrefix(start: start, depth: depth, first: false, policy: BlankPolicy.None);
        }

        EmitHead(start: LineStart(location: locations[index: 0]), depth: depth, head: head);
    }

    /// <summary>Writes one header clause line, with the comments written before it.</summary>
    private void EmitClause(SourceLocation location, int depth, Doc head)
    {
        int start = LineStart(location: location);
        EmitPrefix(start: start, depth: depth, first: false, policy: BlankPolicy.None);
        EmitHead(start: start, depth: depth, head: head);
    }

    /// <summary>
    /// <c>prefix item, item, …</c> on one line, or, when it does not fit, the items filled onto continuation lines
    /// one level in (the lexers read a deeper line after a trailing comma as continuing the clause).
    /// </summary>
    private static Doc ClauseDoc(string prefix, List<Doc> items)
    {
        var parts = new List<Doc>();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                parts.Add(item: Doc.Text(text: ","));
                parts.Add(item: Doc.FillLine(nextWidth: items[index: i]
                                                     .Flat()
                                                     .Length + (i < items.Count - 1
                                                     ? 1
                                                     : 0)));
            }

            parts.Add(item: items[index: i]);
        }

        return Doc.Group(content: Doc.Concat(Doc.Text(text: prefix),
            Doc.Nest(indent: IndentWidth, content: Doc.Concat(parts: parts))));
    }

    private Doc ConstraintDoc(GenericConstraintDeclaration constraint)
    {
        List<TypeExpression> types = constraint.ConstraintTypes ?? [];
        switch (constraint.ConstraintType)
        {
            case ConstraintKind.Obeys:
                return Doc.Concat(Doc.Text(text: constraint.ParameterName + " obeys "), TypeList(types: types));
            case ConstraintKind.TypeEquality when types.Count == 1:
                return Doc.Concat(Doc.Text(text: constraint.ParameterName + " is "), TypeDoc(type: types[index: 0]));
            case ConstraintKind.TypeEquality:
                return Doc.Concat(Doc.Text(text: constraint.ParameterName + " in ["), TypeList(types: types),
                    Doc.Text(text: "]"));
            case ConstraintKind.Everywhere when types.Count == 1 && constraint.ParameterName == "Me":
                return Doc.Concat(TypeDoc(type: types[index: 0]), Doc.Text(text: " everywhere"));
            case ConstraintKind.ConstGeneric when types.Count == 1:
                return Doc.Concat(TypeDoc(type: types[index: 0]), Doc.Text(text: " " + constraint.ParameterName));
        }

        string? classifier = constraint.ConstraintType switch
        {
            ConstraintKind.RoutineType => "RoutineType",
            ConstraintKind.TupleType => "TupleType",
            ConstraintKind.RecordType => "RecordType",
            ConstraintKind.ChoiceType => "ChoiceType",
            ConstraintKind.FlagsType => "FlagsType",
            ConstraintKind.VariantType => "VariantType",
            ConstraintKind.EntityType => "EntityType",
            ConstraintKind.Crashable => "CrashableType",
            ConstraintKind.RedirectType => "RedirectType",
            ConstraintKind.AnyType => "AnyType",
            _ => null
        };
        if (classifier == null || types.Count > 0)
        {
            throw Refuse(at: constraint.Location, reason: $"a '{constraint.ConstraintType}' constraint of this shape");
        }

        return Doc.Text(text: classifier + " " + constraint.ParameterName);
    }

    private Doc TypeList(List<TypeExpression> types)
    {
        var parts = new List<Doc>();
        for (int i = 0; i < types.Count; i++)
        {
            if (i > 0)
            {
                parts.Add(item: Doc.Text(text: ", "));
            }

            parts.Add(item: TypeDoc(type: types[index: i]));
        }

        return Doc.Concat(parts: parts);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TYPES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes a type declaration's header line and its clause lines, each on its own line at the header's indent:
    /// each <c>relates</c>, then <c>obeys</c>, then the <c>needs</c> clauses.
    /// </summary>
    private void PrintTypeHeader(string keyword, SyntaxTree.Declaration declaration, int start, int depth,
        VisibilityModifier visibility, List<GenericConstraintDeclaration>? constraints, List<TypeExpression> obeys,
        List<AssociatedTypeDeclaration>? relates, List<string>? genericParameters = null,
        bool obeysAllowConditions = true)
    {
        // The name, with bare parameter names in its brackets: their kinds and constraints go to `needs`.
        int keywordIndex = _index.TokenIndexAt(position: declaration.Location.Position);
        string name = _index.Tokens[index: keywordIndex + 1].Text;
        if (genericParameters is { Count: > 0 })
        {
            name += "[" + string.Join(separator: ", ", values: genericParameters) + "]";
        }

        EmitHead(start: start, depth: depth, head: Doc.Text(
            text: VisibilityPrefix(visibility: visibility, at: declaration.Location, printer: this) + keyword + " " +
                  name));

        // Every associated type on one `relates` line, filled onto continuation lines past the limit.
        if (relates is { Count: > 0 })
        {
            var items = new List<Doc>();
            foreach (AssociatedTypeDeclaration associated in relates)
            {
                items.Add(item: associated switch
                {
                    { Binding: { } binding, Constraint: null } => Doc.Concat(TypeDoc(type: binding),
                        Doc.Text(text: " as " + associated.Name)),
                    { Binding: null, Constraint: { } constraint } => Doc.Concat(
                        Doc.Text(text: associated.Name + " obeys "), TypeDoc(type: constraint)),
                    { Binding: null, Constraint: null } => Doc.Text(text: associated.Name),
                    _ => throw Refuse(at: associated.Location,
                        reason: "a relates clause with both a binding and a bound")
                });
            }

            // The merged line takes the comments written before each of the lines it replaces, in order.
            EmitMergedClause(locations: relates.Select(selector: r => r.Location!)
                                               .ToList(),
                depth: depth,
                head: ClauseDoc(prefix: "relates ", items: items));
        }

        if (obeys.Count > 0)
        {
            var items = new List<Doc>();
            foreach (TypeExpression protocol in obeys)
            {
                if (protocol.ConformanceConditions is { Count: > 0 } conditions)
                {
                    if (!obeysAllowConditions)
                    {
                        throw Refuse(at: declaration.Location, reason: "an onlyif condition on a protocol's parent");
                    }

                    items.Add(item: Doc.Concat(TypeDoc(type: protocol), OnlyIfDoc(conditions: conditions)));
                }
                else
                {
                    items.Add(item: TypeDoc(type: protocol));
                }
            }

            EmitClause(location: obeys[index: 0].Location, depth: depth, head: ClauseDoc(prefix: "obeys ", items: items));
        }

        PrintNeeds(needs: NeedsConstraints(constraints: constraints, kept: [], at: declaration.Location),
            depth: depth);
    }

    private Doc OnlyIfDoc(List<GenericConstraintDeclaration> conditions)
    {
        var parts = new List<Doc> { Doc.Text(text: " onlyif ") };
        if (conditions.Count > 1)
        {
            parts.Add(item: Doc.Text(text: "("));
        }

        for (int i = 0; i < conditions.Count; i++)
        {
            GenericConstraintDeclaration condition = conditions[index: i];
            if (condition.ConstraintType != ConstraintKind.Obeys || condition.ConstraintTypes is not { Count: 1 })
            {
                throw Refuse(at: condition.Location, reason: "an onlyif condition of this shape");
            }

            if (i > 0)
            {
                parts.Add(item: Doc.Text(text: ", "));
            }

            parts.Add(item: Doc.Text(text: condition.ParameterName + " obeys "));
            parts.Add(item: TypeDoc(type: condition.ConstraintTypes[index: 0]));
        }

        if (conditions.Count > 1)
        {
            parts.Add(item: Doc.Text(text: ")"));
        }

        return Doc.Concat(parts: parts);
    }

    /// <summary>The members of a record, entity or crashable body, each in its source order.</summary>
    private void PrintMembers(SourceLocation keyword, List<SyntaxTree.Declaration> members, bool hasPass, int depth)
    {
        if (hasPass)
        {
            int passStart = BodyTokenStart(keyword: keyword, type: TokenType.Pass);
            if (passStart >= 0)
            {
                EmitPrefix(start: passStart, depth: depth, first: true, policy: BlankPolicy.Preserve);
                EmitHead(start: passStart, depth: depth, head: Doc.Text(text: "pass"));
            }
            else
            {
                WriteLine(indent: depth * IndentWidth, text: "pass");
            }
        }

        bool first = !hasPass;
        foreach (SyntaxTree.Declaration member in members)
        {
            PrintDeclaration(declaration: member, depth: depth, first: first, policy: BlankPolicy.Preserve);
            first = false;
        }

        EmitBlockTrailing(depth: depth);
    }

    private void PrintChoice(ChoiceDeclaration choice, int start, int depth)
    {
        if (choice.MemberRoutines.Count > 0)
        {
            throw Refuse(at: choice.Location, reason: "a choice with member routines in its body");
        }

        PrintTypeHeader(keyword: "choice", declaration: choice, start: start, depth: depth,
            visibility: choice.Visibility, constraints: null, obeys: [], relates: choice.AssociatedTypes);
        bool first = true;
        foreach (ChoiceCase choiceCase in choice.Cases)
        {
            int caseStart = LineStart(location: choiceCase.Location);
            EmitPrefix(start: caseStart, depth: depth + 1, first: first, policy: BlankPolicy.Preserve);
            Doc head = choiceCase.Value == null
                ? Doc.Text(text: choiceCase.Name)
                : Doc.Concat(Doc.Text(text: choiceCase.Name + ": "),
                    Expr(expression: choiceCase.Value, context: ExprContext.Statement));
            EmitHead(start: caseStart, depth: depth + 1, head: head);
            first = false;
        }

        EmitBlockTrailing(depth: depth + 1);
    }

    private void PrintFlags(FlagsDeclaration flags, int start, int depth)
    {
        PrintTypeHeader(keyword: "flags", declaration: flags, start: start, depth: depth,
            visibility: flags.Visibility, constraints: null, obeys: [], relates: null);
        List<int> memberStarts = BodyIdentifierStarts(keyword: flags.Location);
        if (memberStarts.Count != flags.Members.Count)
        {
            throw Refuse(at: flags.Location, reason: "a flags body the formatter cannot match to its members");
        }

        for (int i = 0; i < flags.Members.Count; i++)
        {
            EmitPrefix(start: memberStarts[index: i], depth: depth + 1, first: i == 0, policy: BlankPolicy.Preserve);
            EmitHead(start: memberStarts[index: i], depth: depth + 1, head: Doc.Text(text: flags.Members[index: i]));
        }

        EmitBlockTrailing(depth: depth + 1);
    }

    /// <summary>
    /// The position of the first token of <paramref name="type"/> directly in the indented body after a header
    /// keyword, or -1.
    /// </summary>
    private int BodyTokenStart(SourceLocation keyword, TokenType type)
    {
        int i = _index.TokenIndexAt(position: keyword.Position);
        while (i < _index.Tokens.Count && _index.Tokens[index: i].Type != TokenType.Indent)
        {
            if (_index.Tokens[index: i].Type == TokenType.Eof)
            {
                return -1;
            }

            i++;
        }

        int depth = 0;
        for (; i < _index.Tokens.Count; i++)
        {
            Token token = _index.Tokens[index: i];
            if (token.Type == TokenType.Indent)
            {
                depth++;
            }
            else if (token.Type == TokenType.Dedent)
            {
                depth--;
                if (depth == 0)
                {
                    return -1;
                }
            }
            else if (depth == 1 && token.Type == type)
            {
                return token.Position;
            }
        }

        return -1;
    }

    /// <summary>The positions of the identifiers directly in the indented body after a header keyword.</summary>
    private List<int> BodyIdentifierStarts(SourceLocation keyword)
    {
        var starts = new List<int>();
        int i = _index.TokenIndexAt(position: keyword.Position);
        while (i < _index.Tokens.Count && _index.Tokens[index: i].Type != TokenType.Indent)
        {
            if (_index.Tokens[index: i].Type == TokenType.Newline &&
                _index.Tokens[index: i + 1].Type != TokenType.Indent)
            {
                return starts;
            }

            i++;
        }

        int depth = 0;
        for (; i < _index.Tokens.Count; i++)
        {
            Token token = _index.Tokens[index: i];
            if (token.Type == TokenType.Indent)
            {
                depth++;
            }
            else if (token.Type == TokenType.Dedent)
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
            else if (depth == 1 && token.Type == TokenType.Identifier)
            {
                starts.Add(item: token.Position);
            }
        }

        return starts;
    }

    private void PrintVariant(VariantDeclaration variant, int start, int depth)
    {
        PrintTypeHeader(keyword: "variant", declaration: variant, start: start, depth: depth,
            visibility: VisibilityModifier.Open, constraints: variant.GenericConstraints, obeys: [], relates: null,
            genericParameters: variant.GenericParameters);
        bool first = true;
        foreach (VariantMember member in variant.Members)
        {
            int memberStart = LineStart(location: member.Location);
            EmitPrefix(start: memberStart, depth: depth + 1, first: first, policy: BlankPolicy.Preserve);
            EmitHead(start: memberStart, depth: depth + 1, head: TypeDoc(type: member.Type));
            first = false;
        }

        EmitBlockTrailing(depth: depth + 1);
    }

    private void PrintProtocol(ProtocolDeclaration protocol, int start, int depth)
    {
        PrintTypeHeader(keyword: "protocol", declaration: protocol, start: start, depth: depth,
            visibility: protocol.Visibility, constraints: protocol.GenericConstraints, obeys: protocol.ParentProtocols,
            relates: protocol.AssociatedTypes, genericParameters: protocol.GenericParameters,
            obeysAllowConditions: false);
        bool first = true;
        foreach (RoutineSignature signature in protocol.MemberRoutines)
        {
            PrintSignature(signature: signature, depth: depth + 1, first: first);
            first = false;
        }

        EmitBlockTrailing(depth: depth + 1);
    }

    private void PrintSignature(RoutineSignature signature, int depth, bool first)
    {
        int lineStart = _index.LogicalLineStart(tokenIndex: _index.TokenIndexAt(position: signature.Location.Position));
        int keyword = lineStart;
        while (keyword < _index.Tokens.Count && _index.Tokens[index: keyword].Type != TokenType.Routine)
        {
            keyword++;
        }

        int prefixStart = _index.Tokens[index: _index.DeclarationStartIndex(keywordPosition: _index.Tokens[index: keyword].Position)]
                                .Position;
        EmitPrefix(start: prefixStart, depth: depth, first: first, policy: BlankPolicy.Preserve);
        PrintAnnotations(start: prefixStart, keyword: signature.Location, depth: depth);
        int start = _index.Tokens[index: lineStart].Position;

        List<string> annotations = signature.Annotations ?? [];
        string modifiers = (annotations.Contains(item: "common")
            ? "common "
            : "") + (annotations.Contains(item: "dangerous")
            ? "dangerous "
            : "");
        int open = ParameterListOpen(from: keyword + 1, at: signature.Location);
        EmitHead(start: start, depth: depth, head: Doc.Concat(
            Doc.Text(text: modifiers + "routine " + _index.Respace(first: keyword + 1, last: open - 1)),
            ParameterList(parameters: signature.Parameters, typeOnly: null, variadicTail: false),
            ReturnTypeDoc(type: signature.ReturnType)));
    }

    private void PrintExpandMember(ExpandMemberDeclaration expand, int start, int depth)
    {
        EmitHead(start: start, depth: depth, head: Doc.Concat(
            Doc.Text(text: $"expand {expand.HandleName} in allmemvarof("),
            TypeDoc(type: expand.SourceType),
            Doc.Text(text: ")")));
        bool first = true;
        foreach (ExpandMemberTemplate template in expand.Templates)
        {
            int templateStart = LineStart(location: template.Location);
            EmitPrefix(start: templateStart, depth: depth + 1, first: first, policy: BlankPolicy.Preserve);
            string name = template.NamePrefix.Length == 0
                ? Splices.NameOf(handle: expand.HandleName)
                : "${" + Literals.QuoteText(value: template.NamePrefix, bytes: false) + " + " + expand.HandleName +
                  ".name}";
            EmitHead(start: templateStart, depth: depth + 1, head: Doc.Concat(
                Doc.Text(text: VisibilityPrefix(visibility: template.Visibility, at: template.Location, printer: this) +
                               name + ": "),
                TypeDoc(type: template.Type)));
            first = false;
        }

        EmitBlockTrailing(depth: depth + 1);
    }
}
