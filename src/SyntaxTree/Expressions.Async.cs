namespace SyntaxTree;

#region Async Expressions

/// <summary>
/// <c>f.to[Work](...)</c>: computes the arguments of the call chain <paramref name="Inner"/> now and defers the calls
/// into a <c>Work[T]</c> that a verb starts (<c>start_now()</c>, <c>start_here()</c>, <c>start_apart()</c>, or
/// <c>retrieve()</c>, which starts it with <c>start_now()</c>). <c>try job f()</c> / <c>check job f()</c> parse
/// as a job over the recovery, so the failure is caught inside the work.
/// </summary>
/// <param name="Inner">The deferred call chain.</param>
/// <param name="Location">Source location of the <c>to[Work]</c> conversion.</param>
public record WorkExpression(Expression Inner, SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitWorkExpression(node: this);
    }

    /// <summary>
    /// The analyzed lowering: a call of the routine that builds this Work's recipe from the chain's arguments.
    /// Every pass after analysis sees this call in the node's place. Null until analyzed.
    /// </summary>
    public Expression? LoweredCall { get; set; }

    /// <summary>
    /// Why this Work may only run on the thread that made it: an argument that only one thread may use (a token,
    /// a <c>Retained</c>), described for a message. Such a Work is started with <c>start_here()</c> and stays in
    /// the routine that made it (RF-S632 otherwise). Null when any verb may start it.
    /// </summary>
    public string? OneThreadArgument { get; set; }
}

#endregion

#region Back Index Expressions

/// <summary>
/// Expression that creates an index from the end of a sequence.
/// The ^ prefix operator creates a back-index value that counts from the end.
/// </summary>
/// <param name="Operand">The index offset from the end (^1 = last, ^2 = second to last)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Back index expression examples:
/// <list type="bullet">
/// <item>^1 - last element</item>
/// <item>^2 - second to last element</item>
/// <item>list[^1] - access last element of list</item>
/// <item>text[^3] - access third to last character</item>
/// </list>
/// The operand must be a non-negative integer. ^0 is valid but refers to "one past the end".
/// Used with IndexExpression for end-relative indexing.
/// </remarks>
public record BackIndexExpression(Expression Operand, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitBackIndexExpression(node: this);
    }
}

#endregion

#region Pattern Matching Expressions

/// <summary>
/// Expression that performs pattern matching with 'is' or 'isnot' operators.
/// Supports type checking, variable binding, and destructuring.
/// </summary>
/// <param name="Expression">The expression being matched against the pattern</param>
/// <param name="Pattern">The pattern to match (TypePattern, DestructuringPattern, etc. from Statements.cs)</param>
/// <param name="IsNegated">True for 'isnot', false for 'is'</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Pattern matching expression examples:
/// <list type="bullet">
/// <item>value is s32 - type check</item>
/// <item>value is Text t - type check with binding</item>
/// <item>value is Point (x, y) - destructuring</item>
/// <item>value isnot None - negated type check</item>
/// </list>
/// Note: 'isnot' cannot have bindings or destructuring (what would you bind to if not matched?).
/// </remarks>
public record IsPatternExpression(
    Expression Expression,
    Pattern Pattern,
    bool IsNegated,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitIsPatternExpression(node: this);
    }
}

/// <summary>
/// Pattern matching expression that evaluates to a value based on pattern matching.
/// Similar to Rust's match expression or Kotlin's when expression.
/// </summary>
/// <param name="Expression">The value being matched against patterns</param>
/// <param name="Clauses">The list of pattern-body pairs to evaluate</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// When expression examples:
/// <list type="bullet">
/// <item>return when x { 0 => "zero", 1 => "one", else => "many" }</item>
/// <item>var desc = when status { is ACTIVE => "Running", else => "Not running" }</item>
/// </list>
/// The body of each clause must evaluate to a value of the same type.
/// </remarks>
public record WhenExpression(
    Expression? Expression,
    List<WhenClause> Clauses,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitWhenExpression(node: this);
    }
}

#endregion
