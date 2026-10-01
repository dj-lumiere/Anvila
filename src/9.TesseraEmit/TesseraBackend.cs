using Builder.Backends;

namespace Builder.TesseraEmit;

/// <summary>
/// The Tessera backend: writes the Phase 9 program as Tessera source (<see cref="TesseraWriter"/>), then has the
/// Tessera compiler, with Tessera's standard library, turn it into LLVM IR. Selected by
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

    /// <inheritdoc/>
    public BackendOutput Emit(BackendInput input)
    {
        var writer = new TesseraWriter(input: input);
        string source = writer.Write();
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
            string shown = Path.Combine(path1: "stdlib",
                path2: Path.GetRelativePath(relativeTo: stdlib, path: file));
            decls.AddRange(collection: Parse(file: shown,
                source: Tessera.SourceText.Read(path: file, shown: shown),
                isLibrary: true));
        }

        string ir;
        try
        {
            ir = new Tessera.Compiler(target: TesseraTarget(target: input.Target), decls: decls).Generate();
        }
        catch (Tessera.CompileError ex)
        {
            throw new InvalidOperationException(
                message: $"The Tessera compiler rejected the generated module: {ex.Message}" +
                         " (set [debug] dump-tessera = true in config.toml to keep the module as <entry>.tess).",
                innerException: ex);
        }

        return new BackendOutput(LlvmIr: ir, DefinedRoutineSymbols: writer.DefinedRoutineNames);
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

    private static List<Tessera.Decl> Parse(string file, string source, bool isLibrary)
    {
        List<Tessera.Token> tokens = new Tessera.Lexer(file: file, src: source).Lex();
        return new Tessera.Parser(tokens: tokens, file: file, isLibrary: isLibrary).ParseModule()
                                                                                   .Decls;
    }

    /// <summary>
    /// Tessera's standard library: <c>TESSERA_STDLIB</c> when set, else the copy shipped next to the builder.
    /// </summary>
    private static string StdlibDirectory()
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
