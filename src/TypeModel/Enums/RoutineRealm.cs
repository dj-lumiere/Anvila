namespace TypeModel.Enums;

/// <summary>
/// The realm a routine's implementation lives in — the world its body comes from. Exactly four:
/// the two native languages (a routine written in Revare or Mielia source) and the two foreign
/// realms reached via the <c>C::</c> / <c>LLVM::</c> qualifier (no body; linked/emitted externally).
/// A routine is callable bare only from its own native realm; a foreign routine MUST be called with its
/// realm qualifier (<c>C::name(...)</c> / <c>LLVM::name(...)</c>).
/// </summary>
public enum RoutineRealm
{
    /// <summary>Native Revare routine (a body defined in a <c>.rvr</c> source file).</summary>
    RV,

    /// <summary>Native Mielia routine (a body defined in a <c>.mie</c> source file).</summary>
    ME,

    /// <summary>Foreign C function, declared <c>routine C::name(...)</c> — no body, C ABI linkage.</summary>
    C,

    /// <summary>Foreign LLVM intrinsic, declared <c>routine LLVM::name(...)</c> — no body.</summary>
    LLVM
}
