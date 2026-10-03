using Builder.Declaration;
using SyntaxTree;
using static Builder.Desugaring.Passes.AsyncRecipeSynthesisPass;

namespace Builder.Desugaring.Passes;

/// <summary>
/// A foreign routine marked <c>@blocking</c> (<c>@blocking routine C::read_line(...) -> T</c>) may block its thread
/// for a long time. A coroutine that calls it must not hold its worker meanwhile: the call runs on one of the
/// runtime's I/O threads while the coroutine parks, and anywhere else it runs right there (Ingrid's
/// <c>rf_blocking_call</c>). Runs on the user programs with <see cref="AsyncRecipeSynthesisPass"/>, before
/// declarations are collected.
///
/// <para>For <c>@blocking routine C::f(a: A) -> R</c> it appends:</para>
/// <code>
/// routine __blocking_f(a: A) -> R
///     danger
///         var recipe = recipe_new(slots: 2)
///         recipe_put[A](recipe: recipe, slot: 1, value: a)
///         blocking_call(entry: __blocking_run_f, recipe: recipe)
///         var result = recipe_take[R](recipe: recipe, slot: 0)
///         rf_invalidate(ptr: recipe)
///         return result
///
/// routine __blocking_run_f(recipe: CPtr)
///     danger
///         var r = recipe.get_address()
///         recipe_put[R](recipe: r, slot: 0, value: C::f(a: recipe_take[A](recipe: r, slot: 1)))
///     return
/// </code>
/// <para>and sends every call of <c>C::f</c> in those programs to <c>__blocking_f</c>, except the one in
/// <c>__blocking_run_f</c>. A variadic or failable foreign routine is left as it is.</para>
/// </summary>
internal static class BlockingCallSynthesisPass
{
    /// <summary>Prefix of the routine a call of a blocking foreign routine goes to.</summary>
    internal const string CallPrefix = "__blocking_";

    /// <summary>Prefix of the routine an I/O thread runs to make the foreign call.</summary>
    internal const string RunPrefix = "__blocking_run_";

    private const string Annotation = "blocking";

    internal static void Run(List<(Program Program, string FilePath)> files)
    {
        var blocking = new Dictionary<string, ExternalDeclaration>(comparer: StringComparer.Ordinal);
        foreach ((Program program, string _) in files)
        {
            foreach (ExternalDeclaration external in program.Declarations.OfType<ExternalDeclaration>())
            {
                if (external is { CallingConvention: "C", IsVariadic: false, IsFailable: false } &&
                    external.Annotations?.Contains(value: Annotation) == true)
                {
                    blocking[key: external.Name] = external;
                }
            }
        }

        if (blocking.Count == 0)
        {
            return;
        }

        foreach ((Program program, string _) in files)
        {
            var redirect = new Redirect(blocking: blocking);
            for (int i = 0; i < program.Declarations.Count; i++)
            {
                switch (program.Declarations[index: i])
                {
                    case RoutineDeclaration routine:
                        program.Declarations[index: i] = routine with
                        {
                            Body = redirect.VisitStatement(stmt: routine.Body)
                        };
                        break;
                    case RecordDeclaration record:
                        RedirectMembers(members: record.Members, redirect: redirect);
                        break;
                    case EntityDeclaration entity:
                        RedirectMembers(members: entity.Members, redirect: redirect);
                        break;
                }
            }

            foreach (ExternalDeclaration external in program.Declarations.OfType<ExternalDeclaration>()
                                                            .Where(predicate: e => blocking.ContainsKey(key: e.Name))
                                                            .ToList())
            {
                program.Declarations.Add(item: BuildCall(external: external));
                program.Declarations.Add(item: BuildRun(external: external));
            }
        }
    }

