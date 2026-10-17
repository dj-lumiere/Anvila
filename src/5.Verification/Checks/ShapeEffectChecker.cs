using Builder.Declaration;
using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Build-time shape checks (RazorForge). A container's SHAPE is its element count and positions; a
/// <c>@reshaping</c> routine can change it. While an <c>each</c> loop goes through a container, or a statement
/// works on one of its entity elements in place, the shape must not change: the loop could no longer trust
/// its next element, and the element access could be left pointing at a moved or freed element.
/// <para>The direct case (<c>xs.add_last(...)</c> inside <c>each x in xs</c>) is RF-S625 at the call. This
/// check also sees the INDIRECT cases, because every routine gets a RESHAPE EFFECT: the set of its
/// parameters (and <c>me</c>) whose shape it may change. A <c>@reshaping</c> routine changes <c>me</c>; a
/// routine that hands one of its parameters to a slot that is changed changes that parameter too,
/// transitively through user routines. Stdlib routines count through their <c>@reshaping</c> marker.</para>
/// <para>Two paths the effect cannot follow are rejected outright: handing the container to a routine VALUE
/// (its target is not known at build time), and changing another shared handle (<c>Retained</c>/
/// <c>Tracked</c>) of the same kind of container, which may be the same container.</para>
/// <para>Runs once after all user bodies are analyzed, so every call is resolved. Suflae keeps the runtime
/// check instead (its containers are shared <c>Roamed</c> handles the build cannot track).</para>
/// <para>One checker serves a whole build: the user routines it has seen accumulate across
/// <see cref="Check"/> calls, so a routine analyzed in an earlier file still lends its effect.</para>
/// </summary>
internal sealed class ShapeEffectChecker(DiagnosticReporter report)
{
    private const string MeSlot = "me";

    /// <summary>Token mints and marker coercions that hand out the receiver itself.</summary>
    private static readonly HashSet<string> ReceiverPassThroughVerbs =
        new(comparer: StringComparer.Ordinal)
        {
            "view",
            "modify",
            "consult",
            "amend",
            RuntimeContract.Access,
            RuntimeContract.Control
        };

    private readonly Dictionary<string, RoutineDeclaration> _userRoutineDeclarations =
        new(comparer: StringComparer.Ordinal);

    private readonly Dictionary<RoutineDeclaration, HashSet<string>> _reshapeEffects =
        new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>A container whose shape must stay still: its access path, its type, and why.</summary>
    private sealed record ShapeGuard(string Path, TypeSymbol? Type, bool IsLoop)
    {
        /// <summary>The variable holding a token on an element of the container, from its line to the end of its
        /// block (<c>var p = xs.modify_at(index: 0)</c>), or null for a loop or an in-place statement.</summary>
        public string? Holder { get; init; }

        /// <summary>The line the holder was bound on.</summary>
        public int HolderLine { get; init; }

        /// <summary>The path as a message shows it.</summary>
        public string Shown => ShownPath(path: Path);
    }

    /// <summary>How a shape path reads in a message. The index of an element is not tracked, so an element
    /// path (<c>grid[]</c>) reads <c>grid[...]</c>, and the source excerpt under the message points at it.</summary>
    private static string ShownPath(string path)
    {
        return path.Replace(oldValue: "[]", newValue: "[...]", comparisonType: StringComparison.Ordinal);
    }

    /// <summary>Runs the shape checks over the user programs.</summary>
    internal void Check(IEnumerable<Program> programs)
    {
        List<Program> userPrograms = programs.ToList();
        foreach (RoutineDeclaration routine in userPrograms.SelectMany(selector: EnumerateRoutines))
        {
            if (routine.ResolvedInfo is { } info)
            {
                _userRoutineDeclarations.TryAdd(key: info.RegistryKey, value: routine);
            }
        }

        ComputeReshapeEffects();

        foreach (RoutineDeclaration routine in userPrograms.SelectMany(selector: EnumerateRoutines))
        {
            CheckStatementShapes(stmt: routine.Body, guards: [], preceding: []);
            AstWalker.Walk(root: routine.Body, visit: node =>
            {
                if (node is CallExpression call)
                {
                    CheckSameContainerTwice(call: call);
                }
            });
        }
    }

