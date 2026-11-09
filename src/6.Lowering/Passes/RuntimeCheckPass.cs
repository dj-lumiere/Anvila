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
/// <item>A token taken from a link. An entity ends where its original ends, and such a token may still be in use then
/// (a call through it ends the entity, the routine it was handed to goes on). Before each statement that reaches a
/// member through a token the builder cannot prove comes from an original, the builder puts
/// <c>token.require_live()</c> (Link.rvr), which fails with <c>EntityEndedError</c> once the entity's slot is marked
/// ended. A token is unproven when it was taken from a link (the arm of a <c>when</c> over one, a link used directly)
/// or given by a call of a routine value (a lambda may give one taken from what it captured), when a local was given one, or when it is a token parameter (or <c>me</c>) of a routine some call hands an
/// unproven token to, found across the program's routines until nothing changes. The routines a <c>try</c> or
/// <c>check</c> over an expression is made into count as the program's routines, so a use under the keyword is checked
/// in there and the keyword receives the failure. A token a routine-value call gives, used in the same expression
/// (<c>f().x</c>), has no statement of its own to be checked before: it is wrapped in <c>live_modifying[T](token:)</c>
/// (or the <c>Viewing</c> one), which checks it and gives it back. Only tokens on retroactive entity
/// types are checked: no other entity ends while a link to it is used. A token taken from a link keeps the slot from
/// being reused while it is in use, so the check reads a header that is still the entity's. In a generic routine a token
/// over a type parameter is checked by <c>require_modifying_live[T]</c> / <c>require_viewing_live[T]</c>, which do
/// nothing in an instance whose type is not a retroactive entity.</item>
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

    /// <summary>Whether tokens taken from links are checked: the program keeps a link to some entity type.</summary>
    private readonly bool _links = registry.HasRetroactiveTypes && registry.Rules.ChecksAccessTokens;

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
            Walk(stmt: body, names: [.. Unproven(key: key)], insert: true);
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
            Walk(stmt: body, names: names, insert: true);
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
                    if (insert && _links && WrapLiveUses(stmt: block.Statements[index: i]) is { } wrapped)
                    {
                        block.Statements[index: i] = wrapped;
                    }

                    Statement inner = block.Statements[index: i];
                    Note(stmt: inner, names: scope);
                    if (insert)
                    {
                        List<Statement> checks = Checks(stmt: inner, names: scope);
                        if (checks.Count > 0)
                        {
                            _inserted = true;
                            block.Statements.InsertRange(index: i, collection: checks);
                            i += checks.Count;
                        }
                    }

                    Walk(stmt: inner, names: scope, insert: insert);
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
                when RetroactiveEntity(type: init.ResolvedType) != null && IsUnproven(expr: init, names: names) => name,
            AssignmentStatement { Target: IdentifierExpression { Name: var name }, Value: var value }
                when RetroactiveEntity(type: value.ResolvedType) != null && IsUnproven(expr: value, names: names) => name,
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

                    if (receiver != null && RetroactiveEntity(type: routine.OwnerType) != null &&
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
                        if (parameter != null && RetroactiveEntity(type: parameter.Type) != null &&
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
    private Statement? WrapLiveUses(Statement stmt)
    {
        var wrapper = new LiveUseWrapper(pass: this);
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

    /// <summary><c>live_modifying[T](token: value)</c> (or the <c>Viewing</c> one) over a token a routine-value call
    /// gives, when the token reaches a retroactive entity or a type parameter, else null.</summary>
    private CallExpression? LiveCheck(Expression value)
    {
        if (value.ResolvedType is not { BareName: var wrapper, TypeArguments: [{ } over] } tokenType ||
            wrapper is not (RuntimeContract.Modifying or RuntimeContract.Viewing) || RetroactiveEntity(type: tokenType) == null)
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
    private sealed class LiveUseWrapper(RuntimeCheckPass pass) : AstRewriter
    {
        protected override Expression VisitRecovery(RecoveryExpression e)
        {
            return e;
        }

        protected override Expression VisitMember(MemberExpression e)
        {
            Expression visited = base.VisitMember(e: e);
            return visited is MemberExpression { TakesLink: false, Object: CallExpression call } member &&
                   GivesUnprovenToken(call: call) && pass.LiveCheck(value: call) is { } live
                ? member with { Object = live }
                : visited;
        }

        protected override Expression VisitIndex(IndexExpression e)
        {
            Expression visited = base.VisitIndex(e: e);
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

    /// <summary>The type a token (or an entity itself, as <c>me</c>) reaches when its entities may end while a link
    /// to them is used: a retroactive entity, or a type parameter an instance may bind to one. Null for any other.</summary>
    private static TypeSymbol? RetroactiveEntity(TypeSymbol? type)
    {
        if (type?.BareName is RuntimeContract.Viewing or RuntimeContract.Modifying && type.TypeArguments is [{ } inner])
        {
            type = inner;
        }

        return type switch
        {
            EntityTypeSymbol { IsRetroactive: true } entity => entity,
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
                        RetroactiveEntity(type: name.ResolvedType) is not { } entity || !seen.Add(item: name.Name))
                    {
                        return;
                    }

                    // A token over a type parameter (in a generic routine) is checked by a routine every instance has:
                    // it does nothing where the instance's type is not a retroactive entity.
                    if (entity is GenericParameterTypeSymbol)
                    {
                        if (GenericCheck(token: name, location: stmt.Location) is { } generic)
                        {
                            checks.Add(item: new ExpressionStatement(Expression: generic, Location: stmt.Location));
                        }

                        return;
                    }

                    if (registry.LookupMemberRoutineOverload(type: entity, memberRoutineName: RuntimeContract.RequireLive,
                            argTypes: []) is not { } require)
                    {
                        return;
                    }

                    var call = new CallExpression(
                        Callee: new MemberExpression(Object: name with { }, MemberName: RuntimeContract.RequireLive,
                            Location: stmt.Location) { ResolvedType = require.ReturnType },
                        Arguments: [],
                        Location: stmt.Location)
                    {
                        ResolvedRoutine = require,
                        ResolvedType = require.ReturnType,
                        LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: require),
                        IsSynthesizedLowering = true
                    };
                    checks.Add(item: new ExpressionStatement(Expression: call, Location: stmt.Location));
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

    /// <summary><c>require_modifying_live[T](token: n)</c> (or the <c>Viewing</c> one) for a token over the type
    /// parameter <c>T</c>, or null when the token is neither.</summary>
    private CallExpression? GenericCheck(IdentifierExpression token, SourceLocation location)
    {
        if (token.ResolvedType is not { BareName: var wrapper, TypeArguments: [GenericParameterTypeSymbol over] } tokenType ||
            wrapper is not (RuntimeContract.Modifying or RuntimeContract.Viewing))
        {
            return null;
        }

        string name = wrapper == RuntimeContract.Modifying
            ? RuntimeContract.RequireModifyingLive
            : RuntimeContract.RequireViewingLive;
        if (registry.LookupGenericOverload(name: name, module: "System") is not { } definition)
        {
            return null;
        }

        RoutineInfo instance = registry.GetOrCreateRoutineResolution(genericDef: definition, typeArguments: [over]);
        return new CallExpression(
            Callee: new IdentifierExpression(Name: name, Location: location) { ResolvedRoutine = instance },
            Arguments:
            [
                new NamedArgumentExpression(Name: "token", Value: token with { }, Location: location)
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
            TypeArguments = [new TypeExpression(Name: over.Name, GenericArguments: null, Location: location)]
        };
    }

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
