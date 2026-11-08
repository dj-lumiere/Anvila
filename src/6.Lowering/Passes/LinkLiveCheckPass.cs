using Builder.Declaration;
using Builder.Verification;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// An entity ends where its original ends, and a token taken from a link may still be in use then (a call through it
/// ends the entity, the routine it was handed to goes on). Such a token is checked where it is used: before each
/// statement of the program's own routines that reaches a member through a token the builder cannot prove comes from an
/// original, the builder puts <c>token.require_live()</c> (Link.rvr), which fails with <c>EntityEndedError</c> once the
/// entity's slot is marked ended. A token is unproven when it was taken from a link (the arm of a <c>when</c> over one,
/// a link used directly), or when it is a token parameter (or <c>me</c>) of a routine some call hands an unproven token
/// to, found across the program's routines until nothing changes. Only tokens on retroactive entity types are checked:
/// no other entity ends while a link to it is used. A token taken from a link keeps the slot from being reused while it
/// is in use, so the check reads a header that is still the entity's.
/// <para>Runs once the program's bodies are analyzed and before the recovery variants are made, so a <c>try</c> above
/// a call that ends in this failure receives it.</para>
/// </summary>
internal sealed class LinkLiveCheckPass(TypeRegistry registry)
{
    /// <summary>The routines of the program, by registry key.</summary>
    private readonly Dictionary<string, RoutineDeclaration> _routines = new(comparer: StringComparer.Ordinal);

    /// <summary>The parameters (and <c>me</c>) of each routine that may hold a token taken from a link.</summary>
    private readonly Dictionary<string, HashSet<string>> _unproven = new(comparer: StringComparer.Ordinal);

    private bool _changed;

    /// <summary>Whether a check went into the routine being walked.</summary>
    private bool _inserted;

    /// <summary>Runs over the program's own routines.</summary>
    public void Run(IEnumerable<Program> programs)
    {
        if (!registry.HasRetroactiveTypes || !registry.Rules.ChecksAccessTokens)
        {
            return;
        }

        foreach (Program program in programs)
        {
            AstWalker.Walk(root: program,
                visit: node =>
                {
                    if (node is RoutineDeclaration { Body: BlockStatement, ResolvedInfo: { } info } routine &&
                        info is not ({ IsGenericDefinition: true } or { OwnerType.IsGenericDefinition: true }))
                    {
                        _routines.TryAdd(key: info.RegistryKey, value: routine);
                    }
                });
        }

        do
        {
            _changed = false;
            foreach ((string key, RoutineDeclaration routine) in _routines)
            {
                Walk(stmt: routine.Body, names: [.. Unproven(key: key)], insert: false);
            }
        } while (_changed);

        foreach ((string key, RoutineDeclaration routine) in _routines)
        {
            _inserted = false;
            Walk(stmt: routine.Body, names: [.. Unproven(key: key)], insert: true);
            // A routine that now checks a token can fail, so a `try` or `check` above a call of it receives the failure.
            if (_inserted && routine.ResolvedInfo is { } info)
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
        if (_unproven.TryGetValue(key: routine.RegistryKey, value: out HashSet<string>? names) && names.Count > 0)
        {
            Walk(stmt: body, names: [.. names], insert: true);
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
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    Statement inner = block.Statements[index: i];
                    Note(stmt: inner, names: names);
                    if (insert)
                    {
                        List<Statement> checks = Checks(stmt: inner, names: names);
                        if (checks.Count > 0)
                        {
                            _inserted = true;
                            block.Statements.InsertRange(index: i, collection: checks);
                            i += checks.Count;
                        }
                    }

                    Walk(stmt: inner, names: names, insert: insert);
                }

                return;
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
                        return;
                    }

                    if (e is not CallExpression { ResolvedRoutine: { } called } call)
                    {
                        return;
                    }

                    // A call through a token runs the token's forwarder of the entity's routine.
                    RoutineInfo routine = called;
                    while (routine.WrapperForwarderInnerMemberRoutine is { } inner)
                    {
                        routine = inner;
                    }

                    if (!_routines.ContainsKey(key: routine.RegistryKey))
                    {
                        return;
                    }

                    if (call.Callee is MemberExpression { Object: var receiver } && routine.OwnerType is EntityTypeSymbol
                        {
                            IsRetroactive: true
                        } && IsUnproven(expr: receiver, names: names))
                    {
                        Mark(routine: routine, parameter: "me");
                    }

                    List<ParamInfo> parameters = routine.Parameters.Where(predicate: p => p.Name != "me").ToList();
                    for (int i = 0; i < call.Arguments.Count; i++)
                    {
                        Expression argument = call.Arguments[index: i];
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
        if (Unproven(key: routine.RegistryKey).Add(item: parameter))
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
            _ => false
        };
    }

    /// <summary>The retroactive entity a token (or an entity itself, as <c>me</c>) reaches, or null.</summary>
    private static EntityTypeSymbol? RetroactiveEntity(TypeSymbol? type)
    {
        if (type?.BareName is RuntimeContract.Viewing or RuntimeContract.Modifying && type.TypeArguments is [{ } inner])
        {
            type = inner;
        }

        return type as EntityTypeSymbol is { IsRetroactive: true } entity ? entity : null;
    }

    /// <summary>The checks to put before a statement: one per unproven name it reaches a member through.</summary>
    private List<Statement> Checks(Statement stmt, HashSet<string> names)
    {
        var checks = new List<Statement>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (Expression root in OwnExpressions(stmt: stmt))
        {
            AstWalker.WalkExpressions(root: root,
                visit: e =>
                {
                    if (e is not MemberExpression { TakesLink: false } member ||
                        Root(expr: member.Object) is not { } name || !names.Contains(item: name.Name) ||
                        RetroactiveEntity(type: name.ResolvedType) is not { } entity || !seen.Add(item: name.Name) ||
                        registry.LookupMemberRoutineOverload(type: entity, memberRoutineName: RuntimeContract.RequireLive,
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

        return checks;
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
