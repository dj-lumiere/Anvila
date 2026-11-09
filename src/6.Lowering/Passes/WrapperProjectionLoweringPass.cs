using Builder.Declaration;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Makes the value behind a borrow or sharing wrapper explicit, so the emitter never decides how to reach it.
///
/// <para>A field read or write through a wrapper (<c>w.count</c> where <c>w</c> is a <c>Retained[Node]</c>,
/// <c>Viewing[Node]</c>, <c>Roamed[Node]</c>, …) acts on the inner entity. This pass wraps the object of such an
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
        Expression visited = VisitCallParts(e: e);
        return visited is CallExpression call ? ReferenceAsEntity(call: call) : visited;
    }

    /// <summary>
    /// A call that hands out a reference to an entity kept elsewhere but stands for the entity (<c>me[i]</c> on a list of
    /// entities is <c>me.modify_at(i)</c> typed as the element, OperatorLoweringPass), or is called on such a reference
    /// with a routine of the entity's own (a reference to a container indexed in place): the reference is 128 bits and
    /// the entity an address, so the address it holds is taken where the entity is meant.
    /// </summary>
    private Expression ReferenceAsEntity(CallExpression call)
    {
        if (call.Callee is MemberExpression { Object: { ResolvedType: { } held } receiver } callee &&
            IsReference(type: held) && held.TypeArguments![index: 0] is EntityTypeSymbol referenced &&
            call.ResolvedRoutine is { OwnerType: EntityTypeSymbol owner, IsCreator: false } &&
            (owner.FullName == referenced.FullName || owner.Name == referenced.Name ||
             referenced.GenericDefinition is { } definition && ReferenceEquals(objA: definition, objB: owner)))
        {
            call = call with
            {
                Callee = callee with
                {
                    Object = new BackendCastExpression(Value: receiver, Location: receiver.Location) { ResolvedType = referenced }
                }
            };
        }

        // A reference handed to a parameter of the entity's own type (an element reached through a reference, handed to
        // a generic routine bound to the entity) is the address it holds.
        List<TypeSymbol?>? parameters = call.Callee.ResolvedType is RoutineTypeSymbol routineValue
            ? [.. routineValue.ParameterTypes]
            : call.ResolvedRoutine?.Parameters.Where(predicate: p => p.Name != "me").Select(selector: p => p.Type).ToList();
        if (parameters != null)
        {
            List<Expression>? arguments = null;
            for (int slot = 0; slot < call.Arguments.Count && slot < parameters.Count; slot++)
            {
                Expression argument = call.Arguments[index: slot];
                Expression value = argument is NamedArgumentExpression named ? named.Value : argument;
                if (parameters[index: slot] is not EntityTypeSymbol wanted || !IsReference(type: value.ResolvedType) ||
                    value.ResolvedType!.TypeArguments![index: 0] is not EntityTypeSymbol given ||
                    given.FullName != wanted.FullName)
                {
                    continue;
                }

                var address = new BackendCastExpression(Value: value, Location: value.Location) { ResolvedType = given };
                arguments ??= [.. call.Arguments];
                arguments[index: slot] = argument is NamedArgumentExpression wrapped
                    ? wrapped with { Value = address, ResolvedType = given }
                    : address;
            }

            if (arguments != null)
            {
                call = call with { Arguments = arguments };
            }
        }

        if (call.ResolvedType is EntityTypeSymbol entity &&
            call.ResolvedRoutine?.ReturnType?.BareName is RuntimeContract.Viewing or RuntimeContract.Modifying &&
            call.ResolvedRoutine.ReturnType.TypeArguments is [_])
        {
            TypeSymbol reference = registry.GetOrCreateWrapperType(wrapperName: call.ResolvedRoutine.ReturnType.BareName,
                innerType: entity,
                isReadOnly: call.ResolvedRoutine.ReturnType.BareName == RuntimeContract.Viewing);
            return new BackendCastExpression(Value: call with { ResolvedType = reference }, Location: call.Location)
            {
                ResolvedType = entity
            };
        }

        return call;
    }

    private Expression VisitCallParts(CallExpression e)
    {
        if (e.Callee is not MemberExpression callee)
        {
            Expression rewritten = base.VisitCall(e: e);
            return rewritten is CallExpression { IsRecipeRunCall: true } direct
                ? ProjectByReferenceArguments(call: direct)
                : rewritten;
        }

        // A call through a routine-typed member variable (`h.make()` with `h` a `Modifying[Holder]`) reads that
        // member variable, so it is projected like any other read.
        if (e.LoweringKind == CallLoweringKind.DynamicCall)
        {
            Expression field = VisitMember(e: callee);
            List<Expression> dynamicArgs = RewriteList(items: e.Arguments, rewrite: VisitExpression);
            return ReferenceEquals(objA: field, objB: callee) && ReferenceEquals(objA: dynamicArgs, objB: e.Arguments)
                ? e
                : e with { Callee = field, Arguments = dynamicArgs };
        }

        // The callee is a routine, not a field: only its receiver is rewritten.
        Expression receiver = VisitExpression(expr: callee.Object);

        List<Expression> args = RewriteList(items: e.Arguments, rewrite: VisitExpression);
        if (ReferenceEquals(objA: receiver, objB: callee.Object) &&
            ReferenceEquals(objA: args, objB: e.Arguments))
        {
            return e;
        }

        return e with { Callee = callee with { Object = receiver }, Arguments = args };
    }

    /// <summary>
    /// In the routine that runs a Work recipe, a by-reference argument arrives as
    /// <c>pointer.peek()</c> on the <c>Hijacked</c> the recipe kept (WorkRecipe). The routine
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

    /// <inheritdoc/>
    protected override Expression VisitWrapperProjection(WrapperProjectionExpression e)
    {
        Expression visited = base.VisitWrapperProjection(e: e);
        return visited is WrapperProjectionExpression { Kind: WrapperProjectionKind.RecordAddress } projection &&
               IsReference(type: projection.Wrapper.ResolvedType)
            ? projection with { Wrapper = PlaceAddress(reference: projection.Wrapper) }
            : visited;
    }

    /// <summary>Whether <paramref name="type"/> is a reference (<c>Viewing[T]</c>, <c>Modifying[T]</c>): 128 bits, the
    /// address of the place and the generation of an entity's slot.</summary>
    private static bool IsReference(TypeSymbol? type)
    {
        return type is RecordTypeSymbol { BareName: RuntimeContract.Viewing or RuntimeContract.Modifying, TypeArguments: [_] };
    }

    /// <summary>The address a reference holds, its low half, as an <c>Addr</c>.</summary>
    private BackendCastExpression PlaceAddress(Expression reference)
    {
        return new BackendCastExpression(Value: reference, Location: reference.Location)
        {
            ResolvedType = registry.LookupType(name: "Addr") ??
                           throw new InvalidOperationException(message: "System.Addr is not registered.")
        };
    }

    /// <summary>
    /// The projection of <paramref name="wrapper"/> to its inner entity, or null when it is not a concrete
    /// wrapper of an entity, or <paramref name="ownMemberName"/> names a member variable of the wrapper itself. A
    /// reference to an entity is projected as the address it holds.
    /// </summary>
    private Expression? Project(Expression wrapper, string? ownMemberName)
    {
        TypeSymbol? type = wrapper.ResolvedType;

        if (type == null || ContainsGenericParameter(type: type))
        {
            return null;
        }

        if (IsReference(type: type) && type.TypeArguments![index: 0] is EntityTypeSymbol referenced)
        {
            return new BackendCastExpression(Value: wrapper, Location: wrapper.Location) { ResolvedType = referenced };
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

        return new WrapperProjectionExpression(Wrapper: p.Kind == WrapperProjectionKind.RecordAddress && IsReference(type: type)
                ? PlaceAddress(reference: wrapper)
                : wrapper,
            Kind: p.Kind,
            Controller: p.Kind == WrapperProjectionKind.ControllerData ? controller : null,
            FieldIndex: p.FieldIndex,
            Location: wrapper.Location) { ResolvedType = p.Inner };
    }

    /// <summary>
    /// The field of a struct wrapper that holds the inner entity as a <c>Hijacked[T]</c>
    /// Defaults to 0.
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
