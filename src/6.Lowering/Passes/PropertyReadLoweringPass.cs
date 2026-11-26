using Builder.Declaration;
using Builder.Instantiation;
using SyntaxTree;

namespace Builder.Lowering.Passes;

/// <summary>
/// Puts the call analysis made for a read of a <c>@property</c> (<see cref="MemberExpression.PropertyCall"/>) where
/// the read was: <c>foe.alive</c> becomes <c>foe.alive()</c>. It runs first, so every later pass sees an ordinary
/// call of the routine.
/// </summary>
internal sealed class PropertyReadLoweringPass(TypeRegistry registry, Dictionary<string, Statement>? variantBodies)
    : AstRewriter
{
    internal PropertyReadLoweringPass(PostprocessingContext ctx) : this(registry: ctx.Registry,
        variantBodies: ctx.VariantBodies)
    {
    }

    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(registry: registry, program: program, lower: r => VisitStatement(stmt: r.Body));
    }

    public void RunOnVariantBodies()
    {
        if (variantBodies == null)
        {
            return;
        }

        BodyDispatch.RunOnVariantBodies(registry: registry, bodies: variantBodies,
            lower: (_, body) => VisitStatement(stmt: body));
    }

    public void RunOnInstantiatedGenericBodies(Dictionary<string, MonomorphizedBody> bodies)
    {
        BodyDispatch.RunOnInstantiatedGenericBodies(registry: registry, bodies: bodies,
            lower: (_, entry) => VisitStatement(stmt: entry.Ast.Body));
    }

    protected override Expression VisitMember(MemberExpression e)
    {
        return e.PropertyCall is { } call
            ? VisitExpression(expr: call)
            : base.VisitMember(e: e);
    }
}
