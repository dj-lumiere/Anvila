using Builder.Declaration;
using SyntaxTree;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Writes a <c>BitArray[N]</c> literal in its stored form, so a backend builds every fixed-array literal the same
/// way: literal bools become the packed bytes (<see cref="BitArrayPacking"/>), and bools computed at run time become
/// a <see cref="BitPackExpression"/> that packs them in the same order. Runs at Phase 9 over every body that reaches
/// a backend.
/// </summary>
internal sealed class BitArrayLiteralLoweringPass(TypeSymbol byteType) : AstRewriter
{
    private const string BitArray = "BitArray";

    /// <summary>Lowers the BitArray literals of one routine body in place.</summary>
    public static void Run(Statement body, TypeRegistry registry)
    {
        // A routine body is a block: its statement list is rewritten in place (the body itself is
        // referenced from several places and cannot be replaced).
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(
                message: $"A routine body must be a block, got {body.GetType().Name}.");
        }

        var pass = new BitArrayLiteralLoweringPass(byteType: registry.LookupType(name: "U8") ??
                                                             throw new InvalidOperationException(
                                                                 message: "The U8 type is not registered."));
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    /// <inheritdoc/>
    protected override Expression VisitListLiteral(ListLiteralExpression e)
    {
        var list = (ListLiteralExpression)base.VisitListLiteral(e: e);
        if (!IsBitArray(type: list.ResolvedType))
        {
            return list;
        }

        return (Expression?)BitArrayPacking.Pack(bits: list.Elements,
                   bitArrayType: list.ResolvedType!,
                   byteType: byteType,
                   location: list.Location) ??
               new BitPackExpression(Bits: list.Elements, Location: list.Location) { ResolvedType = list.ResolvedType };
    }

    /// <summary>True for a <c>BitArray[N]</c>, possibly held in an ownership wrapper.</summary>
    internal static bool IsBitArray(TypeSymbol? type)
    {
        while (WrapperShape.TryGet(type: type, name: out string wrapper, inner: out TypeSymbol inner) &&
               wrapper == RuntimeContract.Owned)
        {
            type = inner;
        }

        return type is RecordTypeSymbol record && (record.GenericDefinition ?? record).BareName == BitArray;
    }
}
