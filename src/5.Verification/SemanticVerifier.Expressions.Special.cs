using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    private TypeSymbol AnalyzeInsertedTextExpression(InsertedTextExpression insertedText)
    {
        foreach (InsertedTextPart part in insertedText.Parts)
        {
            if (part is ExpressionPart exprPart)
            {
                TypeSymbol inserted = AnalyzeExpression(expression: exprPart.Expression);
                ValidateFTextFormatSpec(formatSpec: exprPart.FormatSpec,
                    location: exprPart.Location);
                // `{x}` calls `x.represent()`, `{x?}` calls `x.diagnose()`.
                string shown = exprPart.FormatSpec?.Contains(value: '?') == true
                    ? Declaration.RuntimeContract.Display.Diagnose
                    : Declaration.RuntimeContract.Display.Represent;
                if (inserted is not ErrorTypeSymbol)
                {
                    ValidateDangerousImplicitCall(
                        routine: _registry.LookupMemberRoutine(type: inserted, memberRoutineName: shown),
                        written: $"Inserting a '{inserted.ShortTypeName}' into f-text", location: exprPart.Location);
                }
            }
        }

        return _registry.LookupType(name: "Text") ?? ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Validates that an f-text format specifier is one of the allowed values.
    /// Valid: null (none), "=", "?", "=?". Invalid: "?=" (wrong order), anything else.
    /// </summary>
    private void ValidateFTextFormatSpec(string? formatSpec, SourceLocation location)
    {
        if (formatSpec is null or "=" or "?" or "=?")
        {
            return;
        }

        if (formatSpec == "?=")
        {
            ReportError(code: SemanticDiagnosticCode.InvalidFTextFormatSpec,
                message:
                "Invalid f-text format specifier '?='. The correct order is '=?' (name display first, then diagnose).",
                location: location);
            return;
        }

        ReportError(code: SemanticDiagnosticCode.InvalidFTextFormatSpec,
            message:
            $"Invalid f-text format specifier '{formatSpec}'. F-text only supports '=' (name display), '?' (diagnose), and '=?' (combined).",
            location: location);
    }

    /// <summary>
    /// Substitutes type parameters in a type based on a generic resolution.
    /// For example, if genericType is List&lt;S32&gt; and type is T, returns S32.
    /// </summary>
    /// <param name="type">The type that may contain type parameters.</param>
    /// <param name="genericType">The resolved generic type providing type argument bindings.</param>
    /// <returns>The substituted type.</returns>
    private TypeSymbol SubstituteTypeParameters(TypeSymbol type, TypeSymbol genericType)
    {
        if (genericType.TypeArguments == null || genericType.TypeArguments.Count == 0)
        {
            return type;
        }

        // Get the generic definition to find type parameter names
        TypeSymbol? genericDef = GetGenericDefinition(resolution: genericType);
        if (genericDef == null)
        {
            return type;
        }

        // Build a mapping from type parameter names to actual types
        List<string>? typeParamNames = genericDef.GenericParameters;
        if (typeParamNames == null || typeParamNames.Count != genericType.TypeArguments.Count)
        {
            return type;
        }

        var substitutions = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < typeParamNames.Count; i++)
        {
            substitutions[key: typeParamNames[index: i]] = genericType.TypeArguments[index: i];
        }

        return SubstituteWithMapping(type: type, substitutions: substitutions);
    }

    /// <summary>
    /// Gets the generic definition from a resolution.
    /// </summary>
    private TypeSymbol? GetGenericDefinition(TypeSymbol resolution)
    {
        if (!resolution.IsGenericResolution)
        {
            return null;
        }

        // Extract base name (e.g., "List" from "List[S32]")
        string baseName = resolution.BareName;
        TypeSymbol? def = _registry.LookupType(name: baseName);
        // Try slash-qualified module path lookup for non-System types (e.g., "Collections/RotatableList")
        if (def == null && !string.IsNullOrEmpty(value: resolution.Module))
        {
            def = _registry.LookupType(name: $"{resolution.Module}.{baseName}");
        }

        return def;
    }

    /// <summary>
    /// Substitutes type parameters using a mapping. Dispatches to type-specific helpers so each
    /// structural case (projection, generic resolution, routine type, tuple) remains focused.
    /// </summary>
    private TypeSymbol SubstituteWithMapping(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitutions)
    {
        // Associated-type projection (`S/Iter`): substitute the base, then resolve via its binding.
        if (type is AssociatedProjectionTypeSymbol proj)
        {
            return SubstituteProjection(proj: proj, substitutions: substitutions);
        }

        // Direct type parameter replacement (covers ProtocolSelf via its Name "Me").
        if (substitutions.TryGetValue(key: type.Name, value: out TypeSymbol? replacement))
        {
            return replacement;
        }

        // For generic resolutions, recursively substitute in type arguments.
        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            TypeSymbol? resolved =
                SubstituteGenericResolution(type: type, substitutions: substitutions);
            if (resolved != null)
            {
                return resolved;
            }
        }

        // Routine types: substitute inside parameter and return types.
        if (type is RoutineTypeSymbol routineType)
        {
            TypeSymbol? resolved =
                SubstituteRoutineType(routineType: routineType, substitutions: substitutions);
            if (resolved != null)
            {
                return resolved;
            }
        }

        // Tuple types: substitute inside element types.
        if (type is TupleTypeSymbol tupleType)
        {
            TypeSymbol? resolved =
                SubstituteTupleType(tupleType: tupleType, substitutions: substitutions);
            if (resolved != null)
            {
                return resolved;
            }
        }

        return type;
    }

    /// <summary>
    /// Substitutes through an <see cref="AssociatedProjectionTypeSymbol"/>: substitutes the base,
    /// then resolves the associated binding if one is now available, or rebuilds the projection.
    /// Done at the call site so a member-routine return like <c>?EnumerateEmitter[T, S/Iter]</c>
    /// resolves to the CONCRETE emitter — otherwise reachability marks the unresolved-projection
    /// emitter's member routines and the concrete ones never generate.
    /// </summary>
    private TypeSymbol SubstituteProjection(AssociatedProjectionTypeSymbol proj,
        Dictionary<string, TypeSymbol> substitutions)
    {
        TypeSymbol newBase = SubstituteWithMapping(type: proj.Base, substitutions: substitutions);
        TypeSymbol? bound = RecordTypeSymbol.ProjectAssociatedBinding(baseType: newBase,
            slot: proj.SlotName);
        if (bound != null)
        {
            return SubstituteWithMapping(type: bound, substitutions: substitutions);
        }

        return ReferenceEquals(objA: newBase, objB: proj.Base)
            ? proj
            : new AssociatedProjectionTypeSymbol(baseType: newBase, slotName: proj.SlotName);
    }

    /// <summary>
    /// Substitutes into the type arguments of a generic resolution. Returns the new resolution
    /// when any argument changed, or null when no substitution was needed.
    /// </summary>
    private TypeSymbol? SubstituteGenericResolution(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitutions)
    {
        var substitutedArgs = new List<TypeSymbol>();
        bool anyChanged = false;
        foreach (TypeSymbol arg in type.TypeArguments!)
        {
            TypeSymbol substitutedArg =
                SubstituteWithMapping(type: arg, substitutions: substitutions);
            substitutedArgs.Add(item: substitutedArg);
            if (!ReferenceEquals(objA: substitutedArg, objB: arg))
            {
                anyChanged = true;
            }
        }

        if (!anyChanged)
        {
            return null;
        }

        TypeSymbol? baseDef = GetGenericDefinition(resolution: type);
        return baseDef != null
            ? _registry.GetOrCreateResolution(genericDef: baseDef, typeArguments: substitutedArgs)
            : null;
    }

    /// <summary>
    /// Substitutes inside a routine type's parameter and return types so
    /// <c>Callable[(T, T), Bool]</c> becomes <c>Callable[(S64, S64), Bool]</c> for
    /// target-typing lambda arguments. Returns the new routine type when any slot changed,
    /// or null when no substitution was needed.
    /// </summary>
    private RoutineTypeSymbol? SubstituteRoutineType(RoutineTypeSymbol routineType,
        Dictionary<string, TypeSymbol> substitutions)
    {
        var newParams = new List<TypeSymbol>(capacity: routineType.ParameterTypes.Count);
        bool anyChanged = false;
        foreach (TypeSymbol p in routineType.ParameterTypes)
        {
            TypeSymbol substituted = SubstituteWithMapping(type: p, substitutions: substitutions);
            newParams.Add(item: substituted);
            if (!ReferenceEquals(objA: substituted, objB: p))
            {
                anyChanged = true;
            }
        }

        TypeSymbol? newReturn = routineType.ReturnType;
        if (newReturn != null)
        {
            TypeSymbol substitutedRet =
                SubstituteWithMapping(type: newReturn, substitutions: substitutions);
            if (!ReferenceEquals(objA: substitutedRet, objB: newReturn))
            {
                newReturn = substitutedRet;
                anyChanged = true;
            }
        }

        return anyChanged
            ? _registry.GetOrCreateRoutineType(parameterTypes: newParams,
                returnType: newReturn,
                isFailable: routineType.IsFailable)
            : null;
    }

    /// <summary>
    /// Substitutes inside a tuple type's element types. Returns the new tuple type when any
    /// element changed, or null when no substitution was needed.
    /// </summary>
    private TupleTypeSymbol? SubstituteTupleType(TupleTypeSymbol tupleType,
        Dictionary<string, TypeSymbol> substitutions)
    {
        var newElems = new List<TypeSymbol>(capacity: tupleType.ElementTypes.Count);
        bool anyChanged = false;
        foreach (TypeSymbol el in tupleType.ElementTypes)
        {
            TypeSymbol substituted = SubstituteWithMapping(type: el, substitutions: substitutions);
            newElems.Add(item: substituted);
            if (!ReferenceEquals(objA: substituted, objB: el))
            {
                anyChanged = true;
            }
        }

        return anyChanged
            ? _registry.GetOrCreateTupleType(elementTypes: newElems)
            : null;
    }

    /// <summary>
    /// Analyzes a steal expression (RazorForge only).
    /// Validates that the operand can be stolen and returns the stolen type.
    /// </summary>
    /// <param name="steal">The steal expression to analyze.</param>
    /// <returns>The type of the stolen value.</returns>
    /// <remarks>
    /// Stealable types:
    /// - Raw entities (direct entity references)
    ///
    /// Non-stealable types (build error):
    /// - Viewing[T]    (read-only wrapper, scope-bound)
    /// - Modifying[T]  (exclusive wrapper, scope-bound)
    /// - Retained[T]  (shared-ownership wrapper)
    /// - Tracked[T]   (reference-counted wrapper)
    /// - Guarded[T, P] (shared-ownership wrapper)
    /// - Witnessed[T, P] (reference-counted wrapper)
    /// - Hijacked[T]  (internal ownership wrapper)
    /// </remarks>
    private TypeSymbol AnalyzeStealExpression(StealExpression steal)
    {
        // Steal has side effects (deadref marking) and emits diagnostics. Overload-resolution
        // pre-passes can re-analyze argument expressions, which would re-emit errors and
        // double-process the steal. Cache the resolved type on the node so repeated analysis
        // of the same StealExpression is a no-op.
        if (steal.ResolvedType != null)
        {
            return steal.ResolvedType;
        }

        // Analyze the operand
        TypeSymbol operandType = AnalyzeExpression(expression: steal.Operand);

        // The move out of a place a `steal place as old` statement makes: the statement fills the place again.
        if (steal.IsPlaceMove)
        {
            steal.ResolvedType = operandType;
            return operandType;
        }

        // A builder-written move (an argument moved into a Work recipe) moves an entity and leaves any
        // other value, wrappers included, as it is.
        if (steal.IsImplicitMove && !IsRawEntityType(type: operandType))
        {
            steal.ResolvedType = operandType;
            return operandType;
        }

        // A global lives as long as the program and every job reaches it, so nothing takes it away: `steal G` would
        // leave the global empty for every later read, in this routine and in any other.
        if (steal.Operand is IdentifierExpression { Name: var stolenName } &&
            _registry.LookupVariable(name: stolenName) is { IsGlobal: true })
        {
            ReportError(code: SemanticDiagnosticCode.InvalidDropTarget,
                message:
                $"You steal the global '{stolenName}', but a global lives as long as the program and is only read: " +
                $"every later read of '{stolenName}', here or in another routine, would find it gone. Pass " +
                $"'{stolenName}' where it is read (a `Viewing` parameter), or make a value of your own from it.",
                location: steal.Location);
            steal.ResolvedType = operandType;
            return operandType;
        }

        // Aggregate-steal = hole: you cannot move a value OUT of an aggregate's MIDDLE. `steal l[i]`
        // leaves a dense List with no empty-slot state; `steal o.field` leaves a dangling struct hole.
        // Neither copy (two owners) nor steal is sound there — only a REPAIRING removal (`remove_at`,
        // which shifts and adjusts the count) or a shared handle extracts safely. A standalone
        // `steal <identifier>` (deadref'd, no hole) and `steal x.duplicate()` (a fresh owned rvalue
        // from a call — the operand is a CallExpression, not an aggregate-part read) stay legal. Tuple
        // element access (`_t.item0` from a `var (a, b) = …` destructure) is a consumed-temporary MOVE,
        // not an aggregate-middle steal, so it is excluded (Object is a TupleTypeSymbol).
        bool isAggregatePart = steal.Operand switch
        {
            IndexExpression => true,
            MemberExpression m => m.Object.ResolvedType is not TupleTypeSymbol,
            _ => false
        };
        if (isAggregatePart)
        {
            ReportError(code: SemanticDiagnosticCode.StealAggregatePart,
                message:
                "You are trying to steal a value out of the middle of an aggregate, which " +
                "would leave a hole where it used to sit. Take it out with a repairing " +
                "removal (e.g. 'remove_at', which closes the gap), or keep a shareable " +
                "handle instead.",
                location: steal.Location);
            steal.ResolvedType = operandType;
            return operandType;
        }

        // `steal handle` on a Retained or Guarded takes the entity out: the handle ends, and it must be the last strong
        // one (checked when it runs).
        if (operandType.BareName is Declaration.RuntimeContract.Retained or Declaration.RuntimeContract.Guarded &&
            operandType.TypeArguments is [{ } held, ..] &&
            _registry.LookupMemberRoutine(type: operandType, memberRoutineName: "release_inner") is { } release)
        {
            if (steal.Operand is IdentifierExpression handleId)
            {
                _deadrefVariables.Add(item: handleId.Name);
                _everStolenVariables.Add(item: handleId.Name);
                _stealSites[key: handleId.Name] = steal;
            }

            steal.TakesInnerThrough = release;
            steal.IsInFlight = true;
            steal.ResolvedType = held;
            return held;
        }

        // Check the scope-bound / internal / shared-ownership wrapper rejections. Each reports its own
        // diagnostic; on any hit, analysis stops with the operand type.
        if (TryRejectUnstealableWrapper(steal: steal, operandType: operandType))
        {
            steal.ResolvedType = operandType;
            return operandType;
        }

        // `steal` on a record is a no-op — records are value-typed and have no
        // ownership to transfer. Returning the operand type as-is lets stdlib
        // patterns like `return steal result` keep working when `result` is a
        // record (e.g. Text after the refcounted-record migration).
        bool isRecord = operandType is RecordTypeSymbol;

        // A value of a type parameter may be an entity, so a generic body moves one with `steal` (a no-op for a
        // value instantiation, a move for an entity one).
        bool isParameter = operandType is GenericParameterTypeSymbol;

        // The operand already reported its own error (a second `steal` of the same variable, say).
        if (operandType is ErrorTypeSymbol)
        {
            steal.ResolvedType = operandType;
            return operandType;
        }

        if (!isRecord && !isParameter && !IsRawEntityType(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.StealScopeBoundToken,
                message:
                $"Cannot steal '{operandType.Name}': only an entity, a value, or a value of a type parameter can be " +
                "handed over with 'steal'.",
                location: steal.Location);
            steal.ResolvedType = operandType;
            return operandType;
        }

        // #11: Deadref tracking — mark the stolen variable as invalidated
        if (steal.Operand is IdentifierExpression stolenId)
        {
            _deadrefVariables.Add(item: stolenId.Name);
            _everStolenVariables.Add(item: stolenId.Name);
            _stealSites[key: stolenId.Name] = steal;
        }

        // `steal` produces an rvalue of the operand's type: it consumes a binding and yields an in-flight entity
        // that must be bound again (or consumed) at the use site.
        steal.IsInFlight = true;

        steal.ResolvedType = operandType;
        return operandType;
    }

    /// <summary>
    /// Rejects a <c>steal</c> whose operand is an unstealable wrapper: a scope-bound token
    /// (Viewing/Modifying), the internal <c>Hijacked[T]</c>, or a weak handle (Tracked/Witnessed, which keeps no
    /// entity to take out). Reports the matching diagnostic and
    /// returns true on any hit; false when the operand is not one of these wrappers.
    /// </summary>
    private bool TryRejectUnstealableWrapper(StealExpression steal, TypeSymbol operandType)
    {
        // Check if the type is a scope-bound wrapper (cannot be stolen)
        if (IsMemoryToken(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.StealScopeBoundToken,
                message: $"You steal a token (`{operandType.ShortTypeName}`) here, but a token keeps nothing to hand " +
                         "over: it only points at the place the entity is in. Steal the entity itself, or pass the " +
                         "token as it is.",
                location: steal.Location);
            return true;
        }

        // Check for Hijacked[T] (internal ownership, not for user code)
        if (IsHijacked(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.StealHijacked,
                message: "You can't steal a 'Hijacked[T]': it is a raw pointer that keeps nothing, so there is nothing to " +
                         "hand over. Pass the pointer itself.",
                location: steal.Location);
            return true;
        }

        // Check for a single-threaded reference-counted handle (Retained/Tracked). These are SHARED
        // ownership, not unique — multiple handles to the same non-atomic control block can coexist,
        // so `steal` (an exclusive-transfer marker) is a category error: moving one handle proves
        // nothing about the others. Clone with `.retain()`/`.track()`, or convert to `Guarded`/
        // `Witnessed` (atomic Arc) to move ownership across a coroutine/thread boundary.
        if (operandType.BareName is Declaration.RuntimeContract.Tracked or Declaration.RuntimeContract.Witnessed)
        {
            ReportError(code: SemanticDiagnosticCode.StealSharedOwnership,
                message:
                $"Cannot steal '{operandType.Name}': a weak handle doesn't keep the entity, so there is nothing to " +
                "take out of it. 'steal' on the strong handle (Retained or Guarded) takes the entity out when that " +
                "handle is the last one.",
                location: steal.Location);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a type is a scope-bound wrapper (Viewing, Modifying).
    /// Scope-bound wrappers cannot be stolen.
    /// </summary>
    private static bool IsMemoryToken(TypeSymbol type)
    {
        return IsInlineOnlyTokenType(type: type);
    }

    /// <summary>
    /// Checks if a type is Hijacked[T] (internal ownership type).
    /// </summary>
    private static bool IsHijacked(TypeSymbol type)
    {
        return type.Name == Declaration.RuntimeContract.Hijacked;
    }

    /// <summary>
    /// Analyzes a backindex expression (^n = index from end).
    /// Validates that the operand is a non-negative integer type.
    /// </summary>
    /// <param name="back">The back index expression to analyze.</param>
    /// <returns>The BackIndex type.</returns>
    /// <remarks>
    /// BackIndex expressions create indices that count from the end of a sequence:
    /// - ^1 = last element
    /// - ^2 = second to last element
    /// - ^0 = one past the end (valid for slicing, not indexing)
    ///
    /// Used with IndexExpression for end-relative indexing: list[^1], text[^3]
    /// </remarks>
    private TypeSymbol AnalyzeBackIndexExpression(BackIndexExpression back, TypeSymbol? expectedType)
    {
        // `^n` is pure sugar for the forward position `count - n`; it carries no runtime type of its
        // own. The offset is a position of the type the subscript takes (`U64`, or `Integer` for a Suflae
        // collection), and so is the expression (its lowered value). OperatorLoweringPass rewrites the
        // enclosing subscript/slice to the free routine `back_resolve(count:, offset:)`; the
        // `BackIndexExpression` node is the only signal it needs.
        TypeSymbol? u64Type = expectedType is { } position && IsIntegerType(type: position)
            ? position
            : _registry.LookupType(name: "U64");
        TypeSymbol operandType =
            AnalyzeExpression(expression: back.Operand, expectedType: u64Type);

        if (!IsIntegerType(type: operandType))
        {
            ReportError(code: SemanticDiagnosticCode.BackIndexRequiresInteger,
                message:
                $"BackIndex operator '^' requires an integer operand, got '{operandType.Name}'.",
                location: back.Location);
        }

        return u64Type ?? operandType;
    }

    /// <summary>
    /// Creates a RoutineTypeSymbol from a RoutineInfo for first-class routine references.
    /// </summary>
    /// <param name="routine">The routine to create a type for.</param>
    /// <returns>The routine type representing this routine's signature.</returns>
    private RoutineTypeSymbol GetRoutineType(RoutineInfo routine)
    {
        // Extract parameter types from ParamInfo
        var parameterTypes = routine.Parameters
                                    .Select(selector: p => p.Type)
                                    .ToList();

        // Get return type (null means None/void)
        TypeSymbol? returnType = routine.ReturnType;

        // Create or retrieve the cached routine type
        return _registry.GetOrCreateRoutineType(parameterTypes: parameterTypes,
            returnType: returnType,
            isFailable: routine.IsFailable);
    }
}
