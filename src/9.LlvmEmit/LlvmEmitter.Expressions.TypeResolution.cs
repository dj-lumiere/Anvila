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
    /// The type of an expression, as semantic analysis and the later passes stamped it. A literal without
    /// one is typed by its token, and a named argument or a <c>steal</c> by the value it wraps. Any other
    /// expression without a concrete type is an upstream gap and fails loudly.
    /// </summary>
    private TypeSymbol? GetExpressionType(Expression expr)
    {
        return expr.ResolvedType switch
        {
            null or ErrorTypeSymbol => expr switch
            {
                LiteralExpression literal => GetLiteralType(literal: literal),
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
    /// Resolves the creator type from semantic compiler state.
    /// </summary>
    private TypeSymbol? ResolveCreatorType(CreatorExpression creator)
    {
        if (creator.ConstructedType is not null and not ErrorTypeSymbol)
        {
            return ApplyTypeSubstitutions(type: creator.ConstructedType);
        }

        if (creator.ResolvedType is not null and not ErrorTypeSymbol)
        {
            return ApplyTypeSubstitutions(type: creator.ResolvedType);
        }

        TypeSymbol? tupleType = ResolveTupleTypeExpression(typeExpr: new TypeExpression(
            Name: creator.TypeName,
            GenericArguments: creator.TypeArguments,
            Location: creator.Location));
        if (tupleType != null)
        {
            return tupleType;
        }

        TypeSymbol? type = LookupTypeInCurrentModule(name: creator.TypeName);
        if (type == null)
        {
            return null;
        }

        if (type.IsGenericDefinition && creator.TypeArguments is { Count: > 0 })
        {
            var resolvedArgs = new List<TypeSymbol>(capacity: creator.TypeArguments.Count);
            foreach (TypeExpression ta in creator.TypeArguments)
            {
                TypeSymbol? resolved = ResolveTypeArgument(ta: ta);
                if (resolved == null)
                {
                    return type;
                }

                resolvedArgs.Add(item: resolved);
            }

            if (resolvedArgs.Count == type.GenericParameters?.Count)
            {
                return _registry.GetOrCreateResolution(genericDef: type,
                    typeArguments: resolvedArgs);
            }
        }

        return type;
    }

    /// <summary>
    /// Gets the type of a literal expression from its token type.
    /// </summary>
    // NOTE: this fallback token-type-to-type mapping remains only because stdlib bodies bypass SA, so a
    // bare literal reaches the emitter with no SA-resolved type. It is intended to be retired once every
    // literal carries a resolved type before codegen, but that is not yet the case.
    private TypeSymbol? GetLiteralType(LiteralExpression literal)
    {
        string? typeName = literal.LiteralType switch
        {
            // Bare unsuffixed literals default to S64/B64 in RazorForge (same as SA rule).
            // Stdlib bodies bypass SA so we must handle these token types here.
            TokenType.IntegerLiteral => "S64",
            // Explicit `dn` Decimal literal -> the @llvm("i128") BID Decimal (baked as an i128
            // constant by EmitDecimalFloatLiteral). Bare unsuffixed decimals are UndecidedDecimal.
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
            TokenType.CharacterLiteral => "Character",
            TokenType.ByteLetterLiteral => "Byte",
            _ => null
        };

        return typeName != null
            ? _registry.LookupType(name: typeName)
            : null;
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

        // Refresh stale entity metadata for member variable lookup.
        if (lookupType is EntityTypeSymbol entityType)
        {
            lookupType = RefreshEntityMemberVariables(entity: entityType,
                memberVariableName: member.MemberName);
        }

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
    /// Gets the type bit width needed by this compiler phase.
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
    /// Performs the apply type substitutions step for this compiler phase.
    /// </summary>
    internal TypeSymbol ApplyTypeSubstitutions(TypeSymbol type)
    {
        // GenericMonomorphizationPass emits fully-concrete bodies, so codegen holds no live
        // type-substitution map: every generic parameter is already resolved before emission.
        return type;
    }

    /// <summary>
    /// Performs the substitute type params step for this compiler phase.
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
    /// Resolves the type argument from semantic compiler state.
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
    /// Resolves the tuple type expression from semantic compiler state.
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
