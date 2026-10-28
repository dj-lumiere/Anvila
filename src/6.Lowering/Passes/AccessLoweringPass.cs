using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Lowers the <c>viewing</c> / <c>modifying</c> blocks and the <c>g.view()</c> / <c>g.modify()</c> call arguments to
/// the routines of <see cref="RuntimeContract.AccessBlock"/>, so the passes after it see plain declarations, calls
/// and control flow.
///
/// <para>A block becomes, in a scope of its own:</para>
/// <code>
/// var __access_heldK = target           # a target made in the head (a call), held for the block
/// var __accN_i = token_i                # each token or entity target, only checked to differ
/// __accN_i.enlist_identity() ...        # with two or more of them
/// access_open_enlisted()
/// var __accN_tj = target_j.modify_enter()   # each target the block enters, in the order written
/// body                                  # with the exits before every way out of the block
/// target_j.modify_exit() ...            # in the opposite order
/// </code>
/// <para>In a <c>try</c> block the enter is <c>try target.modify_enter()</c>, a <c>Optional</c>, and its exit runs only
/// when it is present. A <c>return</c> inside computes its value before the targets are given up. A statement that
/// passes <c>g.modify()</c> enters <c>g</c> before it and gives it up after it, and the argument becomes the
/// token.</para>
/// </summary>
internal sealed class AccessLoweringPass(TypeRegistry registry) : AstRewriter
{
    private int _count;