    private static void RedirectMembers(List<SyntaxTree.Declaration> members, Redirect redirect)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[index: j] is RoutineDeclaration member)
            {
                members[index: j] = member with { Body = redirect.VisitStatement(stmt: member.Body) };
            }
        }
    }

    /// <summary><c>__blocking_f(params) -> R</c>: moves the arguments into a recipe, has the runtime run it, and
    /// takes the result out.</summary>
    private static RoutineDeclaration BuildCall(ExternalDeclaration external)
    {
        SourceLocation loc = external.Location;
        var statements = new List<Statement>
        {
            Var(name: "recipe",
                value: Call(name: "recipe_new",
                    typeArguments: null,
                    arguments: [Named(name: "slots", value: U64(value: external.Parameters.Count + 1, loc: loc))],
                    loc: loc),
                loc: loc)
        };
        for (int i = 0; i < external.Parameters.Count; i++)
        {
            Parameter p = external.Parameters[index: i];
            statements.Add(item: new ExpressionStatement(
                Expression: Call(name: "recipe_put",
                    typeArguments: [p.Type!],
                    arguments:
                    [
                        Named(name: "recipe", value: Id(name: "recipe", loc: loc)),
                        Named(name: "slot", value: U64(value: i + 1, loc: loc)),
                        Named(name: "value",
                            value: new StealExpression(Operand: Id(name: p.Name, loc: loc), Location: loc)
                            {
                                IsImplicitMove = true
                            })
                    ],
                    loc: loc),
                Location: loc));
        }

        statements.Add(item: new ExpressionStatement(
            Expression: Call(name: "blocking_call",
                typeArguments: null,
                arguments:
                [
                    Named(name: "entry", value: Id(name: RunPrefix + external.Name, loc: loc)),
                    Named(name: "recipe", value: Id(name: "recipe", loc: loc))
                ],
                loc: loc),
            Location: loc));

        if (external.ReturnType != null)
        {
            statements.Add(item: Var(name: "result",
                value: Call(name: "recipe_take",
                    typeArguments: [external.ReturnType],
                    arguments:
                    [
                        Named(name: "recipe", value: Id(name: "recipe", loc: loc)),
                        Named(name: "slot", value: U64(value: 0, loc: loc))
                    ],
                    loc: loc),
                loc: loc));
        }

        statements.Add(item: new ExpressionStatement(
            Expression: Call(name: "rf_invalidate",
                typeArguments: null,
                arguments: [Named(name: "ptr", value: Id(name: "recipe", loc: loc))],
                loc: loc),
            Location: loc));
        statements.Add(item: new ReturnStatement(
            Value: external.ReturnType != null
                ? Id(name: "result", loc: loc)
                : null,
            Location: loc));

        return new RoutineDeclaration(Name: CallPrefix + external.Name,
            Parameters: external.Parameters.ToList(),
            ReturnType: external.ReturnType,
            Body: new BlockStatement(
                Statements:
                [
                    new DangerStatement(Body: new BlockStatement(Statements: statements, Location: loc), Location: loc)
                    {
                        IsBuilderWritten = true
                    }
                ],
                Location: loc),
            Visibility: VisibilityModifier.Secret,
            Annotations: [],
            Location: loc) { IsDangerous = external.IsDangerous };
    }

    /// <summary><c>__blocking_run_f(recipe: CPtr)</c>: takes the arguments out of the recipe, makes the foreign
    /// call and puts its result in slot 0.</summary>
    private static RoutineDeclaration BuildRun(ExternalDeclaration external)
    {
        SourceLocation loc = external.Location;
        var arguments = new List<Expression>();
        for (int i = 0; i < external.Parameters.Count; i++)
        {
            Parameter p = external.Parameters[index: i];
            arguments.Add(item: Named(name: p.Name,
                value: Call(name: "recipe_take",
                    typeArguments: [p.Type!],
                    arguments:
                    [
                        Named(name: "recipe", value: Id(name: "r", loc: loc)),
                        Named(name: "slot", value: U64(value: i + 1, loc: loc))
                    ],
                    loc: loc)));
        }

        var foreign = new CallExpression(
            Callee: new IdentifierExpression(Name: external.Name, Location: loc, Realm: "C"),
            Arguments: arguments,
            Location: loc) { IsDirectAsyncInvoke = true };

        var statements = new List<Statement>
        {
            Var(name: "r",
                value: new CallExpression(
                    Callee: new MemberExpression(Object: Id(name: "recipe", loc: loc), MemberName: "get_address",
                        Location: loc),
                    Arguments: [],
                    Location: loc),
                loc: loc),
            new ExpressionStatement(
                Expression: external.ReturnType != null
                    ? Call(name: "recipe_put",
                        typeArguments: [external.ReturnType],
                        arguments:
                        [
                            Named(name: "recipe", value: Id(name: "r", loc: loc)),
                            Named(name: "slot", value: U64(value: 0, loc: loc)),
                            Named(name: "value", value: foreign)
                        ],
                        loc: loc)
                    : foreign,
                Location: loc)
        };

        return new RoutineDeclaration(Name: RunPrefix + external.Name,
            Parameters:
            [
                new Parameter(Name: "recipe", Type: new TypeExpression(Name: "CPtr", GenericArguments: null, Location: loc),
                    DefaultValue: null, Location: loc)
            ],
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

    private static DeclarationStatement Var(string name, Expression value, SourceLocation loc)
    {
        return new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: name,
                Type: null,
                Initializer: value,
                Visibility: VisibilityModifier.Open,
                Location: loc),
            Location: loc);
    }

    /// <summary>Sends each call of a blocking foreign routine to its <c>__blocking_</c> routine, except the one
    /// that makes the foreign call (<see cref="CallExpression.IsDirectAsyncInvoke"/>).</summary>
    private sealed class Redirect(Dictionary<string, ExternalDeclaration> blocking) : AstRewriter
    {
        protected override Expression VisitCall(CallExpression e)
        {
            Expression visited = base.VisitCall(e: e);
            return visited is CallExpression
                   {
                       IsDirectAsyncInvoke: false, Callee: IdentifierExpression { Realm: "C" } callee
                   } call && blocking.ContainsKey(key: callee.Name)
                ? call with { Callee = Id(name: CallPrefix + callee.Name, loc: callee.Location) }
                : visited;
        }
    }
}
