using System.Text;
using TypeModel.Symbols;

namespace Builder.TesseraEmit;

/// <summary>
/// The crash trace in Tessera, the same shadow stack the LLVM emitter writes: each thread has 32 frames of
/// (routine name, file, line, column) and a depth. A routine that can crash pushes a frame on entry, moves the
/// frame's position before each call, and pops it at each return. Core's <c>crash_report</c> reads the frames
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

    /// <summary>The routine that copies the trace into a caught error's object.</summary>
    public const string Snapshot = "rf_trace_snapshot";

    /// <summary>The record that copy is, a field of every caught error's object.</summary>
    public const string SnapshotRecord = "RfTraceSnapshot";

    /// <summary>The frame stack, the depth, and the routines over them.</summary>
    public const string Support = """
        ### One frame of the crash trace, laid out as the runtime's printer reads it.
        record RfTraceFrame
            `routine`: Addr
            file: Addr
            line: S32
            column: S32

        @threadlocal
        global RF_TRACE_STACK: @Array[RfTraceFrame, 32]

        @threadlocal
        global RF_TRACE_DEPTH: @S32

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

        ### A copy of the trace in a caught error's object, laid out as Core's Crashables reads it: the 32 frames,
        ### then the depth.
        record RfTraceSnapshot
            frames: Array[RfTraceFrame, 32]
            depth: S32

        ### Copies the trace into a caught error's object where the error is first thrown, so a report of the error
        ### thrown again shows where it began.
        routine rf_trace_snapshot(into: @RfTraceSnapshot) -> Void
            RF_TRACE_DEPTH.peek().poke_into(into.depth)
            each i in 0 til 32
                from: @RfTraceFrame = RF_TRACE_STACK.to[@RfTraceFrame]().stride(i)
                to: @RfTraceFrame = into.frames.to[@RfTraceFrame]().stride(i)
                to.poke(from.peek())
            return


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
