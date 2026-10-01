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
    /// Emits a fixed-array literal (an <c>Array[T, N]</c>, or a <c>BitArray[N]</c> in its packed bytes) as a
    /// per-element insertvalue chain. Every other collection literal is lowered to its create and add calls
    /// (ExpressionLoweringPass).
    /// </summary>
    private string EmitListLiteral(StringBuilder sb, ListLiteralExpression list)
    {
        TypeSymbol arrayType = UnwrapCollectionStorageType(type: list.ResolvedType!);
        string llvmType = GetLlvmType(type: arrayType);
        if (!llvmType.StartsWith(value: '['))
        {
            throw new InvalidOperationException(
                message: $"A '{arrayType.FullName}' literal reached the LLVM emitter in [{_currentRoutineDiagName}]; " +
                         "only fixed-array literals do.");
        }

        string current = "zeroinitializer";
        for (int i = 0; i < list.Elements.Count; i++)
        {
            Expression element = list.Elements[index: i];
            string value = EmitExpression(sb: sb, expr: element);
            string elementLlvm = GetLlvmType(type: GetExpressionType(expr: element) ??
                                                   throw new InvalidOperationException(
                                                       message: "An array literal element has no type."));
            string next = NextTemp();
            EmitLine(sb: sb, line: $"  {next} = insertvalue {llvmType} {current}, {elementLlvm} {value}, {i}");
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
