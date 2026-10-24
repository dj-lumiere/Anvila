using Builder.Verification;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// Handles implicit marker protocol conformance for the semantic analyzer.
/// </summary>
internal sealed class ProtocolConformanceAnalyzer
{
    private const string CopyableProtocol = "Copyable";

    private const string EquatableProtocol = "Equatable";

    private const string HashableProtocol = "Hashable";

    private readonly SemanticVerifier _sa;

    internal ProtocolConformanceAnalyzer(SemanticVerifier sa)
    {
        _sa = sa;
    }

    #region Phase 4.2: Implicit Marker Protocol Conformance

    /// <summary>
    /// Automatically adds marker protocol conformance based on type category.
    /// Records implicitly conform to RecordType, entities to EntityType, etc.
    /// Also adds all transitive protocols from the marker's obeys chain.
    /// </summary>
    /// <summary>
    /// Confers the kind marker protocols on every choice and flags type (<c>ChoiceType</c> and what it obeys,
    /// <c>Equatable</c> and <c>Hashable</c> included). They depend on nothing but the kind, so they are in place before
    /// member variable types are resolved: a member variable of type <c>Set[Color]</c> checks <c>Color</c> against
    /// <c>Set</c>'s constraints then.
    /// </summary>
    internal void ApplyKindMarkerConformanceEarly()
    {
        foreach (TypeSymbol type in _sa._registry.GetTypesByCategory(category: TypeCategory.Choice)
                                     .Concat(second: _sa._registry.GetTypesByCategory(category: TypeCategory.Flags)))
        {
            ApplyMarkerConformanceForType(type: type);
        }
    }

    internal void ApplyImplicitMarkerConformance()
    {
        foreach (TypeSymbol type in _sa._registry.GetTypesWithMemberRoutines())
        {
            ApplyMarkerConformanceForType(type: type);
        }

        // SPLIT (2026-08-23) — structural vs semantic:
        // * Copyable is STRUCTURAL: placed on plain-bits values and cascaded over composition by the two passes
        //   below. Whether `=` copies a value is not a protocol at all: TypeRegistry.IsCopiedByAssignment
        //   decides it from the type's kind (a record owning no entity, not a token).
        // * Equatable is STRUCTURAL too: `==` compares values, so every record and entity whose member variables all
        //   have `==` gets it (ApplyAutoEquatableConformance), and a hand-written `eq` wins. Hashable is structural
        //   for records the same way, opt-in for entities (an entity's value changes under a Set/Dict that keys on it).
        // * Comparable is opt-in: an order is a choice. The everywhere-derive loop registers its derives ONLY for a
        //   type that explicitly declares `obeys Comparable`.
        ApplyAutoCopyableConformance();
        ApplyEverywhereConformance();
        ApplyAutoEquatableConformance();
    }

    /// <summary>
    /// Auto-adds the type-category marker protocol (RecordType/EntityType/…) plus all transitive
    /// protocols from the marker's obeys chain to a single type, tracking the implicitly-added
    /// conformances so validation skips them. No-op for generic definitions or types whose category
    /// has no marker.
    /// </summary>
    private void ApplyMarkerConformanceForType(TypeSymbol type)
    {
        // Skip generic definitions — their resolutions inherit conformance
        if (type.IsGenericDefinition)
        {
            return;
        }

        // Determine the marker protocol name for this type category
        string? markerName = TypeRegistry.KindMarkerProtocolName(type: type);

        if (markerName == null)
        {
            return;
        }

        TypeSymbol? markerType = _sa._registry.LookupType(name: markerName);
        if (markerType is not ProtocolTypeSymbol marker)
        {
            return;
        }

        // Collect all transitive protocols from the marker's obeys chain
        var transitiveProtocols = new List<TypeSymbol>();
        CollectTransitiveProtocols(protocol: marker, result: transitiveProtocols);

        // Merge with existing user-declared protocols
        List<TypeSymbol> existing = GetImplementedProtocols(type: type);
        var merged = new List<TypeSymbol>(collection: existing);

        // Add transitive protocols first, then the marker itself
        // Track implicitly-added protocols so validation skips them
        foreach (TypeSymbol proto in transitiveProtocols.Where(predicate: p =>
                     merged.All(predicate: e => e.Name != p.Name)))
        {
            merged.Add(item: proto);
            _sa._implicitProtocolConformances.Add(item: (type.FullName, proto.Name));
        }

        if (merged.All(predicate: p => p.Name != marker.Name))
        {
            merged.Add(item: marker);
            _sa._implicitProtocolConformances.Add(item: (type.FullName, marker.Name));
        }

        // Only update if we actually added something
        if (merged.Count > existing.Count)
        {
            UpdateTypeProtocols(type: type, protocols: merged);
        }
    }

