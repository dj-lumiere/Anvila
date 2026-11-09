using Builder.Declaration;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// The calls the builder writes where a link (<c>Modifying[Node]?</c> / <c>Viewing[Node]?</c> kept in a place) is used
/// without taking it by <c>when</c>: <c>node.parent.id</c>, <c>node.parent.rename()</c>, and the same under <c>try</c>.
/// The token is taken first (Link.rvr), which fails with <c>StaleLinkError</c> once the entity ended (with
/// <c>AbsentValueError</c> for an empty link). The token keeps the link's generation, so a use the builder checks
/// fails once the entity ended.
/// </summary>
internal static class LinkTaking
{
    /// <summary>The name of the local that holds a token taken from a link.</summary>
    public const string TokenPrefix = "__lk_";

    /// <summary>
    /// <c>take_modifying_link(link: ...)</c> (or the <c>Viewing</c> one) on <paramref name="link"/>, or null when the
    /// value is no link or the routine is not there.
    /// </summary>
    public static CallExpression? Take(Expression link, TypeRegistry registry)
    {
        TypeSymbol? linkType = link.ResolvedType;
        bool throughReadToken = linkType?.BareName == RuntimeContract.Viewing;
        // A token on a link (a `for` element over a list of links) is read as the link it is on.
        if (ReadTokenOnLink(value: link, registry: registry) is { } read)
        {
            link = read;
            linkType = read.ResolvedType;
        }

        if (linkType is not { TypeArguments: [{ } token] } || token.TypeArguments is not [{ } entity])
        {
            return null;
        }

        // Through a read token on a write link, the token taken is a read token.
        if (throughReadToken && token.BareName == RuntimeContract.Modifying &&
            registry.LookupType(name: RuntimeContract.OptionalCarrier) is { } optionalDefinition)
        {
            token = registry.GetOrCreateWrapperType(wrapperName: RuntimeContract.Viewing, innerType: entity, isReadOnly: true);
            linkType = registry.GetOrCreateResolution(genericDef: optionalDefinition, typeArguments: [token]);
        }

        string name = token.BareName == RuntimeContract.Viewing
            ? RuntimeContract.TakeViewingLink
            : RuntimeContract.TakeModifyingLink;
        if (registry.LookupGenericOverload(name: name, module: "System") is not { } definition)
        {
            return null;
        }

        RoutineInfo instance = registry.GetOrCreateRoutineResolution(genericDef: definition, typeArguments: [entity]);
        return new CallExpression(
            Callee: new IdentifierExpression(Name: name, Location: link.Location) { ResolvedRoutine = instance },
            Arguments: [new NamedArgumentExpression(Name: "link", Value: link, Location: link.Location) { ResolvedType = linkType }],
            Location: link.Location)
        {
            ResolvedRoutine = instance,
            ResolvedType = token,
            LoweringKind = CallLoweringKind.DirectRoutine,
            IsSynthesizedLowering = true
        };
    }

    /// <summary>The link a token on a link reaches (<c>at.access()</c>, with <c>at</c> a <c>for</c> element over a list of
    /// links), or null for any other value.</summary>
    public static CallExpression? ReadTokenOnLink(Expression value, TypeRegistry registry)
    {
        if (value.ResolvedType is not { BareName: RuntimeContract.Viewing or RuntimeContract.Modifying, TypeArguments: [{ } held] } tokenOnLink ||
            !ImplicitToken.IsLink(type: held) ||
            registry.LookupMemberRoutine(type: tokenOnLink, memberRoutineName: RuntimeContract.Access) is not { } access)
        {
            return null;
        }

        return new CallExpression(
            Callee: new MemberExpression(Object: value, MemberName: RuntimeContract.Access, Location: value.Location),
            Arguments: [],
            Location: value.Location)
        {
            ResolvedRoutine = access,
            ResolvedType = held,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: access),
            IsSynthesizedLowering = true
        };
    }

    /// <summary>A builder local <c>var name = value</c>. <paramref name="owesTeardown"/> marks one written after scope
    /// teardown ran, so its late run tears it down at every exit of its block.</summary>
    public static DeclarationStatement Local(string name, Expression value, bool owesTeardown = false)
    {
        return new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: name,
                Type: null,
                Initializer: value,
                Visibility: VisibilityModifier.Secret,
                Location: value.Location) { OwesTeardown = owesTeardown },
            Location: value.Location);
    }
}