    /// <summary>Lowers the access blocks and access arguments of every routine of <paramref name="program"/>.</summary>
    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(registry: registry, program: program, lower: r => VisitStatement(stmt: r.Body));
    }

    /// <summary>Lowers the access blocks and access arguments of the recovery-variant bodies.</summary>
    public void RunOnVariantBodies(PostprocessingContext ctx)
    {
        BodyDispatch.RunOnVariantBodies(registry: registry, bodies: ctx.VariantBodies,
            lower: (_, body) => VisitStatement(stmt: body));
    }

    // ── blocks ────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override Statement VisitAccess(AccessStatement s)
    {
        Statement body = VisitStatement(stmt: s.Body);
        SourceLocation loc = s.Location;
        int block = _count++;
        var prologue = new List<Statement>();
        var enlists = new List<Statement>();
        var entries = new List<Statement>();
        var closes = new List<Statement>();
        // Each token goes by a name of its own in the lowered body: the name the block gives it may be the name of a
        // variable outside the block (a target without `as` keeps its own name), and the lowered code has one
        // variable per name.
        var renames = new Dictionary<string, (string Name, TypeSymbol Type)>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < s.Targets.Count; i++)
        {
            AccessTarget target = s.Targets[index: i];
            string tokenName = $"__acc{block}_t{i}";
            if (target.Enter != null)
            {
                if (target.Hidden != null)
                {
                    Expression made = VisitExpression(expr: target.Target);
                    TypeSymbol madeType = made.ResolvedType ?? target.Target.ResolvedType ??
                        throw new InvalidOperationException(message: $"An access target at {target.Location} has no type.");
                    prologue.Add(item: Binding(name: target.Hidden, value: made, type: madeType, loc: target.Location,
                        owesTeardown: true));
                }

                Expression enter = VisitExpression(expr: target.Enter);
                TypeSymbol tokenType = enter.ResolvedType ?? target.Enter.ResolvedType ??
                    throw new InvalidOperationException(message: $"The enter at {target.Location} has no type.");
                renames[key: target.Name] = (tokenName, tokenType);
                entries.Add(item: Binding(name: tokenName, value: enter, type: tokenType, loc: target.Location));
                Statement exit = Statement(expr: VisitExpression(expr: target.Exit!));
                if (s.IsTry)
                {
                    exit = new IfStatement(
                        Condition: Call(receiver: Ident(name: tokenName, type: tokenType, loc: target.Location),
                            name: "is_present", type: null, loc: target.Location),
                        ThenStatement: new BlockStatement(Statements: [exit], Location: target.Location),
                        ElseStatement: null,
                        Location: target.Location);
                }

                closes.Insert(index: 0, item: exit);
                continue;
            }

            // A token, or an entity of the routine's own: nothing to take, only checked to differ from the others.
            Expression value = VisitExpression(expr: target.Target);
            TypeSymbol type = value.ResolvedType ?? target.Target.ResolvedType ??
                throw new InvalidOperationException(message: $"An access target at {target.Location} has no type.");
            string hidden = $"__acc{block}_{i}";
            bool narrow = !target.IsWrite && type.BareName == RuntimeContract.Modifying;
            Expression tokenValue = type is EntityTypeSymbol || narrow
                ? Call(receiver: value, name: target.IsWrite
                    ? RuntimeContract.AccessBlock.Modify
                    : RuntimeContract.AccessBlock.View,
                    type: TokenType(isWrite: target.IsWrite, inner: Inner(type: type)), loc: target.Location)
                : value;
            TypeSymbol identityType = tokenValue.ResolvedType!;
            prologue.Add(item: Binding(name: hidden, value: tokenValue, type: identityType, loc: target.Location));
            IdentifierExpression tokenHandle = Ident(name: hidden, type: identityType, loc: target.Location);
            renames[key: target.Name] = (tokenName, identityType);
            entries.Insert(index: enlists.Count,
                item: Binding(name: tokenName, value: tokenHandle, type: identityType, loc: target.Location));
            enlists.Add(item: Statement(expr: Call(receiver: tokenHandle, name: RuntimeContract.AccessBlock.EnlistIdentity,
                type: null, loc: target.Location)));
        }

        // Two or more tokens and entities are checked to be different entities before anything is entered.
        var statements = new List<Statement>(collection: prologue);
        if (enlists.Count > 1)
        {
            statements.AddRange(collection: enlists);
            statements.Add(item: Statement(expr: FreeCall(name: RuntimeContract.AccessBlock.OpenEnlisted, loc: loc)));
        }

        body = new Renamer(renames: renames).VisitStatement(stmt: body);
        Statement? exitAll = closes.Count switch
        {
            0 => null,
            1 => closes[index: 0],
            _ => new BlockStatement(Statements: closes, Location: loc)
        };
        statements.AddRange(collection: entries);
        statements.Add(item: exitAll != null
            ? Release(stmt: body, exit: exitAll, loopDepth: 0)
            : body);
        statements.AddRange(collection: closes);
        return new BlockStatement(Statements: statements, Location: loc);
    }

    /// <summary>
    /// Puts <paramref name="exit"/> before every way out of <paramref name="stmt"/> that leaves the block: a
    /// <c>return</c> (its value computed first, into a variable, so it is read while the access is held), a
    /// <c>throw</c>, a variant return, and a <c>break</c> / <c>continue</c> outside the block's own loops.
    /// </summary>
    private Statement Release(Statement stmt, Statement exit, int loopDepth)
    {
        switch (stmt)
        {
            case ReturnStatement { Value: { } value } ret when value is not IdentifierExpression &&
                                                              value.ResolvedType is { } valueType:
            {
                string spill = $"__acc{_count++}_ret";
                return new BlockStatement(Statements:
                [
                    Binding(name: spill, value: value, type: valueType, loc: ret.Location),
                    exit,
                    ret with { Value = Ident(name: spill, type: valueType, loc: ret.Location) }
                ], Location: ret.Location);
            }
            case ReturnStatement or ThrowStatement or VariantReturnStatement:
                return new BlockStatement(Statements: [exit, stmt], Location: stmt.Location);
            case BreakStatement or ContinueStatement:
                return loopDepth == 0
                    ? new BlockStatement(Statements: [exit, stmt], Location: stmt.Location)
                    : stmt;
            case BlockStatement block:
            {
                var list = block.Statements.Select(selector: s => Release(stmt: s, exit: exit, loopDepth: loopDepth))
                                .ToList();
                return block with { Statements = list };
            }
            case IfStatement ifs:
                return ifs with
                {
                    ThenStatement = Release(stmt: ifs.ThenStatement, exit: exit, loopDepth: loopDepth),
                    ElseStatement = ifs.ElseStatement != null
                        ? Release(stmt: ifs.ElseStatement, exit: exit, loopDepth: loopDepth)
                        : null
                };
            case WhenStatement w:
                return w with
                {
                    Clauses = w.Clauses
                               .Select(selector: c => c with { Body = Release(stmt: c.Body, exit: exit, loopDepth: loopDepth) })
                               .ToList()
                };
            case LoopStatement loop:
                return loop with { Body = Release(stmt: loop.Body, exit: exit, loopDepth: loopDepth + 1) };
            case WhileStatement w:
                return w with
                {
                    Body = Release(stmt: w.Body, exit: exit, loopDepth: loopDepth + 1),
                    ThenBranch = w.ThenBranch != null
                        ? Release(stmt: w.ThenBranch, exit: exit, loopDepth: loopDepth)
                        : null
                };
            case ForStatement each:
                return each with
                {
                    Body = Release(stmt: each.Body, exit: exit, loopDepth: loopDepth + 1),
                    ThenBranch = each.ThenBranch != null
                        ? Release(stmt: each.ThenBranch, exit: exit, loopDepth: loopDepth)
                        : null
                };
            case DangerStatement d:
                return d with { Body = (BlockStatement)Release(stmt: d.Body, exit: exit, loopDepth: loopDepth) };
            case UsingStatement u:
                return u with { Body = Release(stmt: u.Body, exit: exit, loopDepth: loopDepth) };
            default:
                return stmt;
        }
    }

    // ── access arguments ──────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override Statement VisitBlock(BlockStatement s)
    {
        bool changed = false;
        var list = new List<Statement>(capacity: s.Statements.Count);
        foreach (Statement statement in s.Statements)
        {
            Statement lowered = VisitStatement(stmt: statement);
            if (AccessArguments(stmt: lowered) is { } spliced)
            {
                list.AddRange(collection: spliced);
                changed = true;
                continue;
            }

            list.Add(item: lowered);
            changed |= !ReferenceEquals(objA: lowered, objB: statement);
        }

        return changed
            ? s with { Statements = list }
            : s;
    }

    /// <summary>
    /// A statement that passes <c>g.view()</c> / <c>g.modify()</c> as an argument, as the statements that take the
    /// access before it and give it up after it, or null when it passes none.
    /// </summary>
    private List<Statement>? AccessArguments(Statement stmt)
    {
        if (stmt is not (ExpressionStatement or DeclarationStatement or AssignmentStatement or ReturnStatement
            or ThrowStatement))
        {
            return null;
        }

        var nodes = new List<object>();
        AstWalker.Walk(root: stmt, visit: n => nodes.Add(item: n));
        var arguments = new HashSet<Expression>(comparer: ReferenceEqualityComparer.Instance);
        foreach (CallExpression call in nodes.OfType<CallExpression>())
        {
            foreach (Expression argument in call.Arguments)
            {
                arguments.Add(item: argument is NamedArgumentExpression named ? named.Value : argument);
            }

            // The receiver of a member call is its `me` argument: `g.view().count()` holds the access for `count`.
            if (call.Callee is MemberExpression { Object: var receiver })
            {
                arguments.Add(item: receiver);
            }
        }

        // A member variable read through it holds the access for the read: `g.view().balance`.
        foreach (MemberExpression read in nodes.OfType<MemberExpression>())
        {
            arguments.Add(item: read.Object);
        }

        var openers = nodes.OfType<CallExpression>()
                           .Where(predicate: c => arguments.Contains(item: c) &&
                                                  GuardAccess.LockingReceiver(expression: c, isWrite: out _) != null)
                           .ToList();
        if (openers.Count == 0)
        {
            return null;
        }

        int block = _count++;
        var before = new List<Statement>();
        var closes = new List<Statement>();
        var replacements = new Dictionary<Expression, Expression>(comparer: ReferenceEqualityComparer.Instance);
        // Each Guarded is entered in the order written and given up in the opposite order, as nested blocks would.
        for (int i = 0; i < openers.Count; i++)
        {
            CallExpression opener = openers[index: i];
            Expression receiver = GuardAccess.LockingReceiver(expression: opener, isWrite: out bool isWrite)!;
            TypeSymbol type = receiver.ResolvedType!;
            IdentifierExpression handle;
            if (receiver is IdentifierExpression named)
            {
                handle = Ident(name: named.Name, type: type, loc: receiver.Location);
            }
            else
            {
                string hidden = $"__acc{block}_{i}";
                before.Add(item: Binding(name: hidden, value: receiver, type: type, loc: receiver.Location));
                handle = Ident(name: hidden, type: type, loc: receiver.Location);
            }

            TypeSymbol tokenType = opener.ResolvedType ?? TokenType(isWrite: isWrite, inner: Inner(type: type));
            string token = $"__acc{block}_t{i}";
            before.Add(item: Binding(name: token,
                value: Call(receiver: handle, name: isWrite
                    ? RuntimeContract.AccessBlock.ModifyEnter
                    : RuntimeContract.AccessBlock.ViewEnter, type: tokenType, loc: opener.Location),
                type: tokenType, loc: opener.Location));
            closes.Insert(index: 0, item: Statement(expr: Call(receiver: handle, name: isWrite
                ? RuntimeContract.AccessBlock.ModifyExit
                : RuntimeContract.AccessBlock.ViewExit, type: null, loc: opener.Location)));
            replacements[key: opener] = Ident(name: token, type: tokenType, loc: opener.Location);
        }

        Statement rewritten = new Replacer(replacements: replacements).VisitStatement(stmt: stmt);
        switch (rewritten)
        {
            case ReturnStatement { Value: { ResolvedType: { } valueType } value } ret:
            {
                string spill = $"__acc{block}_ret";
                before.Add(item: Binding(name: spill, value: value, type: valueType, loc: ret.Location));
                before.AddRange(collection: closes);
                before.Add(item: ret with { Value = Ident(name: spill, type: valueType, loc: ret.Location) });
                return before;
            }
            case ThrowStatement { Error: { ResolvedType: { } errorType } error } thrown:
            {
                string spill = $"__acc{block}_err";
                before.Add(item: Binding(name: spill, value: error, type: errorType, loc: thrown.Location));
                before.AddRange(collection: closes);
                before.Add(item: thrown with { Error = Ident(name: spill, type: errorType, loc: thrown.Location) });
                return before;
            }
            default:
                before.Add(item: rewritten);
                before.AddRange(collection: closes);
                return before;
        }
    }

    /// <summary>Renames the variables a block's tokens go by inside its body.</summary>
    private sealed class Renamer(Dictionary<string, (string Name, TypeSymbol Type)> renames) : AstRewriter
    {
        public override Expression VisitExpression(Expression expr)
        {
            return expr is IdentifierExpression { Name: var name } id && renames.TryGetValue(key: name, value: out var to)
                ? id with { Name = to.Name, ResolvedType = id.ResolvedType ?? to.Type }
                : base.VisitExpression(expr: expr);
        }
    }

    /// <summary>Replaces given expressions, by reference, with others.</summary>
    private sealed class Replacer(Dictionary<Expression, Expression> replacements) : AstRewriter
    {
        public override Expression VisitExpression(Expression expr)
        {
            return replacements.TryGetValue(key: expr, value: out Expression? replacement)
                ? replacement
                : base.VisitExpression(expr: expr);
        }
    }

    // ── building nodes ────────────────────────────────────────────────────────────────────────────

    /// <summary>The type a <c>Guarded</c>, a token or an entity gives access to.</summary>
    private static TypeSymbol Inner(TypeSymbol type)
    {
        return type is EntityTypeSymbol
            ? type
            : type.TypeArguments is [{ } inner, ..]
                ? inner
                : throw new InvalidOperationException(message: $"'{type.Name}' gives access to no type.");
    }

    /// <summary><c>Viewing[inner]</c> or <c>Modifying[inner]</c>.</summary>
    private TypeSymbol TokenType(bool isWrite, TypeSymbol inner)
    {
        string name = isWrite
            ? RuntimeContract.Modifying
            : RuntimeContract.Viewing;
        TypeSymbol definition = registry.LookupType(name: name) ??
                                throw new InvalidOperationException(message: $"The standard library has no '{name}'.");
        return registry.GetOrCreateResolution(genericDef: definition, typeArguments: [inner]);
    }

    private static IdentifierExpression Ident(string name, TypeSymbol type, SourceLocation loc)
    {
        return new IdentifierExpression(Name: name, Location: loc) { ResolvedType = type };
    }

    /// <summary>A call of the member routine <paramref name="name"/> on <paramref name="receiver"/>.</summary>
    private CallExpression Call(Expression receiver, string name, TypeSymbol? type, SourceLocation loc)
    {
        TypeSymbol receiverType = receiver.ResolvedType ??
                                  throw new InvalidOperationException(message: $"The receiver of '{name}' has no type.");
        RoutineInfo routine = registry.LookupMemberRoutine(type: receiverType, memberRoutineName: name) ??
                              throw new InvalidOperationException(
                                  message: $"The standard library has no '{receiverType.Name}.{name}' for an access block.");
        return new CallExpression(
            Callee: new MemberExpression(Object: receiver, MemberName: name, Location: loc),
            Arguments: [],
            Location: loc)
        {
            ResolvedRoutine = routine, ResolvedType = type ?? routine.ReturnType, IsSynthesizedLowering = true
        };
    }

    /// <summary>A call of the free routine <paramref name="name"/>, which takes no arguments.</summary>
    private CallExpression FreeCall(string name, SourceLocation loc)
    {
        RoutineInfo routine = registry.LookupRoutineOverload(baseName: name, argTypes: []) ??
                              throw new InvalidOperationException(
                                  message: $"The standard library has no '{name}' for an access block.");
        return new CallExpression(
            Callee: new IdentifierExpression(Name: name, Location: loc),
            Arguments: [],
            Location: loc) { ResolvedRoutine = routine, ResolvedType = routine.ReturnType, IsSynthesizedLowering = true };
    }

    private static ExpressionStatement Statement(Expression expr)
    {
        return new ExpressionStatement(Expression: expr, Location: expr.Location);
    }

    private static DeclarationStatement Binding(string name, Expression value, TypeSymbol type, SourceLocation loc,
        bool owesTeardown = false)
    {
        string typeName = type switch
        {
            RecordTypeSymbol { GenericDefinition: not null } r => r.GenericDefinition.Name,
            EntityTypeSymbol { GenericDefinition: not null } e => e.GenericDefinition.Name,
            _ => type.IsGenericResolution
                ? type.BareName
                : type.Name
        };
        var declaration = new VariableDeclaration(Name: name,
            Type: new TypeExpression(Name: typeName, GenericArguments: [], Location: loc) { ResolvedType = type },
            Initializer: value,
            Visibility: VisibilityModifier.Secret,
            Location: loc) { OwesTeardown = owesTeardown };
        return new DeclarationStatement(Declaration: declaration, Location: loc);
    }
}
