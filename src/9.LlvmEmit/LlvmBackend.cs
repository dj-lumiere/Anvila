using Builder.Backends;

namespace Builder.LlvmEmit;

/// <summary>The LLVM emitter as a backend: writes the Phase 9 program as LLVM IR text.</summary>
public sealed class LlvmBackend : IBuilderBackend
{
    /// <inheritdoc/>
    public string Name => BuilderBackends.DefaultName;

    /// <inheritdoc/>
    public bool SupportsResidentJit => true;

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
