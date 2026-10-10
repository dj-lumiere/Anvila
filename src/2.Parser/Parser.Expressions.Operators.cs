using Builder.Diagnostics;
using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Parser;

/// <summary>
/// Partial class containing operator and precedence-chain parsing.
/// </summary>
public partial class Parser
{
    private Expression ParseAssignment()
    {
        Expression expr = ParseInlineConditional();
        Expression value;
        // Check for simple assignment
        if (CheckAndAdvance(type: TokenType.Assign))
        {
            value = ParseAssignment();
            return new BinaryExpression(Left: expr,
                Operator: BinaryOperator.Assign,
                Right: value,
                Location: expr.Location);
        }

        // Check for compound assignment operators
        BinaryOperator? compoundOp = TryMatchCompoundAssignment();
        if (!compoundOp.HasValue)
        {
            return expr;
        }

        value = ParseAssignment();

        // Base operators with in-place wired support -> CompoundAssignmentExpression
        if (compoundOp.Value.GetInPlaceMemberRoutineName() != null)
        {
            return new CompoundAssignmentExpression(Target: expr,
                Operator: compoundOp.Value,
                Value: value,
                Location: expr.Location);
        }

        // Overflow variants and ??= -> expand to a = a op b
        Expression binaryExpr = new BinaryExpression(Left: expr,
            Operator: compoundOp.Value,
            Right: value,
            Location: expr.Location);
        return new BinaryExpression(Left: expr,
            Operator: BinaryOperator.Assign,
            Right: binaryExpr,
            Location: expr.Location);
    }

    /// <summary>
    /// Tries to match a compound assignment operator and returns the corresponding binary operator.
    /// Returns null if the current token is not a compound assignment.
    /// </summary>
    private BinaryOperator? TryMatchCompoundAssignment()
    {
        TokenType current = CurrentToken.Type;
        BinaryOperator? op = current switch
        {
            TokenType.PlusAssign => BinaryOperator.Add,
            TokenType.MinusAssign => BinaryOperator.Subtract,
            TokenType.StarAssign => BinaryOperator.Multiply,
            TokenType.SlashAssign => BinaryOperator.TrueDivide,
            TokenType.DivideAssign => BinaryOperator.FloorDivide,
            TokenType.PercentAssign => BinaryOperator.Modulo,
            TokenType.PowerAssign => BinaryOperator.Power,
            TokenType.AmpersandAssign => BinaryOperator.BitwiseAnd,
            TokenType.PipeAssign => BinaryOperator.BitwiseOr,
            TokenType.CaretAssign => BinaryOperator.BitwiseXor,
            TokenType.LeftShiftAssign => BinaryOperator.ArithmeticLeftShift,
            TokenType.RightShiftAssign => BinaryOperator.ArithmeticRightShift,
            TokenType.LogicalRightShiftAssign => BinaryOperator.LogicalRightShift,
            TokenType.NoneCoalesceAssign => BinaryOperator.NoneCoalesce,
            // Overflow variant compound assignments
            TokenType.PlusWrapAssign => BinaryOperator.AddWrap,
            TokenType.MinusWrapAssign => BinaryOperator.SubtractWrap,
            TokenType.MultiplyWrapAssign => BinaryOperator.MultiplyWrap,
            TokenType.PowerWrapAssign => BinaryOperator.PowerWrap,
            TokenType.PlusClampAssign => BinaryOperator.AddClamp,
            TokenType.MinusClampAssign => BinaryOperator.SubtractClamp,
            TokenType.MultiplyClampAssign => BinaryOperator.MultiplyClamp,
            TokenType.SlashClampAssign => BinaryOperator.TrueDivClamp,
            TokenType.PowerClampAssign => BinaryOperator.PowerClamp,
            _ => null
        };

        if (op.HasValue)
        {
            Advance(); // Consume the compound assignment token
        }

        return op;
    }

