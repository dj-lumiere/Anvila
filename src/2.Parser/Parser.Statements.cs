using Builder.Diagnostics;
using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Parser;

/// <summary>
/// Partial class containing statement parsing (if, while, for, when, return, using, release, danger, etc.).
/// Handles both RazorForge and Suflae syntax via <c>_language</c> dispatch.
/// </summary>
public partial class Parser
{
    // ═══════════════════════════════════════════════════════════════════════════════
    // CONTROL FLOW
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses an if statement with optional elseif/else chains.
    /// Syntax: <c>if condition</c> followed by indented body
    /// </summary>
    /// <remarks>
    /// Parsing phases:
    ///
    /// PHASE 1: INITIAL IF
    ///   - Parse condition expression
    ///   - Parse body (indented block or brace block)
    ///
    /// PHASE 2: ELSEIF CHAIN (optional)
    ///   - Parse each elseif as a nested IfStatement
    ///   - Chain them together via ElseStatement
    ///
    /// PHASE 3: FINAL ELSE (optional)
    ///   - Parse else block
    ///   - Attach to end of elseif chain
    ///
    /// The elseif chain is desugared to nested if-else:
    ///   if a:       becomes    IfStatement(a, then1,
    ///       then1                  IfStatement(b, then2,
    ///   elseif b:                      else3))
    ///       then2
    ///   else:
    ///       else3
    /// </remarks>
    /// <returns>An <see cref="IfStatement"/> AST node.</returns>
    private IfStatement ParseIfStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 1: INITIAL IF (condition and then-block)
        // ═══════════════════════════════════════════════════════════════════════════
        Expression condition = ParseExpression();
        Statement thenBranch = ParseBody();

        Statement? elseBranch = null;

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: ELSEIF CHAIN (convert to nested if-else)
        // ═══════════════════════════════════════════════════════════════════════════
        while (CheckAndAdvance(type: TokenType.Elseif))
        {
            SourceLocation elseifLocation = GetLocation(token: PeekToken(offset: -1));
            Expression elseifCondition = ParseExpression();
            Statement elseifBranch = ParseBody();

            // Create nested if statement for this elseif
            var nestedIf = new IfStatement(Condition: elseifCondition,
                ThenStatement: elseifBranch,
                ElseStatement: null,
                Location: elseifLocation);

            // Attach to the chain
            if (elseBranch == null)
            {
                elseBranch = nestedIf;
            }
            else if (elseBranch is IfStatement prevIf)
            {
                // Find the end of the elseif chain and attach
                elseBranch = AttachElseBranch(root: prevIf, newBranch: nestedIf);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 3: FINAL ELSE (optional)
        // ═══════════════════════════════════════════════════════════════════════════
        if (!CheckAndAdvance(type: TokenType.Else))
        {
            return new IfStatement(Condition: condition,
                ThenStatement: thenBranch,
                ElseStatement: elseBranch,
                Location: location);
        }

        Statement finalElse = ParseBody();

        if (elseBranch == null)
        {
            elseBranch = finalElse;
        }
        else if (elseBranch is IfStatement lastIf)
        {
            // Attach final else to the end of the elseif chain
            elseBranch = AttachElseBranch(root: lastIf, newBranch: finalElse);
        }

        return new IfStatement(Condition: condition,
            ThenStatement: thenBranch,
            ElseStatement: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Helper to recursively attach an else branch to the end of an if-elseif chain.
    /// Since IfStatement is immutable, we need to rebuild the chain.
    /// </summary>
    private static IfStatement AttachElseBranch(IfStatement root, Statement newBranch)
    {
        if (root.ElseStatement == null)
        {
            return new IfStatement(Condition: root.Condition,
                ThenStatement: root.ThenStatement,
                ElseStatement: newBranch,
                Location: root.Location);
        }

        if (root.ElseStatement is IfStatement nestedIf)
        {
            return new IfStatement(Condition: root.Condition,
                ThenStatement: root.ThenStatement,
                ElseStatement: AttachElseBranch(root: nestedIf, newBranch: newBranch),
                Location: root.Location);
        }

        // Already has a non-if else branch, shouldn't happen
        return root;
    }

    /// <summary>
    /// Parses a while loop statement.
    /// Syntax: <c>while condition</c> followed by indented body
    /// An optional <c>then</c> block runs once the loop is left for the line after it.
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node.</returns>
    private WhileStatement ParseWhileStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        return ParseWhileTail(condition: condition, location: location);
    }

    /// <summary>
    /// Parses an until loop: <c>until c</c> runs its body while <c>c</c> is false, so it is <c>while not (c)</c>,
    /// marked to print back as written.
    /// </summary>
    private WhileStatement ParseUntilStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Expression condition = ParseExpression();
        WhileStatement loop = ParseWhileTail(condition: new UnaryExpression(Operator: UnaryOperator.Not,
            Operand: condition,
            Location: condition.Location), location: location);
        return loop with { IsUntil = true };
    }

    private WhileStatement ParseWhileTail(Expression condition, SourceLocation location)
    {
        List<CarriedValue>? with = ParseCarriedValues();
        Statement body = ParseCarryingBody(with: with, location: location);
        RejectLoopElse();
        return new WhileStatement(Condition: condition,
            Body: body,
            Location: location) { ThenBranch = ParseLoopThen(), With = with };
    }

    /// <summary>The carried values of a loop: a loop without `with` carries none (null). Innermost last.</summary>
    private readonly Stack<List<string>?> _carriedLoops = new();

    /// <summary>
    /// <c>with a: T = init, b: U = init2</c> after a loop's header: the values it carries, or null when there is no
    /// <c>with</c>.
    /// </summary>
    private List<CarriedValue>? ParseCarriedValues()
    {
        if (!CheckAndAdvance(type: TokenType.With))
        {
            return null;
        }

        var carried = new List<CarriedValue>();
        do
        {
            SourceLocation at = GetLocation();
            string name = Consume(type: TokenType.Identifier, errorMessage: "Expected the name of a carried value: with a: T = init").Text;
            Consume(type: TokenType.Colon, errorMessage: $"Expected ':' and the type of the carried value '{name}'");
            TypeExpression type = ParseType();
            Consume(type: TokenType.Assign, errorMessage: $"Expected '=' and the first value of '{name}'");
            Expression init = ParseExpression();
            carried.Add(item: new CarriedValue(Name: name, Type: type, Init: init, Location: at));
        } while (CheckAndAdvance(type: TokenType.Comma));

        return carried;
    }

    /// <summary>
    /// A loop's body, with its carried values known to the <c>continue</c> statements in it. A body that carries
    /// values must not run off its end: it continues with the next values, breaks, or leaves the routine.
    /// </summary>
    private BlockStatement ParseCarryingBody(List<CarriedValue>? with, SourceLocation location)
    {
        _carriedLoops.Push(item: with?.Select(selector: c => c.Name).ToList());
        BlockStatement body;
        try
        {
            body = ParseBody();
        }
        finally
        {
            _carriedLoops.Pop();
        }

        if (with != null && !LeavesRound(statement: body))
        {
            RecordError(ex: new GrammarException(code: GrammarDiagnosticCode.LoopBodyRunsOff,
                message: "This loop carries values (`with`), so its body cannot run off its end: end every path with " +
                         $"'continue with {string.Join(", ", with.Select(selector: c => c.Name + ": …"))}', 'break', or 'return'.",
                fileName: FileName,
                line: location.Line,
                column: location.Column,
                language: _language));
        }

        return body;
    }

    /// <summary>Whether every path through a statement leaves the loop round: a <c>continue</c>, <c>break</c>,
    /// <c>return</c> or <c>throw</c>, an <c>if</c> whose arms both do, a <c>when</c> whose arms all do.</summary>
    private static bool LeavesRound(Statement statement)
    {
        return statement switch
        {
            ContinueStatement or BreakStatement or ReturnStatement or ThrowStatement or VariantReturnStatement => true,
            BlockStatement { Statements.Count: > 0 } block => LeavesRound(statement: block.Statements[^1]),
            IfStatement { ElseStatement: { } otherwise } branch => LeavesRound(statement: branch.ThenStatement) &&
                                                                     LeavesRound(statement: otherwise),
            WhenStatement arms => arms.Clauses.Count > 0 && arms.Clauses.All(predicate: c => LeavesRound(statement: c.Body)),
            _ => false
        };
    }

    /// <summary>
    /// An <c>else</c> right after a loop's body is an error: loops take only <c>then</c>. Its body is still parsed,
    /// so the lines after it read as usual.
    /// </summary>
    private void RejectLoopElse()
    {
        if (!Check(type: TokenType.Else))
        {
            return;
        }

        Token word = Advance();
        RecordError(ex: new GrammarException(code: GrammarDiagnosticCode.LoopElse,
            message: "A loop has no 'else'. To do something when the loop would not run even once, test that with " +
                     "an 'if' before the loop. To do something once the loop is left, put it under 'then'.",
            fileName: FileName,
            line: word.Line,
            column: word.Column,
            language: _language));
        _ = ParseBody();
    }

    /// <summary>A loop's <c>then</c> block, or null. It runs once whenever the loop is left for the line after it:
    /// when the loop ends by itself and after a <c>break</c>, never on a <c>return</c> or <c>throw</c>.</summary>
    private Statement? ParseLoopThen()
    {
        return Check(type: TokenType.Then) && PeekToken(offset: 1).Type == TokenType.Newline
            ? AdvanceThen()
            : null;

        Statement AdvanceThen()
        {
            Advance(); // `then`
            return ParseBody();
        }
    }

    /// <summary>
    /// Parses a loop statement (infinite loop).
    /// Syntax: <c>loop</c> followed by indented body
    /// Equivalent to <c>while true</c>.
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node with true condition.</returns>
    private LoopStatement ParseLoopStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Statement body = ParseBody();
        RejectLoopElse();
        return new LoopStatement(Body: body, Location: location) { ThenBranch = ParseLoopThen() };
    }

