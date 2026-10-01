using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Writes each text and bytes literal as the record it builds, and each typed <c>none</c> as the zero value of its
/// type, so a backend has no literal layout of its own:
/// <c>"héllo"</c> becomes <c>Text(data: #constant_data[...], count: 5)</c>, its characters as Unicode code points,
/// and <c>b"abc"</c> becomes <c>Bytes(data: #constant_data[...], count: 3)</c>, one byte per character. The data is
/// a <see cref="ConstantDataExpression"/> the backend lays down once as module data. The controller field is the
/// zero value, null: a literal is never freed and its reference count is never touched. Runs at Phase 9 over every
/// body that reaches a backend, after the passes that write literals of their own (crash messages, use-after-steal
/// guards, <c>var_name</c>).
/// </summary>
internal sealed class TextLiteralLoweringPass(TypeSymbol character, TypeSymbol byteType) : AstRewriter
{
    private const string DataField = "data";
    private const string CountField = "count";

    /// <summary>Lowers the text and bytes literals of one routine body in place.</summary>
    public static void Run(Statement body, TypeRegistry registry)
    {
        // A routine body is a block: its statement list is rewritten in place (the body itself is
        // referenced from several places and cannot be replaced).
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(
                message: $"A routine body must be a block, got {body.GetType().Name}.");
        }

        var pass = new TextLiteralLoweringPass(
            character: registry.LookupType(name: "Character") ??
                       throw new InvalidOperationException(message: "The Character type is not registered."),
            byteType: registry.LookupType(name: "Byte") ??
                      throw new InvalidOperationException(message: "The Byte type is not registered."));
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    /// <inheritdoc/>
    public override Expression VisitExpression(Expression expr)
    {
        // A use-after-steal guard hangs its crash call on the identifier, outside the tree.
        if (expr is IdentifierExpression { StealGuardCrash: { } guard } guarded)
        {
            guarded.StealGuardCrash = VisitExpression(expr: guard);
        }

        return expr switch
        {
            LiteralExpression
            {
                Value: string text,
                LiteralType: TokenType.TextLiteral or TokenType.RawText or TokenType.BytesLiteral
            } literal => Lower(literal: literal, text: text),
            // `none` is the zero value of the optional (or handle) it stands for.
            LiteralExpression { LiteralType: TokenType.NoneValue, ResolvedType: { IsNone: false } type } none =>
                new ZeroValueExpression(Location: none.Location) { ResolvedType = type },
            _ => base.VisitExpression(expr: expr)
        };
    }

    private CreatorExpression Lower(LiteralExpression literal, string text)
    {
        if (literal.ResolvedType is not RecordTypeSymbol record ||
            record.MemberVariables.FindIndex(match: f => f.Name == DataField) is not 0 ||
            record.MemberVariables.FindIndex(match: f => f.Name == CountField) is not 1)
        {
            throw new InvalidOperationException(
                message: $"The literal at {literal.Location} has type {literal.ResolvedType?.FullName ?? "none"}, " +
                         $"not a record starting with '{DataField}' and '{CountField}'.");
        }

        bool bytes = literal.LiteralType == TokenType.BytesLiteral;
        List<long> elements = bytes
            ? text.Select(selector: c => (long)(c & 0xFF))
                  .ToList()
            : text.EnumerateRunes()
                  .Select(selector: rune => (long)rune.Value)
                  .ToList();
        MemberVariableInfo data = record.MemberVariables[index: 0];
        MemberVariableInfo count = record.MemberVariables[index: 1];
        return ConstructionLoweringPass.WithEveryField(creator: new CreatorExpression(TypeName: record.Name,
            TypeArguments: null,
            MemberVariables:
            [
                (DataField, new ConstantDataExpression(Elements: elements,
                    ElementType: bytes
                        ? byteType
                        : character,
                    Location: literal.Location)
                {
                    ResolvedType = data.Type
                }),
                (CountField, new LiteralExpression(Value: (ulong)elements.Count,
                    LiteralType: TokenType.U64Literal,
                    Location: literal.Location) { ResolvedType = count.Type })
            ],
            Location: literal.Location) { ResolvedType = record, ConstructedType = record });
    }
}