    /// <summary>
    /// Parses inline conditional expressions.
    /// Syntax: <c>if condition then thenExpr else elseExpr</c>
    /// Note: This is expression-level only; block-level if uses ParseIfStatement.
    /// Nested inline conditionals are forbidden for readability.
    /// </summary>
    /// <returns>The parsed expression, possibly a conditional.</returns>
    private Expression ParseInlineConditional()
    {
        // Check for inline if-then-else expression
        // Skip if already inside an inline conditional (prevents nesting for readability)
        if (_parsingInlineConditional || !CheckAndAdvance(type: TokenType.If))
        {
            return ParseNoneCoalesce();
        }

        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Set flag to prevent nested inline conditionals
        _parsingInlineConditional = true;

        try
        {
            Expression condition = ParseNoneCoalesce();

            if (Check(type: TokenType.Newline) || Check(type: TokenType.Indent))
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.UnexpectedToken,
                    message: "An 'if' that gives a value is written on one line: 'if condition then a else b'. " +
                             "It takes no indented block. For a choice of several values, or one that needs " +
                             "statements, use 'when'.");
            }

            Consume(type: TokenType.Then,
                errorMessage: "Expected 'then' after condition in inline if");
            Expression
                thenExpr =
                    ParseExpression(); // Full expression, but flag prevents nested inline if

            Consume(type: TokenType.Else, errorMessage: "Expected 'else' in inline if expression");
            Expression
                elseExpr =
                    ParseExpression(); // Full expression, but flag prevents nested inline if

