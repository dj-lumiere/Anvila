using Builder.Declaration;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Settles what every call binds to, so a backend emits the call it is given:
/// <list type="bullet">
/// <item>A member call the builder wrote without a lowering kind is a direct member routine call.</item>
/// <item>A member call still bound to a routine of a generic or universal owner (<c>T.destroy</c> written in a
/// derive body) is bound to the receiver's own routine.</item>
/// <item>A member call on a type name (<c>B64.from(...)</c>) gets the type itself as its receiver, a
/// <see cref="TypeExpression"/>, rather than an identifier a backend would have to tell apart from a value.</item>
/// <item><c>hollow[T]()</c> is a fresh zero-filled entity (<see cref="EntityAllocationExpression"/>).</item>
/// <item>A routine handed to native code (a <c>CPtr</c> parameter, or a routine parameter of a foreign routine) is
/// its native code address (<see cref="NativeRoutineExpression"/>), or for a routine value the checked native
/// callback (<see cref="NativeCallbackExpression"/>).</item>
/// <item>A member call on a const-generic value (<c>N.represent()</c>) gets the number as its receiver.</item>
/// <item>The tag of a variant (<c>v.type_id</c>, written by pattern lowering) is a <see cref="TagOfExpression"/>.</item>
/// </list>
/// Runs at Phase 9 over every body that reaches a backend, after <see cref="CallArgumentOrderPass"/> (arguments
/// line up with parameters) and after every pass that writes calls of its own.
/// </summary>
internal sealed class CallBindingPass : AstRewriter
{
    private readonly TypeRegistry _registry;
    private readonly HashSet<string> _values;
    private readonly string? _module;

    private CallBindingPass(TypeRegistry registry, HashSet<string> values, string? module)
    {
        _registry = registry;
        _values = values;
        _module = module;
    }

    /// <summary>Binds the calls of one routine body in place.</summary>
    public static void Run(Statement body, RoutineInfo? routine, TypeRegistry registry)
    {
        // A routine body is a block: its statement list is rewritten in place (the body itself is
        // referenced from several places and cannot be replaced).
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(
                message: $"A routine body must be a block, got {body.GetType().Name}.");
        }

        // The names that are values here: `me`, the parameters and every local.
        var values = new HashSet<string>(comparer: StringComparer.Ordinal) { "me" };
        foreach (ParamInfo parameter in routine?.Parameters ?? [])
        {
            values.Add(item: parameter.Name);
        }

        AstWalker.Walk(root: body,
            visit: node =>
            {
                if (node is VariableDeclaration local)
                {
                    values.Add(item: local.Name);
                }
            });

        var pass = new CallBindingPass(registry: registry,
            values: values,
            module: routine?.OwnerType?.Module ?? routine?.Module);
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    /// <inheritdoc/>
    protected override Expression VisitCall(CallExpression e)
    {
        var call = (CallExpression)base.VisitCall(e: e);

        if (call is { Callee: IdentifierExpression, ResolvedRoutine: { Name: RuntimeContract.Hollow, OwnerType: null } })
        {
            return call.ResolvedType is EntityTypeSymbol entity
                ? new EntityAllocationExpression(Location: call.Location) { ResolvedType = entity }
                : throw new InvalidOperationException(
                    message: $"hollow[T]() at {call.Location} builds '{call.ResolvedType?.FullName}', not an entity.");
        }

        if (call.ResolvedRoutine is { } routine)
        {
            call = WithNativeArguments(call: call, routine: routine);
        }

        if (call.Callee is not MemberExpression member || call.ResolvedRoutine is not { OwnerType: not null } bound)
        {
            return call;
        }

        if (TypeReceiver(member: member) is { } typeReceiver)
        {
            member = member with { Object = typeReceiver };
            call = call with { Callee = member };
        }
        else if (member.Object is IdentifierExpression { ResolvedType: ConstGenericValueTypeSymbol constant } &&
                 !_values.Contains(item: ((IdentifierExpression)member.Object).Name))
        {
            // A const-generic value receiver (`N.represent()` with N bound to 4) is the number itself.
            member = member with { Object = ConstantValue(constant: constant, location: member.Object.Location) };
            call = call with { Callee = member };
        }

        RoutineInfo concrete = ReceiverRoutine(routine: bound, member: member, call: call) ?? bound;
        return call with
        {
            ResolvedRoutine = concrete,
            LoweringKind = call.LoweringKind == CallLoweringKind.Unknown
                ? CallLoweringKind.DirectMemberRoutine
                : call.LoweringKind
        };
    }

    /// <inheritdoc/>
    protected override Expression VisitMember(MemberExpression e)
    {
        Expression visited = base.VisitMember(e: e);
        return visited is MemberExpression
        {
            MemberName: RuntimeContract.Carrier.TypeIdField, Object.ResolvedType: VariantTypeSymbol
        } tag
            ? new TagOfExpression(Value: tag.Object, Location: tag.Location) { ResolvedType = tag.ResolvedType }
            : visited;
    }

    /// <summary>The type a member call's receiver names, when the receiver is a type name rather than a value.</summary>
    private TypeExpression? TypeReceiver(MemberExpression member)
    {
        // An identifier that is no value here (a local, a parameter, a global, a preset, a routine, a const-generic
        // value) names a type: the one analysis typed it as (a generic parameter's concrete binding), else the one
        // its name finds.
        if (member.Object is not IdentifierExpression
            {
                IsModuleGlobal: false, ResolvedRoutine: null, ResolvedVariable: null
            } name || name.ResolvedType is ConstGenericValueTypeSymbol || _values.Contains(item: name.Name) ||
            IsVariable(name: name.Name))
        {
            return null;
        }

        TypeSymbol? type = name.ResolvedType ??
                           (_module is not null && !name.Name.Contains(value: '.')
                               ? _registry.LookupType(name: $"{_module}.{name.Name}")
                               : null) ?? _registry.LookupType(name: name.Name);
        return type is null
            ? null
            : new TypeExpression(Name: name.Name, GenericArguments: null, Location: name.Location)
            {
                ResolvedType = type
            };
    }

