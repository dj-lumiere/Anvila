using System.Diagnostics;
using System.Runtime.InteropServices;
using Builder.Targeting;

namespace Builder;

/// <summary>
/// Native toolchain driver: resolves the LLVM tools (clang/opt) and the bundled native
/// runtime, optimizes/links emitted LLVM IR into an executable, stages the runtime's shared
/// libraries, and cleans stale build artifacts. Split out of the CLI entry point so the CLI
/// entry point only parses arguments and dispatches; all platform/linker knowledge lives here.
/// </summary>
internal static class NativeToolchain
{
    // Bare tool names used both as the resolution fallback (PATH lookup) and as the
    // sentinel meaning "tool was not found in a bundled/explicit LLVM_HOME location".
    private const string ClangToolName = "clang";
    private const string OptToolName = "opt";
    private const string LlvmLinkToolName = "llvm-link";
    private const string CMakeToolName = "cmake";

    // The native runtime's source/build subdirectory name (development checkout layout).
    private const string NativeDirName = "native";

    /// <summary>Where a development checkout keeps the native runtime: Ingrid's <c>native</c> folder at the
    /// repository root.</summary>
    private static readonly string CheckoutNativeDir = Path.Combine(path1: "Ingrid", path2: "native");

    /// <summary>
    /// Native DLLs a compiled program needs next to its .exe on Windows: the runtime
    /// itself plus the shared libraries it links dynamically (bdwgc builds as a shared
    /// gc.dll; whether the runtime's import table retains it varies by linker, so it is
    /// staged whenever present).
    /// </summary>
    private static readonly string[] NativeRuntimeDlls =
    [
        "razorforge_runtime.dll"
    ];

