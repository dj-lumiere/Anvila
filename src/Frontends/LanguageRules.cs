using Builder.Desugaring;
using Builder.Lowering;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Frontends;

/// <summary>
/// Everything the builder core needs to know about one language: its name, file extension and lexer,
/// the surface it offers, the checks it enforces, and the passes only it runs. Each language's front end
/// (the RazorForge and Suflae projects) supplies one and registers it with <see cref="Languages"/> at
/// startup, so the core asks "does this language have module globals?" instead of "is this Suflae?".
/// </summary>
public abstract class LanguageRules
{
    // ═══════════════════════════════════════════════════════════════════════════
    // IDENTITY
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>The language these rules describe.</summary>
    public abstract Language Language { get; }

    /// <summary>The language's display name (<c>RazorForge</c>).</summary>
    public abstract string Name { get; }

    /// <summary>The command name of the language's tool (<c>razorforge</c>).</summary>
    public abstract string ToolName { get; }

    /// <summary>The version line the tool reports.</summary>
    public abstract string Version { get; }

    /// <summary>The source file extension, with its dot (<c>.rf</c>).</summary>
    public abstract string FileExtension { get; }

    /// <summary>The language's short name (<c>RF</c>): the prefix its diagnostic codes are spelled with,
    /// and the realm its standard library types are registered under.</summary>
    public abstract string ShortName { get; }

    /// <summary>The tokens of <paramref name="source"/>, read from <paramref name="fileName"/>.</summary>
    public abstract List<Token> Tokenize(string source, string fileName);

    /// <summary>
    /// Tokenizes and also returns every comment with its position: the token stream drops `#` comments, and a
    /// tool that reproduces the source (the formatter) needs them back.
    /// </summary>
    public abstract (List<Token> Tokens, List<CommentTrivia> Comments) TokenizeWithComments(string source,
        string fileName);

    /// <summary>The language's own Language Server profile (its keywords, names, and formatter).</summary>
    public abstract LanguageServerProfile LanguageServer { get; }

    /// <summary>True when a bare source file given to the tool runs it (build and execute) rather than
    /// printing its syntax tree.</summary>
    public virtual bool BareSourceRuns => false;

    /// <summary>True when the language reads standard library sources of its own (under
    /// <c>Standard/&lt;Name&gt;</c>) on top of the RazorForge standard library every build reads.</summary>
    public virtual bool HasOwnStandardLibrary => false;

    /// <summary>The imports every user file of the language gets without writing them: (module, the
    /// specific symbols it brings in, or null for the whole module).</summary>
    public virtual IReadOnlyList<(string Module, IReadOnlyList<string>? Symbols)> PreludeImports => [];

    // ═══════════════════════════════════════════════════════════════════════════
    // SURFACE
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Memory-unsafe surface: <c>dangerous</c> routines, <c>danger</c> blocks, and external (C)
    /// declarations.</summary>
    public abstract bool AllowsUnsafeCode { get; }

    /// <summary>The <c>pass</c> placeholder statement and declaration.</summary>
    public abstract bool HasPassStatement { get; }

    /// <summary><c>threaded</c> routines.</summary>
    public abstract bool HasThreadedRoutines { get; }

    /// <summary>Module-level mutable state (<c>global name: Type = init</c>).</summary>
    public abstract bool HasModuleGlobals { get; }

    /// <summary>A file may open with a <c>#@target(...)</c> directive that limits it to some targets.</summary>
    public abstract bool HasTargetDirectives { get; }

    /// <summary>An unsuffixed integer literal with no type from context is an <c>Integer</c> and an
    /// unsuffixed decimal one a <c>Decimal</c> (arbitrary precision), rather than <c>S64</c> / <c>B64</c>.</summary>
    public abstract bool DefaultsToArbitraryPrecision { get; }

    /// <summary>The <c>data_size</c> builder query.</summary>
    public abstract bool HasDataSizeQuery { get; }

    // ═══════════════════════════════════════════════════════════════════════════
    // OWNERSHIP AND ENTITIES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An entity is a shared, reference-counted handle (<c>Roamed[E]</c>) that may be none, rather than a
    /// value with a single owner. Entity slots in the language's own files become <c>Roamed[E]</c>, a
    /// bare entity and its handle convert into each other, and an optional entity (<c>E?</c>) is a handle
    /// that may be none, checked with <c>is None</c>.
    /// </summary>
    public abstract bool EntitiesAreShared { get; }

