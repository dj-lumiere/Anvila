using SyntaxTree;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// The calls on a <c>Guarded</c> whose access the builder takes around them: <c>g.view()</c> on a <c>MultiRead</c>
/// handle and <c>g.modify()</c> on an <c>Exclusive</c> or <c>MultiRead</c> one, passed as a call argument. The check
/// that they are only passed (GuardAccessArgumentChecker) and the lowering that takes the access for that call
/// (AccessLoweringPass) both ask here, so they agree on what such a call is.
/// </summary>
internal static class GuardAccess
{
    /// <summary>The <c>Guarded</c> a <c>view()</c> / <c>modify()</c> call takes the access of, or null for any other
    /// call (one on a <c>ReadOnly</c> handle takes none). <paramref name="isWrite"/> is true for <c>modify()</c>.</summary>
    internal static Expression? LockingReceiver(Expression expression, out bool isWrite)
    {
        isWrite = false;
        if (expression is not CallExpression
            {
                Callee: MemberExpression { Object: { } receiver, MemberName: var name }, Arguments.Count: 0
            } call ||
            // The token call the builder wrote once the access is taken around it (AccessLoweringPass).
            call.IsSynthesizedLowering ||
            name is not (RuntimeContract.AccessBlock.View or RuntimeContract.AccessBlock.Modify) ||
            call.ResolvedRoutine?.OwnerType?.BareName != RuntimeContract.Guarded ||
            receiver.ResolvedType is not { } type ||
            !TakesAccess(type: type))
        {
            return null;
        }

        isWrite = name == RuntimeContract.AccessBlock.Modify;
        return receiver;
    }

    /// <summary>The access policy of a <c>Guarded[T, TPolicy]</c> (<c>ReadOnly</c>, <c>Exclusive</c>, <c>MultiRead</c>),
    /// or null for any other type and for a policy still generic.</summary>
    internal static string? Policy(TypeSymbol type)
    {
        return type.BareName == RuntimeContract.Guarded &&
               type.TypeArguments is [_, { } policy] && policy is not GenericParameterTypeSymbol
            ? policy.BareName
            : null;
    }

    /// <summary>Whether <paramref name="type"/> is a <c>Guarded</c> whose access is taken: one with an <c>Exclusive</c>
    /// or <c>MultiRead</c> policy, or with a policy still generic (the standard library's routines then take nothing
    /// for a <c>ReadOnly</c> one when the program runs).</summary>
    internal static bool TakesAccess(TypeSymbol type)
    {
        return type.BareName == RuntimeContract.Guarded && Policy(type: type) != "ReadOnly";
    }
}
