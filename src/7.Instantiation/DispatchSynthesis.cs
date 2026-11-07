using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Instantiation;

/// <summary>
/// Writes the bodies of the routines of each stored protocol value <c>Dispatched[P]</c>
/// (<see cref="TypeRegistry.DispatchRoutines"/>): a protocol routine, <c>destruct</c> and <c>share</c>. Each body
/// switches on the type hash over exactly the concrete types the program puts into a place of that type (the lowering
/// noted them, <see cref="TypeRegistry.DispatchArms"/>), and calls that type's own routine through a library routine of
/// <c>Dispatched.rvr</c> that reaches the value at the address. The bodies are written as source would be and then
/// analyzed and lowered like the builder's other late bodies, so a call in them reaches each type's routine for the
/// demand collection. There is no table: the last arm is taken without a test, since no other type can be there.
/// </summary>
internal sealed class DispatchSynthesis(TypeRegistry registry)
{
    /// <summary>The bodies written, with the routine each belongs to.</summary>
    public List<(RoutineInfo Routine, Statement Body)> Run()
    {
        var bodies = new List<(RoutineInfo Routine, Statement Body)>();
        foreach (RecordTypeSymbol dispatched in registry.DispatchedTypes.ToList())
        {
            if (TypeRegistry.DispatchedProtocol(type: dispatched) == null)
            {
                continue;
            }

            IReadOnlyList<TypeSymbol> arms = registry.DispatchArms(dispatched: dispatched);
            foreach (RoutineInfo routine in registry.DispatchRoutines(dispatched: dispatched))
            {
                SourceLocation loc = routine.Location ?? dispatched.Location ??
                    new SourceLocation(FileName: "", Line: 0, Column: 0, Position: 0);
                Statement body = routine.Name switch
                {
                    RuntimeContract.Destroy => DestructBody(arms: arms, loc: loc),
                    RuntimeContract.Duplication.Share => ShareBody(arms: arms, loc: loc),
                    _ => CallBody(routine: routine, arms: arms, loc: loc)
                };
                bodies.Add(item: (routine, body as BlockStatement ?? new BlockStatement(Statements: [body], Location: loc)));
            }
        }

        return bodies;
    }

    /// <summary>
    /// A protocol routine: on each arm, the routine of the same name on a write token of the value
    /// (<c>dispatched_modify[T](at: me.address).update(...)</c>), its result returned.
    /// </summary>
    private Statement CallBody(RoutineInfo routine, IReadOnlyList<TypeSymbol> arms, SourceLocation loc)
    {
        bool returnsValue = routine.ReturnType is { Name: not "None" };
        return Switch(arms: arms, loc: loc, arm: arm =>
        {
            var call = new CallExpression(
                Callee: new MemberExpression(Object: Token(arm: arm, loc: loc), MemberName: routine.Name, Location: loc),
                Arguments: routine.Parameters
                                  .Select(selector: p => (Expression)new NamedArgumentExpression(Name: p.Name,
                                       Value: new IdentifierExpression(Name: p.Name, Location: loc),
                                       Location: loc))
                                  .ToList(),
                Location: loc);
            return returnsValue
                ? [new ReturnStatement(Value: call, Location: loc)]
                : [new ExpressionStatement(Expression: call, Location: loc), new ReturnStatement(Value: null, Location: loc)];
        }, noArm: () => NoTypeStored(routine: routine, loc: loc));
    }

    /// <summary>
    /// <c>destruct</c>: nothing for the zero value, else the value's own end on its arm (the entity's
    /// <c>destruct</c>, a handle let go, a record ended and its memory given back).
    /// </summary>
    private Statement DestructBody(IReadOnlyList<TypeSymbol> arms, SourceLocation loc)
    {
        var statements = new List<Statement>
        {
            new IfStatement(Condition: MeCall(name: "holds_nothing", loc: loc),
                ThenStatement: new BlockStatement(Statements: [new ReturnStatement(Value: null, Location: loc)],
                    Location: loc),
                ElseStatement: null,
                Location: loc)
        };
        statements.Add(item: Switch(arms: arms, loc: loc, arm: arm =>
        {
            (string helper, TypeSymbol typeArgument) = IsRecordArm(arm: arm)
                ? (RuntimeContract.Dispatch.EndRecord, arm)
                : (RuntimeContract.Dispatch.EndInline, arm);
            return
            [
                new ExpressionStatement(Expression: Helper(name: helper, typeArgument: typeArgument, loc: loc),
                    Location: loc),
                new ReturnStatement(Value: null, Location: loc)
            ];
        }, noArm: () => new ReturnStatement(Value: null, Location: loc)));
        return new BlockStatement(Statements: statements, Location: loc);
    }

