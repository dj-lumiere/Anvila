using Builder.Targeting;
using TypeModel.Symbols;

namespace Builder.Collection;

/// <summary>
/// Which routines push a frame on the crash trace's shadow stack, and how a frame names its routine. Shared by
/// every backend, so a crash trace reads the same whichever one built the program.
/// </summary>
internal static class TraceFrames
{
    /// <summary>True when a build of <paramref name="mode"/> keeps a crash trace (debug and release; the size and
    /// time builds leave it out).</summary>
    public static bool Enabled(RfBuildMode mode)
    {
        return mode is RfBuildMode.Debug or RfBuildMode.Release;
    }

    /// <summary>
    /// True when <paramref name="routine"/> pushes a frame: a routine of the program (not one the builder wrote,
    /// except a recovery variant, which runs the program's own routine for a <c>try</c> / <c>check</c> call), not
    /// <c>@inline</c> (an implementation detail that adds no navigable frame) or <c>@untraced</c> (a frame that is
    /// noise in a trace, like Core's crash_report), that can crash. A recovery variant's frame is what the trace a
    /// caught error keeps shows of the routines between its throw and the catcher.
    /// </summary>
    public static bool Pushes(RoutineInfo routine, CrashReachability? reachability)
    {
        return (!routine.IsSynthesized || routine.IsRecoveryVariant || routine.TraceName != null) &&
               !routine.Annotations.Contains(value: "inline") &&
               !routine.Annotations.Contains(value: "untraced") && reachability?.CanCrash(routine: routine) != false;
    }

    /// <summary>The name a frame shows: the routine with its failability and parameter types. A recovery variant
    /// shows as the failable routine it runs, and a routine the builder wrote with a trace name shows that.</summary>
    public static string Name(RoutineInfo routine)
    {
        if (routine.TraceName != null)
        {
            return routine.TraceName;
        }

        string parameters = string.Join(separator: ", ", values: routine.Parameters.Select(selector: p => p.Type.FullName));
        bool failable = routine.IsFailable || routine.IsRecoveryVariant;
        return $"{routine.BaseName}{(failable ? "!" : "")}({parameters})";
    }
}
