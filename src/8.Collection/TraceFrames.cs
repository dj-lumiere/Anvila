using Builder.Declaration;
using Builder.Targeting;
using SyntaxTree;
using TypeModel;
using TypeModel.Enums;
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
    /// noise in a trace, like System's crash_report), that can crash. A recovery variant's frame is what the trace a
    /// caught error keeps shows of the routines between its throw and the catcher.
    /// A standard library routine pushes none: the trace shows the program's routines, and the program's frame says
    /// where it called into the library (its line and column point at the call). `[debug] trace-stdlib = true`
    /// keeps the library's frames too, for work on the library itself.
    /// </summary>
    public static bool Pushes(RoutineInfo routine, CrashReachability? reachability, TypeRegistry registry)
    {
        return (!routine.IsSynthesized || routine.IsRecoveryVariant || routine.TraceName != null) &&
               !routine.Annotations.Contains(value: "inline") &&
               !routine.Annotations.Contains(value: "untraced") && reachability?.CanCrash(routine: routine) != false &&
               (Diagnostics.DiagnosticFlags.TraceStdlib || !IsLibrary(routine: routine, registry: registry));
    }

    /// <summary>Whether <paramref name="routine"/> (or the generic routine it was made from) is declared in the
    /// standard library.</summary>
    private static bool IsLibrary(RoutineInfo routine, TypeRegistry registry)
    {
        RoutineInfo definition = routine.GenericDefinition ?? routine;
        return registry.IsStandardLibraryFile(filePath: definition.Location?.FileName);
    }

    /// <summary>The name a frame shows: the routine with its failability and parameter types. A recovery variant
    /// shows as the failable routine it runs, and a routine the builder wrote with a trace name shows that.</summary>
    public static string Name(RoutineInfo routine, bool sharedEntities = false)
    {
        if (routine.TraceName != null)
        {
            return routine.TraceName;
        }

        // A frame names the routine as callers write it: failability is on the declaration, never on the name.
        if (!sharedEntities)
        {
            string parameters = string.Join(separator: ", ",
                values: routine.Parameters.Select(selector: p => p.Type.FullName));
            return $"{routine.BaseName}({parameters})";
        }

        // Where entities are shared handles, the frame speaks of the program's own types: a handle shows as the
        // entity it stands for, and no type names the library's language.
        string owner = routine.OwnerType != null
            ? $"{Shown(type: routine.OwnerType)}.{routine.Name}"
            : routine.BaseName;
        string shownParameters = string.Join(separator: ", ",
            values: routine.Parameters.Select(selector: p => Shown(type: p.Type)));
        return $"{owner}({shownParameters})";
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

/// <summary>
/// With <c>[debug] trace-stdlib = true</c> (the only build where library routines push frames, see
/// <see cref="TraceFrames.Pushes"/>), where entities are shared (Suflae), which standard library routines push a
/// frame: the ones a program can name. A
/// routine of Suflae's library is one when neither it nor its type is <c>secret</c>, and a routine of RazorForge's
/// library when an open <c>@rf</c> declaration binds it and its type. The library's own helpers push none, so a crash
/// trace shows the program's routines and the library routines it called, never what those called inside the library.
/// </summary>
internal sealed class LibraryFrames(TypeRegistry registry)
{
    private readonly HashSet<string> _open = RfBindingCheck.BoundNames(registry: registry, openOnly: true);

    /// <summary>True when <paramref name="routine"/> is a library routine no program can name.</summary>
    public bool Hides(RoutineInfo routine)
    {
        RoutineInfo definition = routine.GenericDefinition ?? routine;
        string? file = definition.Location?.FileName;
        if (registry.IsLanguageLibraryFile(filePath: file, language: Language.Suflae))
        {
            return definition.Visibility == VisibilityModifier.Secret ||
                   definition.OwnerType is { } own && !Open(type: own);
        }

        if (!registry.IsStandardLibraryFile(filePath: file))
        {
            return false;
        }

        bool bound = _open.Contains(item: RfBindingCheck.ShapeOf(routine: routine)) ||
                     definition.OwnerType is { } owner &&
                     Verification.SemanticVerifier.ProtocolsOf(type: owner)
                                 .Any(predicate: p => _open.Contains(
                                      item: RfBindingCheck.ShapeOf(routine: routine, ownerName: p.BareName)));
        return !bound || definition.OwnerType is { } type && !Open(type: type);
    }

    /// <summary>Whether a program can name <paramref name="type"/>: a type of Suflae's library that is not
    /// <c>secret</c>, a RazorForge library type an open <c>@rf</c> declaration binds, or a type of the
    /// program.</summary>
    private bool Open(TypeModel.Types.TypeSymbol type)
    {
        // A handle stands for the entity it holds.
        if (type is TypeModel.Types.RecordTypeSymbol { GenericDefinition.Name: "Roamed", TypeArguments: [var held] })
        {
            return Open(type: held);
        }

        TypeModel.Types.TypeSymbol definition = type switch
        {
            TypeModel.Types.RecordTypeSymbol { GenericDefinition: { } record } => record,
            TypeModel.Types.EntityTypeSymbol { GenericDefinition: { } entity } => entity,
            TypeModel.Types.ProtocolTypeSymbol { GenericDefinition: { } protocol } => protocol,
            _ => type
        };
        // A Suflae library type of the same name (Suflae's own `List`) is stamped with Suflae's realm.
        if (definition.Realm == Realms.Shared &&
            registry.RazorForgeLibraryTypes.Contains(item: $"{definition.Module}.{definition.BareName}"))
        {
            return _open.Contains(item: definition.BareName);
        }

        return definition.Visibility != VisibilityModifier.Secret ||
               !registry.IsLanguageLibraryFile(filePath: definition.Location?.FileName, language: Language.Suflae);
    }
}
