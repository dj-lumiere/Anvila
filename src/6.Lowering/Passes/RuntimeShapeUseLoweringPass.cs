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
/// to or remove from a container (<c>@reshaping</c>) first checks its receiver (<c>xs.require_shape_free()</c>), which
/// fails with <c>ReshapingWhileInUseError</c> while a use of it is open. RuntimeCheckPass puts those checks into the
/// program's routines before their recovery variants are made, so a <c>try</c> above receives the failure. This pass
/// adds them only where that one did not reach (a lambda's body, lifted here). A program without such a loop is left
/// as it is and pays nothing.
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
            HashSet<string> checkedAlready = CheckedBefore(statements: block.Statements, index: i);
            foreach (Expression receiver in RuntimeCheckPass.ReshapedReceivers(stmt: statement))
            {
                if (RuntimeCheckPass.PathText(expr: receiver) is { } path && checkedAlready.Add(item: path) &&
                    Call(receiver: receiver, name: RuntimeContract.ShapeUse.RequireFree) is { } check)
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

    /// <summary>The containers the checks right before a statement already check (put there by RuntimeCheckPass).</summary>
    private static HashSet<string> CheckedBefore(List<Statement> statements, int index)
    {
        var paths = new HashSet<string>(comparer: StringComparer.Ordinal);
        for (int j = index - 1; j >= 0; j--)
        {
            if (statements[index: j] is not ExpressionStatement
                {
                    Expression: CallExpression
                    {
                        ResolvedRoutine.Name: RuntimeContract.ShapeUse.RequireFree or RuntimeContract.RequireEntityGeneration or
                        RuntimeContract.RequireModifyingLive or RuntimeContract.RequireViewingLive
                    } check
                })
            {
                break;
            }

            if (check is { ResolvedRoutine.Name: RuntimeContract.ShapeUse.RequireFree, Callee: MemberExpression { Object: var receiver } } &&
                RuntimeCheckPass.PathText(expr: receiver) is { } path)
            {
                paths.Add(item: path);
            }
        }

        return paths;
    }

    private static bool IsPlainPath(Expression expr)
    {
        return RuntimeCheckPass.PathText(expr: expr) != null;
    }

    /// <summary><c>receiver.name()</c> on the container a plain path names, or null when it is not an entity.</summary>
    private CallExpression? Call(Expression receiver, string name)
    {
        return RuntimeCheckPass.MemberCall(receiver: receiver, name: name, registry: _registry,
            location: receiver.Location);
    }
}
