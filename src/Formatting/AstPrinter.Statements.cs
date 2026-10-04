using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Formatting;

/// <summary>Printing of statements and blocks.</summary>
internal sealed partial class AstPrinter
{
    /// <summary>The source start of a statement's unit: the start of the logical line it begins on.</summary>
    private int StatementStart(Statement statement)
    {
        return statement switch
        {
            // A destructuring declaration is located at the token before its `var`. A binding sits on its line.
            DestructuringStatement { Pattern.Bindings: [var binding, ..] } => LineStart(location: binding.Location),
            DeclarationStatement declaration => LineStart(location: declaration.Declaration.Location),
            _ => LineStart(location: statement.Location)
        };
    }

    private void PrintBlock(List<Statement> statements, int depth, SourceLocation at)
    {
        if (statements.Count == 0)
        {
            throw Refuse(at: at, reason: "an empty block");
        }

        bool first = true;
        foreach (Statement statement in statements)
        {
            PrintStatement(statement: statement, depth: depth, first: first, policy: BlankPolicy.Preserve);
            first = false;
        }

        EmitBlockTrailing(depth: depth);
    }

    /// <summary>A statement body: always a block in parsed source.</summary>
    private void PrintBody(Statement body, int depth, SourceLocation at)
    {
        if (body is not BlockStatement { IntroducesScope: true } block)
        {
            throw Refuse(at: at, reason: $"a {body.GetType().Name} where a block was expected");
        }

        PrintBlock(statements: block.Statements, depth: depth, at: at);
    }

    private void PrintStatement(Statement statement, int depth, bool first, BlankPolicy policy)
    {
        int start = StatementStart(statement: statement);
        EmitPrefix(start: start, depth: depth, first: first, policy: policy);
        PrintStatementAt(statement: statement, start: start, depth: depth, prefix: Doc.Empty);
    }

