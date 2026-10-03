using Builder.Declaration;
using Builder.Diagnostics;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Desugaring.Passes;

/// <summary>
/// Builds, for every <c>threaded</c> or <c>suspended</c> routine, the two ordinary routines that turn a call
/// into an Agent recipe and run that recipe. Runs on the user programs at the start of semantic analysis,
/// before declarations are collected, so the synthesized routines are analyzed, lowered and emitted like any
/// other routine.
///
/// <para>For <c>threaded routine work(a: A, b: B) -> R</c> it appends:</para>
/// <code>
/// routine __async_spawn_work(a: A, b: B) -> Agent[R]
///     danger
///         var recipe = recipe_new(slots: 3)
///         recipe_put[A](recipe: recipe, slot: 1, value: a)
///         recipe_put[B](recipe: recipe, slot: 2, value: b)
///         return agent_threaded[R](entry: __async_run_work, recipe: recipe)
///
/// routine __async_run_work(task: CPtr, recipe: CPtr)
///     danger
///         var r = recipe.get_address()
///         agent_finish[R](recipe: r, value: work(a: recipe_take[A](recipe: r, slot: 1), b: ...))
///     return
/// </code>
/// <para>Semantic analysis sends every call of <c>work</c> to <c>__async_spawn_work</c> (a call builds the
/// recipe and starts nothing), except the call inside <c>__async_run_work</c>, which is marked
/// <see cref="CallExpression.IsDirectAsyncInvoke"/>. A <c>suspended</c> routine's run routine takes only
/// <c>recipe: CPtr</c> (a coroutine entry has one argument). Slot 0 of the recipe holds the task, written by
/// <c>agent_threaded</c>/<c>agent_suspended</c>. An argument moves into the recipe with an implicit
/// <c>steal</c>, which moves an entity and leaves any other value as it is.</para>
///
/// <para>A <c>threaded</c> routine's <c>Atomic</c> parameter is shared with the worker, not copied: it is
/// marked by-reference on the routine and on its spawn routine, the recipe holds <c>cell.hijack()</c>, and
/// the run routine passes <c>recipe_take[Hijacked[Atomic[..]]](..).peek()</c>, which the wrapper projection
/// turns into the cell's address.</para>
///
/// <para>Member and generic <c>threaded</c>/<c>suspended</c> routines are reported (RF-S642): a recipe holds
/// no receiver, and the run routine of a generic one would need its instance's address.</para>
/// </summary>
internal static class AsyncRecipeSynthesisPass
{
    /// <summary>Prefix of the routine that builds a recipe from a call's arguments.</summary>
    internal const string SpawnPrefix = "__async_spawn_";

    /// <summary>Prefix of the routine a coroutine or worker thread runs to execute a recipe.</summary>
    internal const string RunPrefix = "__async_run_";

    /// <summary>Appends the spawn and run routines of each async routine to its program.</summary>
    internal static void Run(List<(SyntaxTree.Program Program, string FilePath)> files,
        Action<SemanticDiagnosticCode, string, SourceLocation> report)
    {
        foreach ((SyntaxTree.Program program, string _) in files)
        {
            var synthesized = new List<ISyntaxTreeNode>();
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is not RoutineDeclaration { Async: AsyncStatus.Threaded or AsyncStatus.Suspended } routine)
                {
                    continue;
                }

                if (routine.ReceiverType != null || routine.GenericParameters is { Count: > 0 })
                {
                    report(arg1: SemanticDiagnosticCode.AsyncRoutineNotFree,
                        arg2: $"'{routine.Name}' is a {(routine.ReceiverType != null ? "member" : "generic")} " +
                              $"{(routine.Async == AsyncStatus.Threaded ? "threaded" : "suspended")} routine. " +
                              "Only a non-generic free routine can be threaded or suspended: make it a free " +
                              "routine that takes the receiver as a parameter, or a non-generic one.",
                        arg3: routine.Location);
                    continue;
                }

                MarkSharedParameters(routine: routine);
                synthesized.Add(item: BuildSpawn(routine: routine));
                synthesized.Add(item: BuildRun(routine: routine));
            }

            program.Declarations.AddRange(collection: synthesized);
        }
    }

    /// <summary>Marks each <c>Atomic</c> parameter of a <c>threaded</c> routine by-reference: every worker
    /// works on the spawner's one cell.</summary>
    private static void MarkSharedParameters(RoutineDeclaration routine)
    {
        if (routine.Async != AsyncStatus.Threaded)
        {
            return;
        }

        for (int i = 0; i < routine.Parameters.Count; i++)
        {
            if (routine.Parameters[index: i] is { Type.Name: RuntimeContract.Atomic } p)
            {
                routine.Parameters[index: i] = p with { IsByReference = true };
            }
        }
    }

    /// <summary><c>__async_spawn_foo(params) -> Agent[R]</c>: boxes each argument into a fresh recipe and
    /// returns the Agent built over it.</summary>
    private static RoutineDeclaration BuildSpawn(RoutineDeclaration routine)
    {
        SourceLocation loc = routine.Location;
        bool threaded = routine.Async == AsyncStatus.Threaded;
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
            Value: Call(name: threaded ? "agent_threaded" : "agent_suspended",
                typeArguments: [resultType],
                arguments:
                [
                    Named(name: "entry", value: Id(name: RunPrefix + routine.Name, loc: loc)),
                    Named(name: "recipe", value: Id(name: "recipe", loc: loc))
                ],
                loc: loc),
            Location: loc));

        return new RoutineDeclaration(Name: SpawnPrefix + routine.Name,
            Parameters: routine.Parameters.ToList(),
            ReturnType: new TypeExpression(Name: "Agent", GenericArguments: [resultType], Location: loc),
            Body: new BlockStatement(
                Statements: [new DangerStatement(Body: new BlockStatement(Statements: statements, Location: loc),
                    Location: loc) { IsBuilderWritten = true }],
                Location: loc),
            Visibility: routine.Visibility,
            Annotations: [],
            Location: loc);
    }

    /// <summary><c>__async_run_foo(task: CPtr, recipe: CPtr)</c> (threaded) or <c>(recipe: CPtr)</c>
    /// (suspended): moves the arguments out of the recipe, calls the routine directly and completes the
    /// task with its result.</summary>
    private static RoutineDeclaration BuildRun(RoutineDeclaration routine)
    {
        SourceLocation loc = routine.Location;
        var cptr = new TypeExpression(Name: "CPtr", GenericArguments: null, Location: loc);
        List<Parameter> parameters = routine.Async == AsyncStatus.Threaded
            ? [new Parameter(Name: "task", Type: cptr, DefaultValue: null, Location: loc),
                new Parameter(Name: "recipe", Type: cptr, DefaultValue: null, Location: loc)]
            : [new Parameter(Name: "recipe", Type: cptr, DefaultValue: null, Location: loc)];

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
            IsDirectAsyncInvoke = true
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

        return new RoutineDeclaration(Name: RunPrefix + routine.Name,
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
            Location: loc);
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
