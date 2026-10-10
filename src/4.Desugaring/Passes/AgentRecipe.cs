using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Desugaring.Passes;

/// <summary>
/// Builds the two routines behind an <c>agent</c> call (see <c>SemanticVerifier.AnalyzeAgentExpression</c>): one that
/// turns the call's arguments into an Agent recipe and one that runs that recipe. Given the call's work routine
/// <c>__agent_N_work(p0: A, p1: B) -> R</c> (the deferred call chain over its parameters), it builds:
/// <code>
/// routine __agent_N_spawn(p0: A, p1: B) -> Agent[R]
///     danger
///         var recipe = recipe_new(slots: 3)
///         recipe_put[A](recipe: recipe, slot: 1, value: p0)
///         recipe_put[B](recipe: recipe, slot: 2, value: p1)
///         return agent_recipe[R](entry: __agent_N_run, recipe: recipe)
///
/// routine __agent_N_run(recipe: CPtr)
///     danger
///         var r = recipe.get_address()
///         agent_finish[R](recipe: r, value: __agent_N_work(p0: recipe_take[A](recipe: r, slot: 1), p1: ...))
///     return
/// </code>
/// <para>The <c>agent</c> call becomes a call of the spawn routine, so the arguments are computed where it is
/// written and the call itself runs wherever the verb that starts the Agent puts it. Slot 0 of the recipe holds
/// the task, written by that verb. An argument moves into the recipe with an implicit <c>steal</c>, which moves an
/// entity and leaves any other value as it is.</para>
///
/// <para>An <c>Atomic</c> argument is shared with the work, not copied: its parameter is by-reference, the recipe
/// holds <c>cell.hijack()</c>, and the run routine passes <c>recipe_take[Hijacked[Atomic[..]]](..).peek()</c>,
/// which the wrapper projection turns into the cell's address.</para>
/// </summary>
internal static class AgentRecipe
{
    /// <summary>The spawn routine: boxes each argument into a fresh recipe and returns the Agent built over it.</summary>
    internal static RoutineDeclaration BuildSpawn(RoutineDeclaration routine, string spawnName, string runName)
    {
        SourceLocation loc = routine.Location;
        TypeExpression resultType = ResultType(routine: routine);

        var statements = new List<Statement>
        {
            new DeclarationStatement(
                Declaration: new VariableDeclaration(Name: "recipe",
                    Type: null,
                    Initializer: Call(name: "recipe_new",
                        typeArguments: null,
                        arguments: [Named(name: "slots", value: U64(value: routine.Parameters.Count + 1, loc: loc))],
                        loc: loc),
                    Visibility: VisibilityModifier.Open,
                    Location: loc),
                Location: loc)
        };

        for (int i = 0; i < routine.Parameters.Count; i++)
        {
            Parameter p = routine.Parameters[index: i];
            Expression moved = p.IsByReference
                ? new CallExpression(
                    Callee: new MemberExpression(Object: Id(name: p.Name, loc: loc), MemberName: "hijack",
                        Location: loc),
                    Arguments: [],
                    Location: loc)
                : new StealExpression(Operand: Id(name: p.Name, loc: loc), Location: loc) { IsImplicitMove = true };
            statements.Add(item: new ExpressionStatement(
                Expression: Call(name: "recipe_put",
                    typeArguments: [SlotType(parameter: p)],
                    arguments:
                    [
                        Named(name: "recipe", value: Id(name: "recipe", loc: loc)),
                        Named(name: "slot", value: U64(value: i + 1, loc: loc)),
                        Named(name: "value", value: moved)
                    ],
                    loc: loc),
                Location: loc));
        }

        statements.Add(item: new ReturnStatement(
            Value: Call(name: "agent_recipe",
                typeArguments: [resultType],
                arguments:
                [
                    Named(name: "entry", value: Id(name: runName, loc: loc)),
                    Named(name: "recipe", value: Id(name: "recipe", loc: loc))
                ],
                loc: loc),
            Location: loc));

        return new RoutineDeclaration(Name: spawnName,
            Parameters: routine.Parameters.ToList(),
            ReturnType: new TypeExpression(Name: "Agent", GenericArguments: [resultType], Location: loc),
            Body: new BlockStatement(
                Statements: [new DangerStatement(Body: new BlockStatement(Statements: statements, Location: loc),
                    Location: loc) { IsBuilderWritten = true }],
                Location: loc),
            Visibility: routine.Visibility,
            Annotations: [],
            Location: loc) { IsBuilderWritten = true };
    }

