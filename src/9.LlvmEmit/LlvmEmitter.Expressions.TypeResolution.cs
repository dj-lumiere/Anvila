using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.LlvmEmit;

/// <summary>
/// Expression code generation helpers for result type resolution and conditional lowering.
/// </summary>
public partial class LlvmEmitter
{
    /// <summary>
    /// The type of an expression, as semantic analysis and the later passes stamped it (every literal is typed by
    /// <c>LiteralTypeStampPass</c>). A named argument or a <c>steal</c> has the type of the value it wraps. Any other
    /// expression without a concrete type is an upstream gap and fails loudly.
    /// </summary>
    private TypeSymbol? GetExpressionType(Expression expr)
    {
        return expr.ResolvedType switch
        {
            null or ErrorTypeSymbol => expr switch
            {
                NamedArgumentExpression named => GetExpressionType(expr: named.Value),
                StealExpression steal => GetExpressionType(expr: steal.Operand),
                _ => throw new InvalidOperationException(
                    message: $"{expr.GetType().Name}{(expr is IdentifierExpression id ? $" '{id.Name}'" : "")} at " +
                             $"{expr.Location} reached the LLVM emitter without a type in [{_currentRoutineDiagName}].")
            },
            GenericParameterTypeSymbol parameter => throw new InvalidOperationException(
                message: $"{expr.GetType().Name} at {expr.Location} reached the LLVM emitter typed as the " +
                         $"generic parameter '{parameter.Name}' in [{_currentRoutineDiagName}]."),
            ConstGenericValueTypeSymbol constVal => ResolveConstGenericUnderlyingType(constVal: constVal),
            { } type => type
        };
    }


    /// <summary>
    /// The type a creator builds, stamped by <c>ConstructionLoweringPass</c>.
    /// </summary>
    private TypeSymbol ResolveCreatorType(CreatorExpression creator)
    {
        return creator.ConstructedType is { } constructed and not ErrorTypeSymbol
            ? constructed
            : throw new InvalidOperationException(
                message: $"The creator of '{creator.TypeName}' at {creator.Location} reached the LLVM emitter " +
                         $"without a constructed type in [{_currentRoutineDiagName}].");
    }

    /// <summary>
    /// Gets the type of a member access expression.
    /// </summary>
    private TypeSymbol? GetMemberType(MemberExpression member)
    {
        TypeSymbol? targetType = GetExpressionType(expr: member.Object);
        if (targetType == null)
        {
            return null;
        }

        TypeSymbol lookupType = targetType;

        MemberVariableInfo? memberVariable = lookupType switch
        {
            EntityTypeSymbol e => e.LookupMemberVariable(memberVariableName: member.MemberName),
            RecordTypeSymbol r => r.LookupMemberVariable(memberVariableName: member.MemberName),
            _ => null
        };

        // A resolution's member variables carry its concrete types (CreateInstance substitutes them).
        return memberVariable?.Type;
    }

    /// <summary>
    /// Gets the type bit width needed by this builder phase.
    /// </summary>
    // NOTE: this derives the bit width by matching on the rendered LLVM type STRING, which the conversion
    // emitters currently rely on. It is intended to be retired in favour of carrying the width structurally
    // from the source TypeSymbol, but the conversion paths do not thread that through yet.
    private int GetTypeBitWidth(string llvmType)
    {
        return llvmType switch
        {
            "i1" => 1,
            "i8" => 8,
            "i16" => 16,
            "i32" => 32,
            "i64" => 64,
            "i128" => 128,
            "half" => 16,
            "float" => 32,
            "double" => 64,
            "fp128" => 128,
            "ptr" => _pointerBitWidth,
            _ => throw new InvalidOperationException(
                message: $"Unknown LLVM type for bitwidth: {llvmType}")
        };
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Resolves a <see cref="ConstGenericValueTypeSymbol"/> to its underlying primitive type
    /// for memberRoutine dispatch. E.g., a const generic value "8" with constraint "N is U64"
    /// resolves to the U64 type so that memberRoutine calls like N.represent() work correctly.
    /// </summary>
    private TypeSymbol ResolveConstGenericUnderlyingType(ConstGenericValueTypeSymbol constVal)
    {
        string typeName = constVal.ExplicitTypeName ?? "U64";
        return _registry.LookupType(name: typeName) ?? constVal;
    }

    // Sentinel used in diagnostic messages to represent a null type/routine reference.
    private const string NullTypePlaceholder = "<null>";

}
