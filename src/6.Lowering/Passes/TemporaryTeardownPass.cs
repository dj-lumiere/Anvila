using Builder.Declaration;
using Builder.Instantiation;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Builder.Verification;

namespace Builder.Lowering.Passes;

/// <summary>
/// RAII teardown for owned <b>rvalue temporaries</b> — the heap-owning intermediate values that
/// <see cref="ScopeTeardownLoweringPass"/> cannot reach because it only tracks <i>named</i> bindings.
///
/// <para>Consider <c>x.represent().count()</c>: <c>x.represent()</c> mints a fresh owned
/// <c>Text</c> (an RC-record holding a heap buffer), <c>.count()</c> borrows it, and the <c>Text</c>
/// is then dropped on the floor — never destroyed. Inside a hot loop that leaks one buffer per
/// iteration. This pass spills such a temporary into a synthetic block-scoped local and emits its
/// <c>destruct()</c> at end-of-statement, so the buffer is freed each time the statement runs.</para>
///
/// <para><b>Why a separate, late pass.</b> <see cref="ScopeTeardownLoweringPass"/> runs before
/// reachability and before user-code Phase 8 lowering — at that point a <c>var u = when … =>
/// x.represent().count()</c> is still a <c>when</c>-<i>expression</i>, so the producing call is
/// buried in conditional arms with no statement to attach teardown to. This pass runs AFTER Phase 8
/// (when→if already lowered, so arms are real blocks) and emits its own <c>destruct</c> calls;
/// codegen's emit-on-demand picks up the referenced concrete <c>destruct</c>.</para>
///
/// <para><b>What is spilled (deliberately narrow — correctness over coverage, never a double-free).</b>
/// Exactly one shape: the <b>receiver of a memberRoutine call where (a) the receiver is a fresh RC-record
/// producer and (b) the call result is not a borrow/view wrapper</b> (so it cannot alias the
/// receiver). <c>retain</c>/<c>track</c> verbs (which consume the receiver) are excluded. This covers
/// both <c>x.represent().count()</c> (scalar result) and the intermediate <c>Text</c> of a
/// concatenation chain <c>a + "-" + b</c> (each <c>add</c> result is the receiver of the next). The object
/// of a field read (<c>xs[0].id</c>) is spilled by the same rule.</para>
///
/// <para><b>Why this is safe.</b> An RC-record <c>destruct</c> releases a <i>refcounted</i>
/// controller, so a balanced release is harmless. A memberRoutine's record/RC-record return is always
/// <i>independent</i> of the receiver — freshly allocated (string concat builds a new buffer) or a
/// retaining +1 copy (<see cref="ScopeTeardownLoweringPass"/>'s sibling RecordCopyLoweringPass injects
/// <c>store</c> on <c>me</c>/lvalue returns) — so freeing the receiver leaves the result valid. The
/// only alias hazard is a borrow/view result pointing into the receiver, which the guard excludes.</para>
///
/// <para><b>Entities.</b> A RazorForge entity a call or construction makes only to call one of its routines or read
/// one of its member variables (<c>pair(x: 3).count()</c>, <c>make().a().b()</c>) is its own (a routine never
/// gives back an entity it still keeps), so it is spilled too, into a local that lives to the end of its block as a
/// declared one would: what the call gives back may point into it (an emitter or adapter over it, a token), and its
/// teardown may wait for work it started. The local is marked for the late scope teardown, which also covers every
/// exit leaving the block. In a return or throw it ends with the statement, unless what is handed out may point into
/// it, where it is left alone. A call that gives back where the entity is (<c>hijack</c>, an address) hands it to
/// code that places it by hand, so it is not spilled.</para>
///
/// <para>Crucially NOT spilled (each a real double-free / leak-vs-crash hazard): call
/// <b>arguments</b> (an rvalue arg is moved into the callee), <b>discarded</b> statement values (a
/// fluent <c>me</c>-returning call aliases an existing binding), non-scalar results, <c>var</c>
/// initializers / assignment RHS / return values (owned by the binding or moved out), and
/// <c>steal</c> operands.</para>
///
/// <para><b>Why a separate, late pass.</b> <see cref="ScopeTeardownLoweringPass"/> runs before
/// reachability and before user-code Phase 8 lowering — at that point a <c>var u = when … =>
/// x.represent().count()</c> is still a <c>when</c>-expression with the producing call buried in
/// conditional arms. This pass runs AFTER Phase 8 (when→if lowered, arms are real blocks) and emits
/// its own <c>destruct</c> calls; codegen's emit-on-demand resolves the concrete <c>destruct</c>.</para>
///
/// <para><b>Known limitations</b> (leak preserved, never a crash): argument-position temporaries,
/// non-scalar-returning chains, an entity temporary a return hands out a value pointing into, and owned
/// temporaries inside loop/if conditions are not freed.</para>
/// </summary>
internal sealed class TemporaryTeardownPass(PostprocessingContext ctx)
{
    private readonly TypeSymbol? _blankType = ctx.Registry.LookupType(name: "None");
    private int _counter;

    /// <summary>Set while lowering monomorphized bodies: only reassignment release is added, with no
    /// temporary spills (the generic definition's spills already ran before monomorphization).</summary>

    /// <summary>The reference primitives whose result is a borrow of a referent owned elsewhere —
    /// a temporary produced by one of these owns nothing, so it must not be torn down. Mirrors
    /// <see cref="ScopeTeardownLoweringPass"/>'s view-verb exclusion.</summary>
    private static readonly IReadOnlySet<string> ViewVerbs = RuntimeContract.ViewVerbs;

    /// <summary>Borrow/view wrapper names whose value points INTO another value, so a memberRoutine
    /// returning one may alias its receiver — freeing the receiver would then dangle it. The owning
    /// RC wrappers (Retained/Tracked/Guarded/Witnessed) are NOT here: they carry a refcounted controller,
    /// so an aliasing owned result is balanced by refcount.</summary>
    private static readonly IReadOnlySet<string> BorrowWrapperNames =
        RuntimeContract.ReferringWrapperNAmes;

    /// <summary>Whether a call is a raw memory write (<see cref="RuntimeContract.IsRawStore"/>), whose fresh value
    /// argument moves into the written slot, so it must NOT be torn down at the caller.</summary>
    private static bool IsRawStoreCall(CallExpression call)
    {
        return RuntimeContract.IsRawStore(resolved: call.ResolvedRoutine, callee: call.Callee);
    }

    // ForScope: the temporary lives to the end of its block (an entity temporary, see SpillEntityTemporary), so its local
    // is marked for the late scope teardown instead of being torn down at the statement's end.
    private sealed record Spill(string Name, TypeSymbol Type, RoutineInfo Destroy, Expression Init, bool ForScope = false);

    /// <summary>Whether the value being visited lives on after its statement: it is the value a declaration, an
    /// assignment or a return keeps, an argument (a callee may keep it), or a value taken from one of those that may
    /// still point into what it was taken from.</summary>
    private bool _kept;

    /// <summary>Whether the statement being lowered leaves the routine (a return or a throw), so a temporary cannot
    /// outlive it.</summary>
    private bool _terminator;

