using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Makes the value behind a borrow or sharing wrapper explicit, so the emitter never decides how to reach it.
///
/// <para>A field read or write through a wrapper (<c>w.count</c> where <c>w</c> is a <c>Retained[Node]</c>,
/// <c>Viewing[Node]</c>, <c>Roamed[Node]</c>, …) and a call of a forwarded entity routine on a
/// <c>Consulting</c>/<c>Amending</c> token both act on the inner entity. This pass wraps the object of such an
/// access in a <see cref="WrapperProjectionExpression"/> typed as the entity, stamped with how the entity
/// pointer comes out of the wrapper: the controller's <c>data</c> field, the wrapper pointer itself, or the
/// struct field holding a <c>Hijacked[T]</c>. The emitter then translates an ordinary entity access.</para>
///
/// <para>A field of a record behind a pointer wrapper (<c>m.x</c> where <c>m</c> is a <c>Modifying[Point]</c>)
/// is projected the same way, typed as the record and stamped as a record address: the wrapper pointer is the
/// record's storage, so the field is read and written in place, through any chain of nested record fields.</para>
///
/// <para>Runs at Phase 9 over each body that reaches the emitter, after monomorphization. Only concrete
/// wrapper types are projected, so a generic template body stays untouched and its clones are projected
/// once they are concrete. The rewrite is idempotent: a projected object is already entity-typed.</para>
/// </summary>
internal sealed class WrapperProjectionLoweringPass(TypeRegistry registry) : AstRewriter
{
    /// <summary>Projects the wrapper accesses of one routine body in place.</summary>
    public static void Run(Statement body, TypeRegistry registry)
    {
        // A routine body is a block: its statement list is rewritten in place (the body itself is
        // referenced from several places and cannot be replaced).
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(
                message: $"A routine body must be a block, got {body.GetType().Name}.");
        }

        var pass = new WrapperProjectionLoweringPass(registry: registry);
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    /// <inheritdoc/>
    protected override Expression VisitMember(MemberExpression e)
    {
        Expression obj = VisitExpression(expr: e.Object);
        if (Project(wrapper: obj, ownMemberName: e.MemberName) is { } projected)
        {
            obj = projected;
        }

        return ReferenceEquals(objA: obj, objB: e.Object)
            ? e
            : e with { Object = obj };
    }

    /// <inheritdoc/>
    protected override Expression VisitCall(CallExpression e)
    {
        if (e.Callee is not MemberExpression callee)
        {
            Expression rewritten = base.VisitCall(e: e);
            return rewritten is CallExpression { IsDirectAsyncInvoke: true } direct
                ? ProjectByReferenceArguments(call: direct)
                : rewritten;
        }

        // The callee is a routine, not a field: only its receiver is rewritten. A Consulting/Amending
        // token points at the shared GuardController, so a forwarded routine owned by the guarded entity
        // gets the entity as its receiver. The token's own routines (enter/exit/refer/control/…) keep it.
        Expression receiver = VisitExpression(expr: callee.Object);
        if (e.ResolvedRoutine is { OwnerType: { } owner } &&
            receiver.ResolvedType is RecordTypeSymbol
            {
                GenericDefinition.BareName: RuntimeContract.Consulting or RuntimeContract.Amending,
                TypeArguments: [EntityTypeSymbol guarded, _, ..]
            } &&
            guarded.FullName == owner.FullName &&
            Project(wrapper: receiver, ownMemberName: null) is { } projected)
        {
            receiver = projected;
        }

        List<Expression> args = RewriteList(items: e.Arguments, rewrite: VisitExpression);
        if (ReferenceEquals(objA: receiver, objB: callee.Object) &&
            ReferenceEquals(objA: args, objB: e.Arguments))
        {
            return e;
        }

        return e with { Callee = callee with { Object = receiver }, Arguments = args };
    }

