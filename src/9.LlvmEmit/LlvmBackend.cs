using Builder.Backends;

namespace Builder.LlvmEmit;

/// <summary>The LLVM emitter as a backend: writes the Phase 9 program as LLVM IR text.</summary>
public sealed class LlvmBackend : IBuilderBackend
{
    /// <inheritdoc/>
    public string Name => BuilderBackends.DefaultName;

    /// <inheritdoc/>
    public bool SupportsResidentBase => true;

    /// <inheritdoc/>
    public bool SupportsLazyJit => true;

    /// <inheritdoc/>
    public ResidentBase EmitResidentBase(BackendInput input)
    {
        // Non-pruned: no live set, so every instance the seed materialized is in the base, and every one of them
        // is resident for the delta's collector.
        var emitter = new LlvmEmitter(userPrograms: [],
            registry: input.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = input.StdlibPrograms,
                SynthesizedBodies = input.SynthesizedBodies,
                InstantiatedGenericBodies = input.InstantiatedGenericBodies,
                // The delta's mode and target: the mode decides whether the base defines the shared trace globals
                // the delta references, and the target fixes the triple and data layout the delta links against.
                BuildMode = input.BuildMode,
                Target = input.Target
            });
        (string ir, IReadOnlyCollection<string> symbols) = emitter.GenerateBase();
        return new ResidentBase(LlvmIr: ir, Symbols: symbols,
            InstanceKeys: new HashSet<string>(collection: input.InstantiatedGenericBodies?.Keys ?? [],
                comparer: StringComparer.Ordinal));
    }

    /// <inheritdoc/>
    public BackendOutput Emit(BackendInput input)
    {
        var emitter = new LlvmEmitter(userPrograms: input.UserPrograms,
            registry: input.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = input.StdlibPrograms,
                Target = input.Target,
                BuildMode = input.BuildMode,
                SynthesizedBodies = input.SynthesizedBodies,
                InstantiatedGenericBodies = input.InstantiatedGenericBodies,
                LiveRoutineKeys = input.LiveRoutineKeys,
                MaySuspendRoutineKeys = input.MaySuspendRoutineKeys,
                ResidentSymbols = input.ResidentSymbols
            }) { Timing = input.Timing, EntryModule = input.EntryModule };
        string ir = emitter.Generate();
        return new BackendOutput(LlvmIr: ir, DefinedRoutineSymbols: emitter.GetEmittedRoutineSymbols());
    }
}
