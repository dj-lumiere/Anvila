using System.Text.RegularExpressions;
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
    private const string GetItemMemberRoutineName = "getitem";

    /// <summary>
    /// The type whose <c>getitem</c>/<c>setitem</c> an index on <paramref name="type"/> uses: the inner value of a
    /// transparent protocol, and the entity behind a Suflae entity handle (<c>h.kids[0]</c> with <c>kids</c> a
    /// <c>Roamed[List[Int]]</c> field indexes the list, as a member call on the handle calls the list's routine).
    /// </summary>
    private TypeSymbol IndexLookupType(TypeSymbol type)
    {
        // An iterator's `Item` in a generic body reads as its element type for indexing, as it does for member
        // access (`each part in parts` over `Iterable[Text]`, then `part[i]`).
        TryUnwrapMarkerReceiver(type: type, innerType: out TypeSymbol target);
        return target is RecordTypeSymbol
        {
            GenericDefinition.Name: Declaration.RuntimeContract.Roamed, TypeArguments: [EntityTypeSymbol entity]
        }
            ? entity
            : target;
    }

    /// <summary>
    /// Unwraps an iterator's `Item` in a generic body to the value it reads as, for member access.
    /// </summary>
    private bool TryUnwrapMarkerReceiver(TypeSymbol type, out TypeSymbol innerType)
    {
        if (ProjectionReadTarget(type: type) is { } read)
        {
            innerType = read;
            return true;
        }

        innerType = type;
        return false;
    }

    /// <summary>
    /// The value an iterator's `Item` reads as: `Emittable[T]`'s `Item` is a token on a stored element or the value
    /// itself, and reads as `T` either way, so `S/Iter/Item` in a generic body reads as `T`. Null for anything
    /// else. Monomorphization makes the projection the iterator's concrete `Item` (a token, or the value).
    /// </summary>
    private TypeSymbol? ProjectionReadTarget(TypeSymbol type)
    {
        return type is AssociatedProjectionTypeSymbol { SlotName: EmittableItemSlot } projection &&
               ProjectionOwner(projection: projection) is { TypeArguments: [{ } read] } owner &&
               (owner.GenericDefinition ?? owner).BareName == EmittableProtocolName
            ? read
            : null;
    }

    /// <summary>The slot an iterator's element is handed out through.</summary>
    private const string EmittableItemSlot = "Item";

    /// <summary>The protocol an iterator obeys.</summary>
    private const string EmittableProtocolName = "Emitter";

    /// <summary>
    /// Analyzes a buildtime splice-selector member access (<c>x.${m.name}</c>). The receiver and
    /// the selector splice are analyzed for real (surfacing mistakes in either), but the selected
    /// field's concrete type is unknown until monomorphization, so this defers to
    /// <see cref="ErrorTypeSymbol"/> — the same cascade-suppressing deferral used for unresolved
    /// generic-body expressions. The real "does this field have that member?" check runs on the
    /// unrolled member access at instantiation.
    /// </summary>
    private ErrorTypeSymbol AnalyzeSpliceMemberExpression(SpliceMemberExpression spliceMember)
    {
        AnalyzeExpression(expression: spliceMember.Object);
        AnalyzeExpression(expression: spliceMember.Selector);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Analyzes a buildtime splice (<c>${expr}</c>). The inner projection is analyzed for real; a
    /// selector-position splice must name a field (fold to <c>Text</c>). The splice's own value is
    /// buildtime-only, so it types as <see cref="ErrorTypeSymbol"/> (deferred to monomorphization).
    /// </summary>
    private ErrorTypeSymbol AnalyzeSpliceExpression(SpliceExpression splice)
    {
        TypeSymbol innerType = AnalyzeExpression(expression: splice.Inner);
        if (splice.RequiredKind == SpliceKind.Selector && innerType is not ErrorTypeSymbol &&
            innerType.Name != "Text")
        {
            ReportError(code: SemanticDiagnosticCode.MemberNotFound,
                message:
                "A member-selector splice 'x.${...}' must name a field: its inner expression has to be a Text name (e.g. 'm.name').",
                location: splice.Location);
        }

        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Rejects a member access off a buildtime <c>expand</c> handle (<c>m.name</c> etc.). The handle is
    /// a sentinel with no member projections — its metadata is read only through the function-form
    /// intrinsics (<c>nameof(m)</c>/<c>orderof(m)</c>/<c>typeof(m)</c>/…), so <c>m.&lt;anything&gt;</c>
    /// is always a mistake.
    /// </summary>
    private TypeSymbol AnalyzeBuildtimeHandleProjection(MemberExpression member)
    {
        ReportError(code: SemanticDiagnosticCode.MemberNotFound,
            message:
            $"A buildtime expand handle has no member '{member.MemberName}'. Read its metadata with " +
            "an intrinsic instead: nameof(m), orderof(m), typeof(m), typeidof(m), valueof(m), " +
            "placeof(m), sizeof(m), or visibilityof(m).",
            location: member.Location);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Reports the Suflae RF-S nullable-entity-dereference error for a member access on a possibly-none
    /// entity receiver, tailoring the receiver description and hint to the receiver expression shape.
    /// </summary>
    private void ReportNullableEntityDeref(MemberExpression member)
    {
        string receiver = member.Object switch
        {
            IdentifierExpression idRecv => $"'{idRecv.Name}'",
            MemberExpression mRecv => $"'{mRecv.MemberName}'",
            _ => "the value"
        };
        string hint = member.Object is IdentifierExpression idHint
            ? $"Null-check it first (e.g. 'if {idHint.Name} isnot None' or 'if {idHint.Name} is None: return')."
            : "Bind it to a local and null-check that local first (e.g. 'var v = …' then 'if v isnot None').";
        ReportError(code: SemanticDiagnosticCode.NullableEntityDeref,
            message:
            $"Cannot access member '{member.MemberName}' on possibly-none entity {receiver}. {hint}",
            location: member.Location);
    }

    private TypeSymbol AnalyzeMemberExpression(MemberExpression member)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

        // A buildtime `expand` handle has no member projections — its metadata is read via the
        // function-form intrinsics (`nameof(m)`/`orderof(m)`/…), so `m.<anything>` is a mistake.
        if (objectType is BuildtimeHandleTypeSymbol)
        {
            return AnalyzeBuildtimeHandleProjection(member: member);
        }

        // The receiver already failed to resolve (its own error was reported). A follow-on
        // "Type '<error>' does not have a member ..." is pure cascade noise — bail quietly.
        if (objectType is ErrorTypeSymbol)
        {
            return ErrorTypeSymbol.Instance;
        }

        // Suflae flow typing: dereferencing (member access / memberRoutine call) a possibly-none entity
        // reference is rejected until it has been null-checked. Covers both a nullable local/param
        // (`x.field` on an unchecked `x: E?`) and a nullable field-chain (`a.b.c` where `b: E?`) — a
        // field read is never flow-narrowed (Kotlin doesn't smart-cast mutable fields either), so it
        // must always be bound to a local and checked there.
        if (_registry.EntitiesAreShared && SharedEntities.IsNullableRead(expr: member.Object))
        {
            ReportNullableEntityDeref(member: member);
        }

        TryUnwrapMarkerReceiver(type: objectType, innerType: out TypeSymbol lookupType);

        // Look up the member variable/property on the type
        if (TryResolveMemberVariableAccess(lookupType: lookupType, member: member) is { } resolved)
        {
            return resolved;
        }

        // Choice case member access: Color.RED -> ChoiceTypeSymbol
        if (lookupType is ChoiceTypeSymbol choice)
        {
            ChoiceCaseInfo? caseInfo =
                choice.Cases.FirstOrDefault(predicate: c => c.Name == member.MemberName);
            if (caseInfo != null)
            {
                return choice; // Color.RED has type Color
            }

            // Fall through to memberRoutine lookup — choice types can have memberRoutines
        }

        // Flags member access: Permissions.READ -> FlagsTypeSymbol
        if (lookupType is FlagsTypeSymbol flags)
        {
            FlagsMemberInfo? memberInfo =
                flags.Members.FirstOrDefault(predicate: m => m.Name == member.MemberName);
            if (memberInfo != null)
            {
                return flags; // Permissions.READ has type Permissions
            }

            // Fall through to memberRoutine lookup — flags types can have builder service memberRoutines
        }

        // Could be a member-routine reference - use LookupMemberRoutine which handles generic resolutions.
        // MemberName is always bare; failability is carried structurally in member.IsFailable.
        string lookupName = member.MemberName;
        RoutineInfo? memberRoutine =
            _registry.LookupMemberRoutine(type: lookupType, memberRoutineName: lookupName);
        if (memberRoutine != null)
        {
            // A BARE member access (`x.name`, no `()`) reads a member VARIABLE — it must not silently
            // invoke a zero-arg memberRoutine. (memberRoutine calls `x.name()` are resolved in AnalyzeCallExpression,
            // which never routes through here.) Auto-calling masked real bugs: e.g. after a record drops
            // a `sign` field but keeps a `sign()` accessor, `var s = w.sign` typechecked as the call
            // result here, so validate-stdlib passed code that only failed at codegen
            // ("Member variable 'sign' not found"). Keep `.name` (field) and `.name()` (call) distinct.
            ReportError(code: SemanticDiagnosticCode.MemberNotFound,
                message:
                $"'{lookupName}' is a member routine on '{objectType.Name}', not a member variable. " +
                $"Bare `.{lookupName}` reads a member variable; call the member routine as `{member.MemberName}()`.",
                location: member.Location);
            return ErrorTypeSymbol.Instance;
        }

        // For-loop destructuring lowering produces item0, item1, ... accesses on the element type.
        // Currently only Tuple[...] supports destructuring. Record breakdown is planned for the future.
        // When the element type is not a tuple, this means the user wrote `for (a, b) in non_tuple`.
        if (lookupType is not TupleTypeSymbol && TupleDestructureFieldRegex()
               .IsMatch(input: member.MemberName))
        {
            ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                message:
                $"Cannot destructure: type '{objectType.Name}' is not a tuple and does not support tuple destructuring.",
                location: member.Location);
        }
        else
        {
            ReportError(code: SemanticDiagnosticCode.MemberNotFound,
                message:
                $"Type '{objectType.Name}' does not have a member '{member.MemberName}'.{Suggestions.ForMember(type: lookupType, name: member.MemberName)}",
                location: member.Location);
        }

        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Attempts to resolve a bare member-variable access on the lookup type: checks record, tuple, entity,
    /// crashable, and wrapper types in priority order. Returns the member's declared type when found (after
    /// access validation), or <c>null</c> when the member is not a field on any of those types.
    /// </summary>
    private TypeSymbol? TryResolveMemberVariableAccess(TypeSymbol lookupType,
        MemberExpression member)
    {
        if (lookupType is RecordTypeSymbol record)
        {
            MemberVariableInfo? memberVariable =
                record.LookupMemberVariable(memberVariableName: member.MemberName);
            if (memberVariable != null)
            {
                ValidateMemberVariableAccess(memberVariable: memberVariable,
                    isWrite: false,
                    accessLocation: member.Location);
                return memberVariable.Type;
            }

            if (Wrappers.IsWrapperType(type: lookupType) &&
                TryForwardWrapperMemberAccess(lookupType: lookupType, member: member) is { } fwd)
            {
                return fwd;
            }
        }
        else if (lookupType is TupleTypeSymbol tupleType)
        {
            return tupleType.GetField(memberVariableName: member.MemberName)
                           ?.Type;
        }
        else if (lookupType is EntityTypeSymbol entity)
        {
            MemberVariableInfo? memberVariable =
                entity.LookupMemberVariable(memberVariableName: member.MemberName);
            if (memberVariable != null)
            {
                ValidateMemberVariableAccess(memberVariable: memberVariable,
                    isWrite: false,
                    accessLocation: member.Location);
                return memberVariable.Type;
            }
        }
        else if (lookupType is CrashableTypeSymbol crashable)
        {
            MemberVariableInfo? memberVariable =
                crashable.LookupMemberVariable(memberVariableName: member.MemberName);
            if (memberVariable != null)
            {
                ValidateMemberVariableAccess(memberVariable: memberVariable,
                    isWrite: false,
                    accessLocation: member.Location);
                return memberVariable.Type;
            }
        }
        else if (Wrappers.IsWrapperType(type: lookupType) &&
                 TryForwardWrapperMemberAccess(lookupType: lookupType, member: member) is
                     { } forwarded)
        {
            return forwarded;
        }

        return null;
    }

    /// <summary>
    /// Forwards a read member access on a wrapper type (Viewing[T], Modifying[T], Guarded[T], …) to its
    /// inner type — first as a member variable, then as a member routine via a Phase-D synthesized
    /// forwarder (or a directly-registered routine). Returns the resolved access type, or <c>null</c>
    /// when neither is found (the caller continues with its normal lookup / error path).
    /// </summary>
    private TypeSymbol? TryForwardWrapperMemberAccess(TypeSymbol lookupType,
        MemberExpression member)
    {
        if (Wrappers.IsLockedGuarded(type: lookupType) && !_isInCompilerGeneratedBody &&
            Wrappers.LookupMemberVariableOnWrapperInnerType(wrapperType: lookupType,
                memberVariableName: member.MemberName) != null)
        {
            ReportGuardedReachedWithoutLock(wrapperType: lookupType, memberName: member.MemberName,
                receiver: member.Object, location: member.Location);
            return ErrorTypeSymbol.Instance;
        }

        // Try to forward member variable access to the inner type
        MemberVariableInfo? innerMemberVariable =
            Wrappers.LookupMemberVariableOnWrapperInnerType(wrapperType: lookupType,
                memberVariableName: member.MemberName);
        if (innerMemberVariable != null)
        {
            // Validate member variable access on the inner type
            ValidateMemberVariableAccess(memberVariable: innerMemberVariable,
                isWrite: false,
                accessLocation: member.Location);
            return innerMemberVariable.Type;
        }

        // Try to forward memberRoutine access to the inner type via Phase D synthesized forwarders
        RoutineInfo? innerMemberRoutine =
            TrySynthesizeWrapperForwarder(wrapperType: lookupType,
                memberRoutineName: member.MemberName,
                isFailable: false) ?? _registry.LookupMemberRoutine(type: lookupType,
                memberRoutineName: member.MemberName);
        if (innerMemberRoutine != null)
        {
            // Validate read-only wrapper restrictions
            ValidateReadOnlyWrapperMemberRoutineAccess(wrapperType: lookupType,
                memberRoutine: innerMemberRoutine,
                location: member.Location,
                receiver: member.Object);
            // Validate memberRoutine access
            ValidateRoutineAccess(routine: innerMemberRoutine, accessLocation: member.Location);
            // Return type is None if not specified
            return innerMemberRoutine.ReturnType ??
                   _registry.LookupType(name: "None") ?? ErrorTypeSymbol.Instance;
        }

        return null;
    }

    /// <summary>
    /// Resolves the index parameter type of a `getitem` routine for a given lookup type, with
    /// owner generic parameters substituted. Returns null when the routine or parameter is missing.
    /// `me` is implicit and not in <see cref="RoutineInfo.Parameters"/>; the index is at index 0.
    /// </summary>
    private TypeSymbol? ResolveIndexParameterType(RoutineInfo? getItem, TypeSymbol lookupType)
    {
        if (getItem is not { Parameters.Count: >= 1 })
        {
            return null;
        }

        TypeSymbol paramType = getItem.Parameters[index: 0].Type;
        paramType = SubstituteOwnerGenerics(paramType: paramType,
            lookupType: lookupType,
            ownerType: getItem.OwnerType) ?? paramType;

        // The index param is often a token (`Dict.getitem!(key: Viewing[K])`) the call makes from a bare `K`.
        // Unwrap it to the key type so a bare integer key literal (`d[1]`) conforms to e.g. S64 instead of
        // stalling at the Suflae `Int` default.
        if (paramType.TypeArguments is { Count: >= 1 } referArgs &&
            GetTypeBaseName(type: paramType) is Declaration.RuntimeContract.Viewing
                or Declaration.RuntimeContract.Modifying)
        {
            paramType = referArgs[index: 0];
        }

        return paramType;
    }

    private TypeSymbol? SubstituteOwnerGenerics(TypeSymbol paramType, TypeSymbol lookupType,
        TypeSymbol? ownerType)
    {
        if (lookupType.TypeArguments is not { Count: > 0 })
        {
            return paramType;
        }

        TypeSymbol? lookupGenericDef = GetGenericDefinition(resolution: lookupType);
        List<string>? ownerGenericParams =
            lookupGenericDef?.GenericParameters ?? ownerType?.GenericParameters;
        if (ownerGenericParams is not { Count: > 0 })
        {
            return paramType;
        }

        var substitutions = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < ownerGenericParams.Count && i < lookupType.TypeArguments.Count; i++)
        {
            substitutions[key: ownerGenericParams[index: i]] = lookupType.TypeArguments[index: i];
        }

        return substitutions.Count > 0
            ? SubstituteWithMapping(type: paramType, substitutions: substitutions)
            : paramType;
    }

    /// <summary>
    /// The types a collection index may have, most preferred first: <c>U64</c>, and before it <c>Int</c> when
    /// the build's language counts with arbitrary-precision integers (Suflae's own collections take an
    /// <c>Int</c> index; one from RazorForge still takes a <c>U64</c>).
    /// </summary>
    private List<TypeSymbol> IndexTypes()
    {
        var types = new List<TypeSymbol>();
        if (_registry.CompilationRules.DefaultsToArbitraryPrecision &&
            _registry.LookupType(name: "System.Int") is { } integer)
        {
            types.Add(item: integer);
        }

        if (_registry.LookupType(name: "U64") is { } u64)
        {
            types.Add(item: u64);
        }

        return types;
    }

    /// <summary>The <c>Range</c> over an index type: the type a slice (<c>s[a til b]</c>) takes.</summary>
    private TypeSymbol? MakeRangeType(TypeSymbol indexType)
    {
        return _registry.LookupType(name: "Range") is { } rangeDef
            ? _registry.GetOrCreateResolution(genericDef: rangeDef, typeArguments: [indexType])
            : null;
    }

    /// <summary>
    /// Type-as-value generic instantiation: when the index object is a bare type name (no shadowing
    /// variable) referring to a generic type, reinterprets the brackets as generic-arg syntax and returns
    /// the resolved type (e.g. <c>NumericSumAdd[T]</c>). Returns <c>null</c> when this is not a
    /// type-instantiation index (the caller falls through to ordinary <c>getitem</c> resolution).
    /// </summary>
    private TypeSymbol? TryAnalyzeTypeAsValueInstantiation(IndexExpression index)
    {
        if (index.Object is not IdentifierExpression typeRefId ||
            _registry.LookupVariable(name: typeRefId.Name) != null ||
            LookupTypeWithImports(name: typeRefId.Name) is not
                { GenericParameters.Count: > 0 } typeRef)
        {
            return null;
        }

        var typeArgs = new List<TypeSymbol>();
        List<Expression> argExprs = index.Index is TupleLiteralExpression tup
            ? tup.Elements
            : [index.Index];
        foreach (Expression argExpr in argExprs)
        {
            TypeSymbol argType = argExpr switch
            {
                IdentifierExpression argId when IsGenericParameter(name: argId.Name) =>
                    new GenericParameterTypeSymbol(name: argId.Name),
                IdentifierExpression argId when LookupTypeWithImports(name: argId.Name) is { } t
                    => t,
                // A number or `true`/`false` (`BitArray[3].blank()`) is a const-generic argument, the value
                // itself, as it is in a type written out (`BitArray[3]`), never the type of the literal.
                LiteralExpression
                {
                    LiteralType: TokenType.UndecidedInteger or TokenType.True or TokenType.False
                } literal => ResolveType(typeExpr: new TypeExpression(Name: literal.LiteralType switch
                    {
                        TokenType.True => "true",
                        TokenType.False => "false",
                        _ => literal.Value?.ToString() ?? ""
                    },
                    GenericArguments: null,
                    Location: literal.Location)),
                _ => AnalyzeExpression(expression: argExpr)
            };
            typeArgs.Add(item: argType);
        }

        if (typeArgs.Count == typeRef.GenericParameters.Count)
        {
            return _registry.GetOrCreateResolution(genericDef: typeRef, typeArguments: typeArgs);
        }

        return null;
    }

    /// <summary>
    /// Resolves the <c>getitem</c> member routine for an index expression on <paramref name="lookupType"/>:
    /// a slice (RangeExpression) binds the <c>Range[U64]</c> overload, otherwise the scalar overload, with
    /// a failable fallback and (for wrapper types) a Phase-D synthesized forwarder. May return <c>null</c>.
    /// </summary>
    private RoutineInfo? ResolveIndexGetItem(IndexExpression index, TypeSymbol lookupType)
    {
        // A slice `text[a til b]` — a RangeExpression index — binds to the `getitem(range: Range[...])` overload
        // (returning the sub-collection), NOT the scalar `getitem(index)`; a scalar index `arr[i]` binds the
        // `getitem(index: ...)` overload. Each is resolved by its argument type, trying each index type in turn,
        // so a container carrying both overloads picks the right one unambiguously: a name-only lookup can't
        // tell >1 same-name overload apart (no first-wins). The slice's range is then analyzed with the type
        // the overload takes, so SA's type matches what OperatorLoweringPass lowers to. Single-overload
        // containers (e.g. `Dict.getitem(key: K)`) fall through to the name-only lookup below.
        RoutineInfo? getItem = null;
        bool slice = index.Index is RangeExpression;
        // A wrapper reads through to its inner value's `getitem` overloads: build their forwarders first, so the
        // overload by index type below finds the one this index wants (`line[a til b]` on a `Modifying[Text]`).
        if (Wrappers.IsWrapperType(type: lookupType) && lookupType.BareName != Declaration.RuntimeContract.Roamed &&
            _registry.LookupMemberRoutine(type: lookupType,
                memberRoutineName: GetItemMemberRoutineName) == null)
        {
            _ = TrySynthesizeWrapperForwarder(wrapperType: lookupType,
                memberRoutineName: GetItemMemberRoutineName,
                isFailable: false) ?? TrySynthesizeWrapperForwarder(wrapperType: lookupType,
                memberRoutineName: GetItemMemberRoutineName,
                isFailable: true);
        }

        TypeSymbol? sliceRange = null;
        foreach (TypeSymbol indexType in IndexTypes())
        {
            TypeSymbol? argType = slice
                ? MakeRangeType(indexType: indexType)
                : indexType;
            if (argType == null)
            {
                continue;
            }

            sliceRange ??= argType;
            getItem = _registry.LookupMemberRoutineOverload(type: lookupType,
                memberRoutineName: GetItemMemberRoutineName,
                argTypes: [argType]);
            if (getItem != null)
            {
                sliceRange = argType;
                break;
            }
        }

        if (slice && sliceRange != null)
        {
            AnalyzeExpression(expression: index.Index, expectedType: sliceRange);
        }

        // A slice is a copy of the elements, which an entity element can't be: the range `getitem` is written for
        // copyable elements only (`needs RecordType T`).
        if (slice && getItem != null && lookupType.TypeArguments is { Count: > 0 } &&
            !_registry.OwnerKindGatesAdmit(routine: getItem, owner: lookupType))
        {
            ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
                message: $"A slice of '{lookupType.ShortTypeName}' would be a copy of its elements, and an element of " +
                         $"'{lookupType.TypeArguments[index: 0].ShortTypeName}' is an entity, which is not copied. Walk the " +
                         "part you need instead: 'xs.skip(a).take(n)'.",
                location: index.Location);
            // Reported once: the slice keeps its type, so nothing after it is reported again.
            return getItem;
        }

        // Look for getitem memberRoutine — LookupMemberRoutine handles generic resolutions
        getItem ??= _registry.LookupMemberRoutine(type: lookupType,
            memberRoutineName: GetItemMemberRoutineName);
        // Try failable variant if non-failable not found
        if (getItem == null)
        {
            getItem = _registry.LookupMemberRoutine(type: lookupType,
                memberRoutineName: GetItemMemberRoutineName,
                isFailable: true);
        }

        // Phase D: synthesize a wrapper forwarder if still not found
        if (getItem == null && Wrappers.IsWrapperType(type: lookupType))
        {
            getItem = TrySynthesizeWrapperForwarder(wrapperType: lookupType,
                memberRoutineName: GetItemMemberRoutineName,
                isFailable: false) ?? TrySynthesizeWrapperForwarder(wrapperType: lookupType,
                memberRoutineName: GetItemMemberRoutineName,
                isFailable: true);
        }

        return getItem;
    }

    /// <summary>
    /// Computes the element type an index expression returns, substituting the owner's generic parameters
    /// into the resolved <c>getitem</c> return type when it came from the generic definition. Assumes
    /// <paramref name="getItem"/> has a non-null <see cref="RoutineInfo.ReturnType"/>.
    /// </summary>
    private TypeSymbol ResolveIndexReturnType(RoutineInfo getItem, TypeSymbol lookupType)
    {
        TypeSymbol returnType = getItem.ReturnType!;
        List<string>? ownerGenericParams = null;
        if (lookupType.TypeArguments is { Count: > 0 })
        {
            TypeSymbol? lookupGenericDef = GetGenericDefinition(resolution: lookupType);
            ownerGenericParams = lookupGenericDef?.GenericParameters ??
                                 getItem.OwnerType?.GenericParameters;
        }

        // Only substitute when `getitem` came from the GENERIC DEFINITION (its ReturnType is the
        // bare owner param, e.g. List[T]'s `T`). If it was resolved against the instantiated
        // owner, its ReturnType is ALREADY expressed in the resolution's type arguments —
        // re-substituting would double-apply. That double-application is silent for `List[S64]`
        // (S64 mentions no param) but corrupts `List[Box[T]]`: the owner's formal param name "T"
        // collides with the routine's own "T" inside the element `Box[T]`, yielding a wrongly
        // nested `Box[Box[T]]`. Guard on the owner carrying type arguments (= already resolved).
        bool memberRoutineAlreadyResolved = getItem.OwnerType is { TypeArguments.Count: > 0 };

        if (!memberRoutineAlreadyResolved && lookupType.TypeArguments is { Count: > 0 } &&
            ownerGenericParams is { Count: > 0 })
        {
            var substitutions = new Dictionary<string, TypeSymbol>();
            for (int i = 0;
                 i < ownerGenericParams.Count && i < lookupType.TypeArguments.Count;
                 i++)
            {
                substitutions[key: ownerGenericParams[index: i]] =
                    lookupType.TypeArguments[index: i];
            }

            if (substitutions.Count > 0)
            {
                returnType = SubstituteWithMapping(type: returnType, substitutions: substitutions);
            }
        }

        return returnType;
    }

    /// <summary>
    /// Whether an index of <paramref name="indexType"/> may stand where the accessor takes <paramref name="taken"/>:
    /// an index may be any integer with a count of its own, which OperatorLoweringPass turns into the <c>U64</c>
    /// (<c>to_index</c>) or <c>Int</c> (<c>to_integer_count</c>) the accessor's <c>index</c> parameter takes. A map's
    /// key is no index and keeps its own type.
    /// </summary>
    private bool IsCountedIndex(RoutineInfo? getItem, TypeSymbol indexType, TypeSymbol taken)
    {
        if (getItem?.Parameters.FirstOrDefault(predicate: p => p.Name != "me") is not { Name: "index" } ||
            !IsIntegerType(type: indexType))
        {
            return false;
        }

        return taken.Name switch
        {
            "U64" => HasCountConversion(type: indexType, memberRoutineName: "to_index"),
            "Int" => HasCountConversion(type: indexType, memberRoutineName: "to_integer_count"),
            _ => false
        };
    }

    private TypeSymbol AnalyzeIndexExpression(IndexExpression index)
    {
        // Type-as-value generic instantiation: when the object is a bare type name (no
        // shadowing variable) referring to a generic type, reinterpret the brackets as
        // generic-arg syntax — `NumericSumAdd[T].identity_lazy()` should produce the
        // resolved type `NumericSumAdd[T]`, not run getitem on the gen-def.
        if (TryAnalyzeTypeAsValueInstantiation(index: index) is { } instantiated)
        {
            return instantiated;
        }

        TypeSymbol objectType = AnalyzeExpression(expression: index.Object);
        TypeSymbol lookupType = IndexLookupType(type: objectType);

        RoutineInfo? getItem = ResolveIndexGetItem(index: index, lookupType: lookupType);
        index.ResolvedGetItem = getItem;

        // Analyze the index expression with the indexer parameter type as expected type so
        // untyped integer literals (`arr[0]`) retype to U64/S64/etc. instead of defaulting to S64
        // and tripping S767 fixed-width-mixing diagnostics.
        TypeSymbol? indexExpectedType = ResolveIndexParameterType(getItem: getItem,
            lookupType: lookupType);
        TypeSymbol indexType = AnalyzeExpression(expression: index.Index, expectedType: indexExpectedType);

        // The index is getitem's argument, so it has to fit the parameter like any other: an S64 variable is not
        // a U64 index (variables keep their width; convert it).
        if (indexExpectedType != null && indexType is not ErrorTypeSymbol &&
            !ContainsUnresolvedTypeParameter(type: indexExpectedType) &&
            !ContainsUnresolvedTypeParameter(type: indexType) &&
            !IsAssignableTo(source: indexType, target: indexExpectedType) &&
            !IsCountedIndex(getItem: getItem, indexType: indexType, taken: indexExpectedType))
        {
            ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
                message:
                $"You index '{lookupType.ShortTypeName}' with {Article.For(name: indexType.ShortTypeName)} '{indexType.ShortTypeName}', but its 'getitem' takes a " +
                $"'{indexExpectedType.ShortTypeName}'. Convert the index to '{indexExpectedType.ShortTypeName}'.",
                location: index.Index.Location);
        }

        // Failability propagation: the resolved getitem may be `!` per its protocol contract
        // (e.g. ViewingIndexable.getitem!). A non-failable caller using `arr[i]` must propagate that.
        if (getItem is { IsFailable: true } && _currentRoutine != null)
        {
            _currentRoutine.HasFailableCalls = true;
            _currentRoutine.FailableCallees.Add(item: getItem);
        }

        if (getItem?.ReturnType != null)
        {
            return ResolveIndexReturnType(getItem: getItem, lookupType: lookupType);
        }

        // No `getitem` resolved. If the lookup type is fully concrete (no unresolved generic
        // parameters), it genuinely does not support indexing — report it cleanly here rather
        // than letting `arr[i]` slip through to codegen, which would crash with
        // "reached codegen ... but no resolved member routine". Types that removed their `getitem`
        // (e.g. SortedList, replaced by the named `get_by_rank!`) land here.
        if (getItem == null && !ContainsUnresolvedTypeParameter(type: lookupType))
        {
            ReportError(code: SemanticDiagnosticCode.TypeNotIndexable,
                message:
                $"Type '{lookupType.Name}' does not support indexing with '[]' (no 'getitem' routine).",
                location: index.Location);
            return ErrorTypeSymbol.Instance;
        }

        // For generic types like List<T> whose `getitem` resolves only after monomorphization,
        // return the element type.
        if (lookupType.TypeArguments is { Count: > 0 })
        {
            return lookupType.TypeArguments[index: 0];
        }

        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// True when <paramref name="type"/> is or contains an unresolved generic parameter,
    /// protocol self-type, or error type — cases where memberRoutine resolution may legitimately
    /// complete only after monomorphization, so a missing routine is not yet a hard error.
    /// </summary>
    private static bool ContainsUnresolvedTypeParameter(TypeSymbol type)
    {
        if (type is GenericParameterTypeSymbol or ProtocolSelfTypeSymbol or ErrorTypeSymbol)
        {
            return true;
        }

        return type.TypeArguments is { Count: > 0 } args &&
               args.Any(predicate: ContainsUnresolvedTypeParameter);
    }

    private TypeSymbol AnalyzeConditionalExpression(ConditionalExpression cond,
        TypeSymbol? expectedType = null)
    {
        // #145: Track nesting depth for deep conditional warning
        _conditionalNestingDepth++;
        if (_conditionalNestingDepth > 2)
        {
            ReportWarning(code: SemanticWarningCode.NestedConditionalExpression,
                message:
                "Deeply nested conditional expression. Consider using 'when' for readability.",
                location: cond.Location);
        }

        TypeSymbol conditionType = AnalyzeExpression(expression: cond.Condition);

        if (!IsBoolType(type: conditionType))
        {
            ReportError(code: SemanticDiagnosticCode.ConditionalNotBool,
                message:
                $"Conditional expression requires a boolean condition, got '{conditionType.Name}'.",
                location: cond.Condition.Location);
        }

        // Bare literals in the branches conform to the context like anywhere else: `return if neg then
        // -1 else 0` in an S8 routine types both literals as S8. Without a context, one branch's type is
        // the other's context, and a literal branch follows the non-literal one (`if c then 0 else x`).
        TypeSymbol trueType;
        TypeSymbol falseType;
        if (expectedType == null && cond.TrueExpression is LiteralExpression &&
            cond.FalseExpression is not LiteralExpression)
        {
            falseType = AnalyzeExpression(expression: cond.FalseExpression);
            trueType = AnalyzeExpression(expression: cond.TrueExpression, expectedType: falseType);
        }
        else
        {
            trueType = AnalyzeExpression(expression: cond.TrueExpression, expectedType: expectedType);
            falseType = AnalyzeExpression(expression: cond.FalseExpression,
                expectedType: expectedType ?? trueType);
        }

        // Both branches must be compatible
        if (!IsAssignableTo(source: trueType, target: falseType) &&
            !IsAssignableTo(source: falseType, target: trueType))
        {
            ReportError(code: SemanticDiagnosticCode.ConditionalBranchTypeMismatch,
                message:
                $"Conditional expression branches have incompatible types: '{trueType.Name}' and '{falseType.Name}'.",
                location: cond.Location);
        }

        _conditionalNestingDepth--;

        // Return the common type (for now, use the true branch type)
        return trueType;
    }

    private RoutineTypeSymbol AnalyzeLambdaExpression(LambdaExpression lambda,
        TypeSymbol? expectedType = null)
    {
        // Collect variables from enclosing scope that might be captured
        IReadOnlyDictionary<string, VariableInfo> enclosingScopeVariables =
            _registry.GetAllVariablesInScope();
        // Collect only local (function-level) variables — these require 'given' to capture
        IReadOnlyDictionary<string, VariableInfo> localScopeVariables =
            _registry.GetLocalScopeVariables();

        _registry.EnterScope(kind: ScopeKind.Function, name: "lambda");

        // Extract expected parameter types from context (e.g., Callable[(S64, S64), Bool])
        List<TypeSymbol>? expectedParamTypes = expectedType is RoutineTypeSymbol rt
            ? rt.ParameterTypes
            : null;

        // Register lambda parameters and collect their types
        var parameterNames = new HashSet<string>();
        var parameterTypes = new List<TypeSymbol>();
        for (int pi = 0; pi < lambda.Parameters.Count; pi++)
        {
            Parameter param = lambda.Parameters[index: pi];
            TypeSymbol paramType;
            if (param.Type != null)
            {
                paramType = ResolveType(typeExpr: param.Type);
            }
            else if (expectedParamTypes != null && pi < expectedParamTypes.Count)
            {
                paramType = expectedParamTypes[index: pi];
            }
            else
            {
                // No annotation AND no typed context to infer from: the parameter has no type, and there is no
                // routine lookup while the program runs to stand in for one (it would silently become the error
                // type and stop the LLVM emitter). Both languages report it.
                string number = _registry.EntitiesAreShared ? "Int" : "S32";
                ReportError(code: SemanticDiagnosticCode.LambdaParameterTypeNotInferable,
                    message:
                    $"Cannot infer the type of lambda parameter '{param.Name}' — there is no type " +
                    $"annotation and no typed target to infer from. Annotate it (e.g. " +
                    $"`(a: {number}, b: {number}) => a + b`) or assign the lambda to a typed target " +
                    $"(`var f: Callable[({number}, {number}), {number}] = …`).",
                    location: param.Location);

                paramType = ErrorTypeSymbol.Instance;
            }

            _registry.DeclareVariable(name: param.Name, type: paramType, location: param.Location,
                isParameter: true);
            parameterNames.Add(item: param.Name);
            parameterTypes.Add(item: paramType);
        }

        // A lambda's body runs where the lambda is called, which may be outside the `danger` block it was written in,
        // so the block does not reach into it.
        int dangerDepthAroundLambda = _dangerBlockDepth;
        _dangerBlockDepth = 0;
        TypeSymbol returnType;
        try
        {
            returnType = AnalyzeExpression(expression: lambda.Body);
        }
        finally
        {
            _dangerBlockDepth = dangerDepthAroundLambda;
        }

        // Validate captured variables (RazorForge only)
        // Lambda bodies can reference variables from enclosing scope - these are captures
        ValidateLambdaCaptures(lambda: lambda,
            enclosingScopeVariables: enclosingScopeVariables,
            localScopeVariables: localScopeVariables,
            parameterNames: parameterNames);

        _registry.ExitScope();

        // Create a proper function type: (ParamTypes) -> ReturnType. A lambda whose body can fail where it stands
        // (a checked operator, a subscript, a call of a failable routine or of a routine value that may fail) is
        // a failable routine value: it goes only to a parameter written `failable Callable[...]`.
        return _registry.GetOrCreateRoutineType(parameterTypes: parameterTypes,
            returnType: returnType,
            isFailable: Builder.Instantiation.FailurePointCalls.HasFailurePoint(body: lambda.Body, registry: _registry));
    }

    /// <summary>
    /// Validates that lambda captures don't include forbidden types and that all
    /// local-scope captures are declared in the 'given' clause (RazorForge only).
    /// </summary>
    /// <param name="lambda">The lambda expression being analyzed.</param>
    /// <param name="enclosingScopeVariables">All variables available in the enclosing scope.</param>
    /// <param name="localScopeVariables">Variables from local (function-level) scopes only — require 'given'.</param>
    /// <param name="parameterNames">Names of lambda parameters (not captures).</param>
    private void ValidateLambdaCaptures(LambdaExpression lambda,
        IReadOnlyDictionary<string, VariableInfo> enclosingScopeVariables,
        IReadOnlyDictionary<string, VariableInfo> localScopeVariables,
        HashSet<string> parameterNames)
    {
        // Find all identifier expressions in the lambda body
        List<IdentifierExpression> identifiers = CollectIdentifiers(expression: lambda.Body);

        // Build set of given captures for quick lookup
        HashSet<string>? givenNames = lambda.Captures != null
            ? [.. lambda.Captures]
            : null;

        foreach (IdentifierExpression id in identifiers)
        {
            ValidateLambdaCaptureIdentifier(id: id,
                enclosingScopeVariables: enclosingScopeVariables,
                localScopeVariables: localScopeVariables,
                parameterNames: parameterNames,
                givenNames: givenNames);
        }
    }

    /// <summary>
    /// Validates a single identifier referenced in a lambda body as a potential capture: skips parameters
    /// and special identifiers, validates the captured type, and enforces the 'given' clause for local
    /// captures (RazorForge only).
    /// </summary>
    private void ValidateLambdaCaptureIdentifier(IdentifierExpression id,
        IReadOnlyDictionary<string, VariableInfo> enclosingScopeVariables,
        IReadOnlyDictionary<string, VariableInfo> localScopeVariables,
        HashSet<string> parameterNames, HashSet<string>? givenNames)
    {
        // Skip if it's a parameter (not a capture)
        if (parameterNames.Contains(item: id.Name))
        {
            return;
        }

        // `me` of a record is a value the lambda keeps a copy of, and a shared entity's `me` (Suflae) one more
        // handle. An entity's `me` is reached for the call only, as a token is.
        if (id.Name == "me")
        {
            if (!_registry.EntitiesAreShared && id.ResolvedType is { } meType &&
                (IsRawEntityType(type: meType) || IsMemoryToken(type: meType)))
            {
                string entity = (meType.TypeArguments is [{ } inner] && IsMemoryToken(type: meType)
                    ? inner
                    : meType).Name;
                ReportError(code: SemanticDiagnosticCode.LambdaCaptureToken,
                    message: $"You capture 'me' in this lambda, but 'me' is the '{entity}' this routine was called " +
                             "on, reached for this call only, and the lambda can be called after the call ends. Copy " +
                             $"what the lambda needs out of 'me' first (`var n = me.n`, then capture 'n'), or capture " +
                             $"a handle that keeps the entity (`Retained[{entity}]`).",
                    location: id.Location);
            }

            return;
        }

        if (id.Name == "none")
        {
            return;
        }

        // Check if this identifier refers to a captured variable
        if (!enclosingScopeVariables.TryGetValue(key: id.Name, value: out VariableInfo? varInfo))
        {
            return;
        }

        // Validate that the captured type is allowed
        ValidateCapturedType(varName: id.Name, varType: varInfo.Type, location: id.Location);

        // Check 'given' clause enforcement for local captures
        if (ChecksOwnershipHere &&
            localScopeVariables.ContainsKey(key: id.Name) && !varInfo.IsPreset)
        {
            if (givenNames == null)
            {
                // No 'given' clause — implicit capture of local variable
                ReportError(code: SemanticDiagnosticCode.LambdaCaptureWithoutGiven,
                    message:
                    $"Lambda captures local variable '{id.Name}' without declaring it in 'given' clause. " +
                    "All local captures must be explicit via 'given'.",
                    location: id.Location);
            }
            else if (!givenNames.Contains(item: id.Name))
            {
                // Has 'given' clause but this variable isn't in it
                ReportError(code: SemanticDiagnosticCode.LambdaCaptureWithoutGiven,
                    message:
                    $"Lambda captures local variable '{id.Name}' but it is not listed in the 'given' clause.",
                    location: id.Location);
            }
        }
    }

    /// <summary>
    /// Validates that a captured variable's type is allowed in lambda captures.
    /// </summary>
    /// <param name="varName">Name of the captured variable.</param>
    /// <param name="varType">Type of the captured variable.</param>
    /// <param name="location">Source location for error reporting.</param>
    private void ValidateCapturedType(string varName, TypeSymbol varType, SourceLocation location)
    {
        // Where entities are shared handles there is no token and no single holder: a lambda keeps one more
        // handle like any other name does.
        if (_registry.EntitiesAreShared)
        {
            return;
        }

        // Check for memory tokens (scope-bound, cannot be captured)
        if (IsMemoryToken(type: varType))
        {
            string target = varType.TypeArguments is [{ } viewed] ? viewed.ShortTypeName : "T";
            ReportError(code: SemanticDiagnosticCode.LambdaCaptureToken,
                message: $"You capture '{varName}' in this lambda, but '{varName}' is a token " +
                         $"(`{varType.ShortTypeName}`), and the lambda can be called after the place `{target}` is in " +
                         $"is gone. Pass the token to the lambda as an argument where you call it, or capture a " +
                         $"handle that keeps the entity (`Retained[{target}]`, or `Guarded[{target}]` across jobs).",
                location: location);
            return;
        }

        // Check for raw entities (must use handles for capture)
        if (IsRawEntityType(type: varType))
        {
            ReportError(code: SemanticDiagnosticCode.LambdaCaptureRawEntity,
                message:
                $"Cannot capture raw entity '{varName}' of type '{varType.Name}' in lambda - " +
                $"raw entities cannot be captured. " +
                $"Put it under a manager that can share it first (Retained[T], or Guarded[T] across jobs) and " +
                $"capture that.",
                location: location);
        }
    }

    /// <summary>
    /// Checks if a type is a raw entity (not wrapped in a handle or token).
    /// </summary>
    private static bool IsRawEntityType(TypeSymbol type)
    {
        // Raw entities are entity types that are not wrapped
        return type.Category == TypeCategory.Entity && !IsMemoryToken(type: type) &&
               !Wrappers.IsWrapperType(type: type) && !IsHijacked(type: type);
    }

    /// <summary>
    /// Collects all identifier expressions in an expression tree.
    /// </summary>
    private static List<IdentifierExpression> CollectIdentifiers(Expression expression)
    {
        var identifiers = new List<IdentifierExpression>();
        CollectIdentifiersRecursive(expression: expression, identifiers: identifiers);
        return identifiers;
    }

    /// <summary>
    /// Recursively collects identifier expressions.
    /// </summary>
    private static void CollectIdentifiersRecursive(Expression expression,
        List<IdentifierExpression> identifiers)
    {
        switch (expression)
        {
            case IdentifierExpression id:
                identifiers.Add(item: id);
                break;

            case CompoundAssignmentExpression compound:
                CollectIdentifiersFromAll(identifiers: identifiers,
                    compound.Target,
                    compound.Value);
                break;

            case BinaryExpression binary:
                CollectIdentifiersFromAll(identifiers: identifiers, binary.Left, binary.Right);
                break;

            case UnaryExpression unary:
                CollectIdentifiersRecursive(expression: unary.Operand, identifiers: identifiers);
                break;

            case StealExpression steal:
                CollectIdentifiersRecursive(expression: steal.Operand, identifiers: identifiers);
                break;

            case BackIndexExpression back:
                CollectIdentifiersRecursive(expression: back.Operand, identifiers: identifiers);
                break;

            case CallExpression call:
                CollectIdentifiersRecursive(expression: call.Callee, identifiers: identifiers);
                CollectIdentifiersFromAll(identifiers: identifiers, expressions: call.Arguments);
                break;

            case MemberExpression member:
                CollectIdentifiersRecursive(expression: member.Object, identifiers: identifiers);
                break;

            case IndexExpression index:
                CollectIdentifiersFromAll(identifiers: identifiers, index.Object, index.Index);
                break;

            case ConditionalExpression cond:
                CollectIdentifiersFromAll(identifiers: identifiers,
                    cond.Condition,
                    cond.TrueExpression,
                    cond.FalseExpression);
                break;

            case LambdaExpression:
                // Don't descend into nested lambdas - they have their own capture context
                break;

            case RangeExpression range:
                CollectIdentifiersFromRange(range: range, identifiers: identifiers);
                break;

            case CreatorExpression creator:
                CollectIdentifiersFromAll(identifiers: identifiers,
                    expressions: creator.MemberVariables.Select(selector: mv => mv.Value));
                break;

            case ListLiteralExpression list:
                CollectIdentifiersFromAll(identifiers: identifiers, expressions: list.Elements);
                break;

            case SetLiteralExpression set:
                CollectIdentifiersFromAll(identifiers: identifiers, expressions: set.Elements);
                break;

            case DictLiteralExpression dict:
                CollectIdentifiersFromPairs(identifiers: identifiers, pairs: dict.Pairs);
                break;

            case TupleLiteralExpression tuple:
                CollectIdentifiersFromAll(identifiers: identifiers, expressions: tuple.Elements);
                break;

            case BlockExpression block:
                CollectIdentifiersRecursive(expression: block.Value, identifiers: identifiers);
                break;

            case IsPatternExpression isPat:
                CollectIdentifiersRecursive(expression: isPat.Expression,
                    identifiers: identifiers);
                break;

            case NamedArgumentExpression named:
                CollectIdentifiersRecursive(expression: named.Value, identifiers: identifiers);
                break;

            case DictEntryLiteralExpression dictEntry:
                CollectIdentifiersFromAll(identifiers: identifiers,
                    dictEntry.Key,
                    dictEntry.Value);
                break;

            case GenericMemberRoutineCallExpression generic:
                CollectIdentifiersRecursive(expression: generic.Object, identifiers: identifiers);
                CollectIdentifiersFromAll(identifiers: identifiers,
                    expressions: generic.Arguments);
                break;

            case GenericMemberExpression genericMember:
                CollectIdentifiersRecursive(expression: genericMember.Object,
                    identifiers: identifiers);
                break;

            case TypeConversionExpression conv:
                CollectIdentifiersRecursive(expression: conv.Expression, identifiers: identifiers);
                break;

            case ChainedComparisonExpression chain:
                CollectIdentifiersFromAll(identifiers: identifiers, expressions: chain.Operands);
                break;

            // Literal expressions and type expressions have no identifiers to collect
            case LiteralExpression:
            case TypeExpression:
                break;
        }
    }

    /// <summary>Recurses into each of the given sub-expressions, collecting identifiers.</summary>
    private static void CollectIdentifiersFromAll(List<IdentifierExpression> identifiers,
        params Expression[] expressions)
    {
        foreach (Expression expression in expressions)
        {
            CollectIdentifiersRecursive(expression: expression, identifiers: identifiers);
        }
    }

    /// <summary>Recurses into each sub-expression in the sequence, collecting identifiers.</summary>
    private static void CollectIdentifiersFromAll(List<IdentifierExpression> identifiers,
        IEnumerable<Expression> expressions)
    {
        foreach (Expression expression in expressions)
        {
            CollectIdentifiersRecursive(expression: expression, identifiers: identifiers);
        }
    }

    /// <summary>Recurses into both sides of each key/value pair, collecting identifiers.</summary>
    private static void CollectIdentifiersFromPairs(List<IdentifierExpression> identifiers,
        IEnumerable<(Expression Key, Expression Value)> pairs)
    {
        foreach ((Expression key, Expression value) in pairs)
        {
            CollectIdentifiersRecursive(expression: key, identifiers: identifiers);
            CollectIdentifiersRecursive(expression: value, identifiers: identifiers);
        }
    }

    /// <summary>Recurses into a range's start/end (and optional step), collecting identifiers.</summary>
    private static void CollectIdentifiersFromRange(RangeExpression range,
        List<IdentifierExpression> identifiers)
    {
        CollectIdentifiersRecursive(expression: range.Start, identifiers: identifiers);
        CollectIdentifiersRecursive(expression: range.End, identifiers: identifiers);
        if (range.Step != null)
        {
            CollectIdentifiersRecursive(expression: range.Step, identifiers: identifiers);
        }
    }

    private TypeSymbol AnalyzeRangeExpression(RangeExpression range,
        TypeSymbol? expectedType = null)
    {
        // When the range flows into a `Range[T]` context (e.g. a subscript slice `s[a til b]`, where
        // the element type is forced to U64), propagate T as the endpoint expected type so untyped
        // literals (`0 til 5`) retype to it instead of defaulting to S32. The returned Range element
        // type is the start type, so this also makes the whole range resolve to `Range[T]`.
        TypeSymbol? endpointExpected =
            expectedType is { TypeArguments: { Count: 1 } expArgs } &&
            expectedType.BareName == "Range"
                ? expArgs[index: 0]
                : null;

        TypeSymbol startType =
            AnalyzeExpression(expression: range.Start, expectedType: endpointExpected);
        TypeSymbol endType =
            AnalyzeExpression(expression: range.End, expectedType: endpointExpected);

        (startType, endType) = AdaptLiteralRangeBounds(range: range,
            endpointExpected: endpointExpected,
            startType: startType,
            endType: endType);

        bool startIsBack = range.Start is BackIndexExpression;
        bool endIsBack = range.End is BackIndexExpression;
        if (range.Step != null)
        {
            // The step has the range's element type. A negative literal step (`by -1`) counts down by its magnitude,
            // which every element type can hold, an unsigned one too (`3u64 to 0u64 by -1`): the magnitude is what
            // is checked against the element type.
            TypeSymbol stepExpected = endpointExpected ?? (startIsBack
                ? endType
                : startType);
            if (NegativeLiteralStepMagnitude(step: range.Step) is { } magnitude)
            {
                range.Step.ResolvedType = AnalyzeExpression(expression: magnitude, expectedType: stepExpected);
                range.DownwardStep = magnitude;
            }
            else
            {
                AnalyzeExpression(expression: range.Step, expectedType: stepExpected);
            }

            ValidateLiteralRangeStep(step: range.Step);
        }

        ValidateRangeBounds(range: range,
            endpointExpected: endpointExpected,
            startType: startType,
            endType: endType,
            startIsBack: startIsBack,
            endIsBack: endIsBack);

        // Element type: a subscript forces it (U64); otherwise the non-BackIndex start (or end) drives
        // it. Return the resolved `Range[T]`.
        TypeSymbol elementType = endpointExpected ?? (startIsBack
            ? endType
            : startType);
        TypeSymbol? rangeGenericDef = _registry.LookupType(name: "Range");
        if (rangeGenericDef != null && elementType is not ErrorTypeSymbol)
        {
            return _registry.GetOrCreateResolution(genericDef: rangeGenericDef,
                typeArguments: new List<TypeSymbol> { elementType });
        }

        return rangeGenericDef ?? ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// The magnitude of a negative literal range step (<c>by -1</c> → <c>1</c>), as a literal of its own, or null when
    /// the step is not a negative literal. A range that counts down keeps the magnitude and a downward direction, so
    /// an unsigned range counts down too.
    /// </summary>
    internal static LiteralExpression? NegativeLiteralStepMagnitude(Expression step)
    {
        return step switch
        {
            LiteralExpression { Value: string text } literal when text.StartsWith(value: '-') =>
                literal with { Value = text[1..] },
            UnaryExpression { Operator: UnaryOperator.Minus, Operand: LiteralExpression { Value: string text } operand }
                when !text.StartsWith(value: '-') => operand with { },
            _ => null
        };
    }

    /// <summary>
    /// Reports a literal range step of zero (<c>by 0</c>, <c>by 0.0</c>): it never moves, so the range would never
    /// reach its end. The sign of a step sets the direction (<c>10 to 0 by -1</c> counts down). A step that is not a
    /// literal is checked when the range is iterated (the stdlib's <c>Range.require_nonzero_step</c>).
    /// </summary>
    private void ValidateLiteralRangeStep(Expression step)
    {
        Expression operand = step;
        while (operand is UnaryExpression { Operator: UnaryOperator.Minus } unary)
        {
            operand = unary.Operand;
        }

        if (operand is not LiteralExpression literal ||
            !TryNumericLiteralIsZero(literal: literal, isZero: out bool isZero) || !isZero)
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.RangeStepZero,
            message: "You wrote `by 0`, but a step of 0 never moves, so the range would never reach its end. Give " +
                     "it a step that moves: `by 1` (or leave `by` out) to count up, `by -1` to count down.",
            location: step.Location);
    }

    /// <summary>
    /// Tells whether a numeric literal's magnitude is zero, ignoring a minus the parser folded into its text.
    /// Returns false for a literal that is not a number.
    /// </summary>
    private static bool TryNumericLiteralIsZero(LiteralExpression literal, out bool isZero)
    {
        switch (literal.Value)
        {
            case sbyte v: isZero = v == 0; return true;
            case short v: isZero = v == 0; return true;
            case int v: isZero = v == 0; return true;
            case long v: isZero = v == 0; return true;
            case Int128 v: isZero = v == 0; return true;
            case byte v: isZero = v == 0; return true;
            case ushort v: isZero = v == 0; return true;
            case uint v: isZero = v == 0; return true;
            case ulong v: isZero = v == 0; return true;
            case UInt128 v: isZero = v == 0; return true;
            case Half v: isZero = v == (Half)0; return true;
            case float v: isZero = v == 0; return true;
            case double v: isZero = v == 0; return true;
            case decimal v: isZero = v == 0; return true;
            case System.Numerics.BigInteger v: isZero = v.IsZero; return true;
            case string text when literal.LiteralType is TokenType.UndecidedInteger or TokenType.UndecidedDecimal
                or TokenType.IntegerLiteral or TokenType.DecimalLiteral or TokenType.S256Literal
                or TokenType.U256Literal or TokenType.B128Literal or TokenType.D32Literal or TokenType.D64Literal
                or TokenType.D128Literal:
                isZero = NumericTextIsZero(text: text);
                return true;
            default:
                isZero = false;
                return false;
        }
    }

    /// <summary>
    /// Tells whether the digits of a numeric literal's text are all zero (<c>0</c>, <c>0.0</c>, <c>0x00</c>,
    /// <c>0e5</c>). The exponent of a decimal or hex float does not count.
    /// </summary>
    private static bool NumericTextIsZero(string text)
    {
        string body = text.Replace(oldValue: "_", newValue: "").TrimStart('-');
        bool hex = false;
        if (body.Length > 2 && body[index: 0] == '0' && body[index: 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            hex = body[index: 1] is 'x' or 'X';
            body = body[2..];
        }

        int exponent = hex
            ? body.IndexOfAny(anyOf: ['p', 'P'])
            : body.IndexOfAny(anyOf: ['e', 'E']);
        if (exponent >= 0)
        {
            body = body[..exponent];
        }

        bool sawDigit = false;
        foreach (char c in body)
        {
            if (c == '.')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(c: c))
            {
                break;
            }

            sawDigit = true;
            if (c != '0')
            {
                return false;
            }
        }

        return sawDigit;
    }

    /// <summary>
    /// Adapts bare-literal range bounds to match the other concrete numeric endpoint type when no forced
    /// <c>Range[T]</c> context is present (RF-S767): <c>0 til me.count()</c> where <c>count()</c> is U64
    /// makes <c>0</c> a U64, not the S64 literal default. Returns the (possibly re-analyzed) start/end types.
    /// </summary>
    private (TypeSymbol StartType, TypeSymbol EndType) AdaptLiteralRangeBounds(
        RangeExpression range, TypeSymbol? endpointExpected, TypeSymbol startType,
        TypeSymbol endType)
    {
        if (endpointExpected != null)
        {
            return (startType, endType);
        }

        bool startIsLiteral = range.Start is LiteralExpression;
        bool endIsLiteral = range.End is LiteralExpression;
        // A const generic endpoint (`0 til COUNT` under `needs COUNT is U64`) gives its value type.
        TypeSymbol endValue = ConstGenericValueType(type: endType) ?? endType;
        TypeSymbol startValue = ConstGenericValueType(type: startType) ?? startType;
        if (startIsLiteral && !endIsLiteral && IsNumericType(type: endValue) &&
            startType != endValue)
        {
            startType = AnalyzeExpression(expression: range.Start, expectedType: endValue);
        }
        else if (endIsLiteral && !startIsLiteral && IsNumericType(type: startValue) &&
                 startValue != endType)
        {
            endType = AnalyzeExpression(expression: range.End, expectedType: startValue);
        }

        return (startType, endType);
    }

    /// <summary>
    /// Validates BackIndex endpoint placement and numeric bound types for a range expression,
    /// reporting diagnostics for violations.
    /// </summary>
    private void ValidateRangeBounds(RangeExpression range, TypeSymbol? endpointExpected,
        TypeSymbol startType, TypeSymbol endType, bool startIsBack,
        bool endIsBack)
    {
        // BackIndex (^n) endpoints are valid ONLY inside a subscript slice (`s[a til ^0]`), where the
        // element type is forced (U64) — a `^n` there lowers to `count - n` (see OperatorLoweringPass).
        // `inSubscript` is exactly "an expected Range[T] flowed in", which only the subscript path does.
        // Outside a subscript a `^n` bound is meaningless (there is no collection to count from).
        bool inSubscript = endpointExpected != null;
        if ((startIsBack || endIsBack) && !inSubscript)
        {
            ReportError(code: SemanticDiagnosticCode.BackIndexOutsideSubscript,
                message:
                "BackIndex (^n) can only be used in a subscript [^n] or slice [a til b], not a bare range.",
                location: (startIsBack
                    ? range.Start
                    : range.End).Location);
        }

        // Range bound types must be numeric — const-generic parameters (e.g. `N is U64`) and numeric-
        // constrained generic params qualify (they hold a numeric value at each monomorphization). A
        // BackIndex bound is exempt: it is a `^n` marker that resolves to a numeric `count - n`.
        bool startOk = startIsBack || IsNumericType(type: startType) ||
                       IsNumericGenericParam(type: startType);
        bool endOk = endIsBack || IsNumericType(type: endType) ||
                     IsNumericGenericParam(type: endType);
        if (!startOk || !endOk)
        {
            bool charBound = startType is { Name: "Char" } || endType is { Name: "Char" };
            ReportError(code: SemanticDiagnosticCode.RangeBoundsNotNumeric,
                message: charBound
                    ? "A range counts by adding its step to a number, and a 'Char' is a character, not a number: it has " +
                      "no next one. Count over the code points instead (`'a'.codepoint() to 'e'.codepoint()`) and " +
                      "make each back into a character with `Char.from_codepoint(n:)`."
                    : $"A range counts by adding its step to a number, and its bounds here are '{startType?.Name}' and " +
                      $"'{endType?.Name}'. Give it numbers as bounds.",
                location: range.Location);
        }
    }

    private TypeSymbol AnalyzeCreatorExpression(CreatorExpression creator)
    {
        TypeSymbol? type = LookupTypeWithImports(name: creator.TypeName);
        if (type == null)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownType,
                message:
                $"Unknown type '{creator.TypeName}'. Check the spelling, and make sure the module that defines it is imported.{Suggestions.ForType(typeName: creator.TypeName)}",
                location: creator.Location);
            return ErrorTypeSymbol.Instance;
        }

        // Handle generic type arguments
        if (creator.TypeArguments is { Count: > 0 })
        {
            var typeArgs = new List<TypeSymbol>();
            foreach (TypeExpression typeArg in creator.TypeArguments)
            {
                typeArgs.Add(item: ResolveType(typeExpr: typeArg));
            }

            // Arity guard: a wrong count of explicit type args (e.g. `Guarded[ReadOnly](from: n)` — one arg
            // for a two-param `Guarded[T, P]`) must be a clean diagnostic. Without the early return,
            // GetOrCreateResolution → RecordTypeSymbol.CreateInstance zips params↔args and crashes.
            if (type.GenericParameters is { } creatorParams &&
                creatorParams.Count != typeArgs.Count)
            {
                ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                    message:
                    $"Type '{type.Name}' expects {creatorParams.Count} type arguments, got {typeArgs.Count}.",
                    location: creator.Location);
                return ErrorTypeSymbol.Instance;
            }

            ValidateGenericConstraints(genericDef: type,
                typeArgs: typeArgs,
                location: creator.Location);
            type = _registry.GetOrCreateResolution(genericDef: type, typeArguments: typeArgs);
        }

        creator.ConstructedType = type;
        ZeroFillCheck.Check(rules: _registry.CompilationRules, constructed: type, argumentCount: creator.MemberVariables.Count, location: creator.Location, report: ReportError);
        creator.LoweringKind = ClassifyConstruction(type: type);

        // Propagate the in-flight bit from the resolved type's implicit constructor.
        // If TryRouteCreatorToCreate routes through a user-declared `create` below,
        // it overrides this with that routine's IsInFlightReturn.
        creator.IsInFlight = type.ImplicitConstructorReturnsInFlight;

        // Named-arg → create routing: if the provided names don't match any field but DO match
        // a `create(named:)` overload's parameter names, dispatch through that creator instead
        // of doing inline field-init. Lets `SegTreeLazy[..](size: 10, alg: alg)` route to
        // `SegTreeLazy.create(size:, alg:)` even though `size`/`alg` aren't field names.
        if (creator.MemberVariables.Count > 0 &&
            TryRouteCreatorToCreate(type: type, creator: creator))
        {
            return type;
        }

        // Validate member variable initializers
        ValidateCreatorMemberVariables(type: type,
            memberVariables: creator.MemberVariables,
            location: creator.Location);

        return type;
    }

    /// <summary>
    /// Tries to route a CreatorExpression's named args to a matching `create(named:)` overload.
    /// Returns true if a matching overload was found and resolved (caller should skip field-init
    /// validation). The match requires that every provided arg name corresponds to a parameter
    /// name on some `create` overload, AND that at least one provided name is NOT a field name
    /// (field-init pattern is still preferred when all names are fields). Routing is purely
    /// name-based; arg analysis (with proper expected types) happens via the standard pipeline
    /// later when codegen evaluates the call.
    /// </summary>
    private bool TryRouteCreatorToCreate(TypeSymbol type, CreatorExpression creator)
    {
        // `Hijacked[T](raw)`: a positional construction on a generic type reaches here with empty names
        // (GenericCallLoweringPass lowers it before analysis). Bind it to a creator by argument type and
        // give each argument its parameter's name, so the named routing below takes over.
        if (creator.MemberVariables.All(predicate: mv => mv.Name.Length == 0) &&
            !NamePositionalCreatorArguments(type: type, creator: creator))
        {
            return false;
        }

        var providedNames = creator.MemberVariables
                                   .Select(selector: mv => mv.Name)
                                   .ToList();

        // Name-based match against create overloads. Iterate type's memberRoutines looking for ones
        // named `create` whose parameter names match the provided set exactly. If multiple
        // overloads share the same param names (e.g. `S64.create(from: S8)` vs
        // `S64.create(from: ComparisonSign)`), bail out — disambiguation by arg type is the
        // job of the legacy path and we don't want to silently pick the wrong overload.
        // Use CollectMemberRoutineCandidates: it walks the generic definition for
        // generic resolutions (e.g. List[V] → List[T]) and runs SubstituteMemberRoutineForOwner
        // so parameter types come back resolved to the receiver's concrete type args.
        var providedNameSet = new HashSet<string>(collection: providedNames);
        var nameMatches = new List<RoutineInfo>();
        var candidates = new List<RoutineInfo>();
        _registry.CollectCreatorCandidates(type: type, candidates: candidates);
        // Also pull the concrete type's own routines directly — CollectCreatorCandidates
        // can miss a user-declared constructor on an entity, while GetMemberRoutinesForType returns it
        // (this is how the entity `destruct` resolves correctly elsewhere). Failable creators keep the
        // same Creator kind (IsFailable is a structured flag), so IsCreator matches both.
        candidates.AddRange(collection: _registry.GetMemberRoutinesForType(type: type)
                                                 .Where(predicate: m => m.IsCreator));
        // A creator may leave defaulted parameters out, but only when the names are not a field-init
        // (all of them fields), which keeps preferring inline field-init for a partial field list.
        List<MemberVariableInfo>? typeFields = type switch
        {
            RecordTypeSymbol record => record.MemberVariables,
            EntityTypeSymbol entity => entity.MemberVariables,
            _ => null
        };
        bool mayOmitDefaults = typeFields == null ||
                               !providedNameSet.IsSubsetOf(
                                   other: typeFields.Select(selector: f => f.Name));
        foreach (RoutineInfo m in candidates)
        {
            // Every provided name is a parameter, and every parameter left out has a default.
            var pNames = new HashSet<string>(
                collection: m.Parameters.Select(selector: p => p.Name));
            bool fits = m.Parameters.Count == providedNameSet.Count
                ? pNames.SetEquals(other: providedNameSet)
                : mayOmitDefaults && pNames.IsSupersetOf(other: providedNameSet) &&
                  m.Parameters.All(predicate: p =>
                      providedNameSet.Contains(item: p.Name) || p.HasDefaultValue);
            if (fits)
            {
                nameMatches.Add(item: m);
            }
        }

        // Prefer a user-defined (non-synthesized) creator. Entities/records also get an
        // auto-synthesized all-args creator whose only job is inline field-init — when that is the
        // sole match we fall through to inline construction below. A user creator with the same
        // signature as the all-args creator is the real constructor and must be called so its
        // body/side-effects run.
        // Dedupe by registry key: CollectMemberRoutineCandidates can surface the same overload
        // through more than one path (owner table + protocol walk), which would make a single
        // user creator look ambiguous and wrongly fall back to inline construction.
        var userMatches = nameMatches.Where(predicate: m => !m.IsSynthesized)
                                     .GroupBy(keySelector: m => m.RegistryKey)
                                     .Select(selector: g => g.First())
                                     .ToList();

        // A type constructor written inside the type's own creator is the field-init base case ONLY
        // when it resolves back to the same creator being compiled (genuine self-recursion). A call to
        // a different create overload (for example a conversion from a narrower integer type) is an
        // ordinary conversion and must route to that overload — otherwise codegen falls back to inline
        // field-init and mis-lowers bit-carrier types to a raw integer reinterpret of the IEEE storage.
        bool insideOwnCreate = _currentRoutine is { IsCreator: true } currentCreate &&
                               currentCreate.OwnerType != null &&
                               (currentCreate.OwnerType.FullName == type.FullName ||
                                currentCreate.OwnerType.Name == type.Name) &&
                               userMatches.Count == 1 &&
                               ReferenceEquals(objA: userMatches[index: 0], objB: currentCreate);

        // Route through a user-defined `create` (so its body runs). A self-reference inside the
        // creator, or no user match, falls back to inline field-init / standard validation. The
        // name-set match above only BINDS the named args to a creator's parameters (and separates a
        // create-call from field-init). When more than one overload binds (e.g. `Text(from: S64)` vs
        // `Text(from: B64)`) the overload is chosen by ARGUMENT TYPE via the
        // canonical signature-based resolver — never by name.
        if (insideOwnCreate || userMatches.Count == 0)
        {
            return false;
        }

        RoutineInfo? match = userMatches.Count == 1
            ? userMatches[index: 0]
            : SelectCreatorOverloadByArgType(type: type,
                userMatches: userMatches,
                creator: creator);
        if (match == null)
        {
            return false;
        }

        // Analyze each arg with the matching parameter's type as the expected type so integer
        // literals coerce correctly (e.g. `size: 10` → S8 if the param is S8, not default S32).
        var paramByName = match.Parameters.ToDictionary(keySelector: p => p.Name);
        foreach ((string argName, Expression val) in creator.MemberVariables)
        {
            TypeSymbol? expected = paramByName.TryGetValue(key: argName,
                value: out ParamInfo? p)
                ? p.Type
                : null;
            TypeSymbol argType = AnalyzeExpression(expression: val, expectedType: expected);

            // The creator was bound by its parameter NAMES, so the argument types are checked here: a value
            // the parameter cannot take (an `Int` for a `B64`) must not reach the emitter as one.
            if (expected != null && argType.Category != TypeCategory.Error &&
                expected.Category != TypeCategory.Error && !MentionsGenericParameter(type: expected) &&
                !IsAssignableTo(source: argType, target: expected))
            {
                ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
                    message:
                    $"Argument '{argName}' of '{type.ShortTypeName}': cannot convert '{argType.ShortTypeName}' to '{expected.ShortTypeName}'.",
                    location: val.Location);
            }
        }

        creator.ResolvedCreatorRoutine = match;
        creator.LoweringKind = CallLoweringKind.TypeConstructor;
        creator.IsInFlight = match.IsInFlightReturn;

        if (match.IsFailable && _currentRoutine != null)
        {
            _currentRoutine.HasFailableCalls = true;
            _currentRoutine.FailableCallees.Add(item: match);
        }

        return true;
    }

    /// <summary>
    /// Selects the `create` overload for a named-arg creator call by ARGUMENT TYPE, when more than
    /// one overload binds the provided parameter names (e.g. `Text(from: S64)` vs
    /// `Text(from: B64)`). Orders the named args into the shared parameter order — analyzing
    /// each with its parameter type as the expected type so a literal coerces to the parameter's
    /// width — then delegates to the canonical signature-based resolver
    /// <c>LookupCreatorOverload</c>. Returns null (caller falls back to standard validation) when the
    /// resolver finds no fit, or resolves to an overload outside the name-bound set. Never picks by
    /// name.
    /// </summary>
    private RoutineInfo? SelectCreatorOverloadByArgType(TypeSymbol type,
        List<RoutineInfo> userMatches, CreatorExpression creator)
    {
        Dictionary<string, Expression> argByName = creator.MemberVariables
            .ToDictionary(keySelector: mv => mv.Name, elementSelector: mv => mv.Value);

        var orderedArgTypes = new List<TypeSymbol>();
        foreach (ParamInfo p in userMatches[index: 0].Parameters)
        {
            if (!argByName.TryGetValue(key: p.Name, value: out Expression? arg))
            {
                if (p.HasDefaultValue)
                {
                    continue;
                }

                return null;
            }

            orderedArgTypes.Add(item: AnalyzeExpression(expression: arg, expectedType: p.Type));
        }

        RoutineInfo? resolved =
            _registry.LookupCreatorOverload(type: type, argTypes: orderedArgTypes);
        return resolved != null &&
               userMatches.Any(predicate: m => m.RegistryKey == resolved.RegistryKey)
            ? resolved
            : null;
    }

    /// <summary>
    /// Names the all-positional arguments of <paramref name="creator"/> after the parameters of the
    /// creator they bind to, chosen by argument type through <c>LookupCreatorOverload</c>. Each argument
    /// is analyzed with its slot's parameter type as the expected type when every candidate creator
    /// agrees on it, so a bare literal adapts. Returns false (arguments left unnamed) when no creator
    /// accepts the arguments.
    /// </summary>
    private bool NamePositionalCreatorArguments(TypeSymbol type, CreatorExpression creator)
    {
        int argCount = creator.MemberVariables.Count;
        var candidates = new List<RoutineInfo>();
        _registry.CollectCreatorCandidates(type: type, candidates: candidates);
        candidates.AddRange(collection: _registry.GetMemberRoutinesForType(type: type)
                                                 .Where(predicate: m => m.IsCreator));
        candidates.RemoveAll(match: m => m.Parameters.Any(predicate: p => p.IsVariadicParam) ||
                                         !RoutineCanAcceptArgCount(routine: m, argCount: argCount));
        if (candidates.Count == 0)
        {
            return false;
        }

        var argTypes = new List<TypeSymbol>(capacity: argCount);
        for (int i = 0; i < argCount; i++)
        {
            var slotTypes = candidates.Select(selector: m => m.Parameters[index: i].Type)
                                      .DistinctBy(keySelector: t => t.FullName)
                                      .ToList();
            argTypes.Add(item: AnalyzeExpression(expression: creator.MemberVariables[index: i].Value,
                expectedType: slotTypes.Count == 1
                    ? slotTypes[index: 0]
                    : null));
        }

        if (_registry.LookupCreatorOverload(type: type, argTypes: argTypes) is not { } resolved)
        {
            return false;
        }

        for (int i = 0; i < argCount; i++)
        {
            creator.MemberVariables[index: i] = (resolved.Parameters[index: i].Name,
                creator.MemberVariables[index: i].Value);
        }

        return true;
    }

    /// <summary>
    /// Validates creator member variable initializers:
    /// - Each provided member variable exists on the type
    /// - Value types are assignable to member variable types
    /// - No duplicate member variable assignments
    /// - All required member variables are provided
    /// <paramref name="analyzedTypes"/>, when given, holds each value's type already analyzed against its member
    /// variable's type (a generic construction analyzes its arguments before it knows it is a field-init), in the
    /// order of <paramref name="memberVariables"/>, so no value is analyzed twice.
    /// </summary>
    private void ValidateCreatorMemberVariables(TypeSymbol type,
        List<(string Name, Expression Value)> memberVariables, SourceLocation location,
        IReadOnlyList<TypeSymbol>? analyzedTypes = null)
    {
        // Get the type's member variables. An instance's are read from its definition and substituted once below:
        // its own list already has the arguments in, and substituting that again undoes a swap (`Pair[B, A]` built
        // inside `Pair[A, B]`).
        List<MemberVariableInfo>? typeMemberVariables = type switch
        {
            RecordTypeSymbol record => (record.GenericDefinition ?? record).MemberVariables,
            EntityTypeSymbol entity => (entity.GenericDefinition ?? entity).MemberVariables,
            _ => null
        };

        if (typeMemberVariables == null)
        {
            if (memberVariables.Count > 0)
            {
                ReportError(code: SemanticDiagnosticCode.TypeNotMemberVariableInitializable,
                    message:
                    $"Type '{type.Name}' does not support member variable initialization.",
                    location: location);
            }

            return;
        }

        // Build a lookup for expected member variables
        var memberVariableLookup = new Dictionary<string, MemberVariableInfo>();
        foreach (MemberVariableInfo memberVariable in typeMemberVariables)
        {
            memberVariableLookup[key: memberVariable.Name] = memberVariable;
        }

        // Names that are not all member variables were meant for a creator the type's author wrote: say which
        // creator and which argument does not fit, instead of listing the member variables left out.
        if (memberVariables.Count > 0 &&
            memberVariables.Any(predicate: mv => mv.Name.Length > 0 && !memberVariableLookup.ContainsKey(key: mv.Name)) &&
            ReportWrittenCreatorMiss(type: type, memberVariables: memberVariables, location: location,
                analyzedTypes: analyzedTypes))
        {
            return;
        }

        // Track which member variables have been provided (to detect duplicates and missing member variables)
        var providedMemberVariables = new HashSet<string>();

        // Validate each provided member variable
        for (int index = 0; index < memberVariables.Count; index++)
        {
            (string memberVariableName, Expression value) = memberVariables[index: index];
            TypeSymbol? analyzed = analyzedTypes?[index];

            // A positional argument that no creator accepted (NamePositionalCreatorArguments left it unnamed).
            if (memberVariableName.Length == 0)
            {
                TypeSymbol argType = analyzed ?? AnalyzeExpression(expression: value);
                ReportError(code: SemanticDiagnosticCode.MemberVariableNotFound,
                    message:
                    $"No creator of '{type.Name}' takes {Article.For(name: argType.Name)} '{argType.Name}' here. Check the argument's type, or name the parameter you mean (for example `{type.Name}(from: ...)`).",
                    location: value.Location);
                continue;
            }

            // Check for duplicates
            if (!providedMemberVariables.Add(item: memberVariableName))
            {
                ReportError(code: SemanticDiagnosticCode.DuplicateMemberVariableInitializer,
                    message: $"Duplicate member variable initializer for '{memberVariableName}'.",
                    location: value.Location);
                continue;
            }

            // Check if member variable exists
            if (!memberVariableLookup.TryGetValue(key: memberVariableName,
                    value: out MemberVariableInfo? expectedMemberVariable))
            {
                ReportError(code: SemanticDiagnosticCode.MemberVariableNotFound,
                    message:
                    $"Type '{type.Name}' does not have a member variable named '{memberVariableName}'.",
                    location: value.Location);
                if (analyzed is null)
                {
                    AnalyzeExpression(expression: value); // Still analyze the value
                }

                continue;
            }

            // Analyze value with expected type for contextual inference
            TypeSymbol memberVariableType = expectedMemberVariable.Type;

            // For generic resolutions, substitute type parameters in member variable type
            if (type is { IsGenericResolution: true, TypeArguments: not null })
            {
                memberVariableType =
                    SubstituteTypeParameters(type: memberVariableType, genericType: type);
            }

            TypeSymbol valueType = analyzed ??
                                   AnalyzeExpression(expression: value, expectedType: memberVariableType);

            // Check type compatibility
            if (!IsAssignableTo(source: valueType, target: memberVariableType))
            {
                ReportError(code: SemanticDiagnosticCode.MemberVariableTypeMismatch,
                    message:
                    $"Cannot assign '{valueType.Name}' to member variable '{memberVariableName}' of type '{memberVariableType.Name}'.",
                    location: value.Location);
            }
        }

        // Check for missing required member variables (member variables without default values)
        foreach (MemberVariableInfo memberVariable in typeMemberVariables)
        {
            if (!providedMemberVariables.Contains(item: memberVariable.Name) &&
                !memberVariable.HasDefaultValue)
            {
                ReportError(code: SemanticDiagnosticCode.MissingRequiredMemberVariable,
                    message:
                    $"Missing required member variable '{memberVariable.Name}' in creator for '{type.Name}'.",
                    location: location);
            }
        }

        FillMemberVariableDefaults(type: type,
            typeMemberVariables: typeMemberVariables,
            memberVariables: memberVariables,
            provided: providedMemberVariables);
    }

    /// <summary>
    /// A construction whose argument names fit no member variable list, on a type with creators its author wrote:
    /// reports one error that names the creator taking those names and each argument whose type does not fit its
    /// parameter, or the creators there are when none takes those names. Returns false when the type has no
    /// written creator (the construction is then checked member variable by member variable).
    /// </summary>
    private bool ReportWrittenCreatorMiss(TypeSymbol type, List<(string Name, Expression Value)> memberVariables,
        SourceLocation location, IReadOnlyList<TypeSymbol>? analyzedTypes)
    {
        List<RoutineInfo> written = _registry.GetMemberRoutinesForType(type: DefinitionOf(type: type))
                                             .Where(predicate: m => m.IsCreator && !m.IsSynthesized)
                                             .ToList();
        if (written.Count == 0)
        {
            return false;
        }

        TypeSymbol ParameterType(ParamInfo p)
        {
            return type is { IsGenericResolution: true, TypeArguments: not null }
                ? SubstituteTypeParameters(type: p.Type, genericType: type)
                : p.Type;
        }

        string Signature(RoutineInfo creator)
        {
            return $"{type.Name}({string.Join(separator: ", ", values: creator.Parameters.Select(selector: p => $"{p.Name}: {ParameterType(p: p).Name}"))})";
        }

        var names = memberVariables.Select(selector: mv => mv.Name).ToHashSet(comparer: StringComparer.Ordinal);
        RoutineInfo? named = written.FirstOrDefault(predicate: m =>
            m.Parameters.Count >= names.Count && names.All(predicate: n => m.Parameters.Any(predicate: p => p.Name == n)) &&
            m.Parameters.All(predicate: p => names.Contains(item: p.Name) || p.HasDefaultValue));
        var argTypes = new List<TypeSymbol>(capacity: memberVariables.Count);
        for (int i = 0; i < memberVariables.Count; i++)
        {
            (string name, Expression value) = memberVariables[index: i];
            ParamInfo? parameter = named?.Parameters.FirstOrDefault(predicate: p => p.Name == name);
            argTypes.Add(item: analyzedTypes?[index: i] ??
                               AnalyzeExpression(expression: value,
                                   expectedType: parameter != null ? ParameterType(p: parameter) : null));
        }

        string given = string.Join(separator: ", ",
            values: memberVariables.Select(selector: (mv, i) => $"{mv.Name}: {argTypes[index: i].Name}"));
        if (named == null)
        {
            ReportError(code: SemanticDiagnosticCode.MemberVariableNotFound,
                message: $"You build '{type.Name}' with ({given}), and no creator of it takes those names. Its " +
                         $"creators are {string.Join(separator: ", ", values: written.Select(selector: c => $"'{Signature(creator: c)}'"))}.",
                location: location);
            return true;
        }

        var misfits = new List<string>();
        for (int i = 0; i < memberVariables.Count; i++)
        {
            ParamInfo parameter = named.Parameters.First(predicate: p => p.Name == memberVariables[index: i].Name);
            TypeSymbol wanted = ParameterType(p: parameter);
            if (argTypes[index: i] is not ErrorTypeSymbol && !IsAssignableTo(source: argTypes[index: i], target: wanted))
            {
                misfits.Add(item: $"'{parameter.Name}' takes {Article.For(name: wanted.Name)} '{wanted.Name}' and you " +
                                  $"give it {Article.For(name: argTypes[index: i].Name)} '{argTypes[index: i].Name}' " +
                                  $"(convert it with '.to[{wanted.Name}]()')");
            }
        }

        if (misfits.Count == 0)
        {
            return false;
        }

        ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
            message: $"The creator '{Signature(creator: named)}' does not take ({given}): " +
                     $"{string.Join(separator: ", and ", values: misfits)}.",
            location: location);
        return true;
    }

    /// <summary>
    /// Gives a field-by-field construction a value for each member variable it leaves out that was written
    /// with a default (<c>age: Int = 30</c>), analyzed against the member variable's type, and puts the
    /// values in declaration order, which is how the construction is built.
    /// </summary>
    private void FillMemberVariableDefaults(TypeSymbol type, List<MemberVariableInfo> typeMemberVariables,
        List<(string Name, Expression Value)> memberVariables, HashSet<string> provided)
    {
        if (memberVariables.Any(predicate: mv => mv.Name.Length == 0) ||
            !typeMemberVariables.Any(predicate: mv => mv.DefaultValue != null && !provided.Contains(item: mv.Name)))
        {
            return;
        }

        var ordered = new List<(string Name, Expression Value)>(capacity: typeMemberVariables.Count);
        foreach (MemberVariableInfo memberVariable in typeMemberVariables)
        {
            int given = memberVariables.FindIndex(match: mv => mv.Name == memberVariable.Name);
            if (given >= 0)
            {
                ordered.Add(item: memberVariables[index: given]);
                continue;
            }

            if (memberVariable.DefaultValue is not { } written)
            {
                return;
            }

            TypeSymbol memberVariableType = type is { IsGenericResolution: true, TypeArguments: not null }
                ? SubstituteTypeParameters(type: memberVariable.Type, genericType: type)
                : memberVariable.Type;
            Expression value = Builder.Instantiation.GenericAstRewriter.DeepCloneExpression(expr: written);
            AnalyzeExpression(expression: value, expectedType: memberVariableType);
            ordered.Add(item: (memberVariable.Name, value));
        }

        memberVariables.Clear();
        memberVariables.AddRange(collection: ordered);
    }

    /// <summary>
    /// Matches a for-loop destructuring field name produced by the lowering pass: <c>item0</c>,
    /// <c>item1</c>, etc. Used to distinguish a real missing-member error from a wrong-element-type
    /// destructuring error (the user wrote <c>for (a, b) in nonTuple</c>).
    /// </summary>
    [GeneratedRegex(pattern: @"^item\d+$")]
    private static partial Regex TupleDestructureFieldRegex();
}