    /// <summary>
    /// In the routine that runs an Agent recipe, a by-reference argument arrives as
    /// <c>pointer.peek()</c> on the <c>Hijacked</c> the recipe kept (AsyncRecipeSynthesisPass). The routine
    /// works on the caller's storage, so the argument becomes the pointee itself: a record-address
    /// projection whose address is the pointer, instead of a loaded copy.
    /// </summary>
    private static CallExpression ProjectByReferenceArguments(CallExpression call)
    {
        if (call.ResolvedRoutine is not { } routine || !routine.Parameters.Any(predicate: p => p.IsByReference))
        {
            return call;
        }

        List<Expression> arguments = RewriteList(items: call.Arguments,
            rewrite: argument =>
            {
                if (argument is not NamedArgumentExpression
                    {
                        Value: CallExpression
                        {
                            Callee: MemberExpression { MemberName: "peek", Object: { } pointer }
                        }
                    } named ||
                    routine.Parameters.FirstOrDefault(predicate: p => p.Name == named.Name) is not
                        { IsByReference: true, Type: RecordTypeSymbol pointee } ||
                    pointer.ResolvedType is not RecordTypeSymbol
                    {
                        GenericDefinition.BareName: RuntimeContract.Hijacked
                    })
                {
                    return argument;
                }

                return named with
                {
                    Value = new WrapperProjectionExpression(Wrapper: pointer,
                        Kind: WrapperProjectionKind.RecordAddress,
                        Controller: null,
                        FieldIndex: 0,
                        Location: named.Value.Location) { ResolvedType = pointee }
                };
            });
        return ReferenceEquals(objA: arguments, objB: call.Arguments)
            ? call
            : call with { Arguments = arguments };
    }

    /// <summary>
    /// The projection of <paramref name="wrapper"/> to its inner entity, or null when it is not a concrete
    /// wrapper of an entity, or <paramref name="ownMemberName"/> names a member variable of the wrapper itself.
    /// </summary>
    private WrapperProjectionExpression? Project(Expression wrapper, string? ownMemberName)
    {
        TypeSymbol? type = wrapper.ResolvedType;

        // A marker borrow protocol receiver (Accessing[X]/Controlling[X]) is transparent to its inner X.
        if (type is ProtocolTypeSymbol { TypeArguments: [{ } markerInner] } proto &&
            RuntimeContract.IsMarkerProtocol(baseName: (proto.GenericDefinition ?? proto).BareName))
        {
            type = markerInner;
        }

        if (type == null || ContainsGenericParameter(type: type))
        {
            return null;
        }

        EntityTypeSymbol? controller = registry.GetControllerType(wrapper: type);
        WrapperProjectionKind pointerKind = controller != null
            ? WrapperProjectionKind.ControllerData
            : WrapperProjectionKind.Direct;
        (WrapperProjectionKind Kind, int FieldIndex, TypeSymbol Inner)? projection = type switch
        {
            // A pointer wrapper of a record: only a field of the record is reached in place.
            RecordTypeSymbol
            {
                GenericDefinition: { } definition, BackendType: not null,
                TypeArguments: [RecordTypeSymbol innerRecord, ..]
            } record when RuntimeContract.WrapperTypes.Contains(item: definition.BareName) &&
                          !record.MemberVariables.Any(predicate: mv => mv.Name == ownMemberName) &&
                          innerRecord.MemberVariables.Any(predicate: mv => mv.Name == ownMemberName) =>
                (WrapperProjectionKind.RecordAddress, 0, innerRecord),
            RecordTypeSymbol
            {
                GenericDefinition: { } definition, TypeArguments: [EntityTypeSymbol inner, ..]
            } record when RuntimeContract.WrapperTypes.Contains(item: definition.BareName) &&
                          !record.MemberVariables.Any(predicate: mv => mv.Name == ownMemberName) =>
                record.BackendType == null
                    ? (WrapperProjectionKind.StructField, HijackedFieldIndex(wrapper: record, inner: inner), inner)
                    : (pointerKind, 0, inner),
            _ => null
        };
        if (projection is not { } p)
        {
            return null;
        }

        return new WrapperProjectionExpression(Wrapper: wrapper,
            Kind: p.Kind,
            Controller: p.Kind == WrapperProjectionKind.ControllerData ? controller : null,
            FieldIndex: p.FieldIndex,
            Location: wrapper.Location) { ResolvedType = p.Inner };
    }

    /// <summary>
    /// The field of a struct wrapper that holds the inner entity as a <c>Hijacked[T]</c>
    /// (e.g. Consulting[T] has ptr=0). Defaults to 0.
    /// </summary>
    private static int HijackedFieldIndex(RecordTypeSymbol wrapper, EntityTypeSymbol inner)
    {
        for (int i = 0; i < wrapper.MemberVariables.Count; i++)
        {
            if (WrapperShape.TryGet(type: wrapper.MemberVariables[index: i].Type, name: out string fieldWrapper,
                    inner: out TypeSymbol fieldInner) &&
                fieldWrapper == RuntimeContract.Hijacked && fieldInner is EntityTypeSymbol &&
                fieldInner.FullName == inner.FullName)
            {
                return i;
            }
        }

        return 0;
    }

    private static bool ContainsGenericParameter(TypeSymbol type)
    {
        return type is GenericParameterTypeSymbol or ProtocolSelfTypeSymbol or BuildtimeConstGenericTypeSymbol ||
               (type.TypeArguments?.Any(predicate: ContainsGenericParameter) ?? false);
    }
}
