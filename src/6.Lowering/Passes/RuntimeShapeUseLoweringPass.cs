using Builder.Declaration;
using Builder.Verification;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// The Revare loops whose container a change inside them may reach through a name the build cannot prove different
/// (<see cref="LoopStatement.ChecksShapeAtRunTime"/>, set by the shape check): each opens a use of its container's
/// shape while it runs (<c>var __su_N = xs.begin_shape_use()</c> right before the loop, in a block of their own, so
/// the use ends with the loop at every exit). When the program has such a loop, every call of a routine that can add
/// to or remove from a container (<c>@reshaping</c>) in the program's own routines first checks its receiver
/// (<c>xs.require_shape_free()</c>), which fails with <c>ReshapingWhileInUseError</c> while a use of it is open. A
/// program without such a loop is left as it is and pays nothing.
/// </summary>
internal sealed class RuntimeShapeUseLoweringPass : AstRewriter
{
    private readonly TypeRegistry _registry;
    private readonly PostprocessingContext _ctx;
    private int _uses;

    internal RuntimeShapeUseLoweringPass(PostprocessingContext ctx)
    {
        _registry = ctx.Registry;
        _ctx = ctx;
    }

    /// <summary>Runs over the recovery variants' bodies of the program's own routines: a variant is made from its
    /// routine's body before the shape check marked its loops, so a loop is found again by where it is written.</summary>
    public void RunOnVariantBodies()
    {
        if (!_registry.RuntimeShapeUses)
        {
            return;
        }

        BodyDispatch.RunOnVariantBodies(registry: _registry, bodies: _ctx.VariantBodies,
            lower: (_, body) => string.IsNullOrEmpty(value: body.Location.FileName) ||
                                _registry.IsStandardLibraryFile(filePath: body.Location.FileName)
                ? body
                : VisitStatement(stmt: body));
    }

    /// <summary>Runs over the program's routine bodies.</summary>
    public void Run(Program program)
    {
        if (!_registry.RuntimeShapeUses || string.IsNullOrEmpty(value: program.Location.FileName) ||
            _registry.IsStandardLibraryFile(filePath: program.Location.FileName))
        {
            return;
        }

        BodyDispatch.RunOnProgram(registry: _registry, program: program, lower: r => VisitStatement(stmt: r.Body));
    }

    /// <inheritdoc/>
    protected override Statement VisitBlock(BlockStatement s)
    {
        Statement visited = base.VisitBlock(s: s);
        if (visited is not BlockStatement block)
        {
            return visited;
        }

        List<Statement>? statements = null;
        for (int i = 0; i < block.Statements.Count; i++)
        {
            Statement statement = block.Statements[index: i];
            List<Statement> lowered = [];
            foreach (Expression receiver in ReshapedReceivers(statement: statement))
            {
                if (Call(receiver: receiver, name: RuntimeContract.ShapeUse.RequireFree) is { } check)
                {
                    lowered.Add(item: new ExpressionStatement(Expression: check, Location: statement.Location));
                }
            }

            if (statement is LoopStatement { IteratorName: { } iterator } loop &&
                (loop.ChecksShapeAtRunTime || _registry.RuntimeShapeLoops.Contains(item: loop.Location)) &&
                ContainerOf(iterator: iterator, preceding: block.Statements.Take(count: i)) is { } container &&
                Call(receiver: container, name: RuntimeContract.ShapeUse.Begin) is { } begin)
            {
                var use = new DeclarationStatement(
                    Declaration: new VariableDeclaration(Name: $"__su_{_uses++}",
                        Type: null,
                        Initializer: begin,
                        Visibility: VisibilityModifier.Secret,
                        Location: loop.Location) { OwesTeardown = true },
                    Location: loop.Location);
                lowered.Add(item: new BlockStatement(Statements: [use, loop], Location: loop.Location));
            }
            else if (lowered.Count == 0)
            {
                statements?.Add(item: statement);
                continue;
            }
            else
            {
                lowered.Add(item: statement);
            }

            statements ??= [.. block.Statements.Take(count: i)];
            statements.AddRange(collection: lowered);
        }

        return statements == null ? block : block with { Statements = statements };
    }

    /// <summary>The container a loop walks: the receiver of the <c>emitter()</c> call its iterator was made from, when
    /// that is a name or a member variable path.</summary>
    private static Expression? ContainerOf(string iterator, IEnumerable<Statement> preceding)
    {
        foreach (Statement statement in preceding.Reverse())
        {
            if (statement is DeclarationStatement
                {
                    Declaration: VariableDeclaration
                    {
                        Initializer: CallExpression { Callee: MemberExpression { Object: var source } }
                    } declared
                } && declared.Name == iterator)
            {
                return IsPlainPath(expr: source) ? source : null;
            }
        }

        return null;
    }

    /// <summary>The receivers the statement's own calls of <c>@reshaping</c> routines change, when each is a plain name
    /// or member variable path (evaluated again by the check without effect). Calls inside a nested block, lambda or
    /// conditional belong to their own statement.</summary>
    private static List<Expression> ReshapedReceivers(Statement statement)
    {
        var found = new List<Expression>();
        IEnumerable<Expression> roots = statement switch
        {
            ExpressionStatement e => [e.Expression],
            AssignmentStatement a => [a.Target, a.Value],
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } } => [init],
            ReturnStatement { Value: { } v } => [v],
            DropStatement d => [d.Expression],
            _ => []
        };
        foreach (Expression root in roots)
        {
            AstWalker.WalkExpressions(root: root,
                visit: e =>
                {
                    if (e is CallExpression
                        {
                            ResolvedRoutine.IsReshaping: true, Callee: MemberExpression { Object: var receiver }
                        } && IsPlainPath(expr: receiver) && receiver.ResolvedType is not null)
                    {
                        found.Add(item: receiver);
                    }
                });
        }

        return found;
    }

    private static bool IsPlainPath(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression => true,
            MemberExpression { TakesLink: false } member => IsPlainPath(expr: member.Object),
            _ => false
        };
    }

    /// <summary>
    /// <c>receiver.name()</c> on the container a plain path names: a token is a way to the entity, so the routine is
    /// the entity's own (<c>T.begin_shape_use</c>, ShapeUse.rvr). Null when the container is not an entity.
    /// </summary>
    private CallExpression? Call(Expression receiver, string name)
    {
        TypeSymbol? type = receiver.ResolvedType;
        if (type?.BareName is RuntimeContract.Viewing or RuntimeContract.Modifying && type.TypeArguments is [{ } inner])
        {
            type = inner;
        }

        if (type is not EntityTypeSymbol entity ||
            _registry.LookupMemberRoutineOverload(type: entity, memberRoutineName: name, argTypes: []) is not { } routine)
        {
            return null;
        }

        Expression copy = receiver with { };
        return new CallExpression(
            Callee: new MemberExpression(Object: copy, MemberName: name, Location: receiver.Location)
            {
                ResolvedType = routine.ReturnType
            },
            Arguments: [],
            Location: receiver.Location)
        {
            ResolvedRoutine = routine,
            ResolvedType = routine.ReturnType,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: routine),
            IsSynthesizedLowering = true
        };
    }
}
