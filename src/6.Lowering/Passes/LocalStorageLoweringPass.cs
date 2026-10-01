using Builder.Declaration;
using SyntaxTree;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Settles how each local's storage starts, so a backend only carries it out:
/// <list type="bullet">
/// <item>A <c>lateinit var</c> declared without a value gets one: a fresh zero-filled heap block for an entity
/// (so the binding is valid at once and teardown frees a real allocation), the zero value otherwise.</item>
/// <item>A local holding an owned handle (an entity or a reference-counted wrapper) is marked
/// <see cref="VariableDeclaration.StartsZeroed"/>: teardown may reach it on a path that leaves before its
/// declaration runs, and must find null there, not garbage.</item>
/// </list>
/// Runs at Phase 9 over every body that reaches a backend, after <see cref="LocalTypeStampPass"/>.
/// </summary>
internal sealed class LocalStorageLoweringPass : AstRewriter
{
    /// <summary>Settles the local storage of one routine body in place.</summary>
    public static void Run(Statement body)
    {
        // A routine body is a block: its statement list is rewritten in place (the body itself is
        // referenced from several places and cannot be replaced).
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(
                message: $"A routine body must be a block, got {body.GetType().Name}.");
        }

        var pass = new LocalStorageLoweringPass();
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    /// <inheritdoc/>
    protected override Statement VisitDeclarationStatement(DeclarationStatement s)
    {
        Statement visited = base.VisitDeclarationStatement(s: s);
        if (visited is not DeclarationStatement { Declaration: VariableDeclaration { LocalType: { } type } local } statement)
        {
            return visited;
        }

        local.StartsZeroed = type is EntityTypeSymbol || TypeRegistry.GetRcWrapperBaseName(type: type) is not null;
        if (local is not { IsLateInit: true, Initializer: null })
        {
            return visited;
        }

        Expression placeholder = type is EntityTypeSymbol
            ? new EntityAllocationExpression(Location: local.Location) { ResolvedType = type }
            : new ZeroValueExpression(Location: local.Location) { ResolvedType = type };
        return statement with { Declaration = local with { Initializer = placeholder } };
    }
}
