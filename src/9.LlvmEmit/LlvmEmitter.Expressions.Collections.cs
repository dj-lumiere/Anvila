using System.Text;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.LlvmEmit;

/// <summary>
/// Expression code generation for collection literals.
/// Array[T,N] and BitArray[N] are emitted as inline insertvalue sequences here.
/// All other collection literals from [] / {} / {} syntax are lowered by
/// ExpressionLoweringPass to their create and add calls before reaching the emitter.
/// </summary>
public partial class LlvmEmitter
{
    private static TypeSymbol UnwrapCollectionStorageType(TypeSymbol type)
    {
        TypeSymbol current = type;
        while (WrapperShape.TryGet(type: current, name: out string wrapper, inner: out TypeSymbol wrapped) &&
               wrapper == Declaration.RuntimeContract.Owned)
        {
            current = wrapped;
        }

        return current;
    }

    /// <summary>
    /// Returns true if the type is Array[T,N] or BitArray[N] — the only list-literal types
    /// that remain in codegen as inline IR (insertvalue). All other collection types must be
    /// lowered to CreatorExpression + add calls by ExpressionLoweringPass.
    /// </summary>
    private static bool IsArrayOrBitArrayLiteral(TypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        TypeSymbol concrete = UnwrapCollectionStorageType(type: type);
        string baseName = GetGenericBaseName(type: concrete) ?? concrete.Name;
        return baseName is "Array" or "BitArray";
    }

    /// <summary>
    /// Emits an Array[T,N] or BitArray[N] literal using inline insertvalue IR.
    /// Guarded at the call site — only reached when IsArrayOrBitArrayLiteral is true.
    /// </summary>
    private string EmitListLiteral(StringBuilder sb, ListLiteralExpression list)
    {
        TypeSymbol concreteListType = UnwrapCollectionStorageType(type: list.ResolvedType!);
        return EmitCollectionLiteralConstructor(sb: sb,
            resolvedType: concreteListType,
            arguments: list.Elements);
    }

    /// <summary>
    /// Emits an Array[T,N] (insertvalue) or BitArray[N] (bit packing) literal. Every other collection
    /// literal is lowered to its create and add calls (ExpressionLoweringPass).
    /// </summary>
    private string EmitCollectionLiteralConstructor(StringBuilder sb, TypeSymbol resolvedType,
        List<Expression> arguments)
    {
        string typeName = resolvedType.Name;
        string baseName = GetGenericBaseName(type: resolvedType) ?? typeName;

        switch (baseName)
        {
            // ACCEPTED codegen boundary case (not debt): Array[T,N] / BitArray[N] are fixed-size
            // backend-native aggregates with no heap allocation and no stdlib create() body — they are
            // pure `insertvalue` (element-per-slot for Array, LSB-packed bytes for BitArray). Building
            // the constant aggregate is intrinsically a codegen concern, so these stay inline. The
            // BitArray literal bit-packing shares PackBitArrayLiteralBytes with the preset path (D6).

            // Array[T, N]: inline array construction via insertvalue
            case "Array":
                return EmitArrayLiteralInline(sb: sb,
                    resolvedType: resolvedType,
                    arguments: arguments);
            // BitArray[N]: inline bit-packed array construction. All-literal elements pack at
            // build time via the shared PackBitArrayLiteralBytes; a non-literal element falls
            // back to the runtime bit-pack.
            case "BitArray":
                return EmitBitArrayLiteralInline(sb: sb,
                    resolvedType: resolvedType,
                    arguments: arguments);
        }

        throw new InvalidOperationException(
            message: $"A '{typeName}' literal reached the LLVM emitter; only Array and BitArray literals do.");
    }

    /// <summary>
    /// Array[T, N] literal: inline array construction via a per-element insertvalue chain.
    /// </summary>
    private string EmitArrayLiteralInline(StringBuilder sb, TypeSymbol resolvedType,
        List<Expression> arguments)
    {
        string llvmType = GetLlvmType(type: resolvedType);
        string current = "zeroinitializer";
        for (int i = 0; i < arguments.Count; i++)
        {
            string elemVal = EmitExpression(sb: sb, expr: arguments[index: i]);
            TypeSymbol? elemType = GetExpressionType(expr: arguments[index: i]);
            string elemLlvm = elemType != null
                ? GetLlvmType(type: elemType)
                : "i64";
            string next = NextTemp();
            EmitLine(sb: sb,
                line: $"  {next} = insertvalue {llvmType} {current}, {elemLlvm} {elemVal}, {i}");
            current = next;
        }

        return current;
    }

    /// <summary>
    /// BitArray[N] literal: inline bit-packed construction. All-literal elements pack at build time
    /// via the shared PackBitArrayLiteralBytes; a non-literal element falls back to the runtime bit-pack.
    /// </summary>
    private string EmitBitArrayLiteralInline(StringBuilder sb, TypeSymbol resolvedType,
        List<Expression> arguments)
    {
        int[] bytes =
            PackBitArrayLiteralBytes(elements: arguments, allLiteral: out bool allLiteral);
        if (!allLiteral)
        {
            return EmitBitArrayRuntime(sb: sb, resolvedType: resolvedType, arguments: arguments);
        }

        string llvmType = GetLlvmType(type: resolvedType);
        string current = "zeroinitializer";
        for (int byteIdx = 0; byteIdx < bytes.Length; byteIdx++)
        {
            string next = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {next} = insertvalue {llvmType} {current}, i8 {bytes[byteIdx]}, {byteIdx}");
            current = next;
        }

        return current;
    }

    /// <summary>
    /// Runtime fallback for BitArray construction when arguments are non-literal booleans.
    /// </summary>
    private string EmitBitArrayRuntime(StringBuilder sb, TypeSymbol resolvedType,
        List<Expression> arguments)
    {
        string llvmType = GetLlvmType(type: resolvedType);
        int bitCount = arguments.Count;
        int byteCount = (bitCount + 7) / 8;

        string current = "zeroinitializer";
        for (int byteIdx = 0; byteIdx < byteCount; byteIdx++)
        {
            string byteAccum = "0";
            for (int bitIdx = 0; bitIdx < 8 && byteIdx * 8 + bitIdx < bitCount; bitIdx++)
            {
                string boolVal =
                    EmitExpression(sb: sb, expr: arguments[index: byteIdx * 8 + bitIdx]);
                string extended = NextTemp();
                EmitLine(sb: sb, line: $"  {extended} = zext i1 {boolVal} to i8");
                if (bitIdx > 0)
                {
                    string shifted = NextTemp();
                    EmitLine(sb: sb, line: $"  {shifted} = shl i8 {extended}, {bitIdx}");
                    extended = shifted;
                }

                string ored = NextTemp();
                EmitLine(sb: sb, line: $"  {ored} = or i8 {byteAccum}, {extended}");
                byteAccum = ored;
            }

            string next = NextTemp();
            EmitLine(sb: sb,
                line: $"  {next} = insertvalue {llvmType} {current}, i8 {byteAccum}, {byteIdx}");
            current = next;
        }

        return current;
    }
}
