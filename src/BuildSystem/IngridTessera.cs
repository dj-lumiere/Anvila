using Builder.Targeting;
using Builder.TesseraEmit;

namespace Builder;

/// <summary>
/// Ingrid's libraries written in Tessera (<c>Ingrid/tessera/*.te</c>): the math, hashing, and random-number code both
/// languages build on. The standard library calls them through <c>C::</c> declarations, and their bodies are not in the
/// runtime DLL, so every RazorForge module is llvm-linked with this library before <c>opt</c>. That puts the bodies in
/// the module being optimized, where LLVM can inline them (<c>#inline</c> routines are <c>alwaysinline</c>).
/// <para>
/// The library is compiled in-process by the Tessera builder, for the CPU the RazorForge module targets (see
/// <see cref="TargetConfig.Cpu"/>), and cached as LLVM IR next to the executable. It is rebuilt when a source, the
/// Tessera builder, or this builder (which picks the CPU) is newer. An installed layout ships the cached IR and no
/// sources.
/// </para>
/// </summary>
internal static class IngridTessera
{
    /// <summary>The cached LLVM IR of the library, next to the executable.</summary>
    internal const string IrFileName = "ingrid_tessera.ll";

    /// <summary>Where a development checkout keeps the library's sources.</summary>
    private static readonly string CheckoutSourceDir = Path.Combine(path1: "Ingrid", path2: "tessera");

    private static readonly object CacheLock = new();