    /// <summary>
    /// One call handed the same container as a write token and as another token (<c>merge(into: s, from: s)</c>,
    /// or <c>s.add_all(other: s)</c> whose receiver is written): the two are the same container, so a change through
    /// the write token moves what the other reads, RF-S623.
    /// </summary>
    private void CheckSameContainerTwice(CallExpression call)
    {
        if (call.ResolvedRoutine is not { } routine)
        {
            return;
        }

        var tokens = new List<(string Path, string Slot, bool Writes)>();
        if (call.Callee is MemberExpression { Object: var receiver } && routine.OwnerType != null &&
            ShapePath(expr: receiver) is { } receiverPath && !routine.IsReadOnly)
        {
            tokens.Add(item: (receiverPath, "the receiver", true));
        }

        for (int i = 0; i < call.Arguments.Count; i++)
        {
            Expression arg = call.Arguments[index: i];
            ParamInfo? param = arg is NamedArgumentExpression named
                ? routine.Parameters.FirstOrDefault(predicate: p => p.Name == named.Name)
                : i < routine.Parameters.Count ? routine.Parameters[index: i] : null;
            if (param?.Type.BareName is not ("Modifying" or "Viewing") || ShapePath(expr: arg) is not { } path)
            {
                continue;
            }

            tokens.Add(item: (path, $"'{param.Name}'", param.Type.BareName == "Modifying"));
        }

        for (int a = 0; a < tokens.Count; a++)
        {
            for (int b = a + 1; b < tokens.Count; b++)
            {
                if ((tokens[index: a].Writes || tokens[index: b].Writes) &&
                    (AccessPaths.IsPrefixOrEqual(prefix: tokens[index: a].Path, path: tokens[index: b].Path) ||
                     AccessPaths.IsPrefixOrEqual(prefix: tokens[index: b].Path, path: tokens[index: a].Path)))
                {
                    report(code: SemanticDiagnosticCode.SameContainerTwiceInCall,
                        message:
                        $"You are handing '{ShownPath(path: tokens[index: a].Path)}' to {tokens[index: a].Slot} and " +
                        $"'{ShownPath(path: tokens[index: b].Path)}' to {tokens[index: b].Slot} of '{routine.Name}' in the same " +
                        "call, and one of them is written: they are the same container, so a change through one moves what " +
                        "the other reads. Copy the one that is only read first, or make the call with two different containers.",
                        location: call.Location);
                    return;
                }
            }
        }
    }

