using Builder.Instantiation;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Desugaring.Passes;

/// <summary>
/// Lowers all loop constructs to the <see cref="LoopStatement"/> primitive so the code
/// generator only needs to handle one loop form via <c>EmitLoop</c>.
///
/// <para><b>until</b> (kept as a loop on its negated condition) -> loop+if-break:</para>
/// <code>
/// until c  (cond = not c) { body }
///  ??
/// loop { if !cond { break }; body }
/// </code>
///
/// <para><b>for v in iterable</b> -> loop+when:</para>
/// <code>
///  {
/// var _lf_iter_N = iterable.iter()
/// loop { when try _lf_iter_N.emit() { is None -> break; else var v -> body } }
/// }
/// </code>
///
/// <para><b>for (a, b) in pairs</b> (tuple destructuring) -> same loop shape, else
/// body prepends positional member-access bindings:</para>
/// <code>
///  {
/// var _lf_iter_N = pairs.iter()
/// loop {
/// when try _lf_iter_N.emit() {
/// is None -> break
/// else var _lf_elem_M -> { var a = _lf_elem_M.item0; var b = _lf_elem_M.item1; body }
///  }
///  }
/// }
/// </code>
///
/// <para>Range-based loops (<c>for x in 0 to n</c>) are also covered: the
/// <c>RangeExpression</c> iterable is converted to <c>Range[T](...)</c> by
/// <see cref="Builder.Lowering.Passes.ExpressionLoweringPass"/> (which runs after this pass). Such a loop is marked
/// (<see cref="LoopStatement.RangeWalk"/>), and analysis replaces the walk with <see cref="RangeCounterLoop"/> when
/// the range's element type is an integer. A loop this pass lowers after analysis (a library body, a recovery
/// variant, a made-concrete body) counts right away (<see cref="TypedRangeCounterLoop"/>).</para>
/// </summary>
internal sealed class ControlFlowLoweringPass(DesugaringContext ctx)
{
    /// <summary>
    /// Tracks the iter count while this builder phase runs.
    /// </summary>
    private int _iterCount;

    /// <summary>
    /// Runs this builder phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[index: i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody = LowerStatement(stmt: r.Body);
                    if (!ReferenceEquals(objA: newBody, objB: r.Body))
                    {
                        program.Declarations[index: i] = r with { Body = newBody };
                    }

