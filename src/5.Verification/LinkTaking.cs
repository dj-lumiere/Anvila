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
/// <c>AbsentValueError</c> for an empty link), and a <c>begin_link_use()</c> keeps the entity's end waiting while the
/// token is used.
/// </summary>
internal static class LinkTaking
{
    /// <summary>The name of the local that holds a token taken from a link.</summary>
    public const string TokenPrefix = "__lk_";

    /// <summary>The name of the local that keeps a token taken from a link in use.</summary>
    public const string UsePrefix = "__lu_";

    /// <summary>
    /// <c>take_modifying_link(link: ...)</c> (or the <c>Viewing</c> one) on <paramref name="link"/>, or null when the
    /// value is no link or the routine is not there.
    /// </summary>
    public static CallExpression? Take(Expression link, TypeRegistry registry)
    {
        if (link.ResolvedType is not { TypeArguments: [{ } token] } linkType ||
            token.TypeArguments is not [{ } entity])
        {
            return null;
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

    /// <summary><c>token.begin_link_use()</c> on the local <paramref name="tokenName"/>, or null when the token type has
    /// no such routine.</summary>
    public static CallExpression? BeginUse(string tokenName, TypeSymbol token, SourceLocation location,
        TypeRegistry registry)
    {
        if (registry.LookupMemberRoutineOverload(type: token, memberRoutineName: RuntimeContract.LinkUseBegin, argTypes: []) is
            not { } begin)
        {
            return null;
        }

        return new CallExpression(
            Callee: new MemberExpression(Object: new IdentifierExpression(Name: tokenName, Location: location)
                {
                    ResolvedType = token
                },
                MemberName: RuntimeContract.LinkUseBegin,
                Location: location) { ResolvedType = begin.ReturnType },
            Arguments: [],
            Location: location)
        {
            ResolvedRoutine = begin,
            ResolvedType = begin.ReturnType,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: begin),
            IsSynthesizedLowering = true
        };
    }

    /// <summary>A builder local <c>var name = value</c>.</summary>
    public static DeclarationStatement Local(string name, Expression value)
    {
        return new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: name,
                Type: null,
                Initializer: value,
                Visibility: VisibilityModifier.Secret,
                Location: value.Location),
            Location: value.Location);
    }
}
