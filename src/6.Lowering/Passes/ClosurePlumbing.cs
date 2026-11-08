using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Enums;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// The typed nodes a capturing lambda is lowered to (LambdaLiftingPass): the payload
/// <c>closure_seal(table: closure_put[V1](bound: closure_put[V0](bound: closure_new(slots: 2), slot: 0, value: a), slot:
/// 1, value: b), drop_hook: ..)</c> at the lambda, and in the lifted routine a trailing <c>__bound: Addr</c> parameter
/// plus <c>var a = closure_get[V0](bound: __bound, slot: 0)</c> per captured value. The payload is a counted entity
/// whose data is the slot table: a copy of the routine value is one more holder, and the last holder gone runs the
/// lambda's drop hook, a routine written here that ends each captured value (<c>closure_drop[V]</c>) and frees the
/// cells and the table (<c>closure_end</c>). When a capture may reach a shared entity or another routine value, the
/// lambda also gets a trace hook (<c>closure_trace[V]</c>) and a free hook for the collector (<c>closure_reap[V]</c>).
/// The helpers live in System (<c>System/Memory/Closure.rvr</c>), so the payload's layout and the copy of each
/// captured value are ordinary RV code, and the emitter only pairs the lifted routine with the payload.
/// </summary>
internal sealed class ClosurePlumbing
{
    /// <summary>The trailing parameter a capturing lifted routine receives its payload in.</summary>
    internal const string BoundParameterName = "__bound";

    private readonly TypeRegistry _registry;
    private readonly SourceLocation _location;
    private readonly TypeSymbol _cptr;
    private readonly TypeSymbol _u64;

    internal ClosurePlumbing(TypeRegistry registry, SourceLocation location)
    {
        _registry = registry;
        _location = location;
        _cptr = Lookup(name: RuntimeContract.Addr);
        _u64 = Lookup(name: "U64");
    }

    /// <summary>The lifted routine's <c>__bound: Addr</c> parameter.</summary>
    internal Parameter BoundParameter()
    {
        return new Parameter(Name: BoundParameterName,
            Type: new TypeExpression(Name: RuntimeContract.Addr, GenericArguments: null, Location: _location)
            {
                ResolvedType = _cptr
            },
            DefaultValue: null,
            Location: _location);
    }

    /// <summary>The <see cref="ParamInfo"/> of <see cref="BoundParameter"/>.</summary>
    internal ParamInfo BoundParamInfo()
    {
        return new ParamInfo(name: BoundParameterName, type: _cptr);
    }