    /// <summary>
    /// Prints a statement whose unit starts at <paramref name="start"/>, with <paramref name="prefix"/> in front of
    /// its first line (the pattern of a <c>when</c> arm whose body is written after its arrow).
    /// </summary>
    private void PrintStatementAt(Statement statement, int start, int depth, Doc prefix)
    {
        switch (statement)
        {
            case ExpressionStatement expression:
                EmitHead(start: start, depth: depth,
                    head: Doc.Concat(prefix, Expr(expression: expression.Expression, context: ExprContext.Statement)));
                break;
            case DeclarationStatement { Declaration: VariableDeclaration variable }:
                if (prefix != Doc.Empty)
                {
                    EmitHead(start: start, depth: depth, head: Doc.Concat(prefix, VariableDoc(variable: variable)));
                }
                else
                {
                    EmitHead(start: start, depth: depth, head: VariableDoc(variable: variable));
                }

                break;
            case DestructuringStatement destructuring:
                EmitHead(start: start, depth: depth, head: Doc.Concat(prefix,
                    Doc.Text(text: "var " + BindingsText(bindings: destructuring.Pattern.Bindings) + " = "),
                    Expr(expression: destructuring.Initializer, context: ExprContext.Statement)));
                break;
            case ReturnStatement ret:
                EmitHead(start: start, depth: depth, head: ret.Value == null
                    ? Doc.Concat(prefix, Doc.Text(text: "return"))
                    : Doc.Concat(prefix, Doc.Text(text: "return "),
                        Expr(expression: ret.Value, context: ExprContext.Statement)));
                break;
            case ThrowStatement thrown:
                EmitHead(start: start, depth: depth, head: Doc.Concat(prefix, Doc.Text(text: thrown.IsFatal
                        ? "pierce "
                        : "throw "),
                    Expr(expression: thrown.Error, context: ExprContext.Statement)));
                break;
            case DiscardStatement discard:
                EmitHead(start: start, depth: depth, head: Doc.Concat(prefix, Doc.Text(text: "discard "),
                    Expr(expression: discard.Expression, context: ExprContext.Statement)));
                break;
            case AbsentStatement:
                EmitHead(start: start, depth: depth, head: Doc.Concat(prefix, Doc.Text(text: "absent")));
                break;
            case BreakStatement:
                EmitHead(start: start, depth: depth, head: Doc.Concat(prefix, Doc.Text(text: "break")));
                break;
            case ContinueStatement:
                EmitHead(start: start, depth: depth, head: Doc.Concat(prefix, Doc.Text(text: "continue")));
                break;
            case PassStatement:
                EmitHead(start: start, depth: depth, head: Doc.Concat(prefix, Doc.Text(text: "pass")));
                break;
            case IfStatement ifStatement:
                PrintIf(statement: ifStatement, start: start, depth: depth, prefix: prefix, keyword: "if");
                break;
            case WhileStatement whileStatement:
                EmitHead(start: start, depth: depth, head: Doc.Concat(prefix, Doc.Text(text: "while "),
                    Expr(expression: whileStatement.Condition, context: ExprContext.Statement)));
                PrintBody(body: whileStatement.Body, depth: depth + 1, at: whileStatement.Location);
                PrintElseBranch(elseBranch: whileStatement.ElseBranch, depth: depth, at: whileStatement.Location);
                break;
            case LoopStatement loop:
                if (loop.IsIteratorEachLoop || loop.IterationSourceName != null || loop.IterationSourcePath != null)
                {
                    throw Unsupported(node: loop, at: loop.Location);
                }

                EmitHead(start: start, depth: depth, head: Doc.Concat(prefix, Doc.Text(text: "loop")));
                PrintBody(body: loop.Body, depth: depth + 1, at: loop.Location);
                break;
            case EachStatement each:
                PrintEach(each: each, start: start, depth: depth, prefix: prefix);
                break;
            case ExpandStatement expand:
                EmitHead(start: start, depth: depth, head: Doc.Concat(prefix,
                    Doc.Text(text: $"expand {expand.HandleName} in {expand.SourceName}("),
                    TypeDoc(type: expand.SourceType),
                    Doc.Text(text: ")")));
                PrintBody(body: expand.Body, depth: depth + 1, at: expand.Location);
                break;
            case WhenStatement whenStatement:
                PrintWhen(when: whenStatement, start: start, depth: depth, prefix: prefix);
                break;
            case DangerStatement { IsBuilderWritten: false } danger:
                EmitHead(start: start, depth: depth, head: Doc.Concat(prefix, Doc.Text(text: "danger")));
                PrintBody(body: danger.Body, depth: depth + 1, at: danger.Location);
                break;
            case UsingStatement usingStatement:
                PrintUsing(statement: usingStatement, start: start, depth: depth, prefix: prefix);
                break;
            default:
                throw Unsupported(node: statement, at: statement.Location);
        }
    }

