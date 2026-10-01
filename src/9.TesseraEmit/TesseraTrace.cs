using System.Text;
using TypeModel.Symbols;

namespace Builder.TesseraEmit;

/// <summary>
/// The crash trace in Tessera, the same shadow stack the LLVM emitter writes: each thread has 32 frames of
/// (routine name, file, line, column) and a depth. A routine that can crash pushes a frame on entry, moves the
/// frame's position before each call, and pops it at each return; on a crash the runtime asks the printer
/// <c>main</c> registered to print the frames.
/// </summary>
internal static class TesseraTrace
{
    /// <summary>The routine that pushes a frame.</summary>
    public const string Push = "rf_trace_push";

    /// <summary>The routine that pops the top frame.</summary>
    public const string Pop = "rf_trace_pop";

    /// <summary>The routine that moves the top frame to another source position.</summary>
    public const string UpdateLocation = "rf_trace_update_loc";

    /// <summary>The routine the runtime calls to print the frames.</summary>
    public const string Printer = "rf_print_trace_stack";

    /// <summary>The frame stack, the depth, and the routines over them.</summary>
    public const string Support = """
        /// One frame of the crash trace, laid out as the runtime's printer reads it.
        record RfTraceFrame
            routine : Addr
            file    : Addr
            line    : S32
            column  : S32

        #threadlocal
        global RF_TRACE_STACK: @Array<RfTraceFrame, 32>

        #threadlocal
        global RF_TRACE_DEPTH: @S32

        #[external("c"), symbol("rf_print_shadow_stack_data")]
        routine c_rf_print_shadow_stack_data(%stack: Addr, %depth: S32) -> Void

        #[external("c"), symbol("rf_set_stack_printer")]
        routine c_rf_set_stack_printer(%printer: Callable<(), Void>) -> Void

        /// Pushes a frame. The depth wraps at 32, so a deeper stack overwrites its oldest frames.
        routine rf_trace_push(%routine: @Byte, %file: @Byte, %line: S32, %column: S32) -> Void
            block entry():
                %depth : S32         = RF_TRACE_DEPTH.load()
                %index : USize       = zext<S32, USize>(band<S32>(%depth, 31))
                %frame : @RfTraceFrame = RF_TRACE_STACK.cast<RfTraceFrame>().stride(%index)
                %frame.routine.store(%routine)
                %frame.file.store(%file)
                %frame.line.store(%line)
                %frame.column.store(%column)
                add<S32>(%depth, 1).store_into(RF_TRACE_DEPTH)
                return()

        routine rf_trace_pop() -> Void
            block entry():
                sub<S32>(RF_TRACE_DEPTH.load(), 1).store_into(RF_TRACE_DEPTH)
                return()

        /// Moves the top frame to the position of the call about to be made.
        routine rf_trace_update_loc(%line: S32, %column: S32) -> Void
            block entry():
                %depth : S32 = RF_TRACE_DEPTH.load()
                branch %depth.gt(0) ? update(%depth) : return()

            block update(%depth: S32):
                %index : USize = zext<S32, USize>(band<S32>(sub<S32>(%depth, 1), 31))
                %frame : @RfTraceFrame = RF_TRACE_STACK.cast<RfTraceFrame>().stride(%index)
                %frame.line.store(%line)
                %frame.column.store(%column)
                return()

        routine rf_print_trace_stack() -> Void
            block entry():
                c_rf_print_shadow_stack_data(RF_TRACE_STACK, RF_TRACE_DEPTH.load())
                return()


        """;

    /// <summary>The name a frame shows for a routine: <c>name!(Param.Type, ...)</c>, as the LLVM emitter writes it.</summary>
    public static string FrameName(RoutineInfo routine)
    {
        string parameters = string.Join(separator: ", ", values: routine.Parameters.Select(selector: p => p.Type.FullName));
        return $"{routine.BaseName}{(routine.IsFailable ? "!" : "")}({parameters})";
    }

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