    /// <summary>
    /// Returns the path of the library's LLVM IR, compiling it when the cache is missing or stale. Throws when there are
    /// neither sources nor a shipped IR file, or when the Tessera builder rejects the sources: the standard library
    /// can't link without these definitions.
    /// </summary>
    internal static string IrPath(string exeDir)
    {
        string cached = Path.Combine(path1: exeDir, path2: IrFileName);
        lock (CacheLock)
        {
            if (!TryFindSourceDir(exeDir: exeDir, sourceDir: out string sourceDir))
            {
                return File.Exists(path: cached)
                    ? cached
                    : throw new InvalidOperationException(
                        message: $"Ingrid's Tessera library is missing: no '{CheckoutSourceDir}' sources near the " +
                                 $"executable and no '{IrFileName}' next to it.");
            }

            string[] sources = Directory.GetFiles(path: sourceDir, searchPattern: "*.te",
                    searchOption: SearchOption.AllDirectories)
                .Order(comparer: StringComparer.Ordinal)
                .ToArray();
            if (File.Exists(path: cached) && !IsStale(cached: cached, sources: sources))
            {
                return cached;
            }

            string ir = Compile(sourceDir: sourceDir, sources: sources);
            // A file of this process's own: builds run side by side (the test suites start several builder
            // processes at once), and a shared temporary name let one process move another's half-written file.
            string tmp = $"{cached}.{Environment.ProcessId}.tmp";
            File.WriteAllText(path: tmp, contents: ir);
            try
            {
                File.Move(sourceFileName: tmp, destFileName: cached, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process put its copy in place first and has it open: the same sources made it, so it is
                // just as good.
                TryDelete(path: tmp);
                if (!File.Exists(path: cached))
                {
                    throw;
                }
            }

            return cached;
        }
    }

    /// <summary>
    /// Returns the path of the library as LLVM bitcode, next to the IR, made from the IR when the bitcode is missing or
    /// older than it. A build links the bitcode: llvm-link reads it in a fraction of the time it takes to parse the
    /// text IR, and the linked module compiles to the same machine code (only the names llvm-link gives types that
    /// have the same layout can differ). Returns null when the bitcode can't be written: the caller then links the IR.
    /// </summary>
    internal static string? BitcodePath(string exeDir)
    {
        string ir = IrPath(exeDir: exeDir);
        string bc = Path.ChangeExtension(path: ir, extension: BitcodeExtension);
        lock (CacheLock)
        {
            if (File.Exists(path: bc) && File.GetLastWriteTimeUtc(path: bc) >= File.GetLastWriteTimeUtc(path: ir))
            {
                return bc;
            }

            // A file of this process's own, so a build in another process can't see it half written.
            string tmp = $"{bc}.{Environment.ProcessId}.tmp";
            if (!NativeToolchain.TryWriteBitcode(irFile: ir, bitcodeFile: tmp))
            {
                TryDelete(path: tmp);
                return null;
            }

            try
            {
                File.Move(sourceFileName: tmp, destFileName: bc, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process put its copy in place first and has it open: that one is just as good.
                TryDelete(path: tmp);
                if (!File.Exists(path: bc))
                {
                    return null;
                }
            }

            return bc;
        }
    }

    /// <summary>The extension of the library's bitcode next to the IR.</summary>
    internal const string BitcodeExtension = ".bc";

    /// <summary>
    /// Returns the path of the library compiled to a native object for the in-process JIT, compiling it from the cached IR
    /// when the object is missing or older than the IR. The JIT loads this object instead of parsing and compiling the
    /// whole library's IR on every run: the object is only relocated and linked, and only when the program reaches one
    /// of its symbols. It is compiled once, optimized (the library is shipped code, like a C library a debug build
    /// links), and with emulated thread-local storage, which the JIT's own code uses. On Windows its constant-pool
    /// symbols are made local here, once, so the JIT loads the object as it is (see
    /// <see cref="Builder.Execution.OrcCoffConstantPools"/>). Returns null when the object can't be compiled (no
    /// clang): the caller then hands the JIT the IR.
    /// </summary>
    internal static string? ObjectPath(string exeDir)
    {
        string ir = IrPath(exeDir: exeDir);
        string obj = Path.ChangeExtension(path: ir, extension: ObjectExtension);
        lock (CacheLock)
        {
            if (File.Exists(path: obj) && File.GetLastWriteTimeUtc(path: obj) >= File.GetLastWriteTimeUtc(path: ir))
            {
                return obj;
            }

            // A file of this process's own, so a build in another process can't see it half written.
            string tmp = $"{obj}.{Environment.ProcessId}.tmp";
            if (NativeToolchain.CompileIrToObject(optFile: ir, objFile: tmp, buildMode: RfBuildMode.Release) != 0)
            {
                TryDelete(path: tmp);
                return null;
            }

            if (OperatingSystem.IsWindows())
            {
                byte[] bytes = File.ReadAllBytes(path: tmp);
                Builder.Execution.OrcCoffConstantPools.Localize(obj: bytes);
                File.WriteAllBytes(path: tmp, bytes: bytes);
            }

            try
            {
                File.Move(sourceFileName: tmp, destFileName: obj, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process put its copy in place first and has it open: that one is just as good.
                TryDelete(path: tmp);
                if (!File.Exists(path: obj))
                {
                    return null;
                }
            }

            return obj;
        }
    }

    /// <summary>The extension of the compiled library next to the IR. It names the JIT's object, whose constant-pool
    /// symbols are already local, apart from the plain object earlier builders wrote as <c>ingrid_tessera.o</c>.</summary>
    private const string ObjectExtension = ".jit.o";

    private static readonly object WatchLock = new();
    private static List<FileSystemWatcher>? _watchers;
    private static volatile bool _watchedStale = true;
    private static string? _watchedObject;

    /// <summary>
    /// <see cref="ObjectPath"/> for a long-lived process (the daemon), without its per-call check of every source:
    /// the library's sources, Tessera's standard library and the cached IR, object and Tessera builder next to the
    /// executable are watched, and the object is looked up again only after one of them changed. The daemon hands the
    /// result to the client in its reply, so a warm run never walks the sources itself. Throws like
    /// <see cref="ObjectPath"/> when the sources don't compile, and checks again on the next call.
    /// </summary>
    internal static string? WatchedObjectPath(string exeDir)
    {
        lock (WatchLock)
        {
            _watchers ??= StartWatching(exeDir: exeDir);
            if (!_watchedStale && _watchedObject != null && File.Exists(path: _watchedObject))
            {
                return _watchedObject;
            }

            // Cleared before the look-up, so a change that lands during it marks the result stale again.
            _watchedStale = false;
            _watchedObject = null;
            try
            {
                _watchedObject = ObjectPath(exeDir: exeDir);
            }
            catch
            {
                _watchedStale = true;
                throw;
            }

            if (_watchedObject == null)
            {
                _watchedStale = true;
            }

            return _watchedObject;
        }
    }

    /// <summary>Watches everything <see cref="ObjectPath"/> decides staleness from. Any event, or a watcher that lost
    /// events (its buffer overflowed), marks the cached answer stale. The object's own writes do too, which costs one
    /// extra look-up, made between requests.</summary>
    private static List<FileSystemWatcher> StartWatching(string exeDir)
    {
        var watchers = new List<FileSystemWatcher>();
        if (TryFindSourceDir(exeDir: exeDir, sourceDir: out string sourceDir))
        {
            Watch(watchers: watchers, dir: sourceDir, recursive: true, filters: ["*.te"]);
        }

        Watch(watchers: watchers, dir: TesseraBackend.StdlibDirectory(), recursive: true, filters: ["*.te"]);
        string builder = typeof(Tessera.Compiler).Assembly.Location;
        Watch(watchers: watchers, dir: exeDir, recursive: false,
            filters: builder.Length > 0
                ? [Path.GetFileNameWithoutExtension(path: IrFileName) + ".*", Path.GetFileName(path: builder)]
                : [Path.GetFileNameWithoutExtension(path: IrFileName) + ".*"]);
        return watchers;
    }

    private static void Watch(List<FileSystemWatcher> watchers, string dir, bool recursive, string[] filters)
    {
        if (!Directory.Exists(path: dir))
        {
            return;
        }

        var watcher = new FileSystemWatcher(path: dir)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                           NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024
        };
        foreach (string filter in filters)
        {
            watcher.Filters.Add(item: filter);
        }

        watcher.Changed += (_, _) => _watchedStale = true;
        watcher.Created += (_, _) => _watchedStale = true;
        watcher.Deleted += (_, _) => _watchedStale = true;
        watcher.Renamed += (_, _) => _watchedStale = true;
        watcher.Error += (_, _) => _watchedStale = true;
        watcher.EnableRaisingEvents = true;
        watchers.Add(item: watcher);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path: path);
        }
        catch (IOException)
        {
            // A leftover temporary file is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless.
        }
    }

