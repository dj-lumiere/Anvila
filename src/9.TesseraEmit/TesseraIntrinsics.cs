using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.TesseraEmit;

/// <summary>
/// The standard library's primitive operations (the <c>LLVM::</c> routines, each an <c>@llvm_ir</c> template) as
/// Tessera operations. Most map onto Tessera's raw generic operations of the same LLVM instruction (<c>add</c>,
/// <c>sdiv</c>, <c>sext</c>, <c>ptrtoint</c>, ...), written with explicit type arguments, so they keep the
/// instruction's exact meaning whatever the operand's Tessera signedness. A primitive not in the table fails the
/// build, naming it.
/// </summary>
internal sealed class TesseraIntrinsics
{
    private readonly RoutineInfo _routine;
    private readonly List<(string Value, TypeSymbol? Type)> _arguments;
    private readonly TypeSymbol? _resultType;
    private readonly Func<TypeSymbol?, string> _typeText;
    private readonly Func<TypeSymbol?, string> _zero;

    private TesseraIntrinsics(RoutineInfo routine, List<(string Value, TypeSymbol? Type)> arguments,
        TypeSymbol? resultType, Func<TypeSymbol?, string> typeText, Func<TypeSymbol?, string> zero)
    {
        _routine = routine;
        _arguments = arguments;
        _resultType = resultType;
        _typeText = typeText;
        _zero = zero;
    }

    /// <summary>The Tessera expression for a call to the primitive <paramref name="routine"/>.</summary>
    /// <param name="routine">The primitive routine.</param>
    /// <param name="arguments">Its arguments in parameter order, each a value with its type.</param>
    /// <param name="resultType">The call's type.</param>
    /// <param name="typeText">The Tessera spelling of a type.</param>
    /// <param name="zero">An expression for the all-zero value of a type.</param>
    public static string Translate(RoutineInfo routine, List<(string Value, TypeSymbol? Type)> arguments,
        TypeSymbol? resultType, Func<TypeSymbol?, string> typeText, Func<TypeSymbol?, string> zero)
    {
        return new TesseraIntrinsics(routine: routine, arguments: arguments, resultType: resultType,
            typeText: typeText, zero: zero).Translate();
    }

    private string Translate()
    {
        return _routine.Name switch
        {
            "add" or "signed_add_exact" or "unsigned_add_exact" => Generic(operation: "add"),
            "sub" or "signed_sub_exact" or "unsigned_sub_exact" => Generic(operation: "sub"),
            "mul" or "signed_mul_exact" or "unsigned_mul_exact" => Generic(operation: "mul"),
            "signed_div" => Generic(operation: "sdiv"),
            "unsigned_div" => Generic(operation: "udiv"),
            "signed_rem" => Generic(operation: "srem"),
            "unsigned_rem" => Generic(operation: "urem"),
            "bit_and" => Generic(operation: "band"),
            "bit_or" => Generic(operation: "bor"),
            "bit_xor" => Generic(operation: "bxor"),
            "shift_left" => Generic(operation: "shl"),
            "shift_right_signed" => Generic(operation: "ashr"),
            "shift_right_unsigned" => Generic(operation: "lshr"),
            "int_eq" => Generic(operation: "ieq"),
            "int_ne" => Generic(operation: "ine"),
            "signed_lt" => Ordered(method: "lt", signed: true),
            "signed_le" => Ordered(method: "le", signed: true),
            "signed_gt" => Ordered(method: "gt", signed: true),
            "signed_ge" => Ordered(method: "ge", signed: true),
            "unsigned_lt" => Ordered(method: "lt", signed: false),
            "unsigned_le" => Ordered(method: "le", signed: false),
            "unsigned_gt" => Ordered(method: "gt", signed: false),
            "unsigned_ge" => Ordered(method: "ge", signed: false),
            "float_add" => Generic(operation: "fadd"),
            "float_sub" => Generic(operation: "fsub"),
            "float_mul" => Generic(operation: "fmul"),
            "float_div" or "float_div_fast" => Generic(operation: "fdiv"),
            "float_rem" => Generic(operation: "frem"),
            "float_neg" => Generic(operation: "fneg"),
            "float_eq" => Generic(operation: "feq"),
            "float_ne" => Generic(operation: "fne"),
            "float_lt" => Generic(operation: "flt"),
            "float_le" => Generic(operation: "fle"),
            "float_gt" => Generic(operation: "fgt"),
            "float_ge" => Generic(operation: "fge"),
            "float_abs" => Generic(operation: "fabs"),
            "sqrt" or "floor" or "ceil" or "round" or "rint" or "copysign" or "fma" or "minnum" or "maxnum" =>
                Generic(operation: _routine.Name),
            "float_truncate" => Generic(operation: "ftrunc"),
            "signed_add_sat" => Generic(operation: "sadd_sat"),
            "unsigned_add_sat" => Generic(operation: "uadd_sat"),
            "signed_sub_sat" => Generic(operation: "ssub_sat"),
            "unsigned_sub_sat" => Generic(operation: "usub_sat"),
            "signed_add_checked" => Checked(operation: "add", overflows: "sadd_overflows"),
            "unsigned_add_checked" => Checked(operation: "add", overflows: "uadd_overflows"),
            "signed_sub_checked" => Checked(operation: "sub", overflows: "ssub_overflows"),
            "unsigned_sub_checked" => Checked(operation: "sub", overflows: "usub_overflows"),
            "signed_mul_checked" => Checked(operation: "mul", overflows: "smul_overflows"),
            "unsigned_mul_checked" => Checked(operation: "mul", overflows: "umul_overflows"),
            "count_ones" => Generic(operation: "popcount"),
            "leading_zeros" => $"clz<{Type(index: 0)}>({Argument(index: 0)}, false)",
            "trailing_zeros" => $"ctz<{Type(index: 0)}>({Argument(index: 0)}, false)",
            "reverse_bits" => Generic(operation: "bitreverse"),
            "byte_swap" => Generic(operation: "bswap"),
            "sign_extend" => Conversion(operation: "sext"),
            "zero_extend" => Conversion(operation: "zext"),
            "int_truncate" => Conversion(operation: "trunc"),
            "reinterpret_bits" => Conversion(operation: "bitcast"),
            "pointer_to_int" => Conversion(operation: "ptrtoint"),
            "int_to_pointer" => Conversion(operation: "inttoptr"),
            "signed_to_float" => Conversion(operation: "sitofp"),
            "unsigned_to_float" => Conversion(operation: "uitofp"),
            "float_to_signed" => Conversion(operation: "fptosi"),
            "float_to_unsigned" => Conversion(operation: "fptoui"),
            "float_extend" => Conversion(operation: "fpext"),
            "float_narrow" => Conversion(operation: "fptrunc"),
            "load" => $"{Argument(index: 0)}.cast<{_typeText(arg: _resultType)}>().load()",
            "load_unaligned" => $"{Argument(index: 0)}.cast<{_typeText(arg: _resultType)}>().load_unaligned()",
            "store" => $"{Argument(index: 0)}.cast<{Type(index: 1)}>().store({Argument(index: 1)})",
            "zeroed" => _zero(arg: _resultType),
            "ptr_same" => $"{Argument(index: 0)}.ptr_eq({Argument(index: 1)})",
            _ => throw new NotSupportedException(
                message: $"The Tessera backend has no translation for the primitive LLVM::{_routine.Name} yet " +
                         $"(result {_resultType?.FullName ?? "none"}).")
        };
    }

