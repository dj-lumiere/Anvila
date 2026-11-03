using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Desugaring.Passes;

/// <summary>
/// The loops given a name with <c>as</c> (<c>for y in 0 til h as rows</c>) and the jumps that name them
/// (<c>break to rows</c>, <c>continue to rows</c>), turned into plain <c>break</c> and <c>continue</c>.
/// <para>
/// A jump written straight in the named loop's body (not inside a loop of its own) is the plain statement. A jump from
/// inside loops nested in it sets a flag, leaves the innermost loop with a plain <c>break</c>, and every loop between
/// that one and the named loop starts its <c>then</c> block with a test of the flag, so the <c>then</c> blocks of those
/// loops are skipped and the named loop ends (<c>break to</c>) or goes on with its next round (<c>continue to</c>)
/// exactly as its own statement would.
/// </para>
/// </summary>
internal static class NamedLoopLowering
{
    private static int _next;

    /// <summary>Whether <paramref name="loop"/> is a loop with a name, which this lowering takes off.</summary>
    internal static bool IsNamed(Statement loop)
    {
        return loop is ForStatement { Name: not null } or WhileStatement { Name: not null } or LoopStatement { Name: not null };
    }

    /// <summary>The loop without its name, its jumps lowered, inside a block that declares the flags the jumps from
    /// nested loops use.</summary>
    internal static Statement Lower(Statement loop)
    {
        string name = loop switch
        {
            ForStatement f => f.Name!,
            WhileStatement w => w.Name!,
            LoopStatement l => l.Name!,
            _ => throw new InvalidOperationException(message: "Not a named loop")
        };
        List<CarriedValue>? carried = loop switch
        {
            ForStatement f => f.With,
            WhileStatement w => w.With,
            LoopStatement l => l.With,
            _ => null
        };
        int id = Interlocked.Increment(location: ref _next);
        var rewriter = new JumpRewriter(name: name, id: id, carried: carried);
        Statement body = loop switch
        {
            ForStatement f => f.Body,
            WhileStatement w => w.Body,
            LoopStatement l => l.Body,
            _ => throw new InvalidOperationException(message: "Not a named loop")
        };
        Statement newBody = rewriter.VisitStatement(stmt: body);
        Statement unnamed = loop switch
        {
            ForStatement f => f with { Name = null, Body = newBody },
            WhileStatement w => w with { Name = null, Body = newBody },
            LoopStatement l => l with { Name = null, Body = newBody },
            _ => loop
        };
        if (!rewriter.UsesBreakFlag && !rewriter.UsesContinueFlag)
        {
            return unnamed;
        }

        SourceLocation at = loop.Location;
        var statements = new List<Statement>();
        if (rewriter.UsesBreakFlag)
        {
            statements.Add(item: Flag(name: rewriter.BreakFlag, at: at));
        }

        if (rewriter.UsesContinueFlag)
        {
            statements.Add(item: Flag(name: rewriter.ContinueFlag, at: at));
        }

        statements.Add(item: unnamed);
        return new BlockStatement(Statements: statements, Location: at);
    }

