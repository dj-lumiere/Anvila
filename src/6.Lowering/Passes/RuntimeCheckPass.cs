using Builder.Declaration;
using Builder.Verification;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// The checks that can fail which the builder puts in front of statements of the program's own routines once their
/// bodies are analyzed. They go in before the recovery variants are made, and a routine that gets one can fail, so a
/// <c>try</c> or <c>check</c> above any call that reaches it receives the failure like any other.
/// <list type="bullet">
/// <item>A reference the builder cannot prove alive. An entity ends where its original ends, and its slot can hold a new
/// entity at once, so a reference keeps the generation its entity's slot had when the reference was made. Before each
/// statement that reaches a member through a reference the builder cannot prove comes from an original, the builder puts
/// <c>require_modifying_live[T](token: n)</c> (or the <c>Viewing</c> one, Link.rvr), which fails with
/// <c>EntityEndedError</c> once the slot's generation is no longer the reference's. A reference is unproven when it was
/// taken from a link (the arm of a <c>when</c> over one, a link used directly) or given by a call of a routine value (a
/// lambda may give one taken from what it captured), when a local was given one, or when it is a reference parameter
/// (or <c>me</c>) of a routine some call hands an unproven reference to, found across the program's routines until
/// nothing changes. A routine whose <c>me</c> is unproven keeps the generation <c>me</c>'s slot has when it starts and
/// compares with it (<c>require_entity_generation</c>). The routines a <c>try</c> or <c>check</c> over an expression is
/// made into count as the program's routines, so a use under the keyword is checked in there and the keyword receives
/// the failure. A statement that calls another routine may end an entity part way through, so each use of an unproven
/// reference in it is also checked where it is used: it is wrapped in <c>live_modifying[T](token:)</c> (or the
/// <c>Viewing</c> one), which checks it and gives it back. So is a reference a routine-value call gives, used in the same
/// expression (<c>f().x</c>). A reference handed to another routine carries its generation, and that routine checks it
/// before each use. A call's receiver (or an argument before a later one) is evaluated before the arguments after it, so
/// when one of those calls a routine and the receiver reaches an entity through an unproven reference
/// (<c>n.attach(world.remove(n))</c>, <c>n.kids.add_last(value: world.remove(n))</c>), the statement's calls up to the
/// last one in those arguments go into locals first, in the order they ran, and the reference is checked again after
/// them, right before the call.</item>
/// <item>A container a running loop may walk. When the shape check left a loop to check its container at run time
/// (RuntimeShapeUseLoweringPass opens the use), every call of an <c>@reshaping</c> routine and every subscript write
/// that may add a key first checks its receiver (<c>xs.require_shape_free()</c>, ShapeUse.rvr), which fails with
/// <c>ReshapingWhileInUseError</c> while a use of it is open.</item>
/// </list>
/// </summary>
internal sealed class RuntimeCheckPass(TypeRegistry registry)
{
    /// <summary>The routines of the program, by registry key, with their bodies. <c>Base</c> marks the routine a
    /// <c>try</c> or <c>check</c> over an expression was made into: its body is analyzed later, so its checks go in
    /// through <see cref="InsertInto"/>.</summary>
    private readonly Dictionary<string, (RoutineInfo Info, Statement Body, bool Base)> _routines =
        new(comparer: StringComparer.Ordinal);

    /// <summary>The files of the program's own routines.</summary>
    private readonly HashSet<string> _files = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Whether the routine kept under <paramref name="key"/> is one of the program's. The routine a <c>try</c> or
    /// <c>check</c> over an expression of the program was made into counts once it is made, which may be after the
    /// walk over the program (a recovery variant's copy of a routine's body makes its own when it is analyzed): it
    /// takes the names the expression reads as parameters, so a token handed there unproven is checked in its body.
    /// </summary>
    private bool Known(string key)
    {
        if (_routines.ContainsKey(key: key))
        {
            return true;
        }

        if (!registry.DeferredVariantBases.TryGetValue(key: key, value: out (RoutineInfo info, Statement body, bool) deferred) ||
            !deferred.info.IsSynthesized || deferred.info.Location is not { FileName: var file } || !_files.Contains(item: file))
        {
            return false;
        }

        _routines[key: key] = (deferred.info, deferred.body, true);
        return true;
    }

    /// <summary>The bodies checks already went into, so a body reached twice gets them once.</summary>
    private readonly HashSet<Statement> _checkedBodies = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>The parameters (and <c>me</c>) of each routine that may hold a token taken from a link.</summary>
    private readonly Dictionary<string, HashSet<string>> _unproven = new(comparer: StringComparer.Ordinal);

    private bool _changed;

    /// <summary>Whether a check went into the routine being walked.</summary>
    private bool _inserted;

    /// <summary>Whether references the builder could not prove alive are checked: the build checks references.</summary>
    private readonly bool _links = registry.Rules.ChecksAccessTokens;

    /// <summary>Whether changes to containers are checked: some loop checks its container at run time.</summary>
    private readonly bool _shapes = registry.RuntimeShapeUses && registry.Rules.ChecksAccessTokens;