    private string Argument(int index)
    {
        return index < _arguments.Count
            ? _arguments[index: index].Value
            : throw new NotSupportedException(
                message: $"The Tessera backend expected an argument {index} to LLVM::{_routine.Name}.");
    }

    private string Type(int index)
    {
        return _typeText(arg: index < _arguments.Count
            ? _arguments[index: index].Type
            : null);
    }

    /// <summary>A raw generic operation over the first operand's type: <c>add&lt;S32&gt;(%a, %b)</c>.</summary>
    private string Generic(string operation)
    {
        return $"{operation}<{Type(index: 0)}>({string.Join(separator: ", ", values: _arguments.Select(selector: a => a.Value))})";
    }

    /// <summary>A conversion between the operand's type and the result type: <c>sext&lt;S32, S64&gt;(%v)</c>.</summary>
    private string Conversion(string operation)
    {
        return $"{operation}<{Type(index: 0)}, {_typeText(arg: _resultType)}>({Argument(index: 0)})";
    }

    /// <summary>The wrapped result and whether it overflowed, as the (value, flag) tuple the primitive returns.</summary>
    private string Checked(string operation, string overflows)
    {
        string type = Type(index: 0);
        return $"({operation}<{type}>({Argument(index: 0)}, {Argument(index: 1)}), " +
               $"{overflows}<{type}>({Argument(index: 0)}, {Argument(index: 1)}))";
    }

    /// <summary>
    /// An ordered comparison. Tessera takes signedness from the type, so an operand whose Tessera type has the
    /// other signedness is reinterpreted to the same-width type of the wanted signedness first.
    /// </summary>
    private string Ordered(string method, bool signed)
    {
        string type = Type(index: 0);
        if (IsSignedText(text: type) == signed)
        {
            return $"{Argument(index: 0)}.{method}({Argument(index: 1)})";
        }

        string other = (signed ? "S" : "U") + type[1..];
        return $"bitcast<{type}, {other}>({Argument(index: 0)}).{method}(bitcast<{type}, {other}>({Argument(index: 1)}))";
    }

    private static bool IsSignedText(string text)
    {
        return text.Length > 1 && text[0] == 'S' && char.IsAsciiDigit(c: text[1]);
    }
}
