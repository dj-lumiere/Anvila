namespace TypeModel;

/// <summary>
/// Realms name the standard-library world-lines that coexist in one build. Every other realm is a language's short
/// name (<see cref="Builder.Frontends.LanguageRules.ShortName"/>), taken from the language a file is written in. The
/// shared realm is the one realm the builder core itself must know: it holds the standard library every build
/// shares, and it is the realm of every type and lookup that no other realm claims.
/// </summary>
public static class Realms
{
    /// <summary>The shared standard library's realm, RazorForge's (<c>RF</c>): what a source writes as <c>RF::</c>
    /// to reach it from another realm.</summary>
    public const string Shared = "RF";
}
