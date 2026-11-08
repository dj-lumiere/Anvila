using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Writes how links and retroactive entities are laid out, as calls of the routines Link.rvr declares, so the emitter
/// only translates them. A link of a retroactive type (<c>Modifying[Node]?</c> where links to <c>Node</c> are kept) is
/// 128 bits: the entity's address and the generation its slot had when the link was made.
/// <list type="bullet">
/// <item>An entity of a retroactive type gets its bytes from a slot of its type's store:
/// <c>C::rf_slot_take(store: LLVM::slot_store[T](), size: ...)</c> is the creator's box.</item>
/// <item>A link built from a token (an <c>Optional</c> creator) is <c>LLVM::link_make[T](present:, token:)</c>, which
/// reads the slot's generation.</item>
/// <item>Whether a link is present (<c>.present</c>) is <c>LLVM::link_live[T](link:)</c>: the address is not zero and
/// the slot's generation is still the link's.</item>
/// <item>The token a link holds (<c>.value</c>, a pattern's payload) is <c>LLVM::link_token[T](link:)</c>.</item>
/// <item>Writing a link's token (<c>x.value = t</c>) writes the whole link made from it.</item>
/// </list>
/// <para>Runs at Phase 9 over each body that reaches the emitter, after <see cref="CallBindingPass"/>, so every
/// construction is a creator and every type is concrete. The calls it writes are templates the emitter expands in
/// place, so none needs a body the collector would have to reach.</para>
/// </summary>
internal sealed class LinkLoweringPass : AstRewriter
{
    private readonly TypeRegistry _registry;
    private readonly TypeSymbol _u64;
    private readonly TypeSymbol _bool;
    private readonly TypeSymbol _addr;

    private LinkLoweringPass(TypeRegistry registry)
    {
        _registry = registry;
        _u64 = Lookup(name: "U64");
        _bool = Lookup(name: "Bool");
        _addr = Lookup(name: "Addr");
    }

    /// <summary>Lowers the links and the retroactive entities of one routine body in place.</summary>
    public static void Run(Statement body, TypeRegistry registry)
    {
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(message: $"A routine body must be a block, got {body.GetType().Name}.");
        }

        // Nothing to do in a program that keeps no link: no type is retroactive.
        if (!registry.HasRetroactiveTypes)
        {
            return;
        }

        var pass = new LinkLoweringPass(registry: registry);
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    private TypeSymbol Lookup(string name)
    {
        return _registry.LookupType(name: name) ??
               throw new InvalidOperationException(message: $"System.{name} is not registered.");
    }

    /// <summary>The entity a link of <paramref name="type"/> names, when it is a link of a retroactive type.</summary>
    private static EntityTypeSymbol? LinkedEntity(TypeSymbol? type)
    {
        return type is RecordTypeSymbol { IsLinkOptional: true, TypeArguments: [RecordTypeSymbol { TypeArguments: [EntityTypeSymbol entity] }] }
            ? entity
            : null;
    }

    /// <inheritdoc/>
    protected override Expression VisitCreator(CreatorExpression e)
    {
        Expression visited = base.VisitCreator(e: e);
        if (visited is not CreatorExpression creator)
        {
            return visited;
        }

        TypeSymbol? made = creator.ConstructedType ?? creator.ResolvedType;
        if (made is EntityTypeSymbol entity && entity.ReadRetroactivity() && creator.Box == null)
        {
            return creator with { Box = TakeSlot(entity: entity, location: creator.Location) };
        }

        if (LinkedEntity(type: made) is not { } linked)
        {
            return creator;
        }

        Expression? present = null;
        Expression? token = null;
        foreach ((string name, Expression value) in creator.MemberVariables)
        {
            if (name == RuntimeContract.Carrier.PresentField)
            {
                present = value;
            }
            else if (name == RuntimeContract.Carrier.ValueField)
            {
                token = value;
            }
        }

        // A link that is never present is all zero, which the emitter writes as the optional's none.
        if (token == null || present is null or LiteralExpression { Value: false })
        {
            return creator;
        }

        // A link written around a link (a forwarder handing on its routine's link) is that link.
        if (present is LiteralExpression { Value: true } &&
            (LinkedEntity(type: token.ResolvedType) != null ||
             token is CallExpression { ResolvedRoutine.ReturnType: { } returned } && LinkedEntity(type: returned) != null))
        {
            // The call gives the link itself, though its value was typed as the token in it.
            if (token is CallExpression { ResolvedRoutine.ReturnType: { } linkType } call)
            {
                call.ResolvedType = linkType;
            }

            return token;
        }

        // A token on a link (`at` of a `for` over a list of links) given where a link is kept is read as that link
        // before demand collection (LinkConversionPass). Made into a new link here, it would read the link's own bytes
        // as an entity.
        if (Verification.LinkTaking.ReadTokenOnLink(value: token, registry: _registry) != null)
        {
            throw new InvalidOperationException(
                message: $"A link is made from a token on a link at {creator.Location}: LinkConversionPass did not read " +
                         "the link it reaches.");
        }

        return Call(name: RuntimeContract.Link.Make,
            entity: linked,
            arguments: [("present", present), ("token", token)],
            type: made!,
            location: creator.Location);
    }

    /// <inheritdoc/>
    public override Expression VisitExpression(Expression expr)
    {
        if (expr is EntityAllocationExpression { ResolvedType: EntityTypeSymbol entity, Box: null } allocation &&
            entity.ReadRetroactivity())
        {
            return allocation with { Box = TakeSlot(entity: entity, location: allocation.Location) };
        }

        return base.VisitExpression(expr: expr);
    }

