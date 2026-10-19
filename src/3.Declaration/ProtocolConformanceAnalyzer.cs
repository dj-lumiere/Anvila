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
        // * Equatable/Comparable/Hashable are SEMANTIC (opt-in): equality/ordering is an assertion, so they
        //   are NOT auto-conferred — the generic `ApplyEverywhereConformance` is deliberately NOT run, so a
        //   plain value record does not silently gain `==`/`<`. The everywhere-derive loop registers their
        //   derives ONLY for a type that explicitly declares `obeys P`.
        ApplyAutoCopyableConformance();
        ApplyEverywhereConformance();
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
        string? markerName = type.Category switch
        {
            TypeCategory.Record => "RecordType",
            TypeCategory.Entity => "EntityType",
            TypeCategory.Choice => "ChoiceType",
            TypeCategory.Flags => "FlagsType",
            _ => null
        };

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

    /// <summary>The STRUCTURAL everywhere-protocols that are auto-conferred (value/memory semantics). The
    /// SEMANTIC everywhere-protocols (Equatable/Comparable/Hashable) are opt-in and deliberately excluded —
    /// a plain value record must not silently gain equality/ordering. Keyed by bare protocol name.</summary>
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
            // Only the STRUCTURAL everywhere-protocol (Copyable) is auto-conferred here. The
            // SEMANTIC ones (Equatable/Comparable/Hashable) carry the same `needs P everywhere` self-
            // constraint but are OPT-IN — conferring them structurally would give a plain value record
            // silent `==`/`<`. Their derives attach only on an explicit `obeys P` (the everywhere-derive
            // loop reads the declared conformance).
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
