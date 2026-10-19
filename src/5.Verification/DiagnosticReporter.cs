using Builder.Diagnostics;
using SyntaxTree;

namespace Builder.Verification;

/// <summary>
/// Reports one semantic error. The checks that stand on their own (outside <see cref="SemanticVerifier"/>)
/// take one of these instead of the verifier, so they depend only on what they report.
/// </summary>
internal delegate void DiagnosticReporter(SemanticDiagnosticCode code, string message, SourceLocation location);

/// <summary>Access paths as the checks spell them: <c>a</c>, <c>a.b</c>, <c>a[]</c> (any element of a).</summary>
internal static class AccessPaths
{
    /// <summary>True when <paramref name="path"/> is <paramref name="prefix"/> or lies under it (a field or an
    /// element of it).</summary>
    internal static bool IsPrefixOrEqual(string prefix, string path)
    {
        return path == prefix || path.StartsWith(value: prefix + ".") ||
               path.StartsWith(value: prefix + "[");
    }

    /// <summary>The dotted path of an identifier / member chain (<c>a</c>, <c>a.b</c>), or null for anything else (an
    /// index, a call).</summary>
    internal static string? Of(Expression expr)
    {
        return expr switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression { Object: var inner, MemberName: var member } => Of(expr: inner) is { } prefix
                ? $"{prefix}.{member}"
                : null,
            _ => null
        };
    }
}