    /// <summary>The number a const-generic value stands for, typed as its declared type (U64 when untyped).</summary>
    private LiteralExpression ConstantValue(ConstGenericValueTypeSymbol constant, SourceLocation location)
    {
        TypeSymbol type = _registry.LookupType(name: constant.ExplicitTypeName ?? "U64") ??
                          throw new InvalidOperationException(
                              message: $"The type of the constant '{constant.Name}' is not registered.");
        return new LiteralExpression(Value: constant.Value, LiteralType: Builder.Tokenizer.TokenType.IntegerLiteral,
            Location: location) { ResolvedType = type };
    }

    /// <summary>True when <paramref name="name"/> is a registered variable (a global or a preset) seen from here.</summary>
    private bool IsVariable(string name)
    {
        return _registry.LookupVariable(name: name) is not null ||
               _module is not null && !name.Contains(value: '.') &&
               _registry.LookupVariable(name: $"{_module}.{name}") is not null;
    }

    /// <summary>The receiver's own routine for a call still bound to a routine of a generic or universal owner, or a
    /// different owner than the receiver; null when the bound routine is already the receiver's.</summary>
    private RoutineInfo? ReceiverRoutine(RoutineInfo routine, MemberExpression member, CallExpression call)
    {
        TypeSymbol? receiver = member.Object.ResolvedType;
        if (receiver is null or ProtocolTypeSymbol or GenericParameterTypeSymbol || receiver.IsGenericDefinition)
        {
            return null;
        }

        if (call.TypeArguments is { Count: > 0 })
        {
            return null;
        }

        // A Suflae entity's routine takes the Roamed handle as `me`, so a `Roamed[Point]` receiver is already
        // its own receiver.
        bool otherOwner = routine.OwnerType is not ProtocolTypeSymbol && routine.OwnerType!.FullName != receiver.FullName &&
                          routine.MeType?.FullName != receiver.FullName;
        bool generic = routine.OwnerType is { IsGenericDefinition: true } or GenericParameterTypeSymbol ||
                       routine.IsGenericDefinition ||
                       routine.TypeArguments?.Any(predicate: t => t is GenericParameterTypeSymbol or ErrorTypeSymbol) == true;
        if (!otherOwner && !generic)
        {
            return null;
        }

        // A recovery variant of the routine of the value a token stands for (`d[name]` under `try` with
        // `d: Viewing[Dict[K, V]]` binds `Dict[K, V].getitem`'s variant) takes the token as its receiver, as the
        // routine itself does.
        if (routine.Recovery != null && !generic && receiver.TypeArguments is [{ } inner] &&
            inner.FullName == routine.OwnerType!.FullName)
        {
            return null;
        }

        List<TypeSymbol> argumentTypes = call.Arguments
                                             .Select(selector: a => (a is NamedArgumentExpression named
                                                  ? named.Value
                                                  : a).ResolvedType)
                                             .OfType<TypeSymbol>()
                                             .ToList();
        // A method-generic call is already its instantiation; the receiver lookup would hand back the template.
        if (_registry.LookupMemberRoutineOverload(type: receiver,
                memberRoutineName: routine.Name,
                argTypes: argumentTypes) is not { IsGenericDefinition: false, OwnerType: not ProtocolTypeSymbol } own)
        {
            return null;
        }

        // A recovery variant shares its routine's name, so the receiver's routine by name is the failable one: the
        // call stays a recovery through that routine's own variant of the same kind (a wrapper forwarder's, for
        // `d[name]` under `try` with `d: Viewing[Dict[K, V]]`).
        return routine.Recovery is { } kind && own.Recovery != kind
            ? _registry.LookupRecoveryVariant(recovered: own, kind: kind)
            : own;
    }

    /// <summary>The call with each routine it hands to native code written as that code's address.</summary>
    private static CallExpression WithNativeArguments(CallExpression call, RoutineInfo routine)
    {
        List<Expression>? arguments = null;
        for (int i = 0; i < call.Arguments.Count && i < routine.Parameters.Count; i++)
        {
            TypeSymbol parameter = routine.Parameters[index: i].Type;
            if (parameter.Name != RuntimeContract.CPtr && !(routine.IsForeign && parameter is RoutineTypeSymbol))
            {
                continue;
            }

            Expression argument = call.Arguments[index: i];
            Expression value = argument is NamedArgumentExpression named
                ? named.Value
                : argument;
            Expression? native = value switch
            {
                IdentifierExpression { ResolvedRoutine: not null } reference =>
                    new NativeRoutineExpression(Routine: reference, Location: value.Location) { ResolvedType = parameter },
                { ResolvedType: RoutineTypeSymbol } routineValue =>
                    new NativeCallbackExpression(Value: routineValue, Location: value.Location) { ResolvedType = parameter },
                _ => null
            };
            if (native is null)
            {
                continue;
            }

            arguments ??= [.. call.Arguments];
            arguments[index: i] = argument is NamedArgumentExpression wrapper
                ? wrapper with { Value = native }
                : native;
        }

        return arguments is null
            ? call
            : call with { Arguments = arguments };
    }
}
