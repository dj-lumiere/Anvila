using Builder.TesseraEmit;

namespace Builder;

/// <summary>
/// Ingrid's libraries written in Tessera (<c>Ingrid/tessera/*.tess</c>): the math, hashing, and random-number code both
/// languages build on. The standard library calls them through <c>C::</c> declarations, and their bodies are not in the
/// runtime DLL, so every RazorForge module is llvm-linked with this library before <c>opt</c>. That puts the bodies in
/// the module being optimized, where LLVM can inline them (<c>#inline</c> routines are <c>alwaysinline</c>).
/// <para>
/// The library is compiled in-process by the Tessera builder, for the host, and cached as LLVM IR next to the
/// executable. It is rebuilt when a source or the Tessera builder is newer. An installed layout ships the cached IR
/// and no sources.
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

            string[] sources = Directory.GetFiles(path: sourceDir, searchPattern: "*.tess",
                    searchOption: SearchOption.AllDirectories)
                .Order(comparer: StringComparer.Ordinal)
                .ToArray();
            if (File.Exists(path: cached) && !IsStale(cached: cached, sources: sources))
            {
                return cached;
            }

            string ir = Compile(sourceDir: sourceDir, sources: sources);
            string tmp = cached + ".tmp";
            File.WriteAllText(path: tmp, contents: ir);
            File.Move(sourceFileName: tmp, destFileName: cached, overwrite: true);
            return cached;
        }
    }

    /// <summary>The cache is stale when a source, the Tessera builder that compiled it, or Tessera's standard library is
    /// newer.</summary>
    private static bool IsStale(string cached, string[] sources)
    {
        DateTime cachedAt = File.GetLastWriteTimeUtc(path: cached);
        string builder = typeof(Tessera.Compiler).Assembly.Location;
        IEnumerable<string> stdlib = Directory.GetFiles(path: TesseraBackend.StdlibDirectory(), searchPattern: "*.tess",
            searchOption: SearchOption.AllDirectories);
        return sources.Concat(second: stdlib).Any(predicate: s => File.GetLastWriteTimeUtc(path: s) > cachedAt)
               || (builder.Length > 0 && File.GetLastWriteTimeUtc(path: builder) > cachedAt);
    }

    private static string Compile(string sourceDir, string[] sources)
    {
        var decls = new List<Tessera.Decl>();
        foreach (string file in sources)
        {
            string shown = Path.Combine(path1: CheckoutSourceDir,
                path2: Path.GetRelativePath(relativeTo: sourceDir, path: file));
            decls.AddRange(collection: TesseraBackend.Parse(file: shown,
                source: Tessera.SourceText.Read(path: file, shown: shown),
                isLibrary: false));
        }

        string stdlib = TesseraBackend.StdlibDirectory();
        foreach (string file in Directory.GetFiles(path: stdlib, searchPattern: "*.tess",
                         searchOption: SearchOption.AllDirectories)
                     .Order(comparer: StringComparer.Ordinal))
        {
            string shown = Path.Combine(path1: "Standard", path2: Path.GetRelativePath(relativeTo: stdlib, path: file));
            decls.AddRange(collection: TesseraBackend.Parse(file: shown,
                source: Tessera.SourceText.Read(path: file, shown: shown),
                isLibrary: true));
        }

        try
        {
            // The standard library's own exports (its crash handler, the half and bfloat conversions) stay out: the
            // RazorForge program brings its runtime, and a stray copy would pull in POSIX calls the JIT can't resolve.
            // Tessera's own crash trace stays out too: Ingrid's routines (ingrid_roam_hold and the like) sit on hot
            // paths of every RazorForge program, and RazorForge keeps its own trace.
            return new Tessera.Compiler(target: Tessera.BuildTarget.Host(), decls: decls, trace: false)
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
        string? current = exeDir;
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
