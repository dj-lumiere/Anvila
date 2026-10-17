using Builder.Tokenizer;
using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Moves the <c>Roamed[E]</c> access-lock bracket out of codegen and into a real AST call. A direct
/// field READ or WRITE through a <c>Roamed[E]</c> handle must be wrapped in
/// <c>handle.lock_enter()</c> … <c>handle.lock_exit()</c> so that — once the object has ESCAPED to
/// another worker — the field touch is serialized. The lock is a no-op while the object is LOCAL and a
/// <b>reentrant</b>, task-keyed acquire once escaped (see <c>Roamed.rf</c>).
///
/// <para>Previously codegen inserted <c>call void @lock_enter/@lock_exit(handle)</c> itself, with NO
/// surface AST call, bracketing each individual field read/write. This pass makes it a REAL AST rewrite
/// instead: for every statement that directly contains a Roamed[E] field access, a
/// <c>handle.lock_enter()</c> <see cref="ExpressionStatement"/> is inserted immediately BEFORE the
/// statement and a matching <c>handle.lock_exit()</c> immediately AFTER it. The read/write itself keeps
/// its value — codegen now just projects through <c>RoamManager.data</c> and emits the load/store,
/// with the lock bracket already present as ordinary calls surrounding the whole statement.</para>
///
/// <para><b>Bracket-count decision: per-statement (once per field-access receiver), NOT per-access.</b>
/// The old codegen bracketed each individual access; because the lock is <i>reentrant</i> and a no-op
/// while local, bracketing the whole enclosing statement once per receiver is exact-parity for the
/// escaped-mode serialization semantics (every field touch inside the statement is still inside a held
/// acquire) while keeping this pass simple and safe under NESTED access (multiple Roamed reads in one
/// statement). A single enter…exit spanning the statement cannot leave any field touch unguarded.</para>
///
/// <para>Reachability already seeds <c>lock_enter</c>/<c>lock_exit</c> for every live <c>Roamed[T]</c>
/// via <c>ImplicitCallContract.ForLiveType</c>, so the targets are live/monomorphized. Mirrors
/// <see cref="RoamedSpawnPromotionLoweringPass"/> (statement insertion around a site). Runs after the
/// other Roamed passes so the field accesses it brackets are in final form.</para>
/// </summary>
internal sealed class RoamedLockBracketLoweringPass(PostprocessingContext ctx)
{
    private TypeRegistry Registry => ctx.Registry;

    /// <summary>Inserts Roamed field-access lock brackets across a whole program.</summary>
    public void Run(Program program)
    {
        foreach (SyntaxTree.Declaration decl in
                 program.Declarations.OfType<SyntaxTree.Declaration>())
        {
            LowerDeclaration(decl: decl);
        }
    }