    /// <summary>The everywhere-protocols this generic pass confers (value/memory semantics). Equatable
    /// and Hashable have their own pass (<see cref="ApplyAutoEquatableConformance"/>), and Comparable is opt-in.
    /// Keyed by bare protocol name.</summary>
    private static readonly HashSet<string> _autoConferredEverywhereProtocols =
        new(comparer: StringComparer.Ordinal) { CopyableProtocol };

    /// <summary>
    /// Generic <c>needs P everywhere</c> gate (④ standard-impl eligibility): for every protocol that declares
    /// an <c>everywhere</c> self-constraint, auto-derive conformance to any concrete member-bearing type all
    /// of whose members obey P (<see cref="TypeRegistry.EverywhereObeys"/>). This is NOT a per-protocol
    /// bespoke pass — it reads the rule from the stdlib protocol declaration, so Copyable (and later
    /// Equatable/…) opt in by writing <c>needs P everywhere</c> rather than growing new C# passes. The BASE
    /// case is the leaf types' own explicit/auto conformance (scalars bitwise, Text/Integer declare Copyable,
    /// <c>@llvm</c> aggregates cascade to their element via EverywhereObeys); this gate is the INDUCTIVE step
    /// over composition. Runs LAST so the leaf conformances are already in place for the member
    /// walk to see. A type already declaring P is skipped (idempotent).
    /// </summary>
    private void ApplyEverywhereConformance()
    {
        // Collect protocols carrying an `everywhere` self-constraint (subject `Me`, ConstraintKind.Everywhere).
        var everywhereProtocols = new List<ProtocolTypeSymbol>();
        foreach (TypeSymbol type in _sa._registry.GetAllTypes())
        {
            // Only Copyable is conferred here. Equatable and Hashable have their own pass, and Comparable carries
            // the same `needs P everywhere` self-constraint but is OPT-IN: its derive attaches only on an
            // explicit `obeys Comparable` (the everywhere-derive loop reads the declared conformance).
            if (type is ProtocolTypeSymbol proto &&
                ProtocolHasEverywhereSelfConstraint(proto: proto) &&
                _autoConferredEverywhereProtocols.Contains(item: (proto.GenericDefinition ?? proto)
                   .BareName))
            {
                everywhereProtocols.Add(item: proto);
            }
        }

        if (everywhereProtocols.Count == 0)
        {
            return;
        }

        foreach (ProtocolTypeSymbol proto in everywhereProtocols)
        {
            foreach (TypeSymbol type in _sa._registry.GetTypesWithMemberRoutines())
            {
                ApplyEverywhereConformanceForType(proto: proto, type: type);
            }
        }
    }

    /// <summary>
    /// Inductive step of the <c>needs P everywhere</c> gate for one (protocol, type) pair: confers
    /// <paramref name="proto"/> on <paramref name="type"/> when every member obeys it. No-op for
    /// generic definitions, entities (opt-in for Copyable), types already declaring the protocol, or
    /// types whose members do not all obey it.
    /// </summary>
    private void ApplyEverywhereConformanceForType(ProtocolTypeSymbol proto, TypeSymbol type)
    {
        if (type.IsGenericDefinition)
        {
            return;
        }

        // Entities stay OPT-IN for Copyable (STEP 4: "entity is NOT always copyable") — a simple
        // `entity Point{x,y}` must not silently become copyable. Entity auto-derive is a separate,
        // deliberate increment; this gate covers value composition (record/tuple/variant/…) only.
        if (type is EntityTypeSymbol)
        {
            return;
        }

        List<TypeSymbol> existing = GetImplementedProtocols(type: type);
        if (existing.Any(predicate: p => p.Name == proto.Name))
        {
            return;
        }

        if (!_sa._registry.EverywhereObeys(type: type, protocol: proto.Name))
        {
            return;
        }

        var merged = new List<TypeSymbol>(collection: existing) { proto };
        _sa._implicitProtocolConformances.Add(item: (type.FullName, proto.Name));
        UpdateTypeProtocols(type: type, protocols: merged);
    }