    /// <summary>
    /// Parses a for-in loop statement.
    /// Syntax: <c>for variable in iterable</c> or <c>for (a, b) in iterable</c> followed by body.
    /// An optional <c>then</c> block runs once the loop is left for the line after it.
    /// </summary>
    /// <returns>A <see cref="EachStatement"/> AST node.</returns>
    private EachStatement ParseEachStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string? variable = null;
        DestructuringPattern? variablePattern = null;

        // Check for tuple destructuring: for (index, item) in items.enumerate()
        if (Check(type: TokenType.LeftParen))
        {
            List<DestructuringBinding> bindings = ParseDestructuringBindings();
            variablePattern = new DestructuringPattern(Bindings: bindings, Location: location);
        }
        else
        {
            variable = ConsumeIdentifier(errorMessage: "Expected variable name");
            if (Check(type: TokenType.Comma))
            {
                throw ThrowParseError(message:
                    $"Several names after 'each' go in parentheses, as a tuple: 'each ({variable}, ...) in ...'. A " +
                    "dictionary's pairs are 'each (key, value) in d.items()'.");
            }
        }

        Consume(type: TokenType.In, errorMessage: "Expected 'in' in for loop");
        Expression sequenceable = ParseExpression();
        List<CarriedValue>? with = ParseCarriedValues();
        Statement body = ParseCarryingBody(with: with, location: location);
        RejectLoopElse();

