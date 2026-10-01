using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Answers, before the backend, the zero-argument member calls whose meaning is about the caller's storage
/// rather than the value:
/// <list type="bullet">
/// <item><c>x.hijack()</c> on a record (or a non-pointer primitive) is the address of <c>x</c>'s storage, an
/// <see cref="AddressOfExpression"/>. The body would only see a copy of <c>x</c>, whose address dies with the
/// call.</item>
/// <item><c>x.get_address()</c> on a struct record is that address as an <c>Address</c>: the same
/// <see cref="AddressOfExpression"/> under a pointer-to-integer <see cref="BackendCastExpression"/>.</item>
/// <item><c>x.var_name()</c> is the text of the receiver's name.</item>
/// </list>
/// A pointer-shaped record (<c>CPtr</c>, <c>Hijacked[T]</c>, an RC wrapper) keeps its own body, which returns
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
    private AddressOfExpression AddressOf(Expression storage, RecordTypeSymbol type, SourceLocation location)
    {
        return new AddressOfExpression(Target: storage, Location: location)
        {
            ResolvedType = registry.GetOrCreateWrapperType(wrapperName: RuntimeContract.Hijacked,
                innerType: type,
                isReadOnly: false)
        };
    }
}
