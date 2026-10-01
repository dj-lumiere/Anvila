using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.TesseraEmit;

/// <summary>
/// The standard library's primitive operations (the <c>LLVM::</c> routines, each an <c>@llvm_ir</c> template) as
/// Tessera operations. Tessera spells signedness on the type, so an ordered comparison is <c>lt</c> on an
/// <c>S</c> or a <c>U</c> operand, and a primitive whose signedness disagrees with its operand's type is refused
/// rather than reinterpreted. A primitive not in the table fails the build, naming it.
/// </summary>
internal static class TesseraIntrinsics
{
    /// <summary>The Tessera expression for a call to the primitive <paramref name="routine"/>.</summary>
    /// <param name="routine">The primitive routine.</param>
    /// <param name="arguments">Its arguments in parameter order: each a value usable as a receiver, with its type.</param>
    /// <param name="resultType">The call's type.</param>
    public static string Translate(RoutineInfo routine, List<(string Value, TypeSymbol? Type)> arguments,
        TypeSymbol? resultType)
    {
        string Binary(string method)
        {
            return arguments is [var a, var b]
                ? $"{a.Value}.{method}({b.Value})"
                : throw Arity(routine: routine, expected: 2, actual: arguments.Count);
        }

        string Ordered(string method, bool signed)
        {
            if (arguments is not [var a, _])
            {
                throw Arity(routine: routine, expected: 2, actual: arguments.Count);
            }

            return IsSigned(type: a.Type) == signed
                ? Binary(method: method)
                : throw new NotSupportedException(
                    message: $"The Tessera backend cannot apply the {(signed ? "signed" : "unsigned")} primitive " +
                             $"{routine.Name} to {a.Type?.FullName}: Tessera takes signedness from the operand type.");
        }

        return routine.Name switch
        {
            "add" => Binary(method: "add_wrap"),
            "sub" => Binary(method: "sub_wrap"),
            "mul" => Binary(method: "mul_wrap"),
            "int_eq" => Binary(method: "eq"),
            "int_ne" => Binary(method: "ne"),
            "signed_lt" => Ordered(method: "lt", signed: true),
            "signed_le" => Ordered(method: "le", signed: true),
            "signed_gt" => Ordered(method: "gt", signed: true),
            "signed_ge" => Ordered(method: "ge", signed: true),
            "unsigned_lt" => Ordered(method: "lt", signed: false),
            "unsigned_le" => Ordered(method: "le", signed: false),
            "unsigned_gt" => Ordered(method: "gt", signed: false),
            "unsigned_ge" => Ordered(method: "ge", signed: false),
            "and" => Binary(method: "bitand"),
            "or" => Binary(method: "bitor"),
            "xor" => Binary(method: "bitxor"),
            _ => throw new NotSupportedException(
                message: $"The Tessera backend has no translation for the primitive LLVM::{routine.Name} yet " +
                         $"(result {resultType?.FullName ?? "none"}).")
        };
    }

    /// <summary>True for a type Tessera treats as signed: the S integers and the choices (signed underlying).</summary>
    private static bool IsSigned(TypeSymbol? type)
    {
        return type switch
        {
            ChoiceTypeSymbol => true,
            RecordTypeSymbol r => r.BareName.StartsWith(value: 'S') && r.BareName.Length > 1 &&
                                  char.IsAsciiDigit(c: r.BareName[1]),
            _ => false
        };
    }

    private static NotSupportedException Arity(RoutineInfo routine, int expected, int actual)
    {
        return new NotSupportedException(
            message: $"The Tessera backend expected {expected} arguments to LLVM::{routine.Name}, found {actual}.");
    }
}
