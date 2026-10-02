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
    private readonly Func<string, TypeSymbol?, string> _spill;
    private readonly Action<string> _emit;

    private TesseraIntrinsics(RoutineInfo routine, List<(string Value, TypeSymbol? Type)> arguments,
        TypeSymbol? resultType, Func<TypeSymbol?, string> typeText, Func<TypeSymbol?, string> zero,
        Func<string, TypeSymbol?, string> spill, Action<string> emit)
    {
        _routine = routine;
        _arguments = arguments;
        _resultType = resultType;
        _typeText = typeText;
        _zero = zero;
        _spill = spill;
        _emit = emit;
    }

    /// <summary>The Tessera expression for a call to the primitive <paramref name="routine"/>.</summary>
    /// <param name="routine">The primitive routine.</param>
    /// <param name="arguments">Its arguments in parameter order, each a value with its type.</param>
    /// <param name="resultType">The call's type.</param>
    /// <param name="typeText">The Tessera spelling of a type.</param>
    /// <param name="zero">An expression for the all-zero value of a type.</param>
    /// <param name="spill">Stores a value of a type in a fresh slot and returns the slot.</param>
    /// <param name="emit">Writes a statement before the expression.</param>
    public static string Translate(RoutineInfo routine, List<(string Value, TypeSymbol? Type)> arguments,
        TypeSymbol? resultType, Func<TypeSymbol?, string> typeText, Func<TypeSymbol?, string> zero,
        Func<string, TypeSymbol?, string> spill, Action<string> emit)
    {
        return new TesseraIntrinsics(routine: routine, arguments: arguments, resultType: resultType,
            typeText: typeText, zero: zero, spill: spill, emit: emit).Translate();
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
            "reinterpret_bits" => ReinterpretBits(),
            "pointer_to_int" => Conversion(operation: "ptrtoint"),
            "int_to_pointer" => Conversion(operation: "inttoptr"),
            "signed_to_float" => Conversion(operation: "sitofp"),
            "unsigned_to_float" => Conversion(operation: "uitofp"),
            "float_to_signed" => Conversion(operation: "fptosi"),
            "float_to_unsigned" => Conversion(operation: "fptoui"),
            "float_extend" => Conversion(operation: "fpext"),
            "float_narrow" => Conversion(operation: "fptrunc"),
            "load" => $"{Argument(index: 0)}.to<@{_typeText(arg: _resultType)}>().load()",
            "load_unaligned" => $"{Argument(index: 0)}.to<@{_typeText(arg: _resultType)}>().load_unaligned()",
            "store" => $"{Argument(index: 0)}.to<@{Type(index: 1)}>().store({Argument(index: 1)})",
            // zero_value is zeroed under an older name: every byte of a T zero.
            "zeroed" or "zero_value" => _zero(arg: _resultType),
            "ptr_same" => $"{Argument(index: 0)}.ptr_eq({Argument(index: 1)})",
            // The crash trace the routines keep (TesseraTrace), read by Core's crash_report.
            "trace_depth" => "RF_TRACE_DEPTH.load()",
            "trace_frames" => _typeText(arg: _resultType) is ['@', .. var pointee]
                ? $"RF_TRACE_STACK.to<@{pointee}>()"
                : "RF_TRACE_STACK",
            "element_pointer" => ElementPointer(),
            "atomic_load" => $"{Argument(index: 0)}.to<@{_typeText(arg: _resultType)}>().atomic_load()",
            "atomic_store" => Atomic(method: "atomic_store"),
            "atomic_add" => Atomic(method: "atomic_fetch_add"),
            "atomic_sub" => Atomic(method: "atomic_fetch_sub"),
            "atomic_and" => Atomic(method: "atomic_fetch_and"),
            "atomic_or" => Atomic(method: "atomic_fetch_or"),
            "atomic_xor" => Atomic(method: "atomic_fetch_xor"),
            "atomic_exchange" => Atomic(method: "atomic_swap"),
            "atomic_compare_and_exchange" => CompareExchange(),
            "load_element_ref" =>
                $"{Argument(index: 0)}.to<@{_typeText(arg: _resultType)}>().stride({Index(index: 1)}).load()",
            "store_element_ref" =>
                $"{Argument(index: 0)}.to<@{Type(index: 2)}>().stride({Index(index: 1)}).store({Argument(index: 2)})",
            "element_at" or "byte_at" =>
                $"{_spill(arg1: Argument(index: 0), arg2: _arguments[index: 0].Type)}.to<@{_typeText(arg: _resultType)}>()" +
                $".stride({Index(index: 1)}).load()",
            "set_byte_at" => SetElement(),
            "entity_from_hijacked" => Argument(index: 0),
            _ => throw new NotSupportedException(
                message: $"The Tessera backend has no translation for the primitive LLVM::{_routine.Name} yet " +
                         $"(result {_resultType?.FullName ?? "none"}).")
        };
    }

    /// <summary>
    /// The address <c>count</c> elements past <c>ptr</c>, the element being the result's pointee: a Tessera
    /// <c>stride</c>, whose count is the pointer-width integer of the same signedness.
    /// </summary>
    private string ElementPointer()
    {
        TypeSymbol element = _resultType?.TypeArguments is [var pointee]
            ? pointee
            : _routine.TypeArguments is [var typeArgument]
                ? typeArgument
                : throw new NotSupportedException(
                    message: "The Tessera backend found an LLVM::element_pointer without an element type.");
        string count = Type(index: 1);
        string size = count.StartsWith(value: 'S') ? "SSize" : "USize";
        return $"{Argument(index: 0)}.to<@{_typeText(arg: element)}>().stride(bitcast<{count}, {size}>({Argument(index: 1)}))";
    }

    /// <summary>
    /// Reinterprets bits as another type, like the LLVM emitter's rewrite of the <c>bitcast</c> template: a pointer
    /// and an integer convert with <c>ptrtoint</c>/<c>inttoptr</c>, two pointers are the same value, and an
    /// aggregate viewed as a pointer is the address of a copy of it (the universal <c>get_address</c> body, which
    /// callers bypass by taking their own storage's address).
    /// </summary>
    private string ReinterpretBits()
    {
        string from = Type(index: 0);
        string to = _typeText(arg: _resultType);
        return (Kind(text: from), Kind(text: to)) switch
        {
            // A typed pointer @T is reached from another pointer by casting it, an untyped Addr takes any pointer.
            ('p', 'p') => to is ['@', .. var pointee] && from != to
                ? $"{Argument(index: 0)}.to<@{pointee}>()"
                : Argument(index: 0),
            ('p', 'i') => $"ptrtoint<{from}, {to}>({Argument(index: 0)})",
            ('i', 'p') => $"inttoptr<{from}, {to}>({Argument(index: 0)})",
            ('a', 'p') => _spill(arg1: Argument(index: 0), arg2: _arguments[index: 0].Type),
            ('a', _) or (_, 'a') => throw new NotSupportedException(
                message: $"The Tessera backend cannot reinterpret {from} as {to}."),
            _ => $"bitcast<{from}, {to}>({Argument(index: 0)})"
        };
    }

    /// <summary>The kind of a Tessera type: <c>i</c>nteger, <c>f</c>loat, <c>p</c>ointer or <c>a</c>ggregate.</summary>
    private static char Kind(string text)
    {
        if (text is "Addr" || text.StartsWith(value: '@'))
        {
            return 'p';
        }

        if (text is "USize" or "SSize" or "Bool" ||
            text.Length > 1 && text[0] is 'S' or 'U' && char.IsAsciiDigit(c: text[1]))
        {
            return 'i';
        }

        return text is "F16" or "BF16" or "F32" or "F64"
            ? 'f'
            : 'a';
    }

    /// <summary>
    /// A sequentially consistent compare-and-exchange as the pair RazorForge returns: the value the address held and
    /// whether it was replaced. Tessera's raw form returns the flag and stores the held value in a slot.
    /// </summary>
    private string CompareExchange()
    {
        string type = Type(index: 1);
        string previous = _spill(arg1: Argument(index: 1), arg2: _arguments[index: 1].Type);
        _emit(obj: $"{previous}_swapped : Bool = {Argument(index: 0)}.to<@{type}>()" +
                   $".atomic_compare_exchange_raw({Argument(index: 1)}, {Argument(index: 2)}, {previous})");
        _emit(obj: $"{previous}_held : {type} = {previous}.load()");
        return $"{{ {previous}_held, {previous}_swapped }}";
    }

    /// <summary>A sequentially consistent atomic operation on the value at the address, of the value's type.</summary>
    private string Atomic(string method)
    {
        return $"{Argument(index: 0)}.to<@{Type(index: 1)}>().{method}({Argument(index: 1)})";
    }

    /// <summary>An index argument as the pointer-width integer <c>stride</c> takes, bit for bit.</summary>
    private string Index(int index)
    {
        string type = Type(index: index);
        string size = type.StartsWith(value: 'S') ? "SSize" : "USize";
        return $"bitcast<{type}, {size}>({Argument(index: index)})";
    }

    /// <summary>The array with one byte replaced: the array goes to a slot, the byte is stored, and the
    /// result is the slot's new contents.</summary>
    private string SetElement()
    {
        string slot = _spill(arg1: Argument(index: 0), arg2: _arguments[index: 0].Type);
        _emit(obj: $"{slot}.to<@{Type(index: 2)}>().stride({Index(index: 1)}).store({Argument(index: 2)})");
        return $"{slot}.load()";
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

    /// <summary>
    /// The wrapped result and whether it overflowed, as the (value, flag) tuple the primitive returns. A builder
    /// tuple is a record with fields <c>item0</c>, <c>item1</c>, ….
    /// </summary>
    private string Checked(string operation, string overflows)
    {
        string type = Type(index: 0);
        return $"{_typeText(arg: _resultType)} {{ item0: {operation}<{type}>({Argument(index: 0)}, {Argument(index: 1)}), " +
               $"item1: {overflows}<{type}>({Argument(index: 0)}, {Argument(index: 1)}) }}";
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
