using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// The typed nodes a capturing lambda is lowered to (LambdaLiftingPass): the payload chain
/// <c>closure_put[V1](bound: closure_put[V0](bound: closure_new(slots: 2), slot: 0, value: a), slot: 1, value: b)</c>
/// at the lambda, and in the lifted routine a trailing <c>__bound: CPtr</c> parameter plus
/// <c>var a = closure_get[V0](bound: __bound, slot: 0)</c> per captured value. The helpers live in Core
/// (<c>Core/Memory/Closure.rf</c>), so the payload's layout and the copy of each captured value are ordinary
/// RF code, and the emitter only pairs the lifted routine with the payload.
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
        _cptr = Lookup(name: "CPtr");
        _u64 = Lookup(name: "U64");
    }

    /// <summary>The lifted routine's <c>__bound: CPtr</c> parameter.</summary>
    internal Parameter BoundParameter()
    {
        return new Parameter(Name: BoundParameterName,
            Type: new TypeExpression(Name: "CPtr", GenericArguments: null, Location: _location)
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

    /// <summary><c>var name = closure_get[type](bound: __bound, slot: slot)</c>.</summary>
    internal Statement CaptureLocal(string name, TypeSymbol type, int slot)
    {
        CallExpression get = Call(routine: Instance(name: "closure_get", typeArgument: type),
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
                Type: new TypeExpression(Name: type.Name, GenericArguments: null, Location: _location)
                {
                    ResolvedType = type
                },
                Initializer: get,
                Visibility: VisibilityModifier.Open,
                Location: _location),
            Location: _location);
    }

    /// <summary>The payload chain storing each captured value, in capture order.</summary>
    internal Expression BuildPayload(List<(string Name, TypeSymbol Type)> captures)
    {
        RoutineInfo closureNew = _registry.LookupRoutineOverload(baseName: "Core.closure_new", argTypes: [_u64]) ??
                                 throw new InvalidOperationException(message: "Core.closure_new is not registered.");
        Expression bound = Call(routine: closureNew, arguments: [("slots", U64(value: captures.Count))]);
        for (int i = 0; i < captures.Count; i++)
        {
            bound = Call(routine: Instance(name: "closure_put", typeArgument: captures[index: i].Type),
                arguments:
                [
                    ("bound", bound),
                    ("slot", U64(value: i)),
                    ("value", new IdentifierExpression(Name: captures[index: i].Name, Location: _location)
                    {
                        ResolvedType = captures[index: i].Type
                    })
                ]);
        }

        return bound;
    }

    /// <summary>The instance of a Core closure helper for one captured value's type.</summary>
    private RoutineInfo Instance(string name, TypeSymbol typeArgument)
    {
        if (ContainsGenericParameter(type: typeArgument))
        {
            throw new InvalidOperationException(
                message: $"A lambda captures a value of generic type '{typeArgument.Name}', which a closure " +
                         "payload cannot hold yet.");
        }

        RoutineInfo definition = _registry.LookupGenericOverload(name: name) is { Module: "Core" } found
            ? found
            : throw new InvalidOperationException(message: $"Core.{name} is not registered.");
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
               throw new InvalidOperationException(message: $"Core.{name} is not registered.");
    }

    private static bool ContainsGenericParameter(TypeSymbol type)
    {
        return type is GenericParameterTypeSymbol or ProtocolSelfTypeSymbol or BuildtimeConstGenericTypeSymbol ||
               (type.TypeArguments?.Any(predicate: ContainsGenericParameter) ?? false);
    }
}
