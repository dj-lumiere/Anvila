using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Builder.Verification.Enums;

namespace Builder.Verification;

/// <summary>
/// Phase 5: Statement analysis.
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Phase 5: Body Analysis

    /// <summary>
    /// Analyzes routine bodies and expressions for type correctness.
    /// </summary>
    /// <param name="program">The program to analyze.</param>
    private void AnalyzeBodies(Program program)
    {
        foreach (ISyntaxTreeNode declaration in program.Declarations)
        {
            AnalyzeDeclaration(node: declaration);
        }

        AnalyzeWorkRoutines(program: program);
    }

    private void AnalyzeDeclaration(ISyntaxTreeNode node)
    {
        switch (node)
        {
            case RoutineDeclaration func:
                // Skip external/LLVM-only routines — their NothingStatement bodies have nothing to analyze
                if (func.Body is not NothingStatement)
                {
                    AnalyzeFunctionBody(routine: func);
                }

                break;

            case RecordDeclaration record:
            {
                TypeSymbol? recordType = _registry.LookupType(name: record.Name);
                AnalyzeTypeMembers(members: record.Members, ownerType: recordType);
                break;
            }

            case EntityDeclaration entity:
            {
                TypeSymbol? entityType = _registry.LookupType(name: entity.Name);
                AnalyzeTypeMembers(members: entity.Members, ownerType: entityType);
                break;
            }

            case VariableDeclaration { IsGlobal: true } globalDecl:
                // Suflae module-level global: registered in Phase 3; here we type-check the initializer
                // against the declared type so codegen has a resolved repr for the stored value.
                if (globalDecl.Initializer != null)
                {
                    TypeSymbol declaredType = ResolveType(typeExpr: globalDecl.Type!);
                    AnalyzeExpression(expression: globalDecl.Initializer,
                        expectedType: declaredType);
                }

                break;

            case PresetDeclaration preset:
                AnalyzePresetValue(preset: preset);
                break;

            case VariableDeclaration varDecl:
                // A bare `var` at module level. `var` is a routine-local binding; there is no module-level
                // `var`. A language with module globals spells module-level mutable state `global`.
                ReportError(code: SemanticDiagnosticCode.ModuleLevelVarNotAllowed,
                    message:
                    $"'var {varDecl.Name}' is not allowed at module level. 'var' declares a routine-local " +
                    "binding — move it inside a routine. " + (!_registry.Rules.HasModuleGlobals
                        ? $"{_registry.Rules.Name} has no module-level mutable state."
                        : _registry.EntitiesAreShared
                            ? $"For module-level mutable state use 'global {varDecl.Name}: <Type> = ...'."
                            : $"A value every routine reads goes in 'global {varDecl.Name}: <Type> = ...', which is " +
                              "only read. Shared state that changes goes in a global Unbroken or Guarded."),
                    location: varDecl.Location);
                break;
        }
    }

    /// <summary>
    /// Type-checks a preset's value against its declared type. Declaration collection only labels the
    /// outermost node, so without this a preset's inner expressions (the literal in <c>Byte(0x20u8)</c>)
    /// stayed untyped wherever the value was inlined, and a mismatched value went unreported. An
    /// <c>Array[T, N]</c> list preset is typed element by element at collection instead.
    /// </summary>
    private void AnalyzePresetValue(PresetDeclaration preset)
    {
        if (preset.Value is ListLiteralExpression)
        {
            return;
        }

        if (PresetReachesItself(preset: preset))
        {
            ReportError(code: SemanticDiagnosticCode.ModuleGlobalCircularInit,
                message: $"The preset '{preset.Name}' is computed from itself (through its own value or the presets " +
                         "it names), so it has no value to start from. Give it a value that does not name it.",
                location: preset.Location);
            return;
        }

        string? previousModule = _currentModuleName;
        if (_presetModules.TryGetValue(key: preset, value: out string? presetModule) && presetModule != null)
        {
            _currentModuleName = presetModule;
        }

        try
        {
            TypeSymbol presetType = ResolveType(typeExpr: preset.Type);
            if (presetType is ErrorTypeSymbol)
            {
                return;
            }

            // The value resolves where it was written: that file's imports.
            TypeSymbol valueType = InImportStateOf(file: preset.Location.FileName,
                analyze: () => AnalyzeExpression(expression: preset.Value, expectedType: presetType));
            if (valueType is not ErrorTypeSymbol && !IsAssignableTo(source: valueType, target: presetType))
            {
                ReportError(code: SemanticDiagnosticCode.VariableInitializerTypeMismatch,
                    message:
                    $"Preset '{preset.Name}' is declared '{presetType.Name}', but its value is {Article.For(name: valueType.Name)} '{valueType.Name}'.",
                    location: preset.Value.Location);
            }
        }
        finally
        {
            _currentModuleName = previousModule;
        }
    }

    /// <summary>Whether the value of <paramref name="preset"/> names the preset itself, directly or through the
    /// values of the presets of its file that it names.</summary>
    private bool PresetReachesItself(PresetDeclaration preset)
    {
        var byName = new Dictionary<string, PresetDeclaration>(comparer: StringComparer.Ordinal);
        foreach (PresetDeclaration p in _presetModules.Keys)
        {
            if (p.Location.FileName == preset.Location.FileName)
            {
                byName[key: p.Name] = p;
            }
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var pending = new Stack<Expression>();
        pending.Push(item: preset.Value);
        while (pending.Count > 0)
        {
            bool found = false;
            AstWalker.WalkExpressions(root: pending.Pop(), visit: e =>
            {
                if (e is not IdentifierExpression { Name: var name } ||
                    !byName.TryGetValue(key: name, value: out PresetDeclaration? named))
                {
                    return;
                }

                if (name == preset.Name)
                {
                    found = true;
                }
                else if (seen.Add(item: name))
                {
                    pending.Push(item: named.Value);
                }
            });
            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private void AnalyzeTypeMembers(List<SyntaxTree.Declaration> members, TypeSymbol? ownerType)
    {
        TypeSymbol? prevType = _currentType;
        _currentType = ownerType;
        foreach (SyntaxTree.Declaration member in members)
        {
            if (member is RoutineDeclaration { Body: not NothingStatement } memberRoutine)
            {
                AnalyzeFunctionBody(routine: memberRoutine);
            }
        }

        _currentType = prevType;
    }

    /// <summary>
    /// Warns about <c>routine T.create(...) -&gt; T</c>: the previous release's constructor spelling, which declares an
    /// ordinary member routine named <c>create</c> now, so <c>T(...)</c> never runs it.
    /// </summary>
    private void WarnCreateIsNotConstructor(RoutineDeclaration routine, RoutineInfo routineInfo)
    {
        if (routineInfo is not { Name: "create", OwnerType: { } owner, ReturnType: { } returned, IsSynthesized: false, IsCommon: false } ||
            routine.MemberRoutineName != "create" || returned.BareName != owner.BareName)
        {
            return;
        }

        ReportWarning(code: SemanticWarningCode.CreateIsNotConstructor,
            message: $"'{owner.BareName}.create' is an ordinary member routine named 'create', not a constructor, so " +
                     $"'{owner.BareName}(...)' never runs it. To make it the constructor, declare it " +
                     $"'routine {owner.BareName}(...)' (migrate rewrites it), or give it another name.",
            location: routine.Location);
    }

    private void AnalyzeFunctionBody(RoutineDeclaration routine)
    {
        // Opt-in-capability derive templates (`@overridable/@override routine T.eq/compare()` with a
        // bare generic-param owner) are buildtime macros consumed ONLY by the wired per-type
        // synthesizer (WiredRoutinePass.CloneUniversalDeriveBody → post-GMP lowering resolves the
        // operators). Unlike represent/diagnose, they are NOT registered as live universal memberRoutines
        // (see StdlibLoader.Registration), so no GMP instance relies on the template body being
        // SA-annotated. Analyzing the raw template pre-monomorphization is meaningless and
        // spuriously errors (unresolved `T`-typed `you:` param, splice `!=`, unknown `SAME`), so
        // skip it. represent/diagnose stay analyzed — their registered universal instances need the
        // SA annotation. Concrete-owner `@override`s (e.g. `MyType.eq`) resolve their owner normally.
        // Structural owner/memberRoutine (name-canonicalization): the parser split the dotted name into
        // OwnerName + memberRoutineName + HasReceiverTypeArgs, so we no longer re-parse the `Name` string.
        // The template is a UNIVERSAL derive when its owner is a bare type-parameter placeholder — no
        // receiver type-args (not `List[T].foo`) AND the owner name doesn't resolve to a real type
        // (it's the `T` placeholder, not a concrete-owner `MyType.eq` override).
        if (IsSkippableOptInDeriveTemplate(routine: routine))
        {
            return;
        }

        // A routine with a parameter written without a type is checked where it is made concrete, once per set of
        // types it is used with (AnalyzePerUseBodyOnDemand): as written, its body says nothing about that value.
        if (routine.IsCheckedPerUse)
        {
            return;
        }

        // A user-defined `destruct` replaces the builder-generated memory teardown (field
        // recursion + invalidate `me`), so the author owns freeing `me` and its fields. Require
        // `dangerous` so this opt-in to manual memory management is explicit at the declaration.
        ValidateDestroyDeclaration(routine: routine);

        // Construct the base name matching how the routine was registered.
        (string baseName, TypeSymbol? routineOwnerType) = ComputeRoutineBaseName(routine: routine);

        // CONSTRUCTORS (`routine T(...)`) are the one case the dot-based name-string logic above
        // can't bind: their AST name is the bare type ("U64", "List") with no ".create" to key on,
        // so the registry lookup misses and the body would analyze against a stub. Use the structured
        // binding attached at registration (ResolvedInfo) for THAT case only — every other routine
        // (dotted members, protocol extensions like `ModifyingIndexable[T].pick`) keeps the existing
        // path, whose `me`-typing special-casing must not be bypassed.
        bool isConstructorDecl = routine.MemberRoutineName is null && routine.ResolvedInfo is
            { IsCreator: true, OwnerType: not null };
        if (isConstructorDecl && routine.ResolvedInfo!.OwnerType is { } resolvedInfoOwner)
        {
            routineOwnerType = resolvedInfoOwner;
        }

        RoutineInfo? routineInfo = ResolveRoutineInfoWithFallbacks(routine: routine,
            baseName: baseName,
            routineOwnerType: routineOwnerType,
            isConstructorDecl: isConstructorDecl);

        if (routineInfo == null)
        {
            // @innate routines may be intentionally skipped at registration.
            if (routine.Annotations.Contains(item: "innate"))
            {
                return;
            }

            ReportError(code: SemanticDiagnosticCode.UnresolvedRoutineBody,
                message:
                $"Routine '{baseName}' body could not be matched to a registered declaration.",
                location: routine.Location);
            return;
        }

        WarnCreateIsNotConstructor(routine: routine, routineInfo: routineInfo);

        RoutineInfo? previousRoutine = _currentRoutine;
        _currentRoutine = routineInfo;
        // One `destruct` call ends an entity: a written body tears down what the entity owns, and the builder gives
        // back the entity's own box before each of its returns (the derived bodies already end that way).
        if (routineInfo is { Name: "destruct" or "roam_free", OwnerType: EntityTypeSymbol, IsSynthesized: false } &&
            routine.Body is BlockStatement destructBody)
        {
            GiveBackBoxBeforeReturns(statement: destructBody);
            // A hollow entity (`T.hollow()` on a type whose member variable is an entity leaves that variable all zero)
            // has nothing to tear down, so `destruct` leaves it alone.
            if (routineInfo.Name == "destruct" &&
                !(destructBody.Statements.FirstOrDefault() is { } first && IsHollowGuard(statement: first)))
            {
                destructBody.Statements.Insert(index: 0, item: HollowGuard(location: destructBody.Location));
            }
        }

        _deadrefVariables.Clear();
        _everStolenVariables.Clear();
        _droppedVariables.Clear();
        _stealSites.Clear();
        _stealDestinations.Clear();

        _registry.EnterScope(kind: ScopeKind.Function, name: routine.Name);
        DeclareParametersInScope(routine: routine, routineInfo: routineInfo);

        if (routine.IsDangerous && UnsafeSurfaceIsBarred())
        {
            ReportError(code: SemanticDiagnosticCode.FeatureNotInSuflae,
                message:
                $"You mark '{routine.Name}' `dangerous`, but a {_registry.Rules.Name} program has no memory-unsafe " +
                "routines. Remove `dangerous`, or move the routine into a `.rf` module this program imports.",
                location: routine.Location);
        }

        bool wasDangerImplicit = routineInfo.IsDangerous && _dangerBlockDepth == 0;
        if (wasDangerImplicit)
        {
            _dangerBlockDepth = 1;
        }

        // @innate routines have builder-supplied bodies — skip analysis entirely.
        if (routine.Annotations.Contains(item: "innate"))
        {
            if (wasDangerImplicit)
            {
                _dangerBlockDepth = 0;
            }

            _registry.ExitScope();
            _currentRoutine = previousRoutine;
            return;
        }

        AnalyzeStatement(statement: routine.Body);

        if (wasDangerImplicit)
        {
            _dangerBlockDepth = 0;
        }

        ValidateRoutineBodyPostAnalysis(routine: routine, routineInfo: routineInfo);

        _registry.ExitScope();
        _currentRoutine = previousRoutine;
    }

    /// <summary>
    /// Resolves the <see cref="RoutineInfo"/> for a routine declaration via a multi-level fallback:
    /// registry-key lookup, failability-matched lookup, bare-name lookup, concrete-specialization
    /// name lookup, and finally the declaration's own pinned <see cref="RoutineDeclaration.ResolvedInfo"/>.
    /// Returns null only when no registration exists (e.g., an @innate unregistered stub).
    /// </summary>
    private RoutineInfo? ResolveRoutineInfoWithFallbacks(RoutineDeclaration routine,
        string baseName, TypeSymbol? routineOwnerType, bool isConstructorDecl)
    {
        // One implementation of a kind-split routine shares its registry key with the others, so a lookup by
        // key finds whichever was registered last. Its body is checked under its own `needs`: the info pinned to
        // this declaration.
        if (routine.ResolvedInfo is { } pinned && Declaration.TypeRegistry.IsKindVariant(routine: pinned) &&
            _registry.HasKindVariants(routine: pinned))
        {
            return pinned;
        }

        // A routine of the other realm's same-named type (Suflae's own `System.List` next to RazorForge's), or a
        // realm's surface routine on a shared type (Suflae's `Array.count() -> Int`), shares its name with
        // the ambient one, and the name lookups below find the ambient one. The info pinned at registration is
        // its own.
        // A specialization (`routine S64.to[U8]()`) shares its name and parameters with its siblings: only the info
        // pinned at registration tells which one this body is.
        if (routine.ResolvedInfo is { } realmPinned &&
            (realmPinned.SurfaceRealm != null || realmPinned.IsSpecialization ||
             // A routine written for a kind of type argument (`routine Text.to[T]() needs TupleType T`) shares its
             // name with the family's specializations too.
             realmPinned is { GenericParameters.Count: > 0, OwnerType: { } kindOwner } &&
             _registry.LookupSpecializedMemberRoutines(owner: kindOwner, name: realmPinned.Name).Count > 0 ||
             realmPinned.OwnerType is { } pinnedOwner && pinnedOwner.Realm != _registry.AmbientRealm))
        {
            // The registry holds the info as later passes completed it (a signature resolved again once every
            // type was known), under the same key.
            return _registry.GetRoutineByExactKey(registryKey: realmPinned.RegistryKey) ?? realmPinned;
        }

        RoutineInfo? routineInfo = ResolveRoutineInfoByRegistryKey(routine: routine,
            baseName: baseName,
            routineOwnerType: routineOwnerType,
            isConstructorDecl: isConstructorDecl);

        routineInfo ??= _registry.LookupRoutine(fullName: baseName,
            isFailable: routine.IsFailable);
        routineInfo ??= _registry.LookupRoutine(fullName: baseName);

        // Extension routines registered under a concrete owner (e.g. System.List[U16].decode_as_utf16).
        if (routineInfo == null && routine.Name.Contains(value: '['))
        {
            string? module = GetCurrentModuleName();
            string concreteName = string.IsNullOrEmpty(value: module)
                ? routine.Name
                : $"{module}.{routine.Name}";
            routineInfo = _registry.LookupRoutine(fullName: concreteName) ??
                          _registry.LookupRoutineByQualifiedName(qualifiedName: concreteName);
        }

        // Final fallback: the exact decl→info binding pinned at registration.
        routineInfo ??= routine.ResolvedInfo;
        return routineInfo;
    }

    /// <summary>
    /// Declares all parameters of <paramref name="routineInfo"/> in the current scope, pairing each
    /// with the matching AST parameter's source location for language-server go-to-definition support.
    /// </summary>
    private void DeclareParametersInScope(RoutineDeclaration routine, RoutineInfo routineInfo)
    {
        for (int pi = 0; pi < routineInfo.Parameters.Count; pi++)
        {
            ParamInfo param = routineInfo.Parameters[index: pi];
            SourceLocation? paramLoc = pi < routine.Parameters.Count
                ? routine.Parameters[index: pi].Location
                : null;
            _registry.DeclareVariable(name: param.Name, type: param.Type, location: paramLoc,
                isParameter: true, isNullable: param.IsNullable);
            ReportProtocolValueType(type: param.Type, location: paramLoc ?? routine.Location,
                what: $"parameter '{param.Name}'");
            ValidateNoTokenTypeArgument(type: param.Type, what: $"parameter '{param.Name}'",
                location: paramLoc ?? routine.Location);
        }

        MarkDangerousCallableParameters(routineInfo: routineInfo);

        // A routine may give back `Maybe` of a token (an iterator's `emit` under `try`), unpacked where it is called.
        if (routineInfo.ReturnType is { } returned)
        {
            ValidateNoTokenTypeArgument(type: returned, what: $"the result of '{routineInfo.Name}'",
                location: routine.ReturnType?.Location ?? routine.Location, carrierAllowed: true);
        }
    }

    /// <summary>
    /// Reports a protocol written where a type of values goes (a variable, a parameter, a member variable, or an
    /// element type such as <c>List[Named]</c>) in the program's own code: a protocol names what types can do, and
    /// every value has one concrete type, so nothing could be stored there. A variant holds values of several types,
    /// a generic routine takes any type that obeys the protocol.
    /// </summary>
    private void ReportProtocolValueType(TypeSymbol type, SourceLocation location, string what)
    {
        if (string.IsNullOrEmpty(value: location.FileName) || location.FileName.StartsWith(value: '<') ||
            IsStdlibFile(filePath: location.FileName) || ProtocolIn(type: type) is not { } protocol)
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
            message: $"'{protocol.Name}' is a protocol, not a type of values, so {what} can't hold one. To keep " +
                     $"values of several types, declare a variant of them. To take any type that obeys " +
                     $"'{protocol.Name}', make the routine generic: 'needs T obeys {protocol.Name}'.",
            location: location);
    }

    /// <summary>The protocol <paramref name="type"/> is or holds as a type argument, at any depth.</summary>
    private static ProtocolTypeSymbol? ProtocolIn(TypeSymbol type)
    {
        return type is ProtocolTypeSymbol protocol
            ? protocol
            : (type.TypeArguments ?? []).Select(selector: ProtocolIn).FirstOrDefault(predicate: p => p != null);
    }

    /// <summary>
    /// Validates a routine body after analysis: infers return type, checks termination, validates
    /// failable-without-failure, stores the body for error-handling variant generation, reports
    /// undismantled Lookup variables, and snapshots stolen variable names for teardown.
    /// </summary>
    /// <summary>Whether <paramref name="routine"/> implements a failable requirement of a protocol its owner
    /// obeys (<c>FileSource.enter!</c> for <c>Enterable</c>'s <c>enter!</c>).</summary>
    private bool ImplementsFailableRequirement(RoutineInfo routine)
    {
        List<TypeSymbol>? protocols = routine.OwnerType switch
        {
            RecordTypeSymbol r => r.ImplementedProtocols,
            EntityTypeSymbol e => e.ImplementedProtocols,
            _ => null
        };
        return protocols?.Any(predicate: protocol =>
            _registry.LookupMemberRoutine(type: protocol, memberRoutineName: routine.Name) is
                { IsFailable: true }) == true;
    }

    private void ValidateRoutineBodyPostAnalysis(RoutineDeclaration routine,
        RoutineInfo routineInfo)
    {
        // Infer None return type if no annotation was given and no return value was found.
        // null is a transient "not yet inferred" state — after body analysis it must be resolved.
        routineInfo.ReturnType ??= _registry.LookupType(name: "None");

        // Validate that all routines terminate explicitly on every path (#144).
        // None-returning routines still require an explicit `return` — implicit fall-off
        // is rejected so control-flow analysis remains uniform across return types.
        if (!ControlFlowExits.AlwaysTerminates(statement: routine.Body, exhaustiveWhens: _exhaustiveWhens))
        {
            ReportError(code: SemanticDiagnosticCode.MissingReturn,
                message: routineInfo.ReturnType is { IsNone: false }
                    ? $"Routine '{routine.Name}' has return type '{routineInfo.ReturnType.Name}' but not all code paths return a value."
                    : $"Routine '{routine.Name}' does not terminate on all paths. Add an explicit 'return' at the end.",
                location: routine.Location);
        }

        // A routine value that may fail goes only to a `failable Callable[...]` (RF-S761).
        CheckFailableValuesGoWhereMarked(body: routine.Body);

        // Failable routine with no throw — error: a ! routine that can't fail is misleading. An
        // implementation of a protocol's failable requirement (`Enterable.enter!`) keeps the `!` the protocol
        // asks for even when this implementation never fails.
        if (routineInfo is
                { IsFailable: true, HasThrow: false, HasFailableCalls: false } &&
            !ImplementsFailableRequirement(routine: routineInfo))
        {
            ReportError(code: SemanticDiagnosticCode.FailableWithoutThrow,
                message:
                $"'{routine.Name}' is declared failable, but it never throws and calls nothing that can fail. " +
                "Remove 'failable' from its declaration, or add the failure path it is missing.",
                location: routine.Location);
        }

        // Store routine body for error handling variant generation (Phase 4).
        // Only store if the body actually has throw/failable-calls — routines
        // implemented via @llvm_ir have no such AST nodes and can't have variants generated.
        // A protocol-extension routine (`routine Sized.doubled()`) is stored too: each type obeying the
        // protocol gets its own copy of the body (ProtocolDefaultImplLoweringPass), as for the library's.
        if (routineInfo.IsFailable && (routineInfo.HasThrow || routineInfo.HasFailableCalls) ||
            routineInfo.OwnerType is ProtocolTypeSymbol)
        {
            StoreRoutineBody(routine: routineInfo, body: routine.Body);
        }

        // Snapshot the per-routine "out of scope via steal/consumption" set onto the declaration so
        // the scope-exit teardown pass can exclude these bindings from `destruct`.
        routine.StolenVariableNames = [.. _deadrefVariables.Where(predicate: n =>
            !n.StartsWith(value: UnassignedMark, comparisonType: StringComparison.Ordinal))];
        routine.EverStolenVariableNames = [.. _everStolenVariables];
    }

    /// <summary>
    /// Whether this routine is an OPT-IN capability-default derive template (`@overridable/@override
    /// routine T.eq/compare()` on a bare generic-param owner) that must be skipped by SA — such templates
    /// aren't registered as live universal member routines, so their `T` placeholder is unbound and
    /// analyzing the raw body would spuriously error. Auto-conferred display derives are excluded.
    /// </summary>
    private bool IsSkippableOptInDeriveTemplate(RoutineDeclaration routine)
    {
        if (_currentType == null &&
            (routine.Annotations.Contains(item: "overridable") ||
             routine.Annotations.Contains(item: "override")) &&
            routine.MemberRoutineName is { } memberRoutine && routine.OwnerName is { } ownerName &&
            !routine.HasReceiverTypeArgs &&
            // A derive template names its owner as a type parameter (`needs T …`), whatever `T` resolves to:
            // a user `record T` must not turn the template into that record's own routine.
            (LookupTypeWithImports(name: ownerName) == null ||
             DeriveOwnerIsTypeParameter(ownerName: ownerName, decl: routine)))
        {
            // The auto-conferred display derives (represent/diagnose) ARE registered universals →
            // `T` is bound → they stay analyzed. Protocol-grounded via the wired catalog, not a
            // per-memberRoutine name list.
            return !Declaration.WiredRoutineCatalog.IsAutoConferredDerive(
                memberRoutine: memberRoutine);
        }

        return false;
    }

    /// <summary>
    /// Computes the registry base name and owner type for a routine, mirroring how it was registered:
    /// member routines key on their owner type, extension/universal member routines resolve their
    /// (possibly generic-param) owner, and top-level routines qualify by module.
    /// </summary>
    private (string baseName, TypeSymbol? routineOwnerType) ComputeRoutineBaseName(
        RoutineDeclaration routine)
    {
        if (_currentType != null)
        {
            // Member routine inside type body: OwnerType.Name + "." + routine.Name
            return ($"{RoutineInfo.GetTypeIdentity(type: _currentType)}.{routine.Name}",
                _currentType);
        }

        if (routine.RenderedReceiver is { } typeName)
        {
            // Extension memberRoutine syntax (e.g., "List[T].add_last"): member + bare owner come from the
            // parser's structural fields; `typeName` (owner WITH type-args = the RenderedReceiver) is still
            // needed for the bracketed protocol-extension registry lookup below — that registry-key string
            // form is the canonical rendered receiver (ResolveType(ReceiverType) resolves differently here).
            string memberRoutineName = routine.MemberRoutineName!;
            TypeSymbol? ownerType = LookupTypeWithImports(name: routine.OwnerName!);
            // Protocol-extension decls like `Iterable[Text].join` should have `me` typed as the
            // bracketed owner so the body's `for part in me` resolves `part` from
            // Iterable[Text]'s `try emit()` return. Without this, `me` is the bare gen-def
            // `Iterable` and body identifiers (parameters, loop vars) get ErrorTypeSymbol.
            // Only override for ProtocolTypeSymbol: for records/entities like
            // `List[PQEntry[TPriority, TElement]]` the gen-param resolution must happen through
            // routine.GenericParameters, not via a bracketed-cache lookup that strips the params.
            if (typeName.Contains(value: '[') && ownerType is ProtocolTypeSymbol)
            {
                TypeSymbol? bracketed = _registry.LookupType(name: typeName);
                if (bracketed is ProtocolTypeSymbol)
                {
                    ownerType = bracketed;
                }
            }

            // Universal member routine (`routine T.represent()`): a bare owner name that resolves to no
            // registered type IS the generic parameter itself. Mirror the registration path
            // (StdlibLoader.Registration: owner → GenericParameterTypeSymbol when LookupType misses) so the
            // body binds to the universal RoutineInfo (whose OwnerType is that generic param) and `T`
            // inside the body resolves as a parameter — instead of falling through to a first-wins
            // by-member-name match on some concrete type's same-named routine (e.g. BitArray.represent),
            // which left `T` resolvable only via the cross-module short-name scan.
            if (ownerType == null && routine.OwnerName is { } bareOwner &&
                !bareOwner.Contains(value: '['))
            {
                ownerType = new GenericParameterTypeSymbol(name: bareOwner);
            }

            string extBaseName = ownerType != null
                ? $"{RoutineInfo.GetTypeIdentity(type: ownerType)}.{memberRoutineName}"
                : routine.Name;
            return (extBaseName, ownerType);
        }

        // Top-level function: Module.Name (if module set), else just Name
        string? module = GetCurrentModuleName();
        string topBaseName = string.IsNullOrEmpty(value: module)
            ? routine.Name
            : $"{module}.{routine.Name}";
        return (topBaseName, null);
    }

    /// <summary>
    /// Resolves the routine's <see cref="RoutineInfo"/> via its RegistryKey (base name + param-type
    /// signature) for overload disambiguation. Temporarily installs a stub <c>_currentRoutine</c> so
    /// param-type resolution sees the routine's generic parameters and owner (for `Me`), restoring it
    /// before returning. Returns null when no keyed entry matches (caller falls back to base name).
    /// </summary>
    private RoutineInfo? ResolveRoutineInfoByRegistryKey(RoutineDeclaration routine,
        string baseName, TypeSymbol? routineOwnerType, bool isConstructorDecl)
    {
        // Look up by RegistryKey (BaseName + param types) for overload disambiguation,
        // then fall back to BaseName for the first-overload-wins entry.
        // Set up generic parameter context so ResolveType recognizes T, U, etc.
        // (mirrors Phase 4.1 registration in Signatures.cs)
        // Set OwnerType so `Me` in param types resolves to the concrete owner
        // (e.g. `routine SumS64.combine(you: Me) -> Me` needs Me → SumS64 during param-type
        // resolution, which happens before routineInfo is looked up).
        RoutineInfo? prevRoutine = _currentRoutine;
        _currentRoutine = new RoutineInfo(name: baseName)
        {
            GenericParameters = routine.GenericParameters, OwnerType = routineOwnerType
        };

        RoutineInfo? routineInfo = isConstructorDecl
            ? routine.ResolvedInfo
            : null;
        if (routineInfo == null && routine.Parameters.Count > 0)
        {
            IEnumerable<string> paramTypeNames = routine.Parameters
                                                        .Select(selector: p =>
                                                         {
                                                             if (p.Type == null)
                                                             {
                                                                 return "";
                                                             }

                                                             TypeSymbol resolved =
                                                                 ResolveType(typeExpr: p.Type);
                                                             if (resolved is ErrorTypeSymbol)
                                                             {
                                                                 return p.Type.Name ?? "";
                                                             }

                                                             // Variadic params are desugared to
                                                             // Array[T, __VarargN] up front (p.Type is
                                                             // already that Array), so the resolved type
                                                             // matches the registered signature directly —
                                                             // no List[T] wrapping.

                                                             return RoutineInfo.GetTypeIdentity(
                                                                 type: resolved);
                                                         })
                                                        .Where(predicate: n =>
                                                             !string.IsNullOrEmpty(value: n));
            string paramSig = string.Join(separator: ",", values: paramTypeNames);
            string registryKey = $"{baseName}#{paramSig}";
            routineInfo = _registry.LookupRoutine(fullName: registryKey,
                isFailable: routine.IsFailable);

            // Fallback: extension memberRoutines on concrete generic specializations
            // (e.g., `List[Byte].create`) register under the concrete owner type,
            // producing a RegistryKey like `System.List[System.Byte].create#System.Bytes`.
            // The first lookup above used the generic-def-normalized owner
            // (`System.List[T].create`), so it missed. Resolve the concrete owner
            // type from the routine name and rebuild the canonical key.
            if (routineInfo == null && routine.HasReceiverTypeArgs &&
                routine.ReceiverType is { } ownerExpr && routine.MemberRoutineName is { } mName)
            {
                // Structured receiver from the parser (was: re-parse the owner substring of Name).
                TypeSymbol resolvedOwner = ResolveType(typeExpr: ownerExpr);
                if (resolvedOwner is not ErrorTypeSymbol)
                {
                    string ownerIdentity = RoutineInfo.GetTypeIdentity(type: resolvedOwner);
                    string concreteKey = $"{ownerIdentity}.{mName}#{paramSig}";
                    routineInfo = _registry.LookupRoutine(fullName: concreteKey);
                }
            }
        }

        _currentRoutine = prevRoutine;
        return routineInfo;
    }

    private void AnalyzeStatement(Statement statement)
    {
        _statementDepth++;
        try
        {
            AnalyzeStatementCore(statement: statement);
        }
        finally
        {
            _statementDepth--;
            if (_expressionDepth == 0)
            {
                ReportSpecialFloats();
                ReportEscapedDangerousCallables();
            }
        }
    }

    private void AnalyzeStatementCore(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                AnalyzeBlockStatement(block: block);
                break;

            case DeclarationStatement decl:
                AnalyzeDeclarationStatement(decl: decl);
                break;

            case ExpressionStatement { IsBecomes: true } becomes:
                // A `becomes` the block arm of a value-giving `when` takes is analyzed there; any other is misplaced.
                ReportError(code: SemanticDiagnosticCode.BecomesOutsideWhenArm,
                    message: "'becomes' gives a block arm of a 'when' that gives a value its value, as the arm's last " +
                             "line. Here it isn't one: return the value, or assign it.",
                    location: becomes.Location);
                _ = AnalyzeExpression(expression: becomes.Expression);
                break;

            case ExpressionStatement expr:
                AnalyzeExpressionStatement(expr: expr);
                break;

            case AssignmentStatement assign:
                AnalyzeAssignmentStatement(assign: assign);
                break;

            case PlaceSwapStatement swap:
                AnalyzePlaceSwap(swap: swap);
                break;

            case IfStatement ifStmt:
                AnalyzeIfStatement(ifStmt: ifStmt);
                break;

            case WhileStatement whileStmt:
                AnalyzeWhileStatement(whileStmt: whileStmt);
                break;

            case LoopStatement loopStmt:
            {
                if (loopStmt.WrittenWhile is { } written)
                {
                    NoteLiteralLoopCondition(whileStmt: written);
                }

                var deadBeforeLoop = new HashSet<string>(collection: _deadrefVariables);
                _unassignedAtBreak.Push(item: []);
                _endedAtContinue.Push(item: []);
                _registry.EnterScope(kind: ScopeKind.Loop, name: "loop");
                // A lowered `each x in a` loop carries the surface source name so the
                // reshaping-during-iteration check can fire even though the EachStatement was
                // rewritten away before this phase. Track it for the duration of the body.
                // An `each` over an adapter chain (`xs.where(...)`) walks the container at its head.
                if (loopStmt.IterationSourceName is { } sourceName &&
                    _adapterSourcePaths.TryGetValue(key: sourceName, value: out string? sourceHead))
                {
                    loopStmt.AdapterSourcePath = sourceHead;
                }
                else if (loopStmt is { IterationSourceName: null, IteratorName: { } iteratorName } &&
                         _adapterSourcePaths.TryGetValue(key: iteratorName, value: out string? adapterHead))
                {
                    loopStmt.AdapterSourcePath = adapterHead;
                }

                string? adapterWalked = loopStmt.AdapterSourcePath is { } head && !head.Contains(value: '.') &&
                                        !head.Contains(value: '[')
                    ? head
                    : null;
                string? walkedName = adapterWalked ?? loopStmt.IterationSourceName;
                bool tracksIterationSource = walkedName != null && _activeIterationSources.Add(item: walkedName);
                AnalyzeStatement(statement: loopStmt.Body);
                if (tracksIterationSource)
                {
                    _activeIterationSources.Remove(item: walkedName!);
                }

                _registry.ExitScope();
                ReportTakenInLoop(loop: loopStmt, before: deadBeforeLoop, atContinue: _endedAtContinue.Pop());
                // A loop is left only by a `break` (or by leaving the routine), so what is still unassigned after
                // it is what was unassigned at one of its breaks.
                HashSet<string> unassignedAtBreak = _unassignedAtBreak.Pop();
                _deadrefVariables.RemoveWhere(match: n =>
                    n.StartsWith(value: UnassignedMark, comparisonType: StringComparison.Ordinal));
                _deadrefVariables.UnionWith(other: unassignedAtBreak);
                break;
            }

            case EachStatement eachStmt:
                AnalyzeEachStatement(eachStmt: eachStmt);
                break;

            case ExpandStatement expandStmt:
                AnalyzeExpandStatement(expandStmt: expandStmt);
                break;

            case WhenStatement whenStmt:
                AnalyzeWhenStatement(whenStmt: whenStmt);
                break;

            case ReturnStatement ret:
                AnalyzeReturnStatement(ret: ret);
                break;

            case VariantReturnStatement variantReturn:
                AnalyzeVariantReturnStatement(variantReturn: variantReturn);
                break;

            case ThrowStatement throwStmt:
                AnalyzeThrowStatement(throwStmt: throwStmt);
                break;

                break;

            case BreakStatement breakStmt:
                AnalyzeBreakStatement(breakStmt: breakStmt);
                if (_unassignedAtBreak.TryPeek(result: out HashSet<string>? atBreak))
                {
                    atBreak.UnionWith(other: _deadrefVariables.Where(predicate: n =>
                        n.StartsWith(value: UnassignedMark, comparisonType: StringComparison.Ordinal)));
                }

                break;

            case ContinueStatement continueStmt:
                AnalyzeContinueStatement(continueStmt: continueStmt);
                if (_endedAtContinue.TryPeek(result: out HashSet<string>? atContinue))
                {
                    atContinue.UnionWith(other: _deadrefVariables.Where(predicate: n =>
                        !n.StartsWith(value: UnassignedMark, comparisonType: StringComparison.Ordinal)));
                }

                break;

            case NothingStatement:
                // Pass is a no-op statement with no type analysis needed
                break;

            case DestructuringStatement destruct:
                AnalyzeDestructuringStatement(destruct: destruct);
                break;

            case DropStatement dropped:
                AnalyzeDropStatement(dropped: dropped);
                break;

            case StealStatement stolen:
                AnalyzeStealStatement(stolen: stolen);
                break;

            case DangerStatement danger:
                AnalyzeDangerStatement(danger: danger);
                break;

            case UsingStatement usingStmt:
                AnalyzeUsingStatement(usingStmt: usingStmt);
                break;

            case AccessStatement access:
                AnalyzeAccessStatement(access: access);
                break;

            default:
                ReportWarning(code: SemanticWarningCode.UnknownStatementType,
                    message:
                    $"Internal: semantic analyzer has no handler for AST node '{statement.GetType().Name}'. This statement will be skipped; downstream analysis may be incomplete. Please report as a builder bug.",
                    location: statement.Location);
                break;
        }
    }

    #endregion

    #region Statement Analysis memberRoutines

    /// <summary>
    /// Analyzes <c>steal place as old [inject fresh]</c> by expanding it, in its block, into statements the rest of
    /// the builder already knows:
    /// <code>
    /// var __steal_fresh_N = fresh          # first, so a crash leaves the place untouched
    /// var old = steal place                # a move out of the place, no copy
    /// place = steal __steal_fresh_N        # tears nothing down: what was there is old's now
    /// </code>
    /// Without <c>inject</c> a <c>T?</c> place is left <c>none</c>, and a whole variable simply moves.
    /// </summary>
    private void AnalyzeStealStatement(StealStatement stolen)
    {
        SourceLocation location = stolen.Location;
        if (stolen.Place is not (IdentifierExpression or MemberExpression or IndexExpression))
        {
            ReportError(code: SemanticDiagnosticCode.StealNotAPlace,
                message: "'steal … as …' takes a value out of a place: a variable, a member variable or an element. " +
                         "Write the place it takes from.",
                location: stolen.Place.Location);
            return;
        }

        // Two more uses of the place, cloned before anything is analyzed.
        Expression movedOut = Instantiation.GenericAstRewriter.DeepCloneExpression(expr: stolen.Place);
        Expression filled = Instantiation.GenericAstRewriter.DeepCloneExpression(expr: stolen.Place);
        TypeSymbol placeType = AnalyzeExpression(expression: stolen.Place);
        if (placeType is ErrorTypeSymbol)
        {
            return;
        }

        bool wholeVariable = stolen.Place is IdentifierExpression;
        bool optionalPlace = placeType is RecordTypeSymbol { GenericDefinition.Name: "Maybe" };
        if (stolen.Fresh == null && !wholeVariable && !optionalPlace)
        {
            ReportError(code: SemanticDiagnosticCode.StealLeavesEmptyPlace,
                message: $"Taking the '{placeType.Name}' out would leave nothing in its place. Put a new value in as " +
                         $"you take it: 'steal … as {stolen.Name} inject <new value>'.",
                location: location);
            return;
        }

        var lowered = new List<Statement>();
        Expression? putBack = null;
        if (stolen.Fresh != null)
        {
            string freshName = $"__steal_fresh_{++_stealStatementSeq}";
            lowered.Add(item: new DeclarationStatement(
                Declaration: new VariableDeclaration(Name: freshName,
                    Type: new TypeExpression(Name: placeType.Name, GenericArguments: null, Location: location)
                    {
                        KnownType = placeType
                    },
                    Initializer: stolen.Fresh,
                    Visibility: VisibilityModifier.Secret,
                    Location: location),
                Location: location));
            putBack = new StealExpression(Operand: new IdentifierExpression(Name: freshName, Location: location),
                Location: location) { IsImplicitMove = true };
        }
        else if (!wholeVariable)
        {
            putBack = new LiteralExpression(Value: null, LiteralType: Builder.Tokenizer.TokenType.NoneValue, Location: location);
        }

        // An element is exchanged by its container in one step (`xs.exchange(index: i, value: fresh)`).
        if (stolen.Place is IndexExpression element && putBack != null)
        {
            // Reached through a token or a handle (`s[b]` with `s: Modifying[List[…]]`), the element is the container's
            // inside it, and so is the routine that exchanges it. A read-only token is turned away when the exchange is
            // analyzed, as any changing call through one is.
            if (_registry.LookupMemberRoutine(type: ContainerBehind(type: element.Object.ResolvedType) ?? placeType,
                    memberRoutineName: Declaration.RuntimeContract.Exchange, isFailable: true) is not
                { Parameters: [.., { Name: var positionName }, _] })
            {
                ReportError(code: SemanticDiagnosticCode.StealNotAPlace,
                    message: $"'{element.Object.ResolvedType?.Name}' cannot swap an element in place, so " +
                             "'steal … as …' cannot take one out of it. Use its 'remove' routines.",
                    location: stolen.Place.Location);
                return;
            }

            var exchangeCall = new CallExpression(
                Callee: new MemberExpression(Object: Instantiation.GenericAstRewriter.DeepCloneExpression(
                        expr: ((IndexExpression)movedOut).Object),
                    MemberName: Declaration.RuntimeContract.Exchange,
                    Location: location),
                Arguments:
                [
                    new NamedArgumentExpression(Name: positionName, Value: ((IndexExpression)movedOut).Index,
                        Location: location),
                    new NamedArgumentExpression(Name: "value", Value: putBack, Location: location)
                ],
                Location: location) { IsSynthesizedLowering = true };
            lowered.Add(item: new DeclarationStatement(
                Declaration: new VariableDeclaration(Name: stolen.Name,
                    Type: null,
                    Initializer: exchangeCall,
                    Visibility: VisibilityModifier.Secret,
                    Location: location),
                Location: location));
            putBack = null;
        }
        else
        {
            lowered.Add(item: new DeclarationStatement(
                Declaration: new VariableDeclaration(Name: stolen.Name,
                    Type: null,
                    Initializer: new StealExpression(Operand: movedOut, Location: location)
                    {
                        IsPlaceMove = !wholeVariable
                    },
                    Visibility: VisibilityModifier.Secret,
                    Location: location),
                Location: location));
        }

        if (putBack != null)
        {
            lowered.Add(item: new AssignmentStatement(Target: filled, Value: putBack, Location: location)
            {
                KeepsReplacedValue = true
            });
        }

        foreach (Statement statement in lowered)
        {
            AnalyzeStatement(statement: statement);
        }

        stolen.Lowered = lowered;
    }

    /// <summary>
    /// The container a token or a handle reaches (<c>List[T]</c> behind <c>Modifying[List[T]]</c>, <c>Retained[List[T]]</c>),
    /// or <paramref name="type"/> itself when it is the container.
    /// </summary>
    private static TypeSymbol? ContainerBehind(TypeSymbol? type)
    {
        return type is RecordTypeSymbol { TypeArguments: [var inner, ..] } handle &&
               (handle.GenericDefinition ?? handle).BareName is Declaration.RuntimeContract.Viewing
                   or Declaration.RuntimeContract.Modifying or Declaration.RuntimeContract.Retained
                   or Declaration.RuntimeContract.Roamed
            ? inner
            : type;
    }

    /// <summary>Numbers the temporaries <see cref="AnalyzeStealStatement"/> introduces.</summary>
    private int _stealStatementSeq;

    private void AnalyzeVariantReturnStatement(VariantReturnStatement variantReturn)
    {
        if (variantReturn.Value != null)
        {
            // A value the routine returns goes into the carrier the variant gives back (`Maybe[S64]` for `-> S64`): a
            // bare number takes the routine's result type, as in the routine it was written in.
            TypeSymbol? returned = variantReturn.SiteKind == VariantSiteKind.FromReturn &&
                                   _currentRoutine?.ReturnType is
                                       { BareName: "Maybe" or "Check" or "Lookup", TypeArguments: [{ } value] }
                ? value
                : null;
            AnalyzeExpression(expression: variantReturn.Value, expectedType: returned);
        }
    }

    /// <summary>Whether the range an <c>each</c> loop walks is over a concrete integer type (a fixed-width integer or
    /// <c>Int</c>), so the loop can count instead of walking an emitter.</summary>
    private bool WalksIntegerRange(RangeWalk rangeWalk)
    {
        return _registry.LookupVariable(name: rangeWalk.SourceName)?.Type is RecordTypeSymbol
        {
            GenericDefinition.Name: "Range", TypeArguments: [RecordTypeSymbol element]
        } && IsIntegerType(type: element);
    }

    private void AnalyzeBlockStatement(BlockStatement block)
    {
        if (block.Statements.Count == 0)
        {
            // `nothing` is a rule about written source. A builder-written body empties a block legitimately (an
            // `expand` over a type with no members, a lowered recovery arm with nothing left to do).
            if (_isInCompilerGeneratedBody)
            {
                return;
            }

            ReportError(code: SemanticDiagnosticCode.EmptyBlockWithoutNothing,
                message: "This block is empty. A block with no work to do holds the line `nothing`.",
                location: block.Location);
            return;
        }

        _registry.EnterScope(kind: ScopeKind.Block, name: null);

        // An `each` over an integer range literal counts: the emitter walk is replaced by a counter loop now that the
        // range's local has its type. A range of any other element type keeps the walk.
        if (block.Statements is [_, LoopStatement { RangeWalk: { } rangeWalk }] &&
            WalksIntegerRange(rangeWalk: rangeWalk) &&
            Desugaring.Passes.ControlFlowLoweringPass.RangeCounterLoop(walk: block.Statements) is { } counter)
        {
            block.Statements.Clear();
            block.Statements.AddRange(collection: counter);
        }

        // Once a statement unconditionally diverges (return / throw / break / continue),
        // any following statement in the same block can never execute — report the first such dead
        // statement. (Conditional divergence inside a nested if/when does NOT terminate this block.)
        bool diverged = false;
        bool afterLoop = false;
        foreach (Statement stmt in block.Statements)
        {
            if (diverged)
            {
                ReportNeverRunsError(code: SemanticDiagnosticCode.UnreachableStatement,
                    message: afterLoop
                        ? "Unreachable statement: the loop before it has no `break`, so only a `return` or " +
                          "`throw` leaves it, and this code can never run."
                        : "Unreachable statement: the previous statement always diverges " +
                          "(return / throw / break / continue), so this code can never run.",
                    location: stmt.Location,
                    endLine: LastLine(statement: block.Statements[^1]));
                break;
            }

            AnalyzeStatement(statement: stmt);
            afterLoop = stmt is LoopStatement loop && ControlFlowExits.NeverEndsByItself(loop: loop);
            diverged = afterLoop || stmt is ReturnStatement or ThrowStatement
                or BreakStatement or ContinueStatement;
        }

        // A `steal place as old` statement stands for the statements it was expanded into.
        if (block.Statements.Any(predicate: s => s is StealStatement { Lowered: not null }))
        {
            List<Statement> expanded = block.Statements
                                            .SelectMany(selector: s => s is StealStatement { Lowered: { } lowered }
                                                 ? lowered
                                                 : [s])
                                            .ToList();
            block.Statements.Clear();
            block.Statements.AddRange(collection: expanded);
        }

        _registry.ExitScope();
    }

    private void AnalyzeDeclarationStatement(DeclarationStatement decl)
    {
        switch (decl.Declaration)
        {
            case VariableDeclaration varDecl:
                AnalyzeVariableDeclaration(varDecl: varDecl);
                break;

            case RoutineDeclaration func:
                // Nested function declaration
                AnalyzeFunctionBody(routine: func);
                break;

            default:
                ReportWarning(code: SemanticWarningCode.UnexpectedDeclaration,
                    message:
                    $"Unexpected declaration in statement context: {decl.Declaration.GetType().Name}",
                    location: decl.Location);
                break;
        }
    }

    private void AnalyzeVariableDeclaration(VariableDeclaration varDecl)
    {
        RejectLayoutAnnotation(annotations: varDecl.Annotations,
            location: varDecl.Location,
            where: "a variable declaration");

        TypeSymbol varType;

        // Suflae: an entity-reference annotation (`x: E` / `x: E?`) stores a `Roamed[E]` handle. Track
        // whether the annotation made this a nullable slot and whether it is a non-null entity slot.
        bool annotatedNullable = false;
        bool annotatedNonNullEntity = false;

        if (varDecl.Type != null)
        {
            // Explicit type annotation
            varType = ResolveType(typeExpr: varDecl.Type);

            (TypeSymbol resolved, bool isNullable, bool isEntitySlot) =
                SharedEntities.ResolveAnnotation(annotated: varType, typeExpr: varDecl.Type);
            varType = resolved;
            annotatedNullable = isNullable;
            annotatedNonNullEntity = isEntitySlot && !isNullable;
        }
        else if (varDecl.Initializer != null)
        {
            // Type inference from initializer
            varType = RuntimeTypeOfConstGeneric(type: AnalyzeExpression(expression: varDecl.Initializer),
                initializer: varDecl.Initializer);
            TypeSymbol? elementToken = ElementTokenBinding(initializer: varDecl.Initializer, elementType: varType);
            if (elementToken != null)
            {
                varType = elementToken;
                _elementTokenSources[key: varDecl.Name] = DescribeIndexRead(element: (IndexExpression)varDecl.Initializer);
            }
            else
            {
                _elementTokenSources.Remove(key: varDecl.Name);
            }

            // Collection literals analyze to the bare entity type (List[T] / Set[T] / Dict[K,V]), and the slot
            // holds that entity directly. No wrapper is made around it.
        }
        else
        {
            ReportError(code: SemanticDiagnosticCode.VariableNeedsTypeOrInitializer,
                message:
                $"Variable '{varDecl.Name}' requires either a type annotation or an initializer.",
                location: varDecl.Location);
            varType = ErrorTypeSymbol.Instance;
        }

        ReportProtocolValueType(type: varType, location: varDecl.Location, what: $"'{varDecl.Name}'");
        ValidateNoTokenTypeArgument(type: varType, what: $"'{varDecl.Name}'", location: varDecl.Location);

        // #16: Plain `var x: T` without an initializer is disallowed.
        // Use `lateinit var x: T` (eager allocation, late initialization).
        if (_registry.Rules.RequiresLateinitForDeferredInit && varDecl is
                { Type: not null, Initializer: null, IsLateInit: false })
        {
            ReportError(code: SemanticDiagnosticCode.VariableNeedsTypeOrInitializer,
                message: $"Variable '{varDecl.Name}' has a type annotation but no initializer. " +
                         "Use 'lateinit var' to defer initialization.",
                location: varDecl.Location);
        }

        ValidateVariableInitializer(varDecl: varDecl, varType: varType);

        CheckVariableCopyRestrictions(varDecl: varDecl, varType: varType);

// Register variable in current scope
        // A new declaration shadows any prior steal of the same name in this scope.
        _deadrefVariables.Remove(item: varDecl.Name);
        NoteAssigned(name: varDecl.Name);
        // A variable declared without a value (`lateinit var label: Text`) can't be read until every path to the
        // read has given it one. Names the builder writes itself (`__rf_...`) are assigned by the code it writes.
        if (varDecl is { Initializer: null, Type: not null } &&
            !varDecl.Name.StartsWith(value: "__", comparisonType: StringComparison.Ordinal))
        {
            _deadrefVariables.Add(item: UnassignedMark + varDecl.Name);
        }

        NoteStealDestination(value: varDecl.Initializer,
            destination: varDecl.IsLoopTakeSource
                ? $"the 'each' loop on line {varDecl.Location.Line}"
                : $"'{varDecl.Name}'");

        // Suflae flow typing: a local is nullable when it was annotated `E?`, or (with no annotation)
        // inferred from a nullable entity read (`var n = a.optField`) or a `none` literal — so member
        // access on it is gated until a null-check.
        bool varIsNullable = annotatedNullable ||
                             varDecl is { Type: null, Initializer: not null } &&
                             SharedEntities.IsNullableRead(expr: varDecl.Initializer);

        // Suflae: assigning a possibly-none value into a NON-NULL entity variable (`var x: E = <nullable>`
        // or `var x: E = none`) is rejected — declare the variable optional (`x: E?`) to allow none.
        if (annotatedNonNullEntity && varDecl.Initializer != null &&
            SharedEntities.IsNullableRead(expr: varDecl.Initializer))
        {
            SharedEntities.ReportIntoNonNull(target: $"variable '{varDecl.Name}'",
                value: varDecl.Initializer,
                optionalHint: $"{varDecl.Name}: <Type>?");
        }

        // A local may not hide a parameter or a local of an enclosing block: one name is one variable within a
        // routine (as for pattern bindings), and the backend keeps one slot per name.
        if (!_registry.CurrentScope.IsDeclaredLocally(name: varDecl.Name) &&
            _registry.CurrentScope.Parent?.LookupWithinRoutine(name: varDecl.Name) is { } hidden)
        {
            string what = hidden.IsParameter ? "a parameter of this routine" : "a variable of an enclosing block";
            string where = hidden.Location is { } hl ? $" (declared at line {hl.Line})" : "";
            ReportError(code: SemanticDiagnosticCode.IdentifierShadowing,
                message: $"You declare '{varDecl.Name}' here, but '{varDecl.Name}' is already {what}{where}. " +
                         "A local cannot hide another variable of the routine: give this one a different name.",
                location: varDecl.Location);
        }

        bool declared = _registry.DeclareVariable(name: varDecl.Name,
            type: varType,
            isNullable: varIsNullable,
            location: varDecl.Location,
            isReadOnly: varDecl.IsPreset);
        if (varDecl.Initializer != null)
        {
            TakeDangerousCallableIntoLocal(name: varDecl.Name, value: varDecl.Initializer);
        }

        if (!declared)
        {
            ReportError(code: SemanticDiagnosticCode.VariableRedeclaration,
                message: $"Variable '{varDecl.Name}' is already declared in this scope.",
                location: varDecl.Location);
        }


        TrackSpecialVariableRegistrations(varDecl: varDecl, varType: varType);
        TrackReadOnlyIterator(varDecl: varDecl);
        NoteOneThreadLambdaVariable(name: varDecl.Name, initializer: varDecl.Initializer);
    }

    /// <summary>
    /// Records whether a variable walks from a read-only place: the iterator an <c>each</c> loop declares
    /// (<c>var _lf_iter_N = source.iter()</c>), or an iterator or adapter made from one (<c>var big =
    /// boxes.where(...)</c>, the source an <c>each</c> over a call names). Any other declaration of the name clears it,
    /// so a name reused in another routine starts over.
    /// </summary>
    private void TrackReadOnlyIterator(VariableDeclaration varDecl)
    {
        // An adapter kept in a variable (`var big = xs.where(...)`, or the `_lf_src_N` an `each` over a call names)
        // walks the container at the head of its chain, and so does an iterator taken from it.
        CallExpression? adapterChain = varDecl.Initializer switch
        {
            CallExpression
            {
                IsSynthesizedLowering: true, Callee: MemberExpression { MemberName: "emitter", Object: CallExpression chain }
            } => chain,
            CallExpression
            {
                IsSynthesizedLowering: true, Callee: MemberExpression { MemberName: "emitter", Object: IdentifierExpression }
            } => null,
            CallExpression chain => chain,
            _ => null
        };
        string? inheritedHead = varDecl.Initializer is CallExpression
        {
            Callee: MemberExpression { Object: IdentifierExpression { Name: var walkedName } }
        } && _adapterSourcePaths.TryGetValue(key: walkedName, value: out string? walkedHead)
            ? walkedHead
            : null;
        if (((adapterChain != null ? AdapterHeadPath(walk: adapterChain) : null) ?? inheritedHead) is { } headPath)
        {
            _adapterSourcePaths[key: varDecl.Name] = headPath;
        }
        else
        {
            _adapterSourcePaths.Remove(key: varDecl.Name);
        }

        bool readOnly = _registry.Rules.ChecksAccessTokens && varDecl.Initializer switch
        {
            CallExpression { IsSynthesizedLowering: true, Callee: MemberExpression { MemberName: "emitter", Object: var source } }
                => StartsReadOnly(place: source),
            { ResolvedType: { } walked } initializer => Walks(type: walked) && StartsReadOnly(place: initializer),
            _ => false
        };
        if (readOnly)
        {
            _readOnlyIterators.Add(item: varDecl.Name);
        }
        else
        {
            _readOnlyIterators.Remove(item: varDecl.Name);
        }
    }

    /// <summary>The iterator variables of <c>each</c> loops over an adapter chain, with the path of the container at
    /// the chain's head (see <see cref="LoopStatement.AdapterSourcePath"/>).</summary>
    private readonly Dictionary<string, string> _adapterSourcePaths = new(comparer: StringComparer.Ordinal);

    /// <summary>The path of the container an adapter chain walks (<c>xs</c> for <c>xs.where(...).select(...)</c>):
    /// every call on the way gives an iterator (it has <c>emit</c>), so it walks its receiver in place. Null when a
    /// call gives anything else (<c>xs.copy()</c> makes a list of its own) or the head is not a name, member variable
    /// or element.</summary>
    private string? AdapterHeadPath(CallExpression walk)
    {
        Expression current = walk;
        while (current is CallExpression { Callee: MemberExpression receiver, ResolvedType: { } walked } &&
               (_registry.LookupMemberRoutine(type: walked, memberRoutineName: "emit", isFailable: true) != null ||
                KeepsPlaceOf(holder: walked, place: receiver.Object.ResolvedType)))
        {
            current = receiver.Object;
        }

        return current is CallExpression ? null : PlacePath(place: current);

        static string? PlacePath(Expression place)
        {
            return place switch
            {
                IdentifierExpression id => id.Name,
                MemberExpression { Object: var inner, MemberName: var field } =>
                    PlacePath(place: inner) is { } prefix ? $"{prefix}.{field}" : null,
                IndexExpression { Object: var container } =>
                    PlacePath(place: container) is { } owner ? $"{owner}[]" : null,
                _ => null
            };
        }
    }

    /// <summary>Whether <paramref name="holder"/> keeps the address of a <paramref name="place"/> (a member variable
    /// <c>Hijacked[TSource]</c> with <c>TSource</c> the receiver's type): an adapter like <c>WhereIterable</c> walks
    /// the container it was made from in place.</summary>
    private static bool KeepsPlaceOf(TypeSymbol holder, TypeSymbol? place)
    {
        if (place == null)
        {
            return false;
        }

        IEnumerable<MemberVariableInfo> members = holder switch
        {
            EntityTypeSymbol entity => entity.MemberVariables,
            RecordTypeSymbol record => record.MemberVariables,
            _ => []
        };
        return members.Any(predicate: m => m.Type is { BareName: Declaration.RuntimeContract.Hijacked, TypeArguments: [{ } kept] } &&
                                           (kept.Name == place.Name ||
                                            Wrappers.GetWrapperInnerType(wrapperType: place)?.Name == kept.Name));
    }

    /// <summary>Whether a value of <paramref name="type"/> is walked: an iterator (it has <c>emit</c>) or an adapter
    /// an <c>each</c> loop asks for one (it has <c>iter</c>).</summary>
    private bool Walks(TypeSymbol type)
    {
        return _registry.LookupMemberRoutine(type: type, memberRoutineName: "emit", isFailable: true) != null ||
               _registry.LookupMemberRoutine(type: type, memberRoutineName: "emitter") != null;
    }

    /// <summary>
    /// Whether a walk starts at a read-only place: following the receivers of a call chain
    /// (<c>boxes.where(...)</c>), member variables and elements back to where it begins, a <c>Viewing</c> token
    /// on the way, <c>me</c> in a <c>@readonly</c> routine, or an iterator or adapter that already walks from one.
    /// </summary>
    private bool StartsReadOnly(Expression place)
    {
        if (place.ResolvedType is { } type && Wrappers.IsReadOnlyWrapper(type: type))
        {
            return true;
        }

        return place switch
        {
            IdentifierExpression { Name: "me" } => _currentRoutine is { IsReadOnly: true },
            IdentifierExpression walk => _readOnlyIterators.Contains(item: walk.Name),
            CallExpression { Callee: MemberExpression receiver } => StartsReadOnly(place: receiver.Object),
            MemberExpression member => StartsReadOnly(place: member.Object),
            IndexExpression element => StartsReadOnly(place: element.Object),
            _ => false
        };
    }

    /// <summary>The element token an <c>each</c> loop binds when its walk starts read-only: <c>Viewing[T]</c> in place
    /// of the <c>Modifying[T]</c> the iterator hands out. Any other type stays as it is.</summary>
    private TypeSymbol ReadOnlyElement(TypeSymbol element)
    {
        return element is { BareName: Declaration.RuntimeContract.Modifying } &&
               Wrappers.GetWrapperInnerType(wrapperType: element) is { } inner &&
               _registry.LookupType(name: Declaration.RuntimeContract.Viewing) is { } viewingDef
            ? _registry.GetOrCreateResolution(genericDef: viewingDef, typeArguments: [inner])
            : element;
    }

    /// <summary>
    /// An entity element read into a variable (<c>var b = boxes[0]</c>) binds a token on the element, with the
    /// permission the container gives: a write token when the container can be changed here
    /// (<c>boxes.modify_at(index: 0)</c>), so a member write through <c>b</c> changes the element in the container, and a
    /// read token when it is reached read-only (<c>boxes.view_at(index: 0)</c>). Returns the token type, or null when
    /// the initializer is not such a read (a range slice copies; a container without <c>view_at</c> has no element
    /// token).
    /// </summary>
    private TypeSymbol? ElementTokenBinding(Expression initializer, TypeSymbol elementType)
    {
        if (!_registry.Rules.ChecksAccessTokens || elementType is not EntityTypeSymbol ||
            initializer is not IndexExpression { Index: not RangeExpression } element ||
            element.Index.ResolvedType is RecordTypeSymbol { GenericDefinition.Name: "Range" } ||
            ElementContainerType(container: element.Object) is not { } containerType ||
            _registry.LookupMemberRoutine(type: containerType, memberRoutineName: "view_at", isFailable: true) is null ||
            _registry.LookupType(name: Declaration.RuntimeContract.Viewing) is not { } viewingDef ||
            _registry.LookupType(name: Declaration.RuntimeContract.Modifying) is not { } modifyingDef)
        {
            return null;
        }

        // A write token on the container (`s: Modifying[List[Dict[K, V]]]`) gives write permission whatever the
        // binding that holds it.
        bool throughWriteToken = element.Object.ResolvedType is { BareName: Declaration.RuntimeContract.Modifying };
        bool writable = !StartsReadOnly(place: element.Object) &&
                        (throughWriteToken || RootVariable(place: element.Object) is not { IsModifiable: false }) &&
                        _registry.LookupMemberRoutine(type: containerType, memberRoutineName: "modify_at",
                            isFailable: true) is not null;
        TypeSymbol tokenType = _registry.GetOrCreateResolution(genericDef: writable ? modifyingDef : viewingDef,
            typeArguments: [elementType]);
        element.ReadsElementToken = true;
        element.ResolvedType = tokenType;
        return tokenType;
    }

    /// <summary>The container an element is read from: the container itself, or the container a
    /// <c>Viewing</c>/<c>Modifying</c> token on it reaches (<c>s</c> in <c>s[0]</c> with
    /// <c>s: Modifying[List[Dict[K, V]]]</c>). Null when the container has no type yet.</summary>
    private static TypeSymbol? ElementContainerType(Expression container)
    {
        return container.ResolvedType is { BareName: Declaration.RuntimeContract.Viewing or
                   Declaration.RuntimeContract.Modifying, TypeArguments: [{ } inner] }
            ? inner
            : container.ResolvedType;
    }

    /// <summary>The variable a place starts from (<c>team</c> in <c>team.members[0]</c>), or null when it starts
    /// elsewhere (a call, <c>me</c>).</summary>
    private VariableInfo? RootVariable(Expression place)
    {
        return place switch
        {
            IdentifierExpression { Name: not "me" } id => _registry.LookupVariable(name: id.Name),
            MemberExpression member => RootVariable(place: member.Object),
            IndexExpression index => RootVariable(place: index.Object),
            _ => null
        };
    }

    /// <summary>
    /// A local never takes a build-time constant AS its type. `var i = K` (K a const-generic parameter
    /// such as `needs U64 K`, or its monomorphized <see cref="ConstGenericValueTypeSymbol"/>) binds a
    /// RUNTIME value of the constant's underlying integer type. Codegen emits every identifier typed
    /// <see cref="ConstGenericValueTypeSymbol"/> as the literal, so a local typed that way would freeze
    /// to the constant: `i = i - 1` would never be read back and a loop over `i` would never advance.
    /// Returns <paramref name="type"/> unchanged for anything that is not a const-generic value.
    /// </summary>
    private TypeSymbol RuntimeTypeOfConstGeneric(TypeSymbol type, Expression initializer)
    {
        if (type is not (GenericParameterTypeSymbol or ConstGenericValueTypeSymbol))
        {
            return type;
        }

        // The declared value type comes from the parameter's `needs <Type> K` constraint — on the
        // routine/owner in scope, or on the generic definition of a monomorphized body.
        string? paramName = type switch
        {
            GenericParameterTypeSymbol gp => gp.Name,
            _ => (initializer as IdentifierExpression)?.Name
        };
        string? boundName = null;
        if (paramName != null)
        {
            IEnumerable<GenericConstraintDeclaration> constraints = ActiveConstraintsFor(paramName: paramName)
               .Concat(second: _currentRoutine?.GenericDefinition?.GenericConstraints?.Where(predicate: c =>
                    c.ParameterName == paramName) ?? []);
            boundName = constraints
                       .FirstOrDefault(predicate: c => c is
                        {
                            ConstraintType: ConstraintKind.ConstGeneric, ConstraintTypes.Count: > 0
                        })
                      ?.ConstraintTypes![index: 0].Name;
        }

        if (boundName == null && type is ConstGenericValueTypeSymbol constVal)
        {
            boundName = constVal.ExplicitTypeName ?? "U64";
        }

        return boundName != null &&
               LookupTypeWithImports(name: boundName) is { Category: not TypeCategory.Protocol } underlying
            ? underlying
            : type;
    }

    /// <summary>
    /// Registers post-declaration side-effects for variables with special tracking needs:
    /// Guarded/Witnessed handle identity (RF-S630) and Lookup dismantlement tracking (#161).
    /// </summary>
    private void TrackSpecialVariableRegistrations(VariableDeclaration varDecl, TypeSymbol varType)
    {
        // RF-S630: track the controller identity of a Guarded/Witnessed handle so the
        // readers-XOR-writer check keys on the shared DATA, not the variable name — a clone
        // (`var s2 = s.share()`) inherits `s`'s identity and so conflicts with it.
        if (ChecksOwnershipHere &&
            varType.BareName is Declaration.RuntimeContract.Guarded
                or Declaration.RuntimeContract.Witnessed)
        {
            RecordSharedHandleIdentity(name: varDecl.Name, initializer: varDecl.Initializer);
        }

    }

    /// <summary>
    /// Enforces the copy/aliasing restrictions on a variable declaration's initializer: single-owner
    /// entities cannot be viewed, variants / Result / Lookup cannot be copied variable-to-variable,
    /// scoped access tokens cannot bind at all, and other non-trivially-copyable wrappers require an
    /// explicit copy verb.
    /// </summary>
    private void CheckVariableCopyRestrictions(VariableDeclaration varDecl, TypeSymbol varType)
    {
        // RazorForge: keeping a value with NO STORE. The dividing line for an implicit bind is whether
        // the value can be STORED — i.e. whether its type obeys `Assignable` (every value type does: a
        // trivial record via the auto-derived bitwise store, a managed leaf like Text via its retaining
        // store). A single-owner `entity` deliberately obeys no `Assignable`, so it has no copy of its own.
        // A bind whose initializer is a VIEW of a value someone else owns — a bare reference (`var x = a`)
        // or a scalar element read (`var x = a[i]`) — would make TWO owners of that one entity, so it is
        // rejected. A fresh owned producer (creator / in-flight call) and an explicit `steal` are MOVES,
        // not views, and are allowed. (SF entity elements are `Roamed`, which DOES obey Assignable via its
        // refcount retain — so this never fires in Suflae.)
        // EXCEPTION: a RANGE slice (`var sub = a[i til j]`) is NOT a view — it binds the `getitem(range)`
        // overload, which returns a FRESH owned sub-collection (a copy, gated on the element being
        // Copyable), exactly like a call. So a range-indexed `IndexExpression` is a move, not a two-owner
        // alias — excluded here, same as `a.copy()` or a creator call.
        // The store-less set is EXACTLY the entities: every value category (records, tuples, routines,
        // SIMD vectors, generic records) is copyable via an auto-derived bitwise or field-wise store, so
        // `obeys Assignable` under-reports them — `EntityTypeSymbol` is the precise predicate for "has no
        // store of its own". (SF entity elements are `Roamed`, a record wrapper, not an `EntityTypeSymbol`.)
        // A tuple element access (`_t.item0`) is how `var (a, b) = expr` destructuring lowers: the tuple
        // is a CONSUMED temporary, so each element MOVES out — not a view of a persisting owner. Exclude it
        // (Object is a TupleTypeSymbol) so channel/pair destructuring of entity elements stays a legal move.
        if (ChecksHolderOf(type: varType) && varDecl.Initializer != null &&
            ReadsKeptEntity(value: varDecl.Initializer, includeVariables: true) &&
            MayHoldEntity(type: varType))
        {
            ReportError(code: SemanticDiagnosticCode.BareEntityAssignment,
                message: KeptEntityMessage(action: $"You are keeping in '{varDecl.Name}'",
                    value: varDecl.Initializer, type: varType),
                location: varDecl.Location);
        }

        // A bare entity-typed variable is the normal bound form.
        // Duplicate-handle prohibition is enforced at copy sites (the block above).

        // A variant is copied like the values it holds: one holding only values copies (decided 2026-10-14), one that
        // may hold an entity is the kept-entity case above (RF-S413).

        // #81: Result/Lookup cannot be copied from variable to variable
        // `var r = check parse(data)` then `when r` is allowed (call result)
        // `var r2 = r1` where r1: Result[T] is not allowed (variable copy)
        if (varDecl.Initializer is IdentifierExpression && IsCarrierType(type: varType) &&
            !IsMaybeType(type: varType))
        {
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable,
                message: $"'{varType.Name}' cannot be copied to another variable. " +
                         "Dismantle it immediately with 'when', '??', or 'if is'.",
                location: varDecl.Location);
        }

        // A token (`var v = a.view()`, `var m = a.modify()`, `var b = boxes[0]`) is a variable like any other:
        // RazorForge's TokenLifetimeChecker follows where it came from and rejects a use after that object changed.
        // A Guarded's `view()` / `modify()` is held for one call, so it goes only where an argument does (RF-S652,
        // GuardAccessArgumentChecker).
        if (!(_registry.Rules.ChecksAccessTokens && IsInlineOnlyTokenType(type: varType)))
        {
            CheckImplicitWrapperCopyOnInit(varDecl: varDecl, varType: varType);
        }
    }

    /// <summary>
    /// Phase 1 check: reports an error when the initializer is an identifier or member expression and the
    /// type is not trivially assignable (contains a managed leaf that requires an explicit copy verb).
    /// </summary>
    private void CheckImplicitWrapperCopyOnInit(VariableDeclaration varDecl, TypeSymbol varType)
    {
        if (!ChecksOwnershipHere)
        {
            return;
        }

        if (varDecl.Initializer is not (IdentifierExpression or MemberExpression))
        {
            return;
        }

        if (Wrappers.IsTriviallyAssignable(type: varType))
        {
            return;
        }

        (string Wrapper, string Path)? hint = Wrappers.FindNonTriviallyAssignableWrapper(type: varType);
        if (hint != null)
        {
            string verb = Wrappers.NonTriviallyAssignableWrappers[key: hint.Value.Wrapper];
            string fieldNote = hint.Value.Path == "<value>"
                ? $"type '{varType.Name}' is a '{hint.Value.Wrapper}[…]' wrapper"
                : $"field '{hint.Value.Path}' of type '{hint.Value.Wrapper}[…]'";
            ReportError(code: SemanticDiagnosticCode.ImplicitWrapperCopy,
                message: verb == Wrappers.ScopedNoEscapeHint
                    ? TokenCopyMessage(copied: varDecl.Initializer, type: varType)
                    : $"Implicit copy of '{varDecl.Name}': {fieldNote} requires an explicit copy verb. " +
                      $"Spell out '{verb}' at the copy site, or reconstruct the record with each field's verb.",
                location: varDecl.Location);
        }
    }

    /// <summary>The warning for a <c>to[Work]</c> conversion whose Work nothing starts.</summary>
    private static string WorkNeverStartedMessage()
    {
        return "This builds a Work that nothing starts, so its work never runs. Start it at once " +
               "with `f.to[Work](...).start_now()`, or keep it in a variable, start it, and `.retrieve()` it later.";
    }

    /// <summary>
    /// Reports a binding from outside a loop that a turn of the loop takes with <c>steal</c> and goes on without: the
    /// next turn would find it gone (RF-S616). A take followed by leaving the loop (`return`, `break`) is fine: the
    /// branch that leaves does not reach the loop's next turn.
    /// </summary>
    private void ReportTakenInLoop(LoopStatement loop, HashSet<string> before, HashSet<string> atContinue)
    {
        // A turn that reaches a `continue` goes on to the next one, whatever the rest of the body does.
        IEnumerable<string> ended = atContinue;
        if (!ControlFlowExits.HasDefiniteExit(statement: loop.Body))
        {
            ended = ended.Concat(second: _deadrefVariables); // a turn that reaches the body's end goes on too
        }

        foreach (string name in ended.Where(predicate: n => !before.Contains(item: n) &&
                                                           !n.StartsWith(value: UnassignedMark,
                                                               comparisonType: StringComparison.Ordinal))
                                     .Distinct()
                                     .Order(comparer: StringComparer.Ordinal))
        {
            if (_registry.LookupVariable(name: name) is null)
            {
                continue;
            }

            ReportError(code: SemanticDiagnosticCode.StealOnSomePaths,
                message: $"'{name}' is {EndedHow(name: name)} in a turn of this loop that goes on to the next turn, " +
                         $"where it is gone. End it where the loop ends ('return', 'break' right after), or declare it " +
                         "inside the loop so each turn has its own.",
                location: loop.Location);
        }
    }

    /// <summary>How a binding was ended, for a message: "taken with 'steal'" or "ended with 'drop'".</summary>
    private string EndedHow(string name)
    {
        return _droppedVariables.Contains(item: name)
            ? "ended with 'drop'"
            : "taken with 'steal'";
    }

    private void AnalyzeExpressionStatement(ExpressionStatement expr)
    {
        // Analyze the expression for side effects and type validation
        TypeSymbol exprType = AnalyzeExpression(expression: expr.Expression);

        // Note: UnhandledCrashableCall check moved to AnalyzeCallExpression to catch all contexts
        // (return values, assignments, nested expressions — not just expression statements)

        // `f.to[Work]()` on its own builds a Work nothing starts: the call never runs.
        if (expr.Expression is WorkExpression agent && IsWorkType(type: exprType))
        {
            ReportWarning(code: SemanticWarningCode.AsyncWorkNeverLaunched,
                message: WorkNeverStartedMessage(),
                location: agent.Location);
            return;
        }

        // Check if this is a call expression with a non-None return value
        // If so, warn that the return value is unused (use 'drop' to drop it on purpose)
        if (expr.Expression is CallExpression call && !exprType.IsNone)
        {
            // Get a readable name for the routine being called
            string routineName = call.Callee switch
            {
                IdentifierExpression id => id.Name,
                MemberExpression member => member.MemberName,
                _ => "routine"
            };

            // A `Work[T]` result thrown away is never started: nothing runs its work. Point at the verbs
            // that start it.
            if (IsWorkType(type: exprType))
            {
                ReportWarning(code: SemanticWarningCode.AsyncWorkNeverLaunched,
                    message:
                    $"The Work '{routineName}()' returns is never started, so its work never runs. Call " +
                    "`.start_now()` on it to start it, and `.retrieve()` later to wait for its value.",
                    location: call.Location);
            }
            else if (IsDiscardable(routine: call.ResolvedRoutine))
            {
                // A by-product result its author marked @discardable: leaving it is fine in both languages.
            }
            else if (Builder.Frontends.Languages.For(language: Builder.Frontends.Languages.OfFile(fileName: _currentFilePath))
                     .MarksDroppedResults)
            {
                ReportWarning(code: SemanticWarningCode.UnusedRoutineReturnValue,
                    message: $"Return value of '{routineName}()' ({exprType.Name}) is unused. " +
                             "Use 'drop' to drop the result on purpose, or bind it to a variable.",
                    location: call.Location,
                    fix: new SuggestedFix(Title: "Prepend 'drop'",
                        Edits: [TextEdit.Insert(line: expr.Location.Line, column: expr.Location.Column, text: "drop ")]));
            }
            else
            {
                // Suflae has no `drop`: the line builds as written, and a notice asks whether the result was meant.
                ReportNotice(code: SemanticWarningCode.UnusedRoutineReturnValue,
                    message: $"You call '{routineName}()' for its result ({exprType.Name}) and leave the result " +
                             "unused. To keep it, put it in a variable (`var x = ...`). If you call it only for what " +
                             "it does, leave the line as it is.",
                    location: call.Location);
            }
        }
    }

    /// <summary>Whether the result of <paramref name="routine"/> is a by-product its author marked
    /// <c>@discardable</c> (<c>Set.add</c>'s Bool): dropping it needs no <c>drop</c>.</summary>
    private static bool IsDiscardable(RoutineInfo? routine)
    {
        return DeclaredWith(routine: routine, annotation: "discardable");
    }

    /// <summary>Whether the routine a call reaches was declared with <paramref name="annotation"/>, looking through a
    /// recovery variant, a generic instance and a wrapper's forwarder to the routine as written.</summary>
    private static bool DeclaredWith(RoutineInfo? routine, string annotation)
    {
        for (RoutineInfo? declared = routine?.RecoveryOf ?? routine; declared != null;
             declared = declared.WrapperForwarderInnerMemberRoutine)
        {
            if ((declared.GenericDefinition ?? declared).Annotations.Contains(value: annotation))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="type"/> is a Work: a language's own Work entity, held as such or as its
    /// handle.</summary>
    internal static bool IsWorkType(TypeSymbol type)
    {
        if (type is RecordTypeSymbol
            {
                GenericDefinition.Name: Declaration.RuntimeContract.Roamed, TypeArguments: [{ } shared]
            })
        {
            type = shared;
        }

        return type switch
        {
            RecordTypeSymbol record => (record.GenericDefinition?.Name ?? record.Name) == "Work",
            EntityTypeSymbol entity => (entity.GenericDefinition?.Name ?? entity.Name) == "Work",
            _ => false
        };
    }

    private void AnalyzeAssignmentStatement(AssignmentStatement assign)
    {
        // Re-binding clears prior deadref state for a simple identifier target.
        // Mirrors AnalyzeVariableDeclaration: an assignment establishes a fresh value,
        // so a previously stolen-from binding becomes live again at this point.
        if (assign.Target is IdentifierExpression rebindId)
        {
            _deadrefVariables.Remove(item: rebindId.Name);
        }

        NoteAssignedPlace(target: assign.Target);

        if (PlaceName(place: assign.Target) is { } assignedPlace)
        {
            NoteStealDestination(value: assign.Value, destination: $"'{assignedPlace}'");
        }

        // #173: Tuple assignment destructuring — (a, b) = (b, a)
        if (assign is { Target: TupleLiteralExpression places, Value: TupleLiteralExpression values } &&
            Desugaring.Passes.ControlFlowLoweringPass.SwapSources(left: places.Elements, right: values.Elements) is
                { } swapSources)
        {
            // A swap analyzed before desugaring turned it into a PlaceSwapStatement (a stdlib body checked on its own).
            AnalyzePlaceSwap(swap: new PlaceSwapStatement(Places: places.Elements, Sources: swapSources,
                Location: assign.Location));
            return;
        }

        if (assign.Target is TupleLiteralExpression tupleLhs)
        {
            AnalyzeTupleDestructuringAssignment(assign: assign, tupleLhs: tupleLhs);
            return;
        }

        ReportAssignWhileMatched(target: assign.Target);
        ForgetNarrowing(target: assign.Target);
        // A global's initialization at the top of start() names the global of its own file's module, which is not
        // the entry file's when two modules have a global of that name.
        TypeSymbol targetType = assign.IsGlobalInit
            ? InImportStateOf(file: assign.Location.FileName,
                analyze: () => AnalyzeExpression(expression: assign.Target))
            : AnalyzeExpression(expression: assign.Target);
        // The name assigned to is not a read of what it holds.
        if (assign.Target is IdentifierExpression assignedName)
        {
            _pendingDangerousCallables.Remove(key: assignedName);
        }

        // A global's initializer stands at the top of start() but was written in the global's own file: its names
        // resolve there (that file's imports and module), not in the entry file.
        TypeSymbol valueType = assign.IsGlobalInit
            ? InImportStateOf(file: assign.Location.FileName,
                analyze: () => AnalyzeExpression(expression: assign.Value, expectedType: targetType))
            : AnalyzeExpression(expression: assign.Value, expectedType: targetType);
        if (assign.Target is IdentifierExpression localTarget)
        {
            TakeDangerousCallableIntoLocal(name: localTarget.Name, value: assign.Value);
        }

        // Check if target is assignable (variable, member variable, or index)
        if (!IsAssignableTarget(target: assign.Target))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                message:
                "Invalid assignment target. Only variables, member accesses (e.g. obj.field), and indexed expressions (e.g. list[i]) can be assigned to.",
                location: assign.Target.Location);
            return;
        }

        // Check modifiability (a global's own initialization at the top of start() excepted)
        if (assign.Target is IdentifierExpression id)
        {
            VariableInfo? varInfo = id.ResolvedVariable ?? _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsModifiable: false } && !assign.IsGlobalInit)
            {
                ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                    message: ImmutableBindingMessage(name: id.Name, what: "it can't be assigned"),
                    location: assign.Location);
            }
        }

        // Validate member variable write access (setter visibility)
        if (assign.Target is MemberExpression member)
        {
            ValidateMemberAssignmentTarget(assign: assign, member: member);
        }

        if (assign.Target is IndexExpression element)
        {
            CheckIndexWriteReshape(index: element, location: assign.Location);
        }

        // #81: Result/Lookup cannot be copied from variable to variable via assignment
        if (assign.Value is IdentifierExpression && IsCarrierType(type: valueType) &&
            !IsMaybeType(type: valueType))
        {
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable,
                message: $"'{valueType.Name}' cannot be copied to another variable. " +
                         "Dismantle it immediately with 'when', '??', or 'if is'.",
                location: assign.Location);
        }

        // Check type compatibility
        if (!IsAssignableTo(source: valueType, target: targetType))
        {
            ReportError(code: SemanticDiagnosticCode.AssignmentTypeMismatch,
                message:
                $"Cannot assign value of type '{valueType.Name}' to target of type '{targetType.Name}'.",
                location: assign.Location);
        }
    }

    /// <summary>
    /// <c>(a, b) = (b, a)</c>: each place is written, so it must be a place that can be (a local, a parameter, a
    /// member variable), and each takes the value of another, so the places that trade values have one type.
    /// </summary>
    private void AnalyzePlaceSwap(PlaceSwapStatement swap)
    {
        var types = new List<TypeSymbol>(capacity: swap.Places.Count);
        foreach (Expression place in swap.Places)
        {
            TypeSymbol type = AnalyzeExpression(expression: place);

            types.Add(item: type);
            if (place is IndexExpression)
            {
                ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                    message: "An element of a collection is no place a swap can move a value into: swap two " +
                             "elements with 'xs.swap(first: i, second: j)'.",
                    location: place.Location);
                continue;
            }

            if (!IsAssignableTarget(target: place))
            {
                ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                    message: "Every element on the left of '(a, b) = (b, a)' is a place: a variable or a member " +
                             "variable.",
                    location: place.Location);
                continue;
            }

            if (place is IdentifierExpression id && _registry.LookupVariable(name: id.Name) is { IsModifiable: false })
            {
                ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                    message: ImmutableBindingMessage(name: id.Name, what: "it can't be assigned"),
                    location: place.Location);
            }

            if (place is MemberExpression member)
            {
                ValidateMemberAssignmentTarget(assign: new AssignmentStatement(Target: place, Value: place,
                    Location: swap.Location), member: member);
            }
        }

        for (int i = 0; i < swap.Places.Count; i++)
        {
            TypeSymbol into = types[index: i];
            TypeSymbol from = types[index: swap.Sources[index: i]];
            if (into is not ErrorTypeSymbol && from is not ErrorTypeSymbol && into.FullName != from.FullName)
            {
                ReportError(code: SemanticDiagnosticCode.AssignmentTypeMismatch,
                    message: $"'{PlaceText(place: swap.Places[index: i])}' is {Article.For(name: into.Name)} '{into.Name}' and would take the value of " +
                             $"'{PlaceText(place: swap.Places[index: swap.Sources[index: i]])}', {Article.For(name: from.Name)} '{from.Name}': places that swap " +
                             "values have one type.",
                    location: swap.Places[index: i].Location);
            }
        }
    }

    /// <summary>A place as it is written: <c>a</c>, <c>me.next</c>.</summary>
    private static string PlaceText(Expression place)
    {
        return place switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression member => $"{PlaceText(place: member.Object)}.{member.MemberName}",
            _ => "the place"
        };
    }

    /// <summary>
    /// #173: Analyzes a tuple-destructuring assignment `(a, b) = expr` — each LHS element must be an
    /// assignable, modifiable target, and the RHS tuple arity must match the target count.
    /// </summary>
    private void AnalyzeTupleDestructuringAssignment(AssignmentStatement assign,
        TupleLiteralExpression tupleLhs)
    {
        TypeSymbol rhsType = AnalyzeExpression(expression: assign.Value);

        // Verify all elements of the LHS tuple are assignable targets
        foreach (Expression element in tupleLhs.Elements)
        {
            AnalyzeExpression(expression: element);
            if (!IsAssignableTarget(target: element))
            {
                ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                    message:
                    "All elements of tuple destructuring must be assignable targets (variables, member accesses, or indices).",
                    location: element.Location);
            }

            // Check modifiability for identifier elements
            if (element is IdentifierExpression elemId)
            {
                VariableInfo? varInfo = _registry.LookupVariable(name: elemId.Name);
                if (varInfo is { IsModifiable: false })
                {
                    ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                        message: ImmutableBindingMessage(name: elemId.Name, what: "it can't be assigned"),
                        location: assign.Location);
                }
            }
        }

        // A tuple of places takes the same places in another order (a swap, PlaceSwapStatement). Anything else
        // would need each value worked out first and then put in order, which the builder doesn't do.
        ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
            message: "A tuple on the left takes the same places in another order on the right: '(a, b) = (b, a)'. " +
                     "To put other values in, assign each place on its own line.",
            location: assign.Location);

        // Check that RHS is a tuple with matching arity
        if (rhsType is TupleTypeSymbol tupleType &&
            tupleLhs.Elements.Count != tupleType.ElementTypes.Count)
        {
            ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                message:
                $"Tuple destructuring has {tupleLhs.Elements.Count} targets but the value has {tupleType.ElementTypes.Count} elements.",
                location: assign.Location);
        }
    }

    /// <summary>
    /// Validates writing to a member-access assignment target: rejects writes through a read-only
    /// wrapper, checks setter visibility, blocks assigning to a member of a preset variable, and
    /// blocks mutating `me` from a @readonly member routine.
    /// </summary>
    private void ValidateMemberAssignmentTarget(AssignmentStatement assign,
        MemberExpression member)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

        ReportWriteThroughReadOnly(objectType: objectType, memberName: member.MemberName, location: assign.Location);
        ReportWriteThroughReadOnlyPlace(member: member, objectType: objectType, location: assign.Location);

        ValidateMemberVariableWriteAccess(objectType: objectType,
            memberVariableName: member.MemberName,
            location: assign.Location);

        ReportWriteIntoReadOnlyBinding(member: member, location: assign.Location);

        // Check if we're in a @readonly memberRoutine trying to mutate 'me' (RazorForge-only; Suflae
        // hides @readonly/@reshaping).
        if (_registry.CompilationRules.ChecksReadonly &&
            _currentRoutine is { IsReadOnly: true } &&
            member.Object is IdentifierExpression { Name: "me" })
        {
            ReportError(code: SemanticDiagnosticCode.MutationInReadonlyMemberRoutine,
                message:
                $"Cannot mutate member variable '{member.MemberName}' in a @readonly member routine. " +
                "Take '@readonly' off the routine to let it change 'me'.",
                location: assign.Location);
        }
    }

    #endregion

    #region Type Expression Parsing Helpers

    /// <summary>
    /// Parses a textual type expression like "List[Byte]" or "Dict[K, List[V]]"
    /// into a synthetic <see cref="TypeExpression"/> AST node. Used to recover
    /// owner-type bindings from a routine's name string when the AST does not
    /// carry the owner as a separate node. Returns null on malformed input.
    /// </summary>
    internal static TypeExpression? ParseTypeExpressionString(string text, SourceLocation location)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(value: text))
        {
            return null;
        }

        // A realm qualifier (`SF::System.List[T]`) is the expression's realm, not part of its name.
        string? realm = null;
        int realmIdx = text.IndexOf(value: "::", comparisonType: StringComparison.Ordinal);
        if (realmIdx > 0 && (text.IndexOf(value: '[') < 0 || realmIdx < text.IndexOf(value: '[')))
        {
            realm = text[..realmIdx];
            text = text[(realmIdx + 2)..];
        }

        int bracketIdx = text.IndexOf(value: '[');
        if (bracketIdx < 0)
        {
            return new TypeExpression(Name: text, GenericArguments: null, Location: location, Realm: realm);
        }

        if (!text.EndsWith(value: ']'))
        {
            return null;
        }

        string head = text[..bracketIdx];
        string inner = text[(bracketIdx + 1)..^1];

        List<TypeExpression> args = [];
        int depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[index: i];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                TypeExpression? arg = ParseTypeExpressionString(
                    text: inner[start..i],
                    location: location);
                if (arg == null)
                {
                    return null;
                }

                args.Add(item: arg);
                start = i + 1;
            }
        }

        TypeExpression? lastArg =
            ParseTypeExpressionString(text: inner[start..], location: location);
        if (lastArg == null)
        {
            return null;
        }

        args.Add(item: lastArg);

        return new TypeExpression(Name: head, GenericArguments: args, Location: location, Realm: realm);
    }

    #endregion

    /// <summary>
    /// Puts <c>me.hijack().invalidate()</c> (give back the entity's own box) right before every <c>return</c> of a
    /// written entity <c>destruct</c>/<c>roam_free</c>. A return already preceded by it is left alone, so running this
    /// twice, or over a body that still frees its box by hand, frees the box once.
    /// </summary>
    private static Statement GiveBackBoxBeforeReturns(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    if (block.Statements[index: i] is ReturnStatement ret)
                    {
                        if (i == 0 || !IsBoxGiveBack(statement: block.Statements[index: i - 1]))
                        {
                            block.Statements.Insert(index: i, item: BoxGiveBack(location: ret.Location));
                            i++;
                        }

                        continue;
                    }

                    block.Statements[index: i] = GiveBackBoxBeforeReturns(statement: block.Statements[index: i]);
                }

                return block;
            case ReturnStatement ret:
                return new BlockStatement(Statements: [BoxGiveBack(location: ret.Location), ret], Location: ret.Location);
            case IfStatement guard when IsHollowGuard(statement: guard):
                // A hollow entity has no box to give back.
                return guard;
            case IfStatement ifs:
                return ifs with
                {
                    ThenStatement = GiveBackBoxBeforeReturns(statement: ifs.ThenStatement),
                    ElseStatement = ifs.ElseStatement == null ? null : GiveBackBoxBeforeReturns(statement: ifs.ElseStatement)
                };
            case WhileStatement w:
                return w with { Body = GiveBackBoxBeforeReturns(statement: w.Body) };
            case LoopStatement loop:
                return loop with { Body = GiveBackBoxBeforeReturns(statement: loop.Body) };
            case DangerStatement danger:
                GiveBackBoxBeforeReturns(statement: danger.Body);
                return danger;
            case UsingStatement u:
                return u with { Body = GiveBackBoxBeforeReturns(statement: u.Body) };
            case AccessStatement access:
                return access with { Body = GiveBackBoxBeforeReturns(statement: access.Body) };
            case WhenStatement whenStatement:
                for (int c = 0; c < whenStatement.Clauses.Count; c++)
                {
                    WhenClause clause = whenStatement.Clauses[index: c];
                    whenStatement.Clauses[index: c] = clause with { Body = GiveBackBoxBeforeReturns(statement: clause.Body) };
                }

                return whenStatement;
            default:
                return statement;
        }
    }

    /// <summary>The statement <c>if me.hijack().is_hollow() then return</c>: a <c>destruct</c> leaves a hollow entity (one
    /// whose reference is all zero) alone.</summary>
    private static IfStatement HollowGuard(SourceLocation location)
    {
        var me = new IdentifierExpression(Name: "me", Location: location);
        var hijack = new CallExpression(
            Callee: new MemberExpression(Object: me, MemberName: Declaration.RuntimeContract.RawPointer.Hijack,
                Location: location),
            Arguments: [],
            Location: location) { IsSynthesizedLowering = true };
        var isHollow = new CallExpression(
            Callee: new MemberExpression(Object: hijack, MemberName: Declaration.RuntimeContract.RawPointer.IsHollow,
                Location: location),
            Arguments: [],
            Location: location) { IsSynthesizedLowering = true };
        return new IfStatement(Condition: isHollow,
            ThenStatement: new BlockStatement(Statements: [new ReturnStatement(Value: null, Location: location)],
                Location: location),
            ElseStatement: null,
            Location: location);
    }

    /// <summary>Whether <paramref name="statement"/> is the guard <see cref="HollowGuard"/> makes.</summary>
    private static bool IsHollowGuard(Statement statement)
    {
        return statement is IfStatement
        {
            ElseStatement: null,
            Condition: CallExpression
            {
                Callee: MemberExpression
                {
                    MemberName: Declaration.RuntimeContract.RawPointer.IsHollow,
                    Object: CallExpression
                    {
                        Callee: MemberExpression
                        {
                            MemberName: Declaration.RuntimeContract.RawPointer.Hijack,
                            Object: IdentifierExpression { Name: "me" }
                        }
                    }
                }
            }
        };
    }

    /// <summary>The statement <c>me.hijack().invalidate()</c>.</summary>
    private static ExpressionStatement BoxGiveBack(SourceLocation location)
    {
        var me = new IdentifierExpression(Name: "me", Location: location);
        var hijack = new CallExpression(
            Callee: new MemberExpression(Object: me, MemberName: Declaration.RuntimeContract.RawPointer.Hijack,
                Location: location),
            Arguments: [],
            Location: location) { IsSynthesizedLowering = true };
        var invalidate = new CallExpression(
            Callee: new MemberExpression(Object: hijack, MemberName: Declaration.RuntimeContract.RawPointer.Invalidate,
                Location: location),
            Arguments: [],
            Location: location) { IsSynthesizedLowering = true };
        return new ExpressionStatement(Expression: invalidate, Location: location);
    }

    /// <summary>Whether <paramref name="statement"/> is <c>me.hijack().invalidate()</c>.</summary>
    private static bool IsBoxGiveBack(Statement statement)
    {
        return statement is ExpressionStatement
        {
            Expression: CallExpression
            {
                Callee: MemberExpression
                {
                    MemberName: Declaration.RuntimeContract.RawPointer.Invalidate,
                    Object: CallExpression
                    {
                        Callee: MemberExpression
                        {
                            MemberName: Declaration.RuntimeContract.RawPointer.Hijack,
                            Object: IdentifierExpression { Name: "me" }
                        }
                    }
                }
            }
        };
    }

    private void ValidateDestroyDeclaration(RoutineDeclaration routine)
    {
        bool isDestroyDecl = routine.Name == "destruct" ||
                             routine.Name.EndsWith(value: ".destroy",
                                 comparisonType: StringComparison.Ordinal) ||
                             routine.Name.EndsWith(value: ".destruct",
                                 comparisonType: StringComparison.Ordinal);
        // Where entities are shared handles, when one goes is decided by its last holder or by the cycle collector,
        // so a `destruct` of the program's own would run at no point the program can name.
        if (routine.Name == "destruct" && IsProgramFile(filePath: routine.Location.FileName) &&
            _registry.EntitiesAreSharedIn(filePath: routine.Location.FileName))
        {
            ReportError(code: SemanticDiagnosticCode.SharedEntityDestruct,
                message: $"You write `{routine.Name}` here, but a value here goes when its last holder lets go of it, or " +
                         "when the cycle collector frees it, at no point the program can name. For a resource that " +
                         "must be released at a known point, give the type `enter` and `exit` and use it with `using`.",
                location: routine.Location);
        }

        if (isDestroyDecl && routine.IsFailable)
        {
            ReportError(code: SemanticDiagnosticCode.DestructNotFailable,
                message: "You write `destruct` here with `failable`, but the builder calls it at the scope's end or at " +
                         "`drop x`, where nothing could take a failure from it. Write it as a plain routine " +
                         $"(`routine {routine.Name}()`), and recover a failing call inside it with `try` or `check` " +
                         "and `when`.",
                location: routine.Location);
        }

        if (isDestroyDecl && routine.IsDangerous)
        {
            ReportError(code: SemanticDiagnosticCode.DestroyNotDangerous,
                message: "You write `destruct` here with `dangerous`. The builder is the one that calls it (at the " +
                         "scope's end, or at `drop x`), so it is a plain routine: write `routine " +
                         $"{routine.Name}()` (`migrate` rewrites it).",
                location: routine.Location);
        }
    }

    private void ValidateVariableInitializer(VariableDeclaration varDecl, TypeSymbol varType)
    {
        // If we have both type annotation and initializer, verify compatibility
        if (varDecl is { Type: not null, Initializer: not null })
        {
            TypeSymbol initType =
                AnalyzeExpression(expression: varDecl.Initializer, expectedType: varType);
            if (!IsAssignableTo(source: initType, target: varType))
            {
                if (varDecl.ReplacementOf is { } replaced)
                {
                    string declared = varDecl.ReplacedRoutineAt is { Line: > 0 } at ? $" (line {at.Line})" : "";
                    ReportError(code: SemanticDiagnosticCode.VariableInitializerTypeMismatch,
                        message:
                        $"You `implant` a `{initType.ShortTypeName}` as the replacement for `{replaced}`, but " +
                        $"`{replaced}`{declared} needs a `{varType.ShortTypeName}`: the replacement takes the object " +
                        $"first, then the arguments of `{replaced}`, and gives back what `{replaced}` gives back. Write " +
                        "the replacement with that signature.",
                        location: varDecl.Initializer.Location);
                    return;
                }

                ReportError(code: SemanticDiagnosticCode.VariableInitializerTypeMismatch,
                    message:
                    $"Cannot assign value of type '{initType.Name}' to variable of type '{varType.Name}'.",
                    location: varDecl.Location);
            }
        }
    }
}
