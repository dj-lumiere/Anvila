using SyntaxTree;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Phase 9 (backend annotation): gives a local reference that is still untyped, or typed as an error, the
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
                    case DeclarationStatement { Declaration: VariableDeclaration v }
                        when (Concrete(type: v.Type?.ResolvedType) ?? Concrete(type: v.Initializer?.ResolvedType)) is
                            { } localType:
                        declared[key: v.Name] = localType;
                        break;
                    case IdentifierExpression { ResolvedType: null or ErrorTypeSymbol } id
                        when declared.TryGetValue(key: id.Name, value: out TypeSymbol? type):
                        id.ResolvedType = type;
                        break;
                }
            });
    }

    private static TypeSymbol? Concrete(TypeSymbol? type)
    {
        return type is null or ErrorTypeSymbol or GenericParameterTypeSymbol
            ? null
            : type;
    }
}
