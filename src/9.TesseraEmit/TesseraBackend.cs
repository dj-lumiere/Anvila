using Builder.Backends;

namespace Builder.TesseraEmit;

/// <summary>
/// The Tessera backend: writes the Phase 9 program as Tessera source (<see cref="TesseraWriter"/>), then has the
/// Tessera builder, with Tessera's standard library, turn it into LLVM IR. Selected by
/// <c>[target] backend = "tessera"</c>. It covers a growing subset of the language; anything outside it fails the
/// build loudly, naming the construct.
/// </summary>
public sealed class TesseraBackend : IBuilderBackend
{
    /// <summary>The name a manifest selects this backend by.</summary>
    public const string BackendName = "tessera";

    /// <summary>The file name the generated module is parsed (and its errors reported) under.</summary>
    private const string ModuleFileName = "razorforge_module.tess";

    /// <inheritdoc/>
    public string Name => BackendName;

    /// <inheritdoc/>
    public bool SupportsResidentJit => false;

    /// <summary>
    /// Writes the Tessera translation of a build another backend emits (<c>[debug] dump-tessera</c> with the LLVM
    /// backend), to <paramref name="path"/>: the whole module, or as far as translation got with the reason it
    /// stopped. Only the file is written; nothing is compiled, and a failure never fails the build.
    /// </summary>
    public static void DumpSource(BackendInput input, string path)
    {
        var writer = new TesseraWriter(input: input);
        try
        {
            File.WriteAllText(path: path, contents: writer.Write());
        }
        catch (NotSupportedException ex)
        {
            File.WriteAllText(path: path, contents: writer.PartialModule(stoppedBecause: ex.Message));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            File.WriteAllText(path: path, contents: $"// The Tessera translation failed: {ex.Message}\n");
        }
    }

    /// <inheritdoc/>
    public BackendOutput Emit(BackendInput input)
    {
        var writer = new TesseraWriter(input: input);
        string source;
        try
        {
            source = writer.Write();
        }
        catch (NotSupportedException ex) when (input.SourceDumpPath is { } partialPath)
        {
            // Keep what was translated, so the stopping point can be read in context.
            File.WriteAllText(path: partialPath, contents: writer.PartialModule(stoppedBecause: ex.Message));
            throw;
        }

        if (input.SourceDumpPath is { } dumpPath)
        {
            File.WriteAllText(path: dumpPath, contents: source);
        }

        var decls = new List<Tessera.Decl>();
        decls.AddRange(collection: Parse(file: ModuleFileName, source: source, isLibrary: false));
        string stdlib = StdlibDirectory();
        foreach (string file in Directory.GetFiles(path: stdlib, searchPattern: "*.tess",
                         searchOption: SearchOption.AllDirectories)
                    .Order(comparer: StringComparer.Ordinal))
        {
            string shown = Path.Combine(path1: "Standard",
                path2: Path.GetRelativePath(relativeTo: stdlib, path: file));
            decls.AddRange(collection: Parse(file: shown,
                source: Tessera.SourceText.Read(path: file, shown: shown),
                isLibrary: true));
        }

        string ir;
        try
        {
            // RazorForge keeps its own crash trace in the generated routines (TesseraTrace), so Tessera's stays out.
            ir = new Tessera.Compiler(target: TesseraTarget(target: input.Target), decls: decls, trace: false)
                .Generate();
        }
        catch (Tessera.CompileError ex)
        {
            throw new InvalidOperationException(
                message: $"The Tessera builder rejected the generated module: {ex.Message}" +
                         " (set [debug] dump-tessera = true in config.toml to keep the module as <entry>.tess).",
                innerException: ex);
        }

        return new BackendOutput(LlvmIr: ir, DefinedRoutineSymbols: writer.DefinedRoutineNames);
    }

    /// <summary>
    /// Writes an Ingrid library (<c>export-ingrid</c>) from a build: <paramref name="library"/>'s exports and every
    /// routine they reach, as one formatted Tessera file. Nothing is compiled: Ingrid's build compiles the file with
    /// the rest of its library. Throws <see cref="NotSupportedException"/> naming a construct the backend does not
    /// translate.
    /// </summary>
    public static string WriteLibrary(BackendInput input, TesseraLibrary library)
    {
        string source = new TesseraWriter(input: input, library: library).Write();
        // Parsed once here, so a module the Tessera parser rejects fails the export rather than Ingrid's build.
        _ = Parse(file: "generated.tess", source: source, isLibrary: false);
        return Tessera.Formatter.Format(text: source);
    }

    /// <summary>Tessera names a target arch-os-abi (<c>x86_64-windows-msvc</c>, <c>aarch64-macos-none</c>).</summary>
    private static Tessera.BuildTarget TesseraTarget(Targeting.TargetConfig target)
    {
        string abi = target.TargetOS switch
        {
            "windows" => "msvc",
            "macos" => "none",
            _ => "gnu"
        };
        return Tessera.BuildTarget.Parse(triple: $"{target.TargetArch}-{target.TargetOS}-{abi}");
    }

    internal static List<Tessera.Decl> Parse(string file, string source, bool isLibrary)
    {
        List<Tessera.Token> tokens = new Tessera.Lexer(file: file, src: source).Lex();
        return new Tessera.Parser(tokens: tokens, file: file, isLibrary: isLibrary).ParseModule()
                                                                                   .Decls;
    }

    /// <summary>
    /// Tessera's standard library: <c>TESSERA_STDLIB</c> when set, else the copy shipped next to the builder.
    /// </summary>
    internal static string StdlibDirectory()
    {
        if (Environment.GetEnvironmentVariable(variable: "TESSERA_STDLIB") is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        string shipped = Path.Combine(path1: AppContext.BaseDirectory, path2: "tessera-stdlib");
        return Directory.Exists(path: shipped)
            ? shipped
            : throw new InvalidOperationException(
                message: $"Tessera's standard library is not at '{shipped}'. Rebuild the builder (it copies the " +
                         "library there) or set TESSERA_STDLIB to Tessera's stdlib directory.");
    }
}

/// <summary>
/// An Ingrid library the Tessera backend writes (<c>export-ingrid</c>): the routines it exports, each under its C
/// symbol (<c>ingrid_d64_add</c>), and the comment the file starts with. What they reach is written with them,
/// private to the file. A library keeps no crash trace and has no entry point, and a
/// crash in it is Tessera's <c>crash</c>, which Ingrid's crash handler reports.
/// </summary>
/// <param name="Exports">The routines the library exports, each a routine without <c>me</c>, with its C symbol.</param>
/// <param name="Header">The comment the file starts with, each line starting <c>//</c>, then a blank line.</param>
/// <param name="SourceFile">The name a <c>#source</c> line gives a source file of the build: Tessera reads a relative
/// one from the library's own directory.</param>
public sealed record TesseraLibrary(
    IReadOnlyList<(TypeModel.Symbols.RoutineInfo Routine, string Symbol)> Exports,
    string Header,
    Func<string, string> SourceFile);