            return new ConditionalExpression(Condition: condition,
                TrueExpression: thenExpr,
                FalseExpression: elseExpr,
                Location: location);
        }
        finally
        {
            _parsingInlineConditional = false;
        }
    }

    /// <summary>
    /// Parses none-coalescing expressions.
    /// Syntax: <c>a ?? b</c>
    /// Returns a if a is not None, otherwise returns b.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseNoneCoalesce()
    {
        Expression expr = ParseLogical();

        while (CheckAndAdvance(type: TokenType.NoneCoalesce))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseLogical();
            expr = new BinaryExpression(Left: expr,
                Operator: BinaryOperator.NoneCoalesce,
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses logical expressions: <c>a and b</c>, <c>a or b</c>, left-associative, short-circuit. <c>and</c> and
    /// <c>or</c> share one level, so mixing them needs parentheses (<c>a or (b and c)</c>): a reader never has to
    /// remember which binds tighter. A chain of one of them (<c>x and y and z</c>) needs none.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseLogical()
    {
        Expression expr = ParseEquality();
        TokenType? chain = null;

        while (Check(TokenType.And, TokenType.Or))
        {
            Token op = CurrentToken;
            if (chain != null && op.Type != chain)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.MixedOperatorsNeedParentheses,
                    message: "You mixed 'and' and 'or' without parentheses. Group the part that goes together: " +
                             "'a or (b and c)' or '(a or b) and c'.");
            }

            chain = op.Type;
            Advance();
            Expression right = ParseEquality();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses range expressions: <c>start to end</c>, <c>start til end</c>, each optionally followed by
    /// <c>by step</c>. A range binds looser than arithmetic and bitwise operators and tighter than comparisons, so
    /// <c>0 to n - 1</c> and <c>0 to 10 have x</c> read as written.
    /// </summary>
    /// <returns>The parsed expression, possibly a range.</returns>
    private Expression ParseRange()
    {
        Expression expr = ParseBitwise();

        // A to B / A til B, each optionally followed by `by C`.
        bool inclusive = Check(type: TokenType.To);
        if (!inclusive && !Check(type: TokenType.Til))
        {
            return expr;
        }

        Advance(); // consume 'to' / 'til'

        Expression end = ParseBitwise();
        Expression? step = null;
        if (CheckAndAdvance(type: TokenType.By))
        {
            step = ParseBitwise();
        }

        return new RangeExpression(Start: expr,
            End: end,
            Step: step,
            IsDescending: false,
            Location: expr.Location,
            IsExclusive: !inclusive);
    }

    /// <summary>
    /// Parses calls of infix routines: <c>a dot b</c> is <c>a.dot(b)</c> for a routine marked <c>@infix</c> (analysis
    /// checks the mark). They bind looser than ranges and tighter than comparisons, left to right:
    /// <c>a dot b cross c</c> is <c>(a dot b) cross c</c>. A name right after an operand, on the same line and
    /// followed by something that starts an operand, is such a call: nothing else in the grammar puts a name there.
    /// </summary>
    private Expression ParseInfix()
    {
        Expression expr = ParseRange();
        while (CurrentToken.Type == TokenType.Identifier && StartsInfixOperand(token: PeekToken(offset: 1)))
        {
            Token name = CurrentToken;
            string? retired = name.Text switch
            {
                "notin" => "'notin' is gone: the container comes first, 'xs lack x'.",
                "but" => "'but' is gone: take flags off with 'perms.without(WRITE)', or test with 'perms lack WRITE'.",
                "disobeys" => "'disobeys' is gone: write 'not (x obeys P)'.",
                _ => null
            };
            if (retired != null)
            {
                throw ThrowParseError(message: retired);
            }

            Advance();
            Expression right = ParseRange();
            SourceLocation at = GetLocation(token: name);
            expr = new CallExpression(Callee: new MemberExpression(Object: expr, MemberName: name.Text, Location: at),
                Arguments: [right],
                Location: at) { IsInfix = true };
        }

        return expr;
    }

    private static bool StartsInfixOperand(Token token)
    {
        return token.Type is TokenType.Identifier or TokenType.Me or TokenType.True or TokenType.False
                   or TokenType.NoneValue or TokenType.LeftParen or TokenType.LeftBracket or TokenType.Minus
                   or TokenType.UndecidedInteger or TokenType.FormattedText or TokenType.RawText
                   or TokenType.RawFormattedText ||
               token.Type.ToString().EndsWith(value: "Literal", comparisonType: StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses equality expressions.
    /// Syntax: <c>a != b</c>
    /// Note: <c>==</c> is handled in ParseComparison for chaining support.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseEquality()
    {
        Expression expr = ParseComparison();

        // `!=` value inequality, plus the non-chainable reference-identity operators `===` / `!==`.
        while (CheckAndAdvance(TokenType.NotEqual,
                   TokenType.IdentityEqual,
                   TokenType.IdentityNotEqual))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseComparison();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses comparison expressions with support for chaining.
    /// Syntax: <c>a &lt; b</c>, <c>a &lt;= b &lt;= c</c> (chained), <c>a &lt;=&gt; b</c> (three-way)
    /// Supports chained comparisons only with consistent direction:
    /// - Ascending chain: &lt;, &lt;=, == (e.g., a &lt; b &lt;= c == d)
    /// - Descending chain: &gt;, &gt;=, == (e.g., a &gt; b &gt;= c == d)
    /// </summary>
    /// <returns>The parsed expression (may be <see cref="ChainedComparisonExpression"/> for chains).</returns>
    private Expression ParseComparison()
    {
        Expression expr = ParseIsExpression();

        // Check for chained comparisons
        var operators = new List<BinaryOperator>();
        var operands = new List<Expression> { expr };

        while (CheckAndAdvance(TokenType.Less,
                   TokenType.LessEqual,
                   TokenType.Greater,
                   TokenType.GreaterEqual,
                   TokenType.Equal,
                   TokenType.ThreeWayComparison))
        {
            Token op = PeekToken(offset: -1);
            BinaryOperator binOp = TokenToBinaryOperator(tokenType: op.Type);

            operators.Add(item: binOp);
            Expression right = ParseIsExpression();
            operands.Add(item: right);
        }

        // If we have chained comparisons, create a ChainedComparisonExpression
        // Note: Chained comparisons are NOT desugared because they need special
        // handling to evaluate middle operands only once (a < b < c)
        if (operators.Count > 1)
        {
            return new ChainedComparisonExpression(Operands: operands,
                Operators: operators,
                Location: GetLocation());
        }

        if (operators.Count == 1)
        {
            // Single comparison
            return new BinaryExpression(Left: operands[index: 0],
                Operator: operators[index: 0],
                Right: operands[index: 1],
                Location: GetLocation());
        }

        return expr;
    }

    /// <summary>
    /// Parses type-checking, membership, and pattern matching expressions.
    /// Syntax:
    /// - <c>expr is Type</c> - type check
    /// - <c>expr is Type binding</c> - type check with variable binding
    /// - <c>expr is Type (field1, field2)</c> - destructuring pattern
    /// - <c>expr is Type (memberVar: binding)</c> - named destructuring
    /// - <c>expr isnot Type</c> - negated type check
    /// - <c>expr in collection</c> - membership test (desugars to collection.contains(expr))
    /// - <c>expr notin collection</c> - negated membership test
    /// - <c>expr obeys Protocol</c> - protocol conformance check
    /// - <c>expr disobeys Protocol</c> - negated protocol check
    /// Context-sensitive: disabled inside when clause bodies to avoid ambiguity.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseIsExpression()
    {
        Expression expr = ParseInfix();

        // `x in coll` as a test: containment is written container-first. `in` only introduces a loop
        // source (`each x in xs`, `expand m in ...`), which those headers consume themselves.
        if (!_inWhenPatternContext && !_inWhenClauseBody && Check(type: TokenType.In))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InAsMembership,
                message: "'in' only names what a loop walks (`each x in xs`). To test membership, " +
                         "put the container first: `xs have x` (or `xs lack x`).");
        }

        // Handle is/isnot/have/lack/obeys/disobeys expressions when not in when pattern/clause context.
        // (`in`/`notin` are RETIRED at the expression level — `in` is iteration-only, containment is
        // `have`/`lack`.)
        while (!_inWhenPatternContext && !_inWhenClauseBody &&
               CheckAndAdvance(TokenType.Is,
                   TokenType.IsNot,
                   TokenType.Have,
                   TokenType.Lack,
                   TokenType.Obeys))
        {
            Token op = PeekToken(offset: -1);
            SourceLocation location = GetLocation(token: op);

            if (op.Type is TokenType.Is or TokenType.IsNot)
            {
                expr = ParseIsOrIsNotPattern(expr: expr, op: op, location: location);
            }
            else if (op.Type is TokenType.Have or TokenType.Lack)
            {
                expr = ParseHaveOrLackExpression(container: expr, op: op, location: location);
            }
            else
            {
                // Handle obeys as a binary operator
                Expression right = ParseInfix();
                expr = new BinaryExpression(Left: expr,
                    Operator: TokenToBinaryOperator(tokenType: op.Type),
                    Right: right,
                    Location: location);
            }
        }

        return expr;
    }

    /// <summary>
    /// Parses a container-first containment expression (<c>container have/lack rhs</c>), the operator
    /// already consumed. A bare flag name followed by <c>and</c>/<c>or</c>/<c>but</c> is a multi-flag
    /// <see cref="FlagsTestExpression"/> (all/any/none + exclusion); anything else — a single flag or a
    /// general collection element — is a <see cref="BinaryExpression"/> with <see cref="BinaryOperator.Have"/>
    /// / <see cref="BinaryOperator.Lack"/> that SA resolves by the container's type (flags bit-test vs
    /// <c>contains</c>).
    /// </summary>
    private Expression ParseHaveOrLackExpression(Expression container, Token op,
        SourceLocation location)
    {
        bool isNegated = op.Type == TokenType.Lack;

        // Multi-flag chain: `have READ and WRITE`, `lack READ or WRITE`, `have READ but WRITE`.
        if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type is TokenType.And or TokenType.Or)
        {
            string firstFlag = ConsumeIdentifier(errorMessage: "Expected flag name");
            return ParseFlagsTestChain(subject: container,
                firstFlag: firstFlag,
                isNegated: isNegated,
                location: location);
        }

        Expression element = ParseInfix();
        return new BinaryExpression(Left: container,
            Operator: isNegated
                ? BinaryOperator.Lack
                : BinaryOperator.Have,
            Right: element,
            Location: location);
    }

    /// <summary>
    /// Parses the type/pattern following an <c>is</c> / <c>isnot</c> operator (the leading operator token
    /// has already been consumed). Handles <c>None</c>, qualified choice cases, flags-test chains, and
    /// the destructuring/single-binding/simple-type-check pattern forms.
    /// </summary>
    private Expression ParseIsOrIsNotPattern(Expression expr, Token op, SourceLocation location)
    {
        bool isNegated = op.Type == TokenType.IsNot;

        TypeExpression type = ParseIsPatternType(location: location);

        // `None` carries no payload, so it binds nothing: reject a binding (`is None x`) or a
        // destructuring (`is None (x, y)`) after it.
        if (type.Name == "None" &&
            (Check(type: TokenType.Identifier) && !IsKeywordToken(token: CurrentToken) ||
             Check(type: TokenType.LeftParen)))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                message:
                "The 'None' pattern binds no value — remove the binding or destructuring after 'None'.");
        }

        // `is` matches a variant TYPE only — flags membership moved to `have`/`lack`. A flags-chain
        // shape here (`is READ and WRITE`) is now an error steering to the new operator.
        if (Check(type: TokenType.And) || Check(type: TokenType.Or))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                message:
                $"'is' matches a variant TYPE only. For a flags membership test, use '{expr switch { IdentifierExpression ie => ie.Name, _ => "flags" }} have {type.Name} …' (or 'lack …').");
        }

        return BuildIsPatternExpression(expr: expr,
            type: type,
            isNegated: isNegated,
            location: location);
    }

    /// <summary>
    /// Parses the type after an <c>is</c>/<c>isnot</c>: either the <c>None</c> keyword or a type with an
    /// optional dotted qualifier (`is Color.RED`).
    /// </summary>
    private TypeExpression ParseIsPatternType(SourceLocation location)
    {
        // Handle 'is None' or 'isnot None' as a special case - None is a keyword
        if (CheckAndAdvance(type: TokenType.None))
        {
            return new TypeExpression(Name: "None", GenericArguments: null, Location: location);
        }

        TypeExpression type = ParseType();

        // Accept qualified choice/variant cases: `is Color.RED`, `isnot HttpStatus.OK`.
        // Mirrors ParseTypePattern's dotted-name handling so f-string holes like
        // `f"{c is Color.RED}"` parse — without this, `.RED` leaks out of the hole
        // and the f-string parser sees a stray Dot before the closing brace.
        while (CheckAndAdvance(type: TokenType.Dot))
        {
            string member = ConsumeIdentifier(
                errorMessage: "Expected identifier after '.' in 'is' pattern");
            type = type with { Name = type.Name + "." + member };
        }

        return type;
    }

    /// <summary>
    /// Builds the IsPatternExpression for the destructuring / single-binding / simple-type-check forms
    /// after the type is parsed and flags/None special cases are ruled out.
    /// </summary>
    private IsPatternExpression BuildIsPatternExpression(Expression expr, TypeExpression type,
        bool isNegated, SourceLocation location)
    {
        switch (isNegated)
        {
            // Check for destructuring pattern: is Type (...)
            case false when Check(type: TokenType.LeftParen):
            {
                // A record pattern: `is Point(x: 0, y: as y)`, and the whole value may take a name after it.
                List<DestructuringBinding> bindings = ParseDestructuringBindings();
                string? wholeName = CheckAndAdvance(type: TokenType.As)
                    ? ConsumeIdentifier(errorMessage: "Expected a name after 'as'")
                    : null;
                var pattern = new TypePattern(Type: type,
                    VariableName: wholeName,
                    Bindings: bindings,
                    Location: location);
                return new IsPatternExpression(Expression: expr,
                    Pattern: pattern,
                    IsNegated: false,
                    Location: location);
            }
            // A single binding: `is Type as name` (only for 'is', not 'isnot').
            case false when Check(type: TokenType.As):
            {
                Advance();
                string variableName = ConsumeIdentifier(errorMessage: "Expected a name after 'as'");
                var pattern = new TypePattern(Type: type,
                    VariableName: variableName,
                    Bindings: null,
                    Location: location);
                return new IsPatternExpression(Expression: expr,
                    Pattern: pattern,
                    IsNegated: false,
                    Location: location);
            }
            default:
            {
                // Simple type check: is Type or isnot Type
                var pattern = new TypePattern(Type: type,
                    VariableName: null,
                    Bindings: null,
                    Location: location);
                return new IsPatternExpression(Expression: expr,
                    Pattern: pattern,
                    IsNegated: isNegated,
                    Location: location);
            }
        }
    }
}
