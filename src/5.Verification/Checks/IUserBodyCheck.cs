using SyntaxTree;

namespace Builder.Verification;

/// <summary>
/// A language's own build-time check over analyzed user bodies (RazorForge: token lifetimes). It runs after
/// the bodies are analyzed, so every call is resolved. One instance serves a whole build: the programs seen in
/// earlier calls still count (a routine analyzed in an earlier file lends its summary to later ones).
/// </summary>
internal interface IUserBodyCheck
{
    /// <summary>Checks the user programs analyzed since the last call.</summary>
    void Check(IReadOnlyList<Program> programs);
}
