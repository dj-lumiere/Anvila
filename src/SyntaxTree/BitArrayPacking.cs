using Builder.Tokenizer;
using TypeModel.Types;

namespace SyntaxTree;

/// <summary>
/// The layout of a <c>BitArray[N]</c> written as a literal: its <c>N</c> bools packed eight to a byte, the first of
/// each eight in the lowest bit. The one place that decides it, for literals in bodies and for preset tables.
/// </summary>
public static class BitArrayPacking
{
    /// <summary>
    /// The literal <paramref name="bits"/> as packed bytes, a list literal of <paramref name="byteType"/> values typed
    /// <paramref name="bitArrayType"/>, or null when an element is not a <c>true</c>/<c>false</c> literal.
    /// </summary>
    public static ListLiteralExpression? Pack(IReadOnlyList<Expression> bits, TypeSymbol bitArrayType,
        TypeSymbol byteType, SourceLocation location)
    {
        var bytes = new List<Expression>(capacity: (bits.Count + 7) / 8);
        for (int start = 0; start < bits.Count; start += 8)
        {
            long value = 0;
            for (int bit = 0; bit < 8 && start + bit < bits.Count; bit++)
            {
                switch (bits[index: start + bit])
                {
                    case LiteralExpression { Value: true }:
                        value |= 1L << bit;
                        break;
                    case LiteralExpression { Value: false }:
                        break;
                    default:
                        return null;
                }
            }

            bytes.Add(item: new LiteralExpression(Value: value, LiteralType: TokenType.U8Literal, Location: location)
            {
                ResolvedType = byteType
            });
        }

        return new ListLiteralExpression(Elements: bytes, ElementType: null, Location: location)
        {
            ResolvedType = bitArrayType
        };
    }
}
