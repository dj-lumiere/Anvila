using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Answers, before the backend, the zero-argument member calls whose meaning is about the caller's storage
/// rather than the value:
/// <list type="bullet">
/// <item><c>x.hijack()</c> on a record (or a non-pointer primitive, or a variant) is the address of <c>x</c>'s storage, an
/// <see cref="AddressOfExpression"/>. The body would only see a copy of <c>x</c>, whose address dies with the
/// call.</item>
/// <item><c>x.get_address()</c> on a struct record is that address as an <c>Addr</c>: the same
/// <see cref="AddressOfExpression"/> under a <see cref="BackendCastExpression"/> (pointer to pointer, the same value).</item>
/// <item><c>x.var_name()</c> is the text of the receiver's name.</item>
/// <item><c>p.referent()</c> on a <c>Hijacked[R]</c> of a pointer-shaped record <c>R</c> is <c>p</c> itself read as an
/// <c>R</c>: <c>hijack</c> of such a record is the pointer it holds, not the address of its storage, so the inverse
/// reads the same bits back rather than loading through them.</item>
/// </list>
/// A pointer-shaped record (<c>Addr</c>, <c>Hijacked[T]</c>, an RC wrapper) keeps its own body, which returns
/// the pointer it holds. Runs at Phase 9 over every body that reaches a backend, after monomorphization.
/// </summary>
internal sealed class AddressLoweringPass(TypeRegistry registry) : AstRewriter
{
    /// <summary>Rewrites the storage-meaning member calls of one routine body in place.</summary>
    public static void Run(Statement body, TypeRegistry registry)
    {
        // A routine body is a block: its statement list is rewritten in place (the body itself is
        // referenced from several places and cannot be replaced).
        if (body is not BlockStatement block)
        {
            throw new InvalidOperationException(
                message: $"A routine body must be a block, got {body.GetType().Name}.");
        }

        var pass = new AddressLoweringPass(registry: registry);
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    /// <inheritdoc/>
    protected override Expression VisitCall(CallExpression e)
    {
        Expression visited = base.VisitCall(e: e);
        if (visited is not CallExpression { Callee: MemberExpression member, Arguments.Count: 0 } call)
        {
            return visited;
        }

        TypeSymbol? receiver = member.Object.ResolvedType;
        switch (member.MemberName)
        {
            case "var_name":
                return new LiteralExpression(Value: member.Object is IdentifierExpression named
                        ? named.Name
                        : "<expr>",
                    LiteralType: TokenType.TextLiteral,
                    Location: call.Location) { ResolvedType = call.ResolvedType };

            case RuntimeContract.RawPointer.Hijack
                when receiver is RecordTypeSymbol { BackendType: null or not "ptr" } record:
                return AddressOf(storage: member.Object, type: record, location: call.Location);

            case RuntimeContract.RawPointer.Hijack when receiver is VariantTypeSymbol variant:
                return AddressOf(storage: member.Object, type: variant, location: call.Location);

            case RuntimeContract.RawPointer.Referent
                when WrapperShape.TryGet(type: receiver, name: out string pointerName, inner: out TypeSymbol pointee) &&
                     pointerName == RuntimeContract.Hijacked && pointee is RecordTypeSymbol { BackendType: "ptr" }:
                return new BackendCastExpression(Value: member.Object, Location: call.Location)
                {
                    ResolvedType = pointee
                };

            case "get_address" when receiver is RecordTypeSymbol { BackendType: null } record:
                return new BackendCastExpression(
                    Value: AddressOf(storage: member.Object, type: record, location: call.Location),
                    Location: call.Location)
                {
                    ResolvedType = call.ResolvedType
                };

            default:
                return visited;
        }
    }

    /// <summary>The address of <paramref name="storage"/>, typed <c>Hijacked[T]</c> of its type.</summary>
    private AddressOfExpression AddressOf(Expression storage, TypeSymbol type, SourceLocation location)
    {
        return new AddressOfExpression(Target: storage, Location: location)
        {
            ResolvedType = registry.GetOrCreateWrapperType(wrapperName: RuntimeContract.Hijacked,
                innerType: type,
                isReadOnly: false)
        };
    }
}