    /// <summary>Whether the body being lowered got a temporary that lives to the end of its scope, so the late scope
    /// teardown has to run over it.</summary>
    private bool _scopeHoisted;

    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[index: i])
            {
                case RoutineDeclaration r:
                    program.Declarations[index: i] = LowerRoutine(r: r);
                    break;
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

    /// <summary>Lowers free-standing bodies (variant / synthesized routine bodies) in place.</summary>
    public void RunOnBodies(Dictionary<string, Statement>? bodies)
    {
        if (bodies is null)
        {
            return;
        }

        foreach (string key in bodies.Keys.ToList())
        {
            _scopeHoisted = false;
            CountPastSpillsIn(body: bodies[key: key]);
            bodies[key: key] = WithScopeTeardown(body: TransformStatement(stmt: bodies[key: key]));
        }
    }

    /// <summary>
    /// The first run over the monomorphized bodies named by <paramref name="keys"/>: bodies cloned for an
    /// implementer from an analyzed body no temporaries' teardown ran on (see
    /// <see cref="ScopeTeardownLoweringPass.RunOnOwedBodies"/>), lowered as a variant body is.
    /// </summary>
    public void RunOnOwedBodies(Dictionary<string, MonomorphizedBody> bodies, IReadOnlySet<string> keys)
    {
        foreach (string key in keys)
        {
            if (!bodies.TryGetValue(key: key, value: out MonomorphizedBody? entry))
            {
                continue;
            }

            Statement lowered;
            using (ctx.Registry.ViewSurfaceOf(fileName: entry.Ast.Body.Location.FileName))
            {
                _scopeHoisted = false;
                CountPastSpillsIn(body: entry.Ast.Body);
                lowered = WithScopeTeardown(r: entry.Ast with { Body = TransformStatement(stmt: entry.Ast.Body) }).Body;
            }

            if (!ReferenceEquals(objA: lowered, objB: entry.Ast.Body))
            {
                bodies[key: key] = entry with { Ast = entry.Ast with { Body = lowered } };
            }
        }
    }

    /// <summary>
    /// Lowers monomorphized bodies. In a generic definition a value typed <c>T</c> has no known lifecycle,
    /// so the pre-monomorphization run leaves it alone: an <c>a[i]</c> handed to a call, or an overwrite
    /// of a <c>T</c> local. Once <c>T</c> is concrete (<c>Retained[E]</c>, <c>Roamed[E]</c>, <c>Text</c>, a
    /// record with RC fields), the <c>getitem</c> holder is torn down after the call and the overwrite
    /// destroys the old value. Other <c>T</c> producers stay as they are: a generic body also reads
    /// elements in place (<c>peek()</c>, <c>get_raw</c>) without taking a holder, and those must not be
    /// released. What the earlier run already lowered is left as is: a spilled producer is now a binding,
    /// and a lowered reassignment ends in a marked <c>target = __rv</c> tail. Runs after the
    /// post-monomorphization <see cref="RecordCopyLoweringPass"/>, like the Phase 8 run.
    /// </summary>
    public void RunOnInstantiatedGenericBodies(Dictionary<string, MonomorphizedBody> bodies)
    {
        _instantiated = true;
        try
        {
            BodyDispatch.RunOnInstantiatedGenericBodies(registry: ctx.Registry, bodies: bodies,
                lower: (_, entry) =>
                {
                    _scopeHoisted = false;
                    CountPastSpillsIn(body: entry.Ast.Body);
                    return WithScopeTeardown(r: entry.Ast with { Body = TransformStatement(stmt: entry.Ast.Body) })
                       .Body;
                });
        }
        finally
        {
            _instantiated = false;
        }
    }

    /// <summary>
    /// Moves the spill counter past every <c>__tt_N</c> local <paramref name="body"/> declares already (a monomorphized
    /// body keeps the spills an earlier run made over its definition), so a new spill never takes the name of one and
    /// a later read never reaches the wrong local.
    /// </summary>
    private void CountPastSpillsIn(Statement body)
    {
        AstWalker.Walk(root: body, visit: node =>
        {
            if (node is VariableDeclaration { Name: var name } &&
                name.StartsWith(value: "__tt_", comparisonType: StringComparison.Ordinal) &&
                int.TryParse(s: name.AsSpan(start: 5), result: out int n) && n >= _counter)
            {
                _counter = n + 1;
            }
        });
    }

    /// <summary>True while lowering monomorphized bodies (see <see cref="RunOnInstantiatedGenericBodies"/>).</summary>
    private bool _instantiated;

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[index: j] is RoutineDeclaration m)
            {
                members[index: j] = LowerRoutine(r: m);
            }
        }
    }

    private RoutineDeclaration LowerRoutine(RoutineDeclaration r)
    {
        _scopeHoisted = false;
        CountPastSpillsIn(body: r.Body);
        Statement body = TransformStatement(stmt: r.Body);
        RoutineDeclaration lowered = r.Body == body
            ? r
            : r with { Body = body };
        return WithScopeTeardown(r: lowered);
    }

    /// <summary>Gives the receiver temporaries that live to the end of their scope (see <see cref="Spill"/>) their
    /// destroys there and at every exit that leaves it, through the late scope teardown run.</summary>
    private RoutineDeclaration WithScopeTeardown(RoutineDeclaration r)
    {
        if (!_scopeHoisted)
        {
            return r;
        }

        _scopeHoisted = false;
        return new ScopeTeardownLoweringPass(ctx: ctx).LowerLateRoutine(r: r);
    }

    /// <summary><see cref="WithScopeTeardown(RoutineDeclaration)"/> for a body with no declaration of its own.</summary>
    private Statement WithScopeTeardown(Statement body)
    {
        if (!_scopeHoisted)
        {
            return body;
        }

        _scopeHoisted = false;
        return new ScopeTeardownLoweringPass(ctx: ctx).LowerLateBody(body: body);
    }

    // ---------------------------------------------------------------------------------------------
    // Statement transform: descend into structure, spilling at leaf statements that bear expressions.
    // ---------------------------------------------------------------------------------------------

    private Statement TransformStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
            {
                var stmts = new List<Statement>(capacity: b.Statements.Count);
                var hoisted = new HashSet<Statement>(comparer: ReferenceEqualityComparer.Instance);
                for (int i = 0; i < b.Statements.Count; i++)
                {
                    Statement original = b.Statements[index: i];
                    if (hoisted.Contains(item: original))
                    {
                        continue;
                    }

                    Statement lowered = HoistLockExit(lowered: TransformStatement(stmt: original),
                        following: b.Statements, after: i, hoisted: hoisted);
                    // A temporary that lives to the end of its scope is declared in this block, so the late scope
                    // teardown ends it with the block and not with the statement's own wrapper.
                    if (lowered is BlockStatement { IntroducesScope: false } spilled &&
                        spilled.Statements.Any(predicate: IsScopeTemporary))
                    {
                        stmts.AddRange(collection: spilled.Statements);
                        continue;
                    }

                    stmts.Add(item: lowered);
                }

                return b with { Statements = stmts };
            }

            case IfStatement ifs:
            {
                // The condition is evaluated where the if sits (and re-evaluated each iteration when
                // the if is inside a loop body), so hoisting its temps just before the if — and
                // freeing them just after — is correct per-entry RAII.
                Statement then = TransformStatement(stmt: ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null
                    ? TransformStatement(stmt: ifs.ElseStatement)
                    : null;
                IfStatement rebuilt = ifs with { ThenStatement = then, ElseStatement = elseS };
                return SpillAround(owner: rebuilt,
                    root: ifs.Condition,
                    rebuildWithCondition: c => rebuilt with { Condition = c });
            }

            case LoopStatement loop:
                return loop with { Body = TransformStatement(stmt: loop.Body) };

            case WhileStatement w:
                // `while` desugars to LoopStatement before this pass; if one survives, only descend
                // into the body (hoisting a pre-checked condition's temps outside would change when
                // they evaluate). Condition temps in this rare case are left as-is.
                return w with
                {
                    Body = TransformStatement(stmt: w.Body)
                };

            case ForStatement f:
                return f with
                {
                    Body = TransformStatement(stmt: f.Body)
                };

            case DangerStatement d:
                return d with { Body = (BlockStatement)TransformStatement(stmt: d.Body) };

            case UsingStatement u:
                return u with
                {
                    Body = TransformStatement(stmt: u.Body)
                };

            case WhenStatement whenStmt:
            {
                // Should already be lowered to if-chains for the bodies codegen emits; handle
                // defensively by descending into clause bodies (guards left as-is).
                var clauses = whenStmt.Clauses
                                      .Select(selector: c => c with
                                       {
                                           Body = TransformStatement(stmt: c.Body)
                                       })
                                      .ToList();
                return whenStmt with { Clauses = clauses };
            }

            // ---- Leaf statements that bear expressions ----

            case ExpressionStatement es:
                return TransformExpressionStatement(es: es);

            // `drop xs.remove_last()` drops a value the call handed over, so it is torn down when the
            // statement ends (a record a member routine returns is its own, never an alias of the receiver).
            case DropStatement dropped:
            {
                // An entity a call hands over is its own (a RazorForge routine never returns one it still keeps), so
                // the dropped one is torn down too.
                _droppedEntityTop = dropped.Expression is CallExpression or CreatorExpression;
                Statement lowered = SpillAround(owner: dropped,
                    root: dropped.Expression,
                    rebuildWithCondition: e => dropped with { Expression = e },
                    topOwning: false);
                _droppedEntityTop = false;
                return lowered;
            }

            case DeclarationStatement { Declaration: VariableDeclaration v } ds
                when v.Initializer != null:
                return SpillAround(owner: ds,
                    root: v.Initializer,
                    rebuildWithCondition: init =>
                        ds with { Declaration = v with { Initializer = init } });

            case AssignmentStatement { Target: IdentifierExpression t2, KeepsReplacedValue: false } a:
                return LowerReassign(owner: a,
                    rhs: a.Value,
                    target: t2,
                    rebuild: val => a with { Value = val });

            case AssignmentStatement a:
                return SpillAround(owner: a,
                    root: a.Value,
                    rebuildWithCondition: val => a with { Value = val });

            case ReturnStatement { Value: { } rv } ret:
                return SpillAround(owner: ret,
                    root: rv,
                    rebuildWithCondition: val => ret with { Value = val },
                    isTerminator: true);

            case VariantReturnStatement { Value: { } vrv } vret:
                return SpillAround(owner: vret,
                    root: vrv,
                    rebuildWithCondition: val => vret with { Value = val },
                    isTerminator: true);

            case ThrowStatement th:
                return SpillAround(owner: th,
                    root: th.Error,
                    rebuildWithCondition: err => th with { Error = err },
                    isTerminator: true);

            default:
                return stmt;
        }
    }

    private Statement TransformExpressionStatement(ExpressionStatement es)
    {
        // Operator-form assignment (`x = …`): recurse the RHS for spillable receivers, but the
        // RHS value itself is owned by the target — never spill the top.
        if (es.Expression is BinaryExpression
            {
                Operator: BinaryOperator.Assign,
                Left: IdentifierExpression t1
            } bin)
        {
            return LowerReassign(owner: es,
                rhs: bin.Right,
                target: t1,
                rebuild: rhs => es with { Expression = bin with { Right = rhs } });
        }

        // A language without `drop` (Suflae) leaves a result it does not need without a marker, so the result is
        // torn down when the statement ends, as `drop` would. An entity there is a shared handle and a returned
        // `me` is one more holder, so the result is always its own.
        if (es.Location?.FileName is { } file &&
            !Builder.Frontends.Languages.For(language: Builder.Frontends.Languages.OfFile(fileName: file)).MarksDroppedResults)
        {
            return SpillAround(owner: es,
                root: es.Expression,
                rebuildWithCondition: e => es with { Expression = e },
                topOwning: false);
        }

        // A bare expression statement: recurse for receivers only. We do NOT spill the
        // discarded top value — a fluent `me`-returning call (e.g. `b.append(x)`) yields an
        // alias of an existing owned binding, so freeing it would double-free.
        return SpillAround(owner: es,
            root: es.Expression,
            rebuildWithCondition: e => es with { Expression = e });
    }

    /// <summary>
    /// Walks <paramref name="root"/> spilling owned receiver/discarded temporaries, then — if any
    /// were found — wraps <paramref name="owner"/> (rebuilt around the rewritten expression) in a
    /// block that declares the temps before it and destroys them (LIFO).
    /// <paramref name="topOwning"/> is true when <paramref name="root"/> itself sits in an owning
    /// position (var init / assignment RHS / return value), so its top-level producer is left intact.
    ///
    /// <para><paramref name="isTerminator"/> must be set for a control-transferring owner (return /
    /// throw / variant-return): the destroys must run BEFORE the terminator (statements after it are
    /// unreachable — placing teardown there would LEAK every spilled temp), yet the value expression
    /// may still reference those temps. So the value is first computed into a moved-out result temp,
    /// the spills are destroyed, and only then does control transfer:
    /// <code>var __ret = EXPR ; &lt;destroy spills LIFO&gt; ; return __ret</code>
    /// The result temp is fresh/independent of the spilled receivers (a memberRoutine's record return never
    /// aliases its receiver — the same invariant that makes spilling the receivers safe), so freeing
    /// them after computing it is sound.</para>
    /// </summary>
    private Statement SpillAround(Statement owner, Expression root,
        Func<Expression, Statement> rebuildWithCondition, bool topOwning = true,
        bool isTerminator = false)
    {
        var spills = new List<Spill>();
        bool savedKept = _kept;
        bool savedTerminator = _terminator;
        _kept = topOwning;
        _terminator = isTerminator;
        Expression rewritten;
        try
        {
            rewritten = Visit(e: root, objectPos: !topOwning, spills: spills);
        }
        finally
        {
            _kept = savedKept;
            _terminator = savedTerminator;
        }

        if (spills.Count == 0)
        {
            return owner;
        }

        var stmts = new List<Statement>(capacity: spills.Count * 2 + 2);
        foreach (Spill s in spills)
        {
            stmts.Add(item: SpillDecl(spill: s, loc: owner.Location));
        }

        if (isTerminator)
        {
            return EmitTerminatorSpillBlock(owner: owner,
                rewritten: rewritten,
                rebuildWithCondition: rebuildWithCondition,
                spills: spills,
                stmts: stmts);
        }

        stmts.Add(item: rebuildWithCondition(arg: rewritten));
        AddStatementEndDestroys(stmts: stmts, spills: spills, loc: owner.Location);
        return new BlockStatement(Statements: stmts, Location: owner.Location) { IntroducesScope = false };
    }

    /// <summary>The local a spill is held in. One that lives to the end of its scope is marked for the late scope
    /// teardown, which tears it down whatever its type.</summary>
    private static DeclarationStatement SpillDecl(Spill spill, SourceLocation loc)
    {
        return new DeclarationStatement(Declaration: new VariableDeclaration(Name: spill.Name,
                Type: null,
                Initializer: spill.Init,
                Visibility: VisibilityModifier.Secret,
                Location: loc) { OwesTeardown = spill.ForScope, OwnsItsValue = spill.ForScope },
            Location: loc);
    }

    /// <summary>Whether <paramref name="stmt"/> declares a temporary that lives to the end of its scope.</summary>
    private static bool IsScopeTemporary(Statement stmt)
    {
        return stmt is DeclarationStatement
        {
            Declaration: VariableDeclaration { OwesTeardown: true, OwnsItsValue: true } v
        } && v.Name.StartsWith(value: "__tt_", comparisonType: StringComparison.Ordinal);
    }

    /// <summary>Tears down the spills that end with the statement, last one first.</summary>
    private void AddStatementEndDestroys(List<Statement> stmts, List<Spill> spills, SourceLocation loc)
    {
        for (int i = spills.Count - 1; i >= 0; i--)
        {
            if (!spills[index: i].ForScope)
            {
                stmts.Add(item: MakeDestroyStmt(spill: spills[index: i], loc: loc));
            }
        }
    }

    // Compute the transferred value while the spills are still alive, tear them down, then
    // transfer control. Without this the destroys would sit after an unreachable point.
    private BlockStatement EmitTerminatorSpillBlock(Statement owner, Expression rewritten,
        Func<Expression, Statement> rebuildWithCondition, List<Spill> spills,
        List<Statement> stmts)
    {
        string retName = $"__ret_{_counter++}";
        stmts.Add(item: DeclStmt(name: retName, init: rewritten, loc: owner.Location));
        for (int i = spills.Count - 1; i >= 0; i--)
        {
            stmts.Add(item: MakeDestroyStmt(spill: spills[index: i], loc: owner.Location));
        }

        stmts.Add(item: rebuildWithCondition(
            arg: new IdentifierExpression(Name: retName, Location: owner.Location)
            {
                ResolvedType = rewritten.ResolvedType
            }));
        return new BlockStatement(Statements: stmts, Location: owner.Location) { IntroducesScope = false };
    }

    /// <summary>
    /// Lowers an assignment to an identifier target. For an owning record target (a managed leaf such
    /// as Text/Decimal, a record with RC-wrapper fields, or an RC wrapper itself), the overwrite must
    /// first release the old value or it leaks — the dominant cost in string-building loops
    /// (<c>s = s + part</c>). The new RHS is already an independent owned value
    /// (RecordCopyLoweringPass, which has already run, turned any lvalue source into a fresh
    /// <c>store</c>; computed results are fresh), so the rewrite is:
    /// <code>var __rv = RHS ; target.destruct() ; target = __rv</code>
    /// computing RHS (which may read the old target) BEFORE the destroy. Entities are released by
    /// ScopeTeardownLoweringPass and scalars need nothing, so for those we only spill receivers.
    /// </summary>
    private Statement LowerReassign(Statement owner, Expression rhs, IdentifierExpression target,
        Func<Expression, Statement> rebuild)
    {
        // Idempotency guard. The tail THIS pass emits for a managed-leaf reassignment is `target = __rv`,
        // whose RHS identifier carries the STRUCTURED marker below. If the pass runs a second time over an
        // already-lowered body — which happens on the warm-restore path, where a synthesized derive body
        // (e.g. a user record's auto-`represent`) is lowered once as a variant body and again when the
        // program is re-processed — re-lowering that tail would inject a SECOND `target.destruct()` + a
        // second spill, double-freeing the heap buffer (the record-`represent` heap-corruption bug). The
        // marker is set on the tail identifier, NOT recovered by parsing the `__rv_` name, so this is a
        // precise no-op only for the pass's own output.
        if (rhs is IdentifierExpression { IsSynthesizedTeardownTemp: true })
        {
            return owner;
        }

        if (!IsReleasedOnReassign(t: target.ResolvedType))
        {
            return SpillAround(owner: owner, root: rhs, rebuildWithCondition: rebuild);
        }

        TypeSymbol t = target.ResolvedType!;
        RoutineInfo destroy = ctx.Registry.GetLifecycle(type: t)
                                 .Destroy!;
        var spills = new List<Spill>();
        Expression rhs2 = _instantiated
            ? rhs
            : VisitIn(e: rhs, objectPos: false, spills: spills, kept: true);

        var stmts = new List<Statement>(capacity: spills.Count * 2 + 3);
        foreach (Spill s in spills)
        {
            stmts.Add(item: SpillDecl(spill: s, loc: owner.Location));
        }

        string newName = $"__rv_{_counter++}";
        stmts.Add(item: DeclStmt(name: newName, init: rhs2, loc: owner.Location));
        stmts.Add(item: MakeDestroyCall(name: target.Name,
            type: t,
            destroy: destroy,
            loc: owner.Location,
            variable: target.ResolvedVariable));
        stmts.Add(item: rebuild(
            arg: new IdentifierExpression(Name: newName, Location: owner.Location)
            {
                // The spill's own type: a value going into a `Optional` place (`n = 5` with `n: Int?`) is still
                // the bare value here, and the later Optional wrap needs to see that.
                ResolvedType = rhs2.ResolvedType ?? t, IsSynthesizedTeardownTemp = true
            }));
        AddStatementEndDestroys(stmts: stmts, spills: spills, loc: owner.Location);

        return new BlockStatement(Statements: stmts, Location: owner.Location) { IntroducesScope = false };
    }

    /// <summary>
    /// A reassignment of a <c>Roamed</c> handle <c>X</c> inside an access-lock bracket
    /// (<c>X.lock_enter()</c> … <c>X = X.next</c> … <c>X.lock_exit()</c>, RoamedLockBracketLoweringPass) has
    /// just been expanded to <c>var __rv = RHS ; X.destruct() ; X = __rv</c>. The lock was taken on the old
    /// object, so it must be released on the old object, and before that object is released: the
    /// bracket's <c>X.lock_exit()</c> (one of the closing calls right after the statement) moves to just
    /// before <c>X.destruct()</c>. Left where it was, it would unlock the NEW object (or a none handle) and
    /// release the old one while still locked. Returns <paramref name="lowered"/> unchanged otherwise.
    /// </summary>
    private static Statement HoistLockExit(Statement lowered, List<Statement> following, int after,
        HashSet<Statement> hoisted)
    {
        if (lowered is not BlockStatement block)
        {
            return lowered;
        }

        int destroyAt = block.Statements.FindIndex(match: s => DestroyedLocal(stmt: s) is not null);
        if (destroyAt < 0)
        {
            return lowered;
        }

        string target = DestroyedLocal(stmt: block.Statements[index: destroyAt])!;
        for (int j = after + 1; j < following.Count && LockExitHandle(stmt: following[index: j]) is { } handle; j++)
        {
            if (handle != target)
            {
                continue;
            }

            hoisted.Add(item: following[index: j]);
            var statements = new List<Statement>(collection: block.Statements);
            statements.Insert(index: destroyAt, item: following[index: j]);
            return block with { Statements = statements };
        }

        return lowered;
    }

    /// <summary>The local a <c>local.destruct()</c> statement releases, or null.</summary>
    private static string? DestroyedLocal(Statement stmt)
    {
        return stmt is ExpressionStatement
        {
            Expression: CallExpression
            {
                Callee: MemberExpression { Object: IdentifierExpression local, MemberName: "destruct" }
            }
        }
            ? local.Name
            : null;
    }

    /// <summary>The local a <c>local.lock_exit()</c> statement unlocks, or null.</summary>
    private static string? LockExitHandle(Statement stmt)
    {
        return stmt is ExpressionStatement
        {
            Expression: CallExpression
            {
                Callee: MemberExpression { Object: IdentifierExpression local } member
            }
        } && member.MemberName == RuntimeContract.RoamedMemberRoutine.LockExit
            ? local.Name
            : null;
    }

    private static DeclarationStatement DeclStmt(string name, Expression init, SourceLocation loc)
    {
        return new DeclarationStatement(Declaration: new VariableDeclaration(Name: name,
                Type: null,
                Initializer: init,
                Visibility: VisibilityModifier.Secret,
                Location: loc),
            Location: loc);
    }

    /// <summary>True for a record target whose old value must be destroyed on reassignment: a managed
    /// leaf with a retaining <c>store</c> (Text/Decimal, or a record carrying such a field), a record
    /// with an RC-wrapper field (checked structurally: a wrapper field is a record type, so the old
    /// <c>HasRCMemberVariables</c> flag), or an
    /// RC wrapper (Retained/Tracked/Guarded/Witnessed/Roamed, whose
    /// <c>destruct</c> is a no-op on the zeroed handle a <c>lateinit</c> binding starts with). Borrow
    /// views, scalars and plain value records are excluded, and entities are handled by
    /// ScopeTeardownLoweringPass.</summary>
    private bool IsReleasedOnReassign(TypeSymbol? t)
    {
        if (t is not RecordTypeSymbol rec)
        {
            return false;
        }

        TypeRegistry.Lifecycle lc = ctx.Registry.GetLifecycle(type: t);
        if (lc.IsBorrow || lc.Destroy == null)
        {
            return false;
        }

        return lc.Store != null || TypeRegistry.GetRcWrapperBaseName(type: rec) is not null ||
               rec.MemberVariables.Any(predicate: f =>
                   TypeRegistry.GetRcWrapperBaseName(type: f.Type) is not null);
    }

    // ---------------------------------------------------------------------------------------------
    // Expression visitor: children first (post-order), so inner temps are declared before outer ones.
    // `objectPos` is true only for positions whose owned producer should be torn down here.
    // ---------------------------------------------------------------------------------------------

    /// <summary><see cref="Visit"/> with <see cref="_kept"/> set to <paramref name="kept"/> for the subtree.</summary>
    private Expression VisitIn(Expression e, bool objectPos, List<Spill> spills, bool kept)
    {
        bool saved = _kept;
        _kept = kept;
        try
        {
            return Visit(e: e, objectPos: objectPos, spills: spills);
        }
        finally
        {
            _kept = saved;
        }
    }

    /// <summary>Whether a part of <paramref name="whole"/> lives on after the statement: the whole is kept and may
    /// still point into the part (<paramref name="part"/>, the receiver, object or operand being visited).</summary>
    private bool PartKept(Expression whole, Expression? part)
    {
        return _kept && MayHoldReceiver(type: whole.ResolvedType, part: part?.ResolvedType, depth: 0);
    }

    private Expression Visit(Expression e, bool objectPos, List<Spill> spills)
    {
        switch (e)
        {
            case CallExpression { Callee: MemberExpression m } call:
                return VisitMemberCall(call: call,
                    m: m,
                    objectPos: objectPos,
                    spills: spills);

            case CallExpression call:
            {
                Expression newCallee = VisitIn(e: call.Callee, objectPos: false, spills: spills, kept: false);
                // See the member-call case: owning-position args (torn down at the caller) unless this is
                // a store primitive.
                bool argsOwned = !RuntimeContract.IsMemberwiseConstruction(
                                     constructedType: call.ConstructedType,
                                     resolved: call.ResolvedRoutine) &&
                                 !IsRawStoreCall(call: call);
                List<Expression> newArgs = VisitArguments(arguments: call.Arguments, argsOwned: argsOwned,
                    spills: spills);
                Expression result = call with { Callee = newCallee, Arguments = newArgs };
                return MaybeSpillTop(e: result, objectPos: objectPos, spills: spills);
            }

            case MemberExpression m:
            {
                // A field read on a fresh owned value (`xs[0].id`, where `getitem` hands back a share of its
                // own) reads the field out within the statement, so the value is torn down when the statement
                // ends, unless the field's value may point into it.
                // An entity made for the read (`pair(x: 3).items.count()`) is torn down the same way.
                bool fieldMayAlias = ResultMayAliasReceiver(resultType: m.ResolvedType);
                Expression newObj = VisitIn(e: m.Object,
                    objectPos: !fieldMayAlias,
                    spills: spills,
                    kept: PartKept(whole: m, part: m.Object));
                if (!fieldMayAlias && IsSpillableEntityTemporary(e: newObj))
                {
                    newObj = SpillEntityTemporary(producer: newObj, consumer: m, spills: spills);
                }

                return m with { Object = newObj };
            }

            case IndexExpression ix:
            {
                Expression newObj = VisitIn(e: ix.Object, objectPos: false, spills: spills, kept: false);
                Expression newIdx = VisitIn(e: ix.Index, objectPos: false, spills: spills, kept: false);
                return ix with { Object = newObj, Index = newIdx };
            }

            // A named argument is an argument like a positional one: its value takes the position the call
            // gives it (a fresh value passed to a call is torn down after the call).
            case NamedArgumentExpression na:
                return na with { Value = Visit(e: na.Value, objectPos: objectPos, spills: spills) };

            case BinaryExpression b:
            {
                bool operandsKept = PartKept(whole: b, part: b.Left) || PartKept(whole: b, part: b.Right);
                return b with
                {
                    Left = VisitIn(e: b.Left, objectPos: false, spills: spills, kept: operandsKept),
                    Right = VisitIn(e: b.Right, objectPos: false, spills: spills, kept: operandsKept)
                };
            }

            case UnaryExpression u:
                return u with
                {
                    Operand = VisitIn(e: u.Operand,
                        objectPos: false,
                        spills: spills,
                        kept: PartKept(whole: u, part: u.Operand))
                };

            // A construction keeps its member values (`Optional[Text](present: true, value: a + b)`, the value a
            // recovery variant gives back), so a value is not torn down, the temporaries it is made from are.
            // The construction itself is a fresh value like a call's result, spilled where one would be.
            case CreatorExpression creator:
                return MaybeSpillTop(e: creator with
                    {
                        MemberVariables = creator.MemberVariables
                                                 .Select(selector: mv => (mv.Name,
                                                      VisitIn(e: mv.Value, objectPos: false, spills: spills,
                                                          kept: true)))
                                                 .ToList()
                    },
                    objectPos: objectPos,
                    spills: spills);

            case StealExpression st:
                // `steal` is an explicit move — never tear down its operand.
                return st with
                {
                    Operand = VisitIn(e: st.Operand, objectPos: false, spills: spills, kept: true)
                };

            // An inline `Array[T, N]` literal handed to a call (the packed `elements...` of `from_literal`) only
            // carries its elements to the callee, which stores what it keeps. Each fresh element is then an
            // argument like any other and is torn down after the call. A literal that initializes a binding
            // (not an argument) owns its elements, so nothing is torn down there.
            case ListLiteralExpression { ResolvedType: RecordTypeSymbol { GenericDefinition.Name: "Items" or "Slots" } } arr
                when objectPos:
                return arr with
                {
                    Elements = arr.Elements
                                  .Select(selector: el => VisitIn(e: el, objectPos: true, spills: spills, kept: true))
                                  .ToList()
                };

            default:
                // Identifiers, literals, and node forms not modeled here: leave untouched. A producer
                // sitting at the very top in a discarded position is still handled below.
                return MaybeSpillTop(e: e, objectPos: objectPos, spills: spills);
        }
    }

    private Expression VisitMemberCall(CallExpression call, MemberExpression m, bool objectPos,
        List<Spill> spills)
    {
        // Recurse the receiver WITHOUT letting it self-spill (objectPos:false): receiver
        // teardown is decided HERE, where the enclosing call's result type is known, so the
        // aliasing guard can apply. Nested receivers (a.b().c()) are handled by this same
        // branch one level down, each guarded by its own call's result type.
        // A COPY verb (the copy step `share` / `duplicate`) reads its receiver to MINT an owned value — so the
        // receiver is a value being copied FROM (an lvalue read, or a raw in-place read like a
        // container's `get_raw`), not a fresh owned producer to tear down here. The minted copy (the
        // call result) is what gets torn down. The exception is `a[i]`: `getitem` hands back a holder
        // of its own, so `xs[0].duplicate()` still releases the element it read.
        // Constructing an RC wrapper FROM a bare entity (STRUCTURAL: entity receiver + RC-wrapper
        // result) moves it into the controller, so that receiver is not torn down either.
        bool receiverIsIndexRead = m.Object is CallExpression { Callee: MemberExpression { MemberName: "getitem" } };
        // `release_inner` takes the entity out of its handle and ends the handle, so the handle is not torn down.
        // An RC handle's `share` is the other way round: one more holder of a fresh handle, which is still torn
        // down here, so the two balance.
        bool receiverIsHandle = m.Object.ResolvedType is { } receiverType &&
                                TypeRegistry.GetRcWrapperBaseName(type: receiverType) is not null;
        bool receiverConsumed = (m.MemberName == "duplicate" || m.MemberName == "share" && !receiverIsHandle) &&
                                !receiverIsIndexRead ||
                                m.MemberName == RuntimeContract.RoamedMemberRoutine.ReleaseInner ||
                                m.Object.ResolvedType is EntityTypeSymbol &&
                                call.ResolvedType is { } rcCtorRes &&
                                TypeRegistry.GetRcWrapperBaseName(type: rcCtorRes) is not null;
        Expression newRecv = VisitIn(e: m.Object,
            objectPos: false,
            spills: spills,
            kept: PartKept(whole: call, part: m.Object));

        // Spill the receiver iff it is a fresh heap-owning RC-record producer, the verb does
        // not consume it (retain/track move it into the RC controller), and the call result
        // cannot be a borrow/view aliasing it. An RC-record receiver is safe to free even when
        // the result is another owned value: a memberRoutine's RC-record/record return is always
        // independent of the receiver — fresh (e.g. string concat allocates a new buffer) or a
        // retaining +1 copy (RecordCopyLoweringPass injects store on lvalue/`me` returns) — so
        // the controller refcount stays balanced. The only hazard is a borrow/view result
        // (Viewing/Modifying/…) pointing into the receiver, which the guard excludes.
        if (!receiverConsumed && IsSpillableProducer(e: newRecv) &&
            !ResultMayAliasReceiver(resultType: call.ResolvedType))
        {
            newRecv = MakeSpill(producer: newRecv, spills: spills);
        }
        // An entity made only to call one of its routines (`pair(x: 3).count()`, `make().emitter()`) is torn down at
        // the end of its block (see SpillEntityTemporary).
        else if (!receiverConsumed && IsSpillableEntityTemporary(e: newRecv) &&
                 !ResultMayAliasReceiver(resultType: call.ResolvedType) &&
                 !ResultLocatesEntity(resultType: call.ResolvedType))
        {
            newRecv = SpillEntityTemporary(producer: newRecv, consumer: call, spills: spills);
        }
        // A carrier a call gives back holding an entity of its own (`(try xs.to[List]()).represent()`, the value of
        // an f-text slot) is read by its display routine and ends with the statement.
        else if (!receiverConsumed && m.MemberName is RuntimeContract.Display.Represent
                     or RuntimeContract.Display.Diagnose &&
                 IsFreshEntityCarrier(e: newRecv))
        {
            newRecv = MakeSpill(producer: newRecv, spills: spills);
        }

        // Three-rules model: a fresh owned RVALUE arg passed to a borrow param is torn down at
        // the CALLER (the callee only borrows it and no longer frees it). So visit args in owning
        // position — EXCEPT for a raw memory write (an `LLVM::` store intrinsic), whose value arg is
        // MOVED into the slot; spilling it would free the just-inserted element → UAF. A routine that only
        // passes the value down to such a write (`poke`) borrows it like any other call.
        // A memberwise construction persists its args into the new value's fields (a destination that
        // RETAINS via RecordCopyLoweringPass), and a store primitive MOVES its value into storage — in both
        // cases the arg lives on, so it must NOT be torn down at the caller. Every other call, a written
        // creator and `setitem` included, borrows a fresh rvalue arg.
        bool argsOwned = !RuntimeContract.IsMemberwiseConstruction(constructedType: call.ConstructedType,
                             resolved: call.ResolvedRoutine) &&
                         !IsRawStoreCall(call: call);
        List<Expression> newArgs = VisitArguments(arguments: call.Arguments, argsOwned: argsOwned, spills: spills);
        Expression result = call with
        {
            Callee = m with { Object = newRecv }, Arguments = newArgs
        };
        return MaybeSpillTop(e: result, objectPos: objectPos, spills: spills);
    }

    /// <summary>
    /// Visits a call's arguments. An entity made for an argument (<c>keys.add(Keyed(name: "Ada"))</c>) is handed over
    /// to the routine called, which keeps it or tears it down itself, so it is never torn down here, also inside a
    /// <c>drop</c> statement, whose own rule covers only the entity the statement throws away.
    /// </summary>
    private List<Expression> VisitArguments(List<Expression> arguments, bool argsOwned, List<Spill> spills)
    {
        bool droppedEntityTop = _droppedEntityTop;
        _droppedEntityTop = false;
        try
        {
            return arguments.Select(selector: a => VisitIn(e: a, objectPos: argsOwned, spills: spills, kept: true))
                            .ToList();
        }
        finally
        {
            _droppedEntityTop = droppedEntityTop;
        }
    }

    /// <summary>Spills <paramref name="e"/> when it sits in a discard/borrow position and is a
    /// spillable owned producer (the discarded-value case — no aliasing concern since the value is
    /// not stored anywhere).</summary>
    /// <summary>True while the value a <c>drop</c> statement throws away is visited: an entity a call there hands over
    /// (the dropped one, or a receiver made for one call) is spilled and torn down when the statement ends.</summary>
    private bool _droppedEntityTop;

    private Expression MaybeSpillTop(Expression e, bool objectPos, List<Spill> spills)
    {
        // A call that hands out access to an entity kept elsewhere (`me[i]`, which is `modify_at`) gives a token, not
        // the entity: the container still owns it.
        if (objectPos && _droppedEntityTop && e is CallExpression or CreatorExpression &&
            !HandsOutAccess(e: e) &&
            e.ResolvedType is EntityTypeSymbol droppedEntity &&
            ctx.Registry.GetLifecycle(type: droppedEntity) is { IsBorrow: false, Destroy: not null })
        {
            return MakeSpill(producer: e, spills: spills);
        }

        if (objectPos && IsSpillableProducer(e: e))
        {
            return MakeSpill(producer: e, spills: spills);
        }

        return e;
    }

    /// <summary>Whether <paramref name="e"/> is a call whose routine returns an access token (<c>Viewing</c> /
    /// <c>Modifying</c>) to a value kept elsewhere.</summary>
    private static bool HandsOutAccess(Expression e)
    {
        return e is CallExpression { ResolvedRoutine.ReturnType: { } returned } &&
               ImplicitToken.IsTokenParameter(parameter: returned, tokenKind: out _, target: out _);
    }

    /// <summary>
    /// Spills an entity temporary used by <paramref name="consumer"/> (a call of one of its routines, or a read of one
    /// of its member variables). The entity lives to the end of its block, as a local the block declared would: what
    /// the call gives back may point into it and live on (an emitter, an adapter, a token), and its teardown may wait
    /// for work it started (<c>f.to[Work](...).start_now()</c> runs until the block ends). In a return or throw the
    /// statement's end is the block's end, unless what is handed out may point into the entity (see
    /// <see cref="MayHoldReceiver"/>): it cannot outlive the routine, so there it is left as it was.
    /// </summary>
    private Expression SpillEntityTemporary(Expression producer, Expression consumer, List<Spill> spills)
    {
        if (_terminator)
        {
            return PartKept(whole: consumer, part: producer)
                ? producer
                : MakeSpill(producer: producer, spills: spills);
        }

        _scopeHoisted = true;
        return MakeSpill(producer: producer, spills: spills, forScope: true);
    }

    /// <summary>
    /// True for an entity a call or construction made fresh (RazorForge: a routine never gives back an entity it still
    /// keeps) with a destroy: not a token, not a view verb's result, and not what a library routine that gives back a
    /// bare <c>T</c> reads in place (<c>get_raw</c>, <c>peek</c>). In a monomorphized body only a routine value's
    /// result and a construction are spilled, as only those the definition could not see are fresh for sure. Shared
    /// entities (Suflae) are handles, torn down as records.
    /// </summary>
    private bool IsSpillableEntityTemporary(Expression e)
    {
        if (ctx.Registry.CompilationRules.EntitiesAreShared || e is not (CallExpression or CreatorExpression) ||
            e.ResolvedType is not EntityTypeSymbol entity || HandsOutAccess(e: e))
        {
            return false;
        }

        if (e is CallExpression call)
        {
            if (call.Callee is MemberExpression vm && ViewVerbs.Contains(item: vm.MemberName))
            {
                return false;
            }

            RoutineInfo? routine = call.ResolvedRoutine;
            RoutineInfo? declared = routine?.GenericDefinition ?? routine?.RecoveryOf?.GenericDefinition ??
                                    routine?.RecoveryOf ?? routine;
            if (declared?.ReturnType is GenericParameterTypeSymbol &&
                ctx.Registry.IsStandardLibraryFile(filePath: declared.Location?.FileName ?? ""))
            {
                return false;
            }

            if (_instantiated && call.LoweringKind != CallLoweringKind.DynamicCall && call.ConstructedType == null)
            {
                return false;
            }
        }

        return DestroyOf(type: entity) != null;
    }

    /// <summary>
    /// Whether <paramref name="e"/> is a call giving back a carrier (<c>Optional</c>, <c>Check</c>) of an entity,
    /// which the carrier's teardown ends: a RazorForge routine never gives back an entity it still keeps, so the
    /// entity is the carrier's own.
    /// </summary>
    private bool IsFreshEntityCarrier(Expression e)
    {
        if (_instantiated || ctx.Registry.CompilationRules.EntitiesAreShared ||
            e is not CallExpression call || HandsOutAccess(e: e) ||
            call.Callee is MemberExpression vm && ViewVerbs.Contains(item: vm.MemberName) ||
            e.ResolvedType is not { TypeArguments: [EntityTypeSymbol] } carrier ||
            carrier is EntityTypeSymbol || TypeRegistry.GetRcWrapperBaseName(type: carrier) is not null ||
            WrapperShape.TryGet(type: carrier, name: out _, inner: out _))
        {
            return false;
        }

        return DestroyOf(type: carrier) != null;
    }

    /// <summary>Whether a call on an entity gives back where the entity is (<c>make().hijack()</c>, an address): the
    /// entity is placed by hand from then on, so it is not torn down.</summary>
    private static bool ResultLocatesEntity(TypeSymbol? resultType)
    {
        return resultType is { Name: RuntimeContract.Addr } ||
               WrapperShape.TryGet(type: resultType, name: out string wrapper, inner: out _) &&
               wrapper is not (RuntimeContract.Viewing or RuntimeContract.Modifying) &&
               TypeRegistry.GetRcWrapperBaseName(type: resultType!) is null;
    }

    /// <summary>
    /// Whether a value of <paramref name="type"/> may still point into the value it was taken from, so that value has
    /// to live as long as this one: a token or another view, an unknown type, an emitter, or a value holding a pointer
    /// to an emittable that may be <paramref name="part"/> or lie inside it (the adapter <c>xs.select(f)</c> points at
    /// <c>xs</c>). A value of its own (a number, <c>Text</c>, a counted handle, a list a terminal collected) does not:
    /// a <c>List[Text]</c> collected from an adapter holds a pointer to its own Text values, which are emittables of
    /// another type than anything in the adapter.
    /// </summary>
    private bool MayHoldReceiver(TypeSymbol? type, TypeSymbol? part, int depth)
    {
        switch (type)
        {
            case null or GenericParameterTypeSymbol or ProtocolTypeSymbol or ProtocolSelfTypeSymbol:
                return true;
        }

        if (TypeRegistry.GetRcWrapperBaseName(type: type) is not null || type.IsNone)
        {
            return false;
        }

        if (WrapperShape.TryGet(type: type, name: out _, inner: out _))
        {
            return true;
        }

        if (ctx.Registry.TypeObeysProtocol(type: type, protocolName: "Emitter"))
        {
            return true;
        }

        if (depth > 3 || type is RecordTypeSymbol && ctx.Registry.GetLifecycle(type: type).Store != null)
        {
            return false;
        }

        IEnumerable<TypeSymbol> fields = type switch
        {
            EntityTypeSymbol entity => entity.MemberVariables.Select(selector: f => f.Type),
            RecordTypeSymbol record => record.MemberVariables.Select(selector: f => f.Type),
            _ => []
        };
        foreach (TypeSymbol field in fields)
        {
            if (TypeRegistry.GetRcWrapperBaseName(type: field) is null &&
                WrapperShape.TryGet(type: field, name: out _, inner: out TypeSymbol pointee))
            {
                if (pointee is GenericParameterTypeSymbol ||
                    ctx.Registry.TypeObeysProtocol(type: pointee, protocolName: "Emittable") &&
                    (part == null || MayLieInside(inner: pointee, outer: part, depth: 0)))
                {
                    return true;
                }

                continue;
            }

            if (field is EntityTypeSymbol or RecordTypeSymbol &&
                MayHoldReceiver(type: field, part: part, depth: depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a value of <paramref name="inner"/> may be <paramref name="outer"/> itself or sit inside it: one of its
    /// member variables, or what one of its pointers points at, followed a few levels down. An unknown or generic type
    /// on either side may be anything.
    /// </summary>
    private static bool MayLieInside(TypeSymbol inner, TypeSymbol outer, int depth)
    {
        if (outer is GenericParameterTypeSymbol or ProtocolTypeSymbol or ProtocolSelfTypeSymbol ||
            SameType(a: inner, b: outer))
        {
            return true;
        }

        if (depth > 3)
        {
            return false;
        }

        IEnumerable<TypeSymbol> fields = outer switch
        {
            EntityTypeSymbol entity => entity.MemberVariables.Select(selector: f => f.Type),
            RecordTypeSymbol record => record.MemberVariables.Select(selector: f => f.Type),
            _ => []
        };
        foreach (TypeSymbol field in fields)
        {
            TypeSymbol next = TypeRegistry.GetRcWrapperBaseName(type: field) is null &&
                              WrapperShape.TryGet(type: field, name: out _, inner: out TypeSymbol pointee)
                ? pointee
                : field;
            if (MayLieInside(inner: inner, outer: next, depth: depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameType(TypeSymbol a, TypeSymbol b)
    {
        return ReferenceEquals(objA: a, objB: b) || a.FullName == b.FullName;
    }

    /// <summary>
    /// The destroy of <paramref name="type"/>, null for a borrow. An entity made concrete in a monomorphized body
    /// (<c>List[Crate]</c>) may have only its instance routines registered so far, so its destroy is looked up as a
    /// call to it would be.
    /// </summary>
    private RoutineInfo? DestroyOf(TypeSymbol type)
    {
        TypeRegistry.Lifecycle lc = ctx.Registry.GetLifecycle(type: type);
        if (lc.IsBorrow)
        {
            return null;
        }

        return lc.Destroy ?? (type is EntityTypeSymbol
            ? ctx.Registry.LookupMemberRoutine(type: type, memberRoutineName: RuntimeContract.Destroy)
            : null);
    }

    private IdentifierExpression MakeSpill(Expression producer, List<Spill> spills, bool forScope = false)
    {
        TypeSymbol type = producer.ResolvedType!;
        RoutineInfo destroy = DestroyOf(type: type)!;
        string name = $"__tt_{_counter++}";
        spills.Add(item: new Spill(Name: name,
            Type: type,
            Destroy: destroy,
            Init: producer,
            ForScope: forScope));
        return new IdentifierExpression(Name: name, Location: producer.Location)
        {
            ResolvedType = type
        };
    }

    /// <summary>True for a fresh owned heap producer worth tearing down: a call/creator whose result
    /// is an entity or RC-record with a real (non-borrow) <c>destruct</c>, excluding view-verb
    /// producers (which yield a borrow of a referent owned elsewhere).</summary>
    private bool IsSpillableProducer(Expression e)
    {
        if (e is not (CallExpression or CreatorExpression))
        {
            return false;
        }

        // In a monomorphized body only a read that hands back a holder of its own is spilled: `a[i]` (getitem) and a
        // token read out where the value is wanted (`v.access()`, which TokenReadLoweringPass writes once `T` is known).
        if (_instantiated && e is not CallExpression
            {
                Callee: MemberExpression { MemberName: "getitem" or RuntimeContract.Access }
            })
        {
            return false;
        }

        if (e is CallExpression { Callee: MemberExpression vm } &&
            ViewVerbs.Contains(item: vm.MemberName) && !ReadsRecordOutOfToken(call: (CallExpression)e))
        {
            return false;
        }

        TypeSymbol? t = e.ResolvedType;
        if (t is null)
        {
            return false;
        }

        TypeRegistry.Lifecycle lc = ctx.Registry.GetLifecycle(type: t);
        if (lc.IsBorrow || lc.Destroy is null)
        {
            return false;
        }

        // Only HEAP-owning RECORDS are spilled: a managed leaf with a retaining store (Text/Decimal),
        // an RC wrapper (Retained/Tracked/Guarded/Witnessed/Roamed: a fresh `x.share()` handed to a call
        // is a holder of its own, released here once the call is done), or a record carrying RC-wrapper
        // fields. Their destroy releases a refcounted controller, so an extra balanced release is always
        // safe. Entities are deliberately excluded for now (their single-owner lifetime and fluent `me`
        // returns are trickier to prove alias-free); plain value records / scalars have a no-op destroy
        // and would only bloat the IR.
        return t is RecordTypeSymbol rec && (lc.Store != null || rec.HasRCMemberVariables ||
                                             TypeRegistry.GetRcWrapperBaseName(type: rec) is not null);
    }

    /// <summary>Whether <paramref name="call"/> reads a record out of its token (<c>v.access()</c> on a
    /// <c>Viewing[T]</c> or <c>Modifying[T]</c>): the record comes back as a holder of its own, as <c>getitem</c> gives
    /// one, unlike an entity's <c>access</c>, which is the entity itself.</summary>
    private static bool ReadsRecordOutOfToken(CallExpression call)
    {
        return call.Callee is MemberExpression { MemberName: RuntimeContract.Access } access &&
               call.ResolvedType is RecordTypeSymbol &&
               WrapperShape.TryGet(type: access.Object.ResolvedType, name: out string token, inner: out _) &&
               token is RuntimeContract.Viewing or RuntimeContract.Modifying;
    }

    /// <summary>True when a call result MAY be a borrow/view pointing into the receiver, so freeing
    /// the receiver after the call could dangle it. Borrow/view wrappers and unknown/abstract results
    /// are treated as possibly-aliasing; scalars, value/RC records, RC wrappers, entities, and
    /// <c>None</c> are independent of an RC-record receiver and safe.</summary>
    private static bool ResultMayAliasReceiver(TypeSymbol? resultType)
    {
        return resultType switch
        {
            null => true,
            GenericParameterTypeSymbol => true,
            ProtocolTypeSymbol => true,
            // A single-thread token taken from a temporary (`make().view()`) is used within its statement only
            // (RazorForge's TokenLifetimeChecker rejects a later use), and the spill lives until the statement ends.
            _ => WrapperShape.TryGet(type: resultType, name: out string wrapper, inner: out _) &&
                 BorrowWrapperNames.Contains(item: wrapper) &&
                 wrapper is not (RuntimeContract.Viewing or RuntimeContract.Modifying)
        };
    }

    private ExpressionStatement MakeDestroyStmt(Spill spill, SourceLocation loc)
    {
        return MakeDestroyCall(name: spill.Name,
            type: spill.Type,
            destroy: spill.Destroy,
            loc: loc);
    }

    private ExpressionStatement MakeDestroyCall(string name, TypeSymbol type, RoutineInfo destroy,
        SourceLocation loc, VariableInfo? variable = null)
    {
        // The variable the name was resolved to goes along: a global's name may be one two modules have.
        var ident = new IdentifierExpression(Name: name, Location: loc) { ResolvedType = type, ResolvedVariable = variable };
        var callee = new MemberExpression(Object: ident, MemberName: "destruct", Location: loc)
        {
            ResolvedType = _blankType
        };
        var call = new CallExpression(Callee: callee, Arguments: [], Location: loc)
        {
            ResolvedRoutine = destroy,
            ResolvedType = _blankType,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: destroy)
        };
        return new ExpressionStatement(Expression: call, Location: loc);
    }
}
