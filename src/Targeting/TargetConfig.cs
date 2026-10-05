using System.Runtime.InteropServices;

namespace Builder.Targeting;

/// <summary>Requested build optimization mode.</summary>
public enum RfBuildMode
{
    /// <summary>Debug build — includes debug info, no optimization.</summary>
    Debug = 0,

    /// <summary>Standard release build — general optimization.</summary>
    Release = 1,

    /// <summary>Release build optimized for execution speed.</summary>
    ReleaseTime = 2,

    /// <summary>Release build optimized for binary size.</summary>
    ReleaseSpace = 3
}

/// <summary>
/// Platform configuration for LLVM code generation.
/// Bundles the target triple, data layout, pointer width, page size, cache line size,
/// and platform names so all target-specific values originate from one place.
/// </summary>
public sealed class TargetConfig
{
    /// <summary>LLVM target triple (e.g., "x86_64-pc-windows-msvc").</summary>
    public string Triple { get; }

    /// <summary>LLVM data layout string.</summary>
    public string DataLayout { get; }

    /// <summary>Pointer bit width (32 or 64).</summary>
    public int PointerBitWidth { get; }

    /// <summary>OS virtual memory page size in bytes.</summary>
    public int PageSize { get; }

    /// <summary>CPU cache line size in bytes.</summary>
    public int CacheLineSize { get; }

    /// <summary>Target OS identifier ("windows", "linux", "macos", …).</summary>
    public string TargetOS { get; }

    /// <summary>Target CPU architecture identifier ("x86_64", "aarch64", …).</summary>
    public string TargetArch { get; }

    /// <summary>
    /// The CPU the build generates code for, or null for the triple's default. On x86-64 the floor is x86-64-v3
    /// (Intel Haswell 2013+, AMD Excavator 2015+ and every Ryzen): FMA, AVX2, BMI1/2, LZCNT, MOVBE and F16C. Two of
    /// those are load-bearing. The correctly rounded B32/B64 math is built on fused multiply-add, and without the
    /// instruction every <c>llvm.fma</c> becomes a call to a software fma (2-3x slower B64 cos/tan/log10/erf/pow).
    /// <c>B16</c> (LLVM <c>half</c>) needs the F16C conversions, without which the backend's soft-promotion path
    /// miscompiles half values crossing a call at -O3. AArch64 has native half and FMA in its base ISA.
    /// </summary>
    public string? Cpu => TargetArch == "x86_64"
        ? "x86-64-v3"
        : null;

    /// <summary>The target as the Tessera builder names it (arch-os-abi), with <see cref="Cpu"/>.</summary>
    public Tessera.BuildTarget TesseraTarget()
    {
        string abi = TargetOS switch
        {
            "windows" => "msvc",
            "macos" => "none",
            _ => "gnu"
        };
        return Tessera.BuildTarget.Parse(triple: $"{TargetArch}-{TargetOS}-{abi}") with { Cpu = Cpu };
    }

    private string? _functionAttributes;

    /// <summary>
    /// The <c>"target-cpu"</c> / <c>"target-features"</c> attributes every defined routine carries, as clang resolves
    /// <see cref="Cpu"/> for the triple. The RazorForge module and Ingrid's Tessera library carry the same set: LLVM
    /// inlines a routine only into one whose features cover its own, so a mismatch keeps even a one-line runtime
    /// helper a call. It also tells opt the CPU, which otherwise optimizes for the triple's baseline.
    /// </summary>
    public string FunctionAttributes => _functionAttributes ??=
        Tessera.CpuModel.For(target: TesseraTarget(), pos: new Tessera.Pos(File: "", Line: 0, Col: 0)).FnAttrs;

    /// <summary>
    /// Creates a TargetConfig with explicit values.
    /// </summary>
    public TargetConfig(string triple, string dataLayout, int pointerBitWidth,
        int pageSize, int cacheLineSize, string targetOS,
        string targetArch)
    {
        Triple = triple;
        DataLayout = dataLayout;
        PointerBitWidth = pointerBitWidth;
        PageSize = pageSize;
        CacheLineSize = cacheLineSize;
        TargetOS = targetOS;
        TargetArch = targetArch;
    }

    /// <summary>
    /// Returns a <see cref="TargetConfig"/> matching the current host platform.
    /// </summary>
    public static TargetConfig ForCurrentHost()
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(osPlatform: OSPlatform.Windows);
        bool isLinux = RuntimeInformation.IsOSPlatform(osPlatform: OSPlatform.Linux);
        bool isMacOS = RuntimeInformation.IsOSPlatform(osPlatform: OSPlatform.OSX);

        string os;
        if (isWindows)
        {
            os = "windows";
        }
        else if (isLinux)
        {
            os = "linux";
        }
        else if (isMacOS)
        {
            os = "macos";
        }
        else
        {
            os = "unknown";
        }

        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 when isWindows => X64WindowsConfig(),
            // Intel Macs use Mach-O mangling (m:o) — without this case they fell through
            // to the Linux triple and produced ELF-flavored IR.
            Architecture.X64 when isMacOS => X64MacOSConfig(),
            Architecture.X64 => X64LinuxConfig(os: os),
            Architecture.Arm64 when isMacOS => Arm64MacOSConfig(),
            Architecture.Arm64 => Arm64LinuxConfig(os: os),
            _ => throw UnsupportedHost(os: os)
        };
    }

    private static TargetConfig X64WindowsConfig()
    {
        return new TargetConfig(triple: "x86_64-pc-windows-msvc",
            dataLayout:
            "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128",
            pointerBitWidth: 64,
            pageSize: 4096,
            cacheLineSize: 64,
            targetOS: "windows",
            targetArch: "x86_64");
    }

    private static TargetConfig X64MacOSConfig()
    {
        return new TargetConfig(triple: "x86_64-apple-darwin",
            dataLayout:
            "e-m:o-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128",
            pointerBitWidth: 64,
            pageSize: 4096,
            cacheLineSize: 64,
            targetOS: "macos",
            targetArch: "x86_64");
    }

    private static TargetConfig X64LinuxConfig(string os)
    {
        return new TargetConfig(triple: "x86_64-unknown-linux-gnu",
            dataLayout:
            "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128",
            pointerBitWidth: 64,
            pageSize: 4096,
            cacheLineSize: 64,
            targetOS: os,
            targetArch: "x86_64");
    }

    private static TargetConfig Arm64MacOSConfig()
    {
        return new TargetConfig(triple: "aarch64-apple-darwin",
            dataLayout: "e-m:o-i64:64-i128:128-n32:64-S128",
            pointerBitWidth: 64,
            pageSize: 16384,
            cacheLineSize: 128,
            targetOS: "macos",
            targetArch: "aarch64");
    }

    private static TargetConfig Arm64LinuxConfig(string os)
    {
        return new TargetConfig(triple: "aarch64-unknown-linux-gnu",
            dataLayout: "e-m:e-i8:8:32-i16:16:32-i64:64-i128:128-n32:64-S128",
            pointerBitWidth: 64,
            pageSize: 4096,
            cacheLineSize: 64,
            targetOS: os,
            targetArch: "aarch64");
    }

    private static PlatformNotSupportedException UnsupportedHost(string os)
    {
        return new PlatformNotSupportedException(
            message:
            $"Unsupported host platform: OS='{os}', Architecture='{RuntimeInformation.OSArchitecture}'. " +
            "RazorForge supports x86_64 (Windows/Linux) and AArch64 (macOS/Linux).");
    }
}
