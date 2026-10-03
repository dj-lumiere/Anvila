using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Nullability of shared entity references, for a language whose entities are shared handles
/// (<see cref="Frontends.LanguageRules.EntitiesAreShared"/>, Suflae): an entity slot is a <c>Roamed[E]</c>
/// handle, <c>E?</c> may be none, and a possibly-none value must be checked before it reaches a non-null
/// slot. Reads the registry's scope for flow facts and asks <paramref name="analyze"/> for the type of a
/// subexpression it meets unanalyzed.
/// </summary>
internal sealed class SharedEntities(TypeRegistry registry, DiagnosticReporter report,
    Func<Expression, TypeSymbol> analyze)
{
    /// <summary>
    /// Suflae flow typing: determines whether the VALUE produced by reading <paramref name="expr"/> is a
    /// possibly-none (`E?`) entity reference. Sources of none-ness:
    /// <list type="bullet">
    /// <item>the <c>none</c> literal;</item>
    /// <item>a read of a nullable entity field (<c>obj.optField</c> where the field was declared <c>x: E?</c>);</item>
    /// <item>a read of a nullable local that has not yet been proven non-none in this flow.</item>
    /// </list>
    /// Constructions (<c>E(...)</c>), non-null field reads, and routine returns of entity type are non-none.
    /// Only meaningful for Suflae; always false for RazorForge.
    /// </summary>
    internal bool IsNullableRead(Expression expr)
    {
        if (!registry.Rules.EntitiesAreShared)
        {
            return false;
        }

        switch (expr)
        {
            // `none` literal
            case LiteralExpression { LiteralType: TokenType.NoneValue }:
                return true;

            // A nullable local, unless flow analysis has already proven it non-none here.
            case IdentifierExpression id:
                return registry.LookupVariable(name: id.Name) is { IsNullable: true } &&
                       !registry.IsVariableProvenNonNull(name: id.Name);

            // A read of an optional entity field (`obj.optField`). The field's IsNullable is set in
            // TypeBodyResolver. At SA time the object type is the bare EntityTypeSymbol (Roamed lowering
            // is a later phase), so look the field up directly on the entity.
            case MemberExpression m:
            {
                TypeSymbol objType =
                    m.Object.ResolvedType ?? analyze(arg: m.Object);
                return objType is EntityTypeSymbol entity &&
                       entity.LookupMemberVariable(memberVariableName: m.MemberName) is
                           { IsNullable: true };
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Suflae: true if the type is an entity reference (a bare <c>EntityTypeSymbol</c> or a
    /// <c>Roamed[E]</c> handle) — i.e. something that participates in nullability flow analysis.
    /// </summary>
    internal bool IsEntityRef(TypeSymbol type)
    {
        return registry.Rules.EntitiesAreShared && (type is EntityTypeSymbol ||
                                                         type is RecordTypeSymbol
                                                         {
                                                             GenericDefinition.Name:
                                                             Declaration.RuntimeContract.Roamed
                                                         });
    }

    /// <summary>
    /// Suflae: reports a possibly-none value flowing into a non-nullable entity slot (field, variable, or
    /// parameter). The message adapts: a literal <c>none</c> gets the crisp "Cannot assign 'none'" wording,
    /// any other possibly-none value gets "Cannot assign a possibly-none value … null-check it first".
    /// Uses <see cref="Builder.Diagnostics.SemanticDiagnosticCode.AssignmentTypeMismatch"/> (RF-S252),
    /// consistent with the construction/assignment none-checks.
    /// </summary>
    internal void ReportIntoNonNull(string target, Expression value, string optionalHint)
    {
        bool isLiteralNone = value is LiteralExpression { LiteralType: TokenType.NoneValue };
        string message = isLiteralNone
            ? $"Cannot assign 'none' to non-nullable entity {target}. " +
              $"Declare it optional ('{optionalHint}') to allow none."
            : $"Cannot assign a possibly-none value to non-nullable entity {target}. " +
              $"Null-check it first (e.g. 'if v isnot None') or declare it optional ('{optionalHint}').";
        report(code: Diagnostics.SemanticDiagnosticCode.AssignmentTypeMismatch,
            message: message,
            location: value.Location);
    }

    /// <summary>
    /// Suflae: resolves an entity-reference variable/parameter type annotation into its <c>Roamed[E]</c>
    /// storage representation, mirroring the field substitution in <c>TypeBodyResolver</c>. A bare
    /// <c>E</c> annotation is a NON-NULL <c>Roamed[E]</c>; an optional <c>E?</c> (parsed as
    /// <c>Maybe[E]</c>) is a NULLABLE <c>Roamed[E]</c>.
    /// </summary>
    /// <returns>
    /// (resolved storage type, isNullable, isEntitySlot). For non-Suflae or non-entity annotations the
    /// type is returned unchanged with both flags false.
    /// </returns>
    internal (TypeSymbol Type, bool IsNullable, bool IsEntitySlot) ResolveAnnotation(
        TypeSymbol annotated, TypeExpression? typeExpr = null)
    {
        // An `RF::`-qualified annotation opts OUT of the entity->Roamed lowering: ResolveType already
        // honored the realm and left it BARE (a wrapper's `inner: RF::Core.List[T]` holds a bare RF
        // entity, not a re-roamed `Roamed[List]`). Do NOT re-roam it here — this is the SA twin of
        // TypeResolver.ResolveType's `Realm != "RF"` gate; the realm tag lives on the TypeExpression,
        // not on the already-resolved TypeSymbol, so it must be threaded in explicitly.
        if (typeExpr?.Realm == TypeModel.Realms.Shared)
        {
            return (annotated, false, false);
        }

        if (!registry.Rules.EntitiesAreShared ||
            registry.LookupType(name: Declaration.RuntimeContract.Roamed) is not { } roamedDef)
        {
            return (annotated, false, false);
        }

        return annotated switch
        {
            // bare `E` -> non-null Roamed[E]
            EntityTypeSymbol entity => (
                registry.GetOrCreateResolution(genericDef: roamedDef, typeArguments: [entity]),
                false, true),
            // `E?` (= Maybe[E]) -> nullable Roamed[E]
            RecordTypeSymbol
            {
                GenericDefinition.Name: "Maybe", TypeArguments: [EntityTypeSymbol inner]
            } => (
                registry.GetOrCreateResolution(genericDef: roamedDef, typeArguments: [inner]),
                true, true),
            // Already a Roamed[E] (e.g. an annotation that spelled the wrapper directly) — non-null slot.
            RecordTypeSymbol { GenericDefinition.Name: Declaration.RuntimeContract.Roamed } => (
                annotated, false, true),
            _ => (annotated, false, false)
        };
    }
}
