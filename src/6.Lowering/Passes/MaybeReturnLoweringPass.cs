using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Makes the optional a <c>Maybe[T]</c>-returning routine returns explicit: <c>return v</c> with a bare
/// <c>T</c> becomes <c>return Maybe[T](present: true, value: v)</c>, so a backend returns the value it is
/// given. A returned value that is already an optional, or is the empty optional (<c>none</c>), stays as it is.
/// Runs at Phase 9 over every body that reaches a backend, with the routine whose body it is.
/// </summary>
internal sealed class MaybeReturnLoweringPass(RecordTypeSymbol maybe, TypeSymbol boolType) : AstRewriter
{
    /// <summary>Wraps the bare values <paramref name="body"/> returns when <paramref name="routine"/> returns an
    /// optional.</summary>
    public static void Run(Statement body, RoutineInfo? routine, TypeRegistry registry)
    {
        if (routine?.ReturnType is not RecordTypeSymbol { CarrierKind: CarrierKind.Maybe } maybe ||
            body is not BlockStatement block)
        {
            return;
        }

        TypeSymbol boolType = registry.LookupType(name: "Bool") ??
                              throw new InvalidOperationException(message: "The Bool type is not registered.");
        var pass = new MaybeReturnLoweringPass(maybe: maybe, boolType: boolType);
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    /// <inheritdoc/>
    protected override Statement VisitReturn(ReturnStatement s)
    {
        if (s.Value is not { ResolvedType: { } returned } value ||
            value is LiteralExpression { LiteralType: TokenType.NoneValue } ||
            returned is RecordTypeSymbol { CarrierKind: CarrierKind.Maybe } or ErrorTypeSymbol)
        {
            return s;
        }

        var present = new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: value.Location)
        {
            ResolvedType = boolType
        };
        return s with
        {
            Value = new CreatorExpression(TypeName: maybe.Name,
                TypeArguments: null,
                MemberVariables:
                [
                    (RuntimeContract.Carrier.PresentField, present),
                    (RuntimeContract.Carrier.ValueField, value)
                ],
                Location: value.Location) { ResolvedType = maybe, ConstructedType = maybe }
        };
    }
}