    /// <summary>
    /// Single ownership is checked at build time: a kept entity is not copied without <c>steal</c>,
    /// a wrapper is not copied implicitly, a lambda names its captures in <c>given</c>, a task's
    /// arguments are moved in, a controller is not rewrapped, and a container is not reshaped while it
    /// is being looped over.
    /// </summary>
    public abstract bool ChecksOwnership { get; }

    /// <summary>Scoped access tokens (<c>Viewing</c>, <c>Modifying</c>, …) are checked: they stay inline,
    /// are not stored, returned or duplicated, and their source is not stolen while they live.</summary>
    public abstract bool ChecksAccessTokens { get; }

    /// <summary><c>@readonly</c> member routines are checked not to change <c>me</c>.</summary>
    public abstract bool ChecksReadonly { get; }

    /// <summary>A container changed in shape while it is being looped over crashes at run time (the
    /// language's containers are shared handles the build cannot follow).</summary>
    public abstract bool ChecksShapeAtRunTime { get; }

    /// <summary>
    /// The type as the language's user knows it, for anything shown to them (hover, completion, hints). A
    /// language whose entities are shared handles hides the handle type the builder wraps them in.
    /// </summary>
    public virtual TypeModel.Types.TypeSymbol SurfaceType(TypeModel.Types.TypeSymbol type)
    {
        return type;
    }

    /// <summary>
    /// Text shown to the language's user (a diagnostic message, a signature) with every builder-internal
    /// type name rewritten to the one the user knows, the text-level counterpart of <see cref="SurfaceType"/>
    /// for messages that were rendered before the language was known.
    /// </summary>
    public virtual string SurfaceText(string text)
    {
        return text;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DECLARATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary><c>var x: T</c> without an initializer is rejected (<c>lateinit var</c> defers it).</summary>
    public abstract bool RequiresLateinitForDeferredInit { get; }

    /// <summary>A lambda parameter with no annotation and no typed target to infer from is rejected.</summary>
    public abstract bool RequiresInferableLambdaParameters { get; }

    /// <summary>A <c>using</c> target must obey <c>Enterable</c>.</summary>
    public abstract bool RequiresEnterableForUsing { get; }

    /// <summary>A display routine (<c>show</c>, <c>alert</c>, …) given a wrapped value picks the wrapper
    /// overload, so the wrapper is not copied into the generic one.</summary>
    public abstract bool RewritesDisplayWrapperArguments { get; }

    /// <summary>The advice given when a routine reads a script's top-level variable.</summary>
    public abstract string ScriptVariableAdvice(string name);

    // ═══════════════════════════════════════════════════════════════════════════
    // PASSES ONLY THIS LANGUAGE RUNS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Synthesizes declarations the language adds to its programs before analysis (Suflae:
    /// the module-globals entity). Returns false when a reported error stops the build.</summary>
    public virtual bool SynthesizeBeforeAnalysis(List<(Program Program, string FilePath)> files,
        Action<Diagnostics.SemanticDiagnosticCode, string, SourceLocation> report)
    {
        return true;
    }

    /// <summary>Adds the standard library declarations the language needs on top of the scanned
    /// <paramref name="programs"/> (program, file path, module) before they register (Suflae: forwarders
    /// onto its wrapper types).</summary>
    public virtual void SynthesizeStandardLibrary(
        IReadOnlyList<(Program Program, string FilePath, string Module)> programs)
    {
    }

    /// <summary>Lowers the language's entity model in a user program right after analysis.</summary>
    public virtual void LowerEntities(Program program, Declaration.TypeRegistry registry)
    {
    }

    /// <summary>Lowers the language's own constructs first in the type-aware lowering of
    /// <paramref name="program"/> (Suflae: global reads and writes become singleton field accesses).</summary>
    public virtual void LowerFirst(PostprocessingContext ctx, Program program)
    {
    }

    /// <summary>Runs <see cref="LowerFirst"/> over the synthesized recovery-variant bodies.</summary>
    public virtual void LowerFirstInVariantBodies(PostprocessingContext ctx)
    {
    }

    /// <summary>Lowers the run-time shape checks of each-loops, after operators are lowered.</summary>
    public virtual void LowerShapeUse(PostprocessingContext ctx, Program program)
    {
    }

    /// <summary>Makes the language's own check over analyzed user bodies (RazorForge: token lifetimes), or null
    /// for a language without one. Called once per build.</summary>
    internal virtual Verification.IUserBodyCheck? CreateUserBodyCheck(Verification.DiagnosticReporter report)
    {
        return null;
    }
}