    /// <summary>
    /// True when a protocol declares an <c>everywhere</c> self-constraint (<c>needs P everywhere</c>, which
    /// the parser records as a <see cref="SyntaxTree.ConstraintKind.Everywhere"/> constraint with subject <c>Me</c> and
    /// the protocol's own name as the constraint target) — the opt-in that makes
    /// <see cref="ApplyEverywhereConformance"/> structurally cascade the protocol over composition.
    /// </summary>
    private static bool ProtocolHasEverywhereSelfConstraint(ProtocolTypeSymbol proto)
    {
        return proto.GenericConstraints is { } cs && cs.Any(predicate: c =>
            c.ConstraintType == SyntaxTree.ConstraintKind.Everywhere);
    }

    /// <summary>
    /// Places <c>Copyable</c> on every type whose layout holds no pointer: a bitwise duplicate is its deep copy
    /// (nothing on the heap is shared). Composites are reached afterwards by the <c>needs Copyable everywhere</c>
    /// cascade. Types that already declare <c>Copyable</c> are left as they are.
    /// </summary>
    private void ApplyAutoCopyableConformance()
    {
        if (_sa._registry.LookupType(name: CopyableProtocol) is not ProtocolTypeSymbol copyable)
        {
            return;
        }

        foreach (TypeSymbol type in _sa._registry.GetTypesWithMemberRoutines())
        {
            List<TypeSymbol> existing = GetImplementedProtocols(type: type);
            if (existing.Any(predicate: p => p.Name is CopyableProtocol))
            {
                continue;
            }

            if (type.IsGenericDefinition)
            {
                ApplyGenericCopyableConformance(type: type, existing: existing, copyable: copyable);
                continue;
            }

            if (!_sa._registry.CanAutoDeriveAssignable(type: type))
            {
                continue;
            }

            var merged = new List<TypeSymbol>(collection: existing) { copyable };
            _sa._implicitProtocolConformances.Add(item: (type.FullName, copyable.Name));
            UpdateTypeProtocols(type: type, protocols: merged);
        }
    }

    /// <summary>
    /// Places <c>Copyable</c> on a generic record or bundle DEFINITION (<c>Pair[T]</c>, <c>DictEntry[K, V]</c>) on the
    /// condition that each parameter a field holds is <c>Copyable</c> too (<c>Copyable when T obeys Copyable</c>). An
    /// instance made later folds in its definition's protocols, so it has <c>duplicate()</c> exactly when its
    /// arguments do. A field of another type must be <c>Copyable</c> itself, or the definition is left alone.
    /// </summary>
    private void ApplyGenericCopyableConformance(TypeSymbol type, List<TypeSymbol> existing, ProtocolTypeSymbol copyable)
    {
        if (type is not RecordTypeSymbol { MemberVariables: { Count: > 0 } fields } record ||
            type is VariantTypeSymbol || Wrappers.IsWrapperType(type: type))
        {
            return;
        }

        var conditions = new List<(string ParamName, string ProtocolName)>();
        foreach (MemberVariableInfo field in fields)
        {
            if (field.Type is GenericParameterTypeSymbol param)
            {
                if (!conditions.Any(predicate: c => c.ParamName == param.Name))
                {
                    conditions.Add(item: (param.Name, CopyableProtocol));
                }

                continue;
            }

            bool concrete = !field.Type.IsGenericDefinition &&
                            field.Type.TypeArguments?.Any(predicate: a => a is GenericParameterTypeSymbol) != true;
            if (!concrete || !(_sa._registry.CanAutoDeriveAssignable(type: field.Type) ||
                               _sa._registry.TypeObeysProtocol(type: field.Type, protocolName: CopyableProtocol)))
            {
                return;
            }
        }

        record.ConditionalObeys ??= new Dictionary<string, List<(string ParamName, string ProtocolName)>>(
            comparer: StringComparer.Ordinal);
        record.ConditionalObeys[key: CopyableProtocol] = conditions;
        var merged = new List<TypeSymbol>(collection: existing) { copyable };
        _sa._implicitProtocolConformances.Add(item: (type.FullName, copyable.Name));
        UpdateTypeProtocols(type: type, protocols: merged);
    }