                    break;
                }

                case EntityDeclaration e:
                    LowerMemberList(members: e.Members);
                    break;

                case RecordDeclaration rec:
                    LowerMemberList(members: rec.Members);
                    break;

                case CrashableDeclaration cr:
                    LowerMemberList(members: cr.Members);
                    break;
            }
        }
    }

    /// <summary>
    /// Lower member list as part of this builder phase.
    /// </summary>
    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[index: j] is not RoutineDeclaration m)
            {
                continue;
            }

            Statement newBody = LowerStatement(stmt: m.Body);
            if (!ReferenceEquals(objA: newBody, objB: m.Body))
            {
                members[index: j] = m with { Body = newBody };
            }
        }
    }

    /// <summary>
    /// Lower statement as part of this builder phase.
    /// </summary>
    private Statement LowerStatement(Statement stmt)
    {
        return stmt switch
        {
            // A loop given a name with `as`: its `break to` and `continue to` become plain jumps and flags first.
            _ when NamedLoopLowering.IsNamed(loop: stmt) => LowerStatement(stmt: NamedLoopLowering.Lower(loop: stmt)),

            // A loop with carried values is a block: the values as variables, then the loop, whose `continue with`
            // stores the next values. The `then` block, inside the same block, still sees them.
            WhileStatement { With.Count: > 0 } w => LowerCarried(loop: w with { With = null }, carried: w.With, body: w.Body,
                rebuild: b => w with { With = null, Body = b, HadWith = true }),
            LoopStatement { With.Count: > 0 } l => LowerCarried(loop: l with { With = null }, carried: l.With, body: l.Body,
                rebuild: b => l with { With = null, Body = b }),
            ForStatement { With.Count: > 0 } f => LowerCarried(loop: f with { With = null }, carried: f.With, body: f.Body,
                rebuild: b => f with { With = null, Body = b }),
            ForStatement { ThenBranch: { } then } f => LowerLoopThen(loop: f with { ThenBranch = null }, then: then),
            WhileStatement { ThenBranch: { } then } w => LowerLoopThen(loop: w with { ThenBranch = null }, then: then),
            LoopStatement { ThenBranch: { } then } l => LowerLoopThen(loop: l with { ThenBranch = null }, then: then),
            ForStatement f => LowerFor(forStmt: f),
            DestructuringStatement ds => LowerDestructuring(destruct: ds),
            BlockStatement b => LowerBlock(b: b),
            WhileStatement w => LowerWhile(whileStmt: w),
            LoopStatement loop => LowerLoop(loop: loop),
            IfStatement ifs => LowerIf(ifs: ifs),
            WhenStatement w => LowerWhen(w: w),
            UsingStatement u => LowerUsing(u: u),
            AccessStatement a => LowerAccess(a: a),
            DangerStatement d => LowerDanger(d: d),
            AssignmentStatement { Target: TupleLiteralExpression left, Value: TupleLiteralExpression right } assign
                when SwapSources(left: left.Elements, right: right.Elements) is { } sources =>
                Swap(left: left.Elements, right: right.Elements, sources: sources, loc: assign.Location),
            ExpressionStatement
                {
                    Expression: BinaryExpression
                    {
                        Operator: BinaryOperator.Assign, Left: TupleLiteralExpression left,
                        Right: TupleLiteralExpression right
                    }
                } written when SwapSources(left: left.Elements, right: right.Elements) is { } sources =>
                Swap(left: left.Elements, right: right.Elements, sources: sources, loc: written.Location),
            _ => stmt
        };
    }

    private BlockStatement LowerBlock(BlockStatement b)
    {
        bool changed = false;
        var stmts = new List<Statement>(capacity: b.Statements.Count);
        foreach (Statement s in b.Statements)
        {
            Statement n = LowerStatement(stmt: s);
            // Splice DestructuringStatement's lowered block into the parent scope
            // so its `var a = ...; var b = ...;` bindings are visible to later siblings.
            if (s is DestructuringStatement && n is BlockStatement ds)
            {
                stmts.AddRange(collection: ds.Statements);
            }
            else
            {
                stmts.Add(item: n);
            }

            if (!ReferenceEquals(objA: n, objB: s))
            {
                changed = true;
            }
        }

        return changed
            ? b with { Statements = stmts }
            : b;
    }

    private LoopStatement LowerLoop(LoopStatement loop)
    {
        Statement body = LowerStatement(stmt: loop.Body);
        if (ReferenceEquals(objA: body, objB: loop.Body))
        {
            return loop;
        }

        return loop with { Body = body };
    }

    private Statement LowerIf(IfStatement ifs)
    {
        // `if x is T p [and guard...] { then } [else]` binds `p` for the then-branch only.
        // Desugar to a `when` so the whole binding/scope/codegen path is reused from
        // when-clauses (the binding is then-branch-scoped for free). Non-binding `if`s
        // (plain bool, `isnot`, `is T` without a name) fall through unchanged.
        if (TryLowerIfPatternBinding(ifs: ifs, result: out Statement? whenStmt))
        {
            return whenStmt!;
        }

        Statement then = LowerStatement(stmt: ifs.ThenStatement);
        Statement? elseS = ifs.ElseStatement != null
            ? LowerStatement(stmt: ifs.ElseStatement)
            : null;
        bool tc = !ReferenceEquals(objA: then, objB: ifs.ThenStatement);
        bool ec = !ReferenceEquals(objA: elseS, objB: ifs.ElseStatement);
        return tc || ec
            ? ifs with { ThenStatement = then, ElseStatement = elseS }
            : ifs;
    }

    private WhenStatement LowerWhen(WhenStatement w)
    {
        bool changed = false;
        var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
        foreach (WhenClause c in w.Clauses)
        {
            Statement body = LowerStatement(stmt: c.Body);
            if (!ReferenceEquals(objA: body, objB: c.Body))
            {
                clauses.Add(item: c with { Body = body });
                changed = true;
            }
            else
            {
                clauses.Add(item: c);
            }
        }

        return changed
            ? w with { Clauses = clauses }
            : w;
    }

    private UsingStatement LowerUsing(UsingStatement u)
    {
        Statement body = LowerStatement(stmt: u.Body);
        return !ReferenceEquals(objA: body, objB: u.Body)
            ? u with { Body = body }
            : u;
    }

    private AccessStatement LowerAccess(AccessStatement a)
    {
        Statement body = LowerStatement(stmt: a.Body);
        return !ReferenceEquals(objA: body, objB: a.Body)
            ? a with { Body = body }
            : a;
    }

    private DangerStatement LowerDanger(DangerStatement d)
    {
        // DangerStatement.Body is BlockStatement; LowerStatement on BlockStatement
        // always returns a BlockStatement so the cast is safe.
        Statement lowered = LowerStatement(stmt: d.Body);
        return !ReferenceEquals(objA: lowered, objB: d.Body)
            ? d with { Body = (BlockStatement)lowered }
            : d;
    }

    /// <summary>
    /// Lowers <c>while cond { body }</c> to <c>loop { if !cond { break } body }</c>.
    /// </summary>
    /// <summary>Whether a loop that runs while <paramref name="condition"/> holds never ends by itself: <c>until
    /// false</c>, kept as <c>not false</c>.</summary>
    internal static bool IsEndless(Expression condition)
    {
        return condition is LiteralExpression { LiteralType: TokenType.True } or UnaryExpression
        {
            Operator: UnaryOperator.Not, Operand: LiteralExpression { LiteralType: TokenType.False }
        };
    }

    private Statement LowerWhile(WhileStatement whileStmt)
    {
        SourceLocation loc = whileStmt.Location;
        Statement loweredBody = LowerStatement(stmt: whileStmt.Body);
        // A literal condition is kept for analysis, which notes what such a loop means.
        WhileStatement? literal = whileStmt.Condition is LiteralExpression { Value: bool } or UnaryExpression
        {
            Operator: UnaryOperator.Not, Operand: LiteralExpression { Value: bool }
        }
            ? whileStmt
            : null;

        // `until false` needs no guard: it ends only at a `break`, which is also what lets the analysis see that a
        // variable given its value before that `break` has one after the loop.
        if (IsEndless(condition: whileStmt.Condition))
        {
            return new LoopStatement(Body: loweredBody, Location: loc) { WrittenWhile = literal };
        }

        // Build: if !cond { break }. An `until c` is kept as a loop on `not (c)`, so its guard is `if c { break }`
        // itself: the analysis then narrows after it as after any guard (`until x is None` leaves `x` non-None in the
        // body).
        Expression negCond = whileStmt.Condition is UnaryExpression { Operator: UnaryOperator.Not } written
            ? written.Operand
            : new UnaryExpression(Operator: UnaryOperator.Not,
                Operand: whileStmt.Condition,
                Location: loc);
        Statement guardBreak = new IfStatement(Condition: negCond,
            ThenStatement: new BlockStatement(Statements:
                [new BreakStatement(Location: loc)],
                Location: loc),
            ElseStatement: null,
            Location: loc);

        // Build: loop { if !cond { break }; body }
        Statement loopBody = loweredBody is BlockStatement block
            ? block with { Statements = [guardBreak, .. block.Statements] }
            : new BlockStatement(Statements: [guardBreak, loweredBody], Location: loc);

        return new LoopStatement(Body: loopBody, Location: loc) { WrittenWhile = literal };
    }

    /// <summary>
    /// <c>(a, b) = (b, a)</c>. Where entities are single-owner, the values move between the places as they are (a
    /// <see cref="PlaceSwapStatement"/>: no copy, no teardown, the owners stay the same). Where entities are shared
    /// handles and records are copied, every value is read into a temporary first and then assigned, which is the
    /// same swap through the ordinary assignment rules.
    /// </summary>
    private Statement Swap(List<Expression> left, List<Expression> right, List<int> sources, SourceLocation loc)
    {
        if (!ctx.Registry.EntitiesAreShared)
        {
            return new PlaceSwapStatement(Places: left, Sources: sources, Location: loc);
        }

        int n = _iterCount++;
        var statements = new List<Statement>(capacity: left.Count * 2);
        for (int i = 0; i < right.Count; i++)
        {
            statements.Add(item: new DeclarationStatement(Declaration: new VariableDeclaration(Name: $"_lf_swap_{n}_{i}",
                    Type: null,
                    Initializer: right[index: i],
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc));
        }

        for (int i = 0; i < left.Count; i++)
        {
            statements.Add(item: new ExpressionStatement(Expression: new BinaryExpression(Left: left[index: i],
                    Operator: BinaryOperator.Assign,
                    Right: new IdentifierExpression(Name: $"_lf_swap_{n}_{i}", Location: loc),
                    Location: loc),
                Location: loc));
        }

        return new BlockStatement(Statements: statements, Location: loc);
    }

    /// <summary>
    /// When <paramref name="right"/> names the places of <paramref name="left"/> in another order (<c>(a, b) =
    /// (b, a)</c>), the index of the left place each right element names, else null.
    /// </summary>
    internal static List<int>? SwapSources(List<Expression> left, List<Expression> right)
    {
        if (left.Count != right.Count || left.Count < 2)
        {
            return null;
        }

        var sources = new List<int>(capacity: right.Count);
        foreach (Expression element in right)
        {
            int at = left.FindIndex(match: place => SamePlace(a: place, b: element));
            if (at < 0 || sources.Contains(item: at))
            {
                return null;
            }

            sources.Add(item: at);
        }

        return sources;
    }

    /// <summary>Whether two expressions name the same place, as written: a name, or a member variable of one.</summary>
    private static bool SamePlace(Expression a, Expression b)
    {
        return (a, b) switch
        {
            (IdentifierExpression x, IdentifierExpression y) => x.Name == y.Name,
            (MemberExpression x, MemberExpression y) => x.MemberName == y.MemberName && SamePlace(a: x.Object, b: y.Object),
            (IndexExpression x, IndexExpression y) => SamePlace(a: x.Object, b: y.Object) && SamePlace(a: x.Index, b: y.Index),
            (LiteralExpression x, LiteralExpression y) => Equals(objA: x.Value, objB: y.Value),
            _ => false
        };
    }

    /// <summary>
    /// A loop with a <c>then</c> block, which runs once whenever the loop is left for the line after it: when the loop
    /// ends by itself and after a <c>break</c>. A <c>return</c> or <c>throw</c> leaves the routine and never reaches
    /// it. So the block is the loop followed by the <c>then</c> lines, inside the block of the carried values when
    /// the loop has any.
    /// </summary>
    private BlockStatement LowerLoopThen(Statement loop, Statement then)
    {
        return new BlockStatement(Statements: [LowerStatement(stmt: loop), LowerStatement(stmt: then)],
            Location: loop.Location);
    }

    /// <summary>
    /// <c>while c with a: T = init</c> … <c>continue with a: v</c>: a block holding <c>var a: T = init</c>, then the loop
    /// with each <c>continue with</c> of its own rounds replaced by stores of the next values (all computed first,
    /// so <c>continue with a: b, b: a</c> swaps) and a bare <c>continue</c>.
    /// </summary>
    private BlockStatement LowerCarried(Statement loop, List<CarriedValue> carried, Statement body,
        Func<Statement, Statement> rebuild)
    {
        SourceLocation loc = loop.Location;
        var statements = new List<Statement>();
        foreach (CarriedValue c in carried)
        {
            statements.Add(item: new DeclarationStatement(
                Declaration: new VariableDeclaration(Name: c.Name,
                    Type: c.Type,
                    Initializer: c.Init,
                    Visibility: VisibilityModifier.Secret,
                    Location: c.Location),
                Location: c.Location));
        }

        int round = _iterCount++;
        Statement passing = new CarryRewriter(carried: carried, round: round).VisitStatement(stmt: body);
        statements.Add(item: LowerStatement(stmt: rebuild(arg: passing)));
        return new BlockStatement(Statements: statements, Location: loc);
    }

    /// <summary>Replaces each <c>continue with</c> of one loop by the stores of the next values and a bare
    /// <c>continue</c>, the loops inside it left alone (their <c>continue</c>s are their own).</summary>
    private sealed class CarryRewriter(List<CarriedValue> carried, int round) : AstRewriter
    {
        private int _seq;

        public override Statement VisitStatement(Statement stmt)
        {
            return stmt switch
            {
                ContinueStatement { With: { } with } c => Stores(with: with, at: c.Location),
                WhileStatement or ForStatement or LoopStatement => stmt,
                _ => base.VisitStatement(stmt: stmt)
            };
        }

        private BlockStatement Stores(List<(string Name, Expression Value, SourceLocation Location)> with, SourceLocation at)
        {
            var statements = new List<Statement>();
            var temps = new List<(string Name, string Temp)>();
            foreach ((string name, Expression value, SourceLocation where) in with)
            {
                CarriedValue c = carried.First(predicate: k => k.Name == name);
                string temp = $"_lf_next_{round}_{_seq++}";
                statements.Add(item: new DeclarationStatement(
                    Declaration: new VariableDeclaration(Name: temp,
                        Type: c.Type,
                        Initializer: value,
                        Visibility: VisibilityModifier.Secret,
                        Location: where),
                    Location: where));
                temps.Add(item: (name, temp));
            }

            foreach ((string name, string temp) in temps)
            {
                statements.Add(item: new AssignmentStatement(Target: new IdentifierExpression(Name: name, Location: at),
                    Value: new IdentifierExpression(Name: temp, Location: at),
                    Location: at));
            }

            statements.Add(item: new ContinueStatement(Location: at));
            return new BlockStatement(Statements: statements, Location: at);
        }
    }

    /// <summary>
    /// Lowers a binding <c>if</c> — <c>if x is T p [and g1 and g2 ...] { then } [else { alt }]</c> —
    /// to a <c>when</c>:
    /// <code>
    /// when x {
    ///   is T p [if g1 and g2 ...] -> then
    ///   else -> alt          // empty block when there is no else
    /// }
    /// </code>
    /// This reuses the when-clause machinery so the pattern binding <c>p</c> is scoped to the
    /// then-branch only (SA declares it inside the clause scope; codegen materialises it via
    /// <c>EmitSwitchArmBinding</c>). Returns false — leaving the <c>if</c> unchanged — unless the
    /// condition's head is a non-negated <c>is</c>-pattern that introduces a binding. Guard operands
    /// on the <c>and</c>-spine (which may reference the binding) become the arm guard; anything under
    /// <c>or</c>/<c>not</c> never reaches here, so a binding never escapes into a negated branch.
    /// </summary>
    private bool TryLowerIfPatternBinding(IfStatement ifs, out Statement? result)
    {
        result = null;

        // Walk the left spine of `and` collecting guard operands, until the head is reached.
        // `x is T p and g1 and g2` parses left-assoc as `((x is T p and g1) and g2)`, so the
        // rightmost guard is seen first — reverse to restore source order.
        var guards = new List<Expression>();
        Expression head = ifs.Condition;
        while (head is BinaryExpression { Operator: BinaryOperator.And } andExpr)
        {
            guards.Add(item: andExpr.Right);
            head = andExpr.Left;
        }

        if (head is not IsPatternExpression { IsNegated: false } ipe)
        {
            return false;
        }

        // Only a pattern that names or tests more qualifies: `is T as p`, or a record pattern `is T(a, hp: 0)`.
        // A bare `is T` (no binding) stays a plain boolean condition.
        bool introducesBinding = ipe.Pattern is TypePattern { VariableName: not null } or TypePattern { Bindings: not null }
            or TypeDestructuringPattern;
        if (!introducesBinding)
        {
            return false;
        }

        SourceLocation loc = ifs.Location;

        Statement then = LowerStatement(stmt: ifs.ThenStatement);
        Statement? elseS = ifs.ElseStatement != null
            ? LowerStatement(stmt: ifs.ElseStatement)
            : null;
        // No else on the original `if` → the fall-through does nothing, but an empty block needs
        // an explicit `nothing` (RV-S211).
        Statement elseBody = elseS ?? new BlockStatement(Statements:
            [new NothingStatement(Location: loc)],
            Location: loc);

        // Guarded form `is T p and g1 and g2 ...`: put the guard test INSIDE the matched arm as a
        // nested `if`, so the arm's binding (`p`) is materialised before the guard reads it. A
        // when-clause `GuardPattern` would instead flatten to `(x is T) and guard`, evaluating the
        // guard before `p` is bound — broken for guards that reference the binding. Guard failure
        // runs the same else path as a type mismatch.
        Statement matchBody = then;
        if (guards.Count > 0)
        {
            guards.Reverse();
            Expression guard = guards[index: 0];
            for (int i = 1; i < guards.Count; i++)
            {
                guard = new BinaryExpression(Left: guard,
                    Operator: BinaryOperator.And,
                    Right: guards[index: i],
                    Location: loc);
            }

            matchBody = new IfStatement(Condition: guard,
                ThenStatement: then,
                ElseStatement: elseBody,
                Location: loc);
        }

        var matchClause = new WhenClause(Pattern: ipe.Pattern, Body: matchBody, Location: loc);
        var elseClause = new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: loc),
            Body: elseBody,
            Location: loc);

        result = new WhenStatement(Expression: ipe.Expression,
            Clauses: [matchClause, elseClause],
            Location: loc);
        return true;
    }

    /// <summary>
    /// Lowers <c>var (a, b, c) = expr</c> to a block:
    /// <code>
    /// var _ld_tmp_N = expr
    /// var a = _ld_tmp_N.item0
    /// var b = _ld_tmp_N.item1
    /// var c = _ld_tmp_N.item2
    /// </code>
    /// Tuple-only: positional bindings map by index to <c>itemI</c>. Wildcards (<c>_</c>) are skipped.
    /// </summary>
    private BlockStatement LowerDestructuring(DestructuringStatement destruct)
    {
        return LowerDestructuring(destruct: destruct, tmpName: $"_ld_tmp_{_iterCount++}");
    }

    /// <summary>The lowering of <see cref="LowerDestructuring(DestructuringStatement)"/> with the temporary named
    /// <paramref name="tmpName"/>: a recovery variant spells a destructuring out the same way, so the failable call
    /// in its initializer becomes a statement of its own the variant passes the failure on from.</summary>
    internal static BlockStatement LowerDestructuring(DestructuringStatement destruct, string tmpName)
    {
        SourceLocation loc = destruct.Location;
        // A stdlib body is analyzed BEFORE this lowering, so its initializer is already typed: carry the
        // tuple's element types onto the accesses built here, or they reach the emitter untyped. User
        // code is lowered before analysis (no type yet) and gets typed by the analyzer instead.
        TypeSymbol? sourceType = destruct.Initializer.ResolvedType;
        var tupleType = sourceType as TupleTypeSymbol;

        var stmts = new List<Statement>(capacity: destruct.Pattern.Bindings.Count + 1)
        {
            new DeclarationStatement(Declaration: new VariableDeclaration(Name: tmpName,
                    Type: destruct.DeclaredType,
                    Initializer: destruct.Initializer,
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc)
        };

        for (int i = 0; i < destruct.Pattern.Bindings.Count; i++)
        {
            DestructuringBinding binding = destruct.Pattern.Bindings[index: i];
            string bindName = binding.BindingName ?? binding.MemberVariableName ?? $"_ld_b{i}";
            if (bindName == "_")
            {
                continue;
            }

            stmts.Add(item: new DeclarationStatement(Declaration: new VariableDeclaration(
                    Name: bindName,
                    Type: null,
                    Initializer: new MemberExpression(
                        Object: new IdentifierExpression(Name: tmpName, Location: loc)
                        {
                            ResolvedType = sourceType
                        },
                        MemberName: $"item{i}",
                        Location: loc)
                    {
                        ResolvedType = tupleType != null && i < tupleType.ElementTypes.Count
                            ? tupleType.ElementTypes[index: i]
                            : null
                    },
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc));
        }

        return new BlockStatement(Statements: stmts, Location: loc);
    }

    /// <summary>
    /// Lower for as part of this builder phase.
    /// </summary>
    /// <summary>Whether <paramref name="expr"/> names a value something already holds: a variable, <c>me</c>, or a
    /// member variable or element reached through one.</summary>
    private static bool IsHeldPlace(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression => true,
            MemberExpression member => IsHeldPlace(expr: member.Object),
            IndexExpression index => IsHeldPlace(expr: index.Object),
            _ => false
        };
    }

    /// <summary>Names each temporary that <paramref name="expr"/> calls a member routine on, innermost first, in
    /// declarations added to <paramref name="into"/>, and returns <paramref name="expr"/> reading those names.</summary>
    private Expression NameTemporaryReceivers(Expression expr, List<Statement> into)
    {
        if (expr is not CallExpression { Callee: MemberExpression { Object: CallExpression or CreatorExpression } member } call)
        {
            return expr;
        }

        Expression receiver = NameTemporaryReceivers(expr: member.Object, into: into);
        string name = $"_lf_src_{_iterCount++}";
        into.Add(item: SourceDeclaration(name: name, value: receiver));
        return call with
        {
            Callee = member with { Object = new IdentifierExpression(Name: name, Location: member.Object.Location) }
        };
    }

    /// <summary>The declaration of a name a <c>for</c> loop gives a temporary it walks.</summary>
    private static DeclarationStatement SourceDeclaration(string name, Expression value)
    {
        return new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: name,
                Type: null,
                Initializer: value,
                Visibility: VisibilityModifier.Secret,
                Location: value.Location),
            Location: value.Location);
    }

    private BlockStatement LowerFor(ForStatement forStmt, string walkRoutine = Declaration.RuntimeContract.Iter,
        RangeExpression? rangeLiteral = null)
    {
        // Lowered after analysis, a loop over an integer range literal counts right away (nothing analyzes it later).
        if (TypedRangeCounterLoop(forStmt: forStmt) is { } counted)
        {
            return counted;
        }

        // A loop over a value nothing holds (`for w in make()`, `for w in text.words()`) walks a temporary
        // the iterator only borrows: name it in a block around the loop, so it lives until the loop is done
        // and is released when the block ends. (Before analysis only: afterwards the name would carry no type.)
        if (forStmt.Iterable.ResolvedType == null && !IsHeldPlace(expr: forStmt.Iterable))
        {
            SourceLocation srcLoc = forStmt.Location;
            // A temporary receiver in the chain (`text.words()` in `text.words().enumerate()`) is named too: the
            // adapter keeps only a pointer to it.
            var sourceDecls = new List<Statement>();
            Expression source = NameTemporaryReceivers(expr: forStmt.Iterable, into: sourceDecls);
            string sourceName = $"_lf_src_{_iterCount++}";
            // `for x in steal xs`: the loop takes `xs` and hands each element over (`take_each`), not a token on it.
            bool takes = source is StealExpression;
            DeclarationStatement sourceDecl = SourceDeclaration(name: sourceName, value: source);
            // `for i: S64 in 1 to n`: the range local is a `Range[S64]`, so its bare literal bounds take that type.
            if (forStmt.VariableType is { } elementType && source is RangeExpression)
            {
                sourceDecl = sourceDecl with
                {
                    Declaration = ((VariableDeclaration)sourceDecl.Declaration) with
                    {
                        Type = new TypeExpression(Name: "Range", GenericArguments: [elementType], Location: elementType.Location)
                    }
                };
            }

            if (takes)
            {
                sourceDecl = sourceDecl with
                {
                    Declaration = ((VariableDeclaration)sourceDecl.Declaration) with { IsLoopTakeSource = true }
                };
            }

            sourceDecls.Add(item: sourceDecl);
            BlockStatement loop = LowerFor(forStmt: forStmt with
                {
                    Iterable = new IdentifierExpression(Name: sourceName, Location: forStmt.Iterable.Location)
                },
                walkRoutine: takes ? Declaration.RuntimeContract.TakeEach : Declaration.RuntimeContract.Iter,
                rangeLiteral: source as RangeExpression);
            sourceDecls.Add(item: loop);
            return new BlockStatement(Statements: sourceDecls, Location: srcLoc);
        }

        SourceLocation loc = forStmt.Location;
        int n = _iterCount++;
        string iterName = $"_lf_iter_{n}";

        // Preserve the surface iteration-source name (only when it is a plain variable) so the
        // Phase-5 reshaping-during-iteration check can recover it — this ForStatement is gone by
        // the time body analysis runs.
        string? iterationSourceName = forStmt.Iterable is IdentifierExpression iterSource
            ? iterSource.Name
            : null;

        // -----------------------------------------------------------------------------
        var iterCallExpr = new CallExpression(
            Callee: new MemberExpression(Object: forStmt.Iterable,
                MemberName: walkRoutine,
                Location: loc),
            Arguments: [],
            Location: loc) { IsSynthesizedLowering = true };

        // Each step is `try _lf_iter_N.emit()`: the iterator's failable `emit` recovered to an Optional.
        var tryNextReceiver = new IdentifierExpression(Name: iterName, Location: loc);
        var emitCall = new CallExpression(
            Callee: new MemberExpression(Object: tryNextReceiver,
                MemberName: EmitRoutineName,
                Location: loc),
            Arguments: [],
            Location: loc) { IsSynthesizedLowering = true };
        Expression tryNextCall = LowerForStep(forStmt: forStmt,
            iterCallExpr: iterCallExpr,
            tryNextReceiver: tryNextReceiver,
            emitCall: emitCall);

        Statement iterVarStmt = new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: iterName,
                Type: null,
                Initializer: iterCallExpr,
                Visibility: VisibilityModifier.Secret,
                Location: loc),
            Location: loc);

        // -----------------------------------------------------------------------------
        Statement loweredBody = LowerStatement(stmt: forStmt.Body);

        // -----------------------------------------------------------------------------
        (Statement elseBody, string? elseVarName) = BuildElseArm(forStmt: forStmt,
            n: n,
            loweredBody: loweredBody,
            loc: loc);

        // In a recovery variant, a failure beneath a lookup-shaped step returns as the variant's failure.
        WhenClause? failureClause = forStmt.StepFailureKind is { } failureKind
            ? RoutineValueCalls.StepFailureClause(kind: failureKind, registry: ctx.Registry, loc: loc)
            : null;

        BlockStatement walk = BuildPlainFor(tryNextCall: tryNextCall,
            elseBody: elseBody,
            elseVarName: elseVarName,
            iterVarStmt: iterVarStmt,
            iterationSourceName: iterationSourceName,
            iterationSourcePath: IterationSourcePathOf(source: forStmt.Iterable),
            loc: loc,
            failureClause: failureClause,
            iteratorName: iterName,
            handsOver: walkRoutine == Declaration.RuntimeContract.TakeEach);

        // A range literal walked by one name may become a counter loop. Analysis decides once it knows the
        // range's element type (RangeCounterLoop).
        if (rangeLiteral != null && iterationSourceName != null && forStmt.VariablePattern == null &&
            failureClause == null && walk.Statements is [_, LoopStatement walkLoop])
        {
            walk.Statements[index: 1] = walkLoop with
            {
                RangeWalk = new RangeWalk(SourceName: iterationSourceName,
                    Inclusive: !rangeLiteral.IsExclusive,
                    Stepped: rangeLiteral.Step != null)
            };
        }

        return walk;
    }

    /// <summary>
    /// The counter loop a <c>for</c> over an integer range literal runs instead of walking an emitter: the
    /// statements that take the place of <paramref name="walk"/> (the emitter's declaration and the loop over it),
    /// or null when <paramref name="walk"/> is not such a loop. The range itself is still made first (its local,
    /// <see cref="RangeWalk.SourceName"/>, comes before <paramref name="walk"/>), so its start, end and step are
    /// evaluated once, in the order written, and give the counter its type. Analysis calls this once it knows the
    /// element type is an integer.
    /// <para>The values are the emitter's. A loop without <c>by</c> counts up by one: <c>til</c> stops before the
    /// end, and <c>to</c> compares with the end before it steps, so an end at the type's largest value never steps
    /// past it. A loop with <c>by</c> takes the step's size from the range (a step of zero crashes there, as the
    /// emitter does) and asks the range before each step whether another one still fits. <c>continue</c> goes back
    /// to the loop's top, which is why the counter moves before the body runs.</para>
    /// <code>
    /// var _lf_end_N = src.end                    var _lf_end_N = src.end
    /// var _lf_at_N = src.start                   var _lf_at_N = src.start
    /// loop                                       var _lf_more_N = _lf_at_N &lt;= _lf_end_N
    ///     if _lf_at_N &gt;= _lf_end_N               loop
    ///         break                                  if not _lf_more_N
    ///     var i = _lf_at_N                               break
    ///     _lf_at_N = _lf_at_N + 1                    var i = _lf_at_N
    ///     body                                       _lf_more_N = _lf_at_N &lt; _lf_end_N
    ///                                                if _lf_more_N
    ///                                                    _lf_at_N = _lf_at_N + 1
    ///                                                body
    /// </code>
    /// </summary>
    internal static List<Statement>? RangeCounterLoop(List<Statement> walk, Func<Expression, Expression>? bindValue = null)
    {
        if (walk is not
            [
                DeclarationStatement,
                LoopStatement
                {
                    RangeWalk: { } range,
                    Body: BlockStatement { Statements: [WhenStatement { Clauses: [.., { Pattern: ElsePattern arm } last] }] }
                } loop
            ])
        {
            return null;
        }

        SourceLocation loc = loop.Location;
        string suffix = loop.IteratorName is { } iterator && iterator.StartsWith(value: "_lf_iter_", comparisonType: StringComparison.Ordinal)
            ? iterator["_lf_iter_".Length..]
            : range.SourceName;
        string at = $"_lf_at_{suffix}";
        string end = $"_lf_end_{suffix}";
        string more = $"_lf_more_{suffix}";
        string by = $"_lf_by_{suffix}";

        var statements = new List<Statement>();
        var round = new List<Statement>();
        if (range.Stepped)
        {
            statements.Add(item: Local(name: by, value: SourceCall(range: range, routine: "walk_stride", arguments: [], loc: loc), loc: loc));
            statements.Add(item: Local(name: at, value: SourceMember(range: range, member: "start", loc: loc), loc: loc));
            statements.Add(item: Local(name: more,
                value: new UnaryExpression(Operator: UnaryOperator.Not,
                    Operand: SourceCall(range: range, routine: "is_empty", arguments: [], loc: loc),
                    Location: loc),
                loc: loc));
            round.Add(item: BreakUnless(more: more, loc: loc));
            AddBinding(round: round, variable: arm.VariableName, at: at, types: null, loc: loc, bindValue: bindValue);
            round.Add(item: new AssignmentStatement(Target: Name(name: more, loc: loc),
                Value: SourceCall(range: range, routine: "continues_after", arguments: StepArguments(at: at, by: by, loc: loc), loc: loc),
                Location: loc));
            round.Add(item: new IfStatement(Condition: Name(name: more, loc: loc),
                ThenStatement: new BlockStatement(Statements:
                    [
                        new AssignmentStatement(Target: Name(name: at, loc: loc),
                            Value: SourceCall(range: range, routine: "next_after", arguments: StepArguments(at: at, by: by, loc: loc), loc: loc),
                            Location: loc)
                    ],
                    Location: loc),
                ElseStatement: null,
                Location: loc));
        }
        else
        {
            statements.Add(item: Local(name: end, value: SourceMember(range: range, member: "end", loc: loc), loc: loc));
            statements.Add(item: Local(name: at, value: SourceMember(range: range, member: "start", loc: loc), loc: loc));
            AddCountingRound(statements: statements,
                round: round,
                counter: new CounterNames(At: at, End: end, More: more),
                inclusive: range.Inclusive,
                variable: arm.VariableName,
                types: null,
                loc: loc,
                bindValue: bindValue);
        }

        if (last.Body is BlockStatement body)
        {
            round.AddRange(collection: body.Statements);
        }
        else
        {
            round.Add(item: last.Body);
        }

        statements.Add(item: new LoopStatement(Body: new BlockStatement(Statements: round, Location: loc), Location: loc));
        return statements;
    }

    /// <summary>The routines on <c>Int</c> (and <c>S64</c>) an <c>Int</c> counter loop's two versions call.</summary>
    private const string CountsInS64 = "counts_in_s64";
    private const string S64Range = "s64_range";
    private const string CountingBits = "counting_bits";
    private const string CountedInteger = "counted_integer";

    /// <summary>Whether <paramref name="body"/> holds a loop of its own (a lambda's loop counts too).</summary>
    private static bool HoldsLoop(Statement body)
    {
        bool found = false;
        AstWalker.Walk(root: body,
            visit: node =>
            {
                if (node is LoopStatement or WhileStatement or ForStatement)
                {
                    found = true;
                }
            });
        return found;
    }

    /// <summary>
    /// The counter loop a <c>for</c> over an <c>Int</c> range literal runs, lowered before analysis: like
    /// <see cref="RangeCounterLoop"/>, but a loop whose body holds no loop of its own comes in two versions, chosen
    /// once before the first round. When the start, the end and the step all lie in the <c>S64</c> range (and the
    /// step is not <c>S64_MIN</c>, whose size has no <c>S64</c>), the loop counts a <c>Range[S64]</c> with the same
    /// values and hands its body each value as an <c>Int</c> made from the bits, so the body's arithmetic on it never
    /// takes the big-number path. Any other range runs the general version. Both give the same values, in the same
    /// order, and an empty range runs neither body. An outer loop stays one version, so the code grows by one copy of
    /// the innermost bodies only. The second copy is analyzed quietly (<see cref="LoopStatement.IsQuietCopy"/>), so a
    /// mistake in the body is reported once.
    /// <code>
    /// var _lf_small_N = src.start.counts_in_s64(end: src.end, step: src.step)
    /// if _lf_small_N
    ///     var _lf_src_Ns = src.start.s64_range(end: src.end, step: src.step, inclusive: src.inclusive,
    ///         descending: src.descending)
    ///     (the counter loop over _lf_src_Ns, with var i = _lf_at_Ns.counted_integer())
    /// else
    ///     (the counter loop over src)
    /// </code>
    /// </summary>
    internal static List<Statement>? IntRangeCounterLoops(List<Statement> walk)
    {
        List<Statement>? general = RangeCounterLoop(walk: walk);
        if (general == null ||
            walk is not
            [
                DeclarationStatement iterDecl,
                LoopStatement
                {
                    RangeWalk: { } range,
                    Body: BlockStatement { Statements: [WhenStatement { Clauses: [.., { Body: var armBody }] }] }
                } loop
            ] ||
            HoldsLoop(body: armBody))
        {
            return general;
        }

        SourceLocation loc = loop.Location;
        string suffix = loop.IteratorName is { } iterator && iterator.StartsWith(value: "_lf_iter_", comparisonType: StringComparison.Ordinal)
            ? iterator["_lf_iter_".Length..]
            : range.SourceName;
        string small = $"_lf_small_{suffix}";
        string smallSource = $"_lf_src_{suffix}s";
        var copy = (LoopStatement)Instantiation.GenericAstRewriter.RewriteStatement(stmt: loop,
            subs: new Dictionary<string, string>(comparer: StringComparer.Ordinal));
        copy = copy with
        {
            RangeWalk = range with { SourceName = smallSource },
            IteratorName = $"_lf_iter_{suffix}s"
        };
        List<Statement> fast = RangeCounterLoop(walk: [iterDecl, copy],
            bindValue: counted => SynthesizedCall(receiver: counted, routine: CountedInteger, arguments: [], loc: loc))!;
        if (fast[^1] is LoopStatement fastLoop)
        {
            fastLoop.IsQuietCopy = true;
        }

        fast.Insert(index: 0,
            item: Local(name: smallSource,
                value: SynthesizedCall(receiver: SourceMember(range: range, member: "start", loc: loc),
                    routine: S64Range,
                    arguments:
                    [
                        Argument(name: "end", value: SourceMember(range: range, member: "end", loc: loc), loc: loc),
                        Argument(name: "step", value: SourceMember(range: range, member: "step", loc: loc), loc: loc),
                        Argument(name: "inclusive", value: SourceMember(range: range, member: "inclusive", loc: loc), loc: loc),
                        Argument(name: "descending", value: SourceMember(range: range, member: "descending", loc: loc), loc: loc)
                    ],
                    loc: loc),
                loc: loc));
        return
        [
            Local(name: small,
                value: SynthesizedCall(receiver: SourceMember(range: range, member: "start", loc: loc),
                    routine: CountsInS64,
                    arguments:
                    [
                        Argument(name: "end", value: SourceMember(range: range, member: "end", loc: loc), loc: loc),
                        Argument(name: "step", value: SourceMember(range: range, member: "step", loc: loc), loc: loc)
                    ],
                    loc: loc),
                loc: loc),
            new IfStatement(Condition: Name(name: small, loc: loc),
                ThenStatement: new BlockStatement(Statements: fast, Location: loc),
                ElseStatement: new BlockStatement(Statements: general, Location: loc),
                Location: loc)
        ];
    }

    /// <summary><c>receiver.routine(arguments)</c>, a call the builder writes (so a secret routine of the library may be
    /// its target).</summary>
    private static CallExpression SynthesizedCall(Expression receiver, string routine, List<Expression> arguments,
        SourceLocation loc)
    {
        return new CallExpression(Callee: new MemberExpression(Object: receiver, MemberName: routine, Location: loc),
            Arguments: arguments,
            Location: loc) { IsSynthesizedLowering = true };
    }

    private static NamedArgumentExpression Argument(string name, Expression value, SourceLocation loc)
    {
        return new NamedArgumentExpression(Name: name, Value: value, Location: loc) { ResolvedType = value.ResolvedType };
    }

    /// <summary>The names of a counter loop's own locals: the value of this round, the range's end, and (for a
    /// <c>to</c> loop) whether another round follows.</summary>
    private sealed record CounterNames(string At, string End, string More);

    /// <summary>The types a counter loop's own expressions carry when it is built after analysis, which nothing
    /// analyzes afterwards: the range's integer element type and <c>Bool</c>. Null before analysis.</summary>
    private sealed record CounterTypes(TypeSymbol Element, TypeSymbol Bool);

    /// <summary>
    /// The rounds of a counter loop without <c>by</c>: the statements in front of the loop that it needs besides
    /// the start and the end (<paramref name="statements"/>), then the round's own head (<paramref name="round"/>)
    /// before the body. <c>til</c> stops before the end, and <c>to</c> compares with the end before it steps, so an
    /// end at the type's largest value never steps past it.
    /// </summary>
    private static void AddCountingRound(List<Statement> statements, List<Statement> round, CounterNames counter,
        bool inclusive, string? variable, CounterTypes? types, SourceLocation loc,
        Func<Expression, Expression>? bindValue = null)
    {
        Statement stepOne = new AssignmentStatement(Target: Name(name: counter.At, loc: loc, type: types?.Element),
            Value: new BinaryExpression(Left: Name(name: counter.At, loc: loc, type: types?.Element),
                Operator: BinaryOperator.Add,
                Right: new LiteralExpression(Value: "1", LiteralType: TokenType.UndecidedInteger, Location: loc)
                {
                    ResolvedType = types?.Element
                },
                Location: loc) { ResolvedType = types?.Element },
            Location: loc);
        if (inclusive)
        {
            statements.Add(item: Local(name: counter.More,
                value: Compare(left: counter.At, op: BinaryOperator.LessEqual, right: counter.End, types: types, loc: loc),
                loc: loc,
                declared: types?.Bool));
            round.Add(item: BreakUnless(more: counter.More, types: types, loc: loc));
            AddBinding(round: round, variable: variable, at: counter.At, types: types, loc: loc, bindValue: bindValue);
            round.Add(item: new AssignmentStatement(Target: Name(name: counter.More, loc: loc, type: types?.Bool),
                Value: Compare(left: counter.At, op: BinaryOperator.Less, right: counter.End, types: types, loc: loc),
                Location: loc));
            round.Add(item: new IfStatement(Condition: Name(name: counter.More, loc: loc, type: types?.Bool),
                ThenStatement: new BlockStatement(Statements: [stepOne], Location: loc),
                ElseStatement: null,
                Location: loc));
        }
        else
        {
            round.Add(item: new IfStatement(
                Condition: Compare(left: counter.At, op: BinaryOperator.GreaterEqual, right: counter.End, types: types, loc: loc),
                ThenStatement: new BlockStatement(Statements: [new BreakStatement(Location: loc)], Location: loc),
                ElseStatement: null,
                Location: loc));
            AddBinding(round: round, variable: variable, at: counter.At, types: types, loc: loc, bindValue: bindValue);
            round.Add(item: stepOne);
        }
    }

    /// <summary>
    /// The counter loop a <c>for</c> over an integer range literal runs when the loop is lowered after analysis (a
    /// library body, a recovery variant, a made-concrete body). It is the loop <see cref="RangeCounterLoop"/> gives a
    /// loop lowered before analysis, built with the types analysis gave the range, since nothing analyzes it
    /// afterwards. The start and the end are evaluated once, in the order written. Null when the loop is not over an
    /// integer range literal without <c>by</c>, and the loop then walks the range's emitter.
    /// </summary>
    private BlockStatement? TypedRangeCounterLoop(ForStatement forStmt)
    {
        if (forStmt is not
            {
                VariablePattern: null,
                StepFailureKind: null,
                Iterable: RangeExpression { Step: null, IsDescending: false } range
            } ||
            range.ResolvedType is not RecordTypeSymbol
            {
                GenericDefinition.Name: "Range", TypeArguments: [RecordTypeSymbol element]
            } ||
            !IsCountingInteger(type: element) ||
            range.Start.ResolvedType?.Name != element.Name ||
            range.End.ResolvedType?.Name != element.Name ||
            ctx.Registry.LookupType(name: "Bool") is not { } boolType)
        {
            return null;
        }

        SourceLocation loc = forStmt.Location;
        int n = _iterCount++;
        var counter = new CounterNames(At: $"_lf_at_{n}", End: $"_lf_end_{n}", More: $"_lf_more_{n}");
        var types = new CounterTypes(Element: element, Bool: boolType);
        var statements = new List<Statement>
        {
            Local(name: counter.At, value: range.Start, loc: loc, declared: element),
            Local(name: counter.End, value: range.End, loc: loc, declared: element)
        };
        string? variable = forStmt.Variable == "_" ? null : forStmt.Variable;
        Statement body = LowerStatement(stmt: forStmt.Body);
        var general = new List<Statement>();
        LoopStatement generalLoop = CountingLoop(statements: general, counter: counter, inclusive: !range.IsExclusive,
            variable: variable, types: types, body: body, bindValue: null, loc: loc);

        // An `Int` loop whose body holds no loop of its own also gets the version that counts in `S64`
        // (IntRangeCounterLoops says why).
        if (element.Name == "Int" && !HoldsLoop(body: body) &&
            TypedIntCounting(counter: counter, boolType: boolType, loc: loc) is var (check, smallStart, smallEnd, smallTypes) &&
            check != null)
        {
            var small = new CounterNames(At: $"_lf_at_{n}s", End: $"_lf_end_{n}s", More: $"_lf_more_{n}s");
            var fast = new List<Statement>
            {
                Local(name: small.At, value: smallStart!, loc: loc, declared: smallTypes!.Element),
                Local(name: small.End, value: smallEnd!, loc: loc, declared: smallTypes.Element)
            };
            Statement copy = Instantiation.GenericAstRewriter.RewriteStatement(stmt: body,
                subs: new Dictionary<string, string>(comparer: StringComparer.Ordinal));
            LoopStatement fastLoop = CountingLoop(statements: fast, counter: small, inclusive: !range.IsExclusive,
                variable: variable, types: smallTypes, body: copy,
                bindValue: counted => TypedCall(receiver: counted, routine: CountedInteger, arguments: [], loc: loc)!,
                loc: loc);
            fastLoop.IsQuietCopy = true;
            statements.Add(item: new IfStatement(Condition: check,
                ThenStatement: new BlockStatement(Statements: fast, Location: loc),
                ElseStatement: new BlockStatement(Statements: general, Location: loc),
                Location: loc));
            return new BlockStatement(Statements: statements, Location: loc);
        }

        statements.AddRange(collection: general);
        return new BlockStatement(Statements: statements, Location: loc);
    }

    /// <summary>A counter loop without <c>by</c> over the locals <paramref name="counter"/> names: the statements in
    /// front of it and the loop itself go into <paramref name="statements"/>, and the loop is returned.</summary>
    private static LoopStatement CountingLoop(List<Statement> statements, CounterNames counter, bool inclusive,
        string? variable, CounterTypes types, Statement body, Func<Expression, Expression>? bindValue,
        SourceLocation loc)
    {
        var round = new List<Statement>();
        AddCountingRound(statements: statements,
            round: round,
            counter: counter,
            inclusive: inclusive,
            variable: variable,
            types: types,
            loc: loc,
            bindValue: bindValue);
        if (body is BlockStatement block)
        {
            round.AddRange(collection: block.Statements);
        }
        else
        {
            round.Add(item: body);
        }

        var loop = new LoopStatement(Body: new BlockStatement(Statements: round, Location: loc), Location: loc);
        statements.Add(item: loop);
        return loop;
    }

    /// <summary>
    /// The typed pieces of the <c>S64</c> version of an <c>Int</c> counter loop without <c>by</c>: the check
    /// <c>_lf_at_N.counts_in_s64(end: _lf_end_N)</c>, the <c>S64</c> start and end (<c>counting_bits()</c>), and the
    /// counter types over <c>S64</c>. All null when the library routines are not there.
    /// </summary>
    private (Expression? Check, Expression? Start, Expression? End, CounterTypes? Types) TypedIntCounting(
        CounterNames counter, TypeSymbol boolType, SourceLocation loc)
    {
        if (ctx.Registry.LookupType(name: "Int") is not { } intType ||
            ctx.Registry.LookupType(name: "S64") is not { } s64Type ||
            ctx.Registry.LookupMemberRoutineOverload(type: s64Type, memberRoutineName: CountedInteger, argTypes: []) == null)
        {
            return (null, null, null, null);
        }

        Expression? check = TypedCall(receiver: Name(name: counter.At, loc: loc, type: intType),
            routine: CountsInS64,
            arguments: [Argument(name: "end", value: Name(name: counter.End, loc: loc, type: intType), loc: loc)],
            loc: loc);
        Expression? start = TypedCall(receiver: Name(name: counter.At, loc: loc, type: intType), routine: CountingBits,
            arguments: [], loc: loc);
        Expression? end = TypedCall(receiver: Name(name: counter.End, loc: loc, type: intType), routine: CountingBits,
            arguments: [], loc: loc);
        return check == null || start == null || end == null
            ? (null, null, null, null)
            : (check, start, end, new CounterTypes(Element: s64Type, Bool: boolType));
    }

    /// <summary><c>receiver.routine(arguments)</c> bound to the routine of the receiver's type that takes the
    /// arguments' types, as analysis would bind it. Null when there is none.</summary>
    private CallExpression? TypedCall(Expression receiver, string routine, List<Expression> arguments, SourceLocation loc)
    {
        if (receiver.ResolvedType is not { } owner ||
            ctx.Registry.LookupMemberRoutineOverload(type: owner,
                memberRoutineName: routine,
                argTypes: arguments.Select(selector: a => a.ResolvedType!).ToList()) is not { ReturnType: { } returned } resolved)
        {
            return null;
        }

        return new CallExpression(Callee: new MemberExpression(Object: receiver, MemberName: routine, Location: loc),
            Arguments: arguments,
            Location: loc)
        {
            ResolvedRoutine = resolved,
            ResolvedType = returned,
            LoweringKind = CallLoweringKind.DirectMemberRoutine,
            IsPreAnalyzed = true,
            IsSynthesizedLowering = true
        };
    }

    /// <summary>Whether a range over <paramref name="type"/> can count: a fixed-width integer (<c>S8</c> to
    /// <c>U256</c>) or <c>Int</c>.</summary>
    private static bool IsCountingInteger(TypeSymbol type)
    {
        return type.Name == "Int" ||
               type.Name.Length >= 2 && type.Name[index: 0] is 'S' or 'U' && type.Name[1..].All(predicate: char.IsDigit);
    }

    /// <summary><c>var name = value</c>, a local of the counter loop, or <c>var name: T = value</c> when
    /// <paramref name="declared"/> gives the type. A loop built after analysis states its types: a body analyzed
    /// again later (a recovery variant) would otherwise give a bare literal start its own default type.</summary>
    private static DeclarationStatement Local(string name, Expression value, SourceLocation loc,
        TypeSymbol? declared = null)
    {
        return new DeclarationStatement(Declaration: new VariableDeclaration(Name: name,
                Type: declared == null
                    ? null
                    : new TypeExpression(Name: declared.Name, GenericArguments: null, Location: loc) { ResolvedType = declared },
                Initializer: value,
                Visibility: VisibilityModifier.Secret,
                Location: loc),
            Location: loc);
    }

    private static IdentifierExpression Name(string name, SourceLocation loc, TypeSymbol? type = null)
    {
        return new IdentifierExpression(Name: name, Location: loc) { ResolvedType = type };
    }

    private static BinaryExpression Compare(string left, BinaryOperator op, string right, SourceLocation loc,
        CounterTypes? types = null)
    {
        return new BinaryExpression(Left: Name(name: left, loc: loc, type: types?.Element),
            Operator: op,
            Right: Name(name: right, loc: loc, type: types?.Element),
            Location: loc) { ResolvedType = types?.Bool };
    }

    /// <summary><c>if not more</c> <c>break</c>.</summary>
    private static IfStatement BreakUnless(string more, SourceLocation loc, CounterTypes? types = null)
    {
        return new IfStatement(Condition: new UnaryExpression(Operator: UnaryOperator.Not,
                Operand: Name(name: more, loc: loc, type: types?.Bool),
                Location: loc) { ResolvedType = types?.Bool },
            ThenStatement: new BlockStatement(Statements: [new BreakStatement(Location: loc)], Location: loc),
            ElseStatement: null,
            Location: loc);
    }

    /// <summary>The loop's own name for the value of this round (<c>var i = _lf_at_N</c>), unless it is <c>_</c>.</summary>
    private static void AddBinding(List<Statement> round, string? variable, string at, CounterTypes? types,
        SourceLocation loc, Func<Expression, Expression>? bindValue = null)
    {
        if (variable is { } name)
        {
            Expression counted = Name(name: at, loc: loc, type: types?.Element);
            DeclarationStatement binding = Local(name: name, value: bindValue?.Invoke(arg: counted) ?? counted, loc: loc);
            round.Add(item: binding with
            {
                Declaration = ((VariableDeclaration)binding.Declaration) with { IsLoopVariable = true }
            });
        }
    }

    /// <summary>The arguments of the range's step questions: <c>value: _lf_at_N, stride: _lf_by_N</c>.</summary>
    private static List<Expression> StepArguments(string at, string by, SourceLocation loc)
    {
        return
        [
            new NamedArgumentExpression(Name: "value", Value: Name(name: at, loc: loc), Location: loc),
            new NamedArgumentExpression(Name: "stride", Value: Name(name: by, loc: loc), Location: loc)
        ];
    }

    private static MemberExpression SourceMember(RangeWalk range, string member, SourceLocation loc)
    {
        return new MemberExpression(Object: Name(name: range.SourceName, loc: loc), MemberName: member, Location: loc);
    }

    private static CallExpression SourceCall(RangeWalk range, string routine, List<Expression> arguments,
        SourceLocation loc)
    {
        return new CallExpression(Callee: SourceMember(range: range, member: routine, loc: loc),
            Arguments: arguments,
            Location: loc) { IsSynthesizedLowering = true };
    }

    /// <summary>The iterator step a <c>for</c> loop recovers with <c>try</c>.</summary>
    private const string EmitRoutineName = "emit";

    /// <summary>The recovery a <c>for</c> loop's step takes: <c>try</c>, or <c>check</c> in a recovery variant
    /// whose loop may reach a routine value (<see cref="ForStatement.StepFailureKind"/>): there the walk's end
    /// (<c>AbsentValueError</c>) ends the loop and any other failure is the variant's.</summary>
    private static RecoveryKind StepKind(ForStatement forStmt)
    {
        return forStmt.StepFailureKind != null
            ? RecoveryKind.Check
            : RecoveryKind.Try;
    }

    /// <summary>The arm that ends a <c>for</c> loop: <c>is None</c> after a <c>try</c> step, or
    /// <c>is AbsentValueError</c> after a <c>check</c> step (a loop with a <paramref name="failureClause"/>).</summary>
    private static Pattern EndOfWalkPattern(WhenClause? failureClause, SourceLocation loc)
    {
        return failureClause == null
            ? new NonePattern(Location: loc)
            : new TypePattern(Type: new TypeExpression(Name: "AbsentValueError", GenericArguments: null, Location: loc),
                VariableName: null,
                Bindings: null,
                Location: loc);
    }

    /// <summary>
    /// The step of a <c>for</c> loop: <c>try _lf_iter_N.emit()</c>. Before analysis it is that recovery
    /// expression, which analysis binds. After analysis (stdlib and variant bodies) the iterable is typed,
    /// so the <c>iter</c> call is annotated and the step is bound here to the try variant of the CONCRETE
    /// iterator's <c>emit</c> (so reachability marks that iterator's variant). Skip ErrorTypeSymbol: SA
    /// suppresses stdlib errors.
    /// </summary>
    private Expression LowerForStep(ForStatement forStmt, CallExpression iterCallExpr,
        IdentifierExpression tryNextReceiver, CallExpression emitCall)
    {
        if (forStmt.Iterable.ResolvedType is { } iterType and not ErrorTypeSymbol &&
            ctx.Registry.LookupMemberRoutine(type: Declaration.RoamedTransparency.SharedEntityOf(type: iterType), memberRoutineName: "emitter") is
                { ReturnType: { } rawIteratorType } iterMemberRoutine)
        {
            // A shared entity is walked through its handle by the entity's own `iter`, whose type parameters
            // the entity's type arguments bind (not the handle's).
            iterType = Declaration.RoamedTransparency.SharedEntityOf(type: iterType);
            // When LookupMemberRoutine returns the generic-def `iter`, its ReturnType still carries the
            // owner's params (e.g. `?EnumerateEmitter[T, S/Iter]`). Substitute the concrete owner's type
            // args so the step binds on the CONCRETE emitter (`EnumerateEmitter[Text, ListEmitter[Text]]`),
            // otherwise reachability marks the unresolved-projection emitter's variant and the concrete
            // one never generates. An instance it returns (one with a generic definition) has its return
            // type in the owner's arguments already. Substituting that again replaces a parameter the
            // arguments themselves hold: in `List[Work[T]].gather` the list's `T` is `Work[T]`, so a
            // second pass turned `ListEmitter[Work[T]]` into `ListEmitter[Work[Work[T]]]`.
            TypeSymbol iteratorType = iterMemberRoutine.GenericDefinition == null
                ? SubstituteForConcreteOwner(type: rawIteratorType, owner: iterType)
                : rawIteratorType;
            iterCallExpr.ResolvedRoutine = iterMemberRoutine;
            iterCallExpr.ResolvedType = iteratorType;
            // Carry the concrete emitter type onto the receiver so reachability/codegen see it.
            tryNextReceiver.ResolvedType = iteratorType;
            // Walking an emitter goes through a write token on it (`Emitter[T].emitter() -> Modifying[Me]`): the
            // step is the emitter's own `emit`.
            if (ctx.Registry.LookupMemberRoutine(
                    type: TokenHeldEntity(type: Declaration.RoamedTransparency.SharedEntityOf(type: iteratorType)),
                    memberRoutineName: EmitRoutineName,
                    isFailable: true) is { } emit &&
                ctx.Registry.LookupRecoveryVariant(recovered: emit, kind: StepKind(forStmt: forStmt)) is { } step)
            {
                return emitCall with
                {
                    ResolvedRoutine = step,
                    LoweringKind = CallLoweringKind.DirectMemberRoutine,
                    ResolvedType = step.ReturnType,
                    IsPreAnalyzed = true
                };
            }
        }

        return new RecoveryExpression(Kind: StepKind(forStmt: forStmt),
            Inner: emitCall,
            Location: emitCall.Location) { IsSynthesized = true };
    }

    /// <summary>The entity a <c>Viewing</c>/<c>Modifying</c> token is on, else the type itself.</summary>
    private static TypeSymbol TokenHeldEntity(TypeSymbol type)
    {
        return type is RecordTypeSymbol
        {
            GenericDefinition.Name: Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying,
            TypeArguments: [EntityTypeSymbol held]
        }
            ? held
            : type;
    }

    /// <summary>
    /// Builds the <c>else var</c> arm body of the emitted <c>when</c> and the arm's bound variable
    /// name. For tuple destructuring, prepends the positional <c>var a = elem.itemI</c> bindings to
    /// the loop body; otherwise the arm binds the loop variable directly (or discards on <c>_</c>).
    /// </summary>
    private static (Statement elseBody, string? elseVarName) BuildElseArm(ForStatement forStmt,
        int n, Statement loweredBody, SourceLocation loc)
    {
        if (forStmt.VariablePattern != null)
        {
            // Tuple destructuring: else var _lf_elem_M -> { var a = elem.item0; var b = elem.item1; ... body }
            string elemName = $"_lf_elem_{n}";

            // Prepend: var a = _lf_elem_M.item0, var b = _lf_elem_M.item1, ??
            var bindStmts =
                new List<Statement>(capacity: forStmt.VariablePattern.Bindings.Count + 1);
            for (int i = 0; i < forStmt.VariablePattern.Bindings.Count; i++)
            {
                DestructuringBinding binding = forStmt.VariablePattern.Bindings[index: i];
                string bindName = binding.BindingName ?? binding.MemberVariableName ?? $"_lf_b{i}";
                if (bindName == "_")
                {
                    continue;
                }

                bindStmts.Add(item: new DeclarationStatement(Declaration: new VariableDeclaration(
                        Name: bindName,
                        Type: null,
                        Initializer: new MemberExpression(
                            Object: new IdentifierExpression(Name: elemName, Location: loc),
                            MemberName: $"item{i}",
                            Location: loc),
                        Visibility: VisibilityModifier.Secret,
                        Location: loc) { IsLoopVariable = true },
                    Location: loc));
            }

            if (loweredBody is BlockStatement bodyBlock)
            {
                bindStmts.AddRange(collection: bodyBlock.Statements);
                return (bodyBlock with { Statements = bindStmts }, elemName);
            }

            bindStmts.Add(item: loweredBody);
            return (new BlockStatement(Statements: bindStmts, Location: loc), elemName);
        }

        // Simple variable or discard
        string? elseVarName = forStmt.Variable == "_"
            ? null
            : forStmt.Variable;
        return (loweredBody, elseVarName);
    }

    /// <summary>
    /// The shape path of a <c>for</c> source: a name, a field chain (<c>me.items</c>) or an element
    /// (<c>grid[0]</c> is <c>grid[]</c>, the index is not tracked). Null for any other source.
    /// </summary>
    private static string? IterationSourcePathOf(Expression source)
    {
        return source switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression { Object: var inner, MemberName: var field } =>
                IterationSourcePathOf(source: inner) is { } prefix
                    ? $"{prefix}.{field}"
                    : null,
            IndexExpression { Object: var container } =>
                IterationSourcePathOf(source: container) is { } owner
                    ? $"{owner}[]"
                    : null,
            _ => null
        };
    }

    /// <summary>
    /// Builds the <c>for</c> lowering: the <c>None</c> arm breaks.
    /// </summary>
    private static BlockStatement BuildPlainFor(Expression tryNextCall, Statement elseBody,
        string? elseVarName, Statement iterVarStmt, string? iterationSourceName,
        string? iterationSourcePath,
        SourceLocation loc, WhenClause? failureClause, string? iteratorName = null, bool handsOver = false)
    {
        Statement noneBody = new BlockStatement(Statements:
            [new BreakStatement(Location: loc)],
            Location: loc);

        var noneClause = new WhenClause(Pattern: EndOfWalkPattern(failureClause: failureClause, loc: loc),
            Body: noneBody,
            Location: loc);
        var elseClause = new WhenClause(
            Pattern: new ElsePattern(VariableName: elseVarName, Location: loc),
            Body: elseBody,
            Location: loc);

        var whenStmt = new WhenStatement(Expression: tryNextCall,
            Clauses: failureClause != null
                ? [noneClause, failureClause, elseClause]
                : [noneClause, elseClause],
            Location: loc) { IsForStep = true, StepHandsOver = handsOver };
        var loopStmt =
            new LoopStatement(Body: new BlockStatement(Statements: [whenStmt], Location: loc),
                Location: loc)
            {
                IsIteratorForLoop = true, IterationSourceName = iterationSourceName,
                IterationSourcePath = iterationSourcePath, IteratorName = iteratorName
            };

        return new BlockStatement(Statements: [iterVarStmt, loopStmt], Location: loc);
    }

    /// <summary>
    /// Substitutes a type with a concrete generic owner's type arguments (e.g. the generic-def
    /// <c>iter</c> return <c>EnumerateEmitter[T, S/Iter]</c> for owner
    /// <c>EnumerateIterator[Text, List[Text]]</c> → <c>EnumerateEmitter[Text, ListEmitter[Text]]</c>).
    /// Resolves associated-type projections via <see cref="RecordTypeSymbol.SubstituteType"/>.
    /// </summary>
    private static TypeSymbol SubstituteForConcreteOwner(TypeSymbol type, TypeSymbol owner)
    {
        TypeSymbol? def = owner switch
        {
            EntityTypeSymbol e => e.GenericDefinition,
            RecordTypeSymbol r => r.GenericDefinition,
            _ => null
        };
        if (def?.GenericParameters is { } defParams && owner.TypeArguments is { } args &&
            defParams.Count == args.Count && defParams.Count > 0)
        {
            var subs = new Dictionary<string, TypeSymbol>();
            for (int i = 0; i < defParams.Count; i++)
            {
                subs[key: defParams[index: i]] = args[index: i];
            }

            return RecordTypeSymbol.SubstituteType(type: type, substitution: subs);
        }

        return type;
    }

    /// <summary>
    /// Lowers control flow in all synthesized variant bodies in
    /// <see cref="DesugaringContext.VariantBodies"/>.
    /// Called from <see cref="DesugaringPipeline.RunGlobal"/> after variant bodies are generated,
    /// so that bodies copied from unlowered stdlib originals get their WhileStatement nodes
    /// converted to LoopStatement before OperatorLoweringPass and codegen see them.
    /// </summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            if (ctx.RestoredVariantKeys.Contains(item: key))
            {
                continue; // already lowered at snapshot capture
            }

            Statement body = ctx.VariantBodies[key: key];
            Statement lowered = LowerStatement(stmt: body);
            if (!ReferenceEquals(objA: lowered, objB: body))
            {
                ctx.VariantBodies[key: key] = lowered;
            }
        }
    }

    /// <summary>
    /// Lower ForStatement/DestructuringStatement etc. in monomorphized bodies that GMP
    /// produced from stdlib originals (which never went through Phase 6 desugaring).
    /// Notable consumer: <see cref="Builder.Instantiation.Passes.ProtocolDefaultImplLoweringPass"/>,
    /// which clones stdlib protocol-default-impl bodies (e.g. <c>Iterable[Text].join</c>) into
    /// per-implementer routines; those clones contain raw `for` loops that codegen rejects.
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        IDictionary<string, Instantiation.MonomorphizedBody> bodies)
    {
        foreach (string key in bodies.Keys.ToList())
        {
            Instantiation.MonomorphizedBody mb = bodies[key: key];
            Statement lowered = LowerStatement(stmt: mb.Ast.Body);
            if (!ReferenceEquals(objA: lowered, objB: mb.Ast.Body))
            {
                bodies[key: key] = mb with { Ast = mb.Ast with { Body = lowered } };
            }
        }
    }
}
