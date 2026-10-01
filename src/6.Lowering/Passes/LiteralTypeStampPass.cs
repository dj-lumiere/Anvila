using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Phase 9 (backend annotation): gives every literal still without a type the type its token names. A body the
/// builder wrote, or a stdlib body that skipped analysis, can carry untyped literals: <c>"..."</c> is <c>Text</c>,
/// <c>5_u32</c> is <c>U32</c>, a bare integer is <c>S64</c> (the analysis default), <c>true</c> is <c>Bool</c>.
/// A literal that already has a type keeps it. Runs first, so every later pass and both backends see typed literals.
/// </summary>
internal static class LiteralTypeStampPass
{
    /// <summary>Stamps the untyped literals in <paramref name="body"/>.</summary>
    public static void Run(Statement body, TypeRegistry registry)
    {
        AstWalker.WalkExpressions(root: body,
            visit: expression =>
            {
                if (expression is LiteralExpression { ResolvedType: null or ErrorTypeSymbol } literal &&
                    TypeNameOf(token: literal.LiteralType) is { } name)
                {
                    literal.ResolvedType = registry.LookupType(name: name) ??
                                           throw new InvalidOperationException(
                                               message: $"The literal type '{name}' is not registered.");
                }
            });
    }

    private static string? TypeNameOf(TokenType token)
    {
        return token switch
        {
            TokenType.IntegerLiteral => "S64",
            TokenType.DecimalLiteral => "Decimal",
            TokenType.S8Literal => "S8",
            TokenType.S16Literal => "S16",
            TokenType.S32Literal => "S32",
            TokenType.S64Literal => "S64",
            TokenType.S128Literal => "S128",
            TokenType.S256Literal => "S256",
            TokenType.U8Literal => "U8",
            TokenType.U16Literal => "U16",
            TokenType.U32Literal => "U32",
            TokenType.U64Literal => "U64",
            TokenType.U128Literal => "U128",
            TokenType.U256Literal => "U256",
            TokenType.B16Literal => "B16",
            TokenType.B32Literal => "B32",
            TokenType.B64Literal => "B64",
            TokenType.B128Literal => "B128",
            TokenType.D32Literal => "D32",
            TokenType.D64Literal => "D64",
            TokenType.D128Literal => "D128",
            TokenType.AddressLiteral => "Address",
            TokenType.True or TokenType.False => "Bool",
            TokenType.TextLiteral or TokenType.RawText => "Text",
            TokenType.BytesLiteral => "Bytes",
            TokenType.CharacterLiteral => "Character",
            TokenType.ByteLetterLiteral => "Byte",
            _ => null
        };
    }
}
