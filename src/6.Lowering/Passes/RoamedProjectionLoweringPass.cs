using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Suflae <c>Roamed[E]</c> receiver-transparency lowering. A Suflae local promoted to
/// <c>Roamed[E]</c> (post-SA) reaches its inner value's memberRoutines through the wrapper handle:
/// operator-lowered calls (<c>x in d</c> -> <c>d.contains(x)</c>, <c>d[i]</c> -> <c>d.getitem(i)</c>,
/// <c>==</c>/<c>&lt;</c>) and f-string <c>represent</c>/<c>diagnose</c> arrive holding the
/// <c>RoamManager</c> handle.
///
/// <para>This pass makes the transparency a REAL AST rewrite instead of a codegen-inserted call:
/// for every <see cref="CallExpression"/> whose callee is a <see cref="MemberExpression"/> on a
/// <c>Roamed[E]</c> receiver, <see cref="RoamedTransparency.Project"/> is the single decision point.
/// When it applies it (a) re-resolves a wrapper-shadowed <c>represent</c>/<c>diagnose</c> to the
/// inner value's routine (stamped onto <see cref="CallExpression.ResolvedRoutine"/>), and (b) for a
/// bare-<c>me</c> inner memberRoutine, rewrites the receiver to <c>receiver.raw_inner()</c> — projecting the
/// controller handle to the real inner pointer. Codegen then emits the already-projected,
/// already-inner-resolved call verbatim.</para>
///
/// <para>Runs after <see cref="OperatorLoweringPass"/> and <see cref="FStringLoweringPass"/> so the
/// operator/f-string-lowered Roamed calls are visible. Idempotent: an already-projected receiver is
/// inner-typed (not Roamed) so <c>Project</c> returns null on a second visit.</para>
///
/// <para>All structural recursion (statements, other expressions) is supplied by
/// <see cref="AstRewriter"/>; this pass overrides only <see cref="VisitCall"/> to apply the Roamed
/// transparency projection after its children have been rewritten.</para>
/// </summary>
internal sealed class RoamedProjectionLoweringPass(PostprocessingContext ctx) : AstRewriter
{
    private TypeRegistry Registry => ctx.Registry;

