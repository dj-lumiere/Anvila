using System.Text;
using TypeModel.Symbols;

namespace Builder.TesseraEmit;

/// <summary>
/// The crash trace in Tessera, the same shadow stack the LLVM emitter writes: each thread has 32 frames of
/// (routine name, file, line, column) and a depth. A routine that can crash pushes a frame on entry, moves the
/// frame's position before each call, and pops it at each return. System's <c>crash_report</c> reads the frames
/// through the <c>LLVM::trace_depth</c> and <c>LLVM::trace_frames</c> primitives and prints them itself.
/// </summary>
internal static class TesseraTrace
{
    /// <summary>The routine that pushes a frame.</summary>
    public const string Push = "rf_trace_push";

    /// <summary>The routine that pops the top frame.</summary>
    public const string Pop = "rf_trace_pop";

    /// <summary>The routine that moves the top frame to another source position.</summary>
    public const string UpdateLocation = "rf_trace_update_loc";

    /// <summary>The routine that notes the trace of a new failure in the thread's ring of recent failures and gives its
    /// number, which a caught error's Crashables keeps.</summary>
    public const string FailureNote = "rf_failure_note";

    /// <summary>The frame stack, the depth, and the routines over them.</summary>
    public const string Support = """
        ### One frame of the crash trace, laid out as the runtime's printer reads it.
        record RfTraceFrame
            `routine`: Addr
            file: Addr
            line: S32
            column: S32

        @threadlocal
        global RF_TRACE_STACK: &Array[RfTraceFrame, 32]

        @threadlocal
        global RF_TRACE_DEPTH: &S32

        ### Pushes a frame. The depth wraps at 32, so a deeper stack overwrites its oldest frames.
        routine rf_trace_push(routine_name: @Byte, file: @Byte, line: S32, column: S32) -> Void
            depth: S32 = RF_TRACE_DEPTH.peek()
            index: U64 = zext[S32, U64](band[S32](depth, 31))
            frame: @RfTraceFrame = RF_TRACE_STACK.to[@RfTraceFrame]().stride(index)
            frame.`routine`.poke(routine_name)
            frame.file.poke(file)
            frame.line.poke(line)
            frame.column.poke(column)
            add[S32](depth, 1).poke_into(RF_TRACE_DEPTH)
            return

        routine rf_trace_pop() -> Void
            sub[S32](RF_TRACE_DEPTH.peek(), 1).poke_into(RF_TRACE_DEPTH)
            return

        ### Moves the top frame to the position of the call about to be made.
        routine rf_trace_update_loc(line: S32, column: S32) -> Void
            depth: S32 = RF_TRACE_DEPTH.peek()
            if depth.le(0)
                return
            index: U64 = zext[S32, U64](band[S32](sub[S32](depth, 1), 31))
            frame: @RfTraceFrame = RF_TRACE_STACK.to[@RfTraceFrame]().stride(index)
            frame.line.poke(line)
            frame.column.poke(column)
            return

        ### The trace's depth, under the name the LLVM emitter exports it by: Ingrid's crash handler reads it.
        @export("_rf_trace_get_depth_shared")
        routine rf_trace_get_depth() -> S32
            depth: S32 = RF_TRACE_DEPTH.peek()
            return depth

        ### The trace's 32 frames, under the name the LLVM emitter exports them by.
        @export("_rf_trace_get_frames_shared")
        routine rf_trace_get_frames() -> @RfTraceFrame
            frames: @RfTraceFrame = RF_TRACE_STACK.to[@RfTraceFrame]()
            return frames

        ### One failure a thread remembers: its number, the depth of the trace when it was thrown, and the trace.
        record RfFailure
            seq: U64
            depth: S32
            frames: Array[RfTraceFrame, 32]

        ### The program's most recent failures, a ring of 64: a failure a Work threw is reported where it is retrieved,
        ### on another thread.
        global RF_FAILURES: &Array[RfFailure, 64]

        ### The number of the last failure, on every thread.
        global RF_FAILURE_COUNT: &U64

        ### Notes the running thread's trace as a new failure's, in the entry its number picks, and gives the failure's
        ### number (never 0), which a caught error keeps so a report of it thrown again shows where it began.
        routine rf_failure_note() -> U64
            previous: U64 = RF_FAILURE_COUNT.fetch_add_unbroken_relaxed(1)
            seq: U64 = add[U64](previous, 1)
            entry: @RfFailure = RF_FAILURES.to[@RfFailure]().stride(band[U64](previous, 63))
            entry.seq.poke_unbroken(0)
            RF_TRACE_DEPTH.peek().poke_into(entry.depth)
            each i in 0 til 32
                from: @RfTraceFrame = RF_TRACE_STACK.to[@RfTraceFrame]().stride(i)
                to: @RfTraceFrame = entry.frames.to[@RfTraceFrame]().stride(i)
                to.poke(from.peek())
            entry.seq.poke_unbroken(seq)
            return seq

        ### The entry of failure `seq`, or a hollow pointer when the thread doesn't remember it.
        routine rf_failure_find(seq: U64) -> @RfFailure
            if ieq[U64](seq, 0)
                return .hollow()
            each i in 0 til 64
                entry: @RfFailure = RF_FAILURES.to[@RfFailure]().stride(i)
                if ieq[U64](entry.seq.peek_unbroken(), seq)
                    return entry
            return .hollow()

        ### The 32 frames of failure `seq`'s trace, under the name the LLVM emitter exports them by, or a hollow pointer.
        @export("_rf_failure_get_frames_shared")
        routine rf_failure_get_frames(seq: U64) -> @RfTraceFrame
            entry: @RfFailure = rf_failure_find(seq)
            if entry.is_hollow()
                return .hollow()
            return entry.frames.to[@RfTraceFrame]()

        ### The depth of failure `seq`'s trace, under the name the LLVM emitter exports it by, or 0.
        @export("_rf_failure_get_depth_shared")
        routine rf_failure_get_depth(seq: U64) -> S32
            entry: @RfFailure = rf_failure_find(seq)
            if entry.is_hollow()
                return 0
            return entry.depth.peek()


        """;

    /// <summary>A Tessera string literal for a C string: printable ASCII as is, every other byte (of the UTF-8
    /// encoding) as a <c>\xXX</c> escape.</summary>
    public static string CString(string text)
    {
        var literal = new StringBuilder(capacity: text.Length + 2);
        literal.Append(value: '"');
        foreach (byte b in Encoding.UTF8.GetBytes(s: text))
        {
            if (b is >= 0x20 and < 0x7F and not (byte)'"' and not (byte)'\\')
            {
                literal.Append(value: (char)b);
            }
            else
            {
                literal.Append(value: $"\\x{b:X2}");
            }
        }

        return literal.Append(value: '"')
                      .ToString();
    }
}
