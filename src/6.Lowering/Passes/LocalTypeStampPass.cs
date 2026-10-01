using SyntaxTree;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Phase 9 (backend annotation): stamps each local declaration's storage type
/// (<see cref="VariableDeclaration.LocalType"/>: the declared type, else the initializer's, else the bound
/// routine's return type), and gives a local reference that is still untyped, or typed as an error, the
/// type its declaration settled on. A temporary that a lowering pass introduced while its value was not yet
/// typed (the teardown return spill <c>var __td_ret = EXPR</c> in a template body, whose <c>EXPR</c> gets
/// its concrete type only after monomorphization and operator lowering) keeps the stale type on its
/// references; the declaration's own type, or its initializer's, is the real one. Only missing or error
/// types are filled: a reference that already has a type is never changed.
/// </summary>
internal static class LocalTypeStampPass
{
    /// <summary>Stamps the untyped local references in <paramref name="body"/>.</summary>
    public static void Run(Statement body)
    {
        var declared = new Dictionary<string, TypeSymbol>(comparer: StringComparer.Ordinal);
        AstWalker.Walk(root: body,
            visit: node =>
            {
                switch (node)
                {
                    case DeclarationStatement { Declaration: VariableDeclaration v }:
                        // Always restamped: a copy of a template body carries the template's stamp.
                        v.LocalType = StorageType(declaration: v);
                        if (v.LocalType is { } localType)
                        {
                            declared[key: v.Name] = localType;
                        }

                        break;
                    case IdentifierExpression { ResolvedType: null or ErrorTypeSymbol } id
                        when declared.TryGetValue(key: id.Name, value: out TypeSymbol? type):
                        id.ResolvedType = type;
                        break;
                }
            });
    }

    private static TypeSymbol? StorageType(VariableDeclaration declaration)
    {
        return Concrete(type: declaration.Type?.ResolvedType) ??
               Concrete(type: declaration.Initializer?.ResolvedType) ??
               declaration.Initializer switch
               {
                   CallExpression { ConstructedType: { } constructed } => Concrete(type: constructed),
                   CallExpression { ResolvedRoutine.ReturnType: { } returned } => Concrete(type: returned),
                   _ => null
               };
    }

    private static TypeSymbol? Concrete(TypeSymbol? type)
    {
        return type is null or ErrorTypeSymbol or GenericParameterTypeSymbol
            ? null
            : type;
    }
}
