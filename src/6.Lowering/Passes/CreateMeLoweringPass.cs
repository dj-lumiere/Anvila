using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Gives an entity's <c>create</c> that works on <c>me</c> (<c>me.field = …</c>, <c>return me</c>) the entity it
/// builds: <c>var me = #allocate(E)</c> at the top of the body, a fresh heap block with every field zero. A
/// <c>create</c> that never names <c>me</c> (the canonical <c>return E(field: …)</c>) allocates nothing extra.
/// Runs at Phase 9 over every body that reaches a backend, with the routine whose body it is.
/// </summary>
internal static class CreateMeLoweringPass
{
    private const string Me = "me";

    /// <summary>Allocates <c>me</c> at the top of <paramref name="body"/> when it is an entity creator naming it.</summary>
    public static void Run(Statement body, RoutineInfo? routine)
    {
        if (routine is not { Kind: RoutineKind.Creator, OwnerType: EntityTypeSymbol entity } ||
            body is not BlockStatement block || !NamesMe(body: body))
        {
            return;
        }

        var declaration = new VariableDeclaration(Name: Me,
            Type: null,
            Initializer: new EntityAllocationExpression(Location: block.Location) { ResolvedType = entity },
            Visibility: VisibilityModifier.Open,
            Location: block.Location) { LocalType = entity };
        block.Statements.Insert(index: 0, item: new DeclarationStatement(Declaration: declaration, Location: block.Location));
    }

    private static bool NamesMe(Statement body)
    {
        bool found = false;
        AstWalker.WalkExpressions(root: body,
            visit: expression => found |= expression is IdentifierExpression { Name: Me });
        return found;
    }
}