    /// <summary>
    /// Places <c>Equatable</c> on every record and entity that has <c>==</c>, and <c>Hashable</c> on every record that
    /// hashes: one that writes its own <c>eq</c> / keyed <c>hash</c>, or one whose member variables all have it (the
    /// builder then writes the routine walking them in order). An entity hashes only when its declaration says
    /// <c>obeys Hashable</c>: its member variables can change while a Set or Dict keys on it. A generic definition
    /// gets the protocol on the condition that each parameter a member variable holds has it too
    /// (<c>Equatable when T obeys Equatable</c>). Runs to a fixed point, since a member variable's type may be one
    /// that only gets the protocol here. A type with no member variables, a variant, a handle or token, and a choice
    /// or flags type (its kind marker gives it both) are left as they are.
    /// </summary>
    private void ApplyAutoEquatableConformance()
    {
        ApplyAutoValueConformance(protocolName: EquatableProtocol, entities: true);
        ApplyAutoValueConformance(protocolName: HashableProtocol, entities: false);
    }

    private void ApplyAutoValueConformance(string protocolName, bool entities)
    {
        if (_sa._registry.LookupType(name: protocolName) is not ProtocolTypeSymbol protocol)
        {
            return;
        }

        List<TypeSymbol> candidates = _sa._registry.GetTypesWithMemberRoutines()
                                         .Where(predicate: t => IsValueProtocolCandidate(type: t) &&
                                                                (entities || t is not EntityTypeSymbol))
                                         .Where(predicate: t =>
                                              !_sa._registry.TypeObeysProtocol(type: t, protocolName: protocolName))
                                         .ToList();

        // A hand-written `eq` / keyed `hash` gives the protocol whatever the member variables are.
        foreach (TypeSymbol type in candidates.Where(predicate: t =>
                                                   _sa._registry.WritesOwnValueRoutine(type: t,
                                                       protocolName: protocolName))
                                              .ToList())
        {
            ConferValueProtocol(type: type, protocol: protocol, conditions: null);
            candidates.Remove(item: type);
        }

        bool changed = true;
        while (changed && candidates.Count > 0)
        {
            changed = false;
            foreach (TypeSymbol type in candidates.ToList())
            {
                List<(string ParamName, string ProtocolName)>? conditions =
                    ValueProtocolConditions(type: type, protocolName: protocolName);
                if (conditions == null)
                {
                    continue;
                }

                ConferValueProtocol(type: type, protocol: protocol,
                    conditions: type.IsGenericDefinition ? conditions : null);
                candidates.Remove(item: type);
                changed = true;
            }
        }
    }

    /// <summary>Whether <paramref name="type"/> is a kind that gets <c>==</c> (and, for a record, a hash) from its
    /// member variables: a plain record or an entity, not a variant, choice, flags, crashable, tuple, handle or
    /// token.</summary>
    internal static bool IsValueProtocolCandidate(TypeSymbol type)
    {
        return type switch
        {
            VariantTypeSymbol or ChoiceTypeSymbol or FlagsTypeSymbol or CrashableTypeSymbol or TupleTypeSymbol => false,
            // `Maybe[T]` keeps a value slot that holds nothing meaningful while it is absent, and `Unbroken[T]` is
            // read only through its atomic routines, so a walk over their member variables would compare or hash
            // what is not their value.
            RecordTypeSymbol record => !Wrappers.IsWrapperType(type: record) &&
                                       (record.GenericDefinition?.Name ?? record.Name) is var name &&
                                       !RuntimeContract.WrapperTypes.Contains(item: name) &&
                                       name is not (RuntimeContract.Maybe or RuntimeContract.Unbroken),
            EntityTypeSymbol => true,
            _ => false
        };
    }

