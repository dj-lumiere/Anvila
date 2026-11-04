using Builder.Targeting;
using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    /// <summary>
    /// Whether the build may leave out what only matters when an entity is reachable from two tasks: the access lock
    /// around every entity access, the escaped check, the promotion where a handle crosses. Set by a build that
    /// compiles the whole program in one go. A build that shares compiled standard library parts with other programs
    /// (the resident-JIT base, a warm daemon) keeps them, since those parts must serve a program with Work too.
    /// </summary>
    public bool PrunesTaskChecks { get; set; }

    /// <summary>
    /// Decides <see cref="TypeRegistry.TasksCross"/> once, before any lowering: a Suflae program whose own routines
    /// create no Work, send on no channel and use no mailbox runs on one task, so no entity is reachable from two.
    /// The standard library creates no Work of its own.
    /// </summary>
    private void DecideTasksCross()
    {
        _registry.SharingCheck = _buildMode == RfBuildMode.Debug;
        if (!PrunesTaskChecks || !_registry.CompilationRules.EntitiesAreShared)
        {
            return;
        }

        bool crosses = false;
        foreach ((Program program, _, _) in _registry.UserPrograms)
        {
            AstWalker.WalkExpressions(root: program,
                visit: e => crosses |= e is CallExpression { ResolvedRoutine: { } routine } &&
                                       (routine.AsyncSpawnOf != null || CrossesTasks(routine: routine)));
            if (crosses)
            {
                break;
            }
        }

        _registry.TasksCross = crosses;
    }

    private static bool CrossesTasks(RoutineInfo routine)
    {
        string? owner = routine.OwnerType is { } type
            ? TypeRegistry.GetRcWrapperBaseName(type: type) ?? type.BareName
            : null;
        return owner is "Work" or "Mailbox" or RuntimeContract.ChannelSend.Transmitter or "SharedReceiver" or "Receiver";
    }
}