        return new EachStatement(Variable: variable,
            VariablePattern: variablePattern,
            Iterable: sequenceable,
            Body: body,
            Location: location) with { ThenBranch = ParseLoopThen(), With = with };
    }

    /// <summary>
    /// Parses a buildtime member-expansion loop.
    /// Syntax (Phase 1): <c>expand m in allmemvarof(T)</c> followed by an indented body.
    /// Unlike <c>each</c> there is no <c>else</c> clause — the loop is unrolled at monomorphization.
    /// </summary>
    /// <returns>An <see cref="ExpandStatement"/> AST node.</returns>
    private ExpandStatement ParseExpandStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string handle = ConsumeIdentifier(errorMessage: "Expected expand handle name");
        Consume(type: TokenType.In, errorMessage: "Expected 'in' in expand loop");

        // The source is an ordinary BuilderExpansion intrinsic call `<name>(T)` — the parser does NOT
        // know the intrinsic names (they are module routines, siblings of `nameof`/`typeof`, no longer
        // keywords). It reads the source NAME + TYPE syntactically; SemanticVerifier classifies the name
        // into an ExpandSourceKind (`allmemvarof`/`openmemvarof`/`caseof`) and gates it on
        // `import BuilderExpansion`.
        string sourceName =
            ConsumeIdentifier(
                errorMessage:
                "Expected an expand source (e.g. 'allmemvarof', 'openmemvarof', 'caseof') after 'in'");
        Consume(type: TokenType.LeftParen, errorMessage: $"Expected '(' after '{sourceName}'");
        TypeExpression sourceType = ParseType();
        Consume(type: TokenType.RightParen,
            errorMessage: $"Expected ')' after {sourceName}(...) type");

        Statement body = ParseBody();

        return new ExpandStatement(HandleName: handle,
            SourceName: sourceName,
            SourceType: sourceType,
            Body: body,
            Location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // WHEN (PATTERN MATCHING)
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a when (pattern matching) statement.
    /// Mandatory <c>=&gt;</c> on all arms.
    /// Both languages use indentation-based arms.
    /// </summary>
    /// <remarks>
    /// Two forms of when statements:
    /// 1. Subject-based: when expr { is Type =&gt; ..., LITERAL =&gt; ..., else =&gt; ... }
    /// 2. Condition-based (RF only): when { condition1 =&gt; ..., condition2 =&gt; ..., else =&gt; ... }
    ///
    /// Pattern types supported:
    /// - 'else' / 'else varName' - default case (wildcard or binding)
    /// - '_' - explicit wildcard
    /// - 'is Type' / 'is Type varName' - type pattern with optional binding
    /// - 'is Type (field1, field2)' - destructuring pattern
    /// - 'isnot Type' - negated type pattern
    /// - comparison operators (==, !=, &lt;, &gt;, &lt;=, &gt;=)
    /// - literal values (42, "hello", true)
    /// - expression patterns (for condition-based when)
    /// </remarks>
    /// <returns>A <see cref="WhenStatement"/> AST node.</returns>
    private WhenStatement ParseWhenStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 1: Determine when form (subject-based vs condition-based)
        // ═══════════════════════════════════════════════════════════════════════════
        // Subject-based:    when value { is Type => ... }     (RF)
        //                   when value\n    is Type => ...     (SF)
        // Condition-based:  when { x > 0 => ... }             (RF only)
        //                   when true { x > 0 => ... }        (RF only)
        // ═══════════════════════════════════════════════════════════════════════════

        Expression expression = ParseWhenStatementSubject(location: location,
            isConditionBased: out bool isConditionBased);

        // Indentation-delimited when block for both languages
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after when expression");

        if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected indented block after when");
        }

        ProcessIndentToken();

        // ═══════════════════════════════════════════════════════════════════════════
        // PHASE 2: Parse clauses (pattern => body)
        // ═══════════════════════════════════════════════════════════════════════════

        var clauses = new List<WhenClause>();
        WhenArmExpansion? armExpansion = null;

        bool AtClauseEnd()
        {
            return Check(type: TokenType.Dedent) || IsAtEnd;
        }

        while (!AtClauseEnd())
        {
            // Skip newlines and doc comments between clauses
            if (CheckAndAdvance(TokenType.Newline, TokenType.DocComment))
            {
                continue;
            }

            // Buildtime arm-expansion: `expand m in branchof(T)` generates one type-dispatch clause per
            // variant arm at monomorphization. It may sit ALONGSIDE explicit clauses (e.g. an
            // `is None => …` clause the payload-arm expansion doesn't cover) — at most one per `when`.
            if (Check(type: TokenType.Expand))
            {
                if (armExpansion != null)
                {
                    throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                        message: "A 'when' may contain at most one 'expand' arm-expansion.");
                }

                armExpansion = ParseWhenArmExpansion();
                continue;
            }

            SourceLocation clauseLocation = GetLocation();
            Pattern pattern = ParseWhenStatementPattern(isConditionBased: isConditionBased,
                clauseLocation: clauseLocation);

            // ─────────────────────────────────────────────────────────────────────
            // Arm body: either `=> expression` (single-line) or indented block
            // ─────────────────────────────────────────────────────────────────────

            Statement body = ParseWhenClauseBody();

            clauses.Add(
                item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

            // Optional comma or newline between clauses
            CheckAndAdvance(TokenType.Comma, TokenType.Newline);
        }

        // Close the when block (indentation-based)
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedent,
                message: "Expected dedent after when clauses");
        }

        return new WhenStatement(Expression: expression,
            Clauses: clauses,
            Location: location,
            ArmExpansion: armExpansion);
    }

    /// <summary>
    /// Parses the subject of a when-STATEMENT, distinguishing the condition-based forms
    /// (<c>when</c>-newline and <c>when true</c>-newline, both yielding a <c>true</c> literal subject)
    /// from a subject-based expression. Reports which form via <paramref name="isConditionBased"/>.
    /// </summary>
    private Expression ParseWhenStatementSubject(SourceLocation location,
        out bool isConditionBased)
    {
        // Check for condition-based forms:
        // 1. `when true\n` - explicit condition-based
        // 2. `when\n` - bare when (no subject) = condition-based
        if (Check(type: TokenType.Newline))
        {
            // Bare `when` followed by newline — condition-based with no subject
            isConditionBased = true;
            return new LiteralExpression(Value: true,
                LiteralType: TokenType.True,
                Location: location);
        }

        if (Check(type: TokenType.True))
        {
            // Peek ahead to see if this is `when true\n  INDENT` (condition-based)
            // vs `when true_var\n  INDENT` (subject-based with a variable named starting with true)
            Token nextToken = PeekToken(offset: 1);
            if (nextToken.Type == TokenType.Newline)
            {
                isConditionBased = true;
                Advance(); // consume 'true'
                return new LiteralExpression(Value: true,
                    LiteralType: TokenType.True,
                    Location: location);
            }
        }

        isConditionBased = false;
        return ParseExpression();
    }

    /// <summary>
    /// Parses a single when-STATEMENT clause pattern, dispatching (in order) to: <c>else</c>/
    /// <c>else name</c>, a condition-based expression pattern, an <c>is</c> flags/type pattern, an
    /// <c>isnot</c> negated type pattern, a comparison pattern, or a general pattern.
    /// </summary>
    private Pattern ParseWhenStatementPattern(bool isConditionBased, SourceLocation clauseLocation)
    {
        // Pattern dispatch: determine which pattern type we're parsing.
        // Order matters — check specific patterns before general ones.

        // Case 1: 'else' keyword - default/fallback case
        if (CheckAndAdvance(type: TokenType.Else))
        {
            return ParseElseStatementPattern(clauseLocation: clauseLocation);
        }

        // Case 2: Condition-based when (RF only) - parse full expression as pattern
        if (isConditionBased)
        {
            return ParseConditionBasedPattern(clauseLocation: clauseLocation);
        }

        // Cases 3+4: 'is' / 'isnot' keyword — type or flags pattern
        if (CheckAndAdvance(type: TokenType.Is))
        {
            // `is (1, 2)`: a tuple arm takes no `is`.
            int saved = _position;
            if (Check(type: TokenType.LeftParen) &&
                TryParseTuplePattern(location: clauseLocation) is { } tuple && HoldsLiteral(pattern: tuple))
            {
                // Reported, and the arm is kept as the tuple arm it means so the rest of the `when` still parses.
                RecordError(ex: ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                    message: "A tuple arm is written without 'is': '(1, 2) => ...'. 'is' is followed by a type.",
                    token: _tokens[index: saved]));
                return TryParseAndGuard(innerPattern: tuple, guardAllowed: true, location: clauseLocation);
            }

            _position = saved;
            return ParseIsWhenPattern();
        }

        if (CheckAndAdvance(type: TokenType.IsNot))
        {
            return ParseIsNotWhenPattern(clauseLocation: clauseLocation);
        }

        // Case 5: Comparison patterns (==, !=, <, >, <=, >=)
        if (IsComparisonOperator(tokenType: CurrentToken.Type))
        {
            return ParseComparisonPattern();
        }

        // Case 6: Other patterns (wildcards, literals, identifiers).
        // Set context flag to prevent single-param lambdas from being parsed
        // inside when patterns (e.g., "a < b => action" should not treat "b => action" as a lambda).
        _inWhenPatternContext = true;
        Pattern generalPattern = ParsePattern();
        _inWhenPatternContext = false;
        return generalPattern;
    }

    /// <summary>
    /// Parses a full expression as an <see cref="ExpressionPattern"/> inside a condition-based
    /// <c>when</c> block (RF only), with the <c>_inWhenConditionContext</c> flag set for the duration
    /// so the expression parser handles <c>=&gt;</c> correctly.
    /// </summary>
    private ExpressionPattern ParseConditionBasedPattern(SourceLocation clauseLocation)
    {
        bool savedConditionContext = _inWhenConditionContext;
        _inWhenConditionContext = true;
        Expression condExpr;
        try
        {
            condExpr = ParseExpression();
        }
        finally
        {
            _inWhenConditionContext = savedConditionContext;
        }

        return new ExpressionPattern(Expression: condExpr, Location: clauseLocation);
    }

    /// <summary>
    /// Parses a when-STATEMENT <c>else</c> clause pattern after the <c>else</c> keyword has been
    /// consumed, with an optional binding (<c>else as name</c>).
    /// </summary>
    private ElsePattern ParseElseStatementPattern(SourceLocation clauseLocation)
    {
        // A name for the value: `else as name => …` or `else as name` with an indented body.
        if (CheckAndAdvance(type: TokenType.As))
        {
            string varName = ConsumeIdentifier(errorMessage: "Expected a name after 'else as'");
            return new ElsePattern(VariableName: varName, Location: clauseLocation);
        }

        if (Check(type: TokenType.Identifier) && PeekToken(offset: 1).Type is TokenType.FatArrow or TokenType.Newline)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                message: $"A name for the value 'else' catches goes after 'as': write 'else as {CurrentToken.Text}'.");
        }

        // Plain else without variable binding
        return new ElsePattern(VariableName: null, Location: clauseLocation);
    }

    /// <summary>
    /// Parses a when-clause body: <c>=> statement</c>, <c>=> pass</c>, or <c>=>\n INDENT block</c>.
    /// </summary>
    private Statement ParseWhenClauseBody()
    {
        Statement body;
        bool savedInWhenClauseBody = _inWhenClauseBody;
        _inWhenClauseBody = true;
        // `1, 2 => ...`: several values in one arm are listed after `==`. Say so, skip to the arm's `=>`, and go on
        // with its body, so the rest of the `when` still parses.
        if (Check(type: TokenType.Comma))
        {
            var ex = new GrammarException(code: GrammarDiagnosticCode.ExpectedFatArrow,
                message: "One arm takes several values with '==' in front of them: '== 1, 2 => ...'. " +
                         "Without it, the arm is the one value before the comma.",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language);
            RecordError(ex: ex);
            while (!Check(type: TokenType.FatArrow) && !Check(type: TokenType.Newline) && !IsAtEnd)
            {
                Advance();
            }
        }

        if (CheckAndAdvance(type: TokenType.FatArrow))
        {
            // After =>, check for block form: => \n INDENT block DEDENT
            if (Check(type: TokenType.Newline) && PeekToken(offset: 1)
                   .Type == TokenType.Indent)
            {
                Advance(); // consume newline
                // An indented block holds ordinary statements, so `is`/`have`/`lack` work in it as anywhere
                // else (`if d have name`). Only the single-line clause form restricts them.
                _inWhenClauseBody = false;
                body = ParseIndentedBlock();
            }
            else if (CheckAndAdvance(type: TokenType.Pass))
            {
                body = new PassStatement(Location: GetLocation());
            }
            else
            {
                body = ParseStatement();
            }
        }
        else
        {
            Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' after when pattern");
            body = ParseStatement();
        }

        _inWhenClauseBody = savedInWhenClauseBody;
        return body;
    }

    /// <summary>
    /// Parses a buildtime arm-expansion inside a <c>when</c>:
    /// <c>expand m in branchof(T)</c> then an indented template clause <c>is ${m.type} x => body</c>
    /// (or payload-less <c>is ${m.type} => body</c>). Unrolled per variant arm at monomorphization.
    /// </summary>
    private WhenArmExpansion ParseWhenArmExpansion()
    {
        Consume(type: TokenType.Expand, errorMessage: "Expected 'expand'");
        string handle = ConsumeIdentifier(errorMessage: "Expected expand handle name");
        Consume(type: TokenType.In, errorMessage: "Expected 'in' in expand");
        // The source is read as a plain identifier (the BuilderExpansion intrinsics are not keywords), but a
        // when-expand only walks a variant's arms, so the name must be `branchof`. The tree does not keep it, so
        // anything else would be silently read as `branchof`.
        ExpectExpandSource(expected: "branchof", where: "an arm expansion inside a 'when'");
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after 'branchof'");
        TypeExpression sourceType = ParseType();
        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after branchof type");
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after branchof(...)");

        if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected indented 'is ${...} => ...' clause after branchof(...)");
        }

        ProcessIndentToken();
        while (CheckAndAdvance(TokenType.Newline, TokenType.DocComment))
        {
            /* skip interleaved blank lines and doc comments */
        }

        SourceLocation clauseLoc = GetLocation();
        Consume(type: TokenType.Is,
            errorMessage: "Expected 'is $typeof(m) ...' clause in an branchof-expand");
        // The arm pattern's type is the handle's arm type — the brace-less `$typeof(m)` splice (or the
        // legacy `${m.type}`); parsed and validated, then the optional payload binding follows.
        ParseAndValidateArmExpansionSplice(handle: handle);

        string? binding = Check(type: TokenType.Identifier)
            ? ThrowOldPatternBinding(typeName: "$typeof(...)")
            : CheckAndAdvance(type: TokenType.As)
                ? ConsumeIdentifier(errorMessage: "Expected a name after 'as'")
            : null;
        var pattern = new SpliceTypePattern(HandleName: handle,
            VariableName: binding,
            Location: clauseLoc);

        Statement body = ParseWhenClauseBody();
        CheckAndAdvance(TokenType.Comma, TokenType.Newline);

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }

        var template = new WhenClause(Pattern: pattern, Body: body, Location: clauseLoc);
        return new WhenArmExpansion(HandleName: handle,
            SourceType: sourceType,
            Template: template);
    }

    /// <summary>
    /// Parses and validates the arm-expansion type splice after <c>is</c>: either the brace-less
    /// <c>$typeof(m)</c> form or the legacy <c>${m.type}</c> form. Both must reference the given
    /// <paramref name="handle"/>; otherwise an <c>InvalidPattern</c> error is raised.
    /// </summary>
    private void ParseAndValidateArmExpansionSplice(string handle)
    {
        if (CheckAndAdvance(type: TokenType.Dollar))
        {
            SpliceExpression splice = ParseDollarSplice(kind: SpliceKind.Value);
            if (splice.Inner is not CallExpression
                {
                    Callee: IdentifierExpression { Name: "typeof" },
                    Arguments: [IdentifierExpression spliceHandleNew]
                } || spliceHandleNew.Name != handle)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                    message:
                    $"An branchof-expand arm pattern must be 'is $typeof({handle}) ...'.");
            }
        }
        else
        {
            Consume(type: TokenType.SpliceOpen,
                errorMessage: "Expected '$typeof(m)' type splice after 'is'");
            SpliceExpression splice = ParseSplice(kind: SpliceKind.Value);
            if (splice.Inner is not MemberExpression
                {
                    Object: IdentifierExpression spliceHandle, MemberName: "type"
                } || spliceHandle.Name != handle)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                    message:
                    $"An branchof-expand arm pattern must be 'is $typeof({handle}) ...'.");
            }
        }
    }


    /// <summary>
    /// Parses a pattern for use in when clauses.
    /// Supports: wildcard (_), type patterns (Type varName), variant patterns (Type.CASE or CASE),
    /// destructuring patterns (CASE (a, b)), literal patterns, guard patterns (n if n &lt; 0).
    /// </summary>
    /// <returns>A <see cref="Pattern"/> AST node.</returns>
    private Pattern ParsePattern()
    {
        SourceLocation location = GetLocation();

        // Wildcard pattern: _
        if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
        {
            Advance(); // consume the '_'
            Pattern wildcardPattern = new WildcardPattern(Location: location);
            return TryParseAndGuard(innerPattern: wildcardPattern,
                guardAllowed: true,
                location: location);
        }

        // Type/Variant pattern: Type, Type varName, Choice.CASE, Variant.CASE varName, CASE, CASE (a, b)
        if (Check(type: TokenType.Identifier) && CurrentToken.Text.Length > 0)
        {
            return ParseTypeOrVariantPattern(location: location);
        }

        // Tuple pattern: `(0, 0) => …`, `(x, y) => …`. Without this, `(x, y) => …` reads as a lambda and
        // `(0, 0)` as a tuple value compared whole.
        if (Check(type: TokenType.LeftParen) && TryParseTuplePattern(location: location) is { } tuple)
        {
            return TryParseAndGuard(innerPattern: tuple, guardAllowed: true, location: location);
        }

        // Literal pattern: constants like 42, "hello", true, etc. No flags collision → guard allowed.
        Expression expr = ParsePrimary();
        if (expr is LiteralExpression literal)
        {
            Pattern litPattern = new LiteralPattern(Value: literal.Value,
                LiteralType: literal.LiteralType,
                Location: location);
            return TryParseAndGuard(innerPattern: litPattern,
                guardAllowed: true,
                location: location);
        }

        // Otherwise, treat as expression pattern
        return new ExpressionPattern(Expression: expr, Location: location);
    }

    /// <summary>
    /// Tries to parse a tuple pattern in a <c>when</c> arm: parenthesized elements, each a binding name, <c>_</c>,
    /// a literal or a nested tuple pattern, with at least two elements (or one and a comma), followed by the arm's
    /// <c>=&gt;</c> or an <c>and</c> guard. Each element matches the tuple member at its position. Returns null,
    /// with the position restored, when the parentheses hold something else (a parenthesized value pattern).
    /// </summary>
    private DestructuringPattern? TryParseTuplePattern(SourceLocation location)
    {
        int saved = _position;
        try
        {
            DestructuringPattern pattern = ParseTuplePatternElements(location: location);
            if (Check(type: TokenType.FatArrow) || Check(type: TokenType.And))
            {
                return pattern;
            }
        }
        catch (GrammarException)
        {
            // Not a tuple pattern: fall back to a value pattern.
        }

        _position = saved;
        return null;
    }

    /// <summary>True when a tuple pattern compares a member with a literal, at any depth.</summary>
    private static bool HoldsLiteral(DestructuringPattern pattern)
    {
        return pattern.Bindings.Any(predicate: b => b.NestedPattern is LiteralPattern ||
                                                    b.NestedPattern is DestructuringPattern nested &&
                                                    HoldsLiteral(pattern: nested));
    }

    private DestructuringPattern ParseTuplePatternElements(SourceLocation location)
    {
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' for a tuple pattern");
        var bindings = new List<DestructuringBinding>();
        bool trailingComma = false;
        do
        {
            if (Check(type: TokenType.RightParen))
            {
                trailingComma = true;
                break;
            }

            SourceLocation elementLocation = GetLocation();
            if (Check(type: TokenType.LeftParen))
            {
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: null,
                    NestedPattern: ParseTuplePatternElements(location: elementLocation),
                    Location: elementLocation));
            }
            else if (Check(type: TokenType.Identifier) &&
                     PeekToken(offset: 1)
                        .Type is TokenType.Comma or TokenType.RightParen)
            {
                string name = Advance()
                   .Text;
                bindings.Add(item: name == "_"
                    ? new DestructuringBinding(MemberVariableName: null,
                        BindingName: "_",
                        NestedPattern: null,
                        Location: elementLocation)
                    : new DestructuringBinding(MemberVariableName: name,
                        BindingName: name,
                        NestedPattern: null,
                        Location: elementLocation));
            }
            else if (ParseUnary() is LiteralExpression literal)
            {
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: null,
                    NestedPattern: new LiteralPattern(Value: literal.Value,
                        LiteralType: literal.LiteralType,
                        Location: elementLocation),
                    Location: elementLocation));
            }
            else
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                    message: "A tuple pattern element is a name, '_', a literal or a nested tuple pattern.");
            }
        } while (CheckAndAdvance(type: TokenType.Comma));

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after a tuple pattern");
        if (bindings.Count < 2 && !trailingComma)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                message: "A tuple pattern has at least two elements.");
        }

        return new DestructuringPattern(Bindings: bindings, Location: location);
    }

    /// <summary>
    /// Parses a bare type/variant pattern that starts with an identifier (not the <c>is</c>-prefixed
    /// form): <c>Type</c>, <c>Type varName</c>, <c>Choice.CASE</c>, <c>Variant.CASE varName</c>,
    /// <c>CASE</c>, or <c>CASE (a, b)</c>, with an optional trailing <c>and</c>-guard.
    /// </summary>
    private Pattern ParseTypeOrVariantPattern(SourceLocation location)
    {
        string name = CurrentToken.Text;

        // A bare lowercase name followed by a guard or the arrow (`n and 0 <= n => …`, `n => …`) is an untyped
        // binding, which RazorForge and Suflae do not have: it would read as an unknown type. Type and case names
        // are capitalized, so this shape is never a valid pattern.
        if (name.Length > 0 && char.IsLower(c: name[index: 0]) &&
            PeekToken(offset: 1)
               .Type is TokenType.And or TokenType.If or TokenType.FatArrow)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                message: $"This arm binds '{name}' without a type, and a 'when' arm needs one. Write " +
                         $"'is <Type> as {name} and …' to bind a value of that type, or 'else as {name} =>' to catch " +
                         "everything else.");
        }

        Advance();

        // Check for qualified name: Type.CASE or Type.CASE.SubCase
        name = ReadQualifiedPatternName(head: name);

        // Check for destructuring: Type.CASE (memberVar1, memberVar2), (memberVar: alias), or ((x, y), z)
        List<DestructuringBinding>? bindings = null;
        if (CheckAndAdvance(type: TokenType.LeftParen))
        {
            bindings = ParseDestructuringBindingList();
            Consume(type: TokenType.RightParen,
                errorMessage: "Expected ')' after destructuring bindings");
        }

        // The value it matched takes a name after `as`, after its member variables too: `is Monster(hp: 0) as m`.
        string? variableName = ParsePatternBindingName(typeName: name);

        TypeExpression type = new(Name: name, GenericArguments: null, Location: location);
        Pattern typePattern = new TypePattern(Type: type,
            VariableName: variableName,
            Bindings: bindings,
            Location: location);
        return TryParseAndGuard(innerPattern: typePattern,
            guardAllowed: variableName != null || bindings != null,
            location: location);
    }

    /// <summary>
    /// Reads the name a type pattern binds: <c>as name</c> after the type, or nothing. A bare name right after the
    /// type (<c>is Weapon w</c>) is the old spelling and is rejected with the fix.
    /// </summary>
    private string? ParsePatternBindingName(string typeName)
    {
        if (CheckAndAdvance(type: TokenType.As))
        {
            return ConsumeIdentifier(errorMessage: "Expected a name after 'as'");
        }

        return Check(type: TokenType.Identifier) ? ThrowOldPatternBinding(typeName: typeName) : null;
    }

    /// <summary>Rejects a name written straight after a pattern's type (<c>is Weapon w</c>), naming the fix.</summary>
    private string ThrowOldPatternBinding(string typeName)
    {
        string name = CurrentToken.Text;
        throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
            message: $"A name for the matched value goes after 'as': write 'is {typeName} as {name}'.");
    }

    /// <summary>
    /// Reads a dot-qualified pattern name (<c>Type.CASE</c>, <c>Type.CASE.SubCase</c>) starting from an
    /// already-consumed <paramref name="head"/> identifier, appending each <c>.identifier</c> segment.
    /// </summary>
    private string ReadQualifiedPatternName(string head)
    {
        var nameSb = new System.Text.StringBuilder(value: head);
        while (CheckAndAdvance(type: TokenType.Dot))
        {
            if (CheckAndAdvance(type: TokenType.Identifier))
            {
                nameSb.Append(value: '.');
                nameSb.Append(value: PeekToken(offset: -1)
                   .Text);
            }
            else
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDotInQualifiedPattern,
                    message: "Expected identifier after '.' in pattern");
            }
        }

        return nameSb.ToString();
    }

    /// <summary>
    /// Parses a type pattern (used after 'is' keyword).
    /// Syntax: Type, Type varName, Type.CASE, Type.CASE varName, CASE (a, b)
    /// Also handles special keywords like 'none'.
    /// </summary>
    /// <returns>A <see cref="TypePattern"/> AST node.</returns>
    private Pattern ParseTypePattern()
    {
        SourceLocation location = GetLocation();

        // Handle 'is None' as a special case - None is a keyword
        if (CheckAndAdvance(type: TokenType.None))
        {
            return ParseNoneTypePattern(location: location);
        }

        // A tuple type: `is (S64, S64) as pair`.
        if (Check(type: TokenType.LeftParen))
        {
            TypeExpression tupleType = ParseType();
            string? tupleName = ParsePatternBindingName(typeName: tupleType.Name);
            return TryParseAndGuard(innerPattern: new TypePattern(Type: tupleType,
                    VariableName: tupleName,
                    Bindings: null,
                    Location: location),
                guardAllowed: tupleName != null,
                location: location);
        }

        if (!Check(type: TokenType.Identifier))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedPattern,
                message: $"Expected type name after 'is', got {CurrentToken.Type}");
        }

        string name = CurrentToken.Text;
        Advance();

        // A type of a named realm: `is RF::System.List[SerialValue] items` in a Suflae file.
        string? realm = ReadRealmQualifier(name: ref name);

        // Check for qualified name: Type.CASE or Type.CASE.SubCase
        if (realm == null)
        {
            name = ReadQualifiedPatternName(head: name);
        }

        // Generic-instance arm: `is Dict[Text, SerialValue] inner` / `is List[SerialValue] xs`.
        // Parse the type arguments so the pattern resolves to the concrete instance (and the binding
        // gets that full type) — mirrors ParseBaseType's `[...]` handling. Without this, a generic
        // variant arm can't be matched in `when` at all (parser stops at '[').
        List<TypeExpression>? genericArguments = null;
        if (CheckAndAdvance(type: TokenType.LeftBracket))
        {
            genericArguments = new List<TypeExpression>();
            do
            {
                genericArguments.Add(item: ParseTypeOrConstGeneric());
            } while (CheckAndAdvance(type: TokenType.Comma));

            Consume(type: TokenType.RightBracket,
                errorMessage: "Expected ']' after type arguments in pattern");
        }

        // Check for destructuring: Type.CASE (memberVar1, memberVar2), (memberVar: alias), or ((x, y), z)
        List<DestructuringBinding>? bindings = null;
        if (CheckAndAdvance(type: TokenType.LeftParen))
        {
            bindings = ParseDestructuringBindingList();
            Consume(type: TokenType.RightParen,
                errorMessage: "Expected ')' after destructuring bindings");
        }

        // The value it matched takes a name after `as`, after its member variables too: `is Monster(hp: 0) as m`.
        string? variableName = ParsePatternBindingName(typeName: name);

        var type = new TypeExpression(Name: name,
            GenericArguments: genericArguments,
            Location: location,
            Realm: realm);
        Pattern typePattern = new TypePattern(Type: type,
            VariableName: variableName,
            Bindings: bindings,
            Location: location);
        return TryParseAndGuard(innerPattern: typePattern,
            guardAllowed: variableName != null || bindings != null,
            location: location);
    }

    /// <summary>
    /// Parses the <c>is None</c> type pattern after the <c>None</c> keyword has been consumed. <c>None</c>
    /// carries no payload, so a trailing binding (<c>is None x</c>) or destructuring (<c>is None (x, y)</c>)
    /// is rejected; it takes no <c>and</c>-guard either.
    /// </summary>
    private TypePattern ParseNoneTypePattern(SourceLocation location)
    {
        var noneType =
            new TypeExpression(Name: "None", GenericArguments: null, Location: location);
        // `None` carries no payload, so it binds nothing: reject a binding (`is None x`) or a
        // destructuring (`is None (x, y)`) after it.
        if (Check(type: TokenType.Identifier) && !IsKeywordToken(token: CurrentToken) ||
            Check(type: TokenType.LeftParen))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                message:
                "The 'None' pattern binds no value — remove the binding or destructuring after 'None'.");
        }

        // `is None` binds nothing, so it takes no `and`-guard (guards require a binding).
        return new TypePattern(Type: noneType,
            VariableName: null,
            Bindings: null,
            Location: location);
    }

    /// <summary>
    /// Tries to parse an <c>and</c>-guard after a pattern: <c>is T n and &lt;guard&gt;</c>,
    /// <c>== 0 and y &gt; 0</c>. The guard is any boolean expression and may reference the pattern's
    /// binding (if any) or outer variables. <paramref name="guardAllowed"/> gates whether a guard is
    /// syntactically unambiguous here: a bare type pattern (<c>is T</c> with no binding) is NOT
    /// allowed a guard because <c>is T and …</c> collides with a flags-test chain
    /// (<c>is FLAG_A and FLAG_B</c>); a bound type pattern, comparison, literal, or wildcard has no
    /// such collision. <c>is None</c> takes no guard.
    /// </summary>
    private Pattern TryParseAndGuard(Pattern innerPattern, bool guardAllowed,
        SourceLocation location)
    {
        if (!guardAllowed || !CheckAndAdvance(type: TokenType.And))
        {
            return innerPattern;
        }

        // Reset _inWhenClauseBody so ParseEquality/ParseComparison parse the full guard expression
        // (needed for nested whens, where the clause-body flag would otherwise stop the parse early).
        bool savedInWhenClauseBody = _inWhenClauseBody;
        _inWhenClauseBody = false;
        Expression guard = ParseExpression();
        _inWhenClauseBody = savedInWhenClauseBody;
        return new GuardPattern(InnerPattern: innerPattern, Guard: guard, Location: location);
    }

    /// <summary>
    /// Checks if the given token type is a comparison operator used in when patterns.
    /// Supported operators: ==, !=, &lt;, &gt;, &lt;=, &gt;=
    /// </summary>
    /// <param name="tokenType">The token type to check.</param>
    /// <returns>True if the token is a comparison operator for patterns.</returns>
    private static bool IsComparisonOperator(TokenType tokenType)
    {
        return tokenType is TokenType.Equal or TokenType.NotEqual or TokenType.Less
            or TokenType.Greater or TokenType.LessEqual or TokenType.GreaterEqual;
    }

    /// <summary>
    /// Parses a comparison pattern in a when clause.
    /// Syntax: <c>== value</c>, <c>!= value</c>, <c>&lt; value</c>, <c>&gt; value</c>,
    /// <c>&lt;= value</c>, <c>&gt;= value</c>
    /// </summary>
    /// <returns>A <see cref="ComparisonPattern"/> or <see cref="GuardPattern"/> AST node.</returns>
    private Pattern ParseComparisonPattern()
    {
        SourceLocation location = GetLocation();
        TokenType op = CurrentToken.Type;
        Advance(); // consume the operator

        // Parse the value to compare against
        // Set context flag to prevent lambda parsing
        _inWhenPatternContext = true;
        Expression ParseValue()
        {
            Expression value = ParsePrimary();

            // Allow member access and calls on the primary expression (e.g., Status.ACTIVE, get_user())
            while (Check(type: TokenType.Dot) || Check(type: TokenType.LeftParen))
            {
                if (CheckAndAdvance(type: TokenType.Dot))
                {
                    string memberName =
                        ConsumeIdentifier(errorMessage: "Expected member name after '.'");
                    value = new MemberExpression(Object: value,
                        MemberName: memberName,
                        Location: GetLocation());
                }
                else if (CheckAndAdvance(type: TokenType.LeftParen))
                {
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");
                    value = new CallExpression(Callee: value,
                        Arguments: args,
                        Location: GetLocation());
                }
            }

            return value;
        }

        Expression first = ParseValue();
        // `== 4, 6, 9, 11 =>`: one arm for several values. Only equality lists them.
        var more = new List<Expression>();
        while (op == TokenType.Equal && CheckAndAdvance(type: TokenType.Comma))
        {
            more.Add(item: ParseValue());
        }

        _inWhenPatternContext = false;

        // Comparison patterns (`< 0`, `== 5`) start with an operator, so `and` after the value is
        // unambiguously a guard (no flags collision). The guard typically tests outer variables.
        var pattern = new ComparisonPattern(Operator: op, Value: first, Location: location) { MoreValues = more };
        return TryParseAndGuard(innerPattern: pattern, guardAllowed: true, location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // JUMP STATEMENTS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a return statement.
    /// Syntax: <c>return</c> or <c>return expression</c>
    /// </summary>
    /// <returns>A <see cref="ReturnStatement"/> AST node.</returns>
    private ReturnStatement ParseReturnStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression? value = null;
        // Check for both Newline and Dedent/RightBrace - either can follow a valueless return
        if (!Check(type: TokenType.Newline) && !Check(type: TokenType.Dedent) &&
            !Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            value = ParseExpression();
        }

        ConsumeStatementTerminator();

        return new ReturnStatement(Value: value, Location: location);
    }

    /// <summary>
    /// Parses a throw (fail) statement for error propagation.
    /// Syntax: <c>throw errorExpression</c>
    /// Used with Crashable types for error handling.
    /// </summary>
    /// <returns>A <see cref="ThrowStatement"/> AST node.</returns>
    private ThrowStatement ParseThrowStatement(bool isFatal)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // throw/pierce both require an error expression (a crashable-kind type). `pierce` (isFatal)
        // marks a fatal, uncatchable crash — no `!`, no try/check variants; `throw` is a recoverable
        // failure that propagates.
        Expression error = ParseExpression();

        ConsumeStatementTerminator();

        return new ThrowStatement(Error: error, Location: location, IsFatal: isFatal);
    }

    /// <summary>
    /// Parses <c>todo</c>, a place not written yet. It is a <c>pierce NotImplementedError(feature: ...)</c> naming
    /// the routine: running it crashes, it never makes the routine failable, and it ends its path, so a body that
    /// is only <c>todo</c> needs no <c>return</c>.
    /// </summary>
    private ThrowStatement ParseTodoStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        var feature = new LiteralExpression(Value: _routineBeingParsed ?? "start",
            LiteralType: TokenType.TextLiteral,
            Location: location);
        var error = new CallExpression(Callee: new IdentifierExpression(Name: "NotImplementedError", Location: location),
            Arguments: [new NamedArgumentExpression(Name: "feature", Value: feature, Location: location)],
            Location: location);
        return new ThrowStatement(Error: error, Location: location, IsFatal: true) { IsTodo = true };
    }

    /// <summary>
    /// Parses a break statement for exiting loops.
    /// Syntax: <c>break</c>
    /// </summary>
    /// <returns>A <see cref="BreakStatement"/> AST node.</returns>
    private BreakStatement ParseBreakStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new BreakStatement(Location: location);
    }

    /// <summary>
    /// Parses a continue statement for skipping to next loop iteration.
    /// Syntax: <c>continue</c>
    /// </summary>
    /// <returns>A <see cref="ContinueStatement"/> AST node.</returns>
    private ContinueStatement ParseContinueStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        List<(string Name, Expression Value, SourceLocation Location)>? with = null;
        if (CheckAndAdvance(type: TokenType.With))
        {
            with = [];
            do
            {
                SourceLocation at = GetLocation();
                string name = Consume(type: TokenType.Identifier, errorMessage: "Expected the name of a carried value: continue with a: v").Text;
                Consume(type: TokenType.Colon, errorMessage: $"Expected ':' and the next value of '{name}'");
                with.Add(item: (name, ParseExpression(), at));
            } while (CheckAndAdvance(type: TokenType.Comma));
        }

        ConsumeStatementTerminator();
        List<string>? carried = _carriedLoops.Count > 0 ? _carriedLoops.Peek() : null;
        string? mismatch = carried == null
            ? with == null ? null : "This loop carries no values (no `with` on it), so `continue` passes none: write a bare 'continue'."
            : with == null
                ? $"This loop carries values, so `continue` passes the next ones: 'continue with {string.Join(", ", carried.Select(selector: c => c + ": …"))}'."
                : with.Select(selector: w => w.Name).OrderBy(keySelector: n => n).SequenceEqual(second: carried.OrderBy(keySelector: n => n))
                    ? null
                    : $"`continue with` names each carried value once: {string.Join(", ", carried)}.";
        if (mismatch != null)
        {
            RecordError(ex: new GrammarException(code: GrammarDiagnosticCode.ContinueCarriesWrongValues,
                message: mismatch,
                fileName: FileName,
                line: location.Line,
                column: location.Column,
                language: _language));
        }

        return new ContinueStatement(Location: location) { With = with };
    }

    /// <summary>
    /// Parses a pass statement (empty statement placeholder).
    /// Syntax: <c>pass</c>
    /// Used as a placeholder in empty blocks or protocols.
    /// </summary>
    /// <returns>A <see cref="PassStatement"/> AST node.</returns>
    private PassStatement ParsePassStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new PassStatement(Location: location);
    }

    /// <summary>
    /// Parses a drop statement (explicitly ignores a return value).
    /// Syntax: <c>drop routine_call()</c>
    /// Used to explicitly indicate that a routine's return value is intentionally ignored.
    /// </summary>
    /// <returns>A <see cref="DropStatement"/> AST node.</returns>
    /// <summary>
    /// Whether the line starting at the current <c>steal</c> is the statement <c>steal place as old [inject fresh]</c>
    /// (an <c>as</c> outside brackets), not an expression statement that moves a variable.
    /// </summary>
    private bool StealStatementAhead()
    {
        int depth = 0;
        for (int offset = 1;; offset++)
        {
            Token token = PeekToken(offset: offset);
            switch (token.Type)
            {
                case TokenType.Newline or TokenType.Eof or TokenType.Dedent or TokenType.Indent:
                    return false;
                case TokenType.LeftParen or TokenType.LeftBracket:
                    depth++;
                    break;
                case TokenType.RightParen or TokenType.RightBracket:
                    depth--;
                    break;
                case TokenType.As when depth == 0:
                    return true;
            }
        }
    }

    /// <summary>
    /// Parses <c>steal place as old</c> or <c>steal place as old inject fresh</c> (the <c>steal</c> is consumed).
    /// </summary>
    private StealStatement ParseStealStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Expression place = ParsePostfix();
        Consume(type: TokenType.As, errorMessage: "Expected 'as' and a name after the place 'steal' takes from");
        string name = Consume(type: TokenType.Identifier, errorMessage: "Expected the name of what 'steal' takes out")
           .Text;
        Expression? fresh = null;
        if (CurrentToken.Type is TokenType.Identifier or TokenType.ReservedWord && CurrentToken.Text == "inject")
        {
            Advance();
            fresh = ParseExpression();
        }

        ConsumeStatementTerminator();
        return new StealStatement(Place: place, Name: name, Fresh: fresh, Location: location);
    }

    private DropStatement ParseDropStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Expression expression = ParseExpression();

        // Whether the discarded expression is actually a routine call is a SEMANTIC check, not a
        // grammatical one — the semantic verifier reports it (RF-S421). The parser just records the
        // discarded expression, whatever its shape.
        ConsumeStatementTerminator();
        return new DropStatement(Expression: expression, Location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // RESOURCE MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a using block for scoped resource management (P17).
    /// Syntax: <c>using expr as name</c> with indented body.
    /// Multi-resource: <c>using expr1 as name1, expr2 as name2</c> (desugars to nested blocks).
    /// </summary>
    /// <remarks>
    /// <code>
    /// using open("file.txt") as file
    ///   var content = file.read_all()
    ///   process(content)
    ///   # file is automatically closed at block exit
    /// </code>
    /// </remarks>
    /// <returns>A <see cref="UsingStatement"/> AST node (possibly nested for multi-resource).</returns>
    private Statement ParseUsingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse resource-binding pairs: using expr as name [, expr as name]*
        var resources = new List<(Expression Resource, string Name, List<string>? Parts)>();

        do
        {
            Expression resource = ParseExpression();
            Consume(type: TokenType.As,
                errorMessage: "Expected 'as' after resource expression in using block");
            // `as (a, b)` names the items of the tuple the scope binds.
            if (CheckAndAdvance(type: TokenType.LeftParen))
            {
                var parts = new List<string>();
                do
                {
                    parts.Add(item: ConsumeIdentifier(errorMessage: "Expected a name in 'as (...)'"));
                } while (CheckAndAdvance(type: TokenType.Comma));

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after the names in 'as (...)'");
                SourceLocation at = resource.Location;
                resources.Add(item: (resource, $"__using_{at.Line}_{at.Column}", parts));
                continue;
            }

            string name = ConsumeIdentifier(errorMessage: "Expected binding name after 'as'");
            resources.Add(item: (resource, name, null));
        } while (CheckAndAdvance(type: TokenType.Comma));

        // Parse the indented body
        Statement body = ParseBody();

        // `fallback` after a `using` body was the branch for a lock that was taken. A Guarded is opened with
        // `try viewing` / `try modifying` and `else` now: reject the old spelling where it stands.
        if (IsContextualFallbackBlock())
        {
            throw ThrowParseError(
                message: "You wrote a 'fallback' branch after 'using', and 'using' no longer opens a Guarded. Open it " +
                         "without waiting with 'try modifying g as x' (or 'try viewing g as x') and put the branch for " +
                         "a taken one under 'else'. 'migrate --step access' rewrites it.");
        }

        // Build nested UsingStatements from inside out (last resource is innermost).
        Statement result = body;
        for (int i = resources.Count - 1; i >= 0; i--)
        {
            result = new UsingStatement(Resource: resources[index: i].Resource,
                Name: resources[index: i].Name,
                Body: result,
                Location: location) { Parts = resources[index: i].Parts };
        }

        return result;
    }

    /// <summary>
    /// Recognises a bare identifier <c>fallback</c> followed by an indented block right after a <c>using</c> body: the
    /// old branch for a taken lock. Requiring the following INDENT keeps a real identifier <c>fallback</c> used as an
    /// ordinary statement from being misread.
    /// </summary>
    private bool IsContextualFallbackBlock()
    {
        if (CurrentToken is not { Type: TokenType.Identifier, Text: "fallback" })
        {
            return false;
        }

        // After `fallback` comes either INDENT directly, or NEWLINE then INDENT.
        return PeekToken(offset: 1)
           .Type == TokenType.Indent || PeekToken(offset: 1)
           .Type == TokenType.Newline && PeekToken(offset: 2)
           .Type == TokenType.Indent;
    }

    /// <summary>
    /// Parses a block that opens access: <c>viewing rates, modifying payer as from, to</c>, then its indented body. A
    /// verb applies to the targets after it until another verb comes, and each target takes its own <c>as</c> (a target
    /// without one keeps its own name). After <c>try</c> (consumed by the caller) the block only tries, and an
    /// <c>else</c> block follows for when a target is taken.
    /// </summary>
    /// <param name="isTry">Whether the block was written <c>try viewing</c> / <c>try modifying</c>.</param>
    /// <returns>An <see cref="AccessStatement"/>.</returns>
    private AccessStatement ParseAccessStatement(bool isTry)
    {
        SourceLocation location = GetLocation(token: isTry ? PeekToken(offset: -1) : CurrentToken);
        var targets = new List<AccessTarget>();
        bool isWrite = false;
        do
        {
            if (Check(type: TokenType.Viewing) || Check(type: TokenType.Modifying))
            {
                isWrite = CurrentToken.Type == TokenType.Modifying;
                Advance();
            }

            SourceLocation at = GetLocation();
            Expression target = ParseExpression();
            if (CheckAndAdvance(type: TokenType.As))
            {
                string alias = ConsumeIdentifier(errorMessage: "Expected the token's name after 'as'");
                targets.Add(item: new AccessTarget(IsWrite: isWrite, Target: target, Name: alias, HasAlias: true,
                    Location: at));
                continue;
            }

            // Without `as` the token keeps the target's own name. A target that has none is reported by the analysis.
            string name = target is IdentifierExpression id ? id.Name : "";
            targets.Add(item: new AccessTarget(IsWrite: isWrite, Target: target, Name: name, HasAlias: false,
                Location: at));
        } while (CheckAndAdvance(type: TokenType.Comma));

        Statement body = ParseBody();
        Statement? elseBody = null;
        if (isTry)
        {
            if (!CheckAndAdvance(type: TokenType.Else))
            {
                throw ThrowParseError(
                    message: "You wrote a 'try' block without its 'else' block. Add 'else' after the body with what to " +
                             "do when a target is taken right now ('pass' to do nothing).");
            }

            elseBody = ParseBody();
        }

        return new AccessStatement(Targets: targets, Body: body, IsTry: isTry, ElseBody: elseBody,
            Location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // RF-ONLY CONSTRUCTS
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a danger statement (unsafe memory operations block).
    /// Parsed in every language: whether a program's own files may use it is checked after parsing
    /// (<c>LanguageRules.AllowsUnsafeCode</c>), since the standard library always may.
    /// Syntax: <c>danger</c> followed by indented body.
    /// </summary>
    /// <returns>A <see cref="DangerStatement"/> AST node.</returns>
    private DangerStatement ParseDangerStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // 'danger' is tokenized as a single Danger token (including the '!')
        BlockStatement body = ParseIndentedBlock();

        return new DangerStatement(Body: body, Location: location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // BLOCK / BODY PARSING
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a block body using indentation (indent/dedent delimited).
    /// Both RazorForge and Suflae use indentation-based syntax.
    /// </summary>
    /// <returns>A <see cref="BlockStatement"/> AST node.</returns>
    private BlockStatement ParseBody()
    {
        return ParseIndentedBlock();
    }

    /// <summary>
    /// Parses an indented block of statements.
    /// Expects INDENT token, parses statements until DEDENT.
    /// </summary>
    /// <returns>A <see cref="BlockStatement"/> containing the parsed statements.</returns>
    private BlockStatement ParseIndentedBlock()
    {
        SourceLocation location = GetLocation();
        var statements = new List<Statement>();

        // Consume newline before indent (optional if we're already at Indent)
        if (Check(type: TokenType.Newline))
        {
            Advance(); // consume newline
        }
        else if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected newline before indented block");
        }

        // Must have an indent token for a proper indented block
        if (!Check(type: TokenType.Indent))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedIndentedBlock,
                message: "Expected indented block");
        }

        // Process the indent token
        ProcessIndentToken();

        bool hadParseError = false;
        // Parse statements until we hit a dedent
        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            // Skip empty lines and doc comments (indentation handler doesn't emit
            // Dedent for comment-only lines, so doc comments at lower indent may appear here)
            if (CheckAndAdvance(TokenType.Newline, TokenType.DocComment))
            {
                continue;
            }

            try
            {
                Statement stmt = ParseStatement();
                statements.Add(item: stmt);
            }
            catch (GrammarException ex)
            {
                // Statement-level recovery: record the error, resynchronize to the next
                // statement in THIS block, and keep parsing — one bad statement must not
                // discard the rest of the routine (or cascade through the rest of the file).
                RecordError(ex: ex);
                SynchronizeWithinBlock();
                hadParseError = true;
            }
        }

        // Process dedent tokens
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedent,
                message: "Expected dedent to close indented block");
        }

        return new BlockStatement(Statements: statements, Location: location) { HadParseError = hadParseError };
    }

    /// <summary>
    /// Parses an expression statement (expression followed by newline/terminator).
    /// Used for function calls, assignments, and other expressions at statement level.
    /// </summary>
    /// <returns>An <see cref="ExpressionStatement"/> AST node.</returns>
    private ExpressionStatement ParseExpressionStatement()
    {
        Expression expr = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatement(Expression: expr, Location: expr.Location);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // DESTRUCTURING
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses record/entity destructuring in var bindings.
    /// Syntax: <c>var (memberVar, memberVar2) = expr</c> or <c>var (memberVar: alias, memberVar2: alias2) = expr</c>
    /// or nested: <c>var ((x, y), radius) = circle</c>
    /// Destructuring only works for types where ALL member variables are public.
    /// </summary>
    /// <returns>A <see cref="DestructuringStatement"/> AST node.</returns>
    private DestructuringStatement ParseDestructuringDeclaration()
    {
        SourceLocation
            location =
                GetLocation(token: PeekToken(offset: -2)); // -2 because we already consumed 'var'

        // Parse the destructuring pattern (reuse ParseDestructuringBindings from Expressions)
        List<DestructuringBinding> bindings = ParseDestructuringBindings();

        var pattern = new DestructuringPattern(Bindings: bindings, Location: location);

        // An optional type the value is read as: `var (a, b): (S64, S64) = ask()`.
        TypeExpression? declaredType = CheckAndAdvance(type: TokenType.Colon)
            ? ParseType()
            : null;

        // Expect '='
        Consume(type: TokenType.Assign, errorMessage: "Expected '=' in destructuring");

        // Parse the initializer expression
        Expression initializer = ParseExpression();

        ConsumeStatementTerminator();

        return new DestructuringStatement(Pattern: pattern,
            Initializer: initializer,
            Location: location) { DeclaredType = declaredType };
    }
}
