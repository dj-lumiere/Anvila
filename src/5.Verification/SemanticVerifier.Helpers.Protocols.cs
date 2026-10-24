using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Builder.Verification.Enums;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    private static TypeSymbol SubstituteTypeParams(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitution)
    {
        // Direct substitution for generic parameters
        if (type is GenericParameterTypeSymbol &&
            substitution.TryGetValue(key: type.Name, value: out TypeSymbol? sub))
        {
            return sub;
        }

        // Recursive substitution in type arguments
        if (type.TypeArguments is not { Count: > 0 })
        {
            return type;
        }

        var newArgs = type.TypeArguments
                          .Select(selector: arg =>
                               SubstituteTypeParams(type: arg, substitution: substitution))
                          .ToList();

        // Check if anything actually changed
        bool changed = false;
        for (int i = 0; i < newArgs.Count; i++)
        {
            if (!ReferenceEquals(objA: newArgs[index: i], objB: type.TypeArguments[index: i]))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            return type;
        }

        // Get the generic definition and create a new instance with substituted args
        TypeSymbol? genericDef = type switch
        {
            RecordTypeSymbol r => r.GenericDefinition,
            EntityTypeSymbol e => e.GenericDefinition,
            ProtocolTypeSymbol p => p.GenericDefinition,
            _ => null
        };

        if (genericDef != null)
        {
            return genericDef.CreateInstance(typeArguments: newArgs);
        }

        // TupleTypeSymbol doesn't have a GenericDefinition — create a new tuple directly
        if (type is TupleTypeSymbol)
        {
            return new TupleTypeSymbol(elementTypes: newArgs);
        }

        return type;
    }

    /// <summary>
    /// Reports RF-S150 for each <c>needs &lt;param&gt; obeys P</c> constraint on a generic ROUTINE that the
    /// resolved explicit type arguments violate — the clean call-site diagnostic that replaces the
    /// over-prune codegen crash (the body's <c>x.duplicate()</c> etc. would otherwise be pruned when the
    /// arg fails the bound, surfacing as an "undefined symbol"). Uses the when-aware
    /// <see cref="ImplementsProtocol"/> so a conditional-conformance failure (e.g. <c>List[NonCopyable]</c>
    /// vs <c>T obeys Copyable</c>) is caught. Args still abstract (a nested generic param) are skipped —
    /// they are validated once fully concrete.
    /// </summary>
    internal void ValidateRoutineGenericConstraints(RoutineInfo routine, List<TypeSymbol> typeArgs,
        SourceLocation location)
    {
        ValidateNoTokenRoutineTypeArgument(routine: routine, typeArgs: typeArgs, location: location);
        if (routine.GenericConstraints is not { Count: > 0 } constraints ||
            routine.GenericParameters is not { Count: > 0 } genParams)
        {
            return;
        }

        foreach (GenericConstraintDeclaration c in constraints)
        {
            // A kind (`needs RecordType T`, `needs EntityType T`, ...) is checked the way a generic type's is.
            if (c.ConstraintType != ConstraintKind.Obeys)
            {
                int kindIdx = genParams.IndexOf(item: c.ParameterName);
                if (kindIdx >= 0 && kindIdx < typeArgs.Count &&
                    typeArgs[index: kindIdx] is not (GenericParameterTypeSymbol or ErrorTypeSymbol) and { } kindArg)
                {
                    _typeResolver.DispatchConstraintValidation(typeArg: kindArg, constraint: c, location: location);
                }

                continue;
            }

            if (c.ConstraintTypes == null)
            {
                continue;
            }

            int idx = genParams.IndexOf(item: c.ParameterName);
            if (idx < 0 || idx >= typeArgs.Count)
            {
                continue;
            }

            TypeSymbol arg = typeArgs[index: idx];
            if (arg is GenericParameterTypeSymbol)
            {
                continue;
            }

            foreach (TypeExpression protoExpr in c.ConstraintTypes.Where(predicate: pe =>
                         !ImplementsProtocol(type: arg, protocolName: pe.Name)))
            {
                ReportError(code: SemanticDiagnosticCode.ProtocolConstraintViolation,
                    message: ProtocolViolationMessage(type: arg, protocolName: protoExpr.Name,
                        parameterName: c.ParameterName),
                    location: location);
            }
        }

        ValidateConstraintTypeArguments(routine: routine, typeArgs: typeArgs, receiver: null, location: location);
    }

    /// <summary>
    /// Reports RF-S150 when a type argument obeys a constraint's protocol with other type arguments than the
    /// constraint names: <c>you: Iterable[S64]</c> (a hidden <c>__T0 obeys Iterable[S64]</c>) given a
    /// <c>List[Text]</c>, which obeys <c>Iterable[Text]</c>. The protocol's name alone matches, and the body
    /// built for it would read every <c>Text</c> as an <c>S64</c>. A constraint argument naming a type parameter
    /// reads it from <paramref name="typeArgs"/> or from the receiver <paramref name="receiver"/>'s type arguments
    /// (<c>List[T].add_range(you: Iterable[T])</c> on a <c>List[S64]</c>). Anything still generic, or a
    /// conformance the type does not declare with arguments, is left alone.
    /// </summary>
    internal void ValidateConstraintTypeArguments(RoutineInfo routine, List<TypeSymbol> typeArgs, TypeSymbol? receiver,
        SourceLocation location)
    {
        ValidateNoTokenRoutineTypeArgument(routine: routine, typeArgs: typeArgs, location: location);
        if (routine.GenericConstraints is not { Count: > 0 } constraints ||
            routine.GenericParameters is not { Count: > 0 } genParams)
        {
            return;
        }

        Dictionary<string, TypeSymbol> bindings = ReceiverTypeBindings(receiver: receiver);
        for (int i = 0; i < genParams.Count && i < typeArgs.Count; i++)
        {
            bindings[key: genParams[index: i]] = typeArgs[index: i];
        }

        foreach (GenericConstraintDeclaration c in constraints)
        {
            int idx = genParams.IndexOf(item: c.ParameterName);
            if (c.ConstraintType != ConstraintKind.Obeys || c.ConstraintTypes is not { } protocols || idx < 0 ||
                idx >= typeArgs.Count || typeArgs[index: idx] is GenericParameterTypeSymbol or ErrorTypeSymbol)
            {
                continue;
            }

            TypeSymbol arg = typeArgs[index: idx];
            TypeSymbol conformer = Declaration.RoamedTransparency.SharedEntityOf(type: arg);
            foreach (TypeExpression protoExpr in protocols)
            {
                if (protoExpr.GenericArguments is not { Count: > 0 } expectedArgs ||
                    ImplementedProtocolsOf(type: conformer)
                       .FirstOrDefault(predicate: p => p.BareName == protoExpr.Name) is not
                        { TypeArguments: { } actualArgs } conformance ||
                    actualArgs.Count != expectedArgs.Count)
                {
                    continue;
                }

                for (int k = 0; k < expectedArgs.Count; k++)
                {
                    TypeSymbol? expected = ConstraintArgument(expression: expectedArgs[index: k], bindings: bindings);
                    if (expected is null or GenericParameterTypeSymbol or ErrorTypeSymbol
                            or AssociatedProjectionTypeSymbol ||
                        actualArgs[index: k] is GenericParameterTypeSymbol or ErrorTypeSymbol
                            or AssociatedProjectionTypeSymbol || actualArgs[index: k].Name == expected.Name)
                    {
                        continue;
                    }

                    string wanted = $"{protoExpr.Name}[{string.Join(separator: ", ", values: expectedArgs.Select(
                        selector: e => ConstraintArgument(expression: e, bindings: bindings) is { } b
                            ? ShortTypeName(type: b)
                            : e.Name))}]";
                    string given = ShortTypeName(type: conformer);
                    ReportError(code: SemanticDiagnosticCode.ProtocolConstraintViolation,
                        message: $"You passed a '{given}' to '{routine.Name}', which takes a value that obeys " +
                                 $"'{wanted}' here. A '{given}' obeys '{ShortTypeName(type: conformance)}', and " +
                                 $"'{ShortTypeName(type: actualArgs[index: k])}' is not " +
                                 $"'{ShortTypeName(type: expected)}'. Pass a value whose type obeys '{wanted}'.",
                        location: location);
                    break;
                }
            }
        }
    }

    /// <summary>The receiver's type arguments by the names of its type's parameters (<c>T</c> to <c>S64</c> for a
    /// <c>List[S64]</c> receiver). Empty without a receiver or for a receiver that is not generic.</summary>
    private static Dictionary<string, TypeSymbol> ReceiverTypeBindings(TypeSymbol? receiver)
    {
        var bindings = new Dictionary<string, TypeSymbol>(comparer: StringComparer.Ordinal);
        if (receiver == null)
        {
            return bindings;
        }

        TypeSymbol ownerValue = Declaration.RoamedTransparency.SharedEntityOf(type: receiver);
        TypeSymbol? ownerDef = (ownerValue as EntityTypeSymbol)?.GenericDefinition ??
                               (ownerValue as RecordTypeSymbol)?.GenericDefinition as TypeSymbol;
        if (ownerDef?.GenericParameters is { } ownerParams && ownerValue.TypeArguments is { } ownerArgs)
        {
            for (int i = 0; i < ownerParams.Count && i < ownerArgs.Count; i++)
            {
                bindings[key: ownerParams[index: i]] = ownerArgs[index: i];
            }
        }

        return bindings;
    }

    /// <summary>
    /// The type a collection literal argument is built for when its parameter is one of the routine's own type
    /// parameters bound by a protocol: <c>you: Iterable[T]</c> (a hidden <c>__T0 obeys Iterable[T]</c>) on a
    /// <c>List[U8]</c> receiver gives <c>Iterable[U8]</c>, so <c>l.add_range(you: [3, 4])</c> builds its elements as
    /// <c>U8</c>, the way a bare number takes the type it goes to. Null when the argument is no collection literal,
    /// the parameter is not such a type parameter, or the protocol's arguments are not all known here.
    /// </summary>
    private TypeSymbol? CollectionLiteralConstraintType(RoutineInfo routine, TypeSymbol paramType,
        Expression argument, TypeSymbol? receiver, IReadOnlyList<TypeSymbol?>? boundTypeArgs = null)
    {
        if (UnwrapNamedArgument(argument: argument) is not (ListLiteralExpression { ArrayLength: null } or
                SetLiteralExpression or DictLiteralExpression) ||
            UnwrapCollectionLiteralExpectedType(type: paramType) is not GenericParameterTypeSymbol parameter ||
            routine.GenericParameters is not { } genParams || !genParams.Contains(item: parameter.Name) ||
            routine.GenericConstraints is not { } constraints)
        {
            return null;
        }

        Dictionary<string, TypeSymbol> bindings = ReceiverTypeBindings(receiver: receiver);
        if (boundTypeArgs != null)
        {
            for (int i = 0; i < genParams.Count && i < boundTypeArgs.Count; i++)
            {
                if (boundTypeArgs[index: i] is { } bound)
                {
                    bindings[key: genParams[index: i]] = bound;
                }
            }
        }

        foreach (GenericConstraintDeclaration c in constraints)
        {
            if (c.ParameterName != parameter.Name || c.ConstraintType != ConstraintKind.Obeys ||
                c.ConstraintTypes is not { } protocols)
            {
                continue;
            }

            foreach (TypeExpression protoExpr in protocols)
            {
                if (protoExpr.GenericArguments is { Count: > 0 } &&
                    ConstraintTypeOf(expression: protoExpr, bindings: bindings) is ProtocolTypeSymbol resolved)
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    /// <summary>The whole type a constraint names, its type arguments read through <paramref name="bindings"/>
    /// (<c>Iterable[DictEntry[K, V]]</c>). Null when a part is unknown or still a type parameter.</summary>
    private TypeSymbol? ConstraintTypeOf(TypeExpression expression, Dictionary<string, TypeSymbol> bindings)
    {
        if (expression.GenericArguments is not { Count: > 0 } arguments)
        {
            return ConstraintArgument(expression: expression, bindings: bindings) is
                { } leaf and not (GenericParameterTypeSymbol or ErrorTypeSymbol)
                ? leaf
                : null;
        }

        if (LookupTypeWithImports(name: expression.Name) is not { } definition)
        {
            return null;
        }

        var resolvedArguments = new List<TypeSymbol>(capacity: arguments.Count);
        foreach (TypeExpression argument in arguments)
        {
            if (ConstraintTypeOf(expression: argument, bindings: bindings) is not { } resolvedArgument)
            {
                return null;
            }

            resolvedArguments.Add(item: resolvedArgument);
        }

        return _registry.GetOrCreateResolution(genericDef: definition, typeArguments: resolvedArguments);
    }

    /// <summary>A type's name with its type arguments, each without its module (<c>List[Text]</c>).</summary>
    private static string ShortTypeName(TypeSymbol type)
    {
        return type.TypeArguments is { Count: > 0 } arguments
            ? $"{type.BareName}[{string.Join(separator: ", ", values: arguments.Select(selector: ShortTypeName))}]"
            : type.BareName;
    }

    /// <summary>The type a constraint's protocol argument names: a type parameter's binding, or a type written
    /// out (<c>S64</c> in <c>Iterable[S64]</c>). Null for anything else (a nested type, a name not known here).
    /// </summary>
    private TypeSymbol? ConstraintArgument(TypeExpression expression, Dictionary<string, TypeSymbol> bindings)
    {
        if (expression.GenericArguments is { Count: > 0 })
        {
            return null;
        }

        return bindings.TryGetValue(key: expression.Name, value: out TypeSymbol? bound)
            ? bound
            : LookupTypeWithImports(name: expression.Name) is { Category: not TypeCategory.Protocol } named
                ? named
                : null;
    }

    /// <summary>
    /// Reports RF-S150 when a resolved MEMBER routine's owner-level <c>needs &lt;param&gt; obeys P</c>
    /// constraints are violated by the concrete receiver's bound type args — e.g.
    /// <c>List[Widget].duplicate()</c> where <c>List.duplicate</c> carries <c>needs T obeys Copyable</c> but
    /// <c>Widget</c> is not Copyable. Without this the call resolves, a body-less concrete instance is
    /// referenced, and codegen trips the "declared+called but never defined" over-prune crash instead of a
    /// clean diagnostic. Permissive on anything unevaluable (non-generic receiver, missing def params).
    /// </summary>
    internal void ValidateMemberOwnerConstraints(RoutineInfo memberRoutine, TypeSymbol ownerType,
        SourceLocation location)
    {
        // Owner-level `needs param obeys P` can only exist on a GENERIC receiver; a non-generic receiver
        // (no bound type args) has nothing to check. Short-circuit here so the common member call never
        // pays the def-method lookup below — this runs on EVERY member call, so the guard is load-bearing
        // for compile speed (variant-body analysis touches thousands of member calls).
        if (ownerType.TypeArguments is not { Count: > 0 })
        {
            return;
        }

        List<GenericConstraintDeclaration>? constraints =
            ResolveMemberOwnerConstraints(memberRoutine: memberRoutine, ownerType: ownerType);
        if (constraints is not { Count: > 0 })
        {
            return;
        }

        TypeSymbol? ownerDef = (ownerType as EntityTypeSymbol)?.GenericDefinition ??
                               (ownerType as RecordTypeSymbol)?.GenericDefinition as TypeSymbol;
        List<string>? paramNames =
            (ownerDef ?? ownerType).GenericParameters ?? ownerType.GenericParameters;
        List<TypeSymbol>? args = ownerType.TypeArguments;
        if (paramNames is null || args is null)
        {
            return;
        }

        var subs = new Dictionary<string, TypeSymbol>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < paramNames.Count && i < args.Count; i++)
        {
            subs[key: paramNames[index: i]] = args[index: i];
        }

        CheckOwnerConstraintViolations(constraints: constraints,
            subs: subs,
            ownerType: ownerType,
            memberRoutine: memberRoutine,
            location: location);
    }

    /// <summary>
    /// A value aggregate that holds an entity (a bundle instance, a tuple, an Array of entities) is duplicated
    /// member by member, so it has a <c>duplicate</c> only when every member is Copyable (RF-S150 otherwise). Its
    /// derive's <c>needs T obeys Copyable</c> is about the receiver itself, which the owner-argument check above
    /// does not see.
    /// </summary>
    private void ValidateOwningValueDuplicate(RoutineInfo memberRoutine, TypeSymbol ownerType, SourceLocation location)
    {
        if (memberRoutine.Name != "duplicate" || ownerType is not RecordTypeSymbol record ||
            ownerType is VariantTypeSymbol || !_registry.IsEntityKind(type: ownerType))
        {
            return;
        }

        if (record.MemberVariables.FirstOrDefault(predicate: m =>
                !ImplementsProtocol(type: m.Type, protocolName: "Copyable")) is { } held)
        {
            ReportError(code: SemanticDiagnosticCode.ProtocolConstraintViolation,
                message: $"'{ownerType.Name}' cannot be duplicated: it is copied member by member, and its member " +
                         $"'{held.Name}' is '{held.Type.Name}', which is not Copyable. Declare '{held.Type.Name}' " +
                         "'obeys Copyable', or hand the value over with 'steal' instead of duplicating it.",
                location: location);
        }
    }

    /// <summary>
    /// Resolves the effective generic constraints for a member routine call: returns the routine's own
    /// constraints when present, or looks them up on the generic definition of the owner type.
    /// </summary>
    private List<GenericConstraintDeclaration>? ResolveMemberOwnerConstraints(
        RoutineInfo memberRoutine, TypeSymbol ownerType)
    {
        TypeSymbol? ownerDef = (ownerType as EntityTypeSymbol)?.GenericDefinition ??
                               (ownerType as RecordTypeSymbol)?.GenericDefinition as TypeSymbol;
        List<GenericConstraintDeclaration>? constraints = memberRoutine.GenericConstraints;
        if (constraints is not { Count: > 0 } && ownerDef != null)
        {
            constraints = _registry.LookupMemberRoutine(type: ownerDef,
                                        memberRoutineName: memberRoutine.Name)
                                  ?.GenericConstraints;
        }

        return constraints;
    }

    /// <summary>
    /// Checks each applicable obeys-constraint against the concrete type argument substitution map
    /// and reports a protocol-constraint violation for each failing protocol.
    /// </summary>
    private void CheckOwnerConstraintViolations(List<GenericConstraintDeclaration> constraints,
        Dictionary<string, TypeSymbol> subs, TypeSymbol ownerType, RoutineInfo memberRoutine,
        SourceLocation location)
    {
        foreach (GenericConstraintDeclaration c in constraints)
        {
            if (c.ConstraintType != ConstraintKind.Obeys || c.ConstraintTypes == null)
            {
                continue;
            }

            if (!subs.TryGetValue(key: c.ParameterName, value: out TypeSymbol? actual) ||
                actual is GenericParameterTypeSymbol)
            {
                continue;
            }

            foreach (TypeExpression protoExpr in c.ConstraintTypes.Where(predicate: pe =>
                         !ImplementsProtocol(type: actual, protocolName: pe.Name)))
            {
                ReportError(code: SemanticDiagnosticCode.ProtocolConstraintViolation,
                    message:
                    $"'{ownerType.Name}.{memberRoutine.Name}' requires '{c.ParameterName} " +
                    $"obeys {protoExpr.Name}', but '{actual.Name}' does not.",
                    location: location);
            }
        }
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> implements the named protocol.
    /// Checks explicit protocol declarations, parent protocol chains, and structural conformance
    /// (i.e., whether the type has all required memberRoutines of the protocol).
    /// </summary>
    internal bool ImplementsProtocol(TypeSymbol type, string protocolName)
    {
        // Get the protocol type. Parameterised protocol names like "Controlling[List[S64]]"
        // won't resolve directly (only the generic definition is registered) — strip brackets
        // and fall back to the base name so conformance checks see the protocol either way.
        TypeSymbol? protocol = LookupTypeWithImports(name: protocolName);
        if (protocol is not { Category: TypeCategory.Protocol } &&
            protocolName.Contains(value: '['))
        {
            string baseProtocolName = BareTypeName(typeName: protocolName);
            protocol = LookupTypeWithImports(name: baseProtocolName);
        }

        if (protocol is not { Category: TypeCategory.Protocol })
        {
            return false;
        }

        // Type-category protocols are satisfied by category membership itself: a record obeys
        // RecordType by BEING a record — no declaration needed (their parent protocols like
        // Equatable/Hashable are auto-derived for these categories). Without this, a
        // `needs M obeys RecordType` constraint can only be met by the wrong-spelling
        // workaround `M is RecordType`.
        if (MatchesCategoryProtocol(type: type, protocolName: protocolName))
        {
            return true;
        }

        // Generic parameter: check current routine/owner type constraints for obeys declarations.
        // e.g., needs T obeys Equatable means T satisfies Equatable inside this routine's body.
        if (type is GenericParameterTypeSymbol)
        {
            return GenericParamObeysConstraint(type: type, protocolName: protocolName);
        }

        // DECLARED conformance (own ImplementedProtocols + their parent chain, gated by `when`, plus the
        // reflexive marker-protocol rule) is decided by the single registry authority — no duplicate walk
        // here. A positive result covers e.g. `List[NonCopyable]` NOT satisfying `T obeys Copyable`
        // (RF-S150), and Viewing/Modifying/value types satisfying Accessing/Controlling.
        if (_registry.TypeObeysProtocol(type: type, protocolName: protocolName))
        {
            return true;
        }

        // A record or entity has `==` and a hash from the builder's conferral (answered above: its member variables
        // all have them, an entity's hash only by `obeys Hashable`) or from its own `eq` / keyed `hash`. The
        // library's field walk for every type (`routine T.eq`, `routine T.hash`) doesn't count by itself.
        if (ValueProtocolByOwnRoutine(type: type, protocolName: protocolName) is { } byOwnRoutine)
        {
            return byOwnRoutine;
        }

        // STRUCTURAL conformance (the type has all of the protocol's required member routines) is the
        // SA-only fallback the registry cannot compute.
        List<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeSymbol record => record.ImplementedProtocols,
            EntityTypeSymbol entity => entity.ImplementedProtocols,
            _ => null
        };
        if (implementedProtocols != null && protocol is ProtocolTypeSymbol protoType)
        {
            return ImplementsProtocolStructurally(type: type,
                protoType: protoType,
                protocolName: protocolName,
                implementedProtocols: implementedProtocols);
        }

        return false;
    }

    /// <summary>For <c>Equatable</c> / <c>Hashable</c> asked of a record or entity (or the handle a program's entity
    /// is held by) that the builder did not confer it on: whether the type writes its own <c>eq</c> / keyed
    /// <c>hash</c>. Null for any other protocol or type.</summary>
    private bool? ValueProtocolByOwnRoutine(TypeSymbol type, string protocolName)
    {
        if (protocolName is not ("Equatable" or "Hashable") ||
            (_registry.EntityOfHandleTakenAsMe(type: type) ?? type) is not { } owner ||
            !Declaration.ProtocolConformanceAnalyzer.IsValueProtocolCandidate(type: owner))
        {
            return null;
        }

        TypeSymbol definition = owner switch
        {
            RecordTypeSymbol { GenericDefinition: { } recordDef } => recordDef,
            EntityTypeSymbol { GenericDefinition: { } entityDef } => entityDef,
            _ => owner
        };
        return _registry.WritesOwnValueRoutine(type: definition, protocolName: protocolName);
    }

    /// <summary>
    /// Returns true when <paramref name="protocolName"/> is a type-category protocol
    /// (RecordType/EntityType/…) that <paramref name="type"/> satisfies by category membership.
    /// Extracted from <see cref="ImplementsProtocol"/>.
    /// </summary>
    private static bool MatchesCategoryProtocol(TypeSymbol type, string protocolName)
    {
        return protocolName switch
        {
            "RecordType" => type.Category == TypeCategory.Record,
            "EntityType" => type.Category == TypeCategory.Entity,
            "ChoiceType" => type.Category == TypeCategory.Choice,
            "VariantType" => type.Category == TypeCategory.Variant,
            "FlagsType" => type.Category == TypeCategory.Flags,
            _ => false
        };
    }

    /// <summary>Whether the generic parameter <paramref name="type"/> has the kind constraint <paramref name="kind"/>
    /// (<c>needs EntityType T</c>) on the current routine or its owner type.</summary>
    private bool GenericParamHasKind(TypeSymbol type, ConstraintKind kind)
    {
        return (_currentRoutine?.GenericConstraints ?? [])
              .Concat(second: _currentRoutine?.OwnerType?.GenericConstraints ?? [])
              .Any(predicate: c => c.ParameterName == type.Name && c.ConstraintType == kind);
    }

    /// <summary>
    /// Returns true when the generic-parameter <paramref name="type"/> obeys
    /// <paramref name="protocolName"/> via an active <c>obeys</c> constraint on the current routine or
    /// its owner type. Extracted from <see cref="ImplementsProtocol"/>.
    /// </summary>
    private bool GenericParamObeysConstraint(TypeSymbol type, string protocolName)
    {
        if (_currentRoutine?.GenericConstraints != null && _currentRoutine.GenericConstraints.Any(
                predicate: c =>
                    c.ParameterName == type.Name &&
                    c is { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null } &&
                    c.ConstraintTypes.Any(predicate: ct => ct.Name == protocolName)))
        {
            return true;
        }

        TypeSymbol? ownerType = _currentRoutine?.OwnerType;
        if (ownerType?.GenericConstraints == null)
        {
            return false;
        }

        return ownerType.GenericConstraints.Any(predicate: c =>
            c.ParameterName == type.Name &&
            c is { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null } &&
            c.ConstraintTypes.Any(predicate: ct => ct.Name == protocolName));
    }

    /// <summary>
    /// Handles the structural-conformance tail of <see cref="ImplementsProtocol"/>: the implicit
    /// entity Accessing/Controlling satisfaction, the transparent readonly relay through a wrapper's
    /// inner type, and the final member-routine structural check.
    /// </summary>
    private bool ImplementsProtocolStructurally(TypeSymbol type, ProtocolTypeSymbol protoType,
        string protocolName, List<TypeSymbol> implementedProtocols)
    {
        // A handle or token over a value (`Retained[T]`, `Viewing[T]`, ...) satisfies any read-only protocol its
        // value satisfies: every member routine of such a protocol only reads, and the handle forwards to the value.
        if (IsAllReadOnlyProtocol(protocol: protoType) && HandleInnerType(type: type) is { } innerT &&
            ImplementsProtocol(type: innerT, protocolName: protocolName))
        {
            return true;
        }

        return CheckStructuralConformance(type: type, protocol: protoType);
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> explicitly declares conformance to the named protocol
    /// via <c>obeys</c>. Unlike <see cref="ImplementsProtocol"/>, this does NOT fall back to
    /// structural conformance, making it suitable for marker protocols like ConstCompatible.
    /// </summary>
    internal bool ExplicitlyImplementsProtocol(TypeSymbol type, string protocolName)
    {
        List<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeSymbol record => record.ImplementedProtocols,
            EntityTypeSymbol entity => entity.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null)
        {
            return false;
        }

        return implementedProtocols.Any(predicate: implemented =>
            implemented.Name == protocolName || implemented.BareName == protocolName ||
            implemented is ProtocolTypeSymbol proto &&
            CheckParentProtocols(proto: proto, targetName: protocolName));
    }

    /// <summary>
    /// Checks if any parent protocol matches the target.
    /// </summary>
    internal bool CheckParentProtocols(ProtocolTypeSymbol proto, string targetName)
    {
        foreach (ProtocolTypeSymbol parent in proto.ParentProtocols)
        {
            if (parent.Name == targetName || parent.BareName == targetName)
            {
                return true;
            }

            // Re-lookup parent from registry to get the latest version with populated ParentProtocols,
            // since immutable type updates may leave stale references in the hierarchy.
            ProtocolTypeSymbol latestParent = parent;
            if (parent.ParentProtocols.Count == 0)
            {
                TypeSymbol? looked = _registry.LookupType(name: parent.Name);
                if (looked is ProtocolTypeSymbol latest)
                {
                    latestParent = latest;
                }
            }

            if (CheckParentProtocols(proto: latestParent, targetName: targetName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a type structurally conforms to a protocol by having all required memberRoutines.
    /// </summary>
    private bool CheckStructuralConformance(TypeSymbol type, ProtocolTypeSymbol protocol)
    {
        // Marker protocols (no memberRoutines) require explicit conformance — never structurally satisfied
        if (protocol.MemberRoutines.Count == 0)
        {
            return false;
        }

        foreach (ProtocolMemberRoutineInfo requiredMemberRoutine in protocol.MemberRoutines)
        {
            // Skip memberRoutines with default implementations
            if (requiredMemberRoutine.HasDefaultImplementation)
            {
                continue;
            }

            // Look for the memberRoutine on the type
            RoutineInfo? typeMemberRoutine = _registry.LookupMemberRoutine(type: type,
                memberRoutineName: requiredMemberRoutine.Name);
            if (typeMemberRoutine == null)
            {
                // memberRoutine names are bare; the failable `!` is a structured flag. Retry matching a
                // same-named failable implementation via the isFailable filter.
                if (requiredMemberRoutine.IsFailable)
                {
                    typeMemberRoutine = _registry.LookupMemberRoutine(type: type,
                        memberRoutineName: requiredMemberRoutine.Name,
                        isFailable: true);
                }

                if (typeMemberRoutine == null)
                {
                    return false;
                }
            }

            // Verify memberRoutine signature matches (basic check)
            if (!memberRoutineSignatureMatches(typeMemberRoutine: typeMemberRoutine,
                    protoMemberRoutine: requiredMemberRoutine))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The error for a type that does not obey a protocol a place asks for: which routine of the protocol it lacks, or
    /// how its routine of that name differs. A parameter written with a protocol as its type (`shape: Shape`) is
    /// generic in a hidden type parameter, which the message does not name.
    /// </summary>
    internal string ProtocolViolationMessage(TypeSymbol type, string protocolName, string parameterName)
    {
        string where = parameterName.StartsWith(value: "__", comparisonType: StringComparison.Ordinal)
            ? ""
            : $" (required of '{parameterName}')";
        TypeSymbol owner = _registry.EntityOfHandleTakenAsMe(type: type) ?? type;
        if (protocolName == "Hashable" && owner is EntityTypeSymbol &&
            ValueProtocolByOwnRoutine(type: type, protocolName: protocolName) == false)
        {
            return $"'{owner.Name}' is an entity without 'obeys Hashable'{where}, so it can't be a Set element or a " +
                   $"Dict key: its member variables can change while the Set or Dict holds it. Add 'obeys Hashable' " +
                   $"under 'entity {owner.Name}' to hash it by its member variables, and leave them unchanged while it is a key.";
        }

        if (protocolName is "Equatable" or "Hashable" &&
            MemberVariableWithout(type: type, protocolName: protocolName) is ({ } holder, { } missing))
        {
            (string what, string routine) = protocolName == "Equatable"
                ? ("'=='", $"eq(you: {holder.Name}) -> Bool")
                : ("hash", "hash(k0: U64, k1: U64) -> U64");
            string fieldType = missing.Type is EntityTypeSymbol entityField && protocolName == "Hashable"
                ? $"'{entityField.BareName}', an entity without 'obeys Hashable'"
                : $"'{MemberTypeName(type: missing.Type)}', which has no {what}";
            return $"'{holder.Name}' has no {what}{where}: its member variable '{missing.Name}' is a {fieldType}. " +
                   $"Write 'routine {holder.Name}.{routine}' to say " +
                   (protocolName == "Equatable" ? "when two are equal." : "what it hashes.");
        }

        return $"'{type.Name}' does not obey '{protocolName}'{where}{ProtocolShortfall(type: type, protocolName: protocolName)}";
    }

    /// <summary>": it has no 'area() -> Decimal'." style ending naming the first protocol routine
    /// <paramref name="type"/> lacks or writes differently, or "." when there is no single one to name.</summary>
    private string ProtocolShortfall(TypeSymbol type, string protocolName)
    {
        if (LookupTypeWithImports(name: protocolName) is not ProtocolTypeSymbol protocol)
        {
            return ".";
        }

        foreach (ProtocolMemberRoutineInfo required in protocol.MemberRoutines.Where(predicate: m =>
                     !m.HasDefaultImplementation && m.IsInstanceMemberRoutine))
        {
            string wanted = $"{required.Name}({string.Join(separator: ", ", values: required.ParameterNames.Zip(
                second: required.ParameterTypes, resultSelector: (n, t) => $"{n}: {ShowProtocolType(type: t, owner: type)}"))})" +
                            (required.ReturnType is { } r ? $" -> {ShowProtocolType(type: r, owner: type)}" : "");
            RoutineInfo? own = _registry.LookupMemberRoutine(type: type, memberRoutineName: required.Name) ??
                               _registry.LookupMemberRoutine(type: type, memberRoutineName: required.Name,
                                   isFailable: true);
            if (own == null || own.OwnerType is ProtocolTypeSymbol)
            {
                return $": it has no routine '{wanted}'. Write 'routine {type.Name}.{wanted}'.";
            }

            if (!memberRoutineSignatureMatches(typeMemberRoutine: own, protoMemberRoutine: required))
            {
                string has = $"{own.Name}({string.Join(separator: ", ", values: own.Parameters.Where(predicate: p => p.Name != "me").Select(selector: p => $"{p.Name}: {p.Type.Name}"))})" +
                             (own.ReturnType is { } ownReturn ? $" -> {ownReturn.Name}" : "");
                return $": its '{has}' does not match the protocol's '{wanted}'.";
            }
        }

        return ".";
    }

    /// <summary>A protocol signature's type as the implementer would write it: `Me` is the implementer.</summary>
    private static string ShowProtocolType(TypeSymbol type, TypeSymbol owner)
    {
        return type is ProtocolSelfTypeSymbol ? owner.Name : type.Name;
    }

    /// <summary>
    /// Checks if a type's memberRoutine signature matches a protocol memberRoutine signature.
    /// </summary>
    private bool memberRoutineSignatureMatches(RoutineInfo typeMemberRoutine,
        ProtocolMemberRoutineInfo protoMemberRoutine)
    {
        // Failability is not part of the contract (the caller recovers with `try` / `check`).

        // Check parameter count (excluding 'me' parameter if present)
        // In-body memberRoutines have explicit 'me' as first parameter
        // Extension memberRoutines don't include 'me' in the parameter list
        int expectedParamCount = protoMemberRoutine.ParameterTypes.Count;
        bool hasMeParam = typeMemberRoutine.Parameters.Count > 0 &&
                          typeMemberRoutine.Parameters[index: 0].Name == "me";
        int actualParamCount = typeMemberRoutine.Parameters.Count - (hasMeParam
            ? 1
            : 0);

        if (actualParamCount != expectedParamCount)
        {
            return false;
        }

        if (!MemberRoutineParameterTypesMatch(typeMemberRoutine: typeMemberRoutine,
                protoMemberRoutine: protoMemberRoutine,
                expectedParamCount: expectedParamCount,
                hasMeParam: hasMeParam))
        {
            return false;
        }

        return MemberRoutineReturnTypeMatches(typeMemberRoutine: typeMemberRoutine,
            protoMemberRoutine: protoMemberRoutine);
    }

    /// <summary>
    /// Checks that each of the type memberRoutine's parameter types (skipping 'me' when present)
    /// matches the corresponding protocol parameter type. Extracted from
    /// <see cref="memberRoutineSignatureMatches"/>.
    /// </summary>
    private static bool MemberRoutineParameterTypesMatch(RoutineInfo typeMemberRoutine,
        ProtocolMemberRoutineInfo protoMemberRoutine, int expectedParamCount, bool hasMeParam)
    {
        // Check parameter types - skip 'me' if present
        int startIndex = hasMeParam
            ? 1
            : 0;
        for (int i = 0; i < expectedParamCount; i++)
        {
            TypeSymbol expectedType = protoMemberRoutine.ParameterTypes[index: i];
            TypeSymbol actualType = typeMemberRoutine.Parameters[index: startIndex + i].Type;

            // `Viewing[Me]` (a parameter the routine only reads): `Viewing[Owner]` or the bare owner both fit.
            // Which one an entity must spell is the signature check's call (RF-S703).
            if (expectedType is { TypeArguments: [ProtocolSelfTypeSymbol] } readsMe &&
                readsMe.BareName == Declaration.RuntimeContract.Viewing &&
                typeMemberRoutine.OwnerType is { } readOwner)
            {
                if (!TypesMatch(actual: actualType, expected: readOwner) &&
                    !Declaration.SignatureResolver.ReadsOwnerThroughToken(paramType: actualType, owner: readOwner))
                {
                    return false;
                }
            }
            // Handle protocol self type (Me) - should match the implementing type
            else if (expectedType is ProtocolSelfTypeSymbol)
            {
                // 'Me' in protocol should match the owner type of the memberRoutine
                if (typeMemberRoutine.OwnerType != null && !TypesMatch(actual: actualType,
                        expected: typeMemberRoutine.OwnerType))
                {
                    return false;
                }
            }
            else if (!TypesMatch(actual: actualType, expected: expectedType))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks that the type memberRoutine's return type is compatible with the protocol
    /// memberRoutine's declared return type. Extracted from
    /// <see cref="memberRoutineSignatureMatches"/>.
    /// </summary>
    private bool MemberRoutineReturnTypeMatches(RoutineInfo typeMemberRoutine,
        ProtocolMemberRoutineInfo protoMemberRoutine)
    {
        // A return named as the implementer's own associated type (`Me/Item`) is whatever the implementer returns.
        if (protoMemberRoutine.ReturnType is AssociatedProjectionTypeSymbol { Base: ProtocolSelfTypeSymbol })
        {
            return typeMemberRoutine.ReturnType != null;
        }

        // Check return type (if specified)
        if (protoMemberRoutine.ReturnType != null && typeMemberRoutine.ReturnType != null)
        {
            if (!IsAssignableTo(source: typeMemberRoutine.ReturnType,
                    target: protoMemberRoutine.ReturnType))
            {
                return false;
            }
        }
        else if (protoMemberRoutine.ReturnType == null != (typeMemberRoutine.ReturnType == null))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true if all required memberRoutines in <paramref name="protocol"/> and its parent chain
    /// have <see cref="MutationCategory.Readonly"/> mutation. Marker protocols with no
    /// memberRoutines return false — they require explicit declaration, not relay.
    /// </summary>
    private bool IsAllReadOnlyProtocol(ProtocolTypeSymbol protocol)
    {
        if (protocol.MemberRoutines.Count == 0)
        {
            return false;
        }

        foreach (ProtocolMemberRoutineInfo memberRoutine in protocol.MemberRoutines)
        {
            if (memberRoutine.Mutation != MutationCategory.Readonly)
            {
                return false;
            }
        }

        foreach (ProtocolTypeSymbol parent in protocol.ParentProtocols)
        {
            // Re-lookup to get a fully-populated parent (same pattern as CheckParentProtocols).
            ProtocolTypeSymbol resolved = parent;
            if (_registry.LookupType(name: parent.Name) is ProtocolTypeSymbol latest)
            {
                resolved = latest;
            }

            if (!IsAllReadOnlyProtocol(protocol: resolved))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The value a handle or token is over (<c>T</c> of <c>Retained[T]</c>, <c>Viewing[T]</c>, ...), or
    /// null for any other type.</summary>
    private static TypeSymbol? HandleInnerType(TypeSymbol type)
    {
        return type.BareName is Declaration.RuntimeContract.Retained or Declaration.RuntimeContract.Roamed
                   or Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying &&
               type.TypeArguments is [{ } inner, ..]
            ? inner
            : null;
    }

    /// <summary>
    /// Checks if two types match for protocol signature comparison.
    /// </summary>
    private static bool TypesMatch(TypeSymbol actual, TypeSymbol expected)
    {
        // Exact name match
        if (actual.Name == expected.Name)
        {
            return true;
        }

        // A shared entity is taken and given as its handle, which stands for the entity itself.
        if (actual is RecordTypeSymbol
            {
                GenericDefinition.Name: Declaration.RuntimeContract.Roamed, TypeArguments: [{ } sharedEntity]
            })
        {
            return TypesMatch(actual: sharedEntity, expected: expected);
        }

        // Handle ProtocolSelfTypeSymbol in expected position
        if (expected is ProtocolSelfTypeSymbol)
        {
            // 'Me' matches the owner type - handled by caller
            return true;
        }

        // Handle generic resolutions
        if (expected.IsGenericDefinition && actual.IsGenericResolution)
        {
            string baseName = actual.BareName;
            if (baseName == expected.Name)
            {
                return true;
            }
        }

        return false;
    }
}
