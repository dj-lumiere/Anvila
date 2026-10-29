using Builder.Declaration;
using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Phase 3 &amp; 4: Declaration collection and type body resolution.
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Phase 3: Declaration Collection

    /// <summary>Per file: the top-level <c>var</c> names a script-mode file's implicit <c>start()</c>
    /// absorbed. Lets an unknown identifier inside a routine be explained as a script variable.</summary>
    private readonly Dictionary<string, IReadOnlySet<string>> _scriptVariablesByFile =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Collects all type and routine declarations without resolving bodies.
    /// Creates placeholder entries in the type registry for forward references.
    /// </summary>
    /// <param name="program">The program to collect declarations from.</param>
    private void CollectDeclarations(Program program)
    {
        if (program.Declarations
                   .OfType<RoutineDeclaration>()
                   .FirstOrDefault(predicate: r => r.IsScriptEntry) is
            { ScriptVariableNames.Count: > 0 } script)
        {
            _scriptVariablesByFile[key: _currentFilePath ?? program.Location.FileName] =
                script.ScriptVariableNames;
        }

        // #106: Validate that imports appear before other declarations
        bool seenNonImport = false;
        foreach (ISyntaxTreeNode declaration in program.Declarations)
        {
            if (declaration is ImportDeclaration import)
            {
                if (seenNonImport)
                {
                    ReportError(code: SemanticDiagnosticCode.ImportPositionViolation,
                        message: $"Import '{import.ModulePath}' is misplaced. " +
                                 "Move all 'import' statements to the top of the file, " +
                                 "immediately after the 'module' declaration and before any other declaration. " +
                                 "RazorForge enforces top-of-file imports for uniform structure across modules.",
                        location: import.Location);
                }
            }
            else if (declaration is not ModuleDeclaration)
            {
                seenNonImport = true;
            }
        }

        foreach (ISyntaxTreeNode declaration in program.Declarations)
        {
            CollectDeclaration(node: declaration);
        }

        CollectPendingGlobals();
        ReportPresetTypeNameCollisions(program: program);
    }

    /// <summary>
    /// Reports a build error when a file declares both a <c>preset</c> and a type of the same name.
    /// Cross-file clashes are fine — presets are file-scoped (public ones inline by value; secret ones
    /// are file-private) — but within one file the identifier is genuinely ambiguous: a call like
    /// <c>Foo(...)</c> could mean the constructor or the constant. Scans the file's declarations directly
    /// so it catches the clash regardless of declaration order.
    /// </summary>
    private void ReportPresetTypeNameCollisions(Program program)
    {
        var typeNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            string? typeName = node switch
            {
                RecordDeclaration r => r.Name,
                EntityDeclaration e => e.Name,
                ChoiceDeclaration c => c.Name,
                FlagsDeclaration f => f.Name,
                CrashableDeclaration cr => cr.Name,
                VariantDeclaration v => v.Name,
                _ => null
            };
            if (typeName != null)
            {
                typeNames.Add(item: typeName);
            }
        }

        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is PresetDeclaration preset && typeNames.Contains(item: preset.Name))
            {
                ReportError(code: SemanticDiagnosticCode.PresetTypeNameCollision,
                    message:
                    $"Preset '{preset.Name}' collides with a type of the same name declared in this file. " +
                    "A bare identifier would be ambiguous between the constant and the type — rename one " +
                    "(a secret preset is only file-private, so it still clashes within its own file).",
                    location: preset.Location);
            }
        }
    }

    /// <summary>
    /// Collects a single declaration.
    /// </summary>
    /// <param name="node">The declaration node to collect.</param>
    internal void CollectDeclaration(ISyntaxTreeNode node)
    {
        switch (node)
        {
            case RecordDeclaration record:
                CollectRecordDeclaration(record: record);
                break;

            case EntityDeclaration entity:
                CollectEntityDeclaration(entity: entity);
                break;

            case ChoiceDeclaration choice:
                CollectChoiceDeclaration(choice: choice);
                break;

            case FlagsDeclaration flags:
                CollectFlagsDeclaration(flags: flags);
                break;

            case CrashableDeclaration crashable:
                CollectCrashableDeclaration(crashable: crashable);
                break;

            case VariantDeclaration variant:
                CollectVariantDeclaration(variant: variant);
                break;

            case DefineDeclaration { OldType: { } oldType } define:
            {
                // The new name stands for the type as written, resolved where it was written, on first use.
                string? module = GetCurrentModuleName();
                string? file = _currentFilePath;
                _registry.RegisterTypeAlias(name: define.NewName, resolve: () =>
                {
                    string? previousModule = _currentModuleName;
                    string previousFile = _currentFilePath;
                    _currentModuleName = module;
                    _currentFilePath = file ?? previousFile;
                    try
                    {
                        return ResolveType(typeExpr: oldType);
                    }
                    finally
                    {
                        _currentModuleName = previousModule;
                        _currentFilePath = previousFile;
                    }
                });
                break;
            }

            case ProtocolDeclaration protocol:
                CollectProtocolDeclaration(protocol: protocol);
                break;

            case RoutineDeclaration func:
                CollectRoutineDeclaration(routine: func);
                break;

            case ExternalDeclaration externalDecl:
                CollectExternalDeclaration(external: externalDecl);
                break;

            case ExternalBlockDeclaration block:
                foreach (SyntaxTree.Declaration decl in block.Declarations)
                {
                    CollectDeclaration(node: decl);
                }

                break;

            case VariableDeclaration { IsGlobal: true } global:
                // Its type may name a type declared after it in the file: collected once the file's types are.
                _pendingGlobals.Add(item: (global, GetCurrentModuleName(), _currentFilePath));
                break;

            case VariableDeclaration variable:
                CollectMemberVariableDeclaration(memberVariable: variable);
                break;

            case ModuleDeclaration ns:
                _currentModuleName = ns.Path;
                ValidateModuleDeclaration(ns: ns);
                break;

            case ImportDeclaration import:
                ProcessImportDeclaration(import: import);
                break;

            case PresetDeclaration preset:
                CollectPresetDeclaration(preset: preset);
                break;
        }
    }

    /// <summary>
    /// Validates a module declaration.
    /// Rejects "module System" and the modules under it, which are the standard library's (user code cannot declare
    /// them).
    /// </summary>
    private void ValidateModuleDeclaration(ModuleDeclaration ns)
    {
        // Module "System" is reserved for stdlib only
        if (StandardModules.IsSystem(module: ns.Path) && !StandardModules.BuildingLibraryExport &&
            !IsStdlibFile(filePath: _currentFilePath))
        {
            ReportError(code: SemanticDiagnosticCode.ReservedModuleCore,
                message:
                $"Module '{ns.Path}' is the standard library's: System and the modules under it are reserved for it, " +
                "so give your module another name.",
                location: ns.Location);
        }
    }

    /// <summary>
    /// Processes an import declaration.
    /// Triggers on-demand module loading for the imported module.
    /// </summary>
    private void ProcessImportDeclaration(ImportDeclaration import)
    {
        // An import brings the module it names, never the modules under it: `import A` doesn't bring `A/B`.
        // Load the module on-demand.
        // This handles both System modules and non-System modules (Collections, ErrorHandling, etc.)
        bool success = _registry.LoadModule(importPath: import.ModulePath,
            currentFile: _currentFilePath,
            location: import.Location,
            effectiveModule: out string? effectiveModule);

        if (!success)
        {
            ReportError(code: SemanticDiagnosticCode.ModuleNotFound,
                message: StandardModules.GoneModuleMessage(module: import.ModulePath) ??
                         $"Cannot resolve import '{import.ModulePath}'. Module not found.",
                location: import.Location);
            return;
        }

        // #105: Check for import name collisions with specific imports
        if (import.SpecificImports != null)
        {
            foreach (string symbolName in import.SpecificImports.Where(predicate: s =>
                         !_importedSymbolNames.Add(item: s)))
            {
                ReportError(code: SemanticDiagnosticCode.ImportNameCollision,
                    message: $"Symbol '{symbolName}' is already imported from another module.",
                    location: import.Location);
            }
        }

        // Track the imported module for per-file type resolution
        if (effectiveModule != null)
        {
            _importedModules.Add(item: effectiveModule);
            _registry.ImportScopes.Record(file: _currentFilePath, import: import, module: effectiveModule);
        }

        // Realm-qualified foreign imports (`import Module.C::qsort`): record each so a BARE call to the
        // routine (`qsort(...)`) is permitted by the realm gate (CheckCallRealm). The module is already
        // loaded above, so the bare name resolves through the normal imported-module lookup.
        if (import.RealmImports != null)
        {
            foreach ((string realm, string routineName) in import.RealmImports)
            {
                _importedForeignAliases.Add(item: $"{realm}::{routineName}");
            }
        }
    }

    private void CollectMemberVariableDeclaration(VariableDeclaration memberVariable)
    {
        // MemberVariables are VariableDeclarations within type members
        // Visibility is validated using the simplified four-level system:
        // - public: read/write from anywhere
        // - published: public read, private write
        // - internal: read/write within module
        // - private: read/write within file

        // Check for duplicate member variable names within the same type
        if (_currentTypeMemberVariableNames != null &&
            !_currentTypeMemberVariableNames.Add(item: memberVariable.Name))
        {
            ReportError(code: SemanticDiagnosticCode.DuplicateMemberVariableDefinition,
                message:
                $"Member variable '{memberVariable.Name}' is already defined in this type.",
                location: memberVariable.Location);
        }

        if (memberVariable.Type == null)
        {
            return; // Type inference will be handled later
        }

        TypeSymbol memberVariableType = ResolveType(typeExpr: memberVariable.Type);
        ReportProtocolValueType(type: memberVariableType, location: memberVariable.Location,
            what: $"member variable '{memberVariable.Name}'");

        // Validate that tokens cannot be stored in member variables
        ValidateNotTokenMemberVariableType(type: memberVariableType,
            memberVariableName: memberVariable.Name,
            location: memberVariable.Location);

        // Variants ARE valid member-variable types — they're first-class values.
        // Copyability is gated separately by the Assignable derivation rule (a variant
        // is Assignable iff every member is).

        // Validate that Result<T> and Lookup<T> are not used as member variable types
        if (IsCarrierType(type: memberVariableType) && !IsMaybeType(type: memberVariableType))
        {
            string carrierName = GetCarrierBaseName(type: memberVariableType)!;
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeAsMemberVariable,
                message: $"'{carrierName}[T]' cannot be used as a member variable type. " +
                         "Error handling types are internal for error propagation and should not be stored.",
                location: memberVariable.Location);
        }

        // Member variable registration into the type's member variable list happens during type body resolution (Phase 4).
    }

    /// <summary>Globals seen while collecting declarations, with the module and file they are in.</summary>
    private readonly List<(VariableDeclaration Global, string? Module, string File)> _pendingGlobals = [];

    /// <summary>Collects the globals <see cref="CollectDeclaration"/> set aside, now that every type is known.</summary>
    private void CollectPendingGlobals()
    {
        string? previousModule = _currentModuleName;
        string previousFile = _currentFilePath;
        foreach ((VariableDeclaration global, string? module, string file) in _pendingGlobals)
        {
            _currentModuleName = module;
            _currentFilePath = file;
            CollectGlobalDeclaration(global: global);
        }

        _pendingGlobals.Clear();
        _currentModuleName = previousModule;
        _currentFilePath = previousFile;
    }

    /// <summary>
    /// Collects a module-level <c>global</c> (`global counter: S64 = 0`). Registers it both into
    /// the current scope (intra-file resolution) and into the registry's module-global table (cross-file
    /// resolution + the codegen storage signal). Unlike a preset the global is MUTABLE and not inlined.
    /// </summary>
    private void CollectGlobalDeclaration(VariableDeclaration global)
    {
        // The parser requires a type annotation on every `global`, so global.Type is non-null here.
        TypeSymbol globalType = InFileRealm(resolve: () => ResolveType(typeExpr: global.Type!));
        if (_registry.Rules.ChecksAccessTokens && IsInlineOnlyTokenType(type: globalType))
        {
            string target = globalType.TypeArguments is [{ } viewed] ? viewed.ShortTypeName : "the entity";
            ReportError(code: SemanticDiagnosticCode.TokenMemberVariableNotAllowed,
                message:
                $"You declare global '{global.Name}' as `{globalType.ShortTypeName}`, but a token lives in a routine's " +
                $"variables and is never kept in a global: the global would still point at the `{target}` after the " +
                $"place it is in is gone. Keep the `{target}` itself in the global (`global {global.Name}: {target}`), " +
                $"or under a manager (`Guarded[{target}, MultiRead]`), and take the token where you use it.",
                location: global.Location);
        }

        ValidateNoTokenTypeArgument(type: globalType, what: $"global '{global.Name}'", location: global.Location);

        // Every job reaches a global, so a single-thread handle in one (`Retained`, `Tracked`) would have its count
        // changed by several threads at once.
        if (_registry.Rules.ChecksOwnership && !IsInlineOnlyTokenType(type: globalType) &&
            TokenInTypeArguments(type: globalType) == null &&
            Wrappers.FindOneThreadPart(type: globalType) is { Wrapper: var wrapper, Path: var at })
        {
            string holds = at == "<value>"
                ? $"which is a `{wrapper}`"
                : $"which holds {Wrappers.DescribeOneThreadPart(wrapper: wrapper, at: at)}";
            ReportError(code: SemanticDiagnosticCode.ThreadArgNotShareable,
                message:
                $"You declare global '{global.Name}' as `{globalType.ShortTypeName}`, {holds}. " +
                $"Every running job reaches a global, and a `{wrapper}` may be used by one thread only. Use " +
                "`Guarded[...]` (or `Witnessed` for a weak handle) to share an entity between jobs, or keep the " +
                $"`{wrapper}` in a variable of the routine that uses it.",
                location: global.Location);
        }

        // A RazorForge global is reached from every thread, so its binding is never assigned again (only its
        // initialization, at the top of start()): an Unbroken or a Guarded changes what it holds through its own
        // routines, any other value is only read.
        // Two modules may each have a global of one name: each is registered under its module, and a lookup from
        // either module finds its own.
        string? module = GetCurrentModuleName();
        VariableInfo registered = _registry.RegisterGlobal(name: global.Name,
            type: globalType,
            module: module,
            isSecret: global.Visibility == VisibilityModifier.Secret,
            isReadOnly: !_registry.EntitiesAreShared);
        _registry.DeclareGlobalInScope(global: registered);
    }

    /// <summary>The module each collected preset was declared in, so its value is analyzed against its own
    /// module's routines and types even when several files are analyzed in one pass.</summary>
    private readonly Dictionary<PresetDeclaration, string?> _presetModules =
        new(comparer: ReferenceEqualityComparer.Instance);

    private void CollectPresetDeclaration(PresetDeclaration preset)
    {
        _presetModules[key: preset] = _currentModuleName ?? GetCurrentModuleName();
        TypeSymbol presetType = InFileRealm(resolve: () => ResolveType(typeExpr: preset.Type));

        // A collection-literal preset is only Presettable as a fixed-size `Array[T, N]` or
        // `BitArray[N]` — those lower to a single constant global. Heap collections (List/Set/Dict/
        // RotatableList/...) would rebuild the whole collection on every use (the fun_bench OOM class), so
        // reject them. Non-collection presets (scalars, constructor calls like `C128(...)`) are fine.
        if (preset.Value is ListLiteralExpression && presetType is not ErrorTypeSymbol)
        {
            string baseName = presetType.BareName;
            if (baseName is not ("Items" or "Bits"))
            {
                ReportError(code: SemanticDiagnosticCode.NonPresettableCollectionPreset,
                    message:
                    $"Preset '{preset.Name}' has type '{presetType.Name}': a collection-literal preset must be " +
                    "a fixed-size 'Items[T, COUNT]' or 'Bits[BITS]'. Other collection types would rebuild the whole " +
                    "collection on every use.",
                    location: preset.Location);
            }
        }

        SeedPresetValueMetadata(value: preset.Value, presetType: presetType);
        Expression presetValue = preset.Value;
        if (preset.Value is ListLiteralExpression list && presetType is not ErrorTypeSymbol)
        {
            ConformPresetListElements(preset: preset, list: list, presetType: presetType);

            // A BitArray table is kept in its stored form, the packed bytes, so every backend lays it down as is.
            if (presetType.BareName == "Bits" && LookupTypeWithImports(name: "U8") is { } byteType)
            {
                presetValue = BitArrayPacking.Pack(bits: list.Elements,
                                  bitArrayType: presetType,
                                  byteType: byteType,
                                  location: list.Location) ??
                              presetValue;
            }
        }

        // The module goes with the name, so a file whose import narrowed this module out does not see it.
        string? module = GetCurrentModuleName();
        _registry.DeclareVariable(name: preset.Name,
            type: presetType,
            isPreset: true,
            presetValue: presetValue,
            module: module);

        // Also register as a module-level preset for cross-file access
        if (module != null)
        {
            _registry.RegisterPreset(name: preset.Name,
                type: presetType,
                module: module,
                value: presetValue,
                isSecret: preset.IsSecret,
                location: preset.Location);
        }
    }

    /// <summary>
    /// Types each element of an <c>Array[T, N]</c> / <c>BitArray[N]</c> preset literal against the element
    /// type. The preset becomes a single constant global that never passes through routine-body analysis or
    /// lowering, so this is where a bare literal gets its type (and its range check), and where its token is
    /// made concrete: an unsuffixed `1.0` in an `Array[B32, N]` must reach the emitter as a B32 constant, not
    /// as an undecided literal.
    /// </summary>
    private void ConformPresetListElements(PresetDeclaration preset, ListLiteralExpression list,
        TypeSymbol presetType)
    {
        TypeSymbol? elementType = presetType.BareName switch
        {
            "Items" when presetType.TypeArguments is { Count: > 0 } args => args[0],
            "Bits" => LookupTypeWithImports(name: "Bool"),
            _ => null
        };
        if (elementType == null)
        {
            return;
        }

        for (int i = 0; i < list.Elements.Count; i++)
        {
            if (list.Elements[index: i] is not LiteralExpression literal)
            {
                ReportError(code: SemanticDiagnosticCode.PresetElementNotConstant,
                    message:
                    $"Element {i} of preset '{preset.Name}' is not a literal. {Article.Capital(name: presetType.Name)} '{presetType.Name}' preset " +
                    "is stored as one constant, so write each element as a literal.",
                    location: list.Elements[index: i].Location);
                continue;
            }

            TypeSymbol literalType = AnalyzeLiteralExpression(literal: literal, expectedType: elementType);
            if (literalType is ErrorTypeSymbol)
            {
                continue;
            }

            if (literalType.Name != elementType.Name)
            {
                ReportError(code: SemanticDiagnosticCode.PresetElementNotConstant,
                    message:
                    $"Element {i} of preset '{preset.Name}' is {Article.For(name: literalType.Name)} '{literalType.Name}' literal, but " +
                    $"'{presetType.Name}' holds '{elementType.Name}'. Write it as {Article.For(name: elementType.Name)} '{elementType.Name}' literal.",
                    location: literal.Location);
                continue;
            }

            literal.ResolvedType = literalType;
            list.Elements[index: i] = UndecidedLiteralConformance.Conform(literal: literal);
        }
    }

    private static void SeedPresetValueMetadata(Expression value, TypeSymbol presetType)
    {
        value.ResolvedType ??= presetType;

        if (value is not CallExpression call ||
            call.Callee is not IdentifierExpression identifier || call.LoweringKind != default)
        {
            return;
        }

        bool matchesPresetType = identifier.Name == presetType.Name ||
                                 identifier.Name == presetType.FullName ||
                                 presetType.FullName.EndsWith(value: "." + identifier.Name,
                                     comparisonType: StringComparison.Ordinal);
        if (!matchesPresetType)
        {
            return;
        }

        call.ResolvedType ??= presetType;
        call.ConstructedType ??= presetType;

        call.LoweringKind = presetType switch
        {
            RecordTypeSymbol { BackendType: not null } => CallLoweringKind.TypeConstructor,
            _ => call.LoweringKind
        };
    }

    /// <summary>
    /// Rejects records that contain themselves by value (directly or transitively) — such a
    /// record would need infinite storage, and the recursive size computation
    /// (<c>RecordTypeSymbol.SizeBytes</c>) would otherwise stack-overflow the builder. Entities,
    /// wrappers, and <c>@llvm</c>-backed records are pointer-sized, so they break the cycle.
    /// </summary>
    /// <returns><c>true</c> if any self-containing value record was found (and reported).</returns>
    internal bool ValidateNoRecursiveValueRecords()
    {
        bool found = false;
        foreach (TypeSymbol t in _registry.GetAllTypes()
                                        .ToList())
        {
            if (t is not RecordTypeSymbol { BackendType: null, IsGenericDefinition: false } rec ||
                rec is TupleTypeSymbol)
            {
                continue;
            }

            var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
            foreach (MemberVariableInfo mv in rec.MemberVariables)
            {
                if (!ValueAggregateReaches(target: rec, current: mv.Type, seen: seen))
                {
                    continue;
                }

                ReportError(code: SemanticDiagnosticCode.RecursiveValueRecord,
                    message:
                    $"Record '{rec.Name}' contains itself by value (directly or transitively), " +
                    "which would require infinite storage. Store it behind an entity or a " +
                    "pointer-like wrapper (e.g. Retained[...]) instead.",
                    location: rec.Location ?? new SourceLocation(FileName: "",
                        Line: 0,
                        Column: 0,
                        Position: 0));
                found = true;
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// True when <paramref name="target"/> is reachable from <paramref name="current"/> by walking
    /// only inline value-aggregate fields (records/tuples). Pointer-shaped types (entities,
    /// <c>@llvm</c>-backed records, wrappers) stop the walk — they don't contribute inline storage.
    /// </summary>
    private static bool ValueAggregateReaches(RecordTypeSymbol target, TypeSymbol current,
        HashSet<string> seen)
    {
        // Only inline value aggregates (plain records / tuples) propagate the cycle.
        if (current is not RecordTypeSymbol { BackendType: null } rec)
        {
            return false;
        }

        if (string.Equals(a: rec.FullName,
                b: target.FullName,
                comparisonType: StringComparison.Ordinal))
        {
            return true;
        }

        if (!seen.Add(item: rec.FullName))
        {
            return false; // already explored from here, no cycle back to target through it
        }

        return rec.MemberVariables.Any(predicate: mv =>
            ValueAggregateReaches(target: target, current: mv.Type, seen: seen));
    }

    private void CollectRecordDeclaration(RecordDeclaration record)
    {
        var typeInfo = new RecordTypeSymbol(name: record.Name)
        {
            GenericParameters = record.GenericParameters,
            GenericConstraints = record.GenericConstraints,
            Visibility = record.Visibility,
            Location = record.Location,
            Module = GetCurrentModuleName(),
            Annotations = record.Annotations,
            BackendType = ExtractLlvmAnnotation(annotations: record.Annotations),
            IsInPlaceEntity = record.IsInPlaceEntity
        };

        // @llvm("typename") IS the layout — fields would be silently discarded by codegen.
        // Permit `nothing` bodies and bodies with only non-field declarations (e.g. comments / nested
        // routines via inline blocks). Reject any VariableDeclaration in Members.
        if (typeInfo.BackendType != null && record.Members
                                                  .OfType<VariableDeclaration>()
                                                  .Any())
        {
            ReportError(code: SemanticDiagnosticCode.LlvmAnnotatedRecordMustHaveNothingBody,
                message:
                $"Record '{record.Name}' is annotated @llvm(\"{typeInfo.BackendType}\") but " +
                "declares member variables. The annotation fully dictates the LLVM representation; " +
                "fields would be silently discarded. Use a `nothing` body.",
                location: record.Location);
        }

        ApplyLayoutAnnotations(typeSymbol: typeInfo, record: record);

        TryRegisterType(type: typeInfo, location: record.Location);
    }

    /// <summary>
    /// Applies C-ABI layout control from <c>@layout("...")</c> annotations to <paramref name="typeSymbol"/>.
    /// Recognized arguments: <c>C</c> (natural layout, a no-op documentation marker), <c>packed</c>
    /// (<see cref="RecordTypeSymbol.IsPacked"/>), and <c>align=N</c> (<see cref="RecordTypeSymbol.ForcedAlignment"/>,
    /// N a positive power of two). Multiple annotations compose (e.g. <c>packed</c> + <c>align=16</c>).
    /// Reports <see cref="SemanticDiagnosticCode.InvalidLayoutAnnotation"/> for anything else.
    /// </summary>
    private void ApplyLayoutAnnotations(RecordTypeSymbol typeSymbol, RecordDeclaration record)
    {
        if (record.Annotations is not { } annotations)
        {
            return;
        }

        foreach (string ann in annotations)
        {
            if (!ann.StartsWith(value: "layout(") || !ann.EndsWith(value: ')'))
            {
                continue;
            }

            string arg = ann[7..^1]
               .Trim(trimChar: '"');
            switch (arg)
            {
                case "C":
                    // Explicit natural C layout — already the default; the marker only documents FFI
                    // intent and locks against future field reordering.
                    break;
                case "packed":
                    typeSymbol.IsPacked = true;
                    break;
                default:
                    if (arg.StartsWith(value: "align="))
                    {
                        ApplyAlignLayout(typeSymbol: typeSymbol,
                            spec: arg["align=".Length..],
                            record: record);
                    }
                    else
                    {
                        ReportError(code: SemanticDiagnosticCode.InvalidLayoutAnnotation,
                            message:
                            $"Record '{record.Name}' has @layout(\"{arg}\") — unknown layout. Use " +
                            "\"C\", \"packed\", or \"align=N\".",
                            location: record.Location);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Reports <see cref="SemanticDiagnosticCode.LayoutAnnotationNotOnRecord"/> if a <c>@layout("...")</c>
    /// annotation appears in a non-record context (variable declaration, use-site type, …). Memory layout
    /// is a per-type property fixed at the record's declaration and nowhere else.
    /// </summary>
    private void RejectLayoutAnnotation(List<string>? annotations, SourceLocation location,
        string where)
    {
        if (annotations is null)
        {
            return;
        }

        foreach (string ann in annotations.Where(predicate: a =>
                     a.StartsWith(value: "layout(") && a.EndsWith(value: ')')))
        {
            ReportError(code: SemanticDiagnosticCode.LayoutAnnotationNotOnRecord,
                message:
                $"@layout(...) is not allowed on {where}. Memory layout is a property of a record " +
                "type, set only on the record declaration.",
                location: location);
        }
    }

    /// <summary>Parses and validates the N in an <c>align=N</c> layout spec: a power of two in [2, 4096].
    /// The full power-of-two range is needed — a C-union byte blob (natural alignment 1) forces its
    /// members' alignment (2/4/8/16), while 16/32/64 cover SSE/AVX/cache-line and page alignment goes up to
    /// 4096. Non-powers-of-two, 1, and huge values are rejected.</summary>
    private void ApplyAlignLayout(RecordTypeSymbol typeSymbol, string spec, RecordDeclaration record)
    {
        if (int.TryParse(s: spec, result: out int n) && n >= 2 && n <= 4096 && (n & n - 1) == 0)
        {
            typeSymbol.ForcedAlignment = n;
            return;
        }

        ReportError(code: SemanticDiagnosticCode.InvalidLayoutAnnotation,
            message:
            $"Record '{record.Name}' has @layout(\"align={spec}\") — the alignment must be a power of two " +
            "in [2, 4096] (e.g. 8, 16, 64).",
            location: record.Location);
    }

    /// <summary>
    /// Extracts the LLVM type from an @llvm("type") annotation.
    /// Returns null if no @llvm annotation is present.
    /// </summary>
    private static string? ExtractLlvmAnnotation(List<string>? annotations)
    {
        if (annotations == null)
        {
            return null;
        }

        string? match = annotations.FirstOrDefault(predicate: ann =>
            ann.StartsWith(value: "llvm(") && ann.EndsWith(value: ')'));
        return match?[5..^1]
           .Trim(trimChar: '"');
    }

    private void CollectEntityDeclaration(EntityDeclaration entity)
    {
        // `@self_locking` lets every call on the entity's handle skip the access lock, which is sound only for an
        // entity whose member variables never change after it is made and do their own locking.
        if (entity.LeadingAnnotations?.Contains(item: RuntimeContract.RoamedMemberRoutine.SelfLockingAnnotation) ==
            true && !IsStdlibFile(filePath: _currentFilePath))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAnnotation,
                message: $"'@self_locking' on '{entity.Name}' belongs to the standard library's own entities, " +
                         "which lock inside. Remove it: calls on your entity keep their access lock.",
                location: entity.Location);
        }

        var typeInfo = new EntityTypeSymbol(name: entity.Name)
        {
            GenericParameters = entity.GenericParameters,
            GenericConstraints = entity.GenericConstraints,
            Visibility = entity.Visibility,
            Location = entity.Location,
            Module = GetCurrentModuleName(),
            Annotations = entity.LeadingAnnotations
        };

        TryRegisterType(type: typeInfo, location: entity.Location);
    }

    private void CollectChoiceDeclaration(ChoiceDeclaration choice)
    {
        var typeInfo = new ChoiceTypeSymbol(name: choice.Name)
        {
            Visibility = choice.Visibility,
            Location = choice.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: choice.Location);
    }

    private void CollectFlagsDeclaration(FlagsDeclaration flags)
    {
        var typeInfo = new FlagsTypeSymbol(name: flags.Name)
        {
            Visibility = flags.Visibility,
            Location = flags.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: flags.Location);
    }

    private void CollectCrashableDeclaration(CrashableDeclaration crashable)
    {
        var typeInfo = new CrashableTypeSymbol(name: crashable.Name)
        {
            Visibility = crashable.Visibility,
            Location = crashable.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: crashable.Location);

        // Collect member declarations (fields + crash_message body) as children
        foreach (SyntaxTree.Declaration member in crashable.Members)
        {
            CollectDeclaration(node: member);
        }
    }

    private void CollectVariantDeclaration(VariantDeclaration variant)
    {
        var typeInfo = new VariantTypeSymbol(name: variant.Name)
        {
            GenericParameters = variant.GenericParameters,
            GenericConstraints = variant.GenericConstraints,
            Location = variant.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: variant.Location);
    }

    private void CollectProtocolDeclaration(ProtocolDeclaration protocol)
    {
        var typeInfo = new ProtocolTypeSymbol(name: protocol.Name)
        {
            GenericParameters = protocol.GenericParameters,
            GenericConstraints = protocol.GenericConstraints,
            Visibility = protocol.Visibility,
            Location = protocol.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: protocol.Location);
    }

    /// <summary>
    /// Suflae: a bare <c>RF::</c> RazorForge entity may NOT be a routine parameter or (non-constructor)
    /// return. An RF entity has no reference count, so passing it by value across an SF routine boundary
    /// lets SF's scope-exit teardown destroy the same object multiple times (caller + callee + return)
    /// → heap corruption. It is fine as a LOCAL or as a FIELD of an SF entity (the container owns it);
    /// only the by-value boundary crossing is unsafe. Reports RF-S439 with the safe alternatives. The
    /// build-time invariant <c>AssertNoBareEntityInSignature</c> still guards the OTHER bare-entity case
    /// (an SF entity that slipped roaming — a builder bug, no <c>RF::</c> tag).
    /// </summary>
    internal void CheckSignatureHasNoForeignBareEntity(RoutineDeclaration routine,
        RoutineKind kind, IReadOnlyList<ParamInfo> parameters, TypeSymbol? returnType)
    {
        string? file = routine.Location.FileName;
        if (!_registry.EntitiesAreShared || file == null ||
            Builder.Frontends.Languages.OfFile(fileName: file) != _registry.Language ||
            IsStdlibFile(filePath: file))
        {
            return;
        }

        const string advice =
            " A RazorForge entity written with `RF::` is not shared and has no reference count, so handing it " +
            "across a routine here would release the same object more than once. Keep it as a member variable of " +
            "an entity of this program and pass that entity, or do the work on it inside a `.rf` module.";

        // The resolved type decides: an entity of an imported `.rf` module resolves to a shared handle here, and
        // only one written with `RF::` stays bare.
        foreach (ParamInfo p in parameters)
        {
            if (p.Type is EntityTypeSymbol pe && IsRazorForgeEntity(type: pe))
            {
                Parameter? declared = routine.Parameters.FirstOrDefault(predicate: d => d.Name == p.Name);
                ReportError(code: SemanticDiagnosticCode.SuflaeBareRfEntityInSignature,
                    message:
                    $"You declare parameter '{p.Name}' as '{pe.Name}', a RazorForge entity." +
                    advice,
                    location: declared?.Type?.Location ?? routine.Location);
            }
        }

        // A constructor's return is the freshly-built entity the CALLER receives, not a
        // by-value hand-off of an already-live object, so it is exempt (mirrors the invariant's carve-out).
        if (kind != RoutineKind.Creator && returnType is EntityTypeSymbol re && IsRazorForgeEntity(type: re))
        {
            ReportError(code: SemanticDiagnosticCode.SuflaeBareRfEntityInSignature,
                message: $"You declare the return type as '{re.Name}', a RazorForge entity." +
                         advice,
                location: routine.ReturnType?.Location ?? routine.Location);
        }
    }

    private void CollectRoutineDeclaration(RoutineDeclaration routine)
    {
        // Determine the kind of routine (member/creator/free) plus its owner + canonical name.
        (RoutineKind kind, TypeSymbol? ownerType, string routineName) =
            DetermineRoutineKind(routine: routine);

        // Validate declaration-level constraints (operator/kind restrictions, wired names, annotations,
        // mutation-category conflicts, varargs placement) before deferring registration.
        ValidateRoutineDeclarationConstraints(routine: routine,
            kind: kind,
            ownerType: ownerType,
            routineName: routineName);

        // Store for deferred resolution and registration in Phase 4.1
        _pendingRoutines.Add(item: new PendingRoutine(Declaration: routine,
            OwnerType: ownerType,
            Kind: kind,
            RoutineName: routineName,
            Module: GetCurrentModuleName(),
            FilePath: _currentFilePath));
    }

    /// <summary>
    /// The owner of a member routine declared outside its type (<c>routine List[T].second()</c>), looked up in the
    /// realm of the file that declares it: in a Suflae file, <c>List</c> is Suflae's own list, as it is everywhere
    /// else in that file. Declarations are collected before the per-file analysis sets that realm.
    /// </summary>
    private TypeSymbol? LookupOwnerInFileRealm(string name)
    {
        return InFileRealm(resolve: () => LookupTypeWithImports(name: name));
    }

    /// <summary>Runs <paramref name="resolve"/> in the realm of the file being collected (Suflae's own types in a
    /// Suflae file), which declaration collection does not set yet.</summary>
    private T InFileRealm<T>(Func<T> resolve)
    {
        string previousRealm = _registry.ResolutionRealm;
        if (!string.IsNullOrEmpty(value: _currentFilePath) && !IsStdlibFile(filePath: _currentFilePath) &&
            Builder.Frontends.Languages.HasSourceExtension(fileName: _currentFilePath) &&
            Builder.Frontends.Languages.OfFile(fileName: _currentFilePath) != Language.RazorForge)
        {
            _registry.ResolutionRealm = Builder.Frontends.Languages.RealmOf(fileName: _currentFilePath);
        }

        try
        {
            return resolve();
        }
        finally
        {
            _registry.ResolutionRealm = previousRealm;
        }
    }

    /// <summary>
    /// Classifies a routine declaration as a member routine, creator, or free function, returning its
    /// <see cref="RoutineKind"/>, resolved owner type (when any), and canonical internal name.
    /// </summary>
    private (RoutineKind Kind, TypeSymbol? OwnerType, string RoutineName) DetermineRoutineKind(
        RoutineDeclaration routine)
    {
        TypeSymbol? ownerType = _currentType;
        string routineName = routine.Name;

        // `common` (type-level static) is folded into RoutineKind.CommonRoutine — the former orthogonal
        // StorageClass.Common axis. A creator is never common; a common member is CommonRoutine.
        bool isCommon = routine.IsCommon;

        if (_currentType != null)
        {
            RoutineKind inBodyKind = isCommon
                ? RoutineKind.CommonRoutine
                : RoutineKind.MemberRoutine;
            return (inBodyKind, ownerType, routineName);
        }

        if (routine.MemberRoutineName is { } declaredMember)
        {
            // Member routine syntax: "Type.routine" or "Type[T].routine". The parser already split
            // the owner base (args-stripped) and member routine into structured fields — read them instead of
            // re-parsing the concatenated Name (name-canonicalization).
            routineName = declaredMember;

            // OwnerName is the bare owner base (e.g. "Stack" for "Stack[T].push") — already the
            // generic-definition key, so no generic-param strip needed here.
            ownerType = LookupOwnerInFileRealm(name: routine.OwnerName!);

            // A constructor is only ever `routine Type(...)`; `routine Type.create(...)` is an ordinary
            // member routine named `create`.
            RoutineKind memberKind = isCommon
                ? RoutineKind.CommonRoutine
                : RoutineKind.MemberRoutine;
            return (memberKind, ownerType, routineName);
        }

        // Top-level routine. A routine whose bare name matches a known type is a
        // CONSTRUCTOR — the surface syntax `routine T(...)` / `routine T[params](...)`.
        // Route it to the reserved creator kind with the type as owner and NO member name
        // (RoutineInfo.CreatorName) — identity is RoutineKind.Creator, never a name string.
        // The trailing `!` (failable) is carried structurally on routine.IsFailable, not in the name.
        // Constructor-sugar detection lives here (rather than in the parser) because the parser has no
        // symbol table to resolve whether a bare name is a known type — that information is only available
        // after declaration collection.
        // A free routine's Name is the canonical bare identifier (the parser folds `[params]` into the
        // structured GenericParameters, never into Name for a non-member routine), so it is looked up
        // directly with no generic-suffix strip.
        TypeSymbol? ctorOwner = LookupTypeWithImports(name: routine.Name);
        if (ctorOwner is EntityTypeSymbol or RecordTypeSymbol or ChoiceTypeSymbol or FlagsTypeSymbol
            or VariantTypeSymbol or CrashableTypeSymbol)
        {
            return (RoutineKind.Creator, ctorOwner, RoutineInfo.CreatorName);
        }

        return (RoutineKind.FreeRoutine, ownerType, routineName);
    }

    /// <summary>
    /// Validates a routine declaration's surface constraints at collection time: operator-on-choice/flags
    /// restrictions, unknown wired names, misplaced annotations, conflicting mutation categories, and
    /// varargs placement. Pure reporting — no registration side effects.
    /// </summary>
    private void ValidateRoutineDeclarationConstraints(RoutineDeclaration routine,
        RoutineKind kind, TypeSymbol? ownerType, string routineName)
    {
        // `from_serial` is the builder's own: it writes one per type, and the library's hand-written ones are the
        // leaves. A program reads a value back with `sv.to[T]()`.
        if (routineName == Instantiation.AutoWiredRegistrationPass.FromSerialRoutineName &&
            IsProgramFile(filePath: routine.Location.FileName))
        {
            ReportError(code: SemanticDiagnosticCode.FromSerialIsBuilderInternal,
                message: $"'{routineName}' is the builder's own routine: it writes one for every type. A value is " +
                         "read back from its serialized form with 'sv.to[T]()'.",
                location: routine.Location);
        }

        // Validate that choice types cannot define any operator wired member routines
        if (ownerType is ChoiceTypeSymbol && kind == RoutineKind.MemberRoutine &&
            IsOperatorWired(name: routineName))
        {
            ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                message:
                $"Choice type '{ownerType.Name}' cannot define operator '{routineName}'. " +
                "Choice types do not support operators. Use 'is' for case matching and regular routines for additional behavior.",
                location: routine.Location);
        }

        // #135: Flags types cannot define any operator wired member routines
        if (ownerType is FlagsTypeSymbol && kind == RoutineKind.MemberRoutine &&
            IsOperatorWired(name: routineName))
        {
            ReportError(code: SemanticDiagnosticCode.FlagsCustomOperatorNotAllowed,
                message:
                $"Flags type '{ownerType.Name}' cannot define operator '{routineName}'. " +
                "A flags value is combined with 'and', tested with 'has' / 'has_any' / 'lacks' / '==', and takes members off with 'without': it defines no operators of its own.",
                location: routine.Location);
        }

        // Member segment from the parser-captured structured field, never a re-split of Name.
        string baseName = routine.MemberRoutineName ?? routineName;

        // Validate $ prefixed names are known built-in member routines
        if (IsUnknownWiredMemberRoutine(bareName: baseName, isWired: routine.IsWiredMemberRoutine))
        {
            ReportError(code: SemanticDiagnosticCode.UnknownWiredRoutine,
                message: $"Routine name '${baseName}' uses reserved '$' prefix. " +
                         "Names starting with '$' are reserved for built-in memberRoutines.",
                location: routine.Location);
        }

        // @generated is only valid on protocol routine declarations
        if (routine.Annotations.Contains(item: "generated"))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidGeneratedInnatePlacement,
                message: "'@generated' annotation is only valid on protocol routine declarations.",
                location: routine.Location);
        }

        // @infix: a member routine with one parameter besides me, written between its two operands (`a dot b`).
        if (routine.Annotations.Contains(item: "infix") &&
            (kind != RoutineKind.MemberRoutine || routine.Parameters.Count(predicate: p => p.Name != "me") != 1))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAnnotation,
                message: $"'@infix' puts a routine between two values ('a {routineName} b'), so it takes a receiver and " +
                         $"one parameter: 'routine T.{routineName}(you: T)'.",
                location: routine.Location);
        }

        // @crash_only is only valid on failable routines (#76)
        if (routine.Annotations.Contains(item: "crash_only") && !routine.IsFailable)
        {
            ReportError(code: SemanticDiagnosticCode.CrashOnlyOnNonFailable,
                message: "'@crash_only' goes only on a routine that can fail. Declare it 'failable' " +
                         "('failable routine name(...)'), or drop the annotation.",
                location: routine.Location);
        }

        // Index operators (getitem/setitem) are governed by PROTOCOL conformance, not type KIND:
        // any type that follows ViewingIndexable/ModifyingIndexable may define them (records like
        // Array[T,N]/BitArray[N], entities like List/Dict, and user containers alike). The
        // conformance requirement is enforced by RF-S411 (OperatorWithoutProtocol) via the
        // wired-routine→protocol catalog, so no type-kind restriction is applied here.

        // @writable is removed — emit error before the conflict check
        if (routine.Annotations.Contains(item: "writable"))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAnnotation,
                message: "@writable is no longer a valid annotation. " +
                         "Routines are writable by default; use @readonly to restrict, or @reshaping explicitly.",
                location: routine.Location);
        }

        // A language without the readonly discipline (Suflae) has neither mark: a record already keeps its value
        // to itself, and a change of a container's shape during a loop is caught as it happens.
        TypeModel.Enums.Language writtenIn = Builder.Frontends.Languages.OfFile(fileName: routine.Location.FileName);
        if (Builder.Frontends.Languages.IsRegistered(language: writtenIn) &&
            Builder.Frontends.Languages.For(language: writtenIn) is { ChecksReadonly: false } rules)
        {
            foreach (string mark in (string[])["readonly", "reshaping"])
            {
                if (routine.Annotations.Contains(item: mark))
                {
                    ReportError(code: SemanticDiagnosticCode.InvalidAnnotation,
                        message: $"'@{mark}' does not exist in {rules.Name}.",
                        location: routine.Location);
                }
            }
        }

        // #157: Conflicting mutation category annotations
        int mutationCount = 0;
        if (routine.Annotations.Contains(item: "readonly"))
        {
            mutationCount++;
        }

        if (routine.Annotations.Contains(item: "reshaping"))
        {
            mutationCount++;
        }

        if (mutationCount > 1)
        {
            ReportError(code: SemanticDiagnosticCode.MutationCategoryConflict,
                message: "Routine has conflicting mutation annotations. " +
                         "Only one of @readonly or @reshaping can be specified.",
                location: routine.Location);
        }

        ValidateVarargsPlacement(routine: routine);
    }

    /// <summary>
    /// #74: Validates varargs placement — at most one variadic parameter, positioned first (or second
    /// after an implicit <c>me</c>).
    /// </summary>
    private void ValidateVarargsPlacement(RoutineDeclaration routine)
    {
        var varargParams = routine.Parameters
                                  .Where(predicate: p => p.IsVariadic)
                                  .ToList();
        if (varargParams.Count > 1)
        {
            ReportError(code: SemanticDiagnosticCode.VarargsMultiple,
                message: "Only one varargs parameter is allowed per routine.",
                location: varargParams[index: 1].Location);
        }

        if (varargParams.Count >= 1)
        {
            int varargIndex = routine.Parameters.IndexOf(item: varargParams[index: 0]);
            bool isFirstNonMe = varargIndex == 0 ||
                                varargIndex == 1 && routine.Parameters[index: 0].Name == "me";
            if (!isFirstNonMe)
            {
                ReportError(code: SemanticDiagnosticCode.VarargsNotFirst,
                    message:
                    "Varargs parameter must be the first parameter (or second after 'me').",
                    location: varargParams[index: 0].Location);
            }
        }
    }

    #endregion

    #region Phase 5: Protocol Implementation Validation

    /// <summary>
    /// Validates that all types declaring "obeys Protocol" implement all required protocol member routines.
    /// This is called after all routines are registered (Phase 4.1) and derived operators are generated.
    /// </summary>
    private void ValidateProtocolImplementations()
    {
        foreach (TypeSymbol type in _registry.GetAllTypes())
        {
            ValidateTypeProtocolImplementation(type: type);
            ValidateCrashMessage(type: type);
        }

        SizeCrashablesRoom();
    }

    /// <summary>
    /// A <c>@throws([A, B])</c> on a failable routine fixes what it may throw (an exported routine says so, and what it
    /// is built from may change): every crashable the builder collected for it (its own throws and those of the
    /// failable routines it calls) must be in the list (RF-S712). On a routine that isn't failable, or naming
    /// something that isn't a crashable, it is RF-S713.
    /// </summary>
    private void ValidateThrowsLists()
    {
        foreach (RoutineInfo routine in _registry.GetAllRoutines())
        {
            if (routine.Location is not { FileName.Length: > 0 } at || routine.IsRecoveryVariant ||
                routine.GenericDefinition is { } instanceOf && !ReferenceEquals(objA: instanceOf, objB: routine) ||
                routine.Annotations.FirstOrDefault(predicate: a => a.StartsWith(value: "throws(", comparisonType: StringComparison.Ordinal)) is not { } annotation)
            {
                continue;
            }

            if (!routine.IsFailable)
            {
                ReportError(code: SemanticDiagnosticCode.ThrowsListMisplaced,
                    message: $"'{routine.Name}' isn't failable, so it throws nothing for '@throws' to list. Declare it " +
                             $"'failable routine {routine.Name}', or remove the annotation.",
                    location: at);
                continue;
            }

            string inner = annotation["throws(".Length..^1].Trim();
            if (!inner.StartsWith(value: '[') || !inner.EndsWith(value: ']'))
            {
                ReportError(code: SemanticDiagnosticCode.ThrowsListMisplaced,
                    message: "'@throws' lists the crashables the routine may throw: '@throws([NotFoundError, ParseError])'.",
                    location: at);
                continue;
            }

            var listed = new List<TypeSymbol>();
            bool valid = true;
            foreach (string name in inner[1..^1].Split(separator: ',', options: StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                if ((_registry.LookupType(name: name) ?? _registry.LookupType(name: $"{routine.Module}.{name}")) is
                    CrashableTypeSymbol crashable)
                {
                    listed.Add(item: crashable);
                    continue;
                }

                valid = false;
                ReportError(code: SemanticDiagnosticCode.ThrowsListMisplaced,
                    message: $"'@throws' lists crashables, and '{name}' isn't one (a crashable is declared 'crashable {name}').",
                    location: at);
            }

            if (!valid)
            {
                continue;
            }

            List<string> missing = routine.ThrowableTypes
                                          .OfType<CrashableTypeSymbol>()
                                          .Where(predicate: t => !listed.Any(predicate: l => l.FullName == t.FullName))
                                          .Select(selector: t => t.Name)
                                          .Distinct()
                                          .ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            ReportError(code: SemanticDiagnosticCode.ThrowsListIncomplete,
                message: $"'{routine.Name}' can throw {string.Join(separator: ", ", values: missing)}, which its '@throws' " +
                         $"doesn't list. Add {(missing.Count == 1 ? "it" : "them")} to the list, or recover " +
                         $"{(missing.Count == 1 ? "it" : "them")} inside with 'check'.",
                location: at);
        }
    }

    /// <summary>The fewest bytes a caught error holds its crashable in (<c>Crashables.data</c>): the room the
    /// standard library's own crashables need, so a program whose crashables all fit keeps the layout the
    /// standard library is built with.</summary>
    internal const int CrashableRoomBytes = 64;

    /// <summary>The fewest bytes this analysis sizes the room of <c>Crashables</c> to (at least
    /// <see cref="CrashableRoomBytes"/>). The debug-jit resident base is built with the room of the program it serves,
    /// so a program whose largest crashable is bigger never runs against a base built for another size.</summary>
    internal int CrashablesRoomFloor { get; init; } = CrashableRoomBytes;

    /// <summary>
    /// Sizes the room of <c>Crashables</c> (its <c>data</c>) for the program's largest crashable, never below
    /// <see cref="CrashableRoomBytes"/>. A failure is written into the mold of the flow it is thrown on, a mold of
    /// this many bytes, and a <c>check</c> copies it from there into the room of the <c>Check</c> it gives, so every
    /// crashable fits both, whatever its size.
    /// </summary>
    private void SizeCrashablesRoom()
    {
        if (_registry.LookupType(name: Builder.Declaration.RuntimeContract.Crashables) is not RecordTypeSymbol crashables)
        {
            return;
        }

        int index = crashables.MemberVariables.FindIndex(match: mv => mv.Name == "data");
        if (index < 0 || crashables.MemberVariables[index: index].Type is not RecordTypeSymbol
            {
                GenericDefinition: { } arrayDef, TypeArguments: [var word, ConstGenericValueTypeSymbol]
            })
        {
            return;
        }

        int largest = Math.Max(val1: CrashableRoomBytes, val2: CrashablesRoomFloor);
        foreach (TypeSymbol type in _registry.GetAllTypes())
        {
            if (type is CrashableTypeSymbol { IsGenericDefinition: false } crashable)
            {
                largest = Math.Max(val1: largest, val2: crashable.SizeBytes(pointerSize: 8));
            }
        }

        long words = (largest + 7) / 8;
        TypeSymbol room = _registry.GetOrCreateResolution(genericDef: arrayDef,
            typeArguments: [word, new ConstGenericValueTypeSymbol(literalText: words.ToString(), value: words, explicitTypeName: null)]);
        if (!ReferenceEquals(objA: room, objB: crashables.MemberVariables[index: index].Type))
        {
            crashables.MemberVariables[index: index] =
                crashables.MemberVariables[index: index].WithSubstitutedType(newType: room);
        }
    }

    /// <summary>
    /// A crashable says what went wrong in its own words: it must declare <c>crash_message</c> (RF-S704). Its
    /// <c>crash_title</c> comes from its name.
    /// </summary>
    private void ValidateCrashMessage(TypeSymbol type)
    {
        if (type is not CrashableTypeSymbol crashable || crashable.Location is not { FileName.Length: > 0 } ||
            _registry.GetMemberRoutinesForType(type: crashable)
                     .Any(predicate: m => m is { Name: RuntimeContract.CrashMessage, IsSynthesized: false }))
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.CrashMessageNotImplemented,
            message:
            $"The crashable '{crashable.Name}' has no 'crash_message'. Write the message it reports when it " +
            $"crashes: 'routine {crashable.Name}.crash_message() -> Text'.",
            location: crashable.Location);
    }

    /// <summary>Where a conformance error points for a type registered without a location (a stdlib type checked
    /// by <see cref="ValidateStdlibProtocolImplementations"/>): its declaration.</summary>
    private SourceLocation? _conformanceDeclaredAt;

    /// <summary>
    /// The protocol check of <see cref="ValidateTypeProtocolImplementation"/> for every type the stdlib declares.
    /// Stdlib types are registered without a location, so a user build skips them, and `validate-stdlib` is where a
    /// stdlib type that leaves out a routine its protocols require (an `Int` without `shift_left`) is caught.
    /// </summary>
    private void ValidateStdlibProtocolImplementations()
    {
        foreach ((Program program, _, string module) in _registry.StdlibPrograms)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                (string? name, SourceLocation? at) = node switch
                {
                    RecordDeclaration r => (r.Name, r.Location),
                    EntityDeclaration e => (e.Name, e.Location),
                    CrashableDeclaration c => (c.Name, c.Location),
                    _ => ((string?)null, (SourceLocation?)null)
                };
                if (name == null || _registry.LookupType(name: $"{module}.{name}") is not { } type ||
                    type.Location is { FileName.Length: > 0 })
                {
                    continue;
                }

                List<TypeSymbol>? protocols = type switch
                {
                    RecordTypeSymbol record => record.ImplementedProtocols,
                    EntityTypeSymbol entity => entity.ImplementedProtocols,
                    _ => null
                };
                // A conformance with an `when` holds per instantiation, through the inner type's routines.
                Dictionary<string, List<(string ParamName, string ProtocolName)>>? conditional = type switch
                {
                    RecordTypeSymbol record => record.ConditionalObeys,
                    EntityTypeSymbol entity => entity.ConditionalObeys,
                    _ => null
                };
                _conformanceDeclaredAt = at;
                foreach (ProtocolTypeSymbol protocol in (protocols ?? []).OfType<ProtocolTypeSymbol>())
                {
                    if (!_implicitProtocolConformances.Contains(item: (type.FullName, protocol.Name)) &&
                        conditional?.ContainsKey(key: protocol.Name) != true)
                    {
                        ValidateProtocolMemberRoutines(type: type, protocol: protocol);
                    }
                }

                _conformanceDeclaredAt = null;
            }
        }
    }

    /// <summary>
    /// Validates that a specific type implements all member routines required by its declared protocols.
    /// </summary>
    private void ValidateTypeProtocolImplementation(TypeSymbol type)
    {
        // Skip stdlib/fallback types (types without source location or in System module)
        // These are pre-defined types that may not have full member routine implementations in test environments
        if (type.Location == null || string.IsNullOrEmpty(value: type.Location.FileName))
        {
            return;
        }

        // Get the list of implemented protocols for this type
        List<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeSymbol record => record.ImplementedProtocols,
            EntityTypeSymbol entity => entity.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null || implementedProtocols.Count == 0)
        {
            return;
        }

        // Check each protocol — skip protocols added by implicit marker conformance
        foreach (TypeSymbol protocol in implementedProtocols)
        {
            if (protocol is not ProtocolTypeSymbol protoInfo)
            {
                continue;
            }

            if (!_implicitProtocolConformances.Contains(item: (type.FullName, protoInfo.Name)))
            {
                ValidateProtocolMemberRoutines(type: type, protocol: protoInfo);
            }
        }

    }

    /// <summary>The message for a type that obeys a protocol and lacks one of its routines: the routine to write, in
    /// the type's own terms.</summary>
    private string RequiredRoutineMessage(TypeSymbol type, ProtocolTypeSymbol protocol,
        ProtocolMemberRoutineInfo required)
    {
        string parameters = string.Join(separator: ", ",
            values: required.ParameterNames.Select(selector: (n, i) =>
                $"{n}: {WrittenTypeName(type: _registry.ReplaceProtocolSelf(
                    type: required.ParameterTypes[index: i], owner: type))}"));
        string result = required.ReturnType is { IsNone: false } returned
            ? $" -> {WrittenTypeName(type: _registry.ReplaceProtocolSelf(type: returned, owner: type))}"
            : "";
        string owner = required.IsInstanceMemberRoutine ? "routine" : "common routine";
        string failable = required.IsFailable ? "failable " : "";
        return $"'{type.Name}' obeys '{protocol.Name}', which needs the routine '{required.Name}', and " +
               $"'{type.Name}' has none. Write `{failable}{owner} {type.Name}.{required.Name}({parameters}){result}`.";
    }

    /// <summary>
    /// A type's name as the program writes it, each part without its module (<c>List[Team]</c>). Suflae has no
    /// access tokens and writes an entity as itself, so in a Suflae build a <c>Viewing</c>, <c>Modifying</c> or
    /// <c>Roamed</c> around a type is shown as the type it holds.
    /// </summary>
    private string WrittenTypeName(TypeSymbol type)
    {
        if (!_registry.CompilationRules.ChecksAccessTokens &&
            type.BareName is RuntimeContract.Viewing or RuntimeContract.Modifying or RuntimeContract.Roamed &&
            type.TypeArguments is [{ } held])
        {
            return WrittenTypeName(type: held);
        }

        return type.TypeArguments is { Count: > 0 } arguments
            ? $"{type.BareName}[{string.Join(separator: ", ", values: arguments.Select(selector: WrittenTypeName))}]"
            : type.BareName;
    }

    /// <summary>
    /// A protocol's creator requirement (<c>routine Me!(text: Text)</c>) is met by a creator of the type that
    /// takes the same parameters, by name and type, with <c>Me</c> standing for the type. A creator that
    /// cannot fail meets a failable requirement, but a failable creator does not meet one that cannot fail.
    /// </summary>
    private void ValidateRequiredProtocolCreator(TypeSymbol type, ProtocolTypeSymbol protocol,
        ProtocolMemberRoutineInfo requiredCreator)
    {
        var candidates = new List<RoutineInfo>();
        _registry.CollectCreatorCandidates(type: type, candidates: candidates);
        candidates.AddRange(collection: _registry.GetMemberRoutinesForType(type: type)
                                                 .Where(predicate: m => m.IsCreator));
        bool met = candidates.Any(predicate: creator =>
            !creator.IsSynthesized && (requiredCreator.IsFailable || !creator.IsFailable) &&
            creator.Parameters.Count == requiredCreator.ParameterNames.Count &&
            creator.Parameters.Select(selector: (p, i) =>
                        p.Name == requiredCreator.ParameterNames[index: i] &&
                        _registry.ReplaceProtocolSelf(type: requiredCreator.ParameterTypes[index: i], owner: type)
                                 .FullName == p.Type.FullName)
                   .All(predicate: matches => matches));
        if (met)
        {
            return;
        }

        string parameters = string.Join(separator: ", ",
            values: requiredCreator.ParameterNames.Select(selector: (n, i) =>
                $"{n}: {WrittenTypeName(type: _registry.ReplaceProtocolSelf(
                    type: requiredCreator.ParameterTypes[index: i], owner: type))}"));
        string failable = requiredCreator.IsFailable ? "failable " : "";
        ReportError(code: SemanticDiagnosticCode.MissingProtocolMemberRoutine,
            message: $"'{type.Name}' obeys '{protocol.Name}', which requires a creator " +
                     $"`{failable}routine {type.Name}({parameters}) -> {type.Name}`, but '{type.Name}' has none that " +
                     "takes those parameters.",
            location: type.Location ?? _conformanceDeclaredAt ?? new SourceLocation(FileName: "",
                Line: 0,
                Column: 0,
                Position: 0));
    }

    /// <summary>
    /// Validates that a type implements all member routines required by a protocol.
    /// </summary>
    private void ValidateProtocolMemberRoutines(TypeSymbol type, ProtocolTypeSymbol protocol,
        List<ProtocolTypeSymbol>? through = null)
    {
        foreach (ProtocolMemberRoutineInfo requiredMemberRoutine in protocol.MemberRoutines)
        {
            // A protocol that obeys this one may write the routine for every type that obeys it
            // (`routine Emitter[T].emitter()` is `Emittable[T]`'s `emitter` for every emitter).
            if (through?.Any(predicate: child => WritesRoutine(protocol: child, name: requiredMemberRoutine.Name)) ==
                true)
            {
                continue;
            }

            ValidateRequiredProtocolMemberRoutine(type: type,
                protocol: protocol,
                requiredMemberRoutine: requiredMemberRoutine);
        }

        // Also check parent protocols
        List<ProtocolTypeSymbol> chain = [.. through ?? [], protocol];
        foreach (ProtocolTypeSymbol parentProtocol in protocol.ParentProtocols)
        {
            ValidateProtocolMemberRoutines(type: type, protocol: parentProtocol, through: chain);
        }
    }

    /// <summary>Whether a routine named <paramref name="name"/> is written on <paramref name="protocol"/> itself (on its
    /// definition), with a body, for every type that obeys it.</summary>
    private bool WritesRoutine(ProtocolTypeSymbol protocol, string name)
    {
        return _registry.GetMemberRoutinesForOwner(ownerType: protocol.GenericDefinition ?? protocol)
                        .Any(predicate: r => r.Name == name && !r.IsSynthesized);
    }

    /// <summary>
    /// Validates a single required protocol member routine against <paramref name="type"/>'s
    /// implementation: skips defaulted / auto-derived-variant requirements, reports a missing
    /// implementation, an illegal innate override, or a mutation-contract violation.
    /// </summary>
    private void ValidateRequiredProtocolMemberRoutine(TypeSymbol type, ProtocolTypeSymbol protocol,
        ProtocolMemberRoutineInfo requiredMemberRoutine)
    {
        // Skip member routines with default implementations
        if (requiredMemberRoutine.HasDefaultImplementation)
        {
            return;
        }

        // `routine Me!(text: Text)` requires a creator, which the type writes under its own name.
        if (requiredMemberRoutine.IsCreatorRequirement)
        {
            ValidateRequiredProtocolCreator(type: type,
                protocol: protocol,
                requiredCreator: requiredMemberRoutine);
            return;
        }

        // Look for the member routine on the type (not on its protocols — that would find the protocol's own declaration)
        // Routine names are bare; the failable `!` is a structured flag. Check the bare name,
        // then (for a failable requirement) fall back to a same-named failable implementation.
        IEnumerable<RoutineInfo> ownMemberRoutines =
            _registry.GetMemberRoutinesForType(type: type);
        RoutineInfo? typeMemberRoutine =
            ownMemberRoutines.FirstOrDefault(predicate: m => m.Name == requiredMemberRoutine.Name);
        if (typeMemberRoutine == null && requiredMemberRoutine.IsFailable)
        {
            typeMemberRoutine = ownMemberRoutines.FirstOrDefault(predicate: m =>
                m.Name == requiredMemberRoutine.Name && m.IsFailable);
        }

        // A stdlib type checked by `validate-stdlib` gets its derived routines (`assign`, `duplicate`) when it is
        // instantiated, so a derive template for the requirement counts as the routine.
        if (typeMemberRoutine == null && _conformanceDeclaredAt != null &&
            _registry.GetDeriveTemplate(name: requiredMemberRoutine.Name,
                arity: requiredMemberRoutine.ParameterTypes.Count,
                forType: type) != null)
        {
            return;
        }

        if (typeMemberRoutine == null)
        {
            ReportError(code: SemanticDiagnosticCode.MissingProtocolMemberRoutine,
                message: RequiredRoutineMessage(type: type, protocol: protocol, required: requiredMemberRoutine),
                location: type.Location ?? _conformanceDeclaredAt ?? new SourceLocation(FileName: "",
                    Line: 0,
                    Column: 0,
                    Position: 0));
        }
        else if (requiredMemberRoutine.GenerationKind == ProtocolRoutineKind.Innate &&
                 !typeMemberRoutine.IsSynthesized)
        {
            ReportError(code: SemanticDiagnosticCode.InnateOverrideNotAllowed,
                message:
                $"Cannot override innate routine '{protocol.Name}.{requiredMemberRoutine.Name}'. " +
                "Innate routines are builder-provided and cannot be overridden.",
                location: typeMemberRoutine.Location ??
                          new SourceLocation(FileName: "",
                              Line: 0,
                              Column: 0,
                              Position: 0));
        }
        // Suflae has no `@readonly` or `@reshaping`: the contract is RazorForge's.
        else if (_registry.CompilationRules.ChecksReadonly &&
                 typeMemberRoutine.MutationCategory > requiredMemberRoutine.Mutation)
        {
            // #61: Protocol mutation contract validation. The implementation must not be MORE
            // mutating than the protocol declares (Readonly < Writable < Reshaping): callers
            // hold tokens sized to the protocol's category — e.g. a Viewing token for @readonly,
            // a Modifying token for the writable default — so an impl that mutates or relocates
            // beyond that contract would be unsound (a Reshaping impl behind a Writable protocol
            // could relocate mid-iteration through a Modifying token, invalidating iterators).
            ReportError(code: SemanticDiagnosticCode.ProtocolMutationContractViolation,
                message:
                $"Protocol '{protocol.Name}' requires '{requiredMemberRoutine.Name}' to be " +
                $"@{requiredMemberRoutine.Mutation.ToString().ToLowerInvariant()} (or less mutating), " +
                $"but implementation on '{type.Name}' is @{typeMemberRoutine.MutationCategory.ToString().ToLowerInvariant()}.",
                location: typeMemberRoutine.Location ??
                          new SourceLocation(FileName: "",
                              Line: 0,
                              Column: 0,
                              Position: 0));
        }
    }

    #endregion

    #region Constraint Validation

    /// <summary>
    /// Validates that generic constraints only reference declared type parameters.
    /// </summary>
    /// <param name="constraints">The constraints to validate.</param>
    /// <param name="typeParameters">The declared type parameters.</param>
    /// <param name="location">Source location for error reporting.</param>
    internal void ValidateConstraintTypeParameters(List<GenericConstraintDeclaration>? constraints,
        List<string>? typeParameters, SourceLocation? location)
    {
        if (constraints == null || constraints.Count == 0)
        {
            return;
        }

        HashSet<string> validParams = typeParameters != null
            ? [.. typeParameters]
            : [];

        foreach (GenericConstraintDeclaration constraint in constraints.Where(predicate: c =>
                     !validParams.Contains(item: c.ParameterName)))
        {
            ReportError(code: SemanticDiagnosticCode.UnknownTypeParameterInConstraint,
                message:
                $"Type parameter '{constraint.ParameterName}' in constraint is not declared. " +
                $"Declared type parameters: {(typeParameters?.Count > 0 ? string.Join(separator: ", ", values: typeParameters) : "none")}.",
                location: constraint.Location ?? location ??
                new SourceLocation(FileName: "",
                    Line: 0,
                    Column: 0,
                    Position: 0));
        }
    }

    #endregion
}
