using Builder.Diagnostics;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Builder.Verification.Enums;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    private static TypeSymbol UnwrapCollectionLiteralExpectedType(TypeSymbol type)
    {
        TypeSymbol current = type;
        while (true)
        {
            if (WrapperShape.InnerOf(type: current) is { } wrapped)
            {
                current = wrapped;
                continue;
            }

            // Retained[T] / Tracked[T] / Roamed[T] are declared as `record` in stdlib so they
            // surface as RecordTypeSymbol. Their single TypeArgument is the
            // wrapped collection type — unwrap so the literal can resolve its base name
            // (PriorityQueue, SortedSet, etc.) from the expected type even when LHS is
            // `Retained[SortedSet[S64]]` etc. Use base-name extraction since instantiated record
            // types have Name like "Foo", not bare "Retained". Roamed[T] is the Suflae lowering of an
            // annotated collection type (`var l: List[S64]` ⇒ `Roamed[List[S64]]`); it MUST be
            // transparent here too, else a bare literal element loses its S64 context and falls back
            // to the Suflae `Integer` default — producing `List[Integer]` that won't assign to the
            // `Roamed[List[S64]]` slot (RF-S201). Inferring the element type through the wrapper is
            // exactly the builder's job.
            if (current is RecordTypeSymbol { TypeArguments: { Count: 1 } recArgs } recRT &&
                GetTypeBaseName(type: recRT) is Declaration.RuntimeContract.Retained or Declaration.RuntimeContract.Tracked
                    or Declaration.RuntimeContract.Roamed)
            {
                current = recArgs[index: 0];
                continue;
            }

            break;
        }

        return current;
    }

    /// <summary>
    /// Picks the collection literal's own type from an expected (annotation) type. A Suflae entity slot
    /// pins the annotation as <c>Roamed[System.List[T]]</c>, but the literal itself must stay the BARE
    /// collection — it builds a fresh <c>create + add_last</c> sequence and the SF binding lowering roams
    /// the result at the var/field/assignment site (exactly like a <c>List[T]()</c> constructor RHS).
    /// Stamping <c>Roamed</c> onto the literal would make its <c>add</c>/<c>add_last</c> calls target the
    /// Roamed handle, which has no collection memberRoutine → codegen "no resolved member routine" (or, once the temp is
    /// created as <c>Roamed[Set]()</c>, an uninitialized controller → AccessViolation). Other expected
    /// wrappers (Retained/Tracked) and exact collection types pass through unchanged.
    /// </summary>
    private static TypeSymbol LiteralTypeFromExpected(TypeSymbol expectedType,
        TypeSymbol? collectionExpectedType)
    {
        return GetTypeBaseName(type: expectedType) == Declaration.RuntimeContract.Roamed
            ? collectionExpectedType!
            : expectedType;
    }

    private static long? GetConstGenericLong(TypeSymbol? type)
    {
        return type is ConstGenericValueTypeSymbol constVal
            ? constVal.Value
            : null;
    }

    private static string GetTypeBaseName(TypeSymbol type)
    {
        return type.BareName;
    }

    /// <summary>
    /// The <c>Array[T, N]</c> type of an element-list construction <c>Array[N](a, b, ...)</c>: <c>N</c>
    /// from <paramref name="arrayLength"/>, <c>T</c> as written (<c>Array[T, N](a, b, ...)</c>) or else from
    /// the first element (the rest are then checked against it). Null when the length or the element type
    /// does not resolve (already reported).
    /// </summary>
    private TypeSymbol? ResolveElementListArrayType(ListLiteralExpression list, TypeExpression arrayLength)
    {
        TypeSymbol length = ResolveType(typeExpr: arrayLength);
        TypeSymbol elementType = list.ElementType is { } written
            ? ResolveType(typeExpr: written)
            : AnalyzeExpression(expression: list.Elements[index: 0]);
        if (length is ErrorTypeSymbol || elementType is ErrorTypeSymbol ||
            _registry.LookupType(name: CollectionNameArray) is not { } arrayDef)
        {
            return null;
        }

        return _registry.GetOrCreateResolution(genericDef: arrayDef, typeArguments: [elementType, length]);
    }

    private TypeSymbol AnalyzeListLiteralExpression(ListLiteralExpression list,
        TypeSymbol? expectedType = null)
    {
        // Collection literals are entity rvalues — value-in-flight produced by a fresh
        // `create + add_last` sequence. Mark for the auto-bind rule (rvalue T → bound T).
        list.IsInFlight = true;

        // `Array[N](a, b, ...)`: the literal is an `Array[T, N]` whose T comes from the elements.
        if (list.ArrayLength is { } arrayLength &&
            ResolveElementListArrayType(list: list, arrayLength: arrayLength) is { } elementListArray)
        {
            expectedType = elementListArray;
        }
        // Extract expected element type from list-shaped expected types.
        TypeSymbol? collectionExpectedType = expectedType != null
            ? UnwrapCollectionLiteralExpectedType(type: expectedType)
            : null;
        string? expectedBaseName = collectionExpectedType != null
            ? GetTypeBaseName(type: collectionExpectedType)
            : null;
        TypeSymbol? expectedElementType = ResolveListExpectedElementType(
            collectionExpectedType: collectionExpectedType,
            expectedBaseName: expectedBaseName);

        TypeSymbol? elementType = ResolveListElementType(list: list,
            expectedElementType: expectedElementType);

        ValidateListFixedArity(list: list,
            expectedBaseName: expectedBaseName,
            collectionExpectedType: collectionExpectedType);

        TypeSymbol resultType = ResolveListResultType(expectedType: expectedType,
            expectedBaseName: expectedBaseName,
            collectionExpectedType: collectionExpectedType,
            elementType: elementType);

        // Resolve the `from_literal` static builder for the (non-inline) collection type, so the lowering
        // emits `Type.from_literal(a, b, c)` and reachability seeds the monomorphized body.
        ResolveLiteralBuilder(literal: list,
            resultType: resultType,
            builderElementType: elementType,
            elementCount: list.Elements.Count);
        return resultType;
    }

    /// <summary>
    /// Determines the expected element type for a list literal from the unwrapped collection expected type
    /// and its base name. Bit-typed collections (<c>BitList</c>/<c>BitArray</c>) force <c>Bool</c>;
    /// sequence types extract their first type argument.
    /// </summary>
    private TypeSymbol? ResolveListExpectedElementType(TypeSymbol? collectionExpectedType,
        string? expectedBaseName)
    {
        if (collectionExpectedType is { IsGenericResolution: true, TypeArguments.Count: >= 1 } &&
            expectedBaseName is "List" or "RotatableList" or "SortedList" or CollectionNameArray)
        {
            return collectionExpectedType.TypeArguments[index: 0];
        }

        if (expectedBaseName is "BitList" or CollectionNameBitArray)
        {
            return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
        }

        return LiteralConformanceArguments(type: collectionExpectedType, literalProtocol: ListLiteralProtocol)
                   ?.FirstOrDefault() ??
               DefaultCollectionArguments(collectionName: "List", expected: collectionExpectedType)
                  ?.FirstOrDefault();
    }

    private const string ListLiteralProtocol = "ListLiteral";
    private const string SetLiteralProtocol = "SetLiteral";
    private const string DictLiteralProtocol = "DictLiteral";

    /// <summary>
    /// The type arguments of <paramref name="type"/>'s conformance to a literal protocol (<c>ListLiteral[T]</c>,
    /// <c>SetLiteral[T]</c>, <c>DictLiteral[K, V]</c>): the element types a literal of that type is built from.
    /// Null for a protocol, a type that does not obey it, or arguments that are still type parameters.
    /// </summary>
    private static List<TypeSymbol>? LiteralConformanceArguments(TypeSymbol? type, string literalProtocol)
    {
        if (type is null or ProtocolTypeSymbol or GenericParameterTypeSymbol ||
            ImplementedProtocolsOf(type: type)
               .FirstOrDefault(predicate: p => p.BareName == literalProtocol) is not
                { TypeArguments: { Count: > 0 } arguments } ||
            arguments.Any(predicate: a => a is GenericParameterTypeSymbol or ErrorTypeSymbol))
        {
            return null;
        }

        return arguments;
    }

    /// <summary>
    /// The type arguments with which the collection a literal builds by default (<c>List</c>, <c>Set</c>,
    /// <c>Dict</c>) obeys the expected protocol <paramref name="expected"/>: <c>[S64]</c> for a list literal where an
    /// <c>Iterable[S64]</c> is expected, <c>[K, V]</c> for a dict literal where an <c>Iterable[DictEntry[K, V]]</c>
    /// is expected. The literal's elements then take those types, the way a bare number takes the type it goes to.
    /// Null when the expected type is no protocol or the collection does not obey it with every argument fixed.
    /// </summary>
    private List<TypeSymbol>? DefaultCollectionArguments(string collectionName, TypeSymbol? expected)
    {
        if (expected is not ProtocolTypeSymbol { TypeArguments.Count: > 0 } ||
            _registry.LookupType(name: collectionName) is not { GenericParameters: { Count: > 0 } parameters } def)
        {
            return null;
        }

        var pending = new Stack<TypeSymbol>(collection: ImplementedProtocolsOf(type: def));
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            TypeSymbol conformance = pending.Pop();
            if (!seen.Add(item: conformance.Name))
            {
                continue;
            }

            var bindings = new Dictionary<string, TypeSymbol>(comparer: StringComparer.Ordinal);
            if (UnifyLiteralPattern(pattern: conformance, concrete: expected, bindings: bindings) &&
                parameters.All(predicate: bindings.ContainsKey))
            {
                return parameters.Select(selector: p => bindings[key: p])
                                 .ToList();
            }

            if (conformance is ProtocolTypeSymbol proto)
            {
                foreach (ProtocolTypeSymbol parent in proto.ParentProtocols)
                {
                    pending.Push(item: parent);
                }
            }
        }

        return null;
    }

    /// <summary>Matches a type written with type parameters (<c>Iterable[DictEntry[TKey, TValue]]</c>) against a
    /// concrete one, recording what each parameter stands for. False on any mismatch.</summary>
    private static bool UnifyLiteralPattern(TypeSymbol pattern, TypeSymbol concrete,
        Dictionary<string, TypeSymbol> bindings)
    {
        if (pattern is GenericParameterTypeSymbol parameter)
        {
            if (concrete is GenericParameterTypeSymbol or ErrorTypeSymbol)
            {
                return false;
            }

            if (bindings.TryGetValue(key: parameter.Name, value: out TypeSymbol? earlier))
            {
                return earlier.Name == concrete.Name;
            }

            bindings[key: parameter.Name] = concrete;
            return true;
        }

        IReadOnlyList<TypeSymbol> patternArguments = pattern.TypeArguments ?? [];
        IReadOnlyList<TypeSymbol> concreteArguments = concrete.TypeArguments ?? [];
        if (pattern.BareName != concrete.BareName || patternArguments.Count != concreteArguments.Count)
        {
            return false;
        }

        if (patternArguments.Count == 0)
        {
            return pattern.Name == concrete.Name;
        }

        for (int i = 0; i < patternArguments.Count; i++)
        {
            if (!UnifyLiteralPattern(pattern: patternArguments[index: i],
                    concrete: concreteArguments[index: i],
                    bindings: bindings))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Infers or validates the element type for a list literal: explicit annotation, inferred from the
    /// first element (with subsequent elements validated), propagated from context, or an error.
    /// </summary>
    private TypeSymbol? ResolveListElementType(ListLiteralExpression list,
        TypeSymbol? expectedElementType)
    {
        // A written element type is the type, and the elements are still analyzed against it (a bare
        // literal conforms to it, a mismatch is reported).
        if (list.ElementType != null)
        {
            expectedElementType = ResolveType(typeExpr: list.ElementType);
        }

        if (list.Elements.Count > 0)
        {
            // Infer from first element, propagating expected element type.
            TypeSymbol first = AnalyzeExpression(expression: list.Elements[index: 0],
                expectedType: expectedElementType);
            // With the element type written, every element is checked against it, the first one too.
            bool written = list.ElementType != null && expectedElementType is not ErrorTypeSymbol;
            // An element type the context gives (`var keys: List[Key] = [1, "two"]`) is the type when the first
            // element fits it, so the elements after it are held to that type, not to the first one's.
            TypeSymbol elementType = written ||
                                     expectedElementType is not null and not ErrorTypeSymbol &&
                                     first is not ErrorTypeSymbol &&
                                     IsAssignableTo(source: first, target: expectedElementType)
                ? expectedElementType!
                : first;

            // Validate all elements have compatible types.
            // Use inferred element type as context for subsequent elements (e.g., [] in [[1,2], []]).
            TypeSymbol elemExpected = expectedElementType ?? elementType;
            if (written && first is not ErrorTypeSymbol && !IsAssignableTo(source: first, target: elementType))
            {
                ReportError(code: SemanticDiagnosticCode.ListElementTypeMismatch,
                    message: $"List element type mismatch: expected '{elementType.Name}', got '{first.Name}'.",
                    location: list.Elements[index: 0].Location);
            }
            for (int i = 1; i < list.Elements.Count; i++)
            {
                TypeSymbol elemType = AnalyzeExpression(expression: list.Elements[index: i],
                    expectedType: elemExpected);
                if (!IsAssignableTo(source: elemType, target: elementType))
                {
                    ReportError(code: SemanticDiagnosticCode.ListElementTypeMismatch,
                        message:
                        $"List element type mismatch: expected '{elementType.Name}', got '{elemType.Name}'.",
                        location: list.Elements[index: i].Location);
                }
            }

            // An entity element inferred from the elements is a shared handle slot in Suflae.
            return expectedElementType != null
                ? elementType
                : _typeResolver.RoamInferredSlot(inferred: elementType);
        }

        if (expectedElementType != null)
        {
            // Empty list with expected type from context — use it.
            return expectedElementType;
        }

        ReportError(code: SemanticDiagnosticCode.EmptyListNoTypeAnnotation,
            message: "Cannot infer element type from empty list literal without type annotation.",
            location: list.Location);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Validates that a fixed-arity list literal (<c>Array[T,N]</c> or <c>BitArray[N]</c>) has exactly
    /// the expected number of elements, reporting <see cref="SemanticDiagnosticCode.ArgumentCountMismatch"/>
    /// if not.
    /// </summary>
    private void ValidateListFixedArity(ListLiteralExpression list, string? expectedBaseName,
        TypeSymbol? collectionExpectedType)
    {
        if (expectedBaseName == CollectionNameArray && collectionExpectedType?.TypeArguments is
                { Count: >= 2 })
        {
            long? expectedCount =
                GetConstGenericLong(type: collectionExpectedType.TypeArguments[index: 1]);
            if (expectedCount != null && list.Elements.Count != expectedCount.Value)
            {
                ReportError(code: SemanticDiagnosticCode.ArgumentCountMismatch,
                    message:
                    $"Array[{collectionExpectedType.TypeArguments[index: 0].Name}, {expectedCount.Value}] literal requires exactly {expectedCount.Value} elements, got {list.Elements.Count}.",
                    location: list.Location);
            }
        }

        if (expectedBaseName == CollectionNameBitArray && collectionExpectedType?.TypeArguments is
                { Count: >= 1 })
        {
            long? expectedCount =
                GetConstGenericLong(type: collectionExpectedType.TypeArguments[index: 0]);
            if (expectedCount != null && list.Elements.Count != expectedCount.Value)
            {
                ReportError(code: SemanticDiagnosticCode.ArgumentCountMismatch,
                    message:
                    $"BitArray[{expectedCount.Value}] literal requires exactly {expectedCount.Value} elements, got {list.Elements.Count}.",
                    location: list.Location);
            }
        }
    }

    /// <summary>
    /// Resolves the result type of a list literal: uses the expected type when it matches a known sequence
    /// collection base name, otherwise defaults to <c>List[T]</c>.
    /// </summary>
    private TypeSymbol ResolveListResultType(TypeSymbol? expectedType, string? expectedBaseName,
        TypeSymbol? collectionExpectedType, TypeSymbol? elementType)
    {
        if (expectedType != null && (expectedBaseName is "List" or "RotatableList" or "SortedList"
                or "BitList" or CollectionNameArray or CollectionNameBitArray ||
            LiteralConformanceArguments(type: collectionExpectedType, literalProtocol: ListLiteralProtocol) != null))
        {
            return LiteralTypeFromExpected(expectedType: expectedType,
                collectionExpectedType: collectionExpectedType);
        }

        // Return List<T> type by default.
        TypeSymbol? listDef = _registry.LookupType(name: "List");
        if (listDef == null || elementType == null)
        {
            return ErrorTypeSymbol.Instance;
        }

        TypeSymbol listType = _registry.GetOrCreateResolution(genericDef: listDef,
            typeArguments: [elementType]);
        return listType;
    }

    /// <summary>
    /// Resolves and monomorphizes a collection literal's `from_literal[K]` static builder onto the literal
    /// node (<see cref="Expression.ResolvedLiteralBuilder"/>). No-op for the inline `Array`/`BitArray`
    /// literals (pure insertvalue) or a type with no variadic `from_literal`. The element type of the
    /// builder's `Array[E, K]` parameter is <paramref name="builderElementType"/> (T for list/set,
    /// DictEntry[K, V] for dicts).
    /// </summary>
    private void ResolveLiteralBuilder(Expression literal, TypeSymbol resultType,
        TypeSymbol? builderElementType, int elementCount)
    {
        if (builderElementType == null)
        {
            return;
        }

        TypeSymbol collectionType = UnwrapCollectionLiteralExpectedType(type: resultType);
        string? baseName = GetTypeBaseName(type: collectionType);
        if (baseName is CollectionNameArray or CollectionNameBitArray or null)
        {
            return;
        }

        RoutineInfo? builder = FindVariadicLiteralBuilder(collectionType: collectionType);
        if (builder == null)
        {
            return;
        }

        TypeSymbol? arrayDef = _registry.LookupType(name: "Array");
        if (arrayDef == null)
        {
            return;
        }

        MonomorphizeLiteralBuilder(literal: literal,
            builder: builder,
            arrayDef: arrayDef,
            builderElementType: builderElementType,
            elementCount: elementCount);
    }

    /// <summary>
    /// Finds the variadic <c>from_literal</c> routine on <paramref name="collectionType"/> by collecting
    /// all member-routine candidates and returning the first one with a variadic parameter.
    /// Returns null when no such routine is registered on the type.
    /// </summary>
    private RoutineInfo? FindVariadicLiteralBuilder(TypeSymbol collectionType)
    {
        var candidates = new List<RoutineInfo>();
        _registry.CollectMemberRoutineCandidates(type: collectionType,
            memberRoutineName: LiteralBuilderRoutineName,
            candidates: candidates);
        candidates.AddRange(collection: _registry.GetMemberRoutinesForType(type: collectionType)
                                                 .Where(predicate: m =>
                                                      m.Name == LiteralBuilderRoutineName));
        return candidates.FirstOrDefault(predicate: m =>
            m.Parameters.Any(predicate: p => p.IsVariadicParam));
    }

    /// <summary>
    /// Infers the generic type arguments for a <c>from_literal</c> builder by constructing an
    /// <c>Array[E, K]</c> probe literal and unifying it against the builder's parameter, then
    /// stamps the monomorphized (or bare generic) builder onto <paramref name="literal"/>.
    /// </summary>
    private void MonomorphizeLiteralBuilder(Expression literal, RoutineInfo builder,
        TypeSymbol arrayDef, TypeSymbol builderElementType, int elementCount)
    {
        var arityConst = new ConstGenericValueTypeSymbol(literalText: elementCount.ToString(),
            value: elementCount,
            explicitTypeName: "U64");
        TypeSymbol arrayType = _registry.GetOrCreateResolution(genericDef: arrayDef,
            typeArguments: [builderElementType, arityConst]);
        var probe =
            new ListLiteralExpression(Elements: [], ElementType: null, Location: literal.Location)
            {
                ResolvedType = arrayType
            };
        List<TypeSymbol>? inferred = InferGenericTypeArguments(genericRoutine: builder,
            arguments: [probe]);
        literal.ResolvedLiteralBuilder = inferred != null
            ? _registry.GetOrCreateRoutineResolution(genericDef: builder,
                typeArguments: inferred) ?? builder
            : builder;
    }

    private const string LiteralBuilderRoutineName = "from_literal";
    private const string CollectionNameArray = "Array";
    private const string CollectionNameBitArray = "BitArray";

    private TypeSymbol AnalyzeSetLiteralExpression(SetLiteralExpression set,
        TypeSymbol? expectedType = null)
    {
        // Collection literals are entity rvalues; see AnalyzeListLiteralExpression.
        set.IsInFlight = true;
        // Extract expected element type from set-shaped expected types.
        TypeSymbol? collectionExpectedType = expectedType != null
            ? UnwrapCollectionLiteralExpectedType(type: expectedType)
            : null;
        string? expectedBaseName = collectionExpectedType != null
            ? GetTypeBaseName(type: collectionExpectedType)
            : null;
        // `{}` is the empty set. Where a dict-shaped collection is expected the empty literal is `{:}`.
        if (set.Elements.Count == 0 && set.ElementType == null &&
            (expectedBaseName is "Dict" or "SortedDict" or "PriorityQueue" or "SecureDict" ||
             LiteralConformanceArguments(type: collectionExpectedType, literalProtocol: DictLiteralProtocol) !=
             null))
        {
            ReportError(code: SemanticDiagnosticCode.EmptySetWhereDictExpected,
                message: $"`{{}}` is an empty set, but a '{expectedType!.Name}' is expected here. " +
                         "Write `{:}` for an empty dict.",
                location: set.Location);
            return ErrorTypeSymbol.Instance;
        }

        TypeSymbol? expectedElementType = null;
        if (collectionExpectedType is { IsGenericResolution: true, TypeArguments.Count: 1 } &&
            expectedBaseName is "Set" or "SortedSet" or "SecureSet")
        {
            expectedElementType = collectionExpectedType.TypeArguments[index: 0];
        }
        else
        {
            expectedElementType =
                LiteralConformanceArguments(type: collectionExpectedType, literalProtocol: SetLiteralProtocol)
                   ?.FirstOrDefault() ??
                DefaultCollectionArguments(collectionName: "Set", expected: collectionExpectedType)
                   ?.FirstOrDefault();
        }

        TypeSymbol? elementType = ResolveSetElementType(set: set,
            expectedElementType: expectedElementType);

        // Analyze all elements with the inferred/expected element type.
        foreach (Expression elem in set.Elements)
        {
            AnalyzeExpression(expression: elem, expectedType: expectedElementType ?? elementType);
        }

        TypeSymbol setResult = ResolveSetResultType(expectedType: expectedType,
            expectedBaseName: expectedBaseName,
            collectionExpectedType: collectionExpectedType,
            elementType: elementType);

        ResolveLiteralBuilder(literal: set,
            resultType: setResult,
            builderElementType: elementType,
            elementCount: set.Elements.Count);
        return setResult;
    }

    /// <summary>
    /// Infers the element type for a set literal: explicit annotation, first element, expected-type
    /// propagation for empty sets, or an error.
    /// </summary>
    private TypeSymbol? ResolveSetElementType(SetLiteralExpression set,
        TypeSymbol? expectedElementType)
    {
        if (set.ElementType != null)
        {
            return ResolveType(typeExpr: set.ElementType);
        }

        if (set.Elements.Count > 0)
        {
            TypeSymbol first = AnalyzeExpression(expression: set.Elements[index: 0],
                expectedType: expectedElementType);
            // An entity element inferred from the elements is a shared handle slot in Suflae.
            return expectedElementType != null
                ? first
                : _typeResolver.RoamInferredSlot(inferred: first);
        }

        if (expectedElementType != null)
        {
            // Empty set with expected type from context — use it.
            return expectedElementType;
        }

        ReportError(code: SemanticDiagnosticCode.EmptySetNoTypeAnnotation,
            message: "You wrote an empty set `{}` with nothing that says its element type. " +
                     "State the type where the variable is declared, or write `{:}` for an empty dict.",
            location: set.Location);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Resolves the result type of a set literal: uses the expected type when it matches a known set
    /// collection base name, otherwise defaults to <c>Set[T]</c>.
    /// </summary>
    private TypeSymbol ResolveSetResultType(TypeSymbol? expectedType, string? expectedBaseName,
        TypeSymbol? collectionExpectedType, TypeSymbol? elementType)
    {
        if (expectedType != null && (expectedBaseName is "Set" or "SortedSet" or "SecureSet" ||
                                     LiteralConformanceArguments(type: collectionExpectedType,
                                         literalProtocol: SetLiteralProtocol) != null))
        {
            return LiteralTypeFromExpected(expectedType: expectedType,
                collectionExpectedType: collectionExpectedType);
        }

        // Return Set<T> type by default.
        TypeSymbol? setDef = _registry.LookupType(name: "Set");
        if (setDef == null || elementType == null)
        {
            return ErrorTypeSymbol.Instance;
        }

        TypeSymbol setType = _registry.GetOrCreateResolution(genericDef: setDef,
            typeArguments: [elementType]);
        return setType;
    }

    private TypeSymbol AnalyzeDictLiteralExpression(DictLiteralExpression dict,
        TypeSymbol? expectedType = null)
    {
        // Collection literals are entity rvalues; see AnalyzeListLiteralExpression.
        dict.IsInFlight = true;
        // Extract expected key/value types from dict-shaped expected types.
        TypeSymbol? expectedKeyType = null;
        TypeSymbol? expectedValueType = null;
        TypeSymbol? collectionExpectedType = expectedType != null
            ? UnwrapCollectionLiteralExpectedType(type: expectedType)
            : null;
        string? expectedBaseName = collectionExpectedType != null
            ? GetTypeBaseName(type: collectionExpectedType)
            : null;
        bool expectedBuildsLiteral = expectedBaseName is "Dict" or "SortedDict" or "PriorityQueue" or "SecureDict" ||
                                     LiteralConformanceArguments(type: collectionExpectedType,
                                         literalProtocol: DictLiteralProtocol) != null;
        if (collectionExpectedType is { IsGenericResolution: true, TypeArguments.Count: 2 } &&
            expectedBaseName is "Dict" or "SortedDict" or "PriorityQueue" or "SecureDict")
        {
            expectedKeyType = collectionExpectedType.TypeArguments[index: 0];
            expectedValueType = collectionExpectedType.TypeArguments[index: 1];
        }
        else if ((LiteralConformanceArguments(type: collectionExpectedType, literalProtocol: DictLiteralProtocol) ??
                  DefaultCollectionArguments(collectionName: "Dict", expected: collectionExpectedType)) is
                 [{ } keyFromExpected, { } valueFromExpected])
        {
            expectedKeyType = keyFromExpected;
            expectedValueType = valueFromExpected;
        }

        (TypeSymbol? keyType, TypeSymbol? valueType) = InferDictKeyValueTypes(dict: dict,
            expectedKeyType: expectedKeyType,
            expectedValueType: expectedValueType);
        // An entity key or value inferred from the pairs is a shared handle slot in Suflae.
        keyType = keyType is null || expectedKeyType != null ? keyType : _typeResolver.RoamInferredSlot(inferred: keyType);
        valueType = valueType is null || expectedValueType != null
            ? valueType
            : _typeResolver.RoamInferredSlot(inferred: valueType);

        // Analyze all pairs with the inferred/expected key/value types.
        foreach ((Expression Key, Expression Value) pair in dict.Pairs)
        {
            AnalyzeExpression(expression: pair.Key, expectedType: expectedKeyType ?? keyType);
            AnalyzeExpression(expression: pair.Value,
                expectedType: expectedValueType ?? valueType);
        }

        TypeSymbol dictResult;
        if (expectedType != null && expectedBuildsLiteral)
        {
            dictResult = LiteralTypeFromExpected(expectedType: expectedType,
                collectionExpectedType: collectionExpectedType);
        }
        else
        {
            // Return Dict<K, V> type by default.
            TypeSymbol? dictDef = _registry.LookupType(name: "Dict");
            if (dictDef == null || keyType == null || valueType == null)
            {
                return ErrorTypeSymbol.Instance;
            }

            TypeSymbol dictType = _registry.GetOrCreateResolution(genericDef: dictDef,
                typeArguments: [keyType, valueType]);
            dictResult = dictType;
        }

        // Dict literals build `DictEntry[K, V]` values — that's the from_literal element type.
        if (keyType != null && valueType != null && _registry.LookupType(name: "DictEntry") is
                { } dictEntryDef)
        {
            TypeSymbol entryType = _registry.GetOrCreateResolution(genericDef: dictEntryDef,
                typeArguments: [keyType, valueType]);
            ResolveLiteralBuilder(literal: dict,
                resultType: dictResult,
                builderElementType: entryType,
                elementCount: dict.Pairs.Count);
        }

        return dictResult;
    }

    /// <summary>
    /// Determines a dict literal's key/value types: from an explicit annotation, inferred from the first
    /// pair, taken from the expected type for an empty literal, else RF-S (empty, no annotation) with
    /// error types.
    /// </summary>
    private (TypeSymbol? keyType, TypeSymbol? valueType) InferDictKeyValueTypes(
        DictLiteralExpression dict, TypeSymbol? expectedKeyType, TypeSymbol? expectedValueType)
    {
        if (dict is { KeyType: not null, ValueType: not null })
        {
            return (ResolveType(typeExpr: dict.KeyType), ResolveType(typeExpr: dict.ValueType));
        }

        if (dict.Pairs.Count > 0)
        {
            return (
                AnalyzeExpression(expression: dict.Pairs[index: 0].Key,
                    expectedType: expectedKeyType),
                AnalyzeExpression(expression: dict.Pairs[index: 0].Value,
                    expectedType: expectedValueType));
        }

        if (expectedKeyType != null && expectedValueType != null)
        {
            // Empty dict with expected types from context — use them
            return (expectedKeyType, expectedValueType);
        }

        ReportError(code: SemanticDiagnosticCode.EmptyDictNoTypeAnnotation,
            message: "Cannot infer types from empty dict literal without type annotation.",
            location: dict.Location);
        return (ErrorTypeSymbol.Instance, ErrorTypeSymbol.Instance);
    }

    private TypeSymbol AnalyzeDictEntryLiteralExpression(DictEntryLiteralExpression dictEntry,
        TypeSymbol? expectedType = null)
    {
        // Extract expected key/value types from tuple expected type (used by collection constructors)
        TypeSymbol? expectedKeyType = null;
        TypeSymbol? expectedValueType = null;
        if (expectedType is TupleTypeSymbol { ElementTypes.Count: 2 } expectedTuple)
        {
            expectedKeyType = expectedTuple.ElementTypes[index: 0];
            expectedValueType = expectedTuple.ElementTypes[index: 1];
        }

        TypeSymbol keyType =
            AnalyzeExpression(expression: dictEntry.Key, expectedType: expectedKeyType);
        TypeSymbol valueType =
            AnalyzeExpression(expression: dictEntry.Value, expectedType: expectedValueType);

        // Resolve to DictEntry[K, V]
        TypeSymbol? dictEntryDef = _registry.LookupType(name: "DictEntry");
        if (dictEntryDef != null)
        {
            return _registry.GetOrCreateResolution(genericDef: dictEntryDef,
                typeArguments: [keyType, valueType]);
        }

        return ErrorTypeSymbol.Instance;
    }

    private TypeSymbol AnalyzeTupleLiteralExpression(TupleLiteralExpression tuple,
        TypeSymbol? expectedType = null)
    {
        // Extract per-element expected types from tuple expected type
        List<TypeSymbol>? expectedElementTypes = null;
        if (expectedType is TupleTypeSymbol expectedTuple &&
            expectedTuple.ElementTypes.Count == tuple.Elements.Count)
        {
            expectedElementTypes = expectedTuple.ElementTypes;
        }

        // Analyze all element expressions
        var elementTypes = new List<TypeSymbol>();
        for (int i = 0; i < tuple.Elements.Count; i++)
        {
            TypeSymbol? elemExpected = expectedElementTypes?[i];
            TypeSymbol elementType = AnalyzeExpression(expression: tuple.Elements[i],
                expectedType: elemExpected);
            elementTypes.Add(item: elementType);

            // RF-S413: a tuple owns its items, so a variable, field or container element placed in one
            // without `steal` would have two owners (both tear it down).
            if (ChecksHolderOf(type: elementType) &&
                ReadsKeptEntity(value: tuple.Elements[i], includeVariables: true) &&
                MayHoldEntity(type: elementType))
            {
                ReportError(code: SemanticDiagnosticCode.BareEntityAssignment,
                    message: KeptEntityMessage(action: "You are putting into a tuple",
                        value: tuple.Elements[i], type: elementType),
                    location: tuple.Elements[i].Location);
            }
        }

        // Empty tuples are not allowed - use None instead
        if (elementTypes.Count == 0)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownType,
                message: "Empty tuples are not allowed. Use 'None' for the unit type.",
                location: tuple.Location);
            return ErrorTypeSymbol.Instance;
        }

        return _registry.GetOrCreateTupleType(elementTypes: elementTypes);
    }

    private TypeSymbol AnalyzeTypeConversionExpression(TypeConversionExpression conv)
    {
        AnalyzeExpression(expression: conv.Expression);

        TypeSymbol? targetType = LookupTypeWithImports(name: conv.TargetType);
        if (targetType == null)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownConversionTargetType,
                message: $"Unknown conversion target type '{conv.TargetType}'.",
                location: conv.Location);
            return ErrorTypeSymbol.Instance;
        }

        conv.LoweringKind = CallLoweringKind.ValueConversion;
        conv.ConstructedType = targetType;
        return targetType;
    }

    private TypeSymbol AnalyzeChainedComparisonExpression(ChainedComparisonExpression chain)
    {
        // Validate that operators don't mix ascending and descending
        ValidateComparisonChain(chain: chain, location: chain.Location);

        // Analyze all operands and validate comparisons between consecutive pairs
        // The operands made of literals are analyzed last, with the type of the chain's other operands, so a literal
        // never takes its default type first (`0 <= me <= 18446744073709551615` beside a U64).
        var operandTypes = new List<TypeSymbol>(
            collection: chain.Operands.Select(selector: _ => (TypeSymbol)ErrorTypeSymbol.Instance));
        TypeSymbol? numberPeer = null;
        for (int i = 0; i < chain.Operands.Count; i++)
        {
            if (LeadingNumber(expression: chain.Operands[index: i]) == null)
            {
                operandTypes[index: i] = AnalyzeExpression(expression: chain.Operands[index: i]);
                numberPeer ??= ConstGenericValueType(type: operandTypes[index: i]) ?? operandTypes[index: i];
            }
        }

        for (int i = 0; i < chain.Operands.Count; i++)
        {
            if (LeadingNumber(expression: chain.Operands[index: i]) is { } number)
            {
                operandTypes[index: i] = AnalyzeExpression(expression: chain.Operands[index: i],
                    expectedType: numberPeer != null &&
                                  ConformsToPeer(literal: number, peer: numberPeer)
                        ? numberPeer
                        : null);
            }
        }

        // Bare unsuffixed integer literals default to S64; re-infer them against the chain's concrete
        // integer operand so `0 < n < 100` (n: S32) conforms the literals to S32 instead of tripping
        // RF-S060. A single pivot (the first non-literal fixed-width operand) covers the whole chain,
        // so even `0 < 5 < n` conforms both literals. Mirrors the pairwise literal re-inference in
        // AnalyzeBinaryExpression.
        // A float or decimal pivot takes the bare numbers too (`-1.0 <= x <= 1.0` with a B128 `x`).
        TypeSymbol? pivot = null;
        for (int i = 0; i < chain.Operands.Count; i++)
        {
            TypeSymbol operandType = ConstGenericValueType(type: operandTypes[index: i]) ?? operandTypes[index: i];
            if (LeadingNumber(expression: chain.Operands[index: i]) == null &&
                (IsFixedWidthIntegerType(type: operandType) || IsFloatType(type: operandType) ||
                 IsDecimalType(type: operandType)))
            {
                pivot = operandType;
                break;
            }
        }

        if (pivot != null)
        {
            for (int i = 0; i < chain.Operands.Count; i++)
            {
                if (LeadingNumber(expression: chain.Operands[index: i]) is { } literal &&
                    ConformsToPeer(literal: literal, peer: pivot) &&
                    operandTypes[index: i].Name != pivot.Name)
                {
                    operandTypes[index: i] = AnalyzeExpression(
                        expression: chain.Operands[index: i],
                        expectedType: pivot);
                }
            }
        }

        // Validate each comparison pair
        for (int i = 0; i < chain.Operators.Count; i++)
        {
            ValidateComparisonOperands(left: operandTypes[index: i],
                right: operandTypes[index: i + 1],
                op: chain.Operators[index: i],
                location: chain.Location);
        }

        // Chained comparisons always return bool
        return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
    }

    private TypeSymbol AnalyzeBlockExpression(BlockExpression block)
    {
        // Block expression evaluates to its contained value expression
        return AnalyzeExpression(expression: block.Value);
    }

    /// <summary>
    /// Analyzes a when expression (pattern matching expression).
    /// Returns the common type of all branch results.
    /// </summary>
    private TypeSymbol AnalyzeWhenExpression(WhenExpression when, TypeSymbol? expectedType = null)
    {
        // Analyze the matched expression (Bool for subject-less when — arms are conditions)
        TypeSymbol matchedType = when.Expression != null
            ? AnalyzeExpression(expression: when.Expression)
            : _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;

        // #88: Pattern order enforcement — else/wildcard must be last
        ValidateWhenPatternOrder(when: when);

        // #130/#148: Duplicate pattern detection
        ValidateWhenDuplicatePatterns(when: when);

        TypeSymbol? resultType = null;
        bool hasElse = false;

        foreach (WhenClause clause in when.Clauses)
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: "when_clause");

            // Analyze the pattern. `else as name` gives the name the one type left after the earlier arms.
            if (clause.Pattern is ElsePattern { VariableName: { } elseName } elsePat)
            {
                TypeSymbol? rest = ResolveElseRemainder(elsePat: elsePat,
                    matchedType: matchedType,
                    priorClauses: when.Clauses.TakeWhile(predicate: c => !ReferenceEquals(objA: c, objB: clause)));
                elsePat.RemainingType = rest;
                elsePat.BindsCarrierError = rest is not null && GetCarrierBaseName(type: matchedType) == "Check" &&
                                            rest.BareName == Builder.Declaration.RuntimeContract.Crashables;
                DeclarePatternVariable(name: elseName, type: rest ?? ErrorTypeSymbol.Instance, location: elsePat.Location);
            }
            else
            {
                AnalyzePattern(pattern: clause.Pattern, matchedType: matchedType);
            }

            // Check for else clause
            if (clause.Pattern is WildcardPattern or ElsePattern)
            {
                hasElse = true;
            }

            resultType = AnalyzeWhenClauseBody(clause: clause, resultType: resultType, expectedType: expectedType);

            _registry.ExitScope();
        }

        // Check exhaustiveness — when expressions MUST produce a value for all inputs
        if (!hasElse)
        {
            ExhaustivenessResult exhaustiveness = CheckExhaustiveness(
                clauses: when.Clauses,
                matchedType: matchedType);

            if (!exhaustiveness.IsExhaustive)
            {
                string missing = exhaustiveness.MissingCases.Count > 0
                    ? $" Missing cases: {string.Join(separator: ", ", values: exhaustiveness.MissingCases)}."
                    : "";
                ReportError(code: SemanticDiagnosticCode.NonExhaustiveMatch,
                    message:
                    $"When expression is not exhaustive — all possible values must be handled.{missing}",
                    location: when.Location);
            }
        }

        return resultType ?? ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// #88: Reports PatternOrderViolation for any clause that follows an <c>else</c>/wildcard pattern
    /// (which must be last).
    /// </summary>
    private void ValidateWhenPatternOrder(WhenExpression when)
    {
        bool seenElse = false;
        foreach (Pattern pattern in when.Clauses.Select(selector: clause => clause.Pattern))
        {
            if (seenElse)
            {
                ReportError(code: SemanticDiagnosticCode.PatternOrderViolation,
                    message: "Unreachable pattern after 'else' or wildcard.",
                    location: pattern.Location);
            }

            if (pattern is ElsePattern or WildcardPattern)
            {
                seenElse = true;
            }
        }
    }

    /// <summary>
    /// #130/#148: Reports DuplicatePattern for any clause whose pattern key repeats an earlier one.
    /// </summary>
    private void ValidateWhenDuplicatePatterns(WhenExpression when)
    {
        var seenPatterns = new HashSet<string>();
        foreach (Pattern pattern in when.Clauses.Select(selector: clause => clause.Pattern))
        {
            string? patternKey = GetPatternKey(pattern: pattern);
            if (patternKey != null && !seenPatterns.Add(item: patternKey))
            {
                ReportError(code: SemanticDiagnosticCode.DuplicatePattern,
                    message: $"Duplicate pattern: {patternKey}.",
                    location: pattern.Location);
            }
        }
    }

    /// <summary>
    /// Analyzes one <c>when</c>-expression clause body — an expression (<c>=&gt;</c>), a return, or a
    /// <c>becomes</c> block — and folds its branch type into the running <paramref name="resultType"/>
    /// (reporting a mismatch against the first branch). Returns the updated result type.
    /// </summary>
    private TypeSymbol? AnalyzeWhenClauseBody(WhenClause clause, TypeSymbol? resultType, TypeSymbol? expectedType)
    {
        // When expressions require expression bodies that return values
        // The Body is a Statement, but for expressions it should typically be an ExpressionStatement
        if (clause.Body is ExpressionStatement exprStmt)
        {
            // An arm's value takes the type the whole `when` goes to, and a bare number arm takes the type of the
            // arms before it (`x > 0 => x`, `else => 0`).
            TypeSymbol? armExpected = expectedType ?? (exprStmt.Expression is LiteralExpression ? resultType : null);
            TypeSymbol branchType = AnalyzeExpression(expression: exprStmt.Expression, expectedType: armExpected);
            return FoldWhenBranchType(resultType: resultType,
                branchType: branchType,
                errorLocation: clause.Body.Location);
        }

        if (clause.Body is ReturnStatement { Value: not null } ret)
        {
            // Allow return statements in when expressions
            TypeSymbol branchType = AnalyzeExpression(expression: ret.Value);
            return resultType ?? branchType;
        }

        if (clause.Body is BlockStatement block)
        {
            return AnalyzeWhenBlockClauseBody(block: block, resultType: resultType);
        }

        // Analyze as regular statement
        AnalyzeStatement(statement: clause.Body);
        return resultType;
    }

    /// <summary>
    /// Folds a branch type into the running result type for a <c>when</c> expression clause, reporting
    /// <see cref="SemanticDiagnosticCode.WhenBranchTypeMismatch"/> when the branch is incompatible with
    /// the established result type. Returns the (possibly updated) result type.
    /// </summary>
    private TypeSymbol? FoldWhenBranchType(TypeSymbol? resultType, TypeSymbol branchType,
        SourceLocation errorLocation)
    {
        if (resultType == null)
        {
            return branchType;
        }

        if (!IsAssignableTo(source: branchType, target: resultType))
        {
            ReportError(code: SemanticDiagnosticCode.WhenBranchTypeMismatch,
                message:
                $"When expression branches have incompatible types: '{resultType.Name}' and '{branchType.Name}'.",
                location: errorLocation);
        }

        return resultType;
    }

    /// <summary>
    /// A block body inside a <c>when</c> that gives a value: an arm there is one expression after <c>=&gt;</c>, so a
    /// block is an error. Its statements are still checked, so their own mistakes are reported too.
    /// </summary>
    private TypeSymbol? AnalyzeWhenBlockClauseBody(BlockStatement block, TypeSymbol? resultType)
    {
        // The arm's value is its last line, `becomes value`; the lines before it are ordinary statements.
        if (block.Statements is [.., ExpressionStatement { IsBecomes: true } becomes])
        {
            _registry.EnterScope(kind: ScopeKind.Block, name: null);
            try
            {
                foreach (Statement stmt in block.Statements.SkipLast(count: 1))
                {
                    AnalyzeStatement(statement: stmt);
                }

                TypeSymbol branchType = AnalyzeExpression(expression: becomes.Expression,
                    expectedType: resultType);
                return FoldWhenBranchType(resultType: resultType,
                    branchType: branchType,
                    errorLocation: becomes.Location);
            }
            finally
            {
                _registry.ExitScope();
            }
        }

        foreach (Statement stmt in block.Statements)
        {
            AnalyzeStatement(statement: stmt);
        }

        ReportError(code: SemanticDiagnosticCode.WhenExpressionArmIsBlock,
            message:
            "You wrote several statements in an arm of a 'when' that gives a value, and none of them says the arm's " +
            "value. End the block with 'becomes value', or make the arm one expression after '=>'.",
            location: block.Location);
        return resultType;
    }

    /// <summary>
    /// Infers type arguments for a generic routine from call arguments.
    /// Returns the inferred type arguments, or null if inference fails.
    /// </summary>
    private List<TypeSymbol>? InferGenericTypeArguments(RoutineInfo genericRoutine,
        List<Expression> arguments, TypeSymbol? expectedType = null)
    {
        if (genericRoutine.GenericParameters == null ||
            genericRoutine.GenericParameters.Count == 0)
        {
            return null;
        }

        var typeArgs = new TypeSymbol?[genericRoutine.GenericParameters.Count];
        var lambdasRead = new HashSet<int>();

        // First pass: infer from argument types. A bare number binds nothing yet: it carries no type of its own,
        // so the other arguments, the constraints and the expected type come first (see the last pass).
        InferTypeArgsFromArguments(genericRoutine: genericRoutine,
            arguments: arguments,
            typeArgs: typeArgs,
            deferred: false,
            lambdasRead: lambdasRead);

        // Second pass: infer any still-unbound generics from `needs` constraints whose constraining
        // param is now known (e.g. `zip[U, S2](other: Accessing[S2]) needs S2 obeys Iterable[U]` —
        // S2 binds from the argument, then U binds from S2's Iterable conformance).
        InferGenericsFromConstraints(routine: genericRoutine, inferred: typeArgs);

        // Third pass: return-type-directed inference. A type parameter that appears ONLY in the return
        // type (e.g. `default[T]() -> T`) can never bind from the
        // arguments; unify the routine's return type against the call's expected type — the field /
        // parameter / assignment target the result flows into — to fill it. Only used to fill gaps
        // (already-inferred params from the argument pass win).
        if (expectedType is not null && expectedType != ErrorTypeSymbol.Instance &&
            genericRoutine.ReturnType is { } returnType)
        {
            InferMemberRoutineTypeArgumentsFromTypes(paramType: returnType,
                argType: expectedType,
                genericParameters: genericRoutine.GenericParameters,
                inferred: typeArgs);
        }

        // Last pass: a type parameter only bare numbers give binds from their default types (`max(a: 5, b: 3)` is
        // over S64), after which the numbers conform to it. The lambdas the first pass left come after the numbers.
        InferTypeArgsFromArguments(genericRoutine: genericRoutine,
            arguments: arguments,
            typeArgs: typeArgs,
            deferred: true,
            lambdasRead: lambdasRead);
        InferGenericsFromConstraints(routine: genericRoutine, inferred: typeArgs);

        // All type args must be inferred.
        return typeArgs.Any(predicate: t => t == null)
            ? null
            : typeArgs.Select(selector: t => t!)
                      .ToList();
    }

    /// <summary>
    /// First-pass argument-type inference: resolves each argument's type and unifies it against the
    /// corresponding parameter type to bind generic-parameter slots in <paramref name="typeArgs"/>.
    /// Variadic packed-array arguments reuse their already-resolved <c>Array[T,K]</c> type to preserve
    /// the arity constant <c>K</c>; error-typed arguments are skipped.
    /// </summary>
    private void InferTypeArgsFromArguments(RoutineInfo genericRoutine, List<Expression> arguments,
        TypeSymbol?[] typeArgs, bool deferred, HashSet<int> lambdasRead)
    {
        // GenericParameters is guaranteed non-null when the caller checks IsGenericDefinition,
        // but the field itself is nullable — guard here so the pass-down is clean.
        if (genericRoutine.GenericParameters is not { } genericParameters)
        {
            return;
        }

        int argCount = Math.Min(val1: genericRoutine.Parameters.Count, val2: arguments.Count);
        foreach (int i in InferenceArguments(arguments: arguments, count: argCount, deferred: deferred,
                     parameters: genericRoutine.Parameters, genericParameters: genericParameters, bound: typeArgs,
                     lambdasRead: lambdasRead))
        {
            TypeSymbol paramType = genericRoutine.Parameters[index: i].Type;
            // A bare number only binds a parameter nothing else bound: once bound it conforms, unread here.
            if (deferred && IsBareNumber(argument: arguments[index: i]) &&
                paramType is GenericParameterTypeSymbol bound &&
                genericParameters.IndexOf(item: bound.Name) is var slot and >= 0 && typeArgs[slot] != null)
            {
                continue;
            }

            Expression argExpr = arguments[index: i] is NamedArgumentExpression na
                ? na.Value
                : arguments[index: i];

            // A variadic call packs its trailing args into an Array[T, K] literal (already analyzed
            // against the Array expected type). Re-analyzing it here without that expected type would
            // default it back to List[T] and lose the arity K, so reuse its resolved Array type.
            TypeSymbol argType = CollectionLiteralConstraintType(routine: genericRoutine,
                paramType: paramType,
                argument: argExpr,
                receiver: null,
                boundTypeArgs: typeArgs) is { } literalType
                ? AnalyzeExpression(expression: argExpr, expectedType: literalType)
                : ResolveArgTypeForInference(argExpr: argExpr);
            if (argType == ErrorTypeSymbol.Instance)
            {
                continue;
            }

            argType = MarkerTokenArgumentType(routine: genericRoutine, paramType: paramType, argType: argType);
            argType = SharedEntities.AsHandleForInference(argType: argType, paramType: paramType);
            // A value's token handed to a parameter of the routine's own type (`nested_repr(value: T)` given an
            // iterated element) binds the value's type: the call reads the value (TokenReadLoweringPass).
            if (paramType is GenericParameterTypeSymbol)
            {
                argType = ValueTokenTarget(type: argType) ?? argType;
            }

            // Recurse into TypeArguments so const- and type-generics inside a parameterized
            // pattern (e.g. array: Array[Byte, N]) bind from the matching position in argType.
            InferMemberRoutineTypeArgumentsFromTypes(paramType: paramType,
                argType: argType,
                genericParameters: genericParameters,
                inferred: typeArgs);
        }
    }

    /// <summary>
    /// The argument positions one inference pass reads. The first pass reads every argument but the bare numbers,
    /// and a lambda only once the types of its parameters are bound. The deferred pass reads the bare numbers (which
    /// bind a parameter only nothing else bound) and then the lambdas the first pass left
    /// (<c>accumulate(start: 100, combiner: (acc, x) => acc + x)</c> types <c>acc</c> from <c>100</c>). The positions
    /// are produced as the caller binds, so a lambda's readiness sees what the arguments before it bound.
    /// </summary>
    private static IEnumerable<int> InferenceArguments(List<Expression> arguments, int count, bool deferred,
        IReadOnlyList<ParamInfo> parameters, List<string> genericParameters, TypeSymbol?[] bound,
        HashSet<int> lambdasRead)
    {
        static bool IsLambda(Expression argument)
        {
            return (argument is NamedArgumentExpression named
                ? named.Value
                : argument) is LambdaExpression;
        }

        bool Unbound(TypeSymbol type)
        {
            return type switch
            {
                GenericParameterTypeSymbol parameter =>
                    genericParameters.IndexOf(item: parameter.Name) is var slot and >= 0 && bound[slot] == null,
                RoutineTypeSymbol routine => routine.ParameterTypes.Any(predicate: Unbound),
                _ => type.TypeArguments?.Any(predicate: Unbound) == true
            };
        }

        if (!deferred)
        {
            for (int i = 0; i < count; i++)
            {
                if (!IsBareNumber(argument: arguments[index: i]) && !IsLambda(argument: arguments[index: i]))
                {
                    yield return i;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (IsLambda(argument: arguments[index: i]) &&
                    !(parameters[index: i].Type is RoutineTypeSymbol routine &&
                      routine.ParameterTypes.Any(predicate: Unbound)))
                {
                    lambdasRead.Add(item: i);
                    yield return i;
                }
            }

            yield break;
        }

        for (int i = 0; i < count; i++)
        {
            if (IsBareNumber(argument: arguments[index: i]))
            {
                yield return i;
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (IsLambda(argument: arguments[index: i]) && lambdasRead.Add(item: i))
            {
                yield return i;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="argument"/> is a bare number (<c>5</c>, <c>-0.5</c>). A number carries no type of its
    /// own, so inference binds a type parameter from it only when nothing else gives that parameter: another
    /// argument (<c>LLVM::sub(a: 0, b: me)</c> binds <c>T</c> from <c>me</c>), a constraint, or the type the result
    /// goes to. The number then conforms to the bound type.
    /// </summary>
    private static bool IsBareNumber(Expression argument)
    {
        if (argument is NamedArgumentExpression named)
        {
            argument = named.Value;
        }

        if (argument is UnaryExpression { Operator: UnaryOperator.Minus } negated)
        {
            argument = negated.Operand;
        }

        return argument is LiteralExpression { LiteralType: TokenType.UndecidedInteger or TokenType.UndecidedDecimal };
    }

    /// <summary>
    /// Resolves the effective type of an expression for generic type-argument inference. For a
    /// <c>ListLiteralExpression</c> whose resolved type is already an <c>Array[T,K]</c> (a packed
    /// variadic argument), returns that resolved type directly to preserve the arity constant. All
    /// other expressions are analyzed fresh.
    /// </summary>
    private TypeSymbol ResolveArgTypeForInference(Expression argExpr)
    {
        if (argExpr is ListLiteralExpression { ResolvedType: { } packed } &&
            GetTypeBaseName(type: packed) is "Array")
        {
            return packed;
        }

        return AnalyzeExpression(expression: argExpr);
    }

    /// <summary>
    /// Infers still-unbound memberRoutine generics from the routine's <c>needs</c> constraints. For a
    /// constraint <c>S obeys Proto[..., U, ...]</c> where <c>S</c> is already inferred to a concrete
    /// type, the bound type's actual conformance to <c>Proto</c> supplies the concrete arguments,
    /// which are unified positionally against the constraint's type arguments to fill in <c>U</c>.
    /// Enables element-type inference for source-parameterized adapters (zip/extend/set-ops) whose
    /// element generic no longer appears directly in a parameter type.
    /// </summary>
    private static void InferGenericsFromConstraints(RoutineInfo routine, TypeSymbol?[] inferred)
    {
        if (routine.GenericConstraints is not { Count: > 0 } constraints ||
            routine.GenericParameters is not { Count: > 0 } gp)
        {
            return;
        }

        foreach (GenericConstraintDeclaration constraint in constraints)
        {
            if (constraint.ConstraintTypes is not { Count: > 0 } constraintTypes)
            {
                continue;
            }

            int boundIdx = gp.IndexOf(item: constraint.ParameterName);
            if (boundIdx < 0 || inferred[boundIdx] is not { } boundType)
            {
                continue;
            }

            List<TypeSymbol> conformances = ImplementedProtocolsOf(type: boundType);
            InferFromConstraintProtocols(gp: gp,
                inferred: inferred,
                constraintTypes: constraintTypes,
                conformances: conformances);
        }
    }

    /// <summary>
    /// For each protocol type expression in a constraint's type list, locates the bound type's
    /// conformance to that protocol and unifies the protocol's type-argument positions against the
    /// constraint's type-argument positions to bind still-unresolved generic slots.
    /// </summary>
    private static void InferFromConstraintProtocols(List<string> gp, TypeSymbol?[] inferred,
        List<TypeExpression> constraintTypes, List<TypeSymbol> conformances)
    {
        foreach (TypeExpression ct in constraintTypes)
        {
            if (ct.GenericArguments is not { Count: > 0 } ctArgs)
            {
                continue;
            }

            TypeSymbol? conformance =
                conformances.FirstOrDefault(predicate: p => ProtocolBaseName(type: p) == ct.Name);
            if (conformance?.TypeArguments is not { Count: > 0 } confArgs)
            {
                continue;
            }

            BindConstraintTypeArgs(gp: gp,
                inferred: inferred,
                ctArgs: ctArgs,
                confArgs: confArgs);
        }
    }

    /// <summary>
    /// Positionally unifies a constraint's type-argument names against a concrete conformance's type
    /// arguments, binding any unresolved generic slot whose name appears in the constraint list.
    /// </summary>
    private static void BindConstraintTypeArgs(List<string> gp, TypeSymbol?[] inferred,
        List<TypeExpression> ctArgs, List<TypeSymbol> confArgs)
    {
        int n = Math.Min(val1: ctArgs.Count, val2: confArgs.Count);
        for (int k = 0; k < n; k++)
        {
            int uIdx = gp.IndexOf(item: ctArgs[index: k].Name);
            if (uIdx >= 0 && inferred[uIdx] == null)
            {
                inferred[uIdx] = confArgs[index: k];
            }
        }
    }

    /// <summary>Reads a type's implemented-protocol list across the type kinds that carry one.</summary>
    private static List<TypeSymbol> ImplementedProtocolsOf(TypeSymbol type)
    {
        return type switch
        {
            RecordTypeSymbol r => r.ImplementedProtocols.ToList(),
            EntityTypeSymbol e => e.ImplementedProtocols.ToList(),
            _ => []
        };
    }

    /// <summary>Base (un-parameterized) name of a possibly-parameterized protocol type.</summary>
    private static string ProtocolBaseName(TypeSymbol type)
    {
        return type.BareName;
    }

    /// <summary>
    /// Infers memberRoutine-level generic type arguments for an already owner-resolved memberRoutine.
    /// </summary>
    private List<TypeSymbol>? InferMemberRoutineGenericTypeArguments(
        RoutineInfo genericMemberRoutine, List<Expression> arguments,
        TypeSymbol? receiverType = null, TypeSymbol? expectedType = null)
    {
        if (genericMemberRoutine.GenericParameters == null ||
            genericMemberRoutine.GenericParameters.Count == 0)
        {
            return null;
        }

        var inferred = new TypeSymbol?[genericMemberRoutine.GenericParameters.Count];
        var lambdasRead = new HashSet<int>();

        // Receiver-based inference for a member declared on a SPECIALIZED generic instantiation
        // (e.g. `routine List[Work[V]].gather!()`): unify the memberRoutine's MeType pattern
        // (List[Work[V]]) against the actual receiver (List[Work[S64]]) to bind generic params
        // (V) that appear only in the receiver, not in any value parameter.
        if (genericMemberRoutine.MeType is { } mePattern && receiverType != null)
        {
            InferMemberRoutineTypeArgumentsFromTypes(paramType: mePattern,
                argType: receiverType,
                genericParameters: genericMemberRoutine.GenericParameters,
                inferred: inferred);
        }

        int argCount = Math.Min(val1: genericMemberRoutine.Parameters.Count,
            val2: arguments.Count);
        InferMemberTypeArgsFromArguments(genericMemberRoutine: genericMemberRoutine, arguments: arguments,
            argCount: argCount, inferred: inferred, deferred: false, lambdasRead: lambdasRead,
            receiverType: receiverType);

        // Infer still-unbound generics from `needs` constraints (e.g. U from `S obeys Iterable[U]`).
        InferGenericsFromConstraints(routine: genericMemberRoutine, inferred: inferred);

        // A type parameter only the result mentions (`count.to() -> T`) binds from where the result goes: the
        // declared variable, the return type, the parameter, or the payload of a `try`/`check` carrier.
        if (expectedType is not (null or ErrorTypeSymbol) && genericMemberRoutine.ReturnType is { } returnType)
        {
            InferMemberRoutineTypeArgumentsFromTypes(paramType: returnType,
                argType: expectedType,
                genericParameters: genericMemberRoutine.GenericParameters,
                inferred: inferred);
        }

        // A type parameter only bare numbers give binds last, from their default types, and the lambdas the first
        // pass left come after the numbers.
        InferMemberTypeArgsFromArguments(genericMemberRoutine: genericMemberRoutine, arguments: arguments,
            argCount: argCount, inferred: inferred, deferred: true, lambdasRead: lambdasRead,
            receiverType: receiverType);
        InferGenericsFromConstraints(routine: genericMemberRoutine, inferred: inferred);

        for (int i = 0; i < inferred.Length; i++)
        {
            if (inferred[i] == null)
            {
                return null;
            }
        }

        return inferred.ToList()!;
    }

    /// <summary>Binds member-routine type parameters from the arguments of one inference pass (see
    /// <see cref="InferenceArguments"/>).</summary>
    private void InferMemberTypeArgsFromArguments(RoutineInfo genericMemberRoutine, List<Expression> arguments,
        int argCount, TypeSymbol?[] inferred, bool deferred, HashSet<int> lambdasRead, TypeSymbol? receiverType)
    {
        foreach (int i in InferenceArguments(arguments: arguments, count: argCount, deferred: deferred,
                     parameters: genericMemberRoutine.Parameters,
                     genericParameters: genericMemberRoutine.GenericParameters!, bound: inferred,
                     lambdasRead: lambdasRead))
        {
            // A bare number only binds a parameter nothing else bound: once bound it conforms, unread here.
            if (deferred && IsBareNumber(argument: arguments[index: i]) &&
                genericMemberRoutine.Parameters[index: i].Type is GenericParameterTypeSymbol bound &&
                genericMemberRoutine.GenericParameters!.IndexOf(item: bound.Name) is var slot and >= 0 &&
                inferred[slot] != null)
            {
                continue;
            }

            Expression arg = arguments[index: i] is NamedArgumentExpression named
                ? named.Value
                : arguments[index: i];
            TypeSymbol argType = CollectionLiteralConstraintType(routine: genericMemberRoutine,
                paramType: genericMemberRoutine.Parameters[index: i].Type,
                argument: arg,
                receiver: receiverType,
                boundTypeArgs: inferred) is { } literalType
                ? AnalyzeExpression(expression: arg, expectedType: literalType)
                : arg.ResolvedType ?? AnalyzeExpression(expression: arg);
            if (argType == ErrorTypeSymbol.Instance)
            {
                continue;
            }

            argType = MarkerTokenArgumentType(routine: genericMemberRoutine,
                paramType: genericMemberRoutine.Parameters[index: i].Type,
                argType: argType);
            InferMemberRoutineTypeArgumentsFromTypes(
                paramType: genericMemberRoutine.Parameters[index: i].Type,
                argType: argType,
                genericParameters: genericMemberRoutine.GenericParameters!,
                inferred: inferred);
        }
    }

    private TypeSymbol MarkerTokenArgumentType(RoutineInfo routine, TypeSymbol paramType, TypeSymbol argType)
    {
        // A token parameter (`value: Viewing[T]`) given a value or a handle binds as the token the call converts
        // it to: `T` is the value's type (or the handle's inner type).
        string tokenKind = paramType.BareName;
        if (tokenKind is not (Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying) ||
            paramType.TypeArguments is not [_] || argType.BareName == tokenKind ||
            argType is ErrorTypeSymbol)
        {
            return argType;
        }

        string handle = argType.BareName;
        TypeSymbol inner = handle is Declaration.RuntimeContract.Retained or Declaration.RuntimeContract.Roamed
                               or Declaration.RuntimeContract.Modifying && argType.TypeArguments is [{ } held, ..]
            ? argType.TypeArguments![index: 0]
            : argType;
        return _registry.LookupType(name: tokenKind) is { } tokenDef
            ? _registry.GetOrCreateResolution(genericDef: tokenDef, typeArguments: [inner])
            : argType;
    }

    private static void InferMemberRoutineTypeArgumentsFromTypes(TypeSymbol paramType,
        TypeSymbol argType, List<string> genericParameters, TypeSymbol?[] inferred)
    {
        if (paramType is GenericParameterTypeSymbol)
        {
            BindInferredSlot(name: paramType.Name,
                argType: argType,
                genericParameters: genericParameters,
                inferred: inferred);
            return;
        }

        // Marker borrow wrappers around a BARE generic param (Accessing[S2]/Controlling[S2]) are
        // transparent at call sites: a bare argument `a` passed where `Accessing[S2]` is expected
        // binds S2 to the WHOLE argument type. Without this, `other: Viewing[S2]` against arg
        // `List[S64]` would wrongly element-wise-bind S2 to S64 (the inner element). Restricted to a
        // bare-generic inner so tokens around constructed types (e.g. `Viewing[List[T]]`) keep
        // the normal element-wise unification that binds their inner params (T) correctly.
        if (TryInferFromMarkerProtocolWrapper(paramType: paramType,
                argType: argType,
                genericParameters: genericParameters,
                inferred: inferred))
        {
            return;
        }

        // A token parameter takes the entity itself at the call site (`largest(items: xs)` views `xs`), so
        // `Viewing[List[T]]` (or `Modifying[List[T]]`) against `List[S64]` binds through the token: T = S64.
        if (paramType is { BareName: Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying,
                TypeArguments: [{ } viewed] } &&
            argType is EntityTypeSymbol)
        {
            InferMemberRoutineTypeArgumentsFromTypes(paramType: viewed,
                argType: argType,
                genericParameters: genericParameters,
                inferred: inferred);
            return;
        }

        if (paramType is { TypeArguments: { Count: > 0 } paramArgs } &&
            argType is { TypeArguments: { Count: > 0 } argArgs } &&
            paramArgs.Count == argArgs.Count)
        {
            for (int i = 0; i < paramArgs.Count; i++)
            {
                InferMemberRoutineTypeArgumentsFromTypes(paramType: paramArgs[index: i],
                    argType: argArgs[index: i],
                    genericParameters: genericParameters,
                    inferred: inferred);
            }
        }

        // RoutineTypeSymbol is structural: its parameter/return types live in ParameterTypes/ReturnType,
        // not TypeArguments. Without this branch, `Callable[(T,), U]` would not unify against
        // `Callable[(S64,), S64]` and memberRoutine-level params (e.g. `select[U]`) would stay unresolved.
        InferFromRoutineTypeStructure(paramType: paramType,
            argType: argType,
            genericParameters: genericParameters,
            inferred: inferred);
    }

    /// <summary>
    /// Binds a generic-parameter slot by name if it is not yet bound: sets
    /// <paramref name="inferred"/>[slot] = <paramref name="argType"/> for the first matching slot.
    /// </summary>
    private static void BindInferredSlot(string name, TypeSymbol argType,
        List<string> genericParameters, TypeSymbol?[] inferred)
    {
        int idx = genericParameters.ToList()
                                   .IndexOf(item: name);
        if (idx >= 0 && inferred[idx] == null)
        {
            inferred[idx] = argType;
        }
    }

    /// <summary>
    /// Handles inference through a token (<c>Viewing[S2]</c> / <c>Modifying[S2]</c>) around a bare generic
    /// parameter, given the value the call makes the token from: binds the token's inner param
    /// slot to the WHOLE argument type rather than element-wise. Returns true and binds the slot
    /// when applicable; returns false otherwise so the caller falls through to element-wise unification.
    /// </summary>
    private static bool TryInferFromMarkerProtocolWrapper(TypeSymbol paramType, TypeSymbol argType,
        List<string> genericParameters, TypeSymbol?[] inferred)
    {
        if (paramType is not { TypeArguments: [GenericParameterTypeSymbol markerParam] })
        {
            return false;
        }

        if (paramType.BareName is not (Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying) ||
            argType.BareName is Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying)
        {
            return false;
        }

        BindInferredSlot(name: markerParam.Name,
            argType: argType,
            genericParameters: genericParameters,
            inferred: inferred);
        return true;
    }

    /// <summary>
    /// Unifies a <c>RoutineTypeSymbol</c> parameter against a <c>RoutineTypeSymbol</c> argument by
    /// recursively inferring from each paired parameter type and then from the return types.
    /// No-op when either side is not a <c>RoutineTypeSymbol</c> or the parameter counts differ.
    /// </summary>
    private static void InferFromRoutineTypeStructure(TypeSymbol paramType, TypeSymbol argType,
        List<string> genericParameters, TypeSymbol?[] inferred)
    {
        if (paramType is not RoutineTypeSymbol paramRoutine ||
            argType is not RoutineTypeSymbol argRoutine)
        {
            return;
        }

        if (paramRoutine.ParameterTypes.Count != argRoutine.ParameterTypes.Count)
        {
            return;
        }

        for (int i = 0; i < paramRoutine.ParameterTypes.Count; i++)
        {
            InferMemberRoutineTypeArgumentsFromTypes(
                paramType: paramRoutine.ParameterTypes[index: i],
                argType: argRoutine.ParameterTypes[index: i],
                genericParameters: genericParameters,
                inferred: inferred);
        }

        if (paramRoutine.ReturnType is { } paramRet && argRoutine.ReturnType is { } argRet)
        {
            InferMemberRoutineTypeArgumentsFromTypes(paramType: paramRet,
                argType: argRet,
                genericParameters: genericParameters,
                inferred: inferred);
        }
    }
}