    /// <summary>
    /// The conditions under which every member variable of <paramref name="type"/> has the protocol, or null when
    /// one has none. A concrete type has no conditions (an empty list). In a generic definition, a member variable of
    /// a parameter's type, or of a type built from parameters whose definition has the protocol, adds those
    /// parameters.
    /// </summary>
    private List<(string ParamName, string ProtocolName)>? ValueProtocolConditions(TypeSymbol type,
        string protocolName)
    {
        List<MemberVariableInfo> fields = type switch
        {
            RecordTypeSymbol r => r.MemberVariables,
            EntityTypeSymbol e => e.MemberVariables,
            _ => []
        };
        if (fields.Count == 0)
        {
            return null;
        }

        var conditions = new List<(string ParamName, string ProtocolName)>();
        return fields.All(predicate: field => MemberTypeHas(type: field.Type, protocolName: protocolName,
            genericDefinition: type.IsGenericDefinition, conditions: conditions))
            ? conditions
            : null;
    }

    /// <summary>
    /// Whether a member variable of type <paramref name="type"/> has the protocol. A type built from no generic
    /// parameter is asked directly. In a generic definition, a parameter adds itself to
    /// <paramref name="conditions"/>, and a type built from parameters (<c>List[T]</c>) needs its definition to
    /// have the protocol and each type argument to have it in turn.
    /// </summary>
    private bool MemberTypeHas(TypeSymbol type, string protocolName, bool genericDefinition,
        List<(string ParamName, string ProtocolName)> conditions)
    {
        var parameters = new List<string>();
        CollectGenericParameters(type: type, into: parameters);
        if (parameters.Count == 0)
        {
            return type.Category == TypeCategory.ConstGenericValue ||
                   _sa._registry.MemberVariableHasValueRoutine(type: type, protocolName: protocolName);
        }

        if (!genericDefinition)
        {
            return false;
        }

        if (type is GenericParameterTypeSymbol parameter)
        {
            if (conditions.All(predicate: c => c.ParamName != parameter.Name))
            {
                conditions.Add(item: (parameter.Name, protocolName));
            }

            return true;
        }

        TypeSymbol definition = type switch
        {
            RecordTypeSymbol { GenericDefinition: { } recordDef } => recordDef,
            EntityTypeSymbol { GenericDefinition: { } entityDef } => entityDef,
            _ => type
        };
        return !RuntimeContract.WrapperTypes.Contains(item: definition.Name) &&
               _sa._registry.TypeObeysProtocol(type: definition, protocolName: protocolName) &&
               (type.TypeArguments ?? []).All(predicate: argument => MemberTypeHas(type: argument,
                   protocolName: protocolName, genericDefinition: true, conditions: conditions));
    }

    /// <summary>The generic parameters <paramref name="type"/> is built from, in order.</summary>
    private static void CollectGenericParameters(TypeSymbol type, List<string> into)
    {
        if (type is GenericParameterTypeSymbol parameter)
        {
            if (!into.Contains(item: parameter.Name))
            {
                into.Add(item: parameter.Name);
            }

            return;
        }

        foreach (TypeSymbol argument in type.TypeArguments ?? [])
        {
            CollectGenericParameters(type: argument, into: into);
        }
    }

