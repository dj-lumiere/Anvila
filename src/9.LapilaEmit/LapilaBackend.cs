using Builder.Backends;

namespace Builder.LapilaEmit;

/// <summary>
/// The Lapila backend: writes the Phase 9 program as Lapila source (<see cref="LapilaWriter"/>), then has the
/// Lapila builder, with Lapila's standard library, turn it into LLVM IR. Selected by
/// <c>[target] backend = "lapila"</c>. It covers a growing subset of the language; anything outside it fails the
/// build loudly, naming the construct.
/// </summary>
public sealed class LapilaBackend : IBuilderBackend
{
    /// <summary>The name a manifest selects this backend by.</summary>
    public const string BackendName = "lapila";

    /// <summary>The file name the generated module is parsed (and its errors reported) under.</summary>
    private const string ModuleFileName = "revare_module.lpa";

    /// <inheritdoc/>
    public string Name => BackendName;

    /// <inheritdoc/>
    public bool SupportsResidentBase => true;

    /// <inheritdoc/>
    public bool SupportsLazyJit => false;

    /// <inheritdoc/>
    /// <remarks>The base defines every routine the writer writes and every instance Lapila makes for them, external
    /// for the delta to link to (<c>Lapila.Compiler.ExposeDefinitions</c>). Its symbols are both kinds of name: the
    /// routine symbols, which the delta's writer declares instead of writing (<see cref="LapilaWriter"/>), and
    /// Lapila's own instance and global symbols, which the delta's Lapila build declares instead of defining. The
    /// two never meet: Lapila's are its mangled names, the routine symbols are the builder's.</remarks>
    public ResidentBase EmitResidentBase(BackendInput input)
    {
        var writer = new LapilaWriter(input: input, residentBase: true);
        string source = writer.Write();
        var decls = new List<Lapila.Decl>();
        decls.AddRange(collection: Parse(file: ModuleFileName, source: source, isLibrary: false));
        decls.AddRange(collection: Lapila.StandardLibrary.Load(directory: StdlibDirectory()));
        NativeToolchain.ShareClangWithLapila();
        var compiler = new Lapila.Compiler(target: input.Target.LapilaTarget(), decls: decls, trace: false)
        {
            ExposeDefinitions = true
        };
        string ir;
        try
        {
            ir = compiler.Generate();
        }
        catch (Lapila.CompileError ex)
        {
            throw new InvalidOperationException(
                message: $"The Lapila builder rejected the generated base module: {ex.Message}", innerException: ex);
        }

        var symbols = new HashSet<string>(collection: writer.DefinedRoutineSymbols, comparer: StringComparer.Ordinal);
        symbols.UnionWith(other: compiler.ExposedSymbols);
        // The delta's collector skips only the instances whose routines the base wrote: an instance outside the
        // seed's live set has no definition here, and the delta builds it.
        var instanceKeys = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach ((string key, Instantiation.MonomorphizedBody body) in input.InstantiatedGenericBodies ??
                                                                      new Dictionary<string, Instantiation.MonomorphizedBody>())
        {
            if (writer.DefinesRoutine(symbol: LlvmEmit.LlvmEmitter.MangleRoutineName(routine: body.Info)))
            {
                instanceKeys.Add(item: key);
            }
        }

        return new ResidentBase(LlvmIr: ir, Symbols: symbols, InstanceKeys: instanceKeys);
    }

    /// <summary>
    /// Writes the Lapila translation of a build another backend emits (<c>[debug] dump-lapila</c> with the LLVM
    /// backend), to <paramref name="path"/>: the whole module, or as far as translation got with the reason it
    /// stopped. Only the file is written; nothing is compiled, and a failure never fails the build.
    /// </summary>
    public static void DumpSource(BackendInput input, string path)
    {
        var writer = new LapilaWriter(input: input);
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
            File.WriteAllText(path: path, contents: $"# The Lapila translation failed: {ex.Message}\n");
        }
    }

    /// <inheritdoc/>
    public BackendOutput Emit(BackendInput input)
    {
        // [debug] timing splits the backend's time into its steps.
        System.Diagnostics.Stopwatch? clock = input.Timing ? System.Diagnostics.Stopwatch.StartNew() : null;
        var steps = new List<string>();
        void Step(string name)
        {
            if (clock != null)
            {
                steps.Add(item: $"{name} {clock.ElapsedMilliseconds} ms");
                clock.Restart();
            }
        }

        var writer = new LapilaWriter(input: input);
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

        Step(name: "write");
        var decls = new List<Lapila.Decl>();
        decls.AddRange(collection: Parse(file: ModuleFileName, source: source, isLibrary: false));
        Step(name: "parse module");
        // Lapila parses its library in parallel and keeps each file's declarations for the life of the process, so
        // the compile daemon parses it once for all its builds (a file changed on disk is parsed again).
        decls.AddRange(collection: Lapila.StandardLibrary.Load(directory: StdlibDirectory()));
        Step(name: "stdlib");

        string ir;
        try
        {
            // Revare keeps its own crash trace in the generated routines (LapilaTrace), so Lapila's stays out.
            // A delta declares what the resident base defines (EmitResidentBase).
            NativeToolchain.ShareClangWithLapila();
            var compiler = new Lapila.Compiler(target: input.Target.LapilaTarget(), decls: decls, trace: false)
            {
                ProvidedSymbols = input.ResidentSymbols is { Count: > 0 } resident
                    ? resident as IReadOnlySet<string> ?? new HashSet<string>(collection: resident, comparer: StringComparer.Ordinal)
                    : new HashSet<string>()
            };
            Step(name: "compiler");
            ir = compiler.Generate();
            Step(name: "generate");
        }
        catch (Lapila.CompileError ex)
        {
            throw new InvalidOperationException(
                message: $"The Lapila builder rejected the generated module: {ex.Message}" +
                         " (set [debug] dump-lapila = true in config.toml to keep the module as <entry>.lpa).",
                innerException: ex);
        }

        if (clock != null)
        {
            Console.Error.WriteLine(value: $"[timing] lapila backend: {string.Join(separator: ", ", values: steps)}");
        }

        return new BackendOutput(LlvmIr: ir, DefinedRoutineSymbols: writer.DefinedRoutineNames);
    }

    internal static List<Lapila.Decl> Parse(string file, string source, bool isLibrary)
    {
        List<Lapila.Token> tokens = new Lapila.Lexer(file: file, src: source).Lex();
        // The module this backend writes still uses Lapila's pre-0.5 spellings (`Void`, blocks and jumps), which the
        // builder reads when asked: a person's own source gets the build error that names the migration instead.
        return new Lapila.Parser(tokens: tokens, file: file, isLibrary: isLibrary, acceptLegacy: true).ParseModule()
                                                                                                    .Decls;
    }

    /// <summary>
    /// Lapila's standard library: <c>LAPILA_STDLIB</c> when set, else the copy shipped next to the builder.
    /// </summary>
    internal static string StdlibDirectory()
    {
        if (Environment.GetEnvironmentVariable(variable: "LAPILA_STDLIB") is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        string shipped = Path.Combine(path1: BuilderLocation.Directory, path2: "lapila-stdlib");
        return Directory.Exists(path: shipped)
            ? shipped
            : throw new InvalidOperationException(
                message: $"Lapila's standard library is not at '{shipped}'. Rebuild the builder (it copies the " +
                         "library there) or set LAPILA_STDLIB to Lapila's stdlib directory.");
    }
}
