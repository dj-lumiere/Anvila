namespace Builder;

/// <summary>
/// Where the running builder is on disk, in a form that holds when it runs as a managed program under the JIT (through
/// the apphost or `dotnet X.dll`), as a ReadyToRun program, and as a NativeAOT executable. <c>Assembly.Location</c> is
/// empty under NativeAOT, so nothing here asks an assembly where it is.
/// </summary>
internal static class BuilderLocation
{
    /// <summary>The folder the builder runs from: the standard library, the native runtime and LLVM sit in or above it.</summary>
    internal static string Directory => Path.TrimEndingDirectorySeparator(path: AppContext.BaseDirectory);

    /// <summary>Whether this process is the `dotnet` host running a managed builder dll (<c>dotnet Revare.dll</c>).</summary>
    internal static bool RunsThroughDotnetHost =>
        Path.GetFileNameWithoutExtension(path: Environment.ProcessPath ?? "")
            .Equals(value: "dotnet", comparisonType: StringComparison.OrdinalIgnoreCase);

    /// <summary>The managed dll of the program that started this process, or null for a NativeAOT executable. The
    /// command line's first element is that dll for both the apphost and `dotnet X.dll`.</summary>
    internal static string? EntryDll
    {
        get
        {
            string[] args = Environment.GetCommandLineArgs();
            return args.Length > 0 && args[0].EndsWith(value: ".dll", comparisonType: StringComparison.OrdinalIgnoreCase)
                   && File.Exists(path: args[0])
                ? Path.GetFullPath(path: args[0])
                : null;
        }
    }

    /// <summary>The files whose build time says which build of the builder runs: the native executable (not the `dotnet`
    /// host), the entry dll, and the dll of each assembly that holds one of <paramref name="parts"/> beside it.</summary>
    internal static IEnumerable<string> BuildFiles(params Type[] parts)
    {
        var files = new List<string>();
        if (!RunsThroughDotnetHost && Environment.ProcessPath is { Length: > 0 } process)
        {
            files.Add(item: process);
        }

        if (EntryDll is { } entry)
        {
            files.Add(item: entry);
        }

        foreach (Type part in parts)
        {
            string name = part.Assembly.GetName().Name ?? "";
            files.Add(item: Path.Combine(path1: Directory, path2: name + ".dll"));
        }

        return files.Where(predicate: File.Exists).Distinct();
    }

    /// <summary>The newest write time, in ticks, over <see cref="BuildFiles"/>, or 0 when none exists.</summary>
    internal static long NewestBuildTicks(params Type[] parts) =>
        BuildFiles(parts: parts).Select(selector: f => File.GetLastWriteTimeUtc(path: f).Ticks).DefaultIfEmpty().Max();
}