    /// <summary>The platform-specific link-time artifact of the bundled native runtime.</summary>
    internal static string RuntimeLinkLibraryFileName
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return "razorforge_runtime.lib";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "librazorforge_runtime.dylib";
            }

            return "librazorforge_runtime.so";
        }
    }

    /// <summary>
    /// Resolves the directory containing the running RazorForge assembly.
    /// </summary>
    internal static string ResolveExecutableDirectory()
    {
        string? exeDir = Path.GetDirectoryName(path: typeof(NativeToolchain).Assembly.Location);
        return exeDir ?? throw new InvalidOperationException(
            message: "Unable to resolve the RazorForge executable directory.");
    }

    /// <summary>
    /// Locates the native runtime's CMake build tree (development checkouts only).
    /// Installed/published layouts ship prebuilt artifacts flat next to the executable
    /// and have no source tree — callers must treat a miss as "use the installed layout".
    /// </summary>
    internal static bool TryFindNativeBuildDirectory(string exeDir, out string nativeBuildDir)
    {
        // A directory given with a trailing separator would otherwise spend its first step up on the same folder.
        string? current = Path.TrimEndingDirectorySeparator(path: exeDir);
        for (int i = 0; i < 6 && current != null; i++)
        {
            string candidate = Path.Combine(path1: current, path2: CheckoutNativeDir, path3: "build");
            if (File.Exists(path: Path.Combine(path1: candidate, path2: "build.ninja")) ||
                File.Exists(path: Path.Combine(path1: candidate, path2: "Makefile")))
            {
                nativeBuildDir = candidate;
                return true;
            }

            current = Path.GetDirectoryName(path: current);
        }

        nativeBuildDir = "";
        return false;
    }

    /// <summary>
    /// The macOS SDK path from `xcrun --show-sdk-path` (Command Line Tools). Apple's own
    /// clang infers the SDK automatically, but the bundled LLVM clang defaults to `/`,
    /// where modern macOS keeps no libSystem stubs — every link needs -isysroot.
    /// </summary>
    private static readonly Lazy<string?> MacSdkPath = new(valueFactory: () =>
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/xcrun",
                Arguments = "--show-sdk-path",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(startInfo: psi);
            if (proc == null)
            {
                return null;
            }

            string path = proc.StandardOutput
                              .ReadToEnd()
                              .Trim();
            proc.WaitForExit();
            return proc.ExitCode == 0 && Directory.Exists(path: path)
                ? path
                : null;
        }
        catch
        {
            return null;
        }
    });

    internal static int BuildNativeRuntime(string exeDir, string nativeBuildDir)
    {
        string buildArgs = $"--build \"{nativeBuildDir}\"";

        try
        {
            // Every build in a checkout and every `dotnet build` (Ingrid/native/build.sh) works in this one build
            // tree. Two at once (the RazorForge and Suflae test suites side by side) could configure it while the
            // other builds in it, so each holds the tree's lock while it builds and copies.
            using FileStream held = LockNativeBuildTree(nativeBuildDir: nativeBuildDir);
            // Resolve cmake to an absolute path (rather than letting Process.Start search PATH at
            // launch) so the tool directory is fixed and a cmake planted earlier on PATH can't be
            // picked up. A missing tool throws and is reported by the catch below.
            var psi = new ProcessStartInfo
            {
                FileName = CMakeTool.Value,
                Arguments = buildArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo: psi);
            if (process == null)
            {
                throw new InvalidOperationException(message: "Failed to start cmake.");
            }

            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Console.Error.Write(value: stderr);
                Console.WriteLine(
                    value:
                    $"Native runtime build failed (cmake exited with code {process.ExitCode})");
                return 1;
            }

            string nativeBinDir = Path.Combine(path1: nativeBuildDir, path2: "bin");
            string nativeLibDir = Path.Combine(path1: nativeBuildDir, path2: "lib");
            string exeNativeBinDir = Path.Combine(path1: exeDir,
                path2: NativeDirName,
                path3: "build",
                path4: "bin");
            string exeNativeLibDir = Path.Combine(path1: exeDir,
                path2: NativeDirName,
                path3: "build",
                path4: "lib");

            CopyDirectoryFiles(srcDir: nativeBinDir, dstDir: exeNativeBinDir);
            CopyDirectoryFiles(srcDir: nativeLibDir, dstDir: exeNativeLibDir);

            // Also copy DLLs to the exe root (matches csproj LinkBase="." behavior).
            // The builder itself P/Invokes razorforge_runtime.dll, so the target file may be
            // locked by this process. In that case the already-loaded copy is what this run
            // will use anyway — warn and continue rather than failing the build.
            if (Directory.Exists(path: nativeBinDir))
            {
                foreach (string dll in Directory.GetFiles(path: nativeBinDir,
                             searchPattern: "*.dll"))
                {
                    string dst = Path.Combine(path1: exeDir, path2: Path.GetFileName(path: dll));
                    TryCopyTolerant(src: dll, dst: dst);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to build native runtime: {ex.Message}");
            Console.WriteLine(
                value: "Make sure CMake is installed and the native runtime has been configured.");
            return 1;
        }
    }

    /// <summary>The file the native runtime's build tree is locked with, shared with Ingrid/native/build.sh
    /// (<c>flock</c> on the same file).</summary>
    internal const string NativeBuildLockFileName = ".build.lock";

    /// <summary>Takes the lock on the native runtime's build tree, waiting while another build holds it.</summary>
    private static FileStream LockNativeBuildTree(string nativeBuildDir)
    {
        string path = Path.Combine(path1: nativeBuildDir, path2: NativeBuildLockFileName);
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path: path, mode: FileMode.OpenOrCreate, access: FileAccess.ReadWrite,
                    share: FileShare.None);
            }
            catch (IOException) when (waited.Elapsed < TimeSpan.FromMinutes(minutes: 10))
            {
                Thread.Sleep(millisecondsTimeout: 100);
            }
        }
    }

    private static void CopyDirectoryFiles(string srcDir, string dstDir)
    {
        if (!Directory.Exists(path: srcDir))
        {
            return;
        }

        Directory.CreateDirectory(path: dstDir);
        foreach (string file in Directory.GetFiles(path: srcDir))
        {
            string dst = Path.Combine(path1: dstDir, path2: Path.GetFileName(path: file));
            TryCopyTolerant(src: file, dst: dst);
        }
    }

    // Copies a file, tolerating sharing violations when the target is already loaded into this
    // process (e.g. razorforge_runtime.dll, which the builder itself P/Invokes). On Windows, a
    // loaded DLL is locked against overwrite but can still be renamed — so we move the locked
    // file aside under a unique name and then copy the fresh one into the original path. This
    // guarantees the on-disk artifact is always up to date; the renamed sidecar is harmless and
    // gets cleaned up on a future run when nothing has it open.
    private static void TryCopyTolerant(string src, string dst)
    {
        try
        {
            File.Copy(sourceFileName: src, destFileName: dst, overwrite: true);
            return;
        }
        catch (IOException) when (File.Exists(path: dst))
        {
            // Fall through to rename-aside fallback.
        }

        try
        {
            string staleSidecar = $"{dst}.stale-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}";
            File.Move(sourceFileName: dst, destFileName: staleSidecar);
            File.Copy(sourceFileName: src, destFileName: dst, overwrite: false);
            TryDeleteSidecars(targetPath: dst);
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                value:
                $"Warning: could not refresh '{dst}' ({ex.Message}). Using existing copy; rerun to pick up changes.");
        }
    }

    // Best-effort cleanup of previously renamed-aside DLL sidecars. Files still locked by
    // running processes will throw and are ignored.
    private static void TryDeleteSidecars(string targetPath)
    {
        string? dir = Path.GetDirectoryName(path: targetPath);
        if (dir == null)
        {
            return;
        }

        string prefix = Path.GetFileName(path: targetPath) + ".stale-";
        try
        {
            foreach (string old in Directory.EnumerateFiles(path: dir,
                         searchPattern: prefix + "*"))
            {
                try { File.Delete(path: old); }
                catch
                {
                    /* still locked — leave it */
                }
            }
        }
        catch
        {
            /* directory access issue — non-fatal */
        }
    }

    /// <summary>
    /// Resolves an LLVM toolchain tool (clang/opt) to a concrete path. Resolution order:
    /// 1. RAZORFORGE_LLVM_HOME/bin/&lt;tool&gt; — explicit user override.
    /// 2. &lt;dir of RazorForge executable&gt;/toolchain/bin/&lt;tool&gt; — self-contained release
    ///    packages bundle a relocatable LLVM (llvm-mingw on Windows) there.
    /// 3. The bare tool name, resolved from PATH (dev setups).
    /// </summary>
    private static string ResolveToolchainTool(string name)
    {
        string exeName = OperatingSystem.IsWindows()
            ? name + ".exe"
            : name;

        string? llvmHome = Environment.GetEnvironmentVariable(variable: "RAZORFORGE_LLVM_HOME");
        if (!string.IsNullOrWhiteSpace(value: llvmHome))
        {
            string fromEnv = Path.Combine(path1: llvmHome, path2: "bin", path3: exeName);
            if (File.Exists(path: fromEnv))
            {
                return fromEnv;
            }
        }

        string bundled = Path.Combine(path1: AppContext.BaseDirectory,
            path2: "toolchain",
            path3: "bin",
            path4: exeName);
        return File.Exists(path: bundled)
            ? bundled
            : name;
    }

    private static readonly Lazy<string> ClangTool =
        new(valueFactory: () => ResolveToolchainTool(name: ClangToolName));

    /// <summary>
    /// Hands the clang this build resolved (a release package's bundled one included) to the Tessera builder, which
    /// runs in this process and otherwise looks for clang on PATH only: it asks clang for the target's CPU features.
    /// A <c>TESSERA_CLANG</c> the user set wins.
    /// </summary>
    public static void ShareClangWithTessera()
    {
        if (string.IsNullOrEmpty(value: Environment.GetEnvironmentVariable(variable: "TESSERA_CLANG")) &&
            ClangTool.Value != ClangToolName)
        {
            Environment.SetEnvironmentVariable(variable: "TESSERA_CLANG", value: ClangTool.Value);
        }
    }

    private static readonly Lazy<string> OptTool =
        new(valueFactory: () => ResolveToolchainTool(name: OptToolName));

    private static readonly Lazy<string> LlvmLinkTool =
        new(valueFactory: () => ResolveToolchainTool(name: LlvmLinkToolName));

    /// <summary>
    /// Resolves a bare executable name to an absolute path by scanning the directories listed in
    /// the PATH environment variable (appending the executable extension on Windows) and returning
    /// the first existing match. Launching by the resolved absolute path — rather than handing a
    /// bare name to <see cref="Process.Start(ProcessStartInfo)"/> and letting the OS search PATH —
    /// fixes the tool directory so an executable planted earlier on PATH cannot be run instead.
    /// Throws when the tool is not found on PATH.
    /// </summary>
    private static string ResolveOnPath(string name)
    {
        string exeName = OperatingSystem.IsWindows()
            ? name + ".exe"
            : name;

        string? pathVar = Environment.GetEnvironmentVariable(variable: "PATH");
        if (!string.IsNullOrEmpty(value: pathVar))
        {
            foreach (string dir in pathVar.Split(separator: Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(value: dir))
                {
                    continue;
                }

                string candidate = Path.Combine(path1: dir.Trim(), path2: exeName);
                if (File.Exists(path: candidate))
                {
                    return Path.GetFullPath(path: candidate);
                }
            }
        }

        throw new InvalidOperationException(
            message:
            $"Required build tool '{name}' was not found in any PATH directory.");
    }

    private static readonly Lazy<string> CMakeTool =
        new(valueFactory: () => ResolveOnPath(name: CMakeToolName));

    private static void ConfigureToolchainEnvironment(ProcessStartInfo psi, string toolPath)
    {
        if (toolPath == ClangToolName || toolPath == OptToolName)
        {
            return;
        }

        string? binDir = Path.GetDirectoryName(path: toolPath);
        string? toolchainDir = binDir == null
            ? null
            : Path.GetDirectoryName(path: binDir);
        if (toolchainDir == null)
        {
            return;
        }

        string libDir = Path.Combine(path1: toolchainDir, path2: "lib");
        if (!Directory.Exists(path: libDir))
        {
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            PrependEnvironmentPath(psi: psi, variableName: "LD_LIBRARY_PATH", path: libDir);
        }
        else if (OperatingSystem.IsMacOS())
        {
            PrependEnvironmentPath(psi: psi, variableName: "DYLD_LIBRARY_PATH", path: libDir);
        }
    }

    private static void PrependEnvironmentPath(ProcessStartInfo psi, string variableName,
        string path)
    {
        psi.Environment.TryGetValue(key: variableName, value: out string? existing);
        psi.Environment[key: variableName] = string.IsNullOrWhiteSpace(value: existing)
            ? path
            : path + Path.PathSeparator + existing;
    }

    /// <summary>
    /// True when the resolved clang targets *-windows-gnu (llvm-mingw). The bundled Windows
    /// toolchain is mingw-based so linking is self-contained (no Visual Studio / Windows SDK
    /// import libraries needed); its GNU-flavored linker rejects lld-link style /flags, so the
    /// link command line must be built differently.
    /// </summary>
    private static readonly Lazy<bool> ClangIsMingw = new(valueFactory: () =>
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ClangTool.Value,
                Arguments = "-dumpmachine",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ConfigureToolchainEnvironment(psi: psi, toolPath: ClangTool.Value);
            using var proc = Process.Start(startInfo: psi);
            if (proc == null)
            {
                return false;
            }

            string triple = proc.StandardOutput
                                .ReadToEnd()
                                .Trim();
            proc.WaitForExit();
            return triple.Contains(value: "mingw") || triple.Contains(value: "windows-gnu");
        }
        catch
        {
            return false;
        }
    });

    /// <summary>
    /// Returns the full path to the compiler-rt builtins library (e.g. clang_rt.builtins-x86_64.lib)
    /// by asking clang where it lives.
    /// </summary>
    private static string? GetCompilerRtBuiltinsLib()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ClangTool.Value,
                Arguments = "--print-libgcc-file-name --rtlib=compiler-rt",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ConfigureToolchainEnvironment(psi: psi, toolPath: ClangTool.Value);
            using var proc = Process.Start(startInfo: psi);
            if (proc == null)
            {
                return null;
            }

            string output = proc.StandardOutput
                                .ReadToEnd()
                                .Trim();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && File.Exists(path: output))
            {
                return output;
            }
        }
        catch
        {
            // clang not available or doesn't support --print-libgcc-file-name
        }

        return null;
    }

    /// <summary>
    /// Detects the underlying linker tool name from clang's stderr output.
    /// </summary>
    private static string DetectLinkerFromStderr(string stderr)
    {
        if (stderr.Contains(value: "lld-link:"))
        {
            return "lld-link";
        }

        if (stderr.Contains(value: "ld.lld:"))
        {
            return "ld.lld";
        }

        if (stderr.Contains(value: "collect2:"))
        {
            return "collect2";
        }

        if (stderr.Contains(value: "LINK :") || stderr.Contains(value: "LINK:"))
        {
            return "link.exe";
        }

        if (stderr.Contains(value: "ld:"))
        {
            return "ld";
        }

        return ClangToolName;
    }

    /// <summary>Maps a build mode to the LLVM optimization level token (O0/O2/O3/Os).</summary>
    private static string OptLevelString(RfBuildMode buildMode)
    {
        return buildMode switch
        {
            RfBuildMode.Release => "O2",
            RfBuildMode.ReleaseTime => "O3",
            RfBuildMode.ReleaseSpace => "Os",
            _ => "O0"
        };
    }

    /// <summary>
    /// Optimizes <paramref name="llFile"/> into <paramref name="optFile"/> by running LLVM `opt`.
    /// Debug builds run mem2reg+sroa at O0 (readability without semantic change); optimized builds
    /// run the full pipeline at the requested level. Returns 0 on success or 1 if opt fails.
    /// </summary>
    internal static int OptimizeIr(string llFile, string optFile, RfBuildMode buildMode,
        bool internalizeForLto = false)
    {
        string optPipelineLevel = OptLevelString(buildMode: buildMode);

        // Use -passes='default<Ox>,...' syntax (LLVM 14+; replaces the -Ox -passes=... split form).
        // With internalizeForLto, `internalize` runs FIRST (preserving only `main`), so the module's
        // definitions, Ingrid's Tessera library among them (the allocators, the divide primitives), become
        // internal: the inliner inlines them (exposing libc calloc/free and divq) and DCE drops the unused
        // ones. Every other symbol in a whole-program executable is internal by construction, so preserving
        // `main` alone is the standard LTO-of-an-exe behavior (callbacks are passed by address, which
        // internalize keeps alive).
        string basePipeline = buildMode == RfBuildMode.Debug
            ? $"default<{optPipelineLevel}>,mem2reg,sroa"
            : $"default<{optPipelineLevel}>";
        string optPipeline = internalizeForLto
            ? $"internalize,{basePipeline}"
            : basePipeline;
        string internalizeArgs = internalizeForLto
            ? " -internalize-public-api-list=main"
            : "";
        string optArgs =
            $"-S -passes={optPipeline}{internalizeArgs} \"{llFile}\" -o \"{optFile}\"";
        var optPsi = new ProcessStartInfo
        {
            FileName = OptTool.Value,
            Arguments = optArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ConfigureToolchainEnvironment(psi: optPsi, toolPath: OptTool.Value);

        try
        {
            using var optProcess = Process.Start(startInfo: optPsi);
            if (optProcess == null)
            {
                Console.WriteLine(value: "Error: Failed to start opt.");
                return 1;
            }

            string optStderr = optProcess.StandardError.ReadToEnd();
            optProcess.WaitForExit();

            if (optProcess.ExitCode != 0)
            {
                Console.Error.WriteLine(value: optStderr.Trim());
                Console.WriteLine(
                    value: $"Optimization failed (opt exited with code {optProcess.ExitCode})");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to execute opt: {ex.Message}");
            Console.WriteLine(value: "Make sure LLVM 'opt' is installed and on your PATH.");
            return 1;
        }

        return 0;
    }

    // ========================================================================
    // Dev-loop LTO: llvm-link the hot native-runtime bitcode into the RF module
    // ========================================================================

    /// <summary>
    /// Runs an LLVM toolchain tool, capturing stderr. Returns the exit code; -1 if the process could
    /// not be started. Used by the LTO helpers, which treat any failure as "skip LTO, fall back to the
    /// plain opt path" rather than failing the build.
    /// </summary>
    private static int RunToolCapture(string toolPath, string args, out string stderr)
    {
        var psi = new ProcessStartInfo
        {
            FileName = toolPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ConfigureToolchainEnvironment(psi: psi, toolPath: toolPath);
        try
        {
            using var proc = Process.Start(startInfo: psi);
            if (proc == null)
            {
                stderr = "process did not start";
                return -1;
            }

            string capturedStdout = "";
            string capturedStderr = "";
            var outThread = new Thread(start: () => capturedStdout = proc.StandardOutput.ReadToEnd());
            var errThread = new Thread(start: () => capturedStderr = proc.StandardError.ReadToEnd());
            outThread.Start();
            errThread.Start();
            proc.WaitForExit();
            outThread.Join();
            errThread.Join();
            _ = capturedStdout;
            stderr = capturedStderr;
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            stderr = ex.Message;
            return -1;
        }
    }

    /// <summary>
    /// llvm-links the emitted RF module (<paramref name="llFile"/>) with Ingrid's Tessera library
    /// (<see cref="IngridTessera"/>) into <paramref name="linkedFile"/>. Unlike the hot-runtime LTO below this isn't
    /// optional: the library's routines are not in the runtime DLL, so a module that calls one links only with it.
    /// Only what the module reaches is linked (<c>--only-needed</c>): the library holds every export, generated
    /// engines included, and a hello world would otherwise compile all of them in a debug build (about 400 ms of
    /// -O0 code generation). Returns false, with the reason in <paramref name="error"/>, when the library can't be
    /// built or linked.
    /// </summary>
    internal static bool TryLinkIngridTessera(string exeDir, string llFile, out string linkedFile, out string error)
    {
        linkedFile = "";
        string ingrid;
        try
        {
            ingrid = IngridTessera.IrPath(exeDir: exeDir);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }

        // The bitcode when it can be had: it reads several times faster than the text IR.
        string? bitcode = null;
        try
        {
            bitcode = IngridTessera.BitcodePath(exeDir: exeDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that can't be written to keeps the IR.
        }

        string linked = Path.ChangeExtension(path: llFile, extension: ".ingrid.ll");
        int rc = -1;
        string err = "";
        if (bitcode != null)
        {
            rc = RunToolCapture(toolPath: LlvmLinkTool.Value,
                args: $"-S \"{llFile}\" --only-needed \"{bitcode}\" -o \"{linked}\"",
                stderr: out err);
        }

        // Without bitcode, or with bitcode this llvm-link can't read (written by another LLVM release), the IR.
        if (rc != 0)
        {
            rc = RunToolCapture(toolPath: LlvmLinkTool.Value,
                args: $"-S \"{llFile}\" --only-needed \"{ingrid}\" -o \"{linked}\"",
                stderr: out err);
        }

        if (rc != 0)
        {
            error = $"llvm-link could not merge Ingrid's Tessera library into the module: {err.Trim()}";
            return false;
        }

        linkedFile = linked;
        error = "";
        return true;
    }

    /// <summary>Writes <paramref name="irFile"/> as LLVM bitcode to <paramref name="bitcodeFile"/> (llvm-link with one
    /// input and no <c>-S</c>). Returns false when llvm-link fails.</summary>
    internal static bool TryWriteBitcode(string irFile, string bitcodeFile)
    {
        return RunToolCapture(toolPath: LlvmLinkTool.Value,
            args: $"\"{irFile}\" -o \"{bitcodeFile}\"",
            stderr: out _) == 0;
    }

    /// <summary>
    /// The CPU flag for the clang codegen/link step: <see cref="TargetConfig.Cpu"/>, the same CPU every defined routine
    /// names in its attributes. <c>-march</c> on x86 (where <c>-mcpu</c> only tunes), <c>-mcpu</c> elsewhere.
    /// Host-compilation only for now: when explicit cross-compilation targets land, this takes the requested target.
    /// </summary>
    private static string TargetCodegenFlags()
    {
        TargetConfig target = TargetConfig.ForCurrentHost();
        if (target.Cpu == null)
        {
            return "";
        }

        return target.TargetArch == "x86_64"
            ? $" -march={target.Cpu}"
            : $" -mcpu={target.Cpu}";
    }

    /// <summary>
    /// Builds the clang link-argument fragment for user-declared C libraries: a <c>-L"dir"</c> for each
    /// search path followed by a <c>-l name</c> for each library. Empty inputs yield an empty string.
    /// Pure and side-effect free so it can be unit-tested without invoking the toolchain. The link
    /// driver stays the bundled clang/lld regardless of platform — each <c>-l</c> name resolves to the
    /// platform's library form (<c>libX.so</c> / <c>X.lib</c> / <c>libX.dylib</c>).
    /// </summary>
    internal static string BuildUserLibraryArgs(IReadOnlyList<string>? cLibraries,
        IReadOnlyList<string>? libraryPaths)
    {
        var sb = new System.Text.StringBuilder();
        if (libraryPaths != null)
        {
            foreach (string path in libraryPaths.Where(predicate: p =>
                         !string.IsNullOrWhiteSpace(value: p)))
            {
                sb.Append(value: $" -L\"{path}\"");
            }
        }

        if (cLibraries != null)
        {
            foreach (string lib in cLibraries.Where(predicate: l =>
                         !string.IsNullOrWhiteSpace(value: l)))
            {
                sb.Append(value: $" -l{lib.Trim()}");
            }
        }

        return sb.ToString();
    }

    // MSVC-target clang needs the CRT and kernel32 import libraries named explicitly when
    // linking from LLVM IR. The mingw-target clang (bundled self-contained toolchain) links
    // its own CRT and the Win32 import libraries automatically. Either way synchronization is
    // named: Ingrid's Tessera code (the Roamed runtime's world lock) waits with WaitOnAddress /
    // WakeByAddressAll, which no default import library carries.
    private static string WindowsThreadingLibsFragment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "";
        }

        return ClangIsMingw.Value
            ? " -lsynchronization"
            : " -lucrt -lmsvcrt -lkernel32 -lsynchronization";
    }

    // On Linux/macOS the LLVM IR emits direct calls into libm (floor, exp, pow, …) and the
    // pthread/dl runtime. Modern ld defaults to --as-needed, so libm must be named explicitly
    // on the command line or linking fails with "DSO missing from command line". We also embed
    // an rpath pointing at the runtime library directory so the produced executable can locate
    // librazorforge_runtime.so at load time without requiring LD_LIBRARY_PATH.
    private static string UnixRuntimeLibsFragment(string runtimeLibDir)
    {
        return OperatingSystem.IsWindows()
            ? ""
            : $" -lm -lpthread -ldl -Wl,-rpath,\"{runtimeLibDir}\"";
    }

    // Builder-RT builtins resolve softfloat/softint symbols that LLVM emits for types
    // without direct hardware support:
    //   fp128 arithmetic: __addtf3, __subtf3, __multf3, __divtf3, __negtf2, __eqtf2, etc.
    //   b16 conversions:  __extendhfsf2, __truncsfhf2
    //   i128 arithmetic:  __divti3, __modti3, __udivti3, __umodti3
    //
    // On Windows, neither MSVC link.exe nor lld-link automatically searches for the clang
    // compiler-rt builtins library when linking an .ll/.obj that was generated from LLVM IR
    // (rather than from a C/C++ source file). We locate the library explicitly via
    //   clang --print-libgcc-file-name --rtlib=compiler-rt
    // and add it directly to the linker command line. Returns false (with a printed error) only
    // when the Windows compiler-rt library can't be located.
    private static bool TryBuildCompilerRtArg(out string compilerRtArg)
    {
        if (OperatingSystem.IsWindows() && !ClangIsMingw.Value)
        {
            string? compilerRtLib = GetCompilerRtBuiltinsLib();
            if (string.IsNullOrWhiteSpace(value: compilerRtLib))
            {
                Console.WriteLine(
                    value: "Failed to locate clang compiler-rt builtins library on Windows.");
                compilerRtArg = "";
                return false;
            }

            compilerRtArg = $" \"{compilerRtLib}\"";
        }
        else
        {
            compilerRtArg = " --rtlib=compiler-rt";
        }

        return true;
    }

    // Windows always links via lld. On Linux/macOS the system linker is fine for dev
    // setups, but when clang came from a bundled/explicit toolchain the host may have
    // no binutils at all — use that toolchain's own ld.lld (clang searches its own
    // bin directory for it first).
    private static string LldFlagFragment()
    {
        bool clangIsBundled = ClangTool.Value != ClangToolName;
        return OperatingSystem.IsWindows() || clangIsBundled
            ? " -fuse-ld=lld"
            : "";
    }

    // lld-link-only flag (MSVC-target clang). The mingw toolchain's GNU-flavored ld.lld rejects
    // /slash-style options. /errorlimit:0 surfaces every undefined-symbol error instead of
    // capping at ~20.
    private static string LinkerErrorLimitFragment()
    {
        return OperatingSystem.IsWindows() && !ClangIsMingw.Value
            ? " -Wl,/errorlimit:0"
            : "";
    }

    // lld-link-only flag (MSVC-target clang). The embedded asInvoker manifest stops Windows'
    // Application Information Service from heuristically requesting UAC elevation for exe names
    // containing "install"/"update"/"setup"/"patch"/"test_dispatch"/… (it never inspects the
    // binary itself).
    private static string ManifestUacFragment()
    {
        return OperatingSystem.IsWindows() && !ClangIsMingw.Value
            ? " -Wl,\"/MANIFESTUAC:level='asInvoker' uiAccess='false'\" -Wl,/MANIFEST:EMBED"
            : "";
    }

    // The main thread's stack, 64 MiB, so a deep recursion has room and runs as deep on Windows and macOS as it can go.
    // It is only reserved: the pages are taken as the calls reach them. Windows reserves 1 MiB and macOS 8 MiB unless
    // the executable asks. Linux gives the main thread the size of the stack limit (`ulimit -s`, usually 8 MiB), which
    // no link option changes.
    private const long MainStackBytes = 64L * 1024 * 1024;

    private static string MainStackFragment()
    {
        if (OperatingSystem.IsWindows())
        {
            return ClangIsMingw.Value
                ? $" -Wl,--stack,{MainStackBytes}"
                : $" -Wl,/STACK:{MainStackBytes}";
        }

        return OperatingSystem.IsMacOS()
            ? $" -Wl,-stack_size,0x{MainStackBytes:x}"
            : "";
    }

    // The modes that print a crash trace (debug, release) write a PDB next to the Windows executable: DbgHelp names
    // the trace's frames, with file and line, from it and from nothing else (the emitter writes the debug information
    // as CodeView on Windows). lld-link takes /DEBUG, the mingw linker --pdb= (empty: next to the output).
    private static string WindowsDebugInfoFragment(RfBuildMode buildMode)
    {
        if (!OperatingSystem.IsWindows() || buildMode is not (RfBuildMode.Debug or RfBuildMode.Release))
        {
            return "";
        }

        return ClangIsMingw.Value
            ? " -Wl,--pdb="
            : " -Wl,/DEBUG";
    }

    // The macOS system libraries (-lm/-lSystem/...) only exist as SDK stubs; point the driver at
    // the Command Line Tools SDK explicitly (see MacSdkPath).
    private static string MacSysrootFragment()
    {
        return OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(value: MacSdkPath.Value)
            ? $" -isysroot \"{MacSdkPath.Value}\""
            : "";
    }

    /// <summary>
    /// Compiles optimized LLVM IR to a native OBJECT file (<c>clang -c</c>) — NO linking, no runtime libs.
    /// Used to AOT the resident-JIT base: the stdlib closure is compiled to a cached <c>.o</c> ONCE per
    /// stdlib fingerprint, then the dev-loop client loads that object and JITs only the per-run delta,
    /// instead of re-JIT-compiling the whole stdlib IR every run. Returns 0 on success, 1 on failure.
    /// </summary>
    internal static int CompileIrToObject(string optFile, string objFile, RfBuildMode buildMode)
    {
        // clang uses -Ox flag style (not opt's -passes=). Same target codegen flags as the link path so the
        // object's ABI/target matches the delta the JIT layers on top of it.
        // -femulated-tls is LOAD-BEARING for the resident-JIT base: the ORC JIT lowers `thread_local` via
        // EMULATED TLS (`__emutls_v.<name>` + `__emutls_get_address`) — it cannot use native TLS in JIT'd code
        // on Windows — so the AOT'd base object MUST also use emulated TLS, else the base defines native-TLS
        // `_rf_trace_stack` while the delta references emutls `__emutls_v._rf_trace_stack` → "symbol not found"
        // at JIT link (the shared trace globals). Emulated TLS makes both sides agree on the symbol names.
        string clangOptLevel = $"-{OptLevelString(buildMode: buildMode)}";
        string clangArgs =
            $"-c -femulated-tls {clangOptLevel}{TargetCodegenFlags()} -o \"{objFile}\" \"{optFile}\"";
        int rc = RunToolCapture(toolPath: ClangTool.Value, args: clangArgs, stderr: out string err);
        if (rc != 0)
        {
            if (!string.IsNullOrWhiteSpace(value: err))
            {
                Console.Error.Write(value: err);
            }

            Console.WriteLine(value: $"base-object compile failed (clang exited with code {rc})");
        }

        return rc;
    }

    /// <summary>
    /// For IR that names its traced routines (<c>!tessera.trace</c>): writes the IR with each traced call's chain, then
    /// its assembly with the return addresses marked and the table (Tessera's <see cref="Tessera.TraceTable"/>), and
    /// gives the assembly's path, which is linked in place of the IR. Other IR is linked as it is. Null when clang fails.
    /// </summary>
    private static string? WriteTraceTableAssembly(string optFile, string clangOptLevel)
    {
        string ir = File.ReadAllText(path: optFile);
        if (!Tessera.TraceTable.Wanted(ir: ir))
        {
            return optFile;
        }

        TargetConfig target = TargetConfig.ForCurrentHost();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool timing = Environment.GetEnvironmentVariable(variable: "RF_TRACE_TIMING") == "1";
        Tessera.TraceTable.Annotation annotation = Tessera.TraceTable.Annotate(ir: ir);
        if (timing)
        {
            Console.Error.WriteLine(value: $"[trace] annotate {clock.ElapsedMilliseconds} ms");
        }
        string annotated = Path.ChangeExtension(path: optFile, extension: ".trace.ll");
        string asm = Path.ChangeExtension(path: optFile, extension: ".s");
        File.WriteAllText(path: annotated, contents: annotation.Ir);
        string args =
            $"-S -fno-verbose-asm {clangOptLevel} -Xclang -disable-llvm-passes -Wno-override-module{TargetCodegenFlags()} -o \"{asm}\" \"{annotated}\"";
        clock.Restart();
        int rc = RunToolCapture(toolPath: ClangTool.Value, args: args, stderr: out string err);
        if (timing)
        {
            Console.Error.WriteLine(value: $"[trace] assembly {clock.ElapsedMilliseconds} ms");
        }

        if (rc != 0)
        {
            Console.WriteLine(value: $"Writing the assembly failed (clang exited with code {rc})");
            Console.WriteLine(value: err);
            return null;
        }

        clock.Restart();
        File.WriteAllText(path: asm,
            contents: Tessera.TraceTable.Patch(asm: File.ReadAllText(path: asm), arch: target.TargetArch,
                annotation: annotation));
        if (timing)
        {
            Console.Error.WriteLine(value: $"[trace] patch {clock.ElapsedMilliseconds} ms");
        }
        if (Environment.GetEnvironmentVariable(variable: "RF_TRACE_KEEP") != "1")
        {
            File.Delete(path: annotated);
        }

        return asm;
    }

    internal static int LinkExecutable(string optFile, string exeFile, string runtimeLibDir,
        RfBuildMode buildMode, IReadOnlyList<string>? cLibraries = null,
        IReadOnlyList<string>? libraryPaths = null)
    {
        // Compile .ll -> .exe using clang (clang uses -Ox flag style, not opt's -passes= form)
        string clangOptLevel = $"-{OptLevelString(buildMode: buildMode)}";
        // A build that reads its crash trace from the stack: the optimized IR gets its chains, the assembly marks the
        // calls' return addresses, and that assembly is linked (Tessera's TraceTable).
        if (WriteTraceTableAssembly(optFile: optFile, clangOptLevel: clangOptLevel) is not { } linkInput)
        {
            return 1;
        }

        optFile = linkInput;
        // Preserve frame pointers in debug/release for accurate platform stack unwinding.
        // release-time/release-space omit frame pointers for maximum performance.
        string framePointerFlag = buildMode is RfBuildMode.Debug or RfBuildMode.Release
            ? " -fno-omit-frame-pointer"
            : "";
        string windowsThreadingLibs = WindowsThreadingLibsFragment();
        string unixRuntimeLibs = UnixRuntimeLibsFragment(runtimeLibDir: runtimeLibDir);
        if (!TryBuildCompilerRtArg(compilerRtArg: out string compilerRtArg))
        {
            return 1;
        }

        string lldFlag = LldFlagFragment();
        string linkerErrorLimitFlag = LinkerErrorLimitFragment();
        string manifestUacFlag = ManifestUacFragment();
        string macSysrootArg = MacSysrootFragment();
        // User-declared C libraries (config.toml [target] c_libraries / library_paths). Placed
        // after the user object + runtime so `-l` symbol resolution sees the referencing objects first.
        string userLibArgs =
            BuildUserLibraryArgs(cLibraries: cLibraries, libraryPaths: libraryPaths);
        string clangArgs =
            $"{clangOptLevel}{framePointerFlag}{TargetCodegenFlags()}{lldFlag}{macSysrootArg} -o \"{exeFile}\" \"{optFile}\" -L\"{runtimeLibDir}\" -lrazorforge_runtime{userLibArgs}{compilerRtArg}{windowsThreadingLibs}{unixRuntimeLibs}{linkerErrorLimitFlag}{manifestUacFlag}{WindowsDebugInfoFragment(buildMode: buildMode)}{MainStackFragment()}";

        var clangPsi = new ProcessStartInfo
        {
            FileName = ClangTool.Value,
            Arguments = clangArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ConfigureToolchainEnvironment(psi: clangPsi, toolPath: ClangTool.Value);

        try
        {
            using var clangProcess = Process.Start(startInfo: clangPsi);
            if (clangProcess == null)
            {
                Console.WriteLine(value: "Error: Failed to start clang.");
                return 1;
            }

            // Read stdout/stderr concurrently to avoid pipe-buffer deadlock when clang/lld
            // emits a lot of output (e.g. many LNK2019 errors on a ~60k-line IR).
            string clangStdout = "";
            string clangStderr = "";
            var stdoutThread =
                new Thread(start: () => clangStdout = clangProcess.StandardOutput.ReadToEnd());
            var stderrThread =
                new Thread(start: () => clangStderr = clangProcess.StandardError.ReadToEnd());
            stdoutThread.Start();
            stderrThread.Start();
            clangProcess.WaitForExit();
            stdoutThread.Join();
            stderrThread.Join();
            // The trace table's assembly is a step of the build, not a byproduct to keep (it is as large as the IR).
            if (optFile.EndsWith(value: ".s", comparisonType: StringComparison.Ordinal) &&
                Environment.GetEnvironmentVariable(variable: "RF_TRACE_KEEP") != "1")
            {
                File.Delete(path: optFile);
            }

            if (clangProcess.ExitCode != 0)
            {
                // MSVC's link.exe sends detailed errors (LNK2019) to stdout,
                // while the summary (LNK1120) goes to stderr -> print both.
                if (!string.IsNullOrWhiteSpace(value: clangStdout))
                {
                    Console.Error.Write(value: clangStdout);
                }

                if (!string.IsNullOrWhiteSpace(value: clangStderr))
                {
                    Console.Error.Write(value: clangStderr);
                }

                string allOutput = clangStdout + clangStderr;
                string linker = DetectLinkerFromStderr(stderr: allOutput);
                Console.WriteLine(
                    value: $"Linking failed ({linker} exited with code {clangProcess.ExitCode})");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to execute clang: {ex.Message}");
            Console.WriteLine(
                value: "Make sure LLVM/Clang is installed and 'clang' is on your PATH.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Copies the runtime DLL (and its shared-library dependencies) from <paramref name="exeDir"/>
    /// next to the output <paramref name="exeFile"/> so the loader can find them at runtime.
    /// </summary>
    internal static void StageRuntimeDlls(string exeDir, string exeFile)
    {
        string? outputDir = Path.GetDirectoryName(path: Path.GetFullPath(path: exeFile));
        if (outputDir == null)
        {
            return;
        }

        foreach (string dllName in NativeRuntimeDlls)
        {
            string srcDll = Path.Combine(path1: exeDir, path2: dllName);
            if (File.Exists(path: srcDll))
            {
                string dstDll = Path.Combine(path1: outputDir, path2: dllName);
                TryCopyTolerant(src: srcDll, dst: dstDll);
            }
        }
    }

    /// <summary>
    /// Stages each dynamically-linked <c>@link</c>/<c>c_libraries</c> dependency's shared object next to
    /// the output <paramref name="exeFile"/>, searching the <paramref name="libraryPaths"/> (<c>-L</c>
    /// dirs) for the platform filename (<c>NAME.dll</c> / <c>libNAME.so</c> / <c>libNAME.dylib</c>). A
    /// library declared <c>static</c> in a <c>[libraries.NAME]</c> table is baked into the exe and skipped;
    /// a library whose shared object is not found (e.g. a system library on the loader's default path) is
    /// simply not copied. Mirrors <see cref="StageRuntimeDlls"/> for the runtime's own DLL.
    /// </summary>
    internal static void StageUserLibraryDlls(string exeFile, IReadOnlyList<string> cLibraries,
        IReadOnlyList<string>? libraryPaths, IReadOnlyDictionary<string, CLibrary>? libraryConfigs)
    {
        if (libraryPaths is not { Count: > 0 })
        {
            return;
        }

        string? outputDir = Path.GetDirectoryName(path: Path.GetFullPath(path: exeFile));
        if (outputDir == null)
        {
            return;
        }

        HashSet<string> staticNames = CollectStaticLibraryNames(libraryConfigs: libraryConfigs);
        foreach (string lib in
                 cLibraries.Where(predicate: lib => !staticNames.Contains(item: lib)))
        {
            StageDynamicLibrary(lib: lib, libraryPaths: libraryPaths, outputDir: outputDir);
        }
    }

    /// <summary>
    /// Builds the set of library names that are statically linked (baked into the exe at link time)
    /// and therefore have no runtime DLL to stage.
    /// </summary>
    private static HashSet<string> CollectStaticLibraryNames(
        IReadOnlyDictionary<string, CLibrary>? libraryConfigs)
    {
        var staticNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        if (libraryConfigs != null)
        {
            foreach (CLibrary cfg in libraryConfigs.Values)
            {
                if (cfg.Kind == CLinkKind.Static)
                {
                    staticNames.Add(item: cfg.Name);
                }
            }
        }

        return staticNames;
    }

    /// <summary>
    /// Searches <paramref name="libraryPaths"/> for the shared-object file for <paramref name="lib"/>
    /// and copies it to <paramref name="outputDir"/> if found.
    /// </summary>
    private static void StageDynamicLibrary(string lib, IReadOnlyList<string> libraryPaths,
        string outputDir)
    {
        string dllName = SharedObjectFileName(libName: lib);
        foreach (string dir in libraryPaths)
        {
            string src = Path.Combine(path1: dir, path2: dllName);
            if (File.Exists(path: src))
            {
                TryCopyTolerant(src: src, dst: Path.Combine(path1: outputDir, path2: dllName));
                break;
            }
        }
    }

    /// <summary>The platform shared-object filename for an <c>-l</c> link name: <c>NAME.dll</c> on Windows,
    /// <c>libNAME.dylib</c> on macOS, <c>libNAME.so</c> elsewhere.</summary>
    private static string SharedObjectFileName(string libName)
    {
        if (OperatingSystem.IsWindows())
        {
            return $"{libName}.dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"lib{libName}.dylib";
        }

        return $"lib{libName}.so";
    }

    /// <summary>
    /// Deletes the stale intermediate files of a build (<c>.ll</c>, <c>.opt.ll</c>) before it starts, so a
    /// failed build leaves none of the previous build's IR behind. The executable, its debug files and the staged
    /// runtime libraries are left alone: a new link replaces them (<see cref="PromoteLinkStaging"/>), and a failed
    /// build keeps the last good executable runnable.
    /// </summary>
    internal static void CleanBuildAndRunOutputs(string llFile, string optFile, string exeFile)
    {
        // Normalize before taking the directory: for a bare relative name like
        // "smoke.exe" GetDirectoryName returns "" (not null), and enumerating ""
        // throws. Full-path first makes the working-directory case work.
        string outputDir = Path.GetDirectoryName(path: Path.GetFullPath(path: exeFile)) ?? ".";

        // Sweep any leftover *.stale.* files from prior runs where TryRemoveBuildArtifact
        // had to fall back to rename-aside because the original was locked.
        try
        {
            foreach (string stale in Directory.EnumerateFiles(path: outputDir,
                         searchPattern: "*.stale.*"))
            {
                TryRemoveBuildArtifact(path: stale);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or DirectoryNotFoundException)
        {
            // Directory unreadable — non-fatal, just skip the sweep.
        }

        TryRemoveBuildArtifact(path: llFile);
        TryRemoveBuildArtifact(path: optFile);
        // A staging folder a killed build left behind.
        DiscardLinkStaging(stagingDir: LinkStagingDirectory(exeFile: exeFile));
    }

    /// <summary>The folder an executable is linked into before it replaces <paramref name="exeFile"/>: next to
    /// it, so moving the result over is a rename on the same file system.</summary>
    internal static string LinkStagingDirectory(string exeFile)
    {
        string full = Path.GetFullPath(path: exeFile);
        string outputDir = Path.GetDirectoryName(path: full) ?? ".";
        return Path.Combine(path1: outputDir, path2: $".{Path.GetFileName(path: full)}.linking");
    }

    /// <summary>Deletes a link staging folder and what it holds, if it exists.</summary>
    internal static void DiscardLinkStaging(string stagingDir)
    {
        try
        {
            if (Directory.Exists(path: stagingDir))
            {
                Directory.Delete(path: stagingDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file in it is still held open: the next build's sweep removes the folder.
        }
    }

    /// <summary>
    /// Moves everything a successful link wrote into <paramref name="stagingDir"/> (the executable, and on Windows
    /// its <c>.pdb</c>/<c>.ilk</c>/<c>.lib</c>/<c>.exp</c>) next to <paramref name="exeFile"/>, replacing the
    /// previous build's files, then deletes the folder. A previous file that is held open is moved aside first
    /// (<see cref="TryRemoveBuildArtifact"/>).
    /// </summary>
    internal static void PromoteLinkStaging(string stagingDir, string exeFile)
    {
        string outputDir = Path.GetDirectoryName(path: Path.GetFullPath(path: exeFile)) ?? ".";
        foreach (string built in Directory.GetFiles(path: stagingDir))
        {
            string target = Path.Combine(path1: outputDir, path2: Path.GetFileName(path: built));
            try
            {
                File.Move(sourceFileName: built, destFileName: target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryRemoveBuildArtifact(path: target);
                File.Move(sourceFileName: built, destFileName: target, overwrite: true);
            }
        }

        DiscardLinkStaging(stagingDir: stagingDir);
    }

    /// <summary>
    /// Best-effort delete with retry + rename-aside fallback. Windows frequently briefly locks
    /// freshly-written PE files (Defender real-time scan, indexers, lingering child processes).
    /// A short retry covers transient locks; renaming the file out of the way unblocks the next
    /// link step even when the original handle is still open (works as long as the holder opened
    /// with FILE_SHARE_DELETE, which Defender and most scanners do).
    /// </summary>
    private static void TryRemoveBuildArtifact(string path)
    {
        if (!File.Exists(path: path))
        {
            return;
        }

        int[] delaysMs = [0, 50, 100, 200];
        foreach (int delay in delaysMs)
        {
            if (delay > 0)
            {
                Thread.Sleep(millisecondsTimeout: delay);
            }

            try
            {
                File.Delete(path: path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (delay == delaysMs[^1])
                {
                    // Last attempt failed — try renaming out of the way so the next build can write here.
                    string aside = $"{path}.stale.{Guid.NewGuid():N}";
                    try
                    {
                        File.Move(sourceFileName: path, destFileName: aside);
                        return;
                    }
                    catch (Exception renameEx) when (renameEx is IOException
                                                         or UnauthorizedAccessException)
                    {
                        Console.WriteLine(
                            value:
                            $"Warning: Could not remove or rename stale build artifact '{path}': {ex.Message}");
                    }
                }
            }
        }
    }
}