    private static DeclarationStatement Flag(string name, SourceLocation at)
    {
        return new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: name,
                Type: new TypeExpression(Name: "Bool", GenericArguments: null, Location: at),
                Initializer: Literal(value: false, at: at),
                Visibility: VisibilityModifier.Secret,
                Location: at),
            Location: at);
    }

    private static LiteralExpression Literal(bool value, SourceLocation at)
    {
        return new LiteralExpression(Value: value,
            LiteralType: value ? TokenType.True : TokenType.False,
            Location: at);
    }

    private static AssignmentStatement SetFlag(string name, bool value, SourceLocation at)
    {
        return new AssignmentStatement(Target: new IdentifierExpression(Name: name, Location: at),
            Value: Literal(value: value, at: at),
            Location: at);
    }

    private sealed class JumpRewriter(string name, int id, List<CarriedValue>? carried) : AstRewriter
    {
        private int _depth;
        private int _breakJumps;
        private int _continueJumps;
        private int _temps;

        public string BreakFlag { get; } = $"_lf_break_{id}";

        public string ContinueFlag { get; } = $"_lf_next_{id}";

        public bool UsesBreakFlag { get; private set; }

        public bool UsesContinueFlag { get; private set; }

        public override Statement VisitStatement(Statement stmt)
        {
            switch (stmt)
            {
                case BreakStatement { TargetName: { } target } b when target == name:
                    if (_depth == 0)
                    {
                        return b with { TargetName = null };
                    }

                    UsesBreakFlag = true;
                    _breakJumps++;
                    return new BlockStatement(Statements:
                        [SetFlag(name: BreakFlag, value: true, at: b.Location), new BreakStatement(Location: b.Location)],
                        Location: b.Location);
                case ContinueStatement { TargetName: { } target } c when target == name:
                    if (_depth == 0)
                    {
                        return c with { TargetName = null };
                    }

                    UsesContinueFlag = true;
                    _continueJumps++;
                    var statements = new List<Statement>();
                    if (c.With != null)
                    {
                        statements.AddRange(collection: Stores(with: c.With, at: c.Location));
                    }

                    statements.Add(item: SetFlag(name: ContinueFlag, value: true, at: c.Location));
                    statements.Add(item: new BreakStatement(Location: c.Location));
                    return new BlockStatement(Statements: statements, Location: c.Location);
                default:
                    return base.VisitStatement(stmt: stmt);
            }
        }

        /// <summary>The next values of the named loop's carried values, all computed before any is stored (so
        /// <c>continue to rows with a: b, b: a</c> swaps).</summary>
        private List<Statement> Stores(List<(string Name, Expression Value, SourceLocation Location)> with, SourceLocation at)
        {
            var statements = new List<Statement>();
            var temps = new List<(string Name, string Temp)>();
            foreach ((string valueName, Expression value, SourceLocation where) in with)
            {
                CarriedValue c = carried!.First(predicate: k => k.Name == valueName);
                string temp = $"_lf_value_{id}_{_temps++}";
                statements.Add(item: new DeclarationStatement(
                    Declaration: new VariableDeclaration(Name: temp,
                        Type: c.Type,
                        Initializer: value,
                        Visibility: VisibilityModifier.Secret,
                        Location: where),
                    Location: where));
                temps.Add(item: (valueName, temp));
            }

            foreach ((string valueName, string temp) in temps)
            {
                statements.Add(item: new AssignmentStatement(Target: new IdentifierExpression(Name: valueName, Location: at),
                    Value: new IdentifierExpression(Name: temp, Location: at),
                    Location: at));
            }

            return statements;
        }

        protected override Statement VisitFor(ForStatement s)
        {
            return VisitNested(loop: s, body: s.Body, then: s.ThenBranch,
                rebuild: (b, t) => s with { Body = b, ThenBranch = t });
        }

        protected override Statement VisitWhile(WhileStatement s)
        {
            return VisitNested(loop: s, body: s.Body, then: s.ThenBranch,
                rebuild: (b, t) => s with { Body = b, ThenBranch = t });
        }

        protected override Statement VisitLoop(LoopStatement s)
        {
            return VisitNested(loop: s, body: s.Body, then: s.ThenBranch,
                rebuild: (b, t) => s with { Body = b, ThenBranch = t });
        }

        /// <summary>A loop inside the named one: its body is one level deeper, its <c>then</c> block is not, and a
        /// jump to the named loop from inside the body makes the <c>then</c> block start with the test of the flag.</summary>
        private Statement VisitNested(Statement loop, Statement body, Statement? then,
            Func<Statement, Statement?, Statement> rebuild)
        {
            int breaksBefore = _breakJumps;
            int continuesBefore = _continueJumps;
            _depth++;
            Statement newBody = VisitStatement(stmt: body);
            int level = _depth;
            _depth--;
            Statement? newThen = then == null ? null : VisitStatement(stmt: then);
            bool breaks = _breakJumps != breaksBefore;
            bool continues = _continueJumps != continuesBefore;
            if (breaks || continues)
            {
                SourceLocation at = loop.Location;
                var checks = new List<Statement>();
                if (breaks)
                {
                    checks.Add(item: new IfStatement(Condition: new IdentifierExpression(Name: BreakFlag, Location: at),
                        ThenStatement: new BlockStatement(Statements: [new BreakStatement(Location: at)], Location: at),
                        ElseStatement: null,
                        Location: at));
                }

                if (continues)
                {
                    // A loop right inside the named one goes on with the named loop's next round, a deeper one leaves
                    // itself and keeps the flag for the loop around it.
                    List<Statement> leave = level == 1
                        ? [SetFlag(name: ContinueFlag, value: false, at: at), new ContinueStatement(Location: at)]
                        : [new BreakStatement(Location: at)];
                    checks.Add(item: new IfStatement(Condition: new IdentifierExpression(Name: ContinueFlag, Location: at),
                        ThenStatement: new BlockStatement(Statements: leave, Location: at),
                        ElseStatement: null,
                        Location: at));
                }

                if (newThen != null)
                {
                    checks.Add(item: newThen);
                }

                newThen = new BlockStatement(Statements: checks, Location: at);
            }

            return rebuild(arg1: newBody, arg2: newThen);
        }
    }
}
