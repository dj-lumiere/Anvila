using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Desugaring.Passes;

/// <summary>
/// Builds the two routines behind a <c>to[Work]</c> conversion (see <c>SemanticVerifier.AnalyzeWorkExpression</c>): one that
/// turns the call's arguments into a Work recipe and one that runs that recipe. Given the call's work routine
/// <c>__work_N_work(p0: A, p1: B) -> Check[R]</c> (<c>check</c> over the deferred call chain, written over its
/// parameters), it builds:
/// <code>
/// routine __work_N_spawn(p0: A, p1: B) -> Work[R]
///     danger
///         var recipe = recipe_new(slots: 3)
///         recipe_put[A](recipe: recipe, slot: 1, value: p0)
///         recipe_put[B](recipe: recipe, slot: 2, value: p1)
///         return work_recipe[R](entry: __work_N_run, recipe: recipe)
///
/// routine __work_N_run(recipe: Addr)
///     danger
///         var r = recipe
///         work_wait_released(recipe: r)
///         var outcome = __work_N_work(p0: recipe_take[A](recipe: r, slot: 1), p1: ...)
///         work_finish[R](recipe: r, outcome: outcome.hijack())
///     return
/// </code>
/// <para>The <c>to[Work]</c> conversion becomes a call of the spawn routine, so the arguments are computed where it is
/// written and the call itself runs wherever the verb that starts the Work puts it. Slot 0 of the recipe holds
/// the task, written by that verb. An argument moves into the recipe with an implicit <c>steal</c>, which moves an
/// entity and leaves any other value as it is.</para>
///
/// <para>An <c>Unbroken</c> argument is shared with the work, not copied: its parameter is by-reference, the recipe
/// holds <c>cell.hijack()</c>, and the run routine passes <c>recipe_take[Hijacked[Unbroken[..]]](..).peek()</c>,
/// which the wrapper projection turns into the cell's address.</para>
/// </summary>
internal static class WorkRecipe
{
    /// <summary>The spawn routine: boxes each argument into a fresh recipe and returns the Work built over it.</summary>
    internal static RoutineDeclaration BuildSpawn(RoutineDeclaration routine, string spawnName, string runName,
        TypeExpression resultType)
    {
        SourceLocation loc = routine.Location;

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
            Value: Call(name: "work_recipe",
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
            ReturnType: new TypeExpression(Name: "Work", GenericArguments: [resultType], Location: loc),
            Body: new BlockStatement(
                Statements: [new DangerStatement(Body: new BlockStatement(Statements: statements, Location: loc),
                    Location: loc) { IsBuilderWritten = true }],
                Location: loc),
            Visibility: routine.Visibility,
            Annotations: ["untraced"],
            Location: loc) { IsBuilderWritten = true };
    }

    /// <summary>The run routine <c>(recipe: Addr)</c>, the entry of the fiber or thread that runs the Work: moves the
    /// arguments out of the recipe, calls the work routine and completes the task with its result.</summary>
    internal static RoutineDeclaration BuildRun(RoutineDeclaration routine, string runName, TypeExpression resultType)
    {
        SourceLocation loc = routine.Location;
        var cptr = new TypeExpression(Name: RuntimeContract.Addr, GenericArguments: null, Location: loc);
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
                    Initializer: Id(name: "recipe", loc: loc),
                    Visibility: VisibilityModifier.Open,
                    Location: loc),
                Location: loc)
        };

        // A Work of a list started with the list's `start_now()` / `start_here()` / `start_apart()` is let go only
        // when every Work of the list is ready: wait for that before reading the arguments.
        statements.Add(item: new ExpressionStatement(
            Expression: Call(name: "work_wait_released",
                typeArguments: null,
                arguments: [Named(name: "recipe", value: Id(name: "r", loc: loc))],
                loc: loc),
            Location: loc));

        // The work returns what it came to (`check` over the call chain): a failure stays in the Work and surfaces
        // where the Work is retrieved. The outcome is a local that `work_finish` moves into the task's box.
        statements.Add(item: new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: "outcome",
                Type: null,
                Initializer: direct,
                Visibility: VisibilityModifier.Open,
                Location: loc),
            Location: loc));
        statements.Add(item: new ExpressionStatement(
            Expression: Call(name: "work_finish",
                typeArguments: [resultType],
                arguments:
                [
                    Named(name: "recipe", value: Id(name: "r", loc: loc)),
                    Named(name: "outcome",
                        value: new CallExpression(
                            Callee: new MemberExpression(Object: Id(name: "outcome", loc: loc), MemberName: "hijack",
                                Location: loc),
                            Arguments: [],
                            Location: loc))
                ],
                loc: loc),
            Location: loc));

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
            Annotations: ["untraced"],
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

    internal static Expression Call(string name, List<TypeExpression>? typeArguments, List<Expression> arguments,
        SourceLocation loc)
    {
        return typeArguments is { Count: > 0 }
            ? new GenericMemberRoutineCallExpression(Object: Id(name: name, loc: loc),
                MemberRoutineName: name,
                TypeArguments: typeArguments,
                Arguments: arguments,
                IsMemoryOperation: false,
                Location: loc) { IsFree = true }
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