    /// <summary>The cache is stale when a source, the Tessera builder that compiled it, or Tessera's standard library is
    /// newer.</summary>
    private static bool IsStale(string cached, string[] sources)
    {
        DateTime cachedAt = File.GetLastWriteTimeUtc(path: cached);
        string[] builders = [typeof(Tessera.Compiler).Assembly.Location, typeof(IngridTessera).Assembly.Location];
        IEnumerable<string> stdlib = Directory.GetFiles(path: TesseraBackend.StdlibDirectory(), searchPattern: "*.te",
            searchOption: SearchOption.AllDirectories);
        return sources.Concat(second: stdlib).Any(predicate: s => File.GetLastWriteTimeUtc(path: s) > cachedAt)
               || builders.Any(predicate: b => b.Length > 0 && File.GetLastWriteTimeUtc(path: b) > cachedAt);
    }

    private static string Compile(string sourceDir, string[] sources)
    {
        // The folders Tessera programs import (`decimal/` is Ingrid/Decimal) are a library, as in a Tessera build: only
        // what an export reaches is built. The rest of the folder is RazorForge's and Suflae's, built whole.
        List<Tessera.StandardLibrary.IngridFile> library = Tessera.StandardLibrary.IngridFiles(directory: sourceDir);
        var inLibrary = new HashSet<string>(collection: library.Select(selector: f => f.Path),
            comparer: StringComparer.OrdinalIgnoreCase);
        var decls = new List<Tessera.Decl>(collection: Tessera.StandardLibrary.LoadIngrid(directory: sourceDir));
        foreach (string file in sources.Where(predicate: f => !inLibrary.Contains(item: Path.GetFullPath(path: f))))
        {
            string shown = Path.Combine(path1: CheckoutSourceDir,
                path2: Path.GetRelativePath(relativeTo: sourceDir, path: file));
            decls.AddRange(collection: TesseraBackend.Parse(file: shown,
                source: Tessera.SourceText.Read(path: file, shown: shown),
                isLibrary: false));
        }

        // Each standard library file is the module its folder names (Tessera.StandardLibrary).
        decls.AddRange(collection: Tessera.StandardLibrary.Load(directory: TesseraBackend.StdlibDirectory()));

        try
        {
            // The standard library's own exports (its crash handler, the half and bfloat conversions) stay out: the
            // RazorForge program brings its runtime, and a stray copy would pull in POSIX calls the JIT can't resolve.
            // Tessera's own crash trace stays out too: Ingrid's routines (ingrid_roam_hold and the like) sit on hot
            // paths of every RazorForge program, and RazorForge keeps its own trace.
            NativeToolchain.ShareClangWithTessera();
            return new Tessera.Compiler(target: TargetConfig.ForCurrentHost().TesseraTarget(), decls: decls,
                    trace: false)
                    { EmitLibraryExports = false }
                .Generate();
        }
        catch (Tessera.CompileError ex)
        {
            throw new InvalidOperationException(message: $"The Tessera builder rejected Ingrid's library: {ex.Message}",
                innerException: ex);
        }
    }

    /// <summary>Finds <c>Ingrid/tessera</c> by walking up from the executable (development checkouts only).</summary>
    private static bool TryFindSourceDir(string exeDir, out string sourceDir)
    {
        // AppContext.BaseDirectory ends in a separator, and GetDirectoryName of "a/b/" is "a/b": without the trim the
        // first step up stays in the same folder and the walk ends one level short of the workspace root.
        string? current = Path.TrimEndingDirectorySeparator(path: exeDir);
        for (int i = 0; i < 6 && current != null; i++)
        {
            string candidate = Path.Combine(path1: current, path2: CheckoutSourceDir);
            if (Directory.Exists(path: candidate))
            {
                sourceDir = candidate;
                return true;
            }

            current = Path.GetDirectoryName(path: current);
        }

        sourceDir = "";
        return false;
    }
}
