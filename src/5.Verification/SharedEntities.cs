using Builder.Declaration;
using SyntaxTree;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Shared entity references, for a language whose entities are shared handles
/// (<see cref="Frontends.LanguageRules.EntitiesAreShared"/>, Mielia): an entity slot is a <c>Roamed[E]</c>
/// handle and an <c>E?</c> slot is <c>Optional[Roamed[E]]</c>.
/// </summary>
internal sealed class SharedEntities(TypeRegistry registry)
{
    /// <summary>
    /// Mielia: true if the type is an entity reference (a bare <c>EntityTypeSymbol</c> or a
    /// <c>Roamed[E]</c> handle).
    /// </summary>
    internal bool IsEntityRef(TypeSymbol type)
    {
        return registry.EntitiesAreShared && (type is EntityTypeSymbol ||
                                                         type is RecordTypeSymbol
                                                         {
                                                             GenericDefinition.Name:
                                                             Declaration.RuntimeContract.Roamed
                                                         });
    }

    /// <summary>
    /// Mielia: true if the type is an optional entity reference, an <c>E?</c> (<c>Optional[Roamed[E]]</c>).
    /// </summary>
    internal bool IsOptionalEntityRef(TypeSymbol type)
    {
        return registry.EntitiesAreShared &&
               type is RecordTypeSymbol { GenericDefinition.Name: "Optional", TypeArguments: [{ } held] } &&
               IsEntityRef(type: held);
    }

    /// <summary>
    /// The type an argument stands for when a generic parameter spelled <c>Roamed[T]</c> is inferred from it.
    /// Where entities are shared, an entity value IS its <c>Roamed[E]</c> handle (the handle type is given to
    /// it only by a later lowering), so <c>T</c> binds to <c>E</c>. Any other argument is returned unchanged.
    /// </summary>
    internal TypeSymbol AsHandleForInference(TypeSymbol argType, TypeSymbol paramType)
    {
        if (paramType is not RecordTypeSymbol { GenericDefinition.Name: Declaration.RuntimeContract.Roamed } ||
            argType is not EntityTypeSymbol)
        {
            return argType;
        }

        return ResolveAnnotation(annotated: argType);
    }

    /// <summary>
    /// Mielia: resolves an entity-reference variable/parameter type annotation into its <c>Roamed[E]</c>
    /// storage representation, mirroring the field substitution in <c>TypeBodyResolver</c>. A bare
    /// <c>E</c> annotation is a <c>Roamed[E]</c>, an optional <c>E?</c> (parsed as <c>Optional[E]</c>) is
    /// <c>Optional[Roamed[E]]</c>. For non-Mielia or non-entity annotations the type is returned unchanged.
    /// </summary>
    internal TypeSymbol ResolveAnnotation(TypeSymbol annotated, TypeExpression? typeExpr = null)
    {
        // An `RV::`-qualified annotation opts OUT of the entity->Roamed lowering: ResolveType already
        // honored the realm and left it BARE (a wrapper's `inner: RV::System.List[T]` holds a bare RV
        // entity, not a re-roamed `Roamed[List]`). Do NOT re-roam it here — this is the SA twin of
        // TypeResolver.ResolveType's `Realm != "RV"` gate. The realm tag lives on the TypeExpression,
        // not on the already-resolved TypeSymbol, so it must be threaded in explicitly.
        if (typeExpr?.Realm == TypeModel.Realms.Shared || !registry.EntitiesAreShared ||
            registry.LookupType(name: Declaration.RuntimeContract.Roamed) is not { } roamedDef)
        {
            return annotated;
        }

        return annotated switch
        {
            EntityTypeSymbol entity => registry.GetOrCreateResolution(genericDef: roamedDef, typeArguments: [entity]),
            RecordTypeSymbol
            {
                GenericDefinition: { Name: "Optional" } optional, TypeArguments: [EntityTypeSymbol inner]
            } => registry.GetOrCreateResolution(genericDef: optional,
                typeArguments: [registry.GetOrCreateResolution(genericDef: roamedDef, typeArguments: [inner])]),
            _ => annotated
        };
    }
}