    private Doc VariableDoc(VariableDeclaration variable)
    {
        if (variable.Annotations is { Count: > 0 } || variable.Visibility != VisibilityModifier.Open ||
            variable.IsGlobal)
        {
            throw Refuse(at: variable.Location, reason: "a local variable with annotations or a visibility");
        }

        var parts = new List<Doc>
        {
            Doc.Text(text: (variable.IsPreset
                ? "preset "
                : (variable.IsLateInit
                    ? "lateinit "
                    : "") + "var ") + variable.Name)
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

        return Doc.Concat(parts: parts);
    }

    private void PrintIf(IfStatement statement, int start, int depth, Doc prefix, string keyword)
    {
        // `unless c` parses to `if not c` with the negation located at the condition itself.
        bool unless = keyword == "if" && statement.Condition is UnaryExpression { Operator: UnaryOperator.Not } not &&
                      not.Location == not.Operand.Location;
        Doc head = unless
            ? Doc.Concat(prefix, Doc.Text(text: "unless "),
                Expr(expression: ((UnaryExpression)statement.Condition).Operand, context: ExprContext.Statement))
            : Doc.Concat(prefix, Doc.Text(text: keyword + " "),
                Expr(expression: statement.Condition, context: ExprContext.Statement));
        EmitHead(start: start, depth: depth, head: head);
        PrintBody(body: statement.ThenStatement, depth: depth + 1, at: statement.Location);

        switch (statement.ElseStatement)
        {
            case null:
                return;
            case IfStatement elseIf when !unless:
                int elseIfStart = LineStart(location: elseIf.Location);
                if (_index.Tokens[index: _index.TokenIndexAt(position: elseIf.Location.Position)].Type !=
                    TokenType.Elseif)
                {
                    throw Refuse(at: elseIf.Location, reason: "an else branch holding a bare if");
                }

                EmitPrefix(start: elseIfStart, depth: depth, first: false, policy: BlankPolicy.None);
                PrintIf(statement: elseIf, start: elseIfStart, depth: depth, prefix: Doc.Empty, keyword: "elseif");
                return;
            default:
                PrintElseBranch(elseBranch: statement.ElseStatement, depth: depth, at: statement.Location);
                return;
        }
    }

    /// <summary>An <c>else</c> header and its block (of an if, a while or an each).</summary>
    private void PrintElseBranch(Statement? elseBranch, int depth, SourceLocation at)
    {
        if (elseBranch == null)
        {
            return;
        }

        PrintContinuation(keyword: "else", body: elseBranch, depth: depth, at: at);
    }

    /// <summary>A header that continues a statement (<c>else</c>, <c>fallback</c>) and its block.</summary>
    private void PrintContinuation(string keyword, Statement body, int depth, SourceLocation at)
    {
        if (body is not BlockStatement block)
        {
            throw Refuse(at: at, reason: $"a {keyword} branch that is not a block");
        }

        // The block is located at the line break that ends its header line.
        int start = LineStart(location: block.Location);
        EmitPrefix(start: start, depth: depth, first: false, policy: BlankPolicy.None);
        EmitHead(start: start, depth: depth, head: Doc.Text(text: keyword));
        PrintBlock(statements: block.Statements, depth: depth + 1, at: at);
    }

    private void PrintEach(EachStatement each, int start, int depth, Doc prefix)
    {
        string variable = each.VariablePattern != null
            ? BindingsText(bindings: each.VariablePattern.Bindings)
            : each.Variable ?? throw Refuse(at: each.Location, reason: "an each loop without a variable");
        EmitHead(start: start, depth: depth, head: Doc.Concat(prefix, Doc.Text(text: "each " + variable + " in "),
            Expr(expression: each.Iterable, context: ExprContext.Statement)));
        PrintBody(body: each.Body, depth: depth + 1, at: each.Location);
        PrintElseBranch(elseBranch: each.ElseBranch, depth: depth, at: each.Location);
    }

    private void PrintUsing(UsingStatement statement, int start, int depth, Doc prefix)
    {
        var resources = new List<Doc>();
        UsingStatement current = statement;
        while (true)
        {
            resources.Add(item: Doc.Concat(Expr(expression: current.Resource, context: ExprContext.ListItem),
                Doc.Text(text: " as " + current.Name)));
            if (current.Body is UsingStatement inner && inner.Location == current.Location &&
                inner.FallbackBody == null)
            {
                current = inner;
                continue;
            }

            break;
        }

        var head = new List<Doc> { prefix, Doc.Text(text: "using ") };
        for (int i = 0; i < resources.Count; i++)
        {
            if (i > 0)
            {
                head.Add(item: Doc.Text(text: ", "));
            }

            head.Add(item: resources[index: i]);
        }

        EmitHead(start: start, depth: depth, head: Doc.Concat(parts: head));
        PrintBody(body: current.Body, depth: depth + 1, at: statement.Location);
        if (statement.FallbackBody != null)
        {
            PrintContinuation(keyword: "fallback", body: statement.FallbackBody, depth: depth, at: statement.Location);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WHEN
    // ═══════════════════════════════════════════════════════════════════════════

    private void PrintWhen(WhenStatement when, int start, int depth, Doc prefix)
    {
        // A condition-based `when` (written `when` or `when true`) carries a `true` subject located at the keyword.
        bool conditionBased = when.Expression is LiteralExpression { LiteralType: TokenType.True } subject &&
                              subject.Location == when.Location;
        Doc head = conditionBased
            ? Doc.Concat(prefix, Doc.Text(text: "when"))
            : Doc.Concat(prefix, Doc.Text(text: "when "), WhenSubject(subject: when.Expression));
        EmitHead(start: start, depth: depth, head: head);
        PrintWhenArms(clauses: when.Clauses, expansion: when.ArmExpansion, depth: depth + 1,
            conditionBased: conditionBased, isExpression: false, at: when.Location);
    }

    /// <summary>
    /// A <c>when</c> subject. A bare <c>true</c> would read as the condition-based form, so a written
    /// <c>(true)</c> subject keeps its parentheses.
    /// </summary>
    private Doc WhenSubject(Expression subject)
    {
        Doc doc = Expr(expression: subject, context: ExprContext.Statement);
        return subject is LiteralExpression { LiteralType: TokenType.True }
            ? Doc.Concat(Doc.Text(text: "("), doc, Doc.Text(text: ")"))
            : doc;
    }

    private void PrintWhenArms(List<WhenClause> clauses, WhenArmExpansion? expansion, int depth,
        bool conditionBased, bool isExpression, SourceLocation at)
    {
        var arms = new List<(int Start, object Arm)>();
        foreach (WhenClause clause in clauses)
        {
            arms.Add(item: (LineStart(location: clause.Pattern.Location), clause));
        }

        if (expansion != null)
        {
            arms.Add(item: (ExpansionStart(expansion: expansion), expansion));
        }

        if (arms.Count == 0)
        {
            throw Refuse(at: at, reason: "a when without arms");
        }

        bool first = true;
        foreach ((int armStart, object arm) in arms.OrderBy(keySelector: a => a.Start))
        {
            EmitPrefix(start: armStart, depth: depth, first: first, policy: BlankPolicy.Preserve);
            if (arm is WhenClause clause)
            {
                PrintArm(clause: clause, start: armStart, depth: depth, conditionBased: conditionBased,
                    isExpression: isExpression);
            }
            else
            {
                var expand = (WhenArmExpansion)arm;
                EmitHead(start: armStart, depth: depth, head: Doc.Concat(
                    Doc.Text(text: $"expand {expand.HandleName} in branchof("),
                    TypeDoc(type: expand.SourceType),
                    Doc.Text(text: ")")));
                int templateStart = LineStart(location: expand.Template.Pattern.Location);
                EmitPrefix(start: templateStart, depth: depth + 1, first: true, policy: BlankPolicy.Preserve);
                PrintArm(clause: expand.Template, start: templateStart, depth: depth + 1, conditionBased: false,
                    isExpression: isExpression);
                EmitBlockTrailing(depth: depth + 1);
            }

            first = false;
        }

        EmitBlockTrailing(depth: depth);
    }

    /// <summary>The start of an arm expansion's <c>expand</c> line: the line before its template arm.</summary>
    private int ExpansionStart(WhenArmExpansion expansion)
    {
        int i = _index.TokenIndexAt(position: expansion.Template.Pattern.Location.Position);
        while (i > 0 && _index.Tokens[index: i].Type != TokenType.Expand)
        {
            i--;
        }

        return _index.Tokens[index: _index.LogicalLineStart(tokenIndex: i)].Position;
    }

    private void PrintArm(WhenClause clause, int start, int depth, bool conditionBased, bool isExpression)
    {
        Doc pattern = PatternDoc(pattern: clause.Pattern, conditionBased: conditionBased);
        switch (clause.Body)
        {
            case BlockStatement block:
                EmitHead(start: start, depth: depth, head: Doc.Concat(pattern, Doc.Text(text: " =>")));
                PrintBlock(statements: block.Statements, depth: depth + 1, at: clause.Location);
                break;
            case ExpressionStatement expression when isExpression:
                EmitHead(start: start, depth: depth, head: Doc.Concat(pattern, Doc.Text(text: " => "),
                    Expr(expression: expression.Expression, context: ExprContext.Statement)));
                break;
            case { } body when !isExpression:
                PrintStatementAt(statement: body, start: start, depth: depth,
                    prefix: Doc.Concat(pattern, Doc.Text(text: " => ")));
                break;
            default:
                throw Unsupported(node: clause.Body, at: clause.Location);
        }
    }
}