    /// <inheritdoc/>
    protected override Expression VisitMember(MemberExpression e)
    {
        Expression visited = base.VisitMember(e: e);
        if (visited is not MemberExpression member || LinkedEntity(type: member.Object.ResolvedType) is not { } linked)
        {
            return visited;
        }

        return member.MemberName switch
        {
            RuntimeContract.Carrier.PresentField => Call(name: RuntimeContract.Link.Live,
                entity: linked,
                arguments: [("link", member.Object)],
                type: _bool,
                location: member.Location),
            RuntimeContract.Carrier.ValueField => Call(name: RuntimeContract.Link.Token,
                entity: linked,
                arguments: [("link", member.Object)],
                type: member.ResolvedType ?? member.Object.ResolvedType!.TypeArguments![index: 0],
                location: member.Location),
            _ => member
        };
    }

    /// <inheritdoc/>
    protected override Expression VisitCarrierPayload(CarrierPayloadExpression e)
    {
        Expression visited = base.VisitCarrierPayload(e: e);
        if (visited is not CarrierPayloadExpression payload || LinkedEntity(type: payload.Carrier.ResolvedType) is not { } linked)
        {
            return visited;
        }

        return Call(name: RuntimeContract.Link.Token,
            entity: linked,
            arguments: [("link", payload.Carrier)],
            type: payload.ResolvedType ?? payload.Carrier.ResolvedType!.TypeArguments![index: 0],
            location: payload.Location);
    }

    /// <inheritdoc/>
    protected override Statement VisitAssignment(AssignmentStatement s)
    {
        // `x.present = ...` keeps the target as written: the emitter stores the none link or leaves the link.
        if (s.Target is MemberExpression { MemberName: RuntimeContract.Carrier.PresentField } presence &&
            LinkedEntity(type: presence.Object.ResolvedType) != null)
        {
            Expression flag = VisitExpression(expr: s.Value);
            Expression holder = VisitLinkHolder(expr: presence.Object);
            return s with { Target = presence with { Object = holder }, Value = flag };
        }

        // `x.value = t` writes the whole link made from `t`.
        if (s.Target is MemberExpression { MemberName: RuntimeContract.Carrier.ValueField } held &&
            LinkedEntity(type: held.Object.ResolvedType) is { } linked)
        {
            Expression holder = VisitLinkHolder(expr: held.Object);
            Expression link = Call(name: RuntimeContract.Link.Make,
                entity: linked,
                arguments: [("present", True(location: s.Location)), ("token", VisitExpression(expr: s.Value))],
                type: holder.ResolvedType!,
                location: s.Location);
            return s with { Target = holder, Value = link };
        }

        return base.VisitAssignment(s: s);
    }

    /// <summary>The place a link is kept in, as an assignment target: its own parts lowered, the place itself left.</summary>
    private Expression VisitLinkHolder(Expression expr)
    {
        return expr switch
        {
            MemberExpression member => member with { Object = VisitExpression(expr: member.Object) },
            IndexExpression index => index with
            {
                Object = VisitExpression(expr: index.Object), Index = VisitExpression(expr: index.Index)
            },
            _ => expr
        };
    }

    /// <summary>The box of a new entity of a retroactive type: a slot of its type's store.</summary>
    private CallExpression TakeSlot(EntityTypeSymbol entity, SourceLocation location)
    {
        CallExpression store = Call(name: RuntimeContract.Link.SlotStore,
            entity: entity,
            arguments: [],
            type: _addr,
            location: location);
        RoutineInfo take = _registry.LookupRoutine(fullName: $"C::{RuntimeContract.Link.SlotTake}") ??
                           _registry.LookupRoutine(fullName: $"C::System.{RuntimeContract.Link.SlotTake}") ??
                           throw new InvalidOperationException(
                               message: $"C::{RuntimeContract.Link.SlotTake} is not registered.");
        var size = new LiteralExpression(
            Value: entity.HeapBlockSize(pointerSize: 8)
                         .ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
            LiteralType: TokenType.U64Literal,
            Location: location) { ResolvedType = _u64 };
        return Direct(routine: take, arguments: [("store", store), ("size", size)], type: _addr, location: location);
    }

    /// <summary>A call of the layout routine <paramref name="name"/> made for <paramref name="entity"/>.</summary>
    private CallExpression Call(string name, EntityTypeSymbol entity, List<(string Name, Expression Value)> arguments,
        TypeSymbol type, SourceLocation location)
    {
        RoutineInfo definition =
            _registry.LookupGenericOverload(name: name, includeForeign: true, module: "System") ??
            throw new InvalidOperationException(message: $"LLVM::{name} is not registered.");
        RoutineInfo instance = _registry.GetOrCreateRoutineResolution(genericDef: definition, typeArguments: [entity]);
        return Direct(routine: instance, arguments: arguments, type: type, location: location);
    }

    private static CallExpression Direct(RoutineInfo routine, List<(string Name, Expression Value)> arguments,
        TypeSymbol type, SourceLocation location)
    {
        return new CallExpression(
            Callee: new IdentifierExpression(Name: routine.Name, Location: location) { ResolvedRoutine = routine },
            Arguments: arguments
                      .Select(selector: a => (Expression)new NamedArgumentExpression(Name: a.Name,
                           Value: a.Value,
                           Location: location) { ResolvedType = a.Value.ResolvedType })
                      .ToList(),
            Location: location)
        {
            ResolvedRoutine = routine,
            ResolvedType = type,
            LoweringKind = CallLoweringKind.DirectRoutine,
            IsSynthesizedLowering = true
        };
    }

    private LiteralExpression True(SourceLocation location)
    {
        return new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location) { ResolvedType = _bool };
    }
}