    /// <summary>The run routine <c>(recipe: CPtr)</c>, the entry of the fiber or thread that runs the Agent: moves the
    /// arguments out of the recipe, calls the work routine and completes the task with its result.</summary>
    internal static RoutineDeclaration BuildRun(RoutineDeclaration routine, string runName)
    {
        SourceLocation loc = routine.Location;
        var cptr = new TypeExpression(Name: "CPtr", GenericArguments: null, Location: loc);
        List<Parameter> parameters = [new Parameter(Name: "recipe", Type: cptr, DefaultValue: null, Location: loc)];

        var arguments = new List<Expression>();
        for (int i = 0; i < routine.Parameters.Count; i++)
        {
            Parameter p = routine.Parameters[index: i];
            Expression taken = Call(name: "recipe_take",
                typeArguments: [SlotType(parameter: p)],
                arguments:
                [
                    Named(name: "recipe", value: Id(name: "r", loc: loc)),
                    Named(name: "slot", value: U64(value: i + 1, loc: loc))
                ],
                loc: loc);
            arguments.Add(item: Named(name: p.Name,
                value: p.IsByReference
                    ? new CallExpression(Callee: new MemberExpression(Object: taken, MemberName: "peek", Location: loc),
                        Arguments: [],
                        Location: loc)
                    : taken));
        }

        var direct = new CallExpression(Callee: Id(name: routine.Name, loc: loc), Arguments: arguments, Location: loc)
        {
            IsRecipeRunCall = true
        };

        var statements = new List<Statement>
        {
            new DeclarationStatement(
                Declaration: new VariableDeclaration(Name: "r",
                    Type: null,
                    Initializer: new CallExpression(
                        Callee: new MemberExpression(Object: Id(name: "recipe", loc: loc),
                            MemberName: "get_address",
                            Location: loc),
                        Arguments: [],
                        Location: loc),
                    Visibility: VisibilityModifier.Open,
                    Location: loc),
                Location: loc)
        };

        if (routine.ReturnType == null)
        {
            statements.Add(item: new ExpressionStatement(Expression: direct, Location: loc));
            statements.Add(item: new ExpressionStatement(
                Expression: Call(name: "agent_finish_none",
                    typeArguments: null,
                    arguments: [Named(name: "recipe", value: Id(name: "r", loc: loc))],
                    loc: loc),
                Location: loc));
        }
        else
        {
            statements.Add(item: new ExpressionStatement(
                Expression: Call(name: "agent_finish",
                    typeArguments: [routine.ReturnType],
                    arguments:
                    [
                        Named(name: "recipe", value: Id(name: "r", loc: loc)),
                        Named(name: "value", value: direct)
                    ],
                    loc: loc),
                Location: loc));
        }

        return new RoutineDeclaration(Name: runName,
            Parameters: parameters,
            ReturnType: null,
            Body: new BlockStatement(
                Statements:
                [
                    new DangerStatement(Body: new BlockStatement(Statements: statements, Location: loc), Location: loc)
                    {
                        IsBuilderWritten = true
                    },
                    new ReturnStatement(Value: null, Location: loc)
                ],
                Location: loc),
            Visibility: VisibilityModifier.Secret,
            Annotations: [],
            Location: loc) { IsBuilderWritten = true };
    }

    /// <summary>What a parameter's recipe slot holds: the value, or for a by-reference parameter a
    /// <c>Hijacked</c> pointer to the caller's storage.</summary>
    private static TypeExpression SlotType(Parameter parameter)
    {
        return parameter.IsByReference
            ? new TypeExpression(Name: RuntimeContract.Hijacked, GenericArguments: [parameter.Type!],
                Location: parameter.Location)
            : parameter.Type!;
    }

    /// <summary>The Agent's result type: the routine's return type, or <c>None</c> when it returns nothing.</summary>
    private static TypeExpression ResultType(RoutineDeclaration routine)
    {
        return routine.ReturnType ??
               new TypeExpression(Name: "None", GenericArguments: null, Location: routine.Location);
    }

    internal static Expression Call(string name, List<TypeExpression>? typeArguments, List<Expression> arguments,
        SourceLocation loc)
    {
        return typeArguments is { Count: > 0 }
            ? new GenericMemberRoutineCallExpression(Object: Id(name: name, loc: loc),
                MemberRoutineName: name,
                TypeArguments: typeArguments,
                Arguments: arguments,
                IsMemoryOperation: false,
                Location: loc)
            : new CallExpression(Callee: Id(name: name, loc: loc), Arguments: arguments, Location: loc);
    }

    internal static NamedArgumentExpression Named(string name, Expression value)
    {
        return new NamedArgumentExpression(Name: name, Value: value, Location: value.Location);
    }

    internal static IdentifierExpression Id(string name, SourceLocation loc)
    {
        return new IdentifierExpression(Name: name, Location: loc);
    }

    internal static LiteralExpression U64(int value, SourceLocation loc)
    {
        return new LiteralExpression(Value: (ulong)value, LiteralType: TokenType.U64Literal, Location: loc);
    }
}