    /// <summary>Runs over the program's own routines.</summary>
    public void Run(IEnumerable<Program> programs)
    {
        if (!_links && !_shapes)
        {
            return;
        }

        HashSet<string> files = _files;
        foreach (Program program in programs)
        {
            AstWalker.Walk(root: program,
                visit: node =>
                {
                    // A generic routine is checked in its definition: each instance is made from that body, and the
                    // check of a token over a type parameter does nothing in an instance whose type is not retroactive.
                    if (node is RoutineDeclaration { Body: BlockStatement body, ResolvedInfo: { } info } routine)
                    {
                        _routines.TryAdd(key: Key(routine: info), value: (info, body, false));
                        files.Add(item: routine.Location.FileName);
                    }
                });
        }

        // The routine a `try` or `check` over an expression of the program was made into takes the names the
        // expression reads as parameters: a token handed there unproven is checked in its body.
        foreach (string key in registry.DeferredVariantBases.Keys)
        {
            Known(key: key);
        }

        while (_links)
        {
            _changed = false;
            foreach ((string key, (RoutineInfo _, Statement body, bool _)) in _routines)
            {
                Walk(stmt: body, names: [.. Unproven(key: key)], insert: false);
            }

            if (!_changed)
            {
                break;
            }
        }

        foreach ((string key, (RoutineInfo info, Statement body, bool isBase)) in _routines)
        {
            if (isBase || !_checkedBodies.Add(item: body))
            {
                continue;
            }

            _inserted = false;
            InsertChecks(routine: info, body: body, names: [.. Unproven(key: key)]);
            // A routine that now checks a token can fail, so a `try` or `check` above a call of it receives the failure.
            if (_inserted)
            {
                info.HasThrow = true;
            }
        }
    }

    /// <summary>
    /// Puts the checks into another copy of a routine's body: the body a recovery variant is made from, captured before
    /// the routine was analyzed, so the variant checks the same tokens and its keyword receives the failure.
    /// </summary>
    public void InsertInto(RoutineInfo routine, Statement body)
    {
        if (!Known(key: Key(routine: routine)) || !_checkedBodies.Add(item: body))
        {
            return;
        }

        HashSet<string> names = _unproven.TryGetValue(key: Key(routine: routine), value: out HashSet<string>? known)
            ? [.. known]
            : new HashSet<string>(comparer: StringComparer.Ordinal);
        if (_shapes || _links)
        {
            InsertChecks(routine: routine, body: body, names: names);
        }
    }

    /// <summary>Puts the checks into a routine's body. When <c>me</c> is unproven, the body first keeps the generation
    /// its slot has, which each check of <c>me</c> compares with: the call that handed it was checked right before.</summary>
    private void InsertChecks(RoutineInfo routine, Statement body, HashSet<string> names)
    {
        Walk(stmt: body, names: names, insert: true);
        if (_links && names.Contains(item: "me") && body is BlockStatement block &&
            MeGenerationLocal(routine: routine, location: block.Location) is { } generation)
        {
            block.Statements.Insert(index: 0, item: generation);
            _inserted = true;
        }
    }

    private HashSet<string> Unproven(string key)
    {
        if (!_unproven.TryGetValue(key: key, value: out HashSet<string>? names))
        {
            names = new HashSet<string>(comparer: StringComparer.Ordinal);
            _unproven[key: key] = names;
        }

        return names;
    }

