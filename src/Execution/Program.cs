using System.Diagnostics;
using System.Text;
using Builder.Backends;
using Builder.LlvmEmit;
using Builder.Declaration;
using Builder.Diagnostics;
using Builder.Tokenizer;
using Builder.Parser;
using Builder.Targeting;
using Builder.Verification;
using Builder.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Execution;

/// <summary>
/// Command-line entry point for the RazorForge builder toolchain.
/// </summary>
internal partial class Program
{
    private const string BuildCommand = "build";
    private const string RunCommand = "run";
    private const string BuildAndRunCommand = "buildandrun";
    /// <summary>The language of the command that is running (<c>razorforge</c> or <c>suflae</c>). Selects
    /// the branding (version/usage) and is the DEFAULT language when a source's extension does not decide
    /// it; a registered extension always wins over this default. Set by <see cref="Run"/> from the command
    /// line that called it.</summary>
    private static Language CliLanguage = Language.RazorForge;

    /// <summary>The rules of <see cref="CliLanguage"/>.</summary>
    private static Builder.Frontends.LanguageRules CliRules => Builder.Frontends.Languages.For(language: CliLanguage);

    /// <summary>
    /// Runs a command line: the shared entry of the <c>razorforge</c> and <c>suflae</c> commands, which
    /// register their language rules first and pass their own language as <paramref name="cliLanguage"/>.
    /// Dispatches to the appropriate command handler based on the first argument.
    /// Returns 0 on success or 1 on error.
    /// </summary>
    public static int Run(string[] args, Language cliLanguage)
    {
        CliLanguage = cliLanguage;
        RuntimeShadowLoader.Install();

        // Make the build driver byte-faithful for UTF-8. RF child processes write UTF-8 and
        // we forward their stdout/stderr to ours; if Console encodings default to the
        // system ACP (Korean CP949, Western CP1252, ...), every non-ASCII byte gets
        // rewritten as `?` somewhere in the read/write chain. Forcing UTF-8 on both input
        // and output makes the pipe a passthrough.
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string command = args[0]
                        .ToLowerInvariant()
                        .TrimStart(trimChar: '-');

        int earlyResult = HandleEarlyCommands(command: command);
        if (earlyResult >= 0)
        {
            return earlyResult;
        }

        // Check if first arg is a command or a file
        bool isCommand = command is "parse" or "tokenize" or "codegen" or BuildCommand
            or RunCommand or "test" or "lint" or "buildandrun" or "check" or "validate-stdlib" or "emit-prebuilt" or "emit-ingrid" or "export-ingrid" or "help" or "fmt";

        if (!isCommand)
        {
            // Every action has its command, so a bare file does nothing on its own: running it is `run`.
            Console.Error.WriteLine(value: File.Exists(path: args[0])
                ? $"error: '{args[0]}' is a file, not a command. To build and run it: {CliRules.ToolName} run {args[0]}"
                : $"error: unknown command '{args[0]}'. See {CliRules.ToolName} help.");
            return 1;
        }

        return DispatchCommand(command: command, args: args);
    }

    /// <summary>
    /// Handles the version/lsp/daemon commands that exit before the main dispatch.
    /// Returns a non-negative exit code when the command was handled, or -1 to continue dispatch.
    /// </summary>
    private static int HandleEarlyCommands(string command)
    {
        if (command is "version" or "v")
        {
            PrintVersion();
            return 0;
        }

        // `--lsp` (or `lsp`): run the stdio Language Server. Takes no file argument; it speaks
        // LSP/JSON-RPC over stdin/stdout until the client sends `exit`.
        if (command is "lsp")
        {
            return LspServer.Run(profile: CliRules.LanguageServer);
        }

        // `daemon`: run the warm-compile daemon (foreground). Captures the fully-processed stdlib in RAM
        // and serves warm build/check/buildandrun requests over a per-user named pipe, so a client skips
        // the ~5 s stdlib reprocessing on every invocation. Started explicitly by the developer; clients
        // opt in via RAZORFORGE_DAEMON=1. Ctrl-C / `daemon-stop` shuts it down.
        if (command is "daemon")
        {
            return CompileDaemon.RunServer();
        }

        if (command is "daemon-stop")
        {
            return CompileDaemon.StopServer();
        }

        return -1; // not handled
    }

    /// <summary>
    /// Dispatches a known command to its handler. Called after early-command and bare-file handling.
    /// </summary>
    private static int DispatchCommand(string command, string[] args)
    {
        switch (command)
        {
            case "parse":
                if (args.Length < 2)
                {
                    Console.WriteLine(value: "Error: parse command requires a file path");
                    return 1;
                }

                return ParseFile(sourceFile: args[1]);

            case "tokenize":
                if (args.Length < 2)
                {
                    Console.WriteLine(value: "Error: tokenize command requires a file path");
                    return 1;
                }

                return TokenizeFile(sourceFile: args[1]);

            case "codegen":
                if (args.Length < 2)
                {
                    Console.WriteLine(value: "Error: codegen command requires a file path");
                    return 1;
                }

                return RunCodegenCommand(args: args);

            case BuildCommand:
                return RunBuildCommand(args: args);

            case RunCommand:
            case "buildandrun":
                return RunBuildAndRunCommand(args: args);

            case "check":
                return RunCheckCommand(args: args);

            case "test":
                return RunTestCommand(args: args);

            case "lint":
                return RunLintCommand(args: args);

            case "validate-stdlib":
                return RunValidateStdlibCommand(args: args);

            case "emit-prebuilt":
                return EmitPrebuilt(args: args);

            case "emit-ingrid":
                return EmitIngrid();

            case "export-ingrid":
                return ExportIngrid(args: args);

            case "fmt":
                return RunFormatCommand(args: args);

            case "help":
                PrintUsage();
                return 0;

            default:
                PrintUsage();
                return 1;
        }
    }

    /// <summary>
    /// Handles the <c>codegen</c> verb: runs the same front pipeline as <c>build</c> (build driver, Suflae
    /// prelude, analysis, lowering) and writes the LLVM IR to <c>[out.ll]</c> (default: the entry file's
    /// <c>.ll</c>) without running opt, clang or the linker.
    /// </summary>
    private static int RunCodegenCommand(string[] args)
    {
        ResolvedEntry resolved = ResolveEntryFile(args: args, needsOutputArg: true);
        if (resolved.EntryFile == null)
        {
            return 1;
        }

        return BuildMultiFile(entryFile: resolved.EntryFile,
            outputFile: resolved.OutputFile ??
                        Path.ChangeExtension(path: resolved.EntryFile, extension: ".ll"),
            discoveredLinkLibraries: out _,
            config: resolved);
    }

    /// <summary>Runs the <c>check</c> command: resolves the entry file and type-checks without codegen.</summary>
    private static int RunCheckCommand(string[] args)
    {
        ResolvedEntry resolved = ResolveEntryFile(args: args, needsOutputArg: false);
        if (resolved.EntryFile == null)
        {
            return 1;
        }

        return CheckMultiFile(entryFile: resolved.EntryFile,
            projectRoot: resolved.ProjectRoot,
            libraryRoots: resolved.LibraryRoots);
    }