    private static IEnumerable<RoutineDeclaration> EnumerateRoutines(Program program)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            switch (node)
            {
                case RoutineDeclaration r:
                    yield return r;
                    break;
                case EntityDeclaration e:
                    foreach (RoutineDeclaration m in e.Members.OfType<RoutineDeclaration>())
                    {
                        yield return m;
                    }

                    break;
                case RecordDeclaration rec:
                    foreach (RoutineDeclaration m in rec.Members.OfType<RoutineDeclaration>())
                    {
                        yield return m;
                    }

                    break;
                case CrashableDeclaration cr:
                    foreach (RoutineDeclaration m in cr.Members.OfType<RoutineDeclaration>())
                    {
                        yield return m;
                    }

                    break;
            }
        }
    }

    // ---- Reshape effects ------------------------------------------------------------------------------

    /// <summary>The slots (parameter names, and <c>me</c>) whose shape a call to <paramref name="callee"/>
    /// may change.</summary>
    private HashSet<string> ReshapedSlots(RoutineInfo callee)
    {
        var slots = new HashSet<string>(comparer: StringComparer.Ordinal);
        if (callee.IsReshaping)
        {
            slots.Add(item: MeSlot);
        }

        RoutineDeclaration? decl = FindUserDeclaration(routine: callee);
        if (decl != null)
        {
            slots.UnionWith(other: InferReshapedSlots(routine: decl));
        }

        return slots;
    }

    private RoutineDeclaration? FindUserDeclaration(RoutineInfo routine)
    {
        if (_userRoutineDeclarations.TryGetValue(key: routine.RegistryKey, value: out RoutineDeclaration? d))
        {
            return d;
        }

        return routine.GenericDefinition is { } def &&
               _userRoutineDeclarations.TryGetValue(key: def.RegistryKey, value: out RoutineDeclaration? g)
            ? g
            : null;
    }

    /// <summary>
    /// Gives every user routine its reshape effect: the parameters (and <c>me</c>) whose shape its body may
    /// change, i.e. every one it hands to a changed slot of a call or to a routine value. Solved as a
    /// fixpoint over all routines at once. Effects only grow, so the iteration ends, and a routine in a
    /// recursive cycle gets the whole cycle's effect. (Memoizing the first, partial answer seen inside a
    /// cycle left a routine reached only through the cycle with too small an effect.)
    /// </summary>
    private void ComputeReshapeEffects()
    {
        foreach (RoutineDeclaration decl in _userRoutineDeclarations.Values)
        {
            _reshapeEffects.TryAdd(key: decl, value: new HashSet<string>(comparer: StringComparer.Ordinal));
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach ((RoutineDeclaration decl, HashSet<string> slots) in _reshapeEffects.ToList())
            {
                foreach (string slot in DirectlyReshapedSlots(routine: decl))
                {
                    changed |= slots.Add(item: slot);
                }
            }
        }
    }

    /// <summary>The routine's reshape effect (see <see cref="ComputeReshapeEffects"/>).</summary>
    private HashSet<string> InferReshapedSlots(RoutineDeclaration routine)
    {
        return _reshapeEffects.TryGetValue(key: routine, value: out HashSet<string>? known)
            ? known
            : [];
    }

    /// <summary>The slots <paramref name="routine"/>'s body changes given the callee effects known so far.</summary>
    private List<string> DirectlyReshapedSlots(RoutineDeclaration routine)
    {
        var own = new HashSet<string>(
            collection: routine.Parameters.Select(selector: p => p.Name).Append(element: MeSlot),
            comparer: StringComparer.Ordinal);
        var found = new List<string>();
        AstWalker.WalkExpressions(root: routine.Body,
            visit: e =>
            {
                // `d[k] = v` through a parameter whose setitem may add the key changes that parameter's shape.
                if (ReshapingIndexWrite(expr: e) is { } written && ShapePath(expr: written) is { } writtenPath &&
                    own.Contains(item: RootName(path: writtenPath)))
                {
                    found.Add(item: Bounded(path: writtenPath));
                    return;
                }

                if (e is not CallExpression call)
                {
                    return;
                }

                foreach ((Expression arg, string _, string rest) in ChangedOperands(call: call))
                {
                    if (ShapePath(expr: arg) is { } path && own.Contains(item: RootName(path: path)))
                    {
                        found.Add(item: Bounded(path: path + rest));
                    }
                }
            });
        return found;
    }

    /// <summary>
    /// The operands of <paramref name="call"/> whose shape the call may change, each with the reason's
    /// callee name and the part of it that changes (<c>.log</c> when the callee changes <c>me.log</c>, empty for the
    /// whole operand): the receiver when the callee changes <c>me</c> or a part of it, each argument bound to a
    /// changed parameter, and every argument of a routine-value call (its target is unknown).
    /// </summary>
    private IEnumerable<(Expression Operand, string Callee, string Inside)> ChangedOperands(CallExpression call)
    {
        if (IsRoutineValueCall(call: call))
        {
            foreach (Expression arg in call.Arguments)
            {
                yield return (Unwrap(expr: arg), "", "");
            }

            yield break;
        }

        if (call.ResolvedRoutine is not { } callee)
        {
            yield break;
        }

        HashSet<string> slots = ReshapedSlots(callee: callee);
        if (slots.Count == 0)
        {
            yield break;
        }

        foreach (string effect in slots)
        {
            string slot = RootName(path: effect);
            string rest = effect[slot.Length..];
            if (slot == MeSlot)
            {
                if (call.Callee is MemberExpression { Object: var receiver } &&
                    callee.Kind is not (RoutineKind.CommonRoutine or RoutineKind.Creator))
                {
                    yield return (receiver, callee.Name, rest);
                }

                continue;
            }

            for (int i = 0; i < call.Arguments.Count; i++)
            {
                Expression arg = call.Arguments[index: i];
                string? paramName = arg is NamedArgumentExpression named
                    ? named.Name
                    : i < callee.Parameters.Count
                        ? callee.Parameters[index: i].Name
                        : null;
                if (paramName == slot)
                {
                    yield return (Unwrap(expr: arg), callee.Name, rest);
                }
            }
        }
    }

    /// <summary>A changed path cut to its first four parts: a routine that recurses down a chain (`me.next.next...`)
    /// would otherwise grow its effect without end. The cut path names a container around the real one, so it
    /// guards more, never less.</summary>
    private static string Bounded(string path)
    {
        int parts = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[index: i] is '.' or '[' && ++parts == 4)
            {
                return path[..i];
            }
        }

        return path;
    }

    private static bool IsRoutineValueCall(CallExpression call)
    {
        return call.Callee.ResolvedType is RoutineTypeSymbol;
    }

    private static Expression Unwrap(Expression expr)
    {
        return expr is NamedArgumentExpression named
            ? named.Value
            : expr;
    }

    /// <summary>
    /// The container path an operand stands for: a name or field chain, seen through a token mint or a
    /// marker coercion (<c>xs.modify()</c> is <c>xs</c>). Null for anything else.
    /// </summary>
    private static string? ShapePath(Expression expr)
    {
        return Unwrap(expr: expr) switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression { Object: var inner, MemberName: var field } =>
                ShapePath(expr: inner) is { } prefix
                    ? $"{prefix}.{field}"
                    : null,
            // Any element of a container (`grid[0]`, `grid[i]`) is `grid[]`: the index is not tracked.
            IndexExpression { Object: var container } =>
                ShapePath(expr: container) is { } owner
                    ? $"{owner}[]"
                    : null,
            CallExpression
            {
                Callee: MemberExpression { Object: var receiver, MemberName: var verb },
                Arguments.Count: 0
            } when ReceiverPassThroughVerbs.Contains(item: verb) => ShapePath(expr: receiver),
            _ => null
        };
    }

    private static string RootName(string path)
    {
        int end = path.IndexOfAny(anyOf: ['.', '[']);
        return end < 0
            ? path
            : path[..end];
    }

    // ---- Guarded regions ------------------------------------------------------------------------------

    /// <summary>
    /// Walks a statement, checking the calls in every region where a container's shape must stay still:
    /// the body of an <c>each</c> loop over a container it can name as a path, and a statement that works on an entity
    /// element of a named container in place.
    /// </summary>
    private void CheckStatementShapes(Statement stmt, List<ShapeGuard> guards,
        List<Statement> preceding)
    {
        switch (stmt)
        {
            case BlockStatement block:
            {
                var seen = new List<Statement>();
                List<ShapeGuard> current = guards;
                foreach (Statement s in block.Statements)
                {
                    CheckStatementShapes(stmt: s, guards: current, preceding: seen);
                    seen.Add(item: s);
                    // A token on an element held in a variable keeps the container's shape still to the block's end.
                    if (HeldElementGuard(stmt: s) is { } held)
                    {
                        current = [.. current, held];
                    }
                }

                return;
            }
            case DangerStatement danger:
                CheckStatementShapes(stmt: danger.Body, guards: guards, preceding: []);
                return;
            case LoopStatement loop:
            {
                List<ShapeGuard> inner = guards;
                string? sourcePath = loop.IterationSourcePath ?? loop.IterationSourceName;
                if (sourcePath != null)
                {
                    TypeSymbol? sourceType = LoopSourceType(path: sourcePath, preceding: preceding);
                    inner = [.. guards, new ShapeGuard(Path: sourcePath, Type: sourceType, IsLoop: true)];
                }

                CheckStatementShapes(stmt: loop.Body, guards: inner, preceding: []);
                return;
            }
            case IfStatement ifs:
                CheckOwnExpressions(exprs: [ifs.Condition], guards: guards);
                CheckStatementShapes(stmt: ifs.ThenStatement, guards: guards, preceding: []);
                if (ifs.ElseStatement is { } alt)
                {
                    CheckStatementShapes(stmt: alt, guards: guards, preceding: []);
                }

                return;
            case WhenStatement whenStmt:
                CheckOwnExpressions(exprs: [whenStmt.Expression], guards: guards);
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    CheckStatementShapes(stmt: clause.Body, guards: guards, preceding: []);
                }

                return;
            case UsingStatement usingStmt:
                CheckOwnExpressions(exprs: [usingStmt.Resource], guards: guards);
                // `using multimodify(first: a, second: b) as (x, y)` checked that the two differ.
                bool distinct = DistinctPair(usingStmt: usingStmt) is { } pair && _distinct.Add(item: pair);
                CheckStatementShapes(stmt: usingStmt.Body, guards: guards, preceding: []);
                if (distinct)
                {
                    _distinct.Remove(item: DistinctPair(usingStmt: usingStmt)!.Value);
                }

                if (usingStmt.FallbackBody is { } fallback)
                {
                    CheckStatementShapes(stmt: fallback, guards: guards, preceding: []);
                }

                return;
            default:
                CheckOwnExpressions(exprs: OwnExpressions(stmt: stmt), guards: guards);
                return;
        }
    }

    private static List<Expression> OwnExpressions(Statement stmt)
    {
        return stmt switch
        {
            ExpressionStatement s => [s.Expression],
            DropStatement s => [s.Expression],
            ReturnStatement { Value: { } v } => [v],
            ThrowStatement s => [s.Error],
            AssignmentStatement s => [s.Target, s.Value],
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } } => [init],
            _ => []
        };
    }

    /// <summary>
    /// Checks one statement's own expressions: against the enclosing loop guards, and against the element
    /// guard the statement itself opens when it works on an entity element of a named container.
    /// </summary>
    private void CheckOwnExpressions(List<Expression> exprs, List<ShapeGuard> guards)
    {
        var active = new List<ShapeGuard>(collection: guards);
        foreach (Expression e in exprs)
        {
            AstWalker.WalkExpressions(root: e,
                visit: node =>
                {
                    Expression? owner = node switch
                    {
                        CallExpression { Callee: MemberExpression { Object: var o } } => o,
                        BinaryExpression { Operator: BinaryOperator.Assign, Left: MemberExpression m } => m.Object,
                        BinaryExpression { Operator: BinaryOperator.Assign, Left: IndexExpression ix } => ix.Object,
                        _ => null
                    };
                    if (owner != null && EntityElementContainer(expr: owner) is { } element &&
                        !active.Any(predicate: g => !g.IsLoop && g.Path == element.Path))
                    {
                        active.Add(item: element);
                    }
                });
        }

        if (active.Count == 0)
        {
            return;
        }

        foreach (Expression e in exprs)
        {
            AstWalker.WalkExpressions(root: e,
                visit: node =>
                {
                    switch (node)
                    {
                        case CallExpression call:
                            CheckCallAgainstGuards(call: call, guards: active);
                            break;
                        case IndexExpression when ReshapingIndexWrite(expr: node) is { } written &&
                                                  ShapePath(expr: written) is { } writtenPath:
                            foreach (ShapeGuard g in active)
                            {
                                // `d[k] = v` on the loop's own variable is RF-S625 where it is written.
                                bool direct = g.IsLoop && written is IdentifierExpression { Name: var name } &&
                                              name == g.Path;
                                if (!direct && AccessPaths.IsPrefixOrEqual(prefix: writtenPath, path: g.Path))
                                {
                                    ReportShapeChange(guard: g,
                                        attempt: $"put a value into '{ShownPath(path: writtenPath)}[...]'",
                                        why: $"it may add an element to '{ShownPath(path: writtenPath)}'",
                                        location: node.Location);
                                }
                            }

                            break;
                        case StealExpression steal when ShapePath(expr: steal.Operand) is { } stolen:
                            foreach (ShapeGuard g in active.Where(predicate: g => !g.IsLoop))
                            {
                                if (AccessPaths.IsPrefixOrEqual(prefix: stolen, path: g.Path))
                                {
                                    ReportShapeChange(guard: g,
                                        attempt: $"steal '{stolen}'",
                                        why: $"'{stolen}' would move away with the element",
                                        location: steal.Location);
                                }
                            }

                            break;
                    }
                });
        }
    }

    /// <summary>The container of a subscript write whose <c>setitem</c> is <c>@reshaping</c> (<c>d[k] = v</c> on a
    /// <c>Dict</c>, which may add the key), or null.</summary>
    private static Expression? ReshapingIndexWrite(Expression expr)
    {
        return expr is IndexExpression { ResolvedSetItem.IsReshaping: true } written
            ? written.Object
            : null;
    }

    /// <summary>The element guard for a path that reaches an entity element of a named container.</summary>
    private static ShapeGuard? EntityElementContainer(Expression expr)
    {
        Expression cur = expr;
        while (cur is MemberExpression member)
        {
            cur = member.Object;
        }

        return cur is IndexExpression { ResolvedType: EntityTypeSymbol } element &&
               ShapePath(expr: element.Object) is { } path
            ? new ShapeGuard(Path: path, Type: element.Object.ResolvedType, IsLoop: false)
            : null;
    }

    private void CheckCallAgainstGuards(CallExpression call, List<ShapeGuard> guards)
    {
        foreach ((Expression operand, string callee, string rest) in ChangedOperands(call: call))
        {
            string? path = ShapePath(expr: operand) is { } whole ? whole + rest : null;
            string shown = path != null
                ? ShownPath(path: path)
                : "";
            foreach (ShapeGuard g in guards)
            {
                // The direct `@reshaping` call on the loop variable itself is RF-S625 at the call site.
                if (g.IsLoop && operand is IdentifierExpression { Name: var direct } && direct == g.Path &&
                    call.ResolvedRoutine?.IsReshaping == true && call.Callee is MemberExpression)
                {
                    continue;
                }

                if (path != null && AccessPaths.IsPrefixOrEqual(prefix: path, path: g.Path))
                {
                    string attempt = callee.Length == 0
                        ? $"pass '{shown}' to a routine value"
                        : operand is IdentifierExpression or MemberExpression or IndexExpression &&
                          call.Callee is MemberExpression { Object: var r } && ReferenceEquals(objA: r, objB: operand)
                            ? $"call '{shown}.{callee}()'"
                            : $"pass '{shown}' to '{callee}'";
                    string why = callee.Length == 0
                        ? "the builder cannot tell whether that routine adds to or removes from it"
                        : $"'{callee}' can add to or remove from it";
                    ReportShapeChange(guard: g, attempt: attempt, why: why, location: call.Location);
                }
                else if (path != null && MayBeSameContainer(operand: operand, path: path, guard: g))
                {
                    string change = callee.Length == 0 ? $"pass '{shown}' to a routine value" : $"change '{shown}' through '{callee}'";
                    string same = $"'{shown}' and '{g.Shown}' are both tokens or shared handles on a {AliasInner(type: g.Type)!.Name}, " +
                                  "so they may be the same one";
                    string multi = $"take both with `using multimodify(first: {RootName(path: path)}, second: {RootName(path: g.Path)}) as (a, b)`, " +
                                   "which checks that they differ";
                    if (g.Holder != null)
                    {
                        report(code: SemanticDiagnosticCode.HeldElementOfAliasReshaped,
                            message:
                            $"You are trying to {change} while '{g.Holder}' (line {g.HolderLine}) holds an element of '{g.Shown}': " +
                            $"{same}, and the change could move or free the element '{g.Holder}' holds. Finish with " +
                            $"'{g.Holder}' before the change, or {multi}.",
                            location: call.Location);
                    }
                    else if (g.IsLoop)
                    {
                        report(code: SemanticDiagnosticCode.ReshapingDuringIteration,
                            message:
                            $"You are trying to {change} while an `each` loop is going through '{g.Shown}': {same}, and after " +
                            $"such a change the loop could no longer trust that its next element is really the next one. " +
                            $"Make the change after the loop, or {multi}.",
                            location: call.Location);
                    }
                    else
                    {
                        ReportShapeChange(guard: g, attempt: change, why: same, location: call.Location);
                    }
                }
            }
        }
    }

    /// <summary>The two-token pairs taken by <c>multimodify</c>/<c>multiamend</c> whose bodies are being checked.</summary>
    private readonly HashSet<(string, string)> _distinct = [];

    /// <summary>The names a <c>using multimodify(...)</c> / <c>using multiamend(...)</c> binds, in name order.</summary>
    private static (string, string)? DistinctPair(UsingStatement usingStmt)
    {
        return usingStmt is { Parts: [var a, var b], Resource: CallExpression { Callee: IdentifierExpression { Name: "multimodify" or "multiamend" } } }
            ? string.CompareOrdinal(strA: a, strB: b) < 0 ? (a, b) : (b, a)
            : null;
    }

    /// <summary>The guard a statement opens when it binds a variable to a token on an element of a named container:
    /// <c>var p = xs.modify_at(index: 0)</c>, <c>var p = xs.view_at(index: 0)</c>, or <c>var b = boxes[0]</c> on an
    /// entity element.</summary>
    private static ShapeGuard? HeldElementGuard(Statement stmt)
    {
        if (stmt is not DeclarationStatement { Declaration: VariableDeclaration { Name: var name, Initializer: { } init } })
        {
            return null;
        }

        Expression? container = Unwrap(expr: init) switch
        {
            CallExpression { Callee: MemberExpression { Object: var o, MemberName: "modify_at" or "view_at" } } => o,
            IndexExpression { ResolvedType: EntityTypeSymbol, Object: var o } => o,
            _ => null
        };
        return container != null && ShapePath(expr: container) is { } path
            ? new ShapeGuard(Path: path, Type: container.ResolvedType, IsLoop: false)
            {
                Holder = name, HolderLine = stmt.Location.Line
            }
            : null;
    }

    /// <summary>Whether the container <paramref name="operand"/> changes may be the one <paramref name="guard"/> holds
    /// an element of: both are tokens or shared handles on the same kind of container (a bare entity has one owner,
    /// so two names of bare entities are two entities), and no <c>multimodify</c> showed they differ.</summary>
    private bool MayBeSameContainer(Expression operand, string path, ShapeGuard guard)
    {
        TypeSymbol? a = AliasInner(type: operand.ResolvedType);
        TypeSymbol? b = AliasInner(type: guard.Type);
        if (a == null || b == null || a.FullName != b.FullName)
        {
            return false;
        }

        string x = RootName(path: path), y = RootName(path: guard.Path);
        return x != y && !_distinct.Contains(item: string.CompareOrdinal(strA: x, strB: y) < 0 ? (x, y) : (y, x));
    }

    /// <summary>The container a token or a shared handle reaches, or null for a bare value.</summary>
    private static TypeSymbol? AliasInner(TypeSymbol? type)
    {
        return type?.BareName is "Modifying" or "Viewing" or "Consulting" or "Amending" && type.TypeArguments is [{ } inner, ..]
            ? inner
            : type is null ? null : SharedHandleInner(type: type);
    }

    /// <summary>Two shared (reference-counted) handles to the same kind of container.</summary>
    private static bool IsSharedHandleOfSameKind(TypeSymbol? a, TypeSymbol? b)
    {
        return a != null && b != null && SharedHandleInner(type: a) is { } ia &&
               SharedHandleInner(type: b) is { } ib && ia.FullName == ib.FullName;
    }

    private static TypeSymbol? SharedHandleInner(TypeSymbol type)
    {
        return TypeRegistry.GetRcWrapperBaseName(type: type) is { } rc &&
               rc != RuntimeContract.Roamed && type.TypeArguments is [{ } inner]
            ? inner
            : null;
    }

    private void ReportShapeChange(ShapeGuard guard, string attempt, string why,
        SourceLocation location)
    {
        if (guard.Holder != null)
        {
            report(code: SemanticDiagnosticCode.HeldElementOfAliasReshaped,
                message:
                $"You are trying to {attempt} while '{guard.Holder}' (line {guard.HolderLine}) holds an element of " +
                $"'{guard.Shown}': {why}. That could move or free the element '{guard.Holder}' holds. Finish with " +
                $"'{guard.Holder}' before the change.",
                location: location);
            return;
        }

        if (guard.IsLoop)
        {
            report(code: SemanticDiagnosticCode.ReshapingDuringIteration,
                message:
                $"You are trying to {attempt} while an `each` loop is going through '{guard.Shown}': {why}. " +
                "After such a change the loop could no longer trust that its next element is really the " +
                "next one. Make the change after the loop.",
                location: location);
            return;
        }

        report(code: SemanticDiagnosticCode.TokenSourceReplaced,
            message:
            $"You are trying to {attempt} while this statement works on an element of '{guard.Shown}' in " +
            $"place: {why}. That could move or free the element under it. Do it in a separate statement " +
            "before or after.",
            location: location);
    }

    /// <summary>The type of an <c>each</c> loop's source, read from its iterator declaration among the
    /// statements before the loop (<c>var _iter = xs.iter()</c>): the analyzed expression with that path.</summary>
    private static TypeSymbol? LoopSourceType(string path, List<Statement> preceding)
    {
        for (int i = preceding.Count - 1; i >= 0; i--)
        {
            if (preceding[index: i] is not DeclarationStatement
                {
                    Declaration: VariableDeclaration { Initializer: { } init }
                })
            {
                continue;
            }

            TypeSymbol? found = null;
            AstWalker.WalkExpressions(root: init,
                visit: e =>
                {
                    if (e.ResolvedType is { } t && ShapePath(expr: e) == path)
                    {
                        found ??= t;
                    }
                });
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }
}
