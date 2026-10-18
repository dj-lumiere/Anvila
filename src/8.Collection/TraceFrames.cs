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
    public static string Name(RoutineInfo routine, bool sharedEntities = false)
    {
        if (routine.TraceName != null)
        {
            return routine.TraceName;
        }

        // A recovery variant shows its routine as written: with `!` only when that routine is declared failable.
        bool failable = routine.IsFailable || routine.RecoveryOf is { IsFailable: true };
        if (!sharedEntities)
        {
            string parameters = string.Join(separator: ", ",
                values: routine.Parameters.Select(selector: p => p.Type.FullName));
            return $"{routine.BaseName}{(failable ? "!" : "")}({parameters})";
        }

        // Where entities are shared handles, the frame speaks of the program's own types: a handle shows as the
        // entity it stands for, and no type names the library's language.
        string owner = routine.OwnerType != null
            ? $"{Shown(type: routine.OwnerType)}.{routine.Name}"
            : routine.BaseName;
        string shownParameters = string.Join(separator: ", ",
            values: routine.Parameters.Select(selector: p => Shown(type: p.Type)));
        return $"{owner}{(failable ? "!" : "")}({shownParameters})";
    }

    /// <summary>A type as a frame shows it where entities are shared: <c>Roamed[X]</c> as <c>X</c>, at any depth,
    /// and each type by its module and name alone.</summary>
    private static string Shown(TypeModel.Types.TypeSymbol type)
    {
        if (type is TypeModel.Types.RecordTypeSymbol { GenericDefinition.Name: "Roamed", TypeArguments: [var held] })
        {
            return Shown(type: held);
        }

        TypeModel.Types.TypeSymbol? definition = type switch
        {
            TypeModel.Types.RecordTypeSymbol r => r.GenericDefinition,
            TypeModel.Types.EntityTypeSymbol e => e.GenericDefinition,
            _ => null
        };
        if (definition != null && type.TypeArguments is { Count: > 0 } arguments)
        {
            return $"{Qualified(type: definition)}[{string.Join(separator: ", ", values: arguments.Select(selector: Shown))}]";
        }

        return type.TypeArguments is { Count: > 0 } ? type.Name : Qualified(type: type);
    }

    /// <summary>A type's module and name, without its realm.</summary>
    private static string Qualified(TypeModel.Types.TypeSymbol type)
    {
        return string.IsNullOrEmpty(value: type.Module) ? type.Name : $"{type.Module}.{type.Name}";
    }
}
