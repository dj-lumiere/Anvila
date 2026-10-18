using Builder.Declaration;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Makes the optional a <c>Maybe[T]</c> place takes explicit: <c>return v</c> with a bare <c>T</c> in a routine that
/// returns <c>Maybe[T]</c>, and <c>x = v</c> into a <c>T?</c> variable or member variable, become
/// <c>Maybe[T](present: true, value: v)</c>, so a backend stores the value it is given. A value that is already an
/// optional, or is the empty optional (<c>none</c>), stays as it is. Runs at Phase 9 over every body that reaches a
/// backend, with the routine whose body it is.
/// </summary>
internal sealed class MaybeReturnLoweringPass(RecordTypeSymbol? maybe, TypeSymbol boolType) : AstRewriter
{
    /// <summary>Wraps the bare values <paramref name="body"/> returns when <paramref name="routine"/> returns an
    /// optional, and the bare values it assigns to optional places.</summary>
    public static void Run(Statement body, RoutineInfo? routine, TypeRegistry registry)
    {
        if (body is not BlockStatement block)
        {
            return;
        }

        TypeSymbol boolType = registry.LookupType(name: "Bool") ??
                              throw new InvalidOperationException(message: "The Bool type is not registered.");
        var pass = new MaybeReturnLoweringPass(
            maybe: routine?.ReturnType as RecordTypeSymbol is { CarrierKind: CarrierKind.Maybe } returned ? returned : null,
            boolType: boolType);
        for (int i = 0; i < block.Statements.Count; i++)
        {
            block.Statements[index: i] = pass.VisitStatement(stmt: block.Statements[index: i]);
        }
    }

    /// <inheritdoc/>
    protected override Statement VisitAssignment(AssignmentStatement s)
    {
        Statement visited = base.VisitAssignment(s: s);
        if (visited is not AssignmentStatement assign)
        {
            return visited;
        }

        return PlaceType(place: assign.Target) is RecordTypeSymbol { CarrierKind: CarrierKind.Maybe } into &&
               Wrapped(value: assign.Value, into: into) is { } wrapped
            ? assign with { Value = wrapped }
            : visited;
    }

    /// <inheritdoc/>
    protected override Statement VisitExpressionStatement(ExpressionStatement s)
    {
        Statement visited = base.VisitExpressionStatement(s: s);
        return visited is ExpressionStatement { Expression: BinaryExpression { Operator: BinaryOperator.Assign } assign } statement &&
               PlaceType(place: assign.Left) is RecordTypeSymbol { CarrierKind: CarrierKind.Maybe } into &&
               Wrapped(value: assign.Right, into: into) is { } wrapped
            ? statement with { Expression = assign with { Right = wrapped } }
            : visited;
    }

    /// <summary>The type a place holds: a variable's own type, not the narrower one the analysis knows at this point
    /// (<c>a</c> after <c>a = none</c>).</summary>
    private static TypeSymbol? PlaceType(Expression place)
    {
        return place is IdentifierExpression { ResolvedVariable.Type: { } declared } ? declared : place.ResolvedType;
    }

    /// <summary><paramref name="value"/> as a present optional of <paramref name="into"/>, or null when it is one
    /// already (or <c>none</c>).</summary>
    private CreatorExpression? Wrapped(Expression value, RecordTypeSymbol into)
    {
        if (value.ResolvedType is not { } type || value is LiteralExpression { LiteralType: TokenType.NoneValue } ||
            type is RecordTypeSymbol { CarrierKind: CarrierKind.Maybe } or ErrorTypeSymbol || type.IsNone)
        {
            return null;
        }

        var present = new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: value.Location)
        {
            ResolvedType = boolType
        };
        return new CreatorExpression(TypeName: into.Name,
            TypeArguments: null,
            MemberVariables:
            [
                (RuntimeContract.Carrier.PresentField, present),
                (RuntimeContract.Carrier.ValueField, value)
            ],
            Location: value.Location) { ResolvedType = into, ConstructedType = into };
    }

    /// <inheritdoc/>
    protected override Statement VisitReturn(ReturnStatement s)
    {
        return maybe != null && s.Value is { } value && Wrapped(value: value, into: maybe) is { } wrapped
            ? s with { Value = wrapped }
            : s;
    }
}