    /// <summary>Adds <paramref name="protocol"/> to <paramref name="type"/>'s protocols as one the builder conferred,
    /// under <paramref name="conditions"/> when it is a generic definition.</summary>
    private void ConferValueProtocol(TypeSymbol type, ProtocolTypeSymbol protocol,
        List<(string ParamName, string ProtocolName)>? conditions)
    {
        if (conditions is { Count: > 0 })
        {
            Dictionary<string, List<(string ParamName, string ProtocolName)>> map = type switch
            {
                RecordTypeSymbol r => r.ConditionalObeys ??=
                    new Dictionary<string, List<(string ParamName, string ProtocolName)>>(
                        comparer: StringComparer.Ordinal),
                EntityTypeSymbol e => e.ConditionalObeys ??=
                    new Dictionary<string, List<(string ParamName, string ProtocolName)>>(
                        comparer: StringComparer.Ordinal),
                _ => throw new InvalidOperationException(message: $"'{type.Name}' carries no protocols")
            };
            map[key: protocol.Name] = conditions;
        }

        var merged = new List<TypeSymbol>(collection: GetImplementedProtocols(type: type)) { protocol };
        _sa._implicitProtocolConformances.Add(item: (type.FullName, protocol.Name));
        UpdateTypeProtocols(type: type, protocols: merged);
    }

    // RC wrappers (Retained/Tracked/Guarded/Witnessed/Roamed) now permit `var b = rc` (2026-09-21): an RC
    // copy is an implicit `share` (refcount bump), release is automatic (`destruct` at teardown IS the
    // decrement). This is enforced by the COPY-POLICY path — the analyzer no longer lists them in
    // `NonTriviallyAssignableWrappers`, and the retaining copy is injected via `ResolveStoreHook` (→ `share`).
    // No `Assignable` protocol stamp is needed on the wrappers themselves for this. Thread-boundary crossing
    // stays barred separately (`IsThreadUnsafeReferenceWrapper`) — the two axes are decoupled.

    /// <summary>
    /// Recursively collects all transitive parent protocols from a protocol's obeys chain.
    /// </summary>
    private static void CollectTransitiveProtocols(ProtocolTypeSymbol protocol,
        List<TypeSymbol> result)
    {
        foreach (ProtocolTypeSymbol parent in protocol.ParentProtocols)
        {
            if (result.Any(predicate: p => p.Name == parent.Name))
            {
                continue;
            }

            result.Add(item: parent);
            CollectTransitiveProtocols(protocol: parent, result: result);
        }
    }

    /// <summary>
    /// Gets the implemented protocols for any type that supports them.
    /// </summary>
    private static List<TypeSymbol> GetImplementedProtocols(TypeSymbol type)
    {
        return type switch
        {
            RecordTypeSymbol r => r.ImplementedProtocols,
            EntityTypeSymbol e => e.ImplementedProtocols,
            _ => []
        };
    }

    /// <summary>
    /// Updates the implemented protocols for any type that supports them.
    /// </summary>
    private void UpdateTypeProtocols(TypeSymbol type, List<TypeSymbol> protocols)
    {
        // Key by the type's REALM-AWARE registry key, not its realm-free FullName. Two coexisting shells of
        // the same module-qualified name — an RF `.rf` type and its SF `.sf` wrapper (e.g. `Collections.BitList`
        // in an SF compile) — would otherwise both resolve to the ambient (RF) shell by FullName, so analyzing
        // the SF wrapper (which auto-derives only Equatable/EntityType) OVERWROTE the RF shell's declared
        // Iterable/ModifyingIndexable/Sized → RF-S205 "BitList is not iterable" in its own display body.
        string key = type is TypeSymbol ti
            ? _sa._registry.RealmRegistryKey(type: ti)
            : type.FullName;
        switch (type)
        {
            case ChoiceTypeSymbol:
                _sa._registry.UpdateChoiceProtocols(choiceName: key, protocols: protocols);
                break;
            case FlagsTypeSymbol:
                _sa._registry.UpdateFlagsProtocols(flagsName: key, protocols: protocols);
                break;
            case CrashableTypeSymbol:
                _sa._registry.UpdateCrashableProtocols(typeName: key, protocols: protocols);
                break;
            case RecordTypeSymbol:
                _sa._registry.UpdateRecordProtocols(recordName: key, protocols: protocols);
                break;
            case EntityTypeSymbol:
                _sa._registry.UpdateEntityProtocols(entityName: key, protocols: protocols);
                break;
        }
    }

    #endregion
}
