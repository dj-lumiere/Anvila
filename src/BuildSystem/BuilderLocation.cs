namespace Builder;

/// <summary>
/// Where the running builder is on disk, in a form that holds when it runs as a managed program under the JIT (through
/// the apphost or `dotnet X.dll`), as a ReadyToRun program, and as a NativeAOT executable. <c>Assembly.Location</c> is
/// empty under NativeAOT, so nothing here asks an assembly where it is.
/// </summary>
internal static class BuilderLocation
{
    /// <summary>The folder the builder runs from: the standard library, the native runtime and LLVM sit in or above it. A
    /// managed daemon started by a NativeAOT builder runs from its own folder below it, and is told the builder's
    /// (<see cref="HomeEnvVar"/>).</summary>
    internal static string Directory =>
        Environment.GetEnvironmentVariable(variable: HomeEnvVar) is { Length: > 0 } home && System.IO.Directory.Exists(path: home)
            ? Path.TrimEndingDirectorySeparator(path: home)
            : Path.TrimEndingDirectorySeparator(path: AppContext.BaseDirectory);

    /// <summary>Set by a NativeAOT builder on the managed daemon it starts: the builder's folder.</summary>
    internal const string HomeEnvVar = "REVARE_BUILDER_HOME";

    /// <summary>The folder a NativeAOT builder keeps its managed daemon in. The daemon is managed code: under the JIT it
    /// analyzes about 1.6 times as fast as the NativeAOT build, and it runs for the whole session, while the
    /// builder a command starts is NativeAOT so it starts in milliseconds.</summary>
    internal const string DaemonFolder = "daemon";

    /// <summary>The name of the builder's executable (`revare`, `mielia`), the same for the NativeAOT builder and its
    /// managed daemon.</summary>
    internal static string ExecutableName =>
        Path.GetFileNameWithoutExtension(path: EntryDll ?? Environment.ProcessPath ?? "");

    /// <summary>The managed daemon a NativeAOT builder starts (<see cref="DaemonFolder"/>), or null when there is none
    /// beside it.</summary>
    internal static string? ManagedDaemonExecutable
    {
        get
        {
            string name = ExecutableName + (OperatingSystem.IsWindows() ? ".exe" : "");
            string path = Path.Combine(path1: Directory, path2: DaemonFolder, path3: name);
            return File.Exists(path: path) &&
                   File.Exists(path: Path.Combine(path1: Directory, path2: DaemonFolder, path3: ExecutableName + ".dll"))
                ? path
                : null;
        }
    }

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
        // A NativeAOT builder with its managed daemon: the builder and the daemon each count both, so they agree on
        // which build runs.
        string pairedDll = Path.Combine(path1: Directory, path2: DaemonFolder, path3: ExecutableName + ".dll");
        string pairedExe = Path.Combine(path1: Directory, path2: ExecutableName + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (File.Exists(path: pairedDll) && File.Exists(path: pairedExe))
        {
            return [pairedExe, pairedDll];
        }

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
