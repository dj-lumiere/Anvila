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
    /// True when <paramref name="routine"/> pushes a frame: a routine of the program (not one the builder wrote), not
    /// <c>@inline</c> (an implementation detail that adds no navigable frame) or <c>@untraced</c> (a frame that is
    /// noise in a trace, like Core's crash_report), that can crash.
    /// </summary>
    public static bool Pushes(RoutineInfo routine, CrashReachability? reachability)
    {
        return !routine.IsSynthesized && !routine.Annotations.Contains(value: "inline") &&
               !routine.Annotations.Contains(value: "untraced") && reachability?.CanCrash(routine: routine) != false;
    }

    /// <summary>The name a frame shows: the routine with its failability and parameter types.</summary>
    public static string Name(RoutineInfo routine)
    {
        string parameters = string.Join(separator: ", ", values: routine.Parameters.Select(selector: p => p.Type.FullName));
        return $"{routine.BaseName}{(routine.IsFailable ? "!" : "")}({parameters})";
    }
}
