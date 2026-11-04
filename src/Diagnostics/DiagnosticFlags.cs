namespace Builder.Diagnostics;

/// <summary>
/// Process-wide builder diagnostic / dev-loop flags, populated from <c>config.toml</c> at the start
/// of a build. These REPLACE the former <c>REVARE_*</c> / <c>RF_*</c> environment variables (removed):
/// dev-loop routing lives in <c>[target]</c> (<c>use-daemon</c>, <c>mode = "debug-jit"</c>) while the
/// diagnostics below — incl. <c>timing</c> (merged sa/phase) — live in <c>[debug]</c>.
/// <para>The one-shot CLI sets these once per run; the compile daemon serves requests SERIALLY, so it
/// resets and re-applies them per request from the client's manifest with no concurrent mutation. Machine-
/// level install paths (LLVM home, stdlib root, daemon pipe) stay as environment variables by design —
/// they are shared across projects, not per-project config.</para>
/// </summary>
public static class DiagnosticFlags
{
    /// <summary>Print per-phase <c>[SA]</c>/<c>[phase]</c> timing. Was <c>REVARE_PHASE_TIMING</c>.</summary>
    public static bool PhaseTiming { get; set; }

    /// <summary>Survey unresolved marker-protocol conformances. Was <c>RV_MARKER_SURVEY</c>.</summary>
    /// <summary>Print codegen DCE prune statistics. Was <c>RV_PRUNE_STATS</c>.</summary>
    public static bool PruneStats { get; set; }

    /// <summary>Trace ORC-JIT lowering stages. Was <c>REVARE_JIT_TRACE</c>.</summary>
    public static bool JitTrace { get; set; }

    /// <summary>Keep the emitted LLVM IR as <c>&lt;entry&gt;.ll</c> next to the source (<c>dump-ir</c>).
    /// When set, <c>buildandrun</c> takes the local AOT path (skips the daemon / in-memory ORC-JIT, which
    /// never write a <c>.ll</c>) and the <c>.ll</c> is NOT cleaned up — so the IR is inspectable after a run.</summary>
    public static bool DumpIr { get; set; }

    /// <summary>Keep the Lapila source the Lapila backend writes as <c>&lt;entry&gt;.lpa</c> next to the
    /// source (<c>dump-lapila</c>).</summary>
    public static bool DumpLapila { get; set; }

    /// <summary>Path to dump the routine-reachability set, or null. Was <c>RV_REACHABILITY_DUMP</c>.</summary>
    public static string? ReachabilityDump { get; set; }

    /// <summary>Path to dump the maysuspend analysis, or null. Was <c>RV_MAYSUSPEND_DUMP</c>.</summary>
    public static string? MaySuspendDump { get; set; }

    /// <summary><c>[debug] deadlock-check</c>: whether jobs waiting on each other for access stop the program, or
    /// null for the build mode's default (on in debug). The emitted <c>main</c> passes it to the runtime's
    /// AccessManager. It replaces the <c>RV_DEADLOCK_DETECT</c> environment variable.</summary>
    public static bool? DeadlockCheck { get; set; }

    /// <summary><c>[debug] access-order</c>: whether accesses taken in opposite orders stop the program.</summary>
    public static bool AccessOrder { get; set; }

    /// <summary><c>[debug] trace-stdlib</c>: whether the standard library's routines keep frames on the crash trace
    /// too, for work on the library itself.</summary>
    public static bool TraceStdlib { get; set; }

    /// <summary><c>[debug] sanitize</c>: the sanitizer (<c>"thread"</c>) the program is built and run under, or null.</summary>
    public static string? Sanitize { get; set; }

    /// <summary>Clears every flag. Call at the start of each build (esp. each daemon request) so a prior
    /// request's flags never leak into the next.</summary>
    public static void Reset()
    {
        PhaseTiming = false;
        PruneStats = false;
        JitTrace = false;
        DumpIr = false;
        DumpLapila = false;
        ReachabilityDump = null;
        MaySuspendDump = null;
        DeadlockCheck = null;
        AccessOrder = false;
        TraceStdlib = false;
        Sanitize = null;
    }
}