    /// <summary>Runs the <c>validate-stdlib</c> command: validates stdlib routine bodies for the given language.</summary>
    private static int RunValidateStdlibCommand(string[] args)
    {
        Language stdlibLang = CliLanguage;
        if (args.Length >= 2)
        {
            string lang = args[1];
            stdlibLang = Builder.Frontends.Languages.All
                                .FirstOrDefault(predicate: rules =>
                                     string.Equals(a: rules.ShortName, b: lang, comparisonType: StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(a: rules.Name, b: lang, comparisonType: StringComparison.OrdinalIgnoreCase))
                               ?.Language ?? CliLanguage;
        }

        return ValidateStdlib(language: stdlibLang);
    }

    /// <summary>
    /// Handles the <c>build</c> verb: compiles all the way to a native executable for the HOST OS
    /// (codegen -> opt -> link -> stage runtime DLLs) but does NOT run it. The intermediate
    /// <c>&lt;entry&gt;.ll</c> / <c>.opt.ll</c> are kept as byproducts for inspection; <c>codegen</c> remains
    /// the IR-only verb. (All-OS artifacts come from the release CI.)
    /// </summary>
    private static int RunBuildCommand(string[] args)
    {
        ResolvedEntry resolved = ResolveEntryFile(args: args, needsOutputArg: false);
        if (resolved.EntryFile == null)
        {
            return 1;
        }

        // Warm-daemon path: delegate the compile to a running daemon (skips stdlib reprocessing).
        if (CompileDaemon.TryClientBuild(resolved: resolved, exitCode: out int dbrc))
        {
            return dbrc;
        }

        int buildRc = BuildExecutable(entryFile: resolved.EntryFile,
            exeFile: out string builtExe,
            config: resolved);
        if (buildRc == 0)
        {
            Console.WriteLine(value: $"Executable written to: {Path.GetFullPath(path: builtExe)}");
        }

        return buildRc;
    }

    /// <summary>
    /// Handles the <c>buildandrun</c> verb: builds and executes. Prefers the in-process ORC-JIT dev loop and
    /// the warm-daemon compile path (both skip work) before falling back to a full local AOT build+run.
    /// </summary>
    private static int RunBuildAndRunCommand(string[] args)
    {
        ClientClock.Mark(label: "buildandrun entered (host startup + Main)");
        ResolvedEntry resolved = ResolveEntryFile(args: args, needsOutputArg: false);
        ClientClock.Mark(label: "manifest resolved");
        if (resolved.EntryFile == null)
        {
            return 1;
        }

        // `[debug] dump-ir`: the ORC-JIT and warm-daemon paths keep the IR in-memory / in the daemon and
        // never write a `<entry>.ll`, so skip BOTH and take the local AOT build+run, which emits (and keeps)
        // `<entry>.ll` + `.opt.ll` beside the source for inspection.
        bool dumpIr = DiagnosticFlags.DumpIr;

        // ORC-JIT dev-loop path (RAZORFORGE_JIT=1): JIT the module in-process — no opt/clang/link,
        // no exe, no spawn. IR comes warm from the daemon when it's up, else a local cold compile.
        if (!dumpIr && CompileDaemon.TryClientJitRun(resolved: resolved, exitCode: out int jrc))
        {
            ClientClock.Mark(label: "JIT path returned");
            ClientClock.Dump();
            return jrc;
        }

        // Warm-daemon path: delegate the COMPILE to a running daemon (skips stdlib reprocessing),
        // then run the produced exe locally so interactive stdin/stdout stays with this process.
        if (!dumpIr &&
            CompileDaemon.TryClientBuildAndRun(resolved: resolved, exitCode: out int drc))
        {
            return drc;
        }

        return BuildAndRun(entryFile: resolved.EntryFile, config: resolved);
    }

    /// <summary>
    /// Emits the modular (per-module) compiled-stdlib prebuilt files (<c>.pbrf</c> / <c>.pbsf</c>, see
    /// <see cref="Builder.Serialization.PrebuiltFormat"/>) as a BUILD BYPRODUCT — the
    /// daemon / cold path then LOADS them instead of paying a ~8 s capture on first run. Invoked by the
    /// MSBuild post-build target (and manually). Writes to <c>&lt;Standard&gt;/.prebuilt/&lt;Language&gt;/</c>.
    /// Incremental: a <c>stamp.txt</c> holds the stdlib content hash — if it matches and an index exists, the
    /// (re)capture is SKIPPED, so a no-stdlib-change rebuild is near-instant. Each language is best-effort:
    /// one failing does not fail the others (or the build — the caller uses ContinueOnError).
    /// Usage: <c>emit-prebuilt [outDir] [--all|--sf]</c>. Default outDir = the resolved stdlib root's <c>.prebuilt</c>.
    /// </summary>
    /// <summary>
    /// Writes Ingrid's Tessera library as LLVM IR next to the executable (<see cref="IngridTessera.IrFileName"/>), where
    /// every build links it from. A packaging step: an installed layout has no sources to compile it from, so the
    /// package ships this file. Usage: <c>emit-ingrid</c>, run from inside the workspace that holds <c>Ingrid/tessera</c>.
    /// </summary>
    private static int EmitIngrid()
    {
        try
        {
            string path = IngridTessera.IrPath(exeDir: AppContext.BaseDirectory);
            Console.WriteLine(value: $"[emit-ingrid] {path}");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(value: $"[emit-ingrid] {ex.Message}");
            return 1;
        }
    }

    private static int EmitPrebuilt(string[] args)
    {
        string? outDir = args.Length > 1 && !args[1]
           .StartsWith(value: "--")
            ? args[1]
            : Path.Combine(path1: StdlibLoader.GetDefaultStdlibPath(),
                path2: Builder.Serialization.PrebuiltFormat.FolderName);

        // RazorForge's library is shared by every build, so it is always emitted, then the running
        // command's language (or every registered language under --all).
        var langs = new List<Language> { Language.RazorForge };
        IEnumerable<Language> more = args.Contains(value: "--all")
            ? Builder.Frontends.Languages.All.Select(selector: rules => rules.Language)
            : [CliLanguage];
        foreach (Language lang in more)
        {
            if (!langs.Contains(item: lang))
            {
                langs.Add(item: lang);
            }
        }

        foreach (Language lang in langs)
        {
            try
            {
                string langDir = Path.Combine(path1: outDir, path2: lang.ToString());
                string stampPath = Path.Combine(path1: langDir, path2: "stamp.txt");
                string? hash =
                    Builder.Serialization.StdlibSnapshotCache.ComputeStdlibHash(language: lang);

                if (hash != null && File.Exists(path: stampPath) && File
                       .ReadAllText(path: stampPath)
                       .Trim() == hash &&
                    File.Exists(path: Path.Combine(path1: langDir,
                        path2: Builder.Serialization.PrebuiltFormat.IndexFileName(language: lang))))
                {
                    Console.WriteLine(value: $"[emit-prebuilt] {lang}: up to date");
                    continue;
                }

                var sw = Stopwatch.StartNew();
                SemanticVerifier.CompiledStdlibState state =
                    SemanticVerifier.CaptureCompiledStdlib(language: lang);
                if (Directory.Exists(path: langDir))
                {
                    Directory.Delete(path: langDir, recursive: true);
                }

                IReadOnlyList<string> labels =
                    Builder.Serialization.ModularStdlibCache.Serialize(state: state,
                        dir: langDir);
                if (hash != null)
                {
                    File.WriteAllText(path: stampPath, contents: hash);
                }

                Console.WriteLine(
                    value:
                    $"[emit-prebuilt] {lang}: {labels.Count} modules ({sw.ElapsedMilliseconds} ms) -> {langDir}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    value: $"[emit-prebuilt] {lang} FAILED (non-fatal): {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Bundles the optional warm-stdlib provider delegates passed down the build pipeline so callers
    /// do not need to forward three separate nullable function parameters individually.
    /// </summary>
    private sealed record WarmProviders(
        Func<Language, SemanticVerifier.CompiledStdlibState?>? WarmProvider,
        Action<string>? IrCallback,
        Func<Language, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>?
            StdlibIndexProvider,
        Action<LazyJitInputs>? LazyJitSink = null,
        Func<TypeModel.Symbols.RoutineInfo, bool>? InstanceCheckSkip = null,
        // Resident-JIT base/delta (Step 3): when set, codegen emits a DELTA — it DEFINES only non-resident
        // symbols and extern-DECLAREs these (the base object's defines). The daemon supplies the base's
        // DEFINED-symbol set so a warm build ships only the small delta IR alongside the cached base .o.
        IReadOnlyCollection<string>? ResidentSymbols = null,
        // Resident-JIT base/delta: RegistryKeys already built into the base object (the base's collected
        // instance set). Threaded to the demand collector so it skips re-building/analyzing/expanding them.
        IReadOnlySet<string>? ResidentInstanceKeys = null,
        // Called after a normal (non-lazy) emit with the build's codegen inputs and the routine symbols the
        // emitted module defined. The daemon uses it to grow its resident layers (CompileDaemon.ResidentLayer).
        Action<BuildObservation>? BuildObserver = null);

    /// <summary>What a finished build hands <see cref="WarmProviders.BuildObserver"/>: its codegen inputs and the
    /// routine symbols the emitted module DEFINED.</summary>
    internal sealed record BuildObservation(LazyJitInputs Inputs, IReadOnlyCollection<string> DefinedSymbols);

    /// <summary>
    /// Bundles the inputs that drive Phase 2 (semantic analysis) of the multi-file pipeline,
    /// reducing the parameter count of <see cref="RunPhase2SemanticAnalysis"/>.
    /// </summary>
    private sealed record Phase2Context(
        Language Language,
        RfBuildMode BuildMode,
        bool SaTiming,
        bool ShowBuildStages,
        bool RequireStartRoutine,
        Func<Language, SemanticVerifier.CompiledStdlibState?>? WarmProvider,
        Func<TypeModel.Symbols.RoutineInfo, bool>? InstanceCheckSkip = null,
        IReadOnlySet<string>? ResidentInstanceKeys = null);

    /// <summary>Groups the parameters for <see cref="RunPhase1BuildDriver"/>.</summary>
    private sealed record Phase1Context(
        string EntryFile,
        string ProjectRoot,
        string StdlibRoot,
        Language Language,
        IReadOnlyList<string>? LibraryRoots,
        Func<Language, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>?
            StdlibIndexProvider,
        bool ShowBuildStages,
        Stopwatch? SwBuild);

    /// <summary>Groups the parameters for <see cref="RunPhase3Codegen"/>.</summary>
    private sealed record Phase3Context(
        string EntryFile,
        string? OutputFile,
        TargetConfig Target,
        RfBuildMode BuildMode,
        bool SaTiming,
        bool DumpAst,
        bool ShowBuildStages,
        Action<string>? IrCallback,
        Stopwatch? SwPhase,
        IBuilderBackend Backend,
        Action<LazyJitInputs>? LazyJitSink = null,
        IReadOnlyCollection<string>? ResidentSymbols = null,
        Action<BuildObservation>? BuildObserver = null);

    /// <summary>
    /// The fully-resolved build configuration for a <c>build</c>/<c>buildandrun</c>/<c>check</c> invocation:
    /// entry file, project root, build mode, and the external-library link config. Produced by
    /// <see cref="ResolveEntryFile"/> from the CLI args + the nearest <c>config.toml</c>. A resolution
    /// FAILURE is signalled by <see cref="EntryFile"/> being null (all other fields keep their defaults).
    /// Replaces a former 12-tuple — the field count outgrew a tuple's readability.
    /// </summary>
    internal sealed record ResolvedEntry
    {
        /// <summary>The entry source file, or null when resolution failed (error already printed).</summary>
        public string? EntryFile { get; init; }

        /// <summary>The project root (manifest directory), used as the import search root.</summary>
        public string? ProjectRoot { get; init; }

        /// <summary>The optional explicit output file (codegen verb); null otherwise.</summary>
        public string? OutputFile { get; init; }

        /// <summary>The build optimization mode.</summary>
        public RfBuildMode BuildMode { get; init; } = RfBuildMode.Debug;

        /// <summary>Whether to dump the post-desugar AST alongside the build.</summary>
        public bool DumpAst { get; init; }

        /// <summary>Whether to print per-phase SA timings.</summary>
        public bool SaTiming { get; init; }

        /// <summary>Whether SA must find a <c>routine start()</c> (an executable build).</summary>
        public bool RequireStartRoutine { get; init; }

        /// <summary>Whether to print build-stage banners.</summary>
        public bool ShowBuildStages { get; init; }

        /// <summary>External RF library dependency directories (import search roots).</summary>
        public IReadOnlyList<string> LibraryRoots { get; init; } = [];

        /// <summary>Simple name-only C libraries to link (the <c>-l</c> names).</summary>
        public IReadOnlyList<string> CLibraries { get; init; } = [];

        /// <summary>Extra <c>-L</c> search directories for the C libraries.</summary>
        public IReadOnlyList<string> LibraryPaths { get; init; } = [];

        /// <summary>Richly-declared C libraries (<c>[libraries.NAME]</c>): linkage kind + calling convention.</summary>
        public IReadOnlyDictionary<string, CLibrary> LibraryConfigs { get; init; } =
            new Dictionary<string, CLibrary>();

        /// <summary>Route builds through a running warm daemon (manifest <c>[target] use-daemon</c>).</summary>
        public bool UseDaemon { get; init; }

        /// <summary>Use the ORC-JIT dev-loop path for buildandrun (manifest <c>mode = "debug-jit"</c>).</summary>
        public bool Jit { get; init; }

        /// <summary>The backend that translates the program (manifest <c>[target] backend</c>).</summary>
        public string Backend { get; init; } = BuilderBackends.DefaultName;

        /// <summary>Incremental JIT dev loop (manifest <c>[target] incremental</c>): fully-lazy on-demand JIT +
        /// per-routine IR cache. Also enables the resident-JIT base/delta split (daemon AOTs the stdlib base
        /// once + ships only the per-run delta IR) — base/delta has no separate opt-in.</summary>
        public bool Incremental { get; init; }
    }

    /// <summary>
    /// Resolves the <see cref="ResolvedEntry"/> for build/buildandrun/check commands.
    /// Searches for a config.toml manifest in all cases: when no entry file is given the
    /// manifest supplies the executable; when an explicit entry file is given it overrides
    /// [target] executable but the manifest's other settings still apply.
    /// ALL build configuration lives in the manifest's [target] section (executable, library,
    /// mode, dump-ast, sa-timing, show-build-stages) — the CLI deliberately takes no flags.
    /// On error the returned entry's <see cref="ResolvedEntry.EntryFile"/> is null.
    /// </summary>
    private static ResolvedEntry ResolveEntryFile(string[] args, bool needsOutputArg)
    {
        // args[0] is the command name (build/buildandrun/check)
        if (!ParsePositionalArgs(args: args,
                needsOutputArg: needsOutputArg,
                explicitEntry: out string? explicitEntry,
                outputFile: out string? outputFile))
        {
            return new ResolvedEntry();
        }

        // Explicit source file given — use it as the entry point, but still honor the
        // nearest config.toml (walking up from the file's directory): the manifest
        // remains the single source of build configuration (mode, library deps, debug
        // fields) even for single-file builds; only [target] executable is overridden.
        // .toml files are treated as manifests, not source files.
        if (explicitEntry != null && !explicitEntry.EndsWith(value: ".toml",
                comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return ResolveExplicitEntry(explicitEntry: explicitEntry, outputFile: outputFile);
        }

        return ResolveManifestEntry(explicitEntry: explicitEntry, outputFile: outputFile);
    }

    /// <summary>Parses the positional CLI args (<c>[entry-file] [out.ll]</c>) after the command name. Returns
    /// false (after printing an error) if an unknown <c>-</c>-prefixed option is encountered.</summary>
    private static bool ParsePositionalArgs(string[] args, bool needsOutputArg,
        out string? explicitEntry, out string? outputFile)
    {
        explicitEntry = null;
        outputFile = null;

        int i = 1;
        while (i < args.Length)
        {
            if (!args[i]
                   .StartsWith(value: '-'))
            {
                if (explicitEntry == null)
                {
                    explicitEntry = args[i];
                }
                else if (needsOutputArg && outputFile == null)
                {
                    outputFile = args[i];
                }

                i++;
            }
            else
            {
                Console.WriteLine(
                    value:
                    $"Error: unknown option '{args[i]}'. RazorForge takes no build flags — configure builds in config.toml ([target] executable, library, mode, ...).");
                return false;
            }
        }

        return true;
    }

    /// <summary>Resolves an explicitly-given source-file entry: honors the nearest config.toml (or debug
    /// defaults when manifest-less); only [target] executable is overridden by the command-line entry.</summary>
    private static ResolvedEntry ResolveExplicitEntry(string explicitEntry, string? outputFile)
    {
        if (!File.Exists(path: explicitEntry))
        {
            Console.WriteLine(value: $"Error: File '{explicitEntry}' not found.");
            return new ResolvedEntry();
        }

        string entryDir =
            Path.GetDirectoryName(path: Path.GetFullPath(path: explicitEntry)) ?? ".";
        string? nearbyManifest = ManifestLoader.FindManifest(startDir: entryDir);
        if (nearbyManifest == null)
        {
            // Truly manifest-less — debug defaults. Assume an executable build so
            // codegen knows to synthesize @main and SA can require a 'start' routine.
            DiagnosticFlags.Reset();
            return new ResolvedEntry
            {
                EntryFile = explicitEntry,
                ProjectRoot = entryDir,
                OutputFile = outputFile,
                RequireStartRoutine = true
            };
        }

        try
        {
            ProjectManifest manifest = ManifestLoader.Load(tomlPath: nearbyManifest,
                resolveExecutable: false);
            BuildTarget target = manifest.Target;
            RfBuildMode buildMode = ParseBuildMode(mode: target.Mode);

            if (manifest.Debug.ShowBuildStages)
            {
                Console.WriteLine(value: $"Using manifest: {nearbyManifest}");
                Console.WriteLine(
                    value:
                    $"Executable: {explicitEntry} ({target.Mode}, entry from command line)");
                if (target.Libraries.Count > 0)
                {
                    Console.WriteLine(
                        value:
                        $"Libraries: {string.Join(separator: ", ", values: target.Libraries)}");
                }
            }

            ApplyDiagnosticFlags(manifest: manifest);
            bool residentJit = BuilderBackends.Get(name: target.Backend).SupportsResidentJit;
            return new ResolvedEntry
            {
                EntryFile = explicitEntry,
                ProjectRoot = manifest.ManifestDirectory,
                OutputFile = outputFile,
                BuildMode = buildMode,
                DumpAst = manifest.Debug.DumpAst,
                SaTiming = manifest.Debug.Timing,
                RequireStartRoutine = true,
                ShowBuildStages = manifest.Debug.ShowBuildStages,
                LibraryRoots = target.Libraries,
                CLibraries = target.CLibraries,
                LibraryPaths = target.LibraryPaths,
                LibraryConfigs = target.LibraryConfigs,
                UseDaemon = target.UseDaemon && !DaemonDisabledByEnv(),
                Jit = ModeUsesJit(mode: target.Mode),
                Incremental = target.Incremental && residentJit,
                Backend = target.Backend
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                value: $"Error loading {ManifestLoader.ManifestFileName}: {ex.Message}");
            return new ResolvedEntry();
        }
    }

    /// <summary>Resolves the entry from a manifest: either a <c>.toml</c> passed explicitly, or (no explicit
    /// entry) the nearest config.toml found from the current directory. The manifest's [target] executable is
    /// the entry point.</summary>
    private static ResolvedEntry ResolveManifestEntry(string? explicitEntry, string? outputFile)
    {
        // No explicit entry (or .toml manifest given) — load manifest
        string? manifestPath;
        if (explicitEntry != null)
        {
            manifestPath = File.Exists(path: explicitEntry)
                ? Path.GetFullPath(path: explicitEntry)
                : null;
        }
        else
        {
            manifestPath = ManifestLoader.FindManifest(startDir: Environment.CurrentDirectory);
        }

        if (manifestPath == null)
        {
            if (explicitEntry != null)
            {
                Console.WriteLine(value: $"Error: Manifest '{explicitEntry}' not found.");
            }
            else
            {
                Console.WriteLine(
                    value: "Error: No entry file specified and no config.toml found.");
                Console.WriteLine(
                    value: "Either provide an entry file or create a config.toml manifest.");
            }

            return new ResolvedEntry();
        }

        try
        {
            ProjectManifest manifest = ManifestLoader.Load(tomlPath: manifestPath);
            BuildTarget target = manifest.Target;

            RfBuildMode buildMode = ParseBuildMode(mode: target.Mode);

            bool showBuildStages = manifest.Debug.ShowBuildStages;
            if (showBuildStages)
            {
                Console.WriteLine(value: $"Using manifest: {manifestPath}");
                Console.WriteLine(value: $"Executable: {target.Executable} ({target.Mode})");
                if (target.Libraries.Count > 0)
                {
                    Console.WriteLine(
                        value:
                        $"Libraries: {string.Join(separator: ", ", values: target.Libraries)}");
                }
            }

            ApplyDiagnosticFlags(manifest: manifest);
            bool residentJit = BuilderBackends.Get(name: target.Backend).SupportsResidentJit;
            return new ResolvedEntry
            {
                EntryFile = target.Executable,
                ProjectRoot = manifest.ManifestDirectory,
                OutputFile = outputFile,
                BuildMode = buildMode,
                DumpAst = manifest.Debug.DumpAst,
                SaTiming = manifest.Debug.Timing,
                RequireStartRoutine = true,
                ShowBuildStages = showBuildStages,
                LibraryRoots = target.Libraries,
                CLibraries = target.CLibraries,
                LibraryPaths = target.LibraryPaths,
                LibraryConfigs = target.LibraryConfigs,
                UseDaemon = target.UseDaemon && !DaemonDisabledByEnv(),
                Jit = ModeUsesJit(mode: target.Mode),
                Incremental = target.Incremental && residentJit,
                Backend = target.Backend
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                value: $"Error loading {ManifestLoader.ManifestFileName}: {ex.Message}");
            return new ResolvedEntry();
        }
    }

    /// <summary>
    /// Maps a [target] mode string to its <see cref="RfBuildMode"/>; throws on unknown modes.
    /// </summary>
    private static RfBuildMode ParseBuildMode(string mode)
    {
        return mode.ToLowerInvariant() switch
        {
            // `debug-jit` = the in-process ORC-JIT dev loop (same -O0 codegen as `debug`, but JIT-and-run
            // instead of AOT build+run). The JIT choice is derived from the mode string separately (see
            // ModeUsesJit); the optimization level here is Debug for both.
            "debug" or "debug-jit" => RfBuildMode.Debug,
            "release" => RfBuildMode.Release,
            "release-time" => RfBuildMode.ReleaseTime,
            "release-space" => RfBuildMode.ReleaseSpace,
            _ => throw new InvalidOperationException(
                message: $"Unknown build mode '{mode}' in [target]. " +
                         "Valid modes are: debug-jit, debug, release, release-time, release-space.")
        };
    }

    /// <summary>Whether a <c>[target] mode</c> string selects the in-process ORC-JIT dev loop.</summary>
    private static bool ModeUsesJit(string mode)
    {
        return string.Equals(a: mode?.Trim(),
            b: "debug-jit",
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Env kill-switch: <c>RF_NO_DAEMON=1</c> forces every build onto the cold in-process path,
    /// bypassing the warm daemon entirely. A safety valve for when the daemon misbehaves (e.g. a wedged
    /// pipe read in a constrained sandbox) — cold builds are slower but never depend on the daemon.
    /// The ORC-JIT dev loop still runs in-process (its daemon IR fetch is optional, with a cold branch),
    /// so <c>buildandrun</c> in <c>debug-jit</c> mode stays fast; only the daemon round-trip is skipped.</summary>
    private static bool DaemonDisabledByEnv()
    {
        string? v = Environment.GetEnvironmentVariable(variable: "RF_NO_DAEMON");
        return !string.IsNullOrEmpty(value: v) && v != "0";
    }

    /// <summary>
    /// Populates the process-wide <see cref="DiagnosticFlags"/> from a manifest's <c>[debug]</c> section,
    /// replacing the former <c>RF_*</c> / <c>RAZORFORGE_PHASE_TIMING</c> environment variables. Reset-then-set
    /// so a prior build's flags never linger (matters for the serial compile daemon).
    /// </summary>
    private static void ApplyDiagnosticFlags(ProjectManifest manifest)
    {
        DiagnosticFlags.Reset();
        DiagnosticFlags.PhaseTiming = manifest.Debug.Timing;
        DiagnosticFlags.PruneStats = manifest.Debug.PruneStats;
        DiagnosticFlags.JitTrace = manifest.Debug.JitTrace;
        DiagnosticFlags.DumpIr = manifest.Debug.DumpIr;
        DiagnosticFlags.DumpTessera = manifest.Debug.DumpTessera;
        DiagnosticFlags.ReachabilityDump = manifest.Debug.ReachabilityDump;
        DiagnosticFlags.MaySuspendDump = manifest.Debug.MaySuspendDump;
    }

    /// <summary>
    /// Prints the CLI usage instructions to standard output.
    /// </summary>
    private static void PrintUsage()
    {
        // The command name the user typed (the shipped short aliases are copies of the apphost), so
        // examples echo how the tool was actually invoked.
        string tool = CliRules.ToolName;
        string header = $"{CliRules.Name} v{CliRules.Version}";

        Console.WriteLine(value: header);
        Console.WriteLine();
        // The commands every Lumi language's tool shares (RazorForge, Suflae, Tessera). The others (parse,
        // tokenize, codegen, validate-stdlib, daemon, ...) still work for the builder's own development, but are
        // not part of the tool's face.
        Console.WriteLine(value: "Usage:");
        Console.WriteLine(value: $"  {tool} check [entry-file]               - Check the program without building it");
        Console.WriteLine(value: $"  {tool} build [entry-file]               - Build a native executable");
        Console.WriteLine(value: $"  {tool} run [entry-file]                 - Build the program and run it");
        Console.WriteLine(value: $"  {tool} test <dir-or-file>...            - Run test programs against their expected output");
        Console.WriteLine(value: $"  {tool} fmt [--check] <file-or-dir>...   - Format sources in place (--check: only list)");
        Console.WriteLine(value: $"  {tool} lint <file-or-dir>...            - Report code that is not in the canonical style");
        Console.WriteLine(value: $"  {tool} lsp                              - Run the language server (for an editor)");
        Console.WriteLine(value: $"  {tool} help                             - Show this help");
        Console.WriteLine(value: $"  {tool} version                          - Show the version");
        Console.WriteLine();
        Console.WriteLine(value: "  test: every source in each directory is built and run. Its standard output must equal");
        Console.WriteLine(value: "        <name>.expected (if present), its exit code <name>.exit (default 0); if <name>.error");
        Console.WriteLine(value: "        exists the build must fail with that text. <name>.input is its standard input.");
        Console.WriteLine();
        string kinds = string.Join(separator: ", ",
            values: Builder.Frontends.Languages.All.Select(selector: rules => $"{rules.FileExtension} for {rules.Name}"));
        Console.WriteLine(value: $"  <source-file>: {kinds}");
        Console.WriteLine(
            value: $"  A source with no known extension is read as {CliRules.Name}.");

        Console.WriteLine(
            value: "  If no entry file is given, searches for config.toml in the current");
        Console.WriteLine(value: "  directory and parent directories.");
        Console.WriteLine();
        Console.WriteLine(
            value: "  There are no build flags: all build configuration lives in config.toml's");
        Console.WriteLine(
            value: "  [target] section (executable, library, mode, show-build-stages, ...).");
    }

    /// <summary>Prints the running command's language and version to standard output.</summary>
    private static void PrintVersion()
    {
        Console.WriteLine(value: $"{CliRules.Name} v{CliRules.Version}");
    }

    /// <summary>The language a source file is compiled as. A registered extension decides; a file with
    /// none (an extension-less entry) is the running command's language.</summary>
    private static Language SourceLanguage(string path)
    {
        return Builder.Frontends.Languages.OfFile(fileName: path, fallback: CliLanguage);
    }

    /// <summary>
    /// Tokenizes the given source file and prints each token with its position and text to standard output.
    /// Returns 0 on success or 1 if the file is not found or tokenization fails.
    /// </summary>
    private static int TokenizeFile(string sourceFile)
    {
        if (!File.Exists(path: sourceFile))
        {
            Console.WriteLine(value: $"Error: File '{sourceFile}' not found.");
            return 1;
        }

        string code = File.ReadAllText(path: sourceFile);
        Language language = SourceLanguage(path: sourceFile);

        Console.WriteLine(
            value:
            $"Tokenizing {sourceFile} as {Builder.Frontends.Languages.For(language: language).Name}...");
        Console.WriteLine();

        try
        {
            List<Token> tokens = Builder.Tokenizer.Lexers.Tokenize(source: code, fileName: sourceFile, language: language);

            Console.WriteLine(value: $"Generated {tokens.Count} tokens:");
            Console.WriteLine();

            foreach (Token tok in tokens)
            {
                Console.WriteLine(
                    value:
                    $"  {tok.Line,4}:{tok.Column,-3} {tok.Type,-25} '{EscapeString(s: tok.Text)}'");
            }

            Console.WriteLine();
            Console.WriteLine(value: "Tokenization successful!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Tokenization failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Tokenizes and parses the given source file, then prints a summary of the resulting AST
    /// along with any warnings. Returns 0 on success or 1 if the file is not found or parsing fails.
    /// </summary>
    private static int ParseFile(string sourceFile)
    {
        if (!File.Exists(path: sourceFile))
        {
            Console.WriteLine(value: $"Error: File '{sourceFile}' not found.");
            return 1;
        }

        string code = File.ReadAllText(path: sourceFile);
        Language language = SourceLanguage(path: sourceFile);

        Console.WriteLine(
            value:
            $"Parsing {sourceFile} as {Builder.Frontends.Languages.For(language: language).Name}...");
        Console.WriteLine();

        try
        {

            // Tokenize
            Console.WriteLine(value: "=== TOKENIZATION ===");
            List<Token> tokens = Builder.Tokenizer.Lexers.Tokenize(source: code, fileName: sourceFile, language: language);
            Console.WriteLine(value: $"Generated {tokens.Count} tokens");

            // Parse
            Console.WriteLine();
            Console.WriteLine(value: "=== PARSING ===");
            var parser = new Builder.Parser.Parser(tokens: tokens, language: language, fileName: sourceFile);
            SyntaxTree.Program ast = parser.Parse();
            List<BuildWarning> warnings = parser.GetWarnings();

            Console.WriteLine(
                value: $"Successfully parsed! AST contains {ast.Declarations.Count} declarations");

            // Show warnings if any
            if (warnings.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== WARNINGS ({warnings.Count}) ===");
                foreach (BuildWarning warning in warnings)
                {
                    DiagnosticRenderer.Print(warning: warning);
                }
            }

            // Show AST summary
            Console.WriteLine();
            Console.WriteLine(value: "=== AST SUMMARY ===");
            foreach (ISyntaxTreeNode decl in ast.Declarations)
            {
                PrintDeclarationSummary(node: decl, indent: 0);
            }

            Console.WriteLine();
            Console.WriteLine(value: "Parsing successful!");
            return 0;
        }
        catch (GrammarException ex)
        {
            DiagnosticRenderer.Print(ex: ex);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: ex.Message);
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Runs the semantic analyzer over the standard library routine bodies for the given language
    /// and reports any errors found. Returns 0 if all bodies are valid, or 1 if errors were found.
    /// </summary>
    private static int ValidateStdlib(Language language)
    {
        try
        {
            string langName = Builder.Frontends.Languages.For(language: language).Name;
            Console.WriteLine(value: $"Validating {langName} stdlib routine bodies...");
            Console.WriteLine();

            var analyzer = new SemanticVerifier(language: language);
            List<SemanticError> stdlibErrors = analyzer.ValidateStdlibBodies();

            // Builder↔stdlib name-contract check: every routine/type/field name the builder
            // hard-codes against the stdlib must still resolve. A rename that breaks a contract
            // fails HERE (loudly) instead of silently miscompiling at runtime.
            List<string> contractErrors = analyzer.CheckRuntimeContract();

            if (stdlibErrors.Count == 0 && contractErrors.Count == 0)
            {
                Console.WriteLine(value: "All stdlib routine bodies validated successfully!");
                return 0;
            }

            if (contractErrors.Count > 0)
            {
                Console.WriteLine(
                    value: $"=== RUNTIME-CONTRACT ERRORS ({contractErrors.Count}) ===");
                Console.WriteLine(
                    value:
                    "  A name the builder hard-codes against the stdlib no longer resolves.");
                Console.WriteLine(
                    value:
                    "  Update src/Resolution/RuntimeContract.cs to match the stdlib rename.");
                foreach (string contractError in contractErrors)
                {
                    Console.WriteLine(value: $"    - {contractError}");
                }

                Console.WriteLine();
                if (stdlibErrors.Count == 0)
                {
                    return 1;
                }
            }

            // Group errors by file
            var errorsByFile = new Dictionary<string, List<SemanticError>>();
            foreach (SemanticError error in stdlibErrors)
            {
                string file = error.Location.FileName;
                if (!errorsByFile.TryGetValue(key: file, value: out List<SemanticError>? list))
                {
                    list = [];
                    errorsByFile[key: file] = list;
                }

                list.Add(item: error);
            }

            Console.WriteLine(
                value:
                $"=== STDLIB VALIDATION ERRORS ({stdlibErrors.Count} errors in {errorsByFile.Count} files) ===");
            Console.WriteLine();

            foreach ((string file, List<SemanticError> errors) in errorsByFile.OrderBy(
                         keySelector: kvp => kvp.Key))
            {
                Console.WriteLine(
                    value: $"  {Path.GetFileName(path: file)} ({errors.Count} errors):");
                foreach (SemanticError error in errors)
                {
                    DiagnosticRenderer.Print(error: error, indent: "    ");
                }

                Console.WriteLine();
            }

            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Stdlib validation failed: {ex.Message}");
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>Filters the build graph's units down to USER files (dropping the stdlib files already loaded
    /// by TypeRegistry/StdlibLoader — those under the normalized stdlib root).</summary>
    internal static List<FileBuildUnit> FilterUserUnits(BuildResult buildResult, string stdlibRoot)
    {
        string normalizedStdlib = Path.GetFullPath(path: stdlibRoot);
        return buildResult.Units
                          .Where(predicate: u => !Path.GetFullPath(path: u.FilePath)
                                                      .StartsWith(value: normalizedStdlib,
                                                           comparisonType: StringComparison
                                                              .OrdinalIgnoreCase))
                          .ToList();
    }

    /// <summary>Orders the user file units by module initialization order, appending any unit not covered by
    /// that order (e.g. an entry file with no module decl) in encounter order.</summary>
    internal static List<(SyntaxTree.Program Program, string FilePath)> OrderUserFiles(
        List<FileBuildUnit> userUnits, IReadOnlyList<string> initializationOrder)
    {
        // Map module names back to file units for ordering
        var unitsByModule =
            new Dictionary<string, FileBuildUnit>(comparer: StringComparer.OrdinalIgnoreCase);
        foreach (FileBuildUnit unit in userUnits)
        {
            string moduleName =
                unit.Module ?? Path.GetFileNameWithoutExtension(path: unit.FilePath);
            unitsByModule[key: moduleName] = unit;
        }

        var orderedFiles = new List<(SyntaxTree.Program Program, string FilePath)>();
        foreach (string moduleName in initializationOrder)
        {
            if (unitsByModule.TryGetValue(key: moduleName, value: out FileBuildUnit? unit))
            {
                orderedFiles.Add(item: (unit.Ast, unit.FilePath));
            }
        }

        // Fallback: if init order doesn't cover all units (e.g., entry file with no module decl)
        orderedFiles.AddRange(collection: userUnits
                                         .Where(predicate: unit =>
                                              !orderedFiles.Any(predicate: f =>
                                                  string.Equals(a: f.FilePath,
                                                      b: unit.FilePath,
                                                      comparisonType: StringComparison
                                                         .OrdinalIgnoreCase)))
                                         .Select(selector: unit => (unit.Ast, unit.FilePath)));

        return orderedFiles;
    }

    /// <summary>
    /// Runs the multi-file build pipeline: BuildDriver (parse + resolve imports + topo sort)
    /// -> SemanticVerifier.AnalyzeMultiple -> LlvmEmitter with multiple user programs.
    /// Returns 0 on success or 1 if any stage fails.
    /// </summary>
    private static int BuildMultiFile(string entryFile, string? outputFile,
        out IReadOnlyList<string> discoveredLinkLibraries, ResolvedEntry config,
        WarmProviders? warm = null)
    {
        string? projectRoot = config.ProjectRoot;
        RfBuildMode buildMode = config.BuildMode;
        bool dumpAst = config.DumpAst;
        bool saTiming = config.SaTiming;
        bool requireStartRoutine = config.RequireStartRoutine;
        bool showBuildStages = config.ShowBuildStages;
        IReadOnlyList<string>? libraryRoots = config.LibraryRoots;
        Func<Language, SemanticVerifier.CompiledStdlibState?>? warmProvider = warm?.WarmProvider;
        Action<string>? irCallback = warm?.IrCallback;
        Func<Language, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>?
            stdlibIndexProvider = warm?.StdlibIndexProvider;
        Action<LazyJitInputs>? lazyJitSink = warm?.LazyJitSink;
        Func<TypeModel.Symbols.RoutineInfo, bool>? instanceCheckSkip = warm?.InstanceCheckSkip;
        IReadOnlyCollection<string>? residentSymbols = warm?.ResidentSymbols;
        IReadOnlySet<string>? residentInstanceKeys = warm?.ResidentInstanceKeys;
        Action<BuildObservation>? buildObserver = warm?.BuildObserver;
        // C libraries declared in source via `@link("...")` on `C::` externs, gathered from the files
        // that actually compile (post `@target` gate) and surfaced to the link step. Assigned once the
        // AST is available; stays empty on the early-error paths below.
        discoveredLinkLibraries = [];
        if (!File.Exists(path: entryFile))
        {
            Console.WriteLine(value: $"Error: File '{entryFile}' not found.");
            return 1;
        }

        Language language = SourceLanguage(path: entryFile);

        if (showBuildStages)
        {
            Console.WriteLine(
                value:
                $"Building {entryFile} as {Builder.Frontends.Languages.For(language: language).Name} (multi-file)...");
            Console.WriteLine();
        }

        try
        {
            Stopwatch? _swBuild = DiagnosticFlags.PhaseTiming
                ? Stopwatch.StartNew()
                : null;
            projectRoot ??= Path.GetDirectoryName(path: Path.GetFullPath(path: entryFile)) ?? ".";
            string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();

            // Phase 1: Parse all files and resolve dependencies
            int phase1Result = RunPhase1BuildDriver(
                p1: new Phase1Context(EntryFile: entryFile,
                    ProjectRoot: projectRoot,
                    StdlibRoot: stdlibRoot,
                    Language: language,
                    LibraryRoots: libraryRoots,
                    StdlibIndexProvider: stdlibIndexProvider,
                    ShowBuildStages: showBuildStages,
                    SwBuild: _swBuild),
                orderedFiles: out List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
                unitsByFile: out Dictionary<string, FileBuildUnit> unitsByFile,
                driver: out BuildDriver driver,
                discoveredLinks: out discoveredLinkLibraries);
            if (phase1Result != 0)
            {
                return phase1Result;
            }

            // Phase 2: Semantic analysis (multi-file) — extracted to keep this method's complexity ≤15.
            var phase2Ctx = new Phase2Context(Language: language,
                BuildMode: buildMode,
                SaTiming: saTiming,
                ShowBuildStages: showBuildStages,
                RequireStartRoutine: requireStartRoutine,
                WarmProvider: warmProvider,
                InstanceCheckSkip: instanceCheckSkip,
                ResidentInstanceKeys: residentInstanceKeys);
            int phase2Result = RunPhase2SemanticAnalysis(ctx: phase2Ctx,
                driver: driver,
                orderedFiles: orderedFiles,
                swBuild: _swBuild,
                result: out AnalysisResult result,
                swPhase: out Stopwatch? _swPhase);
            if (phase2Result != 0)
            {
                return phase2Result;
            }

            // Phase 3: Code generation (multi-program)
            return RunPhase3Codegen(p3: new Phase3Context(EntryFile: entryFile,
                    OutputFile: outputFile,
                    Target: TargetConfig.ForCurrentHost(),
                    BuildMode: buildMode,
                    SaTiming: saTiming,
                    DumpAst: dumpAst,
                    ShowBuildStages: showBuildStages,
                    IrCallback: irCallback,
                    SwPhase: _swPhase,
                    Backend: BuilderBackends.Get(name: config.Backend),
                    LazyJitSink: lazyJitSink,
                    ResidentSymbols: residentSymbols,
                    BuildObserver: buildObserver),
                orderedFiles: orderedFiles,
                unitsByFile: unitsByFile,
                result: result);
        }
        catch (GrammarException ex)
        {
            DiagnosticRenderer.Print(ex: ex);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Build failed: {ex.Message}");
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Phase 2 of the multi-file build: constructs the semantic verifier (warm or cold path),
    /// runs <see cref="SemanticVerifier.AnalyzeMultiple"/>, reports errors and warnings, and
    /// checks that a <c>routine start()</c> is present when required. Returns 0 on success.
    /// </summary>
    private static int RunPhase2SemanticAnalysis(Phase2Context ctx, BuildDriver driver,
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles, Stopwatch? swBuild,
        out AnalysisResult result, out Stopwatch? swPhase)
    {
        if (ctx.ShowBuildStages)
        {
            Console.WriteLine();
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");
        }

        var target = TargetConfig.ForCurrentHost();
        // Warm path: a daemon supplies a fully-processed stdlib snapshot for this language, so the
        // restore ctor skips the stdlib desugaring/verification/monomorphization and only the user
        // program is analyzed. Cold path (WarmProvider == null) constructs a fresh verifier.
        // A cold-path fallback through the snapshot cache is deferred — it exposed a warm-restore
        // over-prune on complex programs. Fix that liveness gap first, then re-enable.
        SemanticVerifier.CompiledStdlibState? warmState =
            ctx.WarmProvider?.Invoke(arg: ctx.Language);
        // The timing flag drives both granular SA sub-phase lines and the coarse phase lines below,
        // via the single DiagnosticFlags.PhaseTiming source.
        SemanticVerifier analyzer = warmState != null
            ? new SemanticVerifier(language: ctx.Language,
                warm: warmState,
                target: target,
                buildMode: ctx.BuildMode)
            {
                SaTiming = ctx.SaTiming || DiagnosticFlags.PhaseTiming
            }
            : new SemanticVerifier(language: ctx.Language,
                target: target,
                buildMode: ctx.BuildMode)
            {
                SaTiming = ctx.SaTiming || DiagnosticFlags.PhaseTiming
            };
        if (swBuild != null)
        {
            Console.Error.WriteLine(
                value:
                $"[timing] warm-restore ctor (rebuild verifier from snapshot): {swBuild.ElapsedMilliseconds} ms");
            swBuild.Restart();
        }

        analyzer.Registry.UseModuleResolver(resolver: driver.Resolver);
        // Incremental JIT only: skip Phase-9 backend-repr+validate for instances whose IR is already cached
        // (they will be M2b codegen-cache hits, never re-emitted this run). Null on every other path.
        analyzer.SkipInstanceCheckIfIrCached = ctx.InstanceCheckSkip;
        // Resident-JIT base/delta: RegistryKeys already built into the base object — the demand collector skips
        // re-building/analyzing/expanding them (codegen extern-declares; JIT resolves into the base dylib).
        analyzer.ResidentInstanceKeys = ctx.ResidentInstanceKeys;
        swPhase = DiagnosticFlags.PhaseTiming
            ? Stopwatch.StartNew()
            : null;
        result = analyzer.AnalyzeMultiple(files: orderedFiles);
        if (swPhase != null)
        {
            Console.Error.WriteLine(
                value:
                $"[phase] AnalyzeMultiple (SA+instantiation+postproc): {swPhase.ElapsedMilliseconds} ms");
            swPhase.Restart();
        }

        if (ctx.ShowBuildStages)
        {
            Console.WriteLine(
                value: $"Routines registered: {result.Registry.GetAllRoutines().Count()}");
        }

        if (result.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.Error.WriteLine(value: $"=== ERRORS ({result.Errors.Count}) ===");
            DiagnosticRenderer.PrintAll(errors: result.Errors);
            Console.WriteLine();
            Console.Error.WriteLine(value: "Code generation aborted due to errors.");
            return 1;
        }

        if (result.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.Error.WriteLine(value: $"=== WARNINGS ({result.Warnings.Count}) ===");
            DiagnosticRenderer.PrintAll(warnings: result.Warnings);
        }

        if (ctx.RequireStartRoutine)
        {
            int startCheck = CheckStartRoutinePresent(orderedFiles: orderedFiles, result: result);
            if (startCheck != 0)
            {
                return startCheck;
            }
        }

        return 0;
    }

    /// <summary>
    /// Phase 1 of the multi-file build: drives the <see cref="BuildDriver"/> to parse all source files,
    /// resolve imports, and produce the topologically-ordered user file list. Returns 0 on success.
    /// </summary>
    private static int RunPhase1BuildDriver(Phase1Context p1,
        out List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        out Dictionary<string, FileBuildUnit> unitsByFile, out BuildDriver driver,
        out IReadOnlyList<string> discoveredLinks)
    {
        string entryFile = p1.EntryFile, projectRoot = p1.ProjectRoot, stdlibRoot = p1.StdlibRoot;
        Language language = p1.Language;
        IReadOnlyList<string>? libraryRoots = p1.LibraryRoots;
        Func<Language, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>?
            stdlibIndexProvider = p1.StdlibIndexProvider;
        bool showBuildStages = p1.ShowBuildStages;
        Stopwatch? swBuild = p1.SwBuild;
        orderedFiles = [];
        unitsByFile =
            new Dictionary<string, FileBuildUnit>(comparer: StringComparer.OrdinalIgnoreCase);
        discoveredLinks = [];

        if (showBuildStages)
        {
            Console.WriteLine(value: "=== BUILD DRIVER ===");
        }

        // Daemon-cached stdlib import index (built once): lets the driver skip the ~0.8 s per-request
        // stdlib re-parse. Null on a cold build → the driver parses the stdlib as before.
        IReadOnlyDictionary<string, string>? cachedStdlibIndex =
            stdlibIndexProvider?.Invoke(arg1: language, arg2: libraryRoots ?? []);
        driver = new BuildDriver(projectRoot: projectRoot,
            stdlibRoot: stdlibRoot,
            language: language,
            libraryRoots: libraryRoots,
            cachedStdlibIndex: cachedStdlibIndex);
        BuildResult buildResult = driver.CompileFile(entryFile: Path.GetFullPath(path: entryFile));

        if (swBuild != null)
        {
            Console.Error.WriteLine(
                value:
                $"[timing] build-driver (parse+module-resolve): {swBuild.ElapsedMilliseconds} ms");
            swBuild.Restart();
        }

        if (showBuildStages)
        {
            Console.WriteLine(value: $"Parsed {buildResult.Units.Count} file(s)");
        }

        if (buildResult.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.Error.WriteLine(value: $"=== BUILD ERRORS ({buildResult.Errors.Count}) ===");
            DiagnosticRenderer.PrintAll(errors: buildResult.Errors);
            Console.WriteLine();
            Console.Error.WriteLine(value: "Build aborted due to errors.");
            return 1;
        }

        if (buildResult.Warnings.Count > 0)
        {
            Console.Error.WriteLine(value: $"Warnings: {buildResult.Warnings.Count}");
            foreach (BuildWarning warning in buildResult.Warnings)
            {
                DiagnosticRenderer.Print(warning: warning);
            }
        }

        if (showBuildStages)
        {
            Console.WriteLine(
                value:
                $"Initialization order: {string.Join(separator: " -> ", values: buildResult.InitializationOrder)}");
        }

        List<FileBuildUnit> userUnits =
            FilterUserUnits(buildResult: buildResult, stdlibRoot: stdlibRoot);
        foreach (FileBuildUnit unit in userUnits)
        {
            unitsByFile[key: unit.FilePath] = unit;
        }

        orderedFiles = OrderUserFiles(userUnits: userUnits,
            initializationOrder: buildResult.InitializationOrder);
        discoveredLinks =
            CollectLinkLibraries(programs: orderedFiles.Select(selector: f => f.Program));
        return 0;
    }

    /// <summary>
    /// Verifies that a <c>routine start()</c> is present in the user programs (not stdlib). Returns
    /// 0 if found, 1 (with error printed) if missing.
    /// </summary>
    private static int CheckStartRoutinePresent(
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles, AnalysisResult result)
    {
        var userFilePaths = orderedFiles.Select(selector: f => f.FilePath)
                                        .ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);
        bool hasStartRoutine = result.Registry
                                     .GetAllRoutines()
                                     .Any(predicate: r =>
                                          r.OwnerType == null &&
                                          (r.Name == "start" ||
                                           r.BaseName.EndsWith(value: ".start")) &&
                                          r.Location != null &&
                                          userFilePaths.Contains(item: r.Location.FileName));
        if (!hasStartRoutine)
        {
            Console.WriteLine();
            Console.WriteLine(value: "Error: executable target has no 'start' routine. " +
                                     "Add 'routine start()' or 'routine start!()' to the entry module, " +
                                     "or set the target type to 'library' in config.toml.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Phase 3 of the multi-file build: runs LLVM IR codegen over the analyzed programs, optionally
    /// dumps the AST, and writes the IR to the output file or invokes the IR callback
    /// configured in <paramref name="p3"/>. Returns 0 on success.
    /// </summary>
    private static int RunPhase3Codegen(Phase3Context p3,
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        Dictionary<string, FileBuildUnit> unitsByFile, AnalysisResult result)
    {
        string entryFile = p3.EntryFile;
        string? outputFile = p3.OutputFile;
        TargetConfig target = p3.Target;
        RfBuildMode buildMode = p3.BuildMode;
        // OR with the process-wide PhaseTiming flag so codegen [CG] stage timings honor `[debug] timing` on
        // EVERY path (the daemon server-compile sets DiagnosticFlags.PhaseTiming from the request but does not
        // thread p3.SaTiming through) — mirrors the analyze-side merge (RunMultipleFullPipeline).
        bool saTiming = p3.SaTiming || DiagnosticFlags.PhaseTiming;
        bool dumpAst = p3.DumpAst, showBuildStages = p3.ShowBuildStages;
        Action<string>? irCallback = p3.IrCallback;
        Stopwatch? swPhase = p3.SwPhase;
        if (showBuildStages)
        {
            Console.WriteLine();
            Console.WriteLine(value: "=== CODE GENERATION ===");
        }

        var userPrograms = orderedFiles.Select(selector: f =>
                                        {
                                            string module =
                                                unitsByFile.TryGetValue(key: f.FilePath,
                                                    value: out FileBuildUnit? u)
                                                    ? u.Module ?? ""
                                                    : "";
                                            return (f.Program, f.FilePath, module);
                                        })
                                       .ToList();

        List<(SyntaxTree.Program Program, string FilePath, string Module)> stdlibPrograms =
            result.Registry.StdlibPrograms;

        // 9-2: instrument may-suspend routine bodies with cancellation push/pop markers
        // (no-op unless something reaches a coroutine suspend point). Mutates the userPrograms
        // ASTs in place — the same objects codegen consumes below.
        Builder.Lowering.Passes.CancellationInstrumentationPass.Run(programs: userPrograms,
            instantiatedBodies: result.InstantiatedGenericBodies,
            maySuspendKeys: result.MaySuspendRoutineKeys);

        // The entry module (manifest executable) is the module declared by the entry file —
        // it, not an arbitrary imported module's `start`, is the program entry point.
        string entryFull = Path.GetFullPath(path: entryFile);
        string? entryModule =
            unitsByFile.TryGetValue(key: entryFull, value: out FileBuildUnit? entryUnit)
                ? entryUnit.Module
                : null;

        // Resident-JIT incremental (B) M3: hand the analysis pieces to the caller (which builds a MAIN module
        // + on-demand materializer + disk IR cache and JIT-runs them lazily) INSTEAD of emitting the whole
        // program eagerly. CancellationInstrumentationPass has already run above, so the ASTs are codegen-ready.
        if (p3.LazyJitSink is { } lazyJitSink)
        {
            lazyJitSink(obj: new LazyJitInputs(UserPrograms: userPrograms,
                Result: result,
                Target: target,
                BuildMode: buildMode,
                EntryModule: entryModule));
            return 0;
        }

        var backendInput = new BackendInput
        {
            UserPrograms = userPrograms,
            StdlibPrograms = stdlibPrograms,
            Registry = result.Registry,
            Target = target,
            BuildMode = buildMode,
            SynthesizedBodies = result.SynthesizedBodies,
            InstantiatedGenericBodies = result.InstantiatedGenericBodies,
            LiveRoutineKeys = result.LiveRoutineKeys,
            MaySuspendRoutineKeys = result.MaySuspendRoutineKeys,
            EntryModule = entryModule,
            // Resident-JIT base/delta: when the daemon supplies the base's defined-symbol set, this
            // emission is the DELTA — it skips defining resident symbols and extern-declares them instead.
            ResidentSymbols = p3.ResidentSymbols,
            Timing = saTiming,
            SourceDumpPath = DiagnosticFlags.DumpTessera
                ? Path.ChangeExtension(path: entryFile, extension: ".tess")
                : null
        };

        // dump-ast dumps the EXACT AST the backend consumes — captured immediately BEFORE emission,
        // after all desugaring/monomorphization + the final CancellationInstrumentation mutation. The
        // backend is a pure translator, so this snapshot fully defines its input.
        if (dumpAst)
        {
            DumpCodegenInputAst(entryFile: entryFile, userPrograms: userPrograms,
                stdlibPrograms: stdlibPrograms, result: result, showBuildStages: showBuildStages);
        }

        BackendOutput emitted = p3.Backend.Emit(input: backendInput);
        string llvmIr = emitted.LlvmIr;

        // dump-tessera with another backend still writes the Tessera translation, for reading side by side.
        if (backendInput.SourceDumpPath is { } tesseraDump && p3.Backend is not Builder.TesseraEmit.TesseraBackend)
        {
            Builder.TesseraEmit.TesseraBackend.DumpSource(input: backendInput, path: tesseraDump);
        }
        if (swPhase != null)
        {
            Console.Error.WriteLine(
                value:
                $"[phase] {p3.Backend.Name} backend: {swPhase.ElapsedMilliseconds} ms ({llvmIr.Length} chars, {emitted.DefinedRoutineSymbols.Count} routines)");
        }

        if (showBuildStages)
        {
            Console.Error.WriteLine(value: $"Routines emitted: {emitted.DefinedRoutineSymbols.Count}");
        }

        WriteCodegenOutput(llvmIr: llvmIr, irCallback: irCallback, outputFile: outputFile,
            entryFile: entryFile, showBuildStages: showBuildStages);
        p3.BuildObserver?.Invoke(obj: new BuildObservation(
            Inputs: new LazyJitInputs(UserPrograms: userPrograms,
                Result: result,
                Target: target,
                BuildMode: buildMode,
                EntryModule: entryModule),
            DefinedSymbols: emitted.DefinedRoutineSymbols));

        if (showBuildStages)
        {
            Console.WriteLine();
            Console.WriteLine(value: "Build successful!");
        }

        return 0;
    }

    /// <summary>
    /// Writes the codegen-input AST snapshot (the exact AST LLVM codegen consumes, after all
    /// desugaring/monomorphization + CancellationInstrumentation) to <c>&lt;entry&gt;.rf.desugared</c>.
    /// </summary>
    private static void DumpCodegenInputAst(string entryFile,
        List<(SyntaxTree.Program Program, string FilePath, string Module)> userPrograms,
        List<(SyntaxTree.Program Program, string FilePath, string Module)> stdlibPrograms,
        AnalysisResult result, bool showBuildStages)
    {
        string astPath = Path.ChangeExtension(path: entryFile, extension: ".rf.desugared");
        string astText = new RfSyntaxTreePrinter().PrintMultiProgram(programs: userPrograms,
            synthesizedBodies: result.SynthesizedBodies,
            registry: result.Registry,
            stdlibPrograms: stdlibPrograms,
            instantiatedGenericBodies: result.InstantiatedGenericBodies);
        File.WriteAllText(path: astPath, contents: astText);
        if (showBuildStages)
        {
            Console.WriteLine(value: $"Codegen-input AST written to: {astPath}");
        }
    }

    /// <summary>
    /// Emits the generated LLVM IR: hands it to <paramref name="irCallback"/> IN MEMORY when set
    /// (the JIT path — no temp <c>.ll</c> write + read-back), otherwise writes it to the output file
    /// (defaulting to <c>&lt;entry&gt;.ll</c>).
    /// </summary>
    private static void WriteCodegenOutput(string llvmIr, Action<string>? irCallback,
        string? outputFile, string entryFile, bool showBuildStages)
    {
        if (irCallback != null)
        {
            irCallback(obj: llvmIr);
            return;
        }

        string outPath = outputFile ?? Path.ChangeExtension(path: entryFile, extension: ".ll");
        File.WriteAllText(path: outPath, contents: llvmIr);
        if (showBuildStages)
        {
            Console.WriteLine(value: $"LLVM IR written to: {outPath}");
        }
    }

    /// <summary>
    /// Runs the multi-file build pipeline through semantic analysis only (no codegen).
    /// Reports errors and warnings. Returns 0 if type-checking succeeds, 1 otherwise.
    /// </summary>
    private static int CheckMultiFile(string entryFile, string? projectRoot = null,
        IReadOnlyList<string>? libraryRoots = null)
    {
        if (!File.Exists(path: entryFile))
        {
            Console.WriteLine(value: $"Error: File '{entryFile}' not found.");
            return 1;
        }

        Language language = SourceLanguage(path: entryFile);

        Console.WriteLine(
            value:
            $"Checking {entryFile} as {Builder.Frontends.Languages.For(language: language).Name} (multi-file)...");
        Console.WriteLine();

        try
        {
            projectRoot ??= Path.GetDirectoryName(path: Path.GetFullPath(path: entryFile)) ?? ".";
            string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();

            // Phase 1: Parse all files and resolve dependencies
            Console.WriteLine(value: "=== BUILD DRIVER ===");
            var driver = new BuildDriver(projectRoot: projectRoot,
                stdlibRoot: stdlibRoot,
                language: language,
                libraryRoots: libraryRoots);
            BuildResult buildResult =
                driver.CompileFile(entryFile: Path.GetFullPath(path: entryFile));

            Console.WriteLine(value: $"Parsed {buildResult.Units.Count} file(s)");

            if (buildResult.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(
                    value: $"=== BUILD ERRORS ({buildResult.Errors.Count}) ===");
                DiagnosticRenderer.PrintAll(errors: buildResult.Errors);

                Console.WriteLine();
                Console.Error.WriteLine(value: "Check failed due to errors.");
                return 1;
            }

            if (buildResult.Warnings.Count > 0)
            {
                Console.Error.WriteLine(value: $"Warnings: {buildResult.Warnings.Count}");
                foreach (BuildWarning warning in buildResult.Warnings)
                {
                    DiagnosticRenderer.Print(warning: warning);
                }
            }

            // Filter out stdlib files, then order the user files in initialization order.
            List<FileBuildUnit> userUnits =
                FilterUserUnits(buildResult: buildResult, stdlibRoot: stdlibRoot);
            List<(SyntaxTree.Program Program, string FilePath)> orderedFiles =
                OrderUserFiles(userUnits: userUnits,
                    initializationOrder: buildResult.InitializationOrder);

            // Phase 2: Semantic analysis (multi-file) -> no codegen
            Console.WriteLine();
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");

            var analyzer = new SemanticVerifier(language: language);
            // Share the driver's fully-indexed resolver so SA-phase imports see the same
            // module set the build graph resolved (incl. [target] library directories).
            analyzer.Registry.UseModuleResolver(resolver: driver.Resolver);
            AnalysisResult result = analyzer.AnalyzeMultiple(files: orderedFiles);

            Console.WriteLine(
                value: $"Routines registered: {result.Registry.GetAllRoutines().Count()}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== ERRORS ({result.Errors.Count}) ===");
                DiagnosticRenderer.PrintAll(errors: result.Errors);

                Console.WriteLine();
                Console.Error.WriteLine(value: "Check failed due to errors.");
                return 1;
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== WARNINGS ({result.Warnings.Count}) ===");
                DiagnosticRenderer.PrintAll(warnings: result.Warnings);
            }

            Console.WriteLine();
            Console.WriteLine(value: "Check passed!");
            return 0;
        }
        catch (GrammarException ex)
        {
            DiagnosticRenderer.Print(ex: ex);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Check failed: {ex.Message}");
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    private static List<string> CollectLinkLibraries(IEnumerable<SyntaxTree.Program> programs)
    {
        var libs = new List<string>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (SyntaxTree.Program prog in programs)
        {
            foreach (ISyntaxTreeNode decl in prog.Declarations)
            {
                VisitLinkDeclaration(node: decl, libs: libs, seen: seen);
            }
        }

        return libs;
    }

    private static void ScanLinkAnnotations(List<string>? annotations, List<string> libs,
        HashSet<string> seen)
    {
        if (annotations == null)
        {
            return;
        }

        foreach (string ann in annotations)
        {
            (string? lib, string? _) = TypeModel.Symbols.LinkAnnotation.Parse(annotation: ann);
            if (lib != null && seen.Add(item: lib))
            {
                libs.Add(item: lib);
            }
        }
    }

    private static void VisitLinkDeclaration(ISyntaxTreeNode node, List<string> libs,
        HashSet<string> seen)
    {
        switch (node)
        {
            case RoutineDeclaration r:
                ScanLinkAnnotations(annotations: r.Annotations, libs: libs, seen: seen); break;
            case ExternalDeclaration e:
                ScanLinkAnnotations(annotations: e.Annotations, libs: libs, seen: seen); break;
            case ExternalBlockDeclaration b:
                foreach (SyntaxTree.Declaration d in b.Declarations)
                {
                    VisitLinkDeclaration(node: d, libs: libs, seen: seen);
                }

                break;
        }
    }

    /// <summary>
    /// Compiles a source file all the way to LLVM-IR TEXT (no opt/clang/link), returning the IR string.
    /// This is the front half of <see cref="BuildExecutable"/> — used by the ORC-JIT dev-loop path, which
    /// JITs the IR in-process instead of producing a native exe. Honors a warm-stdlib provider so a daemon
    /// can supply the fast path. Returns the build exit code (0 = success, and <paramref name="ir"/> holds
    /// the module); diagnostics are printed by <see cref="BuildMultiFile"/> as usual.
    /// </summary>
    private static int BuildToIr(string entryFile, out string ir, ResolvedEntry config,
        WarmProviders? warm = null)
    {
        string captured = "";
        int rc = BuildMultiFile(entryFile: entryFile,
            outputFile: null,
            discoveredLinkLibraries: out _,
            config: config,
            warm: new WarmProviders(WarmProvider: warm?.WarmProvider,
                IrCallback: s => captured = s,
                StdlibIndexProvider: warm?.StdlibIndexProvider,
                ResidentSymbols: warm?.ResidentSymbols,
                ResidentInstanceKeys: warm?.ResidentInstanceKeys,
                BuildObserver: warm?.BuildObserver));
        ir = captured;
        return rc;
    }

    /// <summary>Runs the full front pipeline like <see cref="BuildToIr"/> but STOPS before eager codegen and
    /// captures the analysis pieces (<see cref="LazyJitInputs"/>) instead — the resident-JIT incremental (B)
    /// path then builds a MAIN module + on-demand materializer from them (see <see cref="LazyJitPlanner"/>).</summary>
    private static int BuildToLazyJitInputs(string entryFile, out LazyJitInputs? inputs,
        ResolvedEntry config, WarmProviders? warm = null)
    {
        LazyJitInputs? captured = null;
        int rc = BuildMultiFile(entryFile: entryFile,
            outputFile: null,
            discoveredLinkLibraries: out _,
            config: config,
            warm: new WarmProviders(WarmProvider: warm?.WarmProvider,
                IrCallback: null,
                StdlibIndexProvider: warm?.StdlibIndexProvider,
                LazyJitSink: x => captured = x,
                InstanceCheckSkip: warm?.InstanceCheckSkip));
        inputs = captured;
        return rc;
    }

    /// <summary>
    /// Test-only in-process compile-to-IR entry. Resolves <paramref name="entryFile"/>'s manifest
    /// exactly as the <c>codegen</c> verb does, then runs the FULL front pipeline (tokenize → parse →
    /// declaration → desugaring → collection → verification → instantiation → codegen) WITHOUT
    /// opt/clang/link and WITHOUT executing the produced program, returning the build exit code
    /// (0 = success) and the emitted IR. This lets the test suite exercise the codegen/desugaring/
    /// collection/instantiation stack IN-PROCESS — a subprocess <c>buildandrun</c> runs that stack in
    /// a child process, invisible to coverage instrumentation. Diagnostics are printed to the console
    /// by <see cref="BuildMultiFile"/> as usual; the caller may redirect the console to capture them.
    /// </summary>
    internal static int CompileEntryToIrForTests(string entryFile, out string ir)
    {
        ResolvedEntry resolved =
            ResolveEntryFile(args: ["codegen", entryFile], needsOutputArg: false);
        if (resolved.EntryFile == null)
        {
            ir = "";
            return 1;
        }

        return BuildToIr(entryFile: resolved.EntryFile, ir: out ir, config: resolved);
    }

    private static int BuildExecutable(string entryFile, out string exeFile, ResolvedEntry config,
        Func<Language, SemanticVerifier.CompiledStdlibState?>? warmProvider = null)
    {
        IReadOnlyList<string> cLibraries = config.CLibraries;
        IReadOnlyList<string> libraryPaths = config.LibraryPaths;
        IReadOnlyDictionary<string, CLibrary> libraryConfigs = config.LibraryConfigs;

        // Remove stale per-target outputs before rebuilding.
        string llFile = Path.ChangeExtension(path: entryFile, extension: ".ll");
        string optFile = Path.ChangeExtension(path: llFile, extension: ".opt.ll");
        exeFile = Path.ChangeExtension(path: llFile, extension: ".exe");
        NativeToolchain.CleanBuildAndRunOutputs(llFile: llFile,
            optFile: optFile,
            exeFile: exeFile);

        // Build first (to a temp .ll file). BuildMultiFile also reports any `@link(...)` C libraries
        // declared in the compiled source.
        int buildResult = BuildMultiFile(entryFile: entryFile,
            outputFile: llFile,
            discoveredLinkLibraries: out IReadOnlyList<string> discoveredLinks,
            config: config,
            warm: warmProvider != null
                ? new WarmProviders(WarmProvider: warmProvider,
                    IrCallback: null,
                    StdlibIndexProvider: null)
                : null);
        if (buildResult != 0)
        {
            return buildResult;
        }

        // Merge manifest [target] c_libraries with source `@link(...)` directives (manifest first),
        // de-duplicated, for the link step. A source `@link(lib: "X")` name is remapped through a
        // [libraries.X] declaration's `name` override (e.g. "SDL2" → "SDL2-2.0") when present, so the
        // real `-l` link name is used. The declared libraries' own names are also linked.
        var allCLibraries = new List<string>();

        void AddLib(string lib)
        {
            string resolved = libraryConfigs.TryGetValue(key: lib, value: out CLibrary? cfg)
                ? cfg.Name
                : lib;
            if (!allCLibraries.Contains(item: resolved))
            {
                allCLibraries.Add(item: resolved);
            }
        }

        foreach (string lib in cLibraries)
        {
            AddLib(lib: lib);
        }

        foreach (CLibrary cfg in libraryConfigs.Values)
        {
            AddLib(lib: cfg.Name);
        }

        foreach (string lib in discoveredLinks)
        {
            AddLib(lib: lib);
        }

        return LinkAndStageExecutable(exeFile: exeFile,
            llFile: llFile,
            optFile: optFile,
            buildMode: config.BuildMode,
            allCLibraries: allCLibraries,
            libraryPaths: libraryPaths,
            libraryConfigs: libraryConfigs);
    }

    /// <summary>
    /// Optimizes, links, and stages the runtime DLLs after code generation, factored out of
    /// <see cref="BuildExecutable"/> to reduce its cognitive complexity.
    /// </summary>
    private static int LinkAndStageExecutable(string exeFile, string llFile, string optFile,
        RfBuildMode buildMode, List<string> allCLibraries, IReadOnlyList<string> libraryPaths,
        IReadOnlyDictionary<string, CLibrary> libraryConfigs)
    {
        string exeDir;
        string runtimeLibDir;
        try
        {
            exeDir = NativeToolchain.ResolveExecutableDirectory();
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to resolve native runtime layout: {ex.Message}");
            return 1;
        }

        if (NativeToolchain.TryFindNativeBuildDirectory(exeDir: exeDir,
                nativeBuildDir: out string nativeBuildDir))
        {
            // Development checkout: rebuild the native runtime incrementally before linking.
            int nativeResult = NativeToolchain.BuildNativeRuntime(exeDir: exeDir,
                nativeBuildDir: nativeBuildDir);
            if (nativeResult != 0)
            {
                return nativeResult;
            }

            runtimeLibDir = Path.Combine(path1: nativeBuildDir, path2: "lib");
        }
        else if (File.Exists(path: Path.Combine(path1: exeDir,
                     path2: NativeToolchain.RuntimeLinkLibraryFileName)))
        {
            // Installed/published layout: prebuilt runtime artifacts ship flat next to the
            // executable (csproj LinkBase="." / the packaging scripts) — nothing to rebuild.
            runtimeLibDir = exeDir;
        }
        else
        {
            Console.WriteLine(
                value:
                $"Failed to resolve the RazorForge native runtime: expected either a development 'Ingrid/native/build' tree near the executable, or '{NativeToolchain.RuntimeLinkLibraryFileName}' next to it (installed layout).");
            return 1;
        }

        // Optimize the emitted IR, then link it into a native executable. Ingrid's Tessera library goes
        // in first (its routines are not in the runtime DLL: the allocators, the divide primitives, the
        // math), and optimized builds (anything but -O0 debug) internalize during opt, so those inline
        // across the RF / runtime seam.
        if (!NativeToolchain.TryLinkIngridTessera(exeDir: exeDir,
                llFile: llFile,
                linkedFile: out string withIngrid,
                error: out string ingridError))
        {
            Console.WriteLine(value: ingridError);
            return 1;
        }

        int optResult = NativeToolchain.OptimizeIr(llFile: withIngrid,
            optFile: optFile,
            buildMode: buildMode,
            internalizeForLto: buildMode != RfBuildMode.Debug);
        if (optResult != 0)
        {
            return optResult;
        }

        int linkResult = NativeToolchain.LinkExecutable(optFile: optFile,
            exeFile: exeFile,
            runtimeLibDir: runtimeLibDir,
            buildMode: buildMode,
            cLibraries: allCLibraries,
            libraryPaths: libraryPaths);
        if (linkResult != 0)
        {
            return linkResult;
        }

        // Copy the runtime DLL (and its shared-library dependencies) next to the
        // output .exe so the loader can find them at runtime.
        NativeToolchain.StageRuntimeDlls(exeDir: exeDir, exeFile: exeFile);
        // Also stage each dynamically-linked @link/c_libraries dependency DLL from the -L search paths,
        // so a freshly-built exe runs without the user hand-copying its foreign libraries.
        NativeToolchain.StageUserLibraryDlls(exeFile: exeFile,
            cLibraries: allCLibraries,
            libraryPaths: libraryPaths,
            libraryConfigs: libraryConfigs);
        return 0;
    }

    private static int BuildAndRun(string entryFile, ResolvedEntry config,
        Func<Language, SemanticVerifier.CompiledStdlibState?>? warmProvider = null)
    {
        int buildResult = BuildExecutable(entryFile: entryFile,
            exeFile: out string exeFile,
            config: config,
            warmProvider: warmProvider);
        if (buildResult != 0)
        {
            return buildResult;
        }

        return RunExecutable(exeFile: exeFile, showBuildStages: config.ShowBuildStages);
    }

    /// <summary>
    /// Runs an already-built native executable, forwarding stdin and faithfully draining stdout/stderr
    /// (UTF-8), and returns its exit code. Factored out of <see cref="BuildAndRun"/> so the daemon client
    /// can run the exe locally after a warm build performed the compile in the daemon process.
    /// </summary>
    private static int RunExecutable(string exeFile, bool showBuildStages)
    {
        if (showBuildStages)
        {
            Console.WriteLine();
            Console.WriteLine(value: "=== EXECUTION ===");
        }

        bool stdinIsPiped = Console.IsInputRedirected;
        var psi = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(path: exeFile),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinIsPiped,
            // RF programs write UTF-8 (rf_runtime_init sets SetConsoleOutputCP(65001) on
            // Windows and stdlib paths encode every Text via UTF-8). Without these explicit
            // encodings, .NET's StreamReader defaults to the parent's Console.OutputEncoding
            // (system ACP — CP949 / CP1252 / etc. depending on locale) and rewrites every
            // non-ACP byte as `?`, garbling all non-ASCII output (Korean, emoji, accented
            // Latin, …). Setting these to UTF-8 makes the readers byte-faithful.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using var process = Process.Start(startInfo: psi);
            if (process == null)
            {
                Console.WriteLine(value: "Error: Failed to start the compiled executable.");
                return 1;
            }

            // Forward our stdin to the child CONCURRENTLY — never synchronously before draining
            // the child's output. When this process's own stdin is a redirected pipe that never
            // reaches EOF (the common in-harness / CI case), a synchronous CopyTo blocks forever:
            // it waits for our stdin to end while the child fills its stdout pipe with nobody
            // draining it, so both sides wedge (the long-standing "buildandrun stalls in harness"
            // bug). On a background task the copy can't stall the output drain; it ends when our
            // stdin closes or the child's stdin pipe does. It's a background thread, so a copy that
            // never completes (parent stdin held open) does not keep the process alive.
            if (stdinIsPiped)
            {
                _ = Task.Run(action: () =>
                {
                    try
                    {
                        Console.OpenStandardInput()
                               .CopyTo(destination: process.StandardInput.BaseStream);
                        process.StandardInput.Close();
                    }
                    catch
                    {
                        // Child exited / its stdin pipe closed — nothing left to forward.
                    }
                });
            }

            // Relay stdout and stderr CONCURRENTLY and as they arrive. Reading them sequentially
            // deadlocks whenever the child fills the OS stderr pipe buffer while we are still blocked
            // on stdout. Relaying as the output arrives (not once the child exits) keeps an interactive
            // program's output live, and leaves what a hung or crashed program printed in our output,
            // where a harness timeout can read it.
            Task stdoutTask = RelayAsync(source: process.StandardOutput, target: Console.Out);
            Task stderrTask = RelayAsync(source: process.StandardError, target: Console.Error);
            Task.WaitAll(stdoutTask, stderrTask);
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to execute {exeFile}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Copies a child's output stream to <paramref name="target"/> chunk by chunk, flushing each
    /// chunk, until the child closes the stream.</summary>
    private static async Task RelayAsync(StreamReader source, TextWriter target)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await source.ReadAsync(buffer: buffer, index: 0, count: buffer.Length)
                                   .ConfigureAwait(continueOnCapturedContext: false)) > 0)
        {
            target.Write(buffer: buffer, index: 0, count: read);
            target.Flush();
        }
    }
}
