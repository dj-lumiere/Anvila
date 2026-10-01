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

    /// <summary>Symbols an already built base object defines (the resident JIT delta), or null.</summary>
    public IReadOnlyCollection<string>? ResidentSymbols { get; init; }

    /// <summary>Whether to print the backend's phase timings.</summary>
    public bool Timing { get; init; }
}

/// <summary>What a backend produced: the module as LLVM IR text and the routine symbols it defines.</summary>
/// <param name="LlvmIr">The module, as LLVM IR text the native toolchain or the JIT consumes.</param>
/// <param name="DefinedRoutineSymbols">The routine symbols the module defines.</param>
public sealed record BackendOutput(string LlvmIr, IReadOnlyCollection<string> DefinedRoutineSymbols);

/// <summary>
/// A backend: translates the Phase 9 program into a module. The LLVM emitter is one; Tessera is the other.
/// </summary>
public interface IBuilderBackend
{
    /// <summary>The name a manifest selects this backend by (<c>[target] backend = "..."</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Whether the backend serves the resident dev loop (the compile daemon's base/delta layers and the lazy
    /// per-routine JIT). Builds through a backend without it always compile the whole program.
    /// </summary>
    bool SupportsResidentJit { get; }

    /// <summary>Translates <paramref name="input"/> into a module.</summary>
    BackendOutput Emit(BackendInput input);
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
