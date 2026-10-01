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
            { } type => ApplyTypeSubstitutions(type: type)
        };
    }


    /// <summary>
    /// The type a creator builds, stamped by <c>ConstructionLoweringPass</c>.
    /// </summary>
    private TypeSymbol ResolveCreatorType(CreatorExpression creator)
    {
        return creator.ConstructedType is { } constructed and not ErrorTypeSymbol
            ? ApplyTypeSubstitutions(type: constructed)
            : throw new InvalidOperationException(
                message: $"The creator of '{creator.TypeName}' at {creator.Location} reached the LLVM emitter " +
                         $"without a constructed type in [{_currentRoutineDiagName}].");
    }

    /// <summary>
    /// The inner type X of a marker borrow protocol <c>Accessing[X]</c>/<c>Controlling[X]</c>, else null.
    /// A marker is representation-transparent — a member access on it resolves against its inner X.
    /// GenericMonomorphizationPass/GenericAstRewriter collapse markers to their inner during substitution,
    /// so most are gone before codegen; this is the residual safety net for the paths they do not cover
    /// (non-monomorphized bodies). It disappears once every marker-reaching-codegen path is closed upstream.
    /// </summary>
    private static TypeSymbol? MarkerProtocolInner(TypeSymbol? type)
    {
        if (type is ProtocolTypeSymbol { TypeArguments: [{ } inner] } proto &&
            Declaration.RuntimeContract.IsMarkerProtocol(
                baseName: (proto.GenericDefinition ?? proto).BareName))
        {
            return inner;
        }

        return null;
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

        TypeSymbol? lookupType = MarkerProtocolInner(type: targetType) ?? targetType;

        MemberVariableInfo? memberVariable = lookupType switch
        {
            EntityTypeSymbol e => e.LookupMemberVariable(memberVariableName: member.MemberName),
            RecordTypeSymbol r => r.LookupMemberVariable(memberVariableName: member.MemberName),
            _ => null
        };

        TypeSymbol? memberType = memberVariable?.Type;
        if (memberType != null && lookupType is
                { IsGenericResolution: true, TypeArguments: not null })
        {
            memberType = ResolveGenericMemberType(memberType: memberType, ownerType: lookupType);
        }

        return memberType;
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

    /// <summary>
    /// Performs the apply type substitutions step for this builder phase.
    /// </summary>
    internal TypeSymbol ApplyTypeSubstitutions(TypeSymbol type)
    {
        // GenericMonomorphizationPass emits fully-concrete bodies, so codegen holds no live
        // type-substitution map: every generic parameter is already resolved before emission.
        return type;
    }

    /// <summary>
    /// Performs the substitute type params step for this builder phase.
    /// </summary>
    internal TypeSymbol SubstituteTypeParams(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitutions)
    {
        if (substitutions.TryGetValue(key: type.Name, value: out TypeSymbol? sub))
        {
            return sub;
        }

        TypeSymbol? resolvedGenericResolution =
            TrySubstituteGenericResolution(type: type, substitutions: substitutions);
        if (resolvedGenericResolution != null)
        {
            return resolvedGenericResolution;
        }

        TypeSymbol? resolvedGenericDef = TrySubstituteGenericDefinition(
            type: type,
            substitutions: substitutions);
        if (resolvedGenericDef != null)
        {
            return resolvedGenericDef;
        }

        TypeSymbol? resolvedTuple = TrySubstituteTuple(type: type, substitutions: substitutions);
        if (resolvedTuple != null)
        {
            return resolvedTuple;
        }

        return type;
    }

    /// <summary>
    /// Tries to substitute type arguments into a generic-resolution type. Returns the substituted
    /// resolution when any argument changed; returns null if the type is not a generic resolution
    /// or no argument required substitution.
    /// </summary>
    private TypeSymbol? TrySubstituteGenericResolution(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitutions)
    {
        if (type is not { IsGenericResolution: true, TypeArguments: not null })
        {
            return null;
        }

        bool needsResolution = false;
        var resolvedArgs = new List<TypeSymbol>();
        foreach (TypeSymbol ta in type.TypeArguments)
        {
            resolvedArgs.Add(item: SubstituteTypeArgument(ta: ta,
                substitutions: substitutions,
                needsResolution: ref needsResolution));
        }

        if (!needsResolution)
        {
            return null;
        }

        TypeSymbol? genericBase = GetGenericBase(type: type);
        return genericBase != null
            ? _registry.GetOrCreateResolution(genericDef: genericBase, typeArguments: resolvedArgs)
            : null;
    }

    /// <summary>
    /// Tries to resolve a generic definition whose all parameters are covered by the substitution map.
    /// Returns the concrete resolution when all parameters are bound; null otherwise.
    /// </summary>
    private TypeSymbol? TrySubstituteGenericDefinition(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitutions)
    {
        if (type is not { IsGenericDefinition: true, GenericParameters: not null })
        {
            return null;
        }

        var resolvedArgs = new List<TypeSymbol>();
        foreach (string param in type.GenericParameters)
        {
            if (substitutions.TryGetValue(key: param, value: out TypeSymbol? paramSub))
            {
                resolvedArgs.Add(item: paramSub);
            }
            else
            {
                return null;
            }
        }

        return resolvedArgs.Count > 0
            ? _registry.GetOrCreateResolution(genericDef: type, typeArguments: resolvedArgs)
            : null;
    }

    /// <summary>
    /// Tries to substitute element types inside a tuple type. Returns the new tuple when any element
    /// changed; null when the type is not a tuple or no element changed.
    /// </summary>
    private TupleTypeSymbol? TrySubstituteTuple(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitutions)
    {
        if (type is not TupleTypeSymbol tuple)
        {
            return null;
        }

        bool anyChanged = false;
        var resolvedElems = new List<TypeSymbol>();
        foreach (TypeSymbol elem in tuple.ElementTypes)
        {
            TypeSymbol resolved = SubstituteTypeParams(type: elem, substitutions: substitutions);
            if (resolved != elem)
            {
                anyChanged = true;
            }

            resolvedElems.Add(item: resolved);
        }

        return anyChanged
            ? new TupleTypeSymbol(elementTypes: resolvedElems.ToList())
            : null;
    }

    /// <summary>
    /// Substitutes one type argument of a generic-resolution type: a direct param match, a recursive
    /// sub-resolution, or an unresolved generic-definition argument whose own params are all bound.
    /// Sets <paramref name="needsResolution"/> when the argument actually changed.
    /// </summary>
    private TypeSymbol SubstituteTypeArgument(TypeSymbol ta,
        Dictionary<string, TypeSymbol> substitutions, ref bool needsResolution)
    {
        if (substitutions.TryGetValue(key: ta.Name, value: out TypeSymbol? argSub))
        {
            needsResolution = true;
            return argSub;
        }

        if (ta is { IsGenericResolution: true, TypeArguments: not null })
        {
            TypeSymbol innerResolved = SubstituteTypeParams(type: ta, substitutions: substitutions);
            if (innerResolved != ta)
            {
                needsResolution = true;
            }

            return innerResolved;
        }

        if (ta is { IsGenericDefinition: true, GenericParameters: not null }
            and not EntityTypeSymbol)
        {
            bool canResolve = true;
            var innerArgs = new List<TypeSymbol>();
            foreach (string param in ta.GenericParameters)
            {
                if (substitutions.TryGetValue(key: param, value: out TypeSymbol? paramSub))
                {
                    innerArgs.Add(item: paramSub);
                }
                else
                {
                    canResolve = false;
                    break;
                }
            }

            if (canResolve)
            {
                needsResolution = true;
                return _registry.GetOrCreateResolution(genericDef: ta, typeArguments: innerArgs);
            }
        }

        return ta;
    }

    /// <summary>
    /// Resolves the type argument from semantic builder state.
    /// </summary>
    private TypeSymbol? ResolveTypeArgument(TypeExpression ta)
    {
        if (ta.ResolvedType is { } resolvedType and not ErrorTypeSymbol)
        {
            return ApplyTypeSubstitutions(type: resolvedType);
        }

        if (TryParseConstGenericLiteral(name: ta.Name,
                value: out long constValue,
                explicitType: out string? explicitType))
        {
            return new ConstGenericValueTypeSymbol(literalText: ta.Name,
                value: constValue,
                explicitTypeName: explicitType);
        }

        TypeSymbol? tupleType = ResolveTupleTypeExpression(typeExpr: ta);
        if (tupleType != null)
        {
            return tupleType;
        }

        TypeSymbol? genericInstance = ResolveGenericInstanceTypeArgument(ta: ta);
        if (genericInstance != null)
        {
            return genericInstance;
        }

        return LookupTypeInCurrentModule(name: ta.Name) ?? _registry.LookupType(name: ta.Name);
    }

    /// <summary>
    /// Resolves a generic-instance type argument (e.g. <c>List[S64]</c>): looks up the base type,
    /// recursively resolves its inner arguments, and instantiates it when the arity matches.
    /// </summary>
    private TypeSymbol? ResolveGenericInstanceTypeArgument(TypeExpression ta)
    {
        if (ta.GenericArguments is not { Count: > 0 } genericArguments)
        {
            return null;
        }

        TypeSymbol? baseType = _registry.LookupType(name: ta.Name);
        if (baseType == null)
        {
            return null;
        }

        var innerArgs = new List<TypeSymbol>();
        foreach (TypeExpression innerTa in genericArguments)
        {
            TypeSymbol? innerResolved = ResolveTypeArgument(ta: innerTa);
            if (innerResolved != null)
            {
                innerArgs.Add(item: innerResolved);
            }
        }

        return innerArgs.Count == (baseType.GenericParameters?.Count ?? 0)
            ? _registry.GetOrCreateResolution(genericDef: baseType, typeArguments: innerArgs)
            : null;
    }

    /// <summary>
    /// Resolves the tuple type expression from semantic builder state.
    /// </summary>
    private TupleTypeSymbol? ResolveTupleTypeExpression(TypeExpression typeExpr)
    {
        if (typeExpr.Name is not "Tuple" and not "ValueTuple")
        {
            return null;
        }

        if (typeExpr.GenericArguments is not { Count: > 0 } elementTypeExprs)
        {
            return null;
        }

        var elementTypes = new List<TypeSymbol>(capacity: elementTypeExprs.Count);
        foreach (TypeExpression elementTypeExpr in elementTypeExprs)
        {
            TypeSymbol? elementType = ResolveTypeArgument(ta: elementTypeExpr);
            if (elementType == null)
            {
                return null;
            }

            elementTypes.Add(item: elementType);
        }

        return _registry.GetOrCreateTupleType(elementTypes: elementTypes);
    }

    /// <summary>
    /// Attempts to parse const generic literal and reports whether it succeeded.
    /// </summary>
    private static bool TryParseConstGenericLiteral(string name, out long value,
        out string? explicitType)
    {
        explicitType = null;

        if (long.TryParse(s: name, result: out value))
        {
            return true;
        }

        (string Suffix, string TypeName)[] integerSuffixes =
        [
            ("u8", "U8"), ("u16", "U16"), ("u32", "U32"), ("u64", "U64"), ("u128", "U128"),
            ("s8", "S8"), ("s16", "S16"), ("s32", "S32"), ("s64", "S64"), ("s128", "S128")
        ];

        foreach ((string suffix, string typeName) in integerSuffixes)
        {
            if (name.EndsWith(value: suffix, comparisonType: StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(s: name[..^suffix.Length], result: out value))
            {
                explicitType = typeName;
                return true;
            }
        }

        value = 0;
        return false;
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