    /// <summary>
    /// Walks a statement with the names that hold unproven tokens: notes the routines they are handed to, and with
    /// <paramref name="insert"/> puts the checks in front of the statements that reach a member through one.
    /// </summary>
    private void Walk(Statement stmt, HashSet<string> names, bool insert)
    {
        switch (stmt)
        {
            case BlockStatement block:
            {
                // A local given an unproven token holds one from then on, to the end of the block.
                HashSet<string> scope = names;
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    // The statement as written is noted and checked, then its uses are wrapped where it calls more.
                    Statement inner = block.Statements[index: i];
                    Note(stmt: inner, names: scope);
                    if (insert)
                    {
                        List<Statement> checks = Checks(stmt: inner, names: scope);
                        if (_links && WrapLiveUses(stmt: inner, names: scope) is { } wrapped)
                        {
                            block.Statements[index: i] = wrapped;
                        }

                        if (_links && SpillArguments(stmt: block.Statements[index: i], names: scope) is var (before, residual))
                        {
                            block.Statements[index: i] = residual;
                            checks.AddRange(collection: before);
                        }

                        if (checks.Count > 0)
                        {
                            _inserted = true;
                            block.Statements.InsertRange(index: i, collection: checks);
                            i += checks.Count;
                        }
                    }

                    Walk(stmt: block.Statements[index: i], names: scope, insert: insert);
                    if (GivenUnproven(stmt: inner, names: scope) is { } local && !scope.Contains(item: local))
                    {
                        scope = [.. scope, local];
                    }
                }

                return;
            }
            case IfStatement branch:
                Walk(stmt: branch.ThenStatement, names: names, insert: insert);
                if (branch.ElseStatement is { } other)
                {
                    Walk(stmt: other, names: names, insert: insert);
                }

                return;
            case WhileStatement loop:
                Walk(stmt: loop.Body, names: names, insert: insert);
                return;
            case LoopStatement loop:
                Walk(stmt: loop.Body, names: names, insert: insert);
                return;
            case DangerStatement danger:
                Walk(stmt: danger.Body, names: names, insert: insert);
                return;
            case UsingStatement scope:
                Walk(stmt: scope.Body, names: names, insert: insert);
                return;
            case AccessStatement access:
                Walk(stmt: access.Body, names: names, insert: insert);
                return;
            case ExpressionStatement or AssignmentStatement or DeclarationStatement or ReturnStatement or DropStatement
                when !insert:
                // A statement that is a whole arm or branch: what it hands on is noted here.
                Note(stmt: stmt, names: names);
                return;
            case WhenStatement match:
            {
                // The arms of a `when` over a link bind the token it gives, which is unproven.
                bool fromLink = IsUnproven(expr: match.Expression, names: names);
                for (int i = 0; i < match.Clauses.Count; i++)
                {
                    // An arm of one statement becomes a block, so a check can go in front of it.
                    if (insert && match.Clauses[index: i].Body is not BlockStatement)
                    {
                        match.Clauses[index: i] = match.Clauses[index: i] with
                        {
                            Body = new BlockStatement(Statements: [match.Clauses[index: i].Body],
                                Location: match.Clauses[index: i].Body.Location)
                        };
                    }

                    WhenClause clause = match.Clauses[index: i];
                    string? bound = fromLink
                        ? clause.Pattern switch
                        {
                            ElsePattern { VariableName: { } name } => name,
                            TypePattern { VariableName: { } name } => name,
                            _ => null
                        }
                        : null;
                    HashSet<string> armNames = bound != null ? [.. names, bound] : names;
                    Walk(stmt: clause.Body, names: armNames, insert: insert);
                }

                return;
            }
        }
    }

    /// <summary>The local a statement gives an unproven token (<c>var m = n</c>, <c>m = n</c>), or null.</summary>
    private string? GivenUnproven(Statement stmt, HashSet<string> names)
    {
        if (!_links)
        {
            return null;
        }

        return stmt switch
        {
            DeclarationStatement { Declaration: VariableDeclaration { Name: var name, Initializer: { } init } }
                when CheckedEntity(type: init.ResolvedType) != null && IsUnproven(expr: init, names: names) => name,
            AssignmentStatement { Target: IdentifierExpression { Name: var name }, Value: var value }
                when CheckedEntity(type: value.ResolvedType) != null && IsUnproven(expr: value, names: names) => name,
            _ => null
        };
    }

    /// <summary>The expressions a statement evaluates itself, not those of the statements nested in it.</summary>
    private static IEnumerable<Expression> OwnExpressions(Statement stmt)
    {
        return stmt switch
        {
            ExpressionStatement e => [e.Expression],
            AssignmentStatement a => [a.Target, a.Value],
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } } => [init],
            ReturnStatement { Value: { } v } => [v],
            DropStatement d => [d.Expression],
            IfStatement i => [i.Condition],
            WhenStatement w => [w.Expression],
            _ => []
        };
    }

    /// <summary>Notes each routine of the program a statement hands an unproven token to.</summary>
    private void Note(Statement stmt, HashSet<string> names)
    {
        foreach (Expression root in OwnExpressions(stmt: stmt))
        {
            NoteIn(root: root, names: names);
        }
    }

    /// <summary><see cref="Note"/> over one expression, the expression under a <c>try</c> / <c>check</c> included.</summary>
    private void NoteIn(Expression root, HashSet<string> names)
    {
        {
            AstWalker.WalkExpressions(root: root,
                visit: e =>
                {
                    if (e is RecoveryExpression recovery)
                    {
                        NoteIn(root: recovery.Inner, names: names);
                        // The routine the keyword's expression was made into is handed the names it reads.
                        if (recovery.LoweredCall is { } lowered)
                        {
                            NoteIn(root: lowered, names: names);
                        }

                        return;
                    }

                    // A call written with type arguments (`read[Node](n)`) is still its own node here.
                    (RoutineInfo? called, Expression? receiver, List<Expression> arguments) = e switch
                    {
                        CallExpression { ResolvedRoutine: { } direct } call =>
                            (direct, (call.Callee as MemberExpression)?.Object, call.Arguments),
                        GenericMemberRoutineCallExpression { ElementCall: null, ResolvedRoutine: { } generic } written =>
                            (generic, written.IsFree ? null : written.Object, written.Arguments),
                        _ => (null, null, [])
                    };
                    if (called == null)
                    {
                        return;
                    }

                    // A call through a token runs the token's forwarder of the entity's routine.
                    RoutineInfo routine = called;
                    while (routine.WrapperForwarderInnerMemberRoutine is { } inner)
                    {
                        routine = inner;
                    }

                    if (!Known(key: Key(routine: routine)))
                    {
                        return;
                    }

                    if (receiver != null && CheckedEntity(type: routine.OwnerType) != null &&
                        IsUnproven(expr: receiver, names: names))
                    {
                        Mark(routine: routine, parameter: "me");
                    }

                    List<ParamInfo> parameters = routine.Parameters.Where(predicate: p => p.Name != "me").ToList();
                    for (int i = 0; i < arguments.Count; i++)
                    {
                        Expression argument = arguments[index: i];
                        ParamInfo? parameter = argument is NamedArgumentExpression named
                            ? parameters.FirstOrDefault(predicate: p => p.Name == named.Name)
                            : i < parameters.Count
                                ? parameters[index: i]
                                : null;
                        Expression value = argument is NamedArgumentExpression wrapped ? wrapped.Value : argument;
                        if (parameter != null && CheckedEntity(type: parameter.Type) != null &&
                            IsUnproven(expr: value, names: names))
                        {
                            Mark(routine: routine, parameter: parameter.Name);
                        }
                    }
                });
        }
    }

    private void Mark(RoutineInfo routine, string parameter)
    {
        if (Unproven(key: Key(routine: routine)).Add(item: parameter))
        {
            _changed = true;
        }
    }

    /// <summary>Whether <paramref name="expr"/> reaches an entity through an unproven token: a link (or a token on one), a
    /// name that holds an unproven token, or a member or element reached from one.</summary>
    private static bool IsUnproven(Expression expr, HashSet<string> names)
    {
        if (ImplicitToken.IsLink(type: expr.ResolvedType) ||
            expr.ResolvedType is { BareName: RuntimeContract.Viewing or RuntimeContract.Modifying, TypeArguments: [{ } held] } &&
            ImplicitToken.IsLink(type: held))
        {
            return true;
        }

        return expr switch
        {
            IdentifierExpression id => names.Contains(item: id.Name),
            MemberExpression member => member.TakesLink || IsUnproven(expr: member.Object, names: names),
            IndexExpression element => IsUnproven(expr: element.Object, names: names),
            // A link used where a reference is expected (`hit(e: h.target)`) is taken first: the token it gives names
            // an entity that may end while it is used.
            CallExpression { ResolvedRoutine: { } taken } when IsLinkTake(routine: taken) => true,
            CallExpression call => GivesUnprovenToken(call: call),
            _ => false
        };
    }

    /// <summary>Whether <paramref name="routine"/> takes the token a link holds (<c>take_modifying_link</c>).</summary>
    private static bool IsLinkTake(RoutineInfo routine)
    {
        return (routine.GenericDefinition ?? routine).Name is RuntimeContract.TakeModifyingLink or
            RuntimeContract.TakeViewingLink;
    }

    /// <summary>Whether <paramref name="call"/> calls a routine value and gives a token: the value may be a lambda that
    /// gives a reference taken from what it captured, whose entity may have ended before the call.</summary>
    private static bool GivesUnprovenToken(CallExpression call)
    {
        return call.Callee.ResolvedType is RoutineTypeSymbol &&
               call.ResolvedType?.BareName is RuntimeContract.Viewing or RuntimeContract.Modifying;
    }

    /// <summary>
    /// <paramref name="stmt"/> with each token a routine-value call gives, used in the same expression (<c>f().x</c>,
    /// <c>f()[0]</c>, <c>f().rename()</c>), wrapped in the check that gives it back, or null when it has none. Only the
    /// statement's own expressions are rewritten: a nested statement is reached by its own block.
    /// </summary>
    private Statement? WrapLiveUses(Statement stmt, HashSet<string> names)
    {
        // A statement that calls a routine besides the uses themselves may end an entity part way through: each use of
        // an unproven reference in it is then checked where it is used, not only before the statement.
        var wrapper = new LiveUseWrapper(pass: this, names: CallsBesidesUses(stmt: stmt, names: names) ? names : null);
        Statement rewritten = stmt switch
        {
            ExpressionStatement e => wrapper.VisitExpression(expr: e.Expression) is var x && !ReferenceEquals(objA: x, objB: e.Expression)
                ? e with { Expression = x }
                : e,
            AssignmentStatement a when (wrapper.VisitExpression(expr: a.Target), wrapper.VisitExpression(expr: a.Value)) is var (t, v) &&
                                       (!ReferenceEquals(objA: t, objB: a.Target) || !ReferenceEquals(objA: v, objB: a.Value)) =>
                a with { Target = t, Value = v },
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } local } d =>
                wrapper.VisitExpression(expr: init) is var i && !ReferenceEquals(objA: i, objB: init)
                    ? d with { Declaration = local with { Initializer = i } }
                    : d,
            ReturnStatement { Value: { } value } r => wrapper.VisitExpression(expr: value) is var rv && !ReferenceEquals(objA: rv, objB: value)
                ? r with { Value = rv }
                : r,
            DropStatement d => wrapper.VisitExpression(expr: d.Expression) is var de && !ReferenceEquals(objA: de, objB: d.Expression)
                ? d with { Expression = de }
                : d,
            IfStatement f => wrapper.VisitExpression(expr: f.Condition) is var c && !ReferenceEquals(objA: c, objB: f.Condition)
                ? f with { Condition = c }
                : f,
            WhenStatement w => wrapper.VisitExpression(expr: w.Expression) is var we && !ReferenceEquals(objA: we, objB: w.Expression)
                ? w with { Expression = we }
                : w,
            _ => stmt
        };
        if (ReferenceEquals(objA: rewritten, objB: stmt))
        {
            return null;
        }

        _inserted = true;
        return rewritten;
    }

    /// <summary>
    /// Splits a statement holding a call whose receiver (or an argument before a later one) reaches an entity through an
    /// unproven reference while a later argument calls a routine, which may end that entity. What the statement evaluates
    /// up to the last call in those arguments goes into locals first, in the order it ran: each call, and each number,
    /// choice or flags value read out of an entity (a copy taken while the entity was there). The references the rest of
    /// the statement still reaches an entity through are then checked again, right before the statement runs with the
    /// locals. Null when the statement has no such call. Only eagerly evaluated positions count: the right side of
    /// <c>and</c>/<c>or</c>/<c>??</c>, the arms of a <c>when</c> and the expression under a <c>try</c>/<c>check</c> run on
    /// their own.
    /// </summary>
    private (List<Statement> Before, Statement Residual)? SpillArguments(Statement stmt, HashSet<string> names)
    {
        Expression? root = stmt switch
        {
            ExpressionStatement e => e.Expression,
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } } => init,
            ReturnStatement { Value: { } value } => value,
            DropStatement d => d.Expression,
            AssignmentStatement a => a.Value,
            IfStatement f => f.Condition,
            WhenStatement w => w.Expression,
            _ => null
        };
        if (root == null)
        {
            return null;
        }

        var scan = new ArgumentSpiller(names: names, limit: -1, stealsEntities: false);
        scan.VisitExpression(expr: root);
        if (scan.Limit < 0)
        {
            return null;
        }

        var spiller = new ArgumentSpiller(names: names, limit: scan.Limit,
            stealsEntities: !registry.EntitiesAreSharedIn(filePath: stmt.Location.FileName));
        Expression rewritten = spiller.VisitExpression(expr: root);
        if (spiller.Unhoistable || spiller.Hoisted.Count == 0)
        {
            return null;
        }

        var before = new List<Statement>(collection: spiller.Hoisted);
        foreach (IdentifierExpression name in UncheckedUses(expr: rewritten, names: names))
        {
            if (Check(name: name, location: stmt.Location) is { } check)
            {
                before.Add(item: new ExpressionStatement(Expression: check, Location: stmt.Location));
            }
        }

        _inserted = true;
        Statement residual = stmt switch
        {
            ExpressionStatement e => e with { Expression = rewritten },
            DeclarationStatement { Declaration: VariableDeclaration local } d =>
                d with { Declaration = local with { Initializer = rewritten } },
            ReturnStatement r => r with { Value = rewritten },
            DropStatement d => d with { Expression = rewritten },
            AssignmentStatement a => a with { Value = rewritten },
            IfStatement f => f with { Condition = rewritten },
            WhenStatement w => w with { Expression = rewritten },
            _ => stmt
        };
        return (before, residual);
    }

    /// <summary>The unproven names <paramref name="expr"/> reaches a member or an element through without a check of its
    /// own (<c>me.attach(...)</c>): a use wrapped in <c>live_modifying</c> checks itself where it is evaluated, and a name
    /// handed to a routine carries its generation there. One each.</summary>
    private static List<IdentifierExpression> UncheckedUses(Expression expr, HashSet<string> names)
    {
        var found = new List<IdentifierExpression>();
        AstWalker.WalkExpressions(root: expr,
            visit: e =>
            {
                Expression? reached = e switch
                {
                    MemberExpression { TakesLink: false } member => member.Object,
                    IndexExpression element => element.Object,
                    _ => null
                };
                if (reached is IdentifierExpression id && names.Contains(item: id.Name) &&
                    CheckedEntity(type: id.ResolvedType) != null && found.All(predicate: seen => seen.Name != id.Name))
                {
                    found.Add(item: id);
                }
            });
        return found;
    }

    /// <summary>Whether <paramref name="call"/> is a check this pass put in (<c>live_modifying</c>) or the take of a
    /// link's token: neither ends an entity, and each stays where it is evaluated.</summary>
    private static bool IsUseMarker(Expression call)
    {
        return call is CallExpression { IsSynthesizedLowering: true, ResolvedRoutine: { } routine } &&
               (IsLinkTake(routine: routine) ||
                (routine.GenericDefinition ?? routine).Name is RuntimeContract.LiveModifying or RuntimeContract.LiveViewing);
    }

    private static int _spillTemp;

    /// <summary>
    /// Walks one expression in evaluation order, numbering the calls it reaches (post-order), the checks of
    /// <see cref="IsUseMarker"/> aside. The scan (<c>limit</c> -1) finds each call whose receiver or earlier argument
    /// reaches an entity through an unproven name while a later argument calls a routine, and sets <see cref="Limit"/> to
    /// the number of the last call in its arguments. The spill moves into a fresh <c>var __rc_arg_N</c> in
    /// <see cref="Hoisted"/> each call numbered up to <c>limit</c>, and each number, choice or flags read out of a member
    /// or an element before that call. An entity a temp holds is handed on with <c>steal</c>, unless only a routine of it
    /// is called.
    /// </summary>
    private sealed class ArgumentSpiller(HashSet<string> names, int limit, bool stealsEntities) : AstRewriter
    {
        private int _index;

        /// <summary>The number of the last call the spill moves, or -1 when no call needs it.</summary>
        public int Limit { get; private set; } = -1;

        /// <summary>A value the spill would move is untyped or none, so the statement is left as it is.</summary>
        public bool Unhoistable { get; private set; }

        /// <summary>The moved values' locals, in evaluation order.</summary>
        public List<Statement> Hoisted { get; } = [];

        /// <summary>The calls whose value is the receiver of another call of the statement, with that other call.</summary>
        private readonly Dictionary<Expression, Expression> _receiverOf = new(comparer: ReferenceEqualityComparer.Instance);

        /// <summary>The member expressions that name a called routine (<c>n.attach</c> of <c>n.attach(...)</c>): no read.</summary>
        private readonly HashSet<Expression> _callees = new(comparer: ReferenceEqualityComparer.Instance);

        public override Expression VisitExpression(Expression expr)
        {
            // A number read out of a member or an element before the last moved call is a copy taken where it was
            // evaluated: it moves too, so it is not read after a call that may end its entity.
            if (limit < 0 || _index > limit || expr is not (MemberExpression { TakesLink: false } or IndexExpression) ||
                _callees.Contains(item: expr) || !IsCopiedOut(type: expr.ResolvedType))
            {
                return base.VisitExpression(expr: expr);
            }

            return Hoist(value: base.VisitExpression(expr: expr), original: expr);
        }

        protected override Expression VisitCall(CallExpression e)
        {
            _callees.Add(item: e.Callee);
            Expression? receiver = (e.Callee as MemberExpression)?.Object;
            return Spill(original: e, receiver: receiver, arguments: e.Arguments, rewrite: () => base.VisitCall(e: e));
        }

        protected override Expression VisitGenericMemberRoutineCall(GenericMemberRoutineCallExpression e)
        {
            Expression? receiver = e.IsFree ? null : e.Object;
            return Spill(original: e, receiver: receiver, arguments: e.Arguments,
                rewrite: () => base.VisitGenericMemberRoutineCall(e: e));
        }

        private Expression Spill(Expression original, Expression? receiver, List<Expression> arguments,
            Func<Expression> rewrite)
        {
            if (receiver is CallExpression or GenericMemberRoutineCallExpression)
            {
                _receiverOf[key: receiver] = original;
            }

            if (IsUseMarker(call: original))
            {
                return rewrite();
            }

            Expression rewritten = rewrite();
            int index = _index++;
            if (limit < 0)
            {
                NoteHazard(receiver: receiver, arguments: arguments, index: index);
                return rewritten;
            }

            return index > limit
                ? rewritten
                : Hoist(value: rewritten, original: original);
        }

        /// <summary>Moves <paramref name="value"/> into a fresh local and gives what reads it there.</summary>
        private Expression Hoist(Expression value, Expression original)
        {
            if (value.ResolvedType is not { IsNone: false } type)
            {
                Unhoistable = true;
                return value;
            }

            string tempName = $"__rc_arg_{Interlocked.Increment(location: ref _spillTemp)}";
            Hoisted.Add(item: new DeclarationStatement(Declaration: new VariableDeclaration(
                    Name: tempName,
                    Type: null,
                    Initializer: value,
                    Visibility: VisibilityModifier.Secret,
                    Location: original.Location),
                Location: original.Location));
            var reference = new IdentifierExpression(Name: tempName, Location: original.Location) { ResolvedType = type };
            // An entity whose routine the statement only calls stays the temp's own, which the scope tears down. One that
            // is handed on is stolen from the temp.
            return stealsEntities && type.Category == TypeCategory.Entity && !_receiverOf.ContainsKey(key: original)
                ? new StealExpression(Operand: reference, Location: original.Location) { ResolvedType = type }
                : reference;
        }

        /// <summary>In the scan: when a later argument of the call numbered <paramref name="index"/> calls a routine while
        /// its receiver or an earlier argument reaches an entity through an unproven name, every call up to the one before
        /// it moves.</summary>
        private void NoteHazard(Expression? receiver, List<Expression> arguments, int index)
        {
            int last = arguments.FindLastIndex(match: CallsRoutine);
            if (last < 0)
            {
                return;
            }

            var earlier = new List<Expression>();
            if (receiver != null)
            {
                earlier.Add(item: receiver);
            }

            earlier.AddRange(collection: arguments.Take(count: last));
            bool found = false;
            foreach (Expression part in earlier)
            {
                AstWalker.WalkExpressions(root: part,
                    visit: x => found |= x is IdentifierExpression id && names.Contains(item: id.Name) &&
                                         CheckedEntity(type: id.ResolvedType) != null);
            }

            if (found)
            {
                Limit = Math.Max(val1: Limit, val2: index - 1);
            }
        }

        /// <summary>Whether a value of <paramref name="type"/> is a number, a choice or flags: one register, not a
        /// reference or a link.</summary>
        private static bool IsCopiedOut(TypeSymbol? type)
        {
            return CheckedEntity(type: type) == null && !ImplicitToken.IsLink(type: type) &&
                   type is RecordTypeSymbol { BackendType: not null, MemberVariables.Count: 0 } or ChoiceTypeSymbol or
                       FlagsTypeSymbol;
        }

        /// <summary>Whether <paramref name="expr"/> calls a routine, the checks of <see cref="IsUseMarker"/> aside.</summary>
        private static bool CallsRoutine(Expression expr)
        {
            bool found = false;
            AstWalker.WalkExpressions(root: expr,
                visit: x => found |= x is CallExpression or GenericMemberRoutineCallExpression && !IsUseMarker(call: x));
            return found;
        }

        protected override Expression VisitBinary(BinaryExpression e)
        {
            if (e.Operator is not (BinaryOperator.And or BinaryOperator.Or or BinaryOperator.NoneCoalesce))
            {
                return base.VisitBinary(e: e);
            }

            // The right side runs only when the left side lets it: it is left where it is.
            Expression left = VisitExpression(expr: e.Left);
            return ReferenceEquals(objA: left, objB: e.Left)
                ? e
                : e with { Left = left };
        }

        protected override Expression VisitConditional(ConditionalExpression e)
        {
            return e;
        }

        protected override Expression VisitWhenExpression(WhenExpression e)
        {
            return e;
        }

        protected override Expression VisitRecovery(RecoveryExpression e)
        {
            return e;
        }

        protected override Expression VisitBlockExpression(BlockExpression e)
        {
            return e;
        }

        protected override Expression VisitWork(WorkExpression e)
        {
            return e;
        }
    }

    /// <summary><c>live_modifying[T](token: value)</c> (or the <c>Viewing</c> one) over a token a routine-value call
    /// gives, when the token reaches a retroactive entity or a type parameter, else null.</summary>
    private CallExpression? LiveCheck(Expression value)
    {
        if (value.ResolvedType is not { BareName: var wrapper, TypeArguments: [{ } over] } tokenType ||
            wrapper is not (RuntimeContract.Modifying or RuntimeContract.Viewing) || CheckedEntity(type: tokenType) == null)
        {
            return null;
        }

        string name = wrapper == RuntimeContract.Modifying
            ? RuntimeContract.LiveModifying
            : RuntimeContract.LiveViewing;
        if (registry.LookupGenericOverload(name: name, module: "System") is not { } definition)
        {
            return null;
        }

        RoutineInfo instance = registry.GetOrCreateRoutineResolution(genericDef: definition, typeArguments: [over]);
        return new CallExpression(
            Callee: new IdentifierExpression(Name: name, Location: value.Location) { ResolvedRoutine = instance },
            Arguments:
            [
                new NamedArgumentExpression(Name: "token", Value: value, Location: value.Location)
                {
                    ResolvedType = tokenType
                }
            ],
            Location: value.Location)
        {
            ResolvedRoutine = instance,
            ResolvedType = tokenType,
            LoweringKind = CallLoweringKind.DirectRoutine,
            IsSynthesizedLowering = true,
            // Over a type parameter each instance of the routine binds its own instance of the check.
            TypeArguments = over is GenericParameterTypeSymbol
                ? [new TypeExpression(Name: over.Name, GenericArguments: null, Location: value.Location)]
                : null
        };
    }

    /// <summary>Wraps each token a routine-value call gives where a member or an element is reached through it. A
    /// <c>try</c> / <c>check</c> is left as it is: its expression is checked in the routine it was made into.</summary>
    /// <summary>Whether a statement's own expressions call a routine other than one called on an unproven name
    /// (<c>n.rename(to: t)</c>): only another call can end an entity between the check before the statement and the use.
    /// The arguments of such a call are calls of their own when they call anything.</summary>
    private static bool CallsBesidesUses(Statement stmt, HashSet<string> names)
    {
        bool found = false;
        foreach (Expression root in OwnExpressions(stmt: stmt))
        {
            AstWalker.WalkExpressions(root: root,
                visit: e =>
                {
                    if (found)
                    {
                        return;
                    }

                    found = e switch
                    {
                        CallExpression { Callee: MemberExpression { Object: IdentifierExpression id } }
                            when names.Contains(item: id.Name) => false,
                        CallExpression { IsSynthesizedLowering: true, ResolvedRoutine: { } called } when IsLinkTake(routine: called) => false,
                        CallExpression or GenericMemberRoutineCallExpression => true,
                        _ => false
                    };
                });
        }

        return found;
    }

    private sealed class LiveUseWrapper(RuntimeCheckPass pass, HashSet<string>? names) : AstRewriter
    {
        /// <summary>The use of an unproven reference as the object of a member or an element, wrapped in its check, or
        /// null when <paramref name="obj"/> is not one.</summary>
        private Expression? CheckedUse(Expression obj)
        {
            return names != null && obj is IdentifierExpression id && names.Contains(item: id.Name) &&
                   CheckedEntity(type: id.ResolvedType) != null
                ? pass.LiveCheck(value: id)
                : null;
        }

        protected override Expression VisitRecovery(RecoveryExpression e)
        {
            return e;
        }

        protected override Expression VisitMember(MemberExpression e)
        {
            Expression visited = base.VisitMember(e: e);
            if (visited is MemberExpression { TakesLink: false } use && CheckedUse(obj: use.Object) is { } checkedUse)
            {
                return use with { Object = checkedUse };
            }

            return visited is MemberExpression { TakesLink: false, Object: CallExpression call } member &&
                   GivesUnprovenToken(call: call) && pass.LiveCheck(value: call) is { } live
                ? member with { Object = live }
                : visited;
        }

        protected override Expression VisitIndex(IndexExpression e)
        {
            Expression visited = base.VisitIndex(e: e);
            if (visited is IndexExpression use && CheckedUse(obj: use.Object) is { } checkedUse)
            {
                return use with { Object = checkedUse };
            }

            return visited is IndexExpression { Object: CallExpression call } element &&
                   GivesUnprovenToken(call: call) && pass.LiveCheck(value: call) is { } live
                ? element with { Object = live }
                : visited;
        }
    }

    /// <summary>The key a routine's checks are kept under: a generic routine's definition for each of its instances.</summary>
    private static string Key(RoutineInfo routine)
    {
        // A recovery variant checks what the routine it recovers checks.
        while (routine.RecoveryOf is { } recovered)
        {
            routine = recovered;
        }

        return routine.GenericDefinition is { } definition && !ReferenceEquals(objA: definition, objB: routine)
            ? definition.RegistryKey
            : routine.RegistryKey;
    }

    /// <summary>The type a token (or an entity itself, as <c>me</c>) reaches when its entity may end while the token is
    /// used: an entity, or a type parameter an instance may bind to one. Null for any other.</summary>
    private static TypeSymbol? CheckedEntity(TypeSymbol? type)
    {
        if (type?.BareName is RuntimeContract.Viewing or RuntimeContract.Modifying && type.TypeArguments is [{ } inner])
        {
            type = inner;
        }

        return type switch
        {
            EntityTypeSymbol entity => entity,
            GenericParameterTypeSymbol parameter => parameter,
            _ => null
        };
    }

    /// <summary>The checks to put before a statement: one per unproven name it reaches a member through, then one per
    /// container it may add to or remove from.</summary>
    private List<Statement> Checks(Statement stmt, HashSet<string> names)
    {
        var checks = new List<Statement>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (Expression root in _links ? OwnExpressions(stmt: stmt) : [])
        {
            AstWalker.WalkExpressions(root: root,
                visit: e =>
                {
                    if (e is not MemberExpression { TakesLink: false } member ||
                        Root(expr: member.Object) is not { } name || !names.Contains(item: name.Name) ||
                        CheckedEntity(type: name.ResolvedType) is not { } entity || !seen.Add(item: name.Name))
                    {
                        return;
                    }

                    if (Check(name: name, location: stmt.Location) is { } check)
                    {
                        checks.Add(item: new ExpressionStatement(Expression: check, Location: stmt.Location));
                    }
                });
        }

        if (_shapes)
        {
            var reshaped = new HashSet<string>(comparer: StringComparer.Ordinal);
            foreach (Expression receiver in ReshapedReceivers(stmt: stmt))
            {
                if (PathText(expr: receiver) is { } path && reshaped.Add(item: path) &&
                    MemberCall(receiver: receiver, name: RuntimeContract.ShapeUse.RequireFree, registry: registry,
                        location: stmt.Location) is { } check)
                {
                    checks.Add(item: new ExpressionStatement(Expression: check, Location: stmt.Location));
                }
            }
        }

        return checks;
    }

    /// <summary>
    /// The containers a statement's own calls of <c>@reshaping</c> routines and subscript writes that may add a key
    /// change, when each is a plain name or member variable path (evaluated again by the check without effect).
    /// Calls inside a nested block or lambda belong to their own statement.
    /// </summary>
    internal static List<Expression> ReshapedReceivers(Statement stmt)
    {
        var found = new List<Expression>();
        foreach (Expression root in OwnExpressions(stmt: stmt))
        {
            AstWalker.WalkExpressions(root: root,
                visit: e =>
                {
                    if (e is IndexExpression { ResolvedSetItem.IsReshaping: true, Object: var container } &&
                        PathText(expr: container) != null && container.ResolvedType is not null)
                    {
                        found.Add(item: container);
                    }
                    else if (e is CallExpression
                        {
                            ResolvedRoutine.IsReshaping: true, Callee: MemberExpression { Object: var receiver }
                        } && PathText(expr: receiver) != null && receiver.ResolvedType is not null)
                    {
                        found.Add(item: receiver);
                    }
                });
        }

        return found;
    }

    /// <summary>The text of a plain name or member variable path (<c>shelf.items</c>), or null for anything else.</summary>
    internal static string? PathText(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression { TakesLink: false } member when PathText(expr: member.Object) is { } owner =>
                $"{owner}.{member.MemberName}",
            _ => null
        };
    }

    /// <summary>
    /// <c>receiver.name()</c> on the container a plain path names: a token is a way to the entity, so the routine is
    /// the entity's own (<c>T.require_shape_free</c>, ShapeUse.rvr). Null when the container is not an entity.
    /// </summary>
    internal static CallExpression? MemberCall(Expression receiver, string name, TypeRegistry registry,
        SourceLocation location)
    {
        TypeSymbol? type = receiver.ResolvedType;
        if (type?.BareName is RuntimeContract.Viewing or RuntimeContract.Modifying && type.TypeArguments is [{ } inner])
        {
            type = inner;
        }

        if (type is not EntityTypeSymbol entity ||
            registry.LookupMemberRoutineOverload(type: entity, memberRoutineName: name, argTypes: []) is not { } routine)
        {
            return null;
        }

        return new CallExpression(
            Callee: new MemberExpression(Object: receiver with { }, MemberName: name, Location: location)
            {
                ResolvedType = routine.ReturnType
            },
            Arguments: [],
            Location: location)
        {
            ResolvedRoutine = routine,
            ResolvedType = routine.ReturnType,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: routine),
            IsSynthesizedLowering = true
        };
    }

    /// <summary>The local that keeps the generation <c>me</c>'s slot had when a routine started, in a routine whose
    /// <c>me</c> a reference the builder could not prove alive may be handed to.</summary>
    private const string MeGeneration = "__me_generation";

    /// <summary>The check of one unproven name: <c>require_modifying_live[T](token: n)</c> (or the <c>Viewing</c> one)
    /// for a token, <c>me.require_entity_generation(generation: __me_generation)</c> for <c>me</c>. Null when the name is
    /// neither.</summary>
    private CallExpression? Check(IdentifierExpression name, SourceLocation location)
    {
        if (name.ResolvedType is EntityTypeSymbol entity)
        {
            return name.Name == "me" && registry.LookupMemberRoutineOverload(type: entity,
                memberRoutineName: RuntimeContract.RequireEntityGeneration, argTypes: [U64]) is { } require
                ? new CallExpression(
                    Callee: new MemberExpression(Object: name with { }, MemberName: RuntimeContract.RequireEntityGeneration,
                        Location: location) { ResolvedType = require.ReturnType },
                    Arguments:
                    [
                        new NamedArgumentExpression(Name: "generation",
                            Value: new IdentifierExpression(Name: MeGeneration, Location: location) { ResolvedType = U64 },
                            Location: location) { ResolvedType = U64 }
                    ],
                    Location: location)
                {
                    ResolvedRoutine = require,
                    ResolvedType = require.ReturnType,
                    LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: require),
                    IsSynthesizedLowering = true
                }
                : null;
        }

        if (name.ResolvedType is not { BareName: var wrapper, TypeArguments: [{ } over] } tokenType ||
            wrapper is not (RuntimeContract.Modifying or RuntimeContract.Viewing))
        {
            return null;
        }

        string routine = wrapper == RuntimeContract.Modifying
            ? RuntimeContract.RequireModifyingLive
            : RuntimeContract.RequireViewingLive;
        if (registry.LookupGenericOverload(name: routine, module: "System") is not { } definition)
        {
            return null;
        }

        RoutineInfo instance = registry.GetOrCreateRoutineResolution(genericDef: definition, typeArguments: [over]);
        return new CallExpression(
            Callee: new IdentifierExpression(Name: routine, Location: location) { ResolvedRoutine = instance },
            Arguments:
            [
                new NamedArgumentExpression(Name: "token", Value: name with { }, Location: location)
                {
                    ResolvedType = tokenType
                }
            ],
            Location: location)
        {
            ResolvedRoutine = instance,
            ResolvedType = instance.ReturnType,
            LoweringKind = CallLoweringKind.DirectRoutine,
            IsSynthesizedLowering = true,
            // Over a type parameter each instance of the routine binds its own instance of the check.
            TypeArguments = over is GenericParameterTypeSymbol
                ? [new TypeExpression(Name: over.Name, GenericArguments: null, Location: location)]
                : null
        };
    }

    /// <summary><c>var __me_generation = me.entity_generation()</c>, the first statement of a routine whose <c>me</c> is
    /// unproven, or null when <c>me</c> is no entity.</summary>
    private DeclarationStatement? MeGenerationLocal(RoutineInfo routine, SourceLocation location)
    {
        if (routine.OwnerType is not EntityTypeSymbol owner ||
            registry.LookupMemberRoutineOverload(type: owner, memberRoutineName: RuntimeContract.EntityGeneration,
                argTypes: []) is not { } generation)
        {
            return null;
        }

        var call = new CallExpression(
            Callee: new MemberExpression(Object: new IdentifierExpression(Name: "me", Location: location) { ResolvedType = owner },
                MemberName: RuntimeContract.EntityGeneration,
                Location: location) { ResolvedType = generation.ReturnType },
            Arguments: [],
            Location: location)
        {
            ResolvedRoutine = generation,
            ResolvedType = generation.ReturnType,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: generation),
            IsSynthesizedLowering = true
        };
        return new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: MeGeneration,
                Type: null,
                Initializer: call,
                Visibility: VisibilityModifier.Secret,
                Location: location),
            Location: location);
    }

    /// <summary>The builder's <c>U64</c> type.</summary>
    private TypeSymbol U64 => registry.LookupType(name: "U64") ??
                              throw new InvalidOperationException(message: "System.U64 is not registered.");

    /// <summary>The name a member path starts at (<c>n</c> of <c>n.items[0].x</c>), or null.</summary>
    private static IdentifierExpression? Root(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression id => id,
            MemberExpression { TakesLink: false } member => Root(expr: member.Object),
            IndexExpression element => Root(expr: element.Object),
            _ => null
        };
    }
}
