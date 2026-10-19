namespace Builder.Declaration;

/// <summary>
/// The module rules of the standard library and of the modules under one another. <c>System</c> is the language's
/// base: every file sees it and every module under it (<c>System/Numerics</c>, <c>System/Memory</c>, ...) without an
/// import, and those modules share one namespace, <c>System</c>, so their names are one set. A standard library file is
/// the module its folder names, and a <c>module</c> line that names another module is a build error. An import brings
/// the module it names, never the modules under it. A <c>secret</c> name is seen in its module and in the modules
/// under it.
/// </summary>
internal static class StandardModules
{
    /// <summary>The language's base module.</summary>
    public const string Base = "System";

    /// <summary>The folder of a language's library that holds the files bound from RazorForge's library. Its own
    /// folders mirror RazorForge's, so a file there is the module of its folder below it.</summary>
    public const string SharedFolder = "Shared";

    /// <summary>Set while <c>export-ingrid</c> builds an export file: such a file is part of the standard library, so it
    /// may declare a module under <c>System</c> and reach its secrets.</summary>
    public static bool BuildingLibraryExport { get; set; }

    /// <summary>Whether <paramref name="module"/> is <c>System</c> or a module under it, written with <c>/</c> or
    /// with <c>.</c>.</summary>
    public static bool IsSystem(string? module) =>
        module != null &&
        (module.Equals(value: Base, comparisonType: StringComparison.OrdinalIgnoreCase) ||
         module.StartsWith(value: Base + "/", comparisonType: StringComparison.OrdinalIgnoreCase) ||
         module.StartsWith(value: Base + ".", comparisonType: StringComparison.OrdinalIgnoreCase));

    /// <summary>The namespace a module's names register under: <c>System</c> for <c>System</c> and every module
    /// under it, the module's own path otherwise.</summary>
    public static string Identity(string module) => IsSystem(module: module)
        ? Base
        : module;

    /// <summary>Whether <paramref name="module"/> is <paramref name="parent"/> or a module under it
    /// (<c>Garden/Tools</c> is under <c>Garden</c>). Either may be written with <c>/</c> or <c>.</c>.</summary>
    public static bool IsUnder(string module, string parent)
    {
        if (parent.Length == 0)
        {
            return module.Length == 0;
        }

        string m = module.Replace(oldChar: '.', newChar: '/');
        string p = parent.Replace(oldChar: '.', newChar: '/');
        return m == p || m.StartsWith(value: p + "/", comparisonType: StringComparison.Ordinal);
    }

    /// <summary>The modules this release took away, each with where its names are now, for the message of an
    /// import or a path that still names one.</summary>
    public static string? MovedModule(string module)
    {
        string path = module.Replace(oldChar: '.', newChar: '/');
        if (path.Equals(value: "Core", comparisonType: StringComparison.Ordinal) ||
            path.StartsWith(value: "Core/", comparisonType: StringComparison.Ordinal))
        {
            return Base;
        }

        return path == "Numerics" || path.StartsWith(value: "Numerics/", comparisonType: StringComparison.Ordinal)
            ? Base + "/Numerics"
            : null;
    }

    /// <summary>The message of an import that names a module this release took away, or null when
    /// <paramref name="module"/> is not one.</summary>
    public static string? GoneModuleMessage(string module)
    {
        string path = module.Replace(oldChar: '.', newChar: '/');
        if (path == "Signals" || path.StartsWith(value: "Signals/", comparisonType: StringComparison.Ordinal))
        {
            return $"Module '{module}' is gone: its handlers are 'CpuEventManager.when_interrupted' and " +
                   "'CpuEventManager.when_terminated' in System/Cpu, which every file sees without an import. Leave " +
                   "the import out and call them on CpuEventManager (migrate --step cpu-events does both).";
        }

        return MovedModule(module: module) is { } now
            ? $"Module '{module}' is gone: its names are in {now}, which every file sees without an import. Leave " +
              "the import out (migrate --step modules does it)."
            : null;
    }
}
