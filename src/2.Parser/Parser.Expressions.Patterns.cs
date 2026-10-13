using Builder.Diagnostics;
using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Parser;

/// <summary>
/// Partial class containing pattern and flags-test expression parsing helpers.
/// </summary>
public partial class Parser
{
    private const string ExpectedFlagNameAfterAnd = "Expected flag name after 'and'";


    /// <summary>
    /// Parses destructuring bindings for pattern matching.
    /// Syntax: (memberVar1, memberVar2) or (memberVar: binding, ...) or nested ((x: x1, y: y1), ...)
    /// </summary>
    private List<DestructuringBinding> ParseDestructuringBindings()
    {
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' for destructuring pattern");

        var bindings = new List<DestructuringBinding>();

        do
        {
            SourceLocation bindingLocation = GetLocation();

            // Check for nested pattern: (...)
            if (Check(type: TokenType.LeftParen))
            {
                // Nested destructuring without type name - used for anonymous record member variables
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                // For nested patterns without a member variable name, use index-based binding
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: "_nested",
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings,
                        Location: bindingLocation),
                    Location: bindingLocation));
            }
            // Check for wildcard: _
            else if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
            {
                Advance();
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: "_",
                    NestedPattern: null,
                    Location: bindingLocation));
            }
            else
            {
                bindings.Add(
                    item: ParseNamedOrPositionalBinding(bindingLocation: bindingLocation));
            }
        } while (CheckAndAdvance(type: TokenType.Comma));

        Consume(type: TokenType.RightParen,
            errorMessage: "Expected ')' after destructuring pattern");

        return bindings;
    }

    /// <summary>One member variable of a record pattern (see <see cref="ParseNamedOrPositionalBindingForList"/>).</summary>
    private DestructuringBinding ParseNamedOrPositionalBinding(SourceLocation bindingLocation)
    {
        return ParseNamedOrPositionalBindingForList(bindingLocation: bindingLocation);
    }

    /// <summary>
    /// Parses a list of destructuring bindings (without consuming the surrounding parentheses).
    /// Used by ParseTypePattern() for type patterns with destructuring like: is CIRCLE ((x, y), radius)
    /// </summary>
    private List<DestructuringBinding> ParseDestructuringBindingList()
    {
        var bindings = new List<DestructuringBinding>();

        if (Check(type: TokenType.RightParen))
        {
            return bindings; // Empty list
        }

        do
        {
            SourceLocation bindingLocation = GetLocation();

            // Check for nested pattern: (...)
            if (Check(type: TokenType.LeftParen))
            {
                // Nested destructuring - recursively parse with parens
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                // For nested patterns without a member variable name, use null MemberVariableName
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: null,
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings,
                        Location: bindingLocation),
                    Location: bindingLocation));
            }
            // Check for wildcard: _
            else if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
            {
                Advance();
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: "_",
                    NestedPattern: null,
                    Location: bindingLocation));
            }
            else
            {
                bindings.Add(
                    item: ParseNamedOrPositionalBindingForList(bindingLocation: bindingLocation));
            }
        } while (CheckAndAdvance(type: TokenType.Comma));

        return bindings;
    }

    /// <summary>
    /// Parses one member variable of a record pattern for <see cref="ParseDestructuringBindingList"/>, written the
    /// way the record is built (<c>T(field: value)</c>): <c>field</c> alone names it (<c>field: as field</c>),
    /// <c>field: as name</c> names it otherwise, <c>field: is Weapon as w</c> observes it, <c>field: (a, b)</c>
    /// takes it apart, <c>field: &lt; 5</c> compares it, and <c>field: value</c> matches when it equals the value.
    /// </summary>
    private DestructuringBinding ParseNamedOrPositionalBindingForList(
        SourceLocation bindingLocation)
    {
        string name = ConsumeIdentifier(
            errorMessage: "Expected member variable name or binding in destructuring pattern");

        if (!CheckAndAdvance(type: TokenType.Colon))
        {
            return new DestructuringBinding(MemberVariableName: name,
                BindingName: name,
                NestedPattern: null,
                Location: bindingLocation);
        }

        if (Check(type: TokenType.LeftParen))
        {
            List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
            return new DestructuringBinding(MemberVariableName: name,
                BindingName: null,
                NestedPattern: new DestructuringPattern(Bindings: nestedBindings,
                    Location: bindingLocation),
                Location: bindingLocation);
        }

        if (CheckAndAdvance(type: TokenType.As))
        {
            return new DestructuringBinding(MemberVariableName: name,
                BindingName: ConsumeIdentifier(errorMessage: "Expected a name after 'as'"),
                NestedPattern: null,
                Location: bindingLocation);
        }

        Pattern fieldPattern;
        if (CheckAndAdvance(type: TokenType.Is))
        {
            fieldPattern = ParseTypePattern();
        }
        else if (CheckAndAdvance(type: TokenType.IsNot))
        {
            fieldPattern = new NegatedTypePattern(Type: ParseType(), Location: bindingLocation);
        }
        else if (IsComparisonOperator(tokenType: CurrentToken.Type))
        {
            fieldPattern = ParseComparisonPattern();
        }
        else
        {
            fieldPattern = new ComparisonPattern(Operator: TokenType.Equal,
                Value: ParseRange(),
                Location: bindingLocation);
        }

        return new DestructuringBinding(MemberVariableName: name,
            BindingName: null,
            NestedPattern: fieldPattern,
            Location: bindingLocation);
    }

    /// <summary>
    /// Checks if the current token is a keyword that should not be treated as an identifier.
    /// Used to prevent parsing keywords as binding names in pattern matching.
    /// </summary>
    private static bool IsKeywordToken(Token token)
    {
        return token.Type switch
        {
            TokenType.And or TokenType.Or or TokenType.Not or TokenType.Is or TokenType.IsNot
                or TokenType.In or TokenType.NotIn or TokenType.Obeys
                or TokenType.If or TokenType.Else or TokenType.While or TokenType.Each
                or TokenType.Return or TokenType.Throw or TokenType.Pierce or TokenType.Todo or TokenType.When
                or TokenType.Then or TokenType.To or TokenType.Til or TokenType.By => true,
            _ => false
        };
    }

    /// <summary>
    /// Parses bitwise expressions: <c>&lt;&lt; &gt;&gt; &gt;&gt;&gt; &amp; ^ |</c> on one level, left-associative, looser than
    /// arithmetic (<c>1 &lt;&lt; n + 1</c> shifts by <c>n + 1</c>) and tighter than ranges and comparisons
    /// (<c>x &amp; 1 == 0</c> is <c>(x &amp; 1) == 0</c>). Different bitwise operators side by side need parentheses
    /// (<c>(a &amp; b) | c</c>), a chain of one of them does not (<c>a | b | c</c>).
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseBitwise()
    {
        Expression expr = ParseAdditive();
        Token? first = null;

        while (Check(TokenType.Pipe, TokenType.Caret, TokenType.Ampersand, TokenType.LeftShift,
                   TokenType.RightShift, TokenType.LogicalRightShift))
        {
            Token op = CurrentToken;
            if (first != null && op.Type != first.Type)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.MixedOperatorsNeedParentheses,
                    message: $"You put '{first.Text}' and '{op.Text}' side by side without parentheses. Different " +
                             "bitwise operators share one level: group the part that goes together, as in " +
                             "'(a & b) | c'.");
            }

            first ??= op;
            Advance();
            Expression right = ParseAdditive();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses additive expressions.
    /// Syntax: <c>a + b</c>, <c>a - b</c>, and overflow variants (+%, +^, -%, -^).
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseAdditive()
    {
        Expression expr = ParseMultiplicative();

        while (CheckAndAdvance(TokenType.Plus,
                   TokenType.Minus,
                   TokenType.PlusWrap,
                   TokenType.PlusClamp,
                   TokenType.MinusWrap,
                   TokenType.MinusClamp,
                   TokenType.PlusUnchecked,
                   TokenType.MinusUnchecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseMultiplicative();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses multiplicative expressions.
    /// Syntax: <c>a * b</c>, <c>a / b</c>, <c>a % b</c>, and overflow variants.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseMultiplicative()
    {
        Expression expr = ParseUnary();

        while (CheckAndAdvance(TokenType.Star,
                   TokenType.Slash,
                   TokenType.Percent,
                   TokenType.Divide,
                   TokenType.MultiplyWrap,
                   TokenType.MultiplyClamp,
                   TokenType.SlashClamp,
                   TokenType.MultiplyUnchecked,
                   TokenType.SlashUnchecked,
                   TokenType.DivideUnchecked,
                   TokenType.PercentUnchecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseUnary();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses power/exponentiation expressions.
    /// Syntax: <c>a ** b</c> and overflow variants (**%, **^).
    /// Power is right-associative: <c>a ** b ** c</c> = <c>a ** (b ** c)</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParsePower()
    {
        return ContinuePower(baseExpr: ParsePostfix());
    }

    /// <summary>Applies a <c>**</c> after <paramref name="baseExpr"/>, if one follows: right-associative, and the
    /// exponent may carry a sign (<c>2 ** -1</c>).</summary>
    private Expression ContinuePower(Expression baseExpr)
    {
        Expression expr = baseExpr;

        if (CheckAndAdvance(TokenType.Power,
                TokenType.PowerWrap,
                TokenType.PowerClamp,
                TokenType.PowerUnchecked))
        {
            Token op = PeekToken(offset: -1);
            // Right-associative, and the exponent may carry a sign: `2 ** -1`.
            Expression right = ParseUnary();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses unary prefix expressions.
    /// Syntax: <c>-x</c> (negation), <c>not x</c> (logical not), <c>~x</c> (bitwise not), <c>^n</c> (backindex).
    /// Special handling for unary minus on numeric literals to support min values.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseUnary()
    {
        // Recovery prefix keywords (try/check/lookup): wrap a failable CALL into a carrier. MILESTONE
        // lowering — rewrite the wrapped call's name to the existing generated recovery variant:
        //   try foo(a)      -> foo's try variant    (Maybe[T])
        //   check x.foo()    -> x.foo's check variant (Check[T])
        // Bare-word keywords only (`try_foo` etc. tokenize as single identifiers). Whole-expression
        // monadic composition + a dedicated RecoveryExpression node replace this rewrite later.
        if (CheckAndAdvance(TokenType.Try, TokenType.Check))
        {
            return ParseRecoveryPrefix();
        }

        // `agent f(x)`: the whole following call chain is deferred into an Agent, as `try` takes the whole
        // expression after it: `agent serve(s).start_now()` defers `start_now()` too, so a caller that starts it
        // at once writes `(agent serve(s)).start_now()`.
        if (CheckAndAdvance(type: TokenType.Agent))
        {
            SourceLocation agentLocation = GetLocation(token: PeekToken(offset: -1));
            return new AgentExpression(Inner: ParsePostfix(), Location: agentLocation);
        }

        // Handle steal expression (steal expr = ownership transfer, RazorForge only)
        if (CheckAndAdvance(type: TokenType.Steal))
        {
            SourceLocation stealLocation = GetLocation(token: PeekToken(offset: -1));
            Expression operand = ParseUnary(); // Right-associative
            return new StealExpression(Operand: operand, Location: stealLocation);
        }

        // Handle backindex operator (^n = index from end)
        if (CheckAndAdvance(type: TokenType.Caret))
        {
            SourceLocation caretLocation = GetLocation(token: PeekToken(offset: -1));
            Expression operand = ParseUnary(); // Right-associative
            return new BackIndexExpression(Operand: operand, Location: caretLocation);
        }

        if (!CheckAndAdvance(TokenType.Minus, TokenType.Not, TokenType.Tilde))
        {
            return ParsePower();
        }

        Token op = PeekToken(offset: -1);
        SourceLocation opLocation = GetLocation(token: op);

        // Special handling for unary minus on numeric literals
        // This allows parsing negative min values like -9_223_372_036_854_775_808_s64
        // All numeric literals are now strings, so we prepend "-" to the string
        if (op.Type == TokenType.Minus && Check(TokenType.UndecidedInteger,
                TokenType.UndecidedDecimal,
                TokenType.S8Literal,
                TokenType.S16Literal,
                TokenType.S32Literal,
                TokenType.S64Literal,
                TokenType.S128Literal,
                TokenType.U8Literal,
                TokenType.U16Literal,
                TokenType.U32Literal,
                TokenType.U64Literal,
                TokenType.U128Literal,
                TokenType.S256Literal,
                TokenType.U256Literal,
                TokenType.AddressLiteral,
                TokenType.IntegerLiteral,
                TokenType.B16Literal,
                TokenType.B32Literal,
                TokenType.B64Literal,
                TokenType.B128Literal,
                TokenType.D32Literal,
                TokenType.D64Literal,
                TokenType.D128Literal,
                TokenType.DecimalLiteral,
                TokenType.ImaginaryLiteral))
        {
            // The literal alone, then any postfix steps on it. Only a literal with nothing after it folds the sign
            // in (`-5` is one literal); `-5.abs()` is the minus of `5.abs()`, as unary minus binds looser than a
            // postfix step. Parsing the whole postfix chain first and then falling through to ParseUnary dropped the
            // call, since ParseUnary started after it.
            Expression literal = ParsePrimary();
            Expression chained = literal;
            while (TryParsePostfixStep(expr: ref chained))
            {
            }

            // `**` binds tighter than the sign: `-2 ** 2` is `-(2 ** 2)`, so the literal keeps no sign of its own.
            if (Check(TokenType.Power, TokenType.PowerWrap, TokenType.PowerClamp, TokenType.PowerUnchecked))
            {
                return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type),
                    Operand: ContinuePower(baseExpr: chained),
                    Location: opLocation);
            }

            if (!ReferenceEquals(objA: chained, objB: literal))
            {
                return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type),
                    Operand: chained,
                    Location: opLocation);
            }

            // If it's a literal expression with string value, prepend negative sign. `-inf` and `-nan` stay a negation:
            // the value is the number's negation in whatever type `inf` takes.
            if (literal is LiteralExpression { Value: string strVal } litExpr && strVal is not ("inf" or "nan"))
            {
                // Toggle negative sign: if already negative, remove it; otherwise add it
                string newValue = strVal.StartsWith(value: '-')
                    ? strVal[1..]
                    : "-" + strVal;
                return new LiteralExpression(Value: newValue,
                    LiteralType: litExpr.LiteralType,
                    Location: opLocation);
            }

            // The literal is already consumed: whatever it is, it is the operand.
            return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type),
                Operand: literal,
                Location: opLocation);
        }

        Expression expr = ParseUnary();
        return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type),
            Operand: expr,
            Location: opLocation);
    }

    /// <summary>
    /// Parses a recovery-prefix expression (try/check/lookup) after the keyword has been consumed.
    /// Rewrites the wrapped failable expression into a <see cref="RecoveryExpression"/> carrier,
    /// re-applying any trailing force-unwraps on top of the carrier.
    /// </summary>
    /// <returns>The parsed recovery expression.</returns>
    private Expression ParseRecoveryPrefix()
    {
        Token recoveryKw = PeekToken(offset: -1);
        RecoveryKind recoveryKind = recoveryKw.Type switch
        {
            TokenType.Check => RecoveryKind.Check,
            _ => RecoveryKind.Try
        };
        // The keyword takes the WHOLE following expression (down to — but not including — `??`, which
        // ParseNoneCoalesce sits above and thus applies to the recovery RESULT). ParseLogicalOr captures
        // arithmetic/comparison/range/logical operators so `try a + b` recovers the checked-arith overflow
        // as a whole-expression composition (`try (a + b)`, not `(try a) + b`). SA's AnalyzeRecoveryExpression
        // hoists every failable sub-call AND checked-arith operator inside it.
        Expression recoveryInner = ParseLogical();

        // A trailing force-unwrap binds LOOSER than the recovery keyword: `try route()!!` means
        // `(try route())!!`, not `try (route()!!)`. The keyword PRODUCES a carrier and `!!` CONSUMES it,
        // so `!!` applies to the recovery RESULT. ParseUnary already folded the `!!` onto the inner
        // (`ForceUnwrap(route())`), peel those TOP-LEVEL force-unwraps off, recover the inner, then re-apply
        // them on top of the RecoveryExpression. (`??` needs no such handling — it is a binary operator
        // above ParseLogicalOr, so it already applies to the recovery result.)
        var forceUnwraps = new List<SourceLocation>();
        Expression callSpine = recoveryInner;
        while (callSpine is UnaryExpression
               {
                   Operator: UnaryOperator.ForceUnwrap, Operand: { } unwrapped
               } fu)
        {
            forceUnwraps.Add(item: fu.Location);
            callSpine = unwrapped;
        }

        // No CallExpression requirement: the inner may be any expression (a bare call, a member chain, or
        // a checked-arith composition like `a + b`). An operand with no failable call or operator degenerates
        // to an always-present carrier in SA — harmless, and keeping the surface uniform.
        // `try agent f()` recovers inside the deferred work: an Agent over the recovery (`Agent[Maybe[T]]`).
        Expression recovery = callSpine is AgentExpression agent
            ? agent with
            {
                Inner = new RecoveryExpression(Kind: recoveryKind,
                    Inner: agent.Inner,
                    Location: GetLocation(token: recoveryKw))
            }
            : new RecoveryExpression(Kind: recoveryKind,
                Inner: callSpine,
                Location: GetLocation(token: recoveryKw));

        // Re-apply the peeled `!!`s outermost-last so `try route()!!` → `(try route())!!`.
        for (int i = forceUnwraps.Count - 1; i >= 0; i--)
        {
            recovery = new UnaryExpression(Operator: UnaryOperator.ForceUnwrap,
                Operand: recovery,
                Location: forceUnwraps[index: i]);
        }

        return recovery;
    }
}