    /// <summary>Lowers Roamed receiver projections across a whole program.</summary>
    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(registry: ctx.Registry, program: program, lower: r => VisitBody(name: r.Name, body: r.Body));
    }

    /// <summary>True while the body of `handle_eq` / `handle_ne` is visited: its own `==` on the two handles reaches
    /// the entities, it doesn't call itself.</summary>
    private bool _insideHandleEquality;

    private Statement VisitBody(string name, Statement body)
    {
        _insideHandleEquality = name is RuntimeContract.RoamedMemberRoutine.HandleEq
            or RuntimeContract.RoamedMemberRoutine.HandleNe;
        try
        {
            return VisitStatement(stmt: body);
        }
        finally
        {
            _insideHandleEquality = false;
        }
    }

    /// <summary>Lowers Roamed receiver projections in routines made concrete from generic ones: a library
    /// routine made concrete for a shared entity holds it as its handle (an iterator `iter` hands back, a
    /// `Maybe` value it displays), and reaches the entity through it like any other code.</summary>
    public void RunOnInstantiatedGenericBodies(Dictionary<string, Instantiation.MonomorphizedBody> bodies)
    {
        BodyDispatch.RunOnInstantiatedGenericBodies(registry: ctx.Registry, bodies: bodies,
            lower: (_, entry) => VisitBody(name: entry.Ast.Name, body: entry.Ast.Body));
    }

    /// <summary>Lowers Roamed receiver projections in synthesized variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        BodyDispatch.RunOnVariantBodies(registry: ctx.Registry, bodies: ctx.VariantBodies,
            lower: (_, body) => VisitStatement(stmt: body));
    }

    // ---- The core rewrite -----------------------------------------------------------------------

    /// <summary>
    /// The only node this pass rewrites: after the base rewrites the call's callee and arguments, apply
    /// the Roamed transparency projection when the callee is a member call on a Roamed[E] receiver.
    /// </summary>
    protected override Expression VisitCall(CallExpression e)
    {
        Expression lowered = base.VisitCall(e: e);
        return lowered is CallExpression call
            ? ProjectRoamedReceiver(call: call)
            : lowered;
    }

    // The core rewrite: when the callee is a member call on a Roamed[E] receiver and
    // RoamedTransparency.Project applies, stamp the inner memberRoutine as ResolvedRoutine and — for a
    // bare-`me` inner memberRoutine — rewrite the receiver to `receiver.raw_inner()`.
    private CallExpression ProjectRoamedReceiver(CallExpression call)
    {
        if (call.Callee is not MemberExpression member)
        {
            return call;
        }

        // Inside `handle_eq` the two entities are compared as `me.control() == you.control()`: the handles are put
        // back, so the comparison reaches the entities the way `==` on two handles does anywhere else (an entity
        // whose routines take its handle is handed the handle).
        if (_insideHandleEquality && member.MemberName == "eq" && call.Arguments.Count == 1 &&
            ControlledHandle(expression: member.Object) is { } receiverHandle &&
            ControlledHandle(expression: call.Arguments[index: 0] is NamedArgumentExpression controlledNamed
                ? controlledNamed.Value
                : call.Arguments[index: 0]) is { } argumentHandle)
        {
            member = member with { Object = receiverHandle };
            Expression handleArgument = call.Arguments[index: 0] is NamedArgumentExpression named
                ? named with { Value = argumentHandle, ResolvedType = argumentHandle.ResolvedType }
                : argumentHandle;
            call = call with { Callee = member, Arguments = [handleArgument] };
        }

        TypeSymbol? receiverType = member.Object.ResolvedType;
        // A common routine is called on the type (`T.from_serial(value:)` with `T` a handle's type): there is no
        // handle to reach into.
        if (receiverType is null || call.ResolvedRoutine is { Kind: TypeModel.Enums.RoutineKind.CommonRoutine })
        {
            return call;
        }

        // The initial memberRoutine: the SA/operator-stamped routine, or (when unresolved on a Roamed
        // receiver — an operator-lowered `d[i]`/`x in d` whose owner has no such memberRoutine) a transparent
        // lookup on the wrapper, matching the codegen path this pass replaces.
        RoutineInfo? memberRoutine = call.ResolvedRoutine ??
                                     Registry.LookupMemberRoutine(type: receiverType,
                                         memberRoutineName: member.MemberName);

        // `==` / `!=` on two handles: either may hold nothing (a Suflae `E?` member variable, read in the `eq` the
        // builder writes), so the call goes to the handle's own null-safe comparison instead of reaching into both.
        if (HandleEqualityCall(call: call, member: member, receiverType: receiverType) is { } handleCall)
        {
            return handleCall;
        }

        RoamedTransparency.Projection? proj = RoamedTransparency.Project(
            receiverType: receiverType,
            memberRoutine: memberRoutine,
            memberName: member.MemberName,
            registry: Registry);
        if (proj is not { } roamProj)
        {
            return call;
        }

        // Stamp the inner memberRoutine (represent/diagnose shadowed by the wrapper → inner's; a null-stamped
        // operator call → the transparently-resolved inner memberRoutine) so codegen emits it directly.
        call.ResolvedRoutine = roamProj.MemberRoutine;
        call.LoweringKind =
            Verification.CallClassifier.ClassifyMemberRoutineCall(
                memberRoutine: roamProj.MemberRoutine);
        if (!roamProj.ProjectToInner)
        {
            return call;
        }

        Expression innerRecv = MakeControlCall(receiver: member.Object,
            receiverType: receiverType,
            innerType: roamProj.InnerType);
        CallExpression projected = ReferenceEquals(objA: innerRecv, objB: member.Object)
            ? call
            : call with { Callee = member with { Object = innerRecv } };
        return ProjectRoamedArguments(call: projected, routine: roamProj.MemberRoutine);
    }

    // The handle `h` of `h.control()`, or null.
    private static Expression? ControlledHandle(Expression expression)
    {
        return expression is CallExpression
        {
            Callee: MemberExpression { MemberName: RuntimeContract.Control, Object: { ResolvedType: { } handleType } handle }
        } && TypeRegistry.GetRcWrapperBaseName(type: handleType) == RuntimeContract.Roamed
            ? handle
            : null;
    }

    // `a.eq(you: b)` / `a.ne(you: b)` with `a` and `b` both `Roamed[E]` handles: `a.handle_eq(you: b)` /
    // `a.handle_ne(you: b)`, or null for any other call.
    private CallExpression? HandleEqualityCall(CallExpression call, MemberExpression member, TypeSymbol receiverType)
    {
        if (_insideHandleEquality || member.MemberName is not ("eq" or "ne") || call.Arguments.Count != 1 ||
            TypeRegistry.GetRcWrapperBaseName(type: receiverType) != RuntimeContract.Roamed)
        {
            return null;
        }

        Expression argument = call.Arguments[index: 0] is NamedArgumentExpression named
            ? named.Value
            : call.Arguments[index: 0];
        // The operand of an `eq` taking a token is the address of the place holding the handle.
        if (argument is AddressOfExpression { Target: { ResolvedType: { } heldType } held } &&
            heldType.FullName == receiverType.FullName)
        {
            argument = held;
        }

        // An argument projected to its entity already (`you.next.control()`) is compared through its handle.
        if (argument is CallExpression
            {
                Callee: MemberExpression { MemberName: RuntimeContract.Control, Object: { ResolvedType: { } projectedType } handle }
            } && projectedType.FullName == receiverType.FullName)
        {
            argument = handle;
        }

        if (argument.ResolvedType is not { } argumentType || argumentType.FullName != receiverType.FullName)
        {
            return null;
        }

        string target = member.MemberName == "eq"
            ? RuntimeContract.RoamedMemberRoutine.HandleEq
            : RuntimeContract.RoamedMemberRoutine.HandleNe;
        if (Registry.LookupMemberRoutine(type: receiverType, memberRoutineName: target) is not { } routine)
        {
            return null;
        }

        var callee = member with { MemberName = target };
        return new CallExpression(Callee: callee,
            Arguments: [new NamedArgumentExpression(Name: "you", Value: argument, Location: argument.Location)
            {
                ResolvedType = argumentType
            }],
            Location: call.Location)
        {
            ResolvedRoutine = routine,
            ResolvedType = call.ResolvedType,
            LoweringKind = Verification.CallClassifier.ClassifyMemberRoutineCall(memberRoutine: routine)
        };
    }

    // The inner routine takes bare entities, so an argument holding a handle (`zs` in `ys == zs`, which
    // becomes `ys.eq(you: zs)` only now) is projected the same way as the receiver: to the entity itself
    // when the parameter is that entity or a `Viewing`/`Modifying` token over it.
    private CallExpression ProjectRoamedArguments(CallExpression call, RoutineInfo routine)
    {
        List<ParamInfo> parameters = routine.Parameters.Where(predicate: p => p.Name != "me").ToList();
        bool changed = false;
        var arguments = new List<Expression>(capacity: call.Arguments.Count);
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            Expression argument = call.Arguments[index: i];
            ParamInfo? parameter = argument is NamedArgumentExpression named
                ? parameters.FirstOrDefault(predicate: p => p.Name == named.Name)
                : i < parameters.Count
                    ? parameters[index: i]
                    : null;
            Expression value = argument is NamedArgumentExpression namedValue
                ? namedValue.Value
                : argument;
            // An operator's token operand (`ada == ada` hands `you` as the address of the place `ada` is in) is made
            // from the handle there: the entity's own pointer is its token, so the handle is projected to it.
            if (value is AddressOfExpression { Target: { ResolvedType: { } heldType } held } &&
                TypeRegistry.GetRcWrapperBaseName(type: heldType) == RuntimeContract.Roamed)
            {
                value = held;
            }

            if (BareEntityOf(type: parameter?.Type) is not { } entity || value.ResolvedType is not { } handleType ||
                TypeRegistry.GetRcWrapperBaseName(type: handleType) != RuntimeContract.Roamed)
            {
                arguments.Add(item: argument);
                continue;
            }

            Expression inner = MakeControlCall(receiver: value, receiverType: handleType, innerType: entity);
            arguments.Add(item: argument is NamedArgumentExpression wrapped
                ? wrapped with { Value = inner }
                : inner);
            changed |= !ReferenceEquals(objA: inner, objB: value);
        }

        return changed
            ? call with { Arguments = arguments }
            : call;
    }

    // The bare entity a parameter takes: its own type, or the entity a `Viewing`/`Modifying` token parameter
    // reaches (`you: Viewing[Me]` of an entity's `eq`). The call makes the token from the entity it is handed, so a
    // handle argument is projected to that entity first, else the token would be made from the handle.
    private static EntityTypeSymbol? BareEntityOf(TypeSymbol? type)
    {
        return type switch
        {
            EntityTypeSymbol entity => entity,
            RecordTypeSymbol { TypeArguments: [EntityTypeSymbol tokenEntity] } token
                when (token.GenericDefinition ?? token).BareName is RuntimeContract.Viewing
                    or RuntimeContract.Modifying => tokenEntity,
            _ => null
        };
    }

    // Build `receiver.control()` : the inner entity, via the Controlling marker-protocol deref (Roamed
    // obeys Controlling[T]). Stamps the resolved control routine and inner type so codegen emits it as a
    // real, already-resolved call. Reachability seeds control via ImplicitCallContract.ForLiveType, so
    // the target is live/monomorphized. The access lock is applied around the enclosing statement by
    // RoamedLockBracketLoweringPass (which recognizes this control() coercion), so the deref is safe.
    private Expression MakeControlCall(Expression receiver, TypeSymbol receiverType,
        TypeSymbol innerType)
    {
        RoutineInfo? control = Registry.LookupMemberRoutine(type: receiverType,
            memberRoutineName: RuntimeContract.Control);
        if (control is null)
        {
            return receiver;
        }

        var callee = new MemberExpression(Object: receiver,
            MemberName: RuntimeContract.Control,
            Location: receiver.Location) { ResolvedType = innerType };
        return new CallExpression(Callee: callee,
            Arguments: new List<Expression>(),
            Location: receiver.Location) { ResolvedRoutine = control, ResolvedType = innerType };
    }
}
