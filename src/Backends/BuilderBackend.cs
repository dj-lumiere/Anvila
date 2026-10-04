using Builder.Declaration;
using Builder.Instantiation;
using Builder.Targeting;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Backends;

/// <summary>
/// The program a backend translates: the verified, lowered and monomorphized ASTs that leave Phase 9, plus
/// the build facts every backend needs. Nothing in it is specific to one backend, and a backend makes no
/// language decision of its own: every such decision is already stamped on these trees.
/// </summary>
public sealed record BackendInput
{
    /// <summary>The user programs in build order, with their file paths and module names.</summary>
    public required List<(Program Program, string FilePath, string Module)> UserPrograms { get; init; }

    /// <summary>The standard library programs the build analyzed.</summary>
    public required List<(Program Program, string FilePath, string Module)> StdlibPrograms { get; init; }

    /// <summary>The type registry the analysis produced.</summary>
    public required TypeRegistry Registry { get; init; }

    /// <summary>The platform the build targets.</summary>
    public required TargetConfig Target { get; init; }

    /// <summary>The optimization mode.</summary>
    public required RfBuildMode BuildMode { get; init; }

    /// <summary>Bodies the builder wrote (derived operators, wired routines), keyed by routine.</summary>
    public IReadOnlyDictionary<string, Statement>? SynthesizedBodies { get; init; }

    /// <summary>The concrete bodies of instantiated generic routines, keyed by routine.</summary>
    public IReadOnlyDictionary<string, MonomorphizedBody>? InstantiatedGenericBodies { get; init; }

    /// <summary>The routines reachable from <c>start</c>; empty emits everything.</summary>
    public IReadOnlyCollection<string>? LiveRoutineKeys { get; init; }

    /// <summary>The routines that may suspend.</summary>
    public IReadOnlyCollection<string>? MaySuspendRoutineKeys { get; init; }

    /// <summary>The module whose <c>start</c> is the program entry.</summary>
    public string? EntryModule { get; init; }

    /// <summary>Symbols an already built base object defines (the resident JIT delta), or null: the
    /// <see cref="ResidentBase.Symbols"/> of the backend's own base.</summary>
    public IReadOnlyCollection<string>? ResidentSymbols { get; init; }

    /// <summary>Whether to print the backend's phase timings.</summary>
    public bool Timing { get; init; }

    /// <summary>Where a backend that writes source of its own (Tessera) keeps a copy of it, or null
    /// (<c>[debug] dump-tessera</c>).</summary>
    public string? SourceDumpPath { get; init; }
}

/// <summary>What a backend produced: the module as LLVM IR text and the routine symbols it defines.</summary>
/// <param name="LlvmIr">The module, as LLVM IR text the native toolchain or the JIT consumes.</param>
/// <param name="DefinedRoutineSymbols">The routine symbols the module defines.</param>
public sealed record BackendOutput(string LlvmIr, IReadOnlyCollection<string> DefinedRoutineSymbols);

/// <summary>
/// The resident base of the compile daemon's dev loop: the standard library a seed program reaches, compiled once
/// to an object every later build links against, so each edit translates and JIT-compiles only its delta.
/// </summary>
/// <param name="LlvmIr">The base module (no <c>main</c>), as LLVM IR text.</param>
/// <param name="Symbols">Everything the base defines that a delta may use, in the form
/// <see cref="BackendInput.ResidentSymbols"/> hands it back to the same backend.</param>
/// <param name="InstanceKeys">The registry keys of the instances the base defines, which a delta's demand collector
/// does not build again.</param>
public sealed record ResidentBase(string LlvmIr, IReadOnlyCollection<string> Symbols,
    IReadOnlySet<string> InstanceKeys);

/// <summary>
/// A backend: translates the Phase 9 program into a module. The LLVM emitter is one; Tessera is the other.
/// </summary>
public interface IBuilderBackend
{
    /// <summary>The name a manifest selects this backend by (<c>[target] backend = "..."</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Whether the backend splits a dev-loop build into the compile daemon's resident base
    /// (<see cref="EmitResidentBase"/>) and a per-edit delta that declares what the base defines
    /// (<see cref="BackendInput.ResidentSymbols"/>). Builds through a backend without it compile the whole program.
    /// </summary>
    bool SupportsResidentBase { get; }

    /// <summary>
    /// Whether the backend serves the in-process lazy JIT, which emits one routine at a time as the running program
    /// reaches it (the incremental path when no daemon is reachable). A backend without it runs that path as one
    /// whole module.
    /// </summary>
    bool SupportsLazyJit { get; }

    /// <summary>Translates <paramref name="input"/> into a module.</summary>
    BackendOutput Emit(BackendInput input);

    /// <summary>Translates the base program of the resident dev loop: every routine <paramref name="input"/> holds,
    /// none of its user programs and no <c>main</c>, each defined for a delta to link to. Only called when
    /// <see cref="SupportsResidentBase"/>.</summary>
    ResidentBase EmitResidentBase(BackendInput input);
}

/// <summary>
/// The backends this builder can use, by name: the LLVM emitter and Tessera are built in, and another backend
/// registers when its project starts, the way front ends register their language rules.
/// </summary>
public static class BuilderBackends
{
    /// <summary>The backend a manifest without <c>[target] backend</c> uses.</summary>
    public const string DefaultName = "llvm";

    private static readonly Dictionary<string, IBuilderBackend> Registered = new(comparer: StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    /// <summary>Registers <paramref name="backend"/> under its name, replacing an earlier one of that name.</summary>
    public static void Register(IBuilderBackend backend)
    {
        lock (Gate)
        {
            Registered[key: backend.Name] = backend;
        }
    }

    /// <summary>The names of the registered backends, sorted.</summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            lock (Gate)
            {
                EnsureDefault();
                return Registered.Keys.Order(comparer: StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>The backend registered as <paramref name="name"/>, or false when there is none.</summary>
    public static bool TryGet(string name, out IBuilderBackend backend)
    {
        lock (Gate)
        {
            EnsureDefault();
            return Registered.TryGetValue(key: name, value: out backend!);
        }
    }

    /// <summary>The backend registered as <paramref name="name"/>; throws, naming the choices, when none is.</summary>
    public static IBuilderBackend Get(string name)
    {
        return TryGet(name: name, backend: out IBuilderBackend backend)
            ? backend
            : throw new InvalidOperationException(
                message: $"There is no backend named '{name}'. Set [target] backend to one of: " +
                         $"{string.Join(separator: ", ", values: Names)}.");
    }

    /// <summary>Registers the backends built into the builder (the LLVM emitter and Tessera) unless a
    /// registration under their name already replaced them.</summary>
    private static void EnsureDefault()
    {
        Registered.TryAdd(key: DefaultName, value: new LlvmEmit.LlvmBackend());
        Registered.TryAdd(key: TesseraEmit.TesseraBackend.BackendName, value: new TesseraEmit.TesseraBackend());
    }
}