    /// <summary><c>var name = closure_get[type](bound: __bound, slot: slot)</c>, a copy the routine ends at its exits.
    /// A token or entity the lambda is bound to is read back as it is (<c>closure_get_bound</c>), with nothing copied
    /// and nothing ended.</summary>
    internal Statement CaptureLocal(string name, TypeSymbol type, int slot, bool isBound = false)
    {
        CallExpression get = Call(
            routine: Instance(name: isBound ? RuntimeContract.Closure.GetBound : RuntimeContract.Closure.Get,
                typeArgument: type),
            arguments:
            [
                ("bound", new IdentifierExpression(Name: BoundParameterName, Location: _location)
                {
                    ResolvedType = _cptr
                }),
                ("slot", U64(value: slot))
            ]);
        return new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: name,
                // A routine type's name (`Callable[(S64,), Text]`) is no type name a later lookup can read back, so
                // such a local takes its type from the call.
                Type: type is RoutineTypeSymbol
                    ? null
                    : new TypeExpression(Name: type.Name, GenericArguments: null, Location: _location)
                    {
                        ResolvedType = type
                    },
                Initializer: get,
                Visibility: VisibilityModifier.Open,
                Location: _location)
            {
                IsBoundCapture = isBound,
                // The copy is the lifted routine's own, ended at its exits. The lambda is lifted after the
                // program's scope teardown ran, so the late run gives the local its teardown.
                OwesTeardown = !isBound,
                OwnsItsValue = !isBound
            },
            Location: _location);
    }

    /// <summary>
    /// The payload storing each captured value, in capture order, and the hook routines written for it (to be lifted
    /// next to the lambda). A captured token or entity the lambda is bound to (<c>IsBound</c>) is kept where it is:
    /// the hooks free its cell and end nothing.
    /// </summary>
    /// <param name="captures">The captured values in slot order.</param>
    /// <param name="hookName">The prefix of the hook routines' names.</param>
    /// <param name="module">The module the hooks belong to.</param>
    internal (Expression Payload, List<(RoutineDeclaration Declaration, RoutineInfo Info)> Hooks) BuildPayload(
        List<(string Name, TypeSymbol Type, bool IsBound)> captures, string hookName, string? module)
    {
        RoutineInfo closureNew = Helper(name: RuntimeContract.Closure.New, argTypes: [_u64]);
        Expression table = Call(routine: closureNew, arguments: [("slots", U64(value: captures.Count))]);
        for (int i = 0; i < captures.Count; i++)
        {
            table = Call(routine: Instance(name: RuntimeContract.Closure.Put, typeArgument: captures[index: i].Type),
                arguments:
                [
                    ("bound", table),
                    ("slot", U64(value: i)),
                    ("value", new IdentifierExpression(Name: captures[index: i].Name, Location: _location)
                    {
                        ResolvedType = captures[index: i].Type
                    })
                ]);
        }

        var hooks = new List<(RoutineDeclaration Declaration, RoutineInfo Info)>();
        var kept = captures.Select(selector: (c, i) => (c.Type, Slot: i, c.IsBound))
                           .Where(predicate: c => !c.IsBound)
                           .ToList();
        (RoutineDeclaration Declaration, RoutineInfo Info) drop = Hook(name: $"{hookName}_drop",
            module: module,
            perCapture: RuntimeContract.Closure.Drop,
            captures: kept,
            slots: captures.Count);
        hooks.Add(item: drop);

        // A payload no capture of which can reach a handle is never traced: it is collected only as part of a cycle
        // through what keeps it, and then ending its captures whole is what the free hook does.
        if (!kept.Any(predicate: c => HandleReach.Reaches(type: c.Type)))
        {
            RoutineInfo seal = Helper(name: RuntimeContract.Closure.Seal, argTypes: [_cptr, _cptr]);
            return (Call(routine: seal, arguments: [("table", table), ("drop_hook", Address(hook: drop.Info))]), hooks);
        }

        (RoutineDeclaration Declaration, RoutineInfo Info) trace = Hook(name: $"{hookName}_trace",
            module: module,
            perCapture: RuntimeContract.Closure.Trace,
            captures: kept.Where(predicate: c => HandleReach.Reaches(type: c.Type))
                          .ToList(),
            slots: null);
        (RoutineDeclaration Declaration, RoutineInfo Info) free = Hook(name: $"{hookName}_free",
            module: module,
            perCapture: RuntimeContract.Closure.Reap,
            captures: kept,
            slots: captures.Count);
        hooks.Add(item: trace);
        hooks.Add(item: free);
        RoutineInfo sealTraced = Helper(name: RuntimeContract.Closure.SealTraced, argTypes: [_cptr, _cptr, _cptr, _cptr]);
        return (Call(routine: sealTraced,
            arguments:
            [
                ("table", table),
                ("drop_hook", Address(hook: drop.Info)),
                ("trace_hook", Address(hook: trace.Info)),
                ("free_hook", Address(hook: free.Info))
            ]), hooks);
    }

    /// <summary>
    /// A hook routine of a lambda's payload, called by the runtime with the slot table: <c>routine name(data: Addr)</c>
    /// whose body calls <paramref name="perCapture"/> on each of <paramref name="captures"/>, then frees the cells
    /// and the table when <paramref name="slots"/> is given.
    /// </summary>
    private (RoutineDeclaration Declaration, RoutineInfo Info) Hook(string name, string? module, string perCapture,
        List<(TypeSymbol Type, int Slot, bool IsBound)> captures, int? slots)
    {
        const string dataName = "data";
        var statements = new List<Statement>();
        foreach ((TypeSymbol type, int slot, _) in captures)
        {
            statements.Add(item: new ExpressionStatement(
                Expression: Call(routine: Instance(name: perCapture, typeArgument: type),
                    arguments: [("table", Data(name: dataName)), ("slot", U64(value: slot))]),
                Location: _location));
        }

        if (slots is { } count)
        {
            RoutineInfo end = Helper(name: RuntimeContract.Closure.End, argTypes: [_cptr, _u64]);
            statements.Add(item: new ExpressionStatement(
                Expression: Call(routine: end, arguments: [("table", Data(name: dataName)), ("slots", U64(value: count))]),
                Location: _location));
        }

        statements.Add(item: new ReturnStatement(Value: null, Location: _location));
        var declaration = new RoutineDeclaration(Name: name,
            Parameters:
            [
                new Parameter(Name: dataName,
                    Type: new TypeExpression(Name: RuntimeContract.Addr, GenericArguments: null, Location: _location)
                    {
                        ResolvedType = _cptr
                    },
                    DefaultValue: null,
                    Location: _location)
            ],
            ReturnType: null,
            Body: new BlockStatement(Statements: statements, Location: _location),
            Visibility: VisibilityModifier.Secret,
            Annotations: [],
            Location: _location,
            GenericParameters: null,
            GenericConstraints: null,
            IsFailable: false,
            IsCommon: false,
            IsDangerous: false);
        var info = new RoutineInfo(name: name)
        {
            Kind = RoutineKind.FreeRoutine,
            IsClosureHook = true,
            Parameters = [new ParamInfo(name: dataName, type: _cptr)],
            ReturnType = null,
            Visibility = VisibilityModifier.Secret,
            Location = _location,
            Module = module,
            ModulePath = module?.Split(separator: '/')
                                .ToList(),
            IsSynthesized = true
        };
        declaration.ResolvedInfo = info;
        return (declaration, info);
    }

    /// <summary>The code address of a hook routine, as the payload's controller keeps it.</summary>
    private NativeRoutineExpression Address(RoutineInfo hook)
    {
        RoutineTypeSymbol hookType = _registry.GetOrCreateRoutineType(parameterTypes: [_cptr],
            returnType: null,
            isFailable: false);
        return new NativeRoutineExpression(
            Routine: new IdentifierExpression(Name: hook.Name, Location: _location)
            {
                ResolvedRoutine = hook, ResolvedType = hookType
            },
            Location: _location) { ResolvedType = _cptr };
    }

    private IdentifierExpression Data(string name)
    {
        return new IdentifierExpression(Name: name, Location: _location) { ResolvedType = _cptr };
    }

    /// <summary>A non-generic System closure helper.</summary>
    private RoutineInfo Helper(string name, List<TypeSymbol> argTypes)
    {
        return _registry.LookupRoutineOverload(baseName: $"System.{name}", argTypes: argTypes) ??
               throw new InvalidOperationException(message: $"System.{name} is not registered.");
    }

    /// <summary>The instance of a System closure helper for one captured value's type.</summary>
    private RoutineInfo Instance(string name, TypeSymbol typeArgument)
    {
        if (ContainsGenericParameter(type: typeArgument))
        {
            throw new InvalidOperationException(
                message: $"A lambda captures a value of generic type '{typeArgument.Name}', which a closure " +
                         "payload cannot hold yet.");
        }

        RoutineInfo definition = _registry.LookupGenericOverload(name: name) is { Module: "System" } found
            ? found
            : throw new InvalidOperationException(message: $"System.{name} is not registered.");
        return _registry.GetOrCreateRoutineResolution(genericDef: definition, typeArguments: [typeArgument]);
    }

    private CallExpression Call(RoutineInfo routine, List<(string Name, Expression Value)> arguments)
    {
        return new CallExpression(
            Callee: new IdentifierExpression(Name: routine.Name, Location: _location) { ResolvedRoutine = routine },
            Arguments: arguments
                      .Select(selector: a => (Expression)new NamedArgumentExpression(Name: a.Name,
                           Value: a.Value,
                           Location: _location) { ResolvedType = a.Value.ResolvedType })
                      .ToList(),
            Location: _location) { ResolvedRoutine = routine, ResolvedType = routine.ReturnType };
    }

    private LiteralExpression U64(int value)
    {
        return new LiteralExpression(Value: value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
            LiteralType: TokenType.U64Literal,
            Location: _location) { ResolvedType = _u64 };
    }

    private TypeSymbol Lookup(string name)
    {
        return _registry.LookupType(name: name) ??
               throw new InvalidOperationException(message: $"System.{name} is not registered.");
    }

    private static bool ContainsGenericParameter(TypeSymbol type)
    {
        return type is GenericParameterTypeSymbol or ProtocolSelfTypeSymbol or BuildtimeConstGenericTypeSymbol ||
               (type.TypeArguments?.Any(predicate: ContainsGenericParameter) ?? false);
    }
}