    /// <summary>
    /// <c>share</c> (a place that keeps values): the zero value as it is, else the same type hash with a copy of
    /// the record in memory of its own, or with one more holder of a handle.
    /// </summary>
    private Statement ShareBody(IReadOnlyList<TypeSymbol> arms, SourceLocation loc)
    {
        var statements = new List<Statement>
        {
            new IfStatement(Condition: MeCall(name: "holds_nothing", loc: loc),
                ThenStatement: new BlockStatement(
                    Statements: [new ReturnStatement(Value: new IdentifierExpression(Name: "me", Location: loc), Location: loc)],
                    Location: loc),
                ElseStatement: null,
                Location: loc)
        };
        statements.Add(item: Switch(arms: arms, loc: loc, arm: arm =>
        {
            Expression copied = TypeRegistry.DispatchArmEntity(type: arm) is { } entity
                ? Helper(name: RuntimeContract.Dispatch.CopyHandle, typeArgument: entity, loc: loc)
                : Helper(name: RuntimeContract.Dispatch.CopyRecord, typeArgument: arm, loc: loc);
            var withAddress = new CallExpression(
                Callee: new MemberExpression(Object: new IdentifierExpression(Name: "me", Location: loc),
                    MemberName: "with_address",
                    Location: loc),
                Arguments: [new NamedArgumentExpression(Name: "address", Value: copied, Location: loc)],
                Location: loc);
            return [new ReturnStatement(Value: withAddress, Location: loc)];
        }, noArm: () => new ReturnStatement(Value: new IdentifierExpression(Name: "me", Location: loc), Location: loc)));
        return new BlockStatement(Statements: statements, Location: loc);
    }

    /// <summary>
    /// <c>if me.type_hash == h1 ... else if ... else last</c>: one arm per stored type, the last one taken without a
    /// test. No stored type at all gives <paramref name="noArm"/>.
    /// </summary>
    private static Statement Switch(IReadOnlyList<TypeSymbol> arms, SourceLocation loc,
        Func<TypeSymbol, List<Statement>> arm, Func<Statement> noArm)
    {
        if (arms.Count == 0)
        {
            return noArm();
        }

        Statement chain = new BlockStatement(Statements: arm(arg: arms[^1]), Location: loc);
        for (int i = arms.Count - 2; i >= 0; i--)
        {
            var test = new BinaryExpression(
                Left: new MemberExpression(Object: new IdentifierExpression(Name: "me", Location: loc),
                    MemberName: RuntimeContract.Dispatch.TypeHash,
                    Location: loc),
                Operator: BinaryOperator.Equal,
                Right: new LiteralExpression(Value: TypeRegistry.DispatchTypeHash(type: arms[index: i]).ToString(),
                    LiteralType: TokenType.U32Literal,
                    Location: loc),
                Location: loc);
            chain = new IfStatement(Condition: test,
                ThenStatement: new BlockStatement(Statements: arm(arg: arms[index: i]), Location: loc),
                ElseStatement: chain,
                Location: loc);
        }

        return chain;
    }

    /// <summary>A write token on the value of an arm: the entity or the record at the address, or the entity a
    /// handle reaches.</summary>
    private static Expression Token(TypeSymbol arm, SourceLocation loc)
    {
        return TypeRegistry.DispatchArmEntity(type: arm) is { } entity
            ? Helper(name: RuntimeContract.Dispatch.ModifyHandle, typeArgument: entity, loc: loc)
            : Helper(name: RuntimeContract.Dispatch.ModifyAt, typeArgument: arm, loc: loc);
    }

    /// <summary>Whether an arm's value is a record kept in memory of its own (not an entity or a handle kept by its
    /// bits).</summary>
    private static bool IsRecordArm(TypeSymbol arm)
    {
        return arm is not EntityTypeSymbol && TypeRegistry.DispatchArmEntity(type: arm) == null;
    }

    /// <summary><c>helper[T](at: me.address)</c>.</summary>
    private static Expression Helper(string name, TypeSymbol typeArgument, SourceLocation loc)
    {
        return new GenericMemberRoutineCallExpression(Object: new IdentifierExpression(Name: name, Location: loc),
            MemberRoutineName: name,
            TypeArguments:
            [
                new TypeExpression(Name: typeArgument.Name, GenericArguments: null, Location: loc)
                {
                    KnownType = typeArgument
                }
            ],
            Arguments:
            [
                new NamedArgumentExpression(Name: "at",
                    Value: new MemberExpression(Object: new IdentifierExpression(Name: "me", Location: loc),
                        MemberName: RuntimeContract.Dispatch.Address,
                        Location: loc),
                    Location: loc)
            ],
            IsMemoryOperation: false,
            Location: loc) { IsFree = true };
    }

    /// <summary><c>me.name()</c>.</summary>
    private static Expression MeCall(string name, SourceLocation loc)
    {
        return new CallExpression(
            Callee: new MemberExpression(Object: new IdentifierExpression(Name: "me", Location: loc), MemberName: name,
                Location: loc),
            Arguments: [],
            Location: loc);
    }

    /// <summary>A protocol routine called on a place no value was ever put into: it can only be reached on a place
    /// that holds nothing, so it stops the program, naming the routine.</summary>
    private static Statement NoTypeStored(RoutineInfo routine, SourceLocation loc)
    {
        var feature = new LiteralExpression(
            Value: $"{routine.Name} on a {TypeRegistry.DispatchedProtocol(type: routine.OwnerType)?.Name} place no value was put into",
            LiteralType: TokenType.TextLiteral,
            Location: loc);
        var error = new CallExpression(Callee: new IdentifierExpression(Name: "NotImplementedError", Location: loc),
            Arguments: [new NamedArgumentExpression(Name: "feature", Value: feature, Location: loc)],
            Location: loc);
        return new ThrowStatement(Error: error, Location: loc, IsFatal: true);
    }
}