    /// <summary>Inserts Roamed field-access lock brackets in synthesized variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            LowerBody(body: ctx.VariantBodies[key: key]);
        }
    }

    private void LowerDeclaration(SyntaxTree.Declaration decl)
    {
        switch (decl)
        {
            case RoutineDeclaration r:
                LowerBody(body: r.Body);
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

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        foreach (SyntaxTree.Declaration m in members)
        {
            if (m is RoutineDeclaration mr)
            {
                LowerBody(body: mr.Body);
            }
        }
    }

    private void LowerBody(Statement body)
    {
        if (body is BlockStatement block)
        {
            LowerBlock(block: block);
        }
    }

    // ---- Block rewrite (in place, mirroring RoamedSpawnPromotionLoweringPass) ---------------------

    private void LowerBlock(BlockStatement block)
    {
        var rewritten = new List<Statement>(capacity: block.Statements.Count);
        foreach (Statement stmt in block.Statements)
        {
            if (stmt is BlockStatement { HoldsAccessLock: true } held)
            {
                LowerHeldBlock(block: held, into: rewritten);
                continue;
            }

            RecurseInto(stmt: stmt);
            // An atomic-width scalar global RMW (`__globals__.n = __globals__.n.add(d)`) becomes one
            // lock-free AtomicRmwStatement instead of a bracketed load-compute-store. Everything else
            // (heavy-value field RMW, plain read/write) still gets the access-lock brackets.
            if (TryLowerAtomicGlobalRmw(stmt: stmt) is { } atomic)
            {
                rewritten.Add(item: atomic);
                continue;
            }

            Statement current = ReadFieldsBeforeCalls(stmt: stmt, into: rewritten);
            List<Expression> handles = FieldAccessHandles(stmt: current)
                                       .Where(predicate: h => IsHeldPlace(handle: h) && !IsSelfLocking(handle: h))
                                       .ToList();
            // The IterGuard: a write to a member variable of an entity changes the entity itself (a container's
            // count, capacity or buffer), which an `each` loop over it or a call on one of its elements relies
            // on. Where shapes are checked at run time, the write crashes while such a use is open. An element's
            // value lives in the container's buffer, not in a member variable, so changing it stays allowed.
            if (Registry.CompilationRules.ChecksShapeAtRunTime && WrittenFieldHandle(stmt: stmt) is { } written &&
                IsHeldPlace(handle: written))
            {
                AddBracket(into: rewritten,
                    handle: written,
                    memberRoutine: RuntimeContract.ShapeUse.RequireFree,
                    isFailable: true);
            }

            if (handles.Count > 0 && SpillLeavingValue(stmt: current, handles: handles, into: rewritten))
            {
                continue;
            }

            AddBracketed(into: rewritten, handles: handles, stmt: current);
        }

        block.Statements.Clear();
        block.Statements.AddRange(collection: rewritten);
    }

    /// <summary>
    /// A block that reads a member variable and writes it back in several statements (see
    /// <see cref="BlockStatement.HoldsAccessLock"/>) takes the access lock of every entity it touches before its
    /// first statement and lets it go after its last, and also before every <c>return</c> or <c>throw</c> inside it
    /// (the pass-on of a failure). Its own statements keep their brackets, which the reentrant lock allows.
    /// </summary>
    private void LowerHeldBlock(BlockStatement block, List<Statement> into)
    {
        var handles = new List<Expression>();
        var seen = new HashSet<string>();
        AstWalker.Walk(root: block,
            visit: n =>
            {
                if (n is not Statement s)
                {
                    return;
                }

                foreach (Expression h in FieldAccessHandles(stmt: s))
                {
                    if (IsHeldPlace(handle: h) && !IsSelfLocking(handle: h) && seen.Add(item: PlaceKey(place: h)))
                    {
                        handles.Add(item: h);
                    }
                }
            });
        LowerBlock(block: block);
        if (handles.Count > 0)
        {
            ReleaseBeforeLeaving(block: block, handles: handles);
        }

        AddBracketed(into: into, handles: handles, stmt: block);
    }

    /// <summary>
    /// A routine of the program can take access locks of its own (it reads member variables of the entities it is
    /// given). Holding one lock while waiting for another is how two agents wait for each other forever: one holds
    /// the list and waits for the account, the other holds the account and waits for the list. So a member variable
    /// read in the arguments of a call to such a routine (<c>me.take(account: payer, note: f"to {payee.member}")</c>)
    /// is read first, into a temp inside its own bracket, and the call itself runs holding no lock for it. Standard
    /// library routines take no lock of the program's entities, so a call to one stays inside the bracket. The
    /// entity a statement writes keeps its lock across the statement, so a read that feeds the write stays in it.
    /// A read is not moved ahead of an earlier argument that calls a routine of the program, which could change it.
    /// Returns <paramref name="stmt"/> with the reads replaced, after adding the temps to <paramref name="into"/>.
    /// </summary>
    private Statement ReadFieldsBeforeCalls(Statement stmt, List<Statement> into)
    {
        string? written = WrittenFieldHandle(stmt: stmt) is { } w
            ? PlaceKey(place: w)
            : null;
        var reader = new FieldReadHoister(pass: this, keepPlace: written, into: into);
        return stmt switch
        {
            ExpressionStatement es => es with { Expression = reader.VisitExpression(expr: es.Expression) },
            DropStatement ds => ds with { Expression = reader.VisitExpression(expr: ds.Expression) },
            AssignmentStatement a => a with { Value = reader.VisitExpression(expr: a.Value) },
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } v } d =>
                d with { Declaration = v with { Initializer = reader.VisitExpression(expr: init) } },
            ReturnStatement { Value: { } rv } r => r with { Value = reader.VisitExpression(expr: rv) },
            VariantReturnStatement { Value: { } vv } vr => vr with { Value = reader.VisitExpression(expr: vv) },
            ThrowStatement t => t with { Error = reader.VisitExpression(expr: t.Error) },
            IfStatement i => i with { Condition = reader.VisitExpression(expr: i.Condition) },
            WhenStatement wh => wh with { Expression = reader.VisitExpression(expr: wh.Expression) },
            _ => stmt
        };
    }

    /// <summary>Whether a call to <paramref name="call"/>'s routine may run while an access lock is held: a routine
    /// of the standard library, or one the builder wrote with no source of its own.</summary>
    private bool RunsUnderLock(CallExpression call)
    {
        if (call.ResolvedRoutine is not { } routine)
        {
            return false;
        }

        RoutineInfo source = routine.RecoveryOf ?? routine.GenericDefinition ?? routine;
        string? file = source.Location?.FileName ?? routine.Location?.FileName;
        return file == null || Registry.IsStandardLibraryFile(filePath: file);
    }

    /// <summary>The held place of a member variable read through a Roamed handle that must leave the bracket of the
    /// statement (see <see cref="ReadFieldsBeforeCalls"/>), or null.</summary>
    private static Expression? MovableFieldRead(Expression e, string? keepPlace)
    {
        return e is MemberExpression member && RoamedFieldReceiver(member: member) is { } handle &&
               IsHeldPlace(handle: handle) && !IsSelfLocking(handle: handle) && PlaceKey(place: handle) != keepPlace
            ? handle
            : null;
    }

    /// <summary>Replaces the member variable reads in the arguments of calls to routines of the program with temps
    /// read just before the statement (see <see cref="ReadFieldsBeforeCalls"/>). Only eagerly evaluated parts are
    /// visited: the right side of <c>and</c>/<c>or</c>/<c>??</c>, the branches of a conditional, a <c>when</c>
    /// expression and a routine value stay as they are.</summary>
    private sealed class FieldReadHoister(RoamedLockBracketLoweringPass pass, string? keepPlace, List<Statement> into)
        : AstRewriter
    {
        protected override Expression VisitCall(CallExpression e)
        {
            var visited = (CallExpression)base.VisitCall(e: e);
            if (pass.RunsUnderLock(call: visited))
            {
                return visited;
            }

            Expression callee = visited.Callee;
            bool earlierCall = false;
            if (callee is MemberExpression { Object: var receiver } cm)
            {
                callee = cm with { Object = MoveReads(e: receiver) };
                earlierCall = ContainsProgramCall(e: receiver);
            }

            var args = new List<Expression>(capacity: visited.Arguments.Count);
            foreach (Expression arg in visited.Arguments)
            {
                args.Add(item: earlierCall ? arg : MoveReads(e: arg));
                earlierCall |= ContainsProgramCall(e: arg);
            }

            return visited with { Callee = callee, Arguments = args };
        }

        protected override Expression VisitBinary(BinaryExpression e)
        {
            if (e.Operator is BinaryOperator.And or BinaryOperator.Or or BinaryOperator.NoneCoalesce)
            {
                Expression left = VisitExpression(expr: e.Left);
                return ReferenceEquals(objA: left, objB: e.Left)
                    ? e
                    : e with { Left = left };
            }

            return base.VisitBinary(e: e);
        }

        protected override Expression VisitConditional(ConditionalExpression e)
        {
            Expression condition = VisitExpression(expr: e.Condition);
            return ReferenceEquals(objA: condition, objB: e.Condition)
                ? e
                : e with { Condition = condition };
        }

        protected override Expression VisitWhenExpression(WhenExpression e)
        {
            return e;
        }

        protected override Expression VisitClosureValue(ClosureValueExpression e)
        {
            return e;
        }

        /// <summary>Reads every movable member variable in <paramref name="e"/> into a temp first.</summary>
        private Expression MoveReads(Expression e)
        {
            return new ReadMover(pass: pass, keepPlace: keepPlace, into: into).VisitExpression(expr: e);
        }

        private bool ContainsProgramCall(Expression e)
        {
            bool found = false;
            AstWalker.WalkExpressions(root: e,
                visit: n => found |= n is CallExpression c && !pass.RunsUnderLock(call: c));
            return found;
        }
    }

    /// <summary>Puts each movable member variable read it meets into a bracketed temp (see
    /// <see cref="FieldReadHoister"/>), skipping parts that may not run.</summary>
    private sealed class ReadMover(RoamedLockBracketLoweringPass pass, string? keepPlace, List<Statement> into)
        : AstRewriter
    {
        protected override Expression VisitMember(MemberExpression e)
        {
            if (MovableFieldRead(e: e, keepPlace: keepPlace) is { } handle)
            {
                return pass.Spill(value: e, handles: [handle], into: into);
            }

            return base.VisitMember(e: e);
        }

        protected override Expression VisitBinary(BinaryExpression e)
        {
            if (e.Operator is BinaryOperator.And or BinaryOperator.Or or BinaryOperator.NoneCoalesce)
            {
                Expression left = VisitExpression(expr: e.Left);
                return ReferenceEquals(objA: left, objB: e.Left)
                    ? e
                    : e with { Left = left };
            }

            return base.VisitBinary(e: e);
        }

        protected override Expression VisitConditional(ConditionalExpression e)
        {
            Expression condition = VisitExpression(expr: e.Condition);
            return ReferenceEquals(objA: condition, objB: e.Condition)
                ? e
                : e with { Condition = condition };
        }

        protected override Expression VisitWhenExpression(WhenExpression e)
        {
            return e;
        }

        protected override Expression VisitClosureValue(ClosureValueExpression e)
        {
            return e;
        }
    }

    /// <summary>The spelling of a held place (<c>me.board</c>), which tells two reads of the same handle apart from
    /// reads of different ones.</summary>
    private static string PlaceKey(Expression place)
    {
        return place switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression member => PlaceKey(place: member.Object) + "." + member.MemberName,
            _ => ""
        };
    }

    /// <summary>Puts <c>lock_exit</c> of <paramref name="handles"/> before every <c>return</c> and <c>throw</c> inside
    /// <paramref name="block"/>, at any depth.</summary>
    private void ReleaseBeforeLeaving(BlockStatement block, List<Expression> handles)
    {
        var rewritten = new List<Statement>(capacity: block.Statements.Count);
        foreach (Statement stmt in block.Statements)
        {
            if (stmt is ReturnStatement or VariantReturnStatement or ThrowStatement)
            {
                foreach (Expression h in handles)
                {
                    AddBracket(into: rewritten, handle: h, memberRoutine: RuntimeContract.RoamedMemberRoutine.LockExit);
                }

                rewritten.Add(item: stmt);
                continue;
            }

            rewritten.Add(item: ReleaseIn(stmt: stmt, handles: handles));
        }

        block.Statements.Clear();
        block.Statements.AddRange(collection: rewritten);
    }

    /// <summary>The statement with the releases of <see cref="ReleaseBeforeLeaving"/> put into its branches. A branch
    /// that is a bare <c>return</c> becomes a block of the releases and the <c>return</c>.</summary>
    private Statement ReleaseIn(Statement stmt, List<Expression> handles)
    {
        switch (stmt)
        {
            case BlockStatement b:
                ReleaseBeforeLeaving(block: b, handles: handles);
                return b;
            case ReturnStatement or VariantReturnStatement or ThrowStatement:
            {
                var wrapped = new BlockStatement(Statements: new List<Statement> { stmt }, Location: stmt.Location);
                ReleaseBeforeLeaving(block: wrapped, handles: handles);
                return wrapped;
            }
            case IfStatement i:
                return i with
                {
                    ThenStatement = ReleaseIn(stmt: i.ThenStatement, handles: handles),
                    ElseStatement = i.ElseStatement == null
                        ? null
                        : ReleaseIn(stmt: i.ElseStatement, handles: handles)
                };
            case WhenStatement w:
                return w with
                {
                    Clauses = w.Clauses.Select(selector: c => c with { Body = ReleaseIn(stmt: c.Body, handles: handles) })
                                       .ToList()
                };
            case WhileStatement loop:
                return loop with
                {
                    Body = ReleaseIn(stmt: loop.Body, handles: handles)
                };
            case LoopStatement l:
                return l with { Body = ReleaseIn(stmt: l.Body, handles: handles) };
            case DangerStatement d:
                ReleaseBeforeLeaving(block: d.Body, handles: handles);
                return d;
            default:
                return stmt;
        }
    }

    private void AddBracketed(List<Statement> into, List<Expression> handles, Statement stmt)
    {
        foreach (Expression h in handles)
        {
            AddBracket(into: into,
                handle: h,
                memberRoutine: RuntimeContract.RoamedMemberRoutine.LockEnter);
        }

        into.Add(item: stmt);
        foreach (Expression h in handles)
        {
            AddBracket(into: into,
                handle: h,
                memberRoutine: RuntimeContract.RoamedMemberRoutine.LockExit);
        }
    }

    private int _spillCounter;

    /// <summary>
    /// A bracket around a whole statement only closes when the statement runs to its end. A <c>return</c> or
    /// <c>throw</c> never does, and an <c>if</c>, <c>when</c> or <c>while</c> runs its body inside the bracket,
    /// where a return, a wait or another lock would hold this one. So the value the statement reads through the
    /// handle (the returned value, the condition) is computed first into a temp inside its own bracket, and the
    /// statement itself works on the temp. A <c>while</c> reads its condition every round, so it becomes
    /// <c>while true</c> whose body computes the condition, runs the loop body when it holds, and otherwise runs
    /// the loop's <c>then</c> branch and breaks. Returns false when <paramref name="stmt"/> is not such a
    /// statement, or already reads a plain name.
    /// </summary>
    private bool SpillLeavingValue(Statement stmt, List<Expression> handles, List<Statement> into)
    {
        switch (stmt)
        {
            case ReturnStatement { Value: not null and not IdentifierExpression } r:
                into.Add(item: r with { Value = Spill(value: r.Value, handles: handles, into: into) });
                return true;
            case VariantReturnStatement { Value: not null and not IdentifierExpression } vr:
                into.Add(item: vr with { Value = Spill(value: vr.Value, handles: handles, into: into) });
                return true;
            case ThrowStatement { Error: not IdentifierExpression } t:
                into.Add(item: t with { Error = Spill(value: t.Error, handles: handles, into: into) });
                return true;
            case IfStatement { Condition: not IdentifierExpression } i:
                into.Add(item: i with { Condition = Spill(value: i.Condition, handles: handles, into: into) });
                return true;
            case WhenStatement { Expression: not IdentifierExpression } w:
                into.Add(item: w with { Expression = Spill(value: w.Expression, handles: handles, into: into) });
                return true;
            case WhileStatement { Condition: not IdentifierExpression } loop:
            {
                var body = new List<Statement>();
                Expression condition = Spill(value: loop.Condition, handles: handles, into: body);
                var stop = new List<Statement>();
                stop.Add(item: new BreakStatement(Location: loop.Location));
                body.Add(item: new IfStatement(Condition: condition,
                    ThenStatement: loop.Body,
                    ElseStatement: new BlockStatement(Statements: stop, Location: loop.Location),
                    Location: loop.Location));
                var forever = new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: loop.Location)
                {
                    ResolvedType = Registry.LookupType(name: "Bool")
                };
                into.Add(item: loop with
                {
                    Condition = forever,
                    Body = new BlockStatement(Statements: body, Location: loop.Location)
                });
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Computes <paramref name="value"/> into a fresh temp inside the lock bracket of <paramref name="handles"/>,
    /// appending the bracketed declaration to <paramref name="into"/>, and returns a read of the temp.</summary>
    private Expression Spill(Expression value, List<Expression> handles, List<Statement> into)
    {
        string tmp = $"__lk_{_spillCounter++}";
        var decl = new VariableDeclaration(Name: tmp,
            Type: null,
            Initializer: value,
            Visibility: VisibilityModifier.Secret,
            Location: value.Location);
        AddBracketed(into: into,
            handles: handles,
            stmt: new DeclarationStatement(Declaration: decl, Location: value.Location));
        return new IdentifierExpression(Name: tmp, Location: value.Location)
        {
            ResolvedType = value.ResolvedType
        };
    }

    /// <summary>
    /// Whether <paramref name="handle"/> names a handle someone already holds (a local, a parameter, <c>me</c>, or a
    /// member variable reached through them), which the brackets read again as they are. A handle a call has
    /// just produced (`jobs.gather()` returning a new list) is not shared with anyone yet, so it needs no lock,
    /// and naming it again in the brackets would run the call again.
    /// </summary>
    private static bool IsHeldPlace(Expression handle)
    {
        return handle switch
        {
            IdentifierExpression => true,
            MemberExpression member => IsHeldPlace(handle: member.Object),
            _ => false
        };
    }

    /// <summary>
    /// Whether <paramref name="handle"/> is over a standard-library entity marked <c>@self_locking</c> (a mailbox):
    /// its member variables are set once and lock inside, so calls on it take no access lock. Holding one across a
    /// call that waits (taking from an empty mailbox) would block the agent that adds.
    /// </summary>
    private static bool IsSelfLocking(Expression handle)
    {
        return RoamedInnerEntity(t: handle.ResolvedType) is { } entity &&
               ((entity.GenericDefinition ?? entity).Annotations
                ?.Contains(item: RuntimeContract.RoamedMemberRoutine.SelfLockingAnnotation) ?? false);
    }

    private void AddBracket(List<Statement> into, Expression handle, string memberRoutine, bool isFailable = false)
    {
        if (MakeLockCall(handle: handle, memberRoutine: memberRoutine, isFailable: isFailable) is { } call)
        {
            into.Add(item: call);
        }
    }

    /// <summary>The Roamed handle whose own member variable <paramref name="stmt"/> assigns (<c>me.count = n</c>,
    /// also written as the expression <c>(me.count = n)</c>), or null.</summary>
    private static Expression? WrittenFieldHandle(Statement stmt)
    {
        Expression? target = stmt switch
        {
            AssignmentStatement a => a.Target,
            ExpressionStatement { Expression: BinaryExpression { Operator: BinaryOperator.Assign, Left: var left } } =>
                left,
            _ => null
        };
        return target is MemberExpression member
            ? RoamedFieldReceiver(member: member)
            : null;
    }

    // ---- Lock-free atomic global RMW ----------------------------------------------------------------

    /// <summary>
    /// Recognizes an atomic-width scalar module-<c>global</c> read-modify-write, which
    /// <c>GlobalEntityRewritePass</c> has turned into a field update on the hidden <c>__ModuleGlobals</c>
    /// singleton (<c>__globals__.g = __globals__.g.add(d)</c> or <c>.sub(d)</c>), and returns the
    /// lock-free <see cref="AtomicRmwStatement"/> that replaces it, or null when the statement doesn't
    /// match. It matches only when the field is an atomic width (S8..S64, U8..U64, B32, B64) and the
    /// delta reads no other module-global field (an unlocked read of a heavy field could tear). Heavy
    /// fields (S128/S256/B16/B128/Text/Decimal/records) keep the locked path. Atomic arithmetic wraps on
    /// overflow instead of crashing like a checked add, as atomics do everywhere.
    /// </summary>
    private static AtomicRmwStatement? TryLowerAtomicGlobalRmw(Statement stmt)
    {
        (Expression Target, Expression Value)? tv = stmt switch
        {
            AssignmentStatement a => (a.Target, a.Value),
            ExpressionStatement
            {
                Expression: BinaryExpression { Operator: BinaryOperator.Assign } b
            } => (b.Left, b.Right),
            _ => null
        };
        if (tv is not { Target: MemberExpression tm } p)
        {
            return null;
        }

        if (ModuleGlobalsInnerEntity(t: tm.Object.ResolvedType) is null ||
            !IsAtomicWidthScalar(t: tm.ResolvedType))
        {
            return null;
        }

        Expression v = p.Value is NamedArgumentExpression nav
            ? nav.Value
            : p.Value;
        if (v is not CallExpression
            {
                Callee: MemberExpression { Object: MemberExpression vm, MemberName: var op },
                Arguments: [{ } deltaArg]
            })
        {
            return null;
        }

        AtomicRmwOperation? operation = op switch
        {
            "add" => AtomicRmwOperation.Add,
            "sub" => AtomicRmwOperation.Subtract,
            _ => null
        };
        if (operation is null || !IsSameSingletonField(tm: tm, vm: vm))
        {
            return null;
        }

        Expression delta = deltaArg is NamedArgumentExpression nad
            ? nad.Value
            : deltaArg;
        if (ReferencesModuleGlobalField(e: delta))
        {
            return null;
        }

        return new AtomicRmwStatement(Field: tm,
            Operation: operation.Value,
            Delta: delta,
            Location: stmt.Location);
    }

    /// <summary>True when both member expressions name the same field on the same <c>__globals__</c>
    /// singleton identifier.</summary>
    private static bool IsSameSingletonField(MemberExpression tm, MemberExpression vm)
    {
        return tm.MemberName == vm.MemberName && tm.Object is IdentifierExpression ti &&
               vm.Object is IdentifierExpression vi && ti.Name == vi.Name;
    }

    /// <summary>The <c>__ModuleGlobals</c> entity inside a <c>Roamed[__ModuleGlobals]</c> handle type,
    /// or null when the type is not that handle.</summary>
    private static EntityTypeSymbol? ModuleGlobalsInnerEntity(TypeSymbol? t)
    {
        EntityTypeSymbol? inner = t switch
        {
            RecordTypeSymbol
            {
                GenericDefinition.Name: RuntimeContract.Roamed,
                TypeArguments: [EntityTypeSymbol e]
            } => e,
            _ => null
        };
        return inner?.BareName == RuntimeContract.ModuleGlobals.EntityName
            ? inner
            : null;
    }

    private static bool IsAtomicWidthScalar(TypeSymbol? t)
    {
        return t?.BareName is "S8" or "S16" or "S32" or "S64" or "U8" or "U16" or "U32" or "U64" or "B32"
            or "B64";
    }

    private static bool ReferencesModuleGlobalField(Expression e)
    {
        bool found = false;
        AstWalker.WalkExpressions(root: e,
            visit: n =>
            {
                if (n is MemberExpression m &&
                    ModuleGlobalsInnerEntity(t: m.Object.ResolvedType) is not null)
                {
                    found = true;
                }
            });
        return found;
    }

    // ---- Field-access handles -----------------------------------------------------------------------

    // The Roamed[E] handle expressions of every DIRECT field access (read or write) in `stmt`'s own
    // expressions. One entry per field-access occurrence (per-statement bracketing, see class doc).
    // Nested statements are bracketed when the block recursion reaches them, so only this statement's
    // own expressions are walked here (matching RoamedSpawnPromotionLoweringPass.CollectPromotes).
    private static List<Expression> FieldAccessHandles(Statement stmt)
    {
        var handles = new List<Expression>();
        foreach (Expression e in DirectExpressions(stmt: stmt))
        {
            AstWalker.WalkExpressions(root: e,
                visit: n =>
                {
                    if (n is not MemberExpression member)
                    {
                        return;
                    }

                    // A direct field access through a Roamed handle, OR a memberRoutine-dispatch deref (the
                    // `control`/`refer`/`raw_inner` coercion RoamedProjectionLoweringPass inserts on a
                    // Roamed receiver). Both must hold the access lock across the enclosing statement —
                    // otherwise a memberRoutine call on a Roamed receiver (e.g. an SF wrapper's `xs.getitem!(i)`,
                    // whose bare-`me` inner is reached via the coercion) touches the object UNLOCKED,
                    // breaking escaped-mode serialization. Coarse (whole-statement) bracketing is exact for
                    // the reentrant, task-keyed lock.
                    if (RoamedFieldReceiver(member: member) is { } fieldHandle)
                    {
                        handles.Add(item: fieldHandle);
                    }
                    else if (RoamedCoercionReceiver(member: member) is { } coerceHandle)
                    {
                        handles.Add(item: coerceHandle);
                    }
                });
        }

        return handles;
    }

    // The expressions directly owned by a statement (its condition/value/target), excluding nested
    // Statement bodies — those are visited separately by the block recursion.
    private static IEnumerable<Expression> DirectExpressions(Statement stmt)
    {
        switch (stmt)
        {
            case ExpressionStatement es:
                yield return es.Expression;
                break;
            case DropStatement ds:
                yield return ds.Expression;
                break;
            case ReturnStatement { Value: not null } s: yield return s.Value; break;
            case VariantReturnStatement { Value: not null } s: yield return s.Value; break;
            case ThrowStatement s: yield return s.Error; break;
            case AssignmentStatement s:
                yield return s.Target;
                yield return s.Value;
                break;
            case DestructuringStatement s: yield return s.Initializer; break;
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: not null } v
            }:
                yield return v.Initializer; break;
            case IfStatement s: yield return s.Condition; break;
            case WhileStatement s: yield return s.Condition; break;
            case EachStatement s: yield return s.Iterable; break;
            case WhenStatement s: yield return s.Expression; break;
            case UsingStatement s: yield return s.Resource; break;
        }
    }

    // When `member` is a DIRECT field access through a Roamed[E] handle (the member name is a member
    // VARIABLE of the inner entity, not a memberRoutine — a memberRoutine call's callee member name never matches an
    // entity field), returns the Roamed[E] handle expression to bracket; otherwise null.
    private static Expression? RoamedFieldReceiver(MemberExpression member)
    {
        if (RoamedInnerEntity(t: member.Object.ResolvedType) is not { } innerEntity)
        {
            return null;
        }

        bool isField =
            innerEntity.MemberVariables.Any(predicate: mv => mv.Name == member.MemberName);
        return isField
            ? member.Object
            : null;
    }

    // When `member` is a deref COERCION (`control` / `refer` / `raw_inner`) on a Roamed[E] handle —
    // the receiver projection RoamedProjectionLoweringPass inserts for a memberRoutine call on a Roamed
    // receiver — returns the Roamed[E] handle to bracket; otherwise null. This is what makes a memberRoutine
    // call on a Roamed receiver hold the access lock (the field-access path above only catches direct
    // field touches). The lifecycle coercions on the handle itself (`roam`/`promote`/`lock_*`/`destruct`)
    // are NOT deref and are deliberately excluded.
    private static Expression? RoamedCoercionReceiver(MemberExpression member)
    {
        if (member.MemberName is not (RuntimeContract.RoamedMemberRoutine.RawInner
            or RuntimeContract.Control or RuntimeContract.Access))
        {
            return null;
        }

        return RoamedInnerEntity(t: member.Object.ResolvedType) is not null
            ? member.Object
            : null;
    }

    // The bare entity `E` inside a `Roamed[E]` handle. Null when the type is not a Roamed handle over an
    // entity.
    private static EntityTypeSymbol? RoamedInnerEntity(TypeSymbol? t)
    {
        return t switch
        {
            RecordTypeSymbol
            {
                GenericDefinition.Name: RuntimeContract.Roamed,
                TypeArguments: [EntityTypeSymbol e]
            } => e,
            _ => null
        };
    }

    // Build `handle.lock_enter()` / `handle.lock_exit()` as an ExpressionStatement. Both are dangerous,
    // take the Roamed handle, and return void — so the statement is a pure side effect around the field
    // access, exactly what the removed codegen bracket did. The handle node is reused (side-effect-free
    // to re-evaluate: an identifier / member handle), mirroring the promote/retain steps.
    private ExpressionStatement? MakeLockCall(Expression handle, string memberRoutine, bool isFailable = false)
    {
        if (handle.ResolvedType is not { } recvType)
        {
            return null;
        }

        RoutineInfo? routine =
            Registry.LookupMemberRoutine(type: recvType, memberRoutineName: memberRoutine, isFailable: isFailable);
        if (routine is null)
        {
            return null;
        }

        var callee =
            new MemberExpression(Object: handle,
                MemberName: memberRoutine,
                Location: handle.Location) { ResolvedType = recvType };
        var call =
            new CallExpression(Callee: callee,
                Arguments: new List<Expression>(),
                Location: handle.Location)
            {
                ResolvedRoutine = routine,
                ResolvedType = routine.ReturnType,
                LoweringKind =
                    Verification.CallClassifier.ClassifyMemberRoutineCall(memberRoutine: routine)
            };
        return new ExpressionStatement(Expression: call, Location: handle.Location);
    }

    // ---- Nested-block recursion (mirrors RoamedSpawnPromotionLoweringPass) ------------------------

    private void RecurseInto(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
                LowerBlock(block: b);
                break;
            case IfStatement i:
                RecurseStmt(stmt: i.ThenStatement);
                if (i.ElseStatement != null)
                {
                    RecurseStmt(stmt: i.ElseStatement);
                }

                break;
            case WhileStatement w:
                RecurseStmt(stmt: w.Body);
                break;
            case LoopStatement l:
                RecurseStmt(stmt: l.Body);
                break;
            case EachStatement f:
                RecurseStmt(stmt: f.Body);
                break;
            case DangerStatement d:
                LowerBlock(block: d.Body);
                break;
            case UsingStatement u:
                RecurseStmt(stmt: u.Body);
                if (u.FallbackBody != null)
                {
                    RecurseStmt(stmt: u.FallbackBody);
                }

                break;
            case WhenStatement whenStmt:
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    RecurseStmt(stmt: clause.Body);
                }

                break;
        }
    }

    private void RecurseStmt(Statement stmt)
    {
        if (stmt is BlockStatement b)
        {
            LowerBlock(block: b);
        }
        else
        {
            RecurseInto(stmt: stmt);
        }
    }
}
