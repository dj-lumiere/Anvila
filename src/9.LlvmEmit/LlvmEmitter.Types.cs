using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Reprs;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.LlvmEmit;

/// <summary>
/// Type mapping: RazorForge/Suflae types -> LLVM IR types.
/// </summary>
public partial class LlvmEmitter
{
    #region Type Mapping

    /// <summary>
    /// Gets the LLVM type needed by this builder phase.
    /// </summary>
    private static string GetLlvmType(BackendRepr repr)
    {
        return repr.LlvmAbiType;
    }

    /// <summary>
    /// Gets the expression LLVM type needed by this builder phase.
    /// </summary>
    private string GetExpressionLlvmType(Expression expr, string fallback = "i64")
    {
        if (expr.ResolvedRepr != null)
        {
            return GetLlvmType(repr: expr.ResolvedRepr);
        }

        TypeSymbol? type = GetExpressionType(expr: expr);
        return type != null
            ? GetLlvmType(type: type)
            : fallback;
    }

    /// <summary>
    /// Gets the LLVM type needed by this builder phase.
    /// </summary>
    /// <summary>
    /// The in-MEMORY/aggregate LLVM type of a record field. <c>Bool</c> (register type <c>i1</c>) is
    /// stored as <c>i8</c> inside structs: an <c>i1</c> field in a value record returned by <c>sret</c>
    /// miscompiles at -O3 (its 1-bit-value / 1-byte-slot duality confuses SROA/store-forwarding, which
    /// corrupts the neighbouring wide field). Using the honest byte type and zext/trunc at the field
    /// boundary — exactly how clang lowers C++ <c>bool</c> — removes the <c>i1</c> from the aggregate.
    /// The struct size is unchanged (an <c>i1</c> already occupied a byte), so field offsets are stable.
    /// </summary>
    private string GetFieldStorageLlvmType(TypeSymbol type)
    {
        return FieldNeedsBoolStorage(type: type)
            ? "i8"
            : GetValueLlvmType(type: type);
    }

    /// <summary>True when a record field's register type is <c>i1</c> (Bool) and needs <c>i8</c> storage.</summary>
    private bool FieldNeedsBoolStorage(TypeSymbol type)
    {
        return GetLlvmType(type: type) is "i1";
    }

    /// <summary>
    /// The LLVM type for STORING a value of <paramref name="type"/> (an alloca, a struct field, a
    /// by-value parameter). Identical to <see cref="GetLlvmType(TypeSymbol)"/> except for <c>None</c>: None is
    /// <c>@llvm("void")</c>, and <c>void</c> is illegal as a value (you cannot <c>alloca void</c> or
    /// put a <c>void</c> field in a struct). A stored None is the empty record <c>{}</c> — a real
    /// zero-size value. Direct routine RETURNS keep using <see cref="GetLlvmType(TypeSymbol)"/> (so void-returning
    /// routines stay <c>void</c>); only None-as-a-VALUE uses this.
    /// </summary>
    private string GetValueLlvmType(TypeSymbol type)
    {
        string t = GetLlvmType(type: type);
        return t == "void"
            ? "{}"
            : t;
    }

    /// <summary>zext an <c>i1</c> Bool value to its <c>i8</c> storage form before writing an aggregate field.</summary>
    private string CoerceBoolToStorage(System.Text.StringBuilder sb, string value,
        TypeSymbol fieldType)
    {
        if (!FieldNeedsBoolStorage(type: fieldType))
        {
            return value;
        }

        string t = NextTemp();
        EmitLine(sb: sb, line: $"  {t} = zext i1 {value} to i8");
        return t;
    }

    /// <summary>trunc an <c>i8</c> storage Bool back to <c>i1</c> after reading an aggregate field.</summary>
    private string CoerceStorageToBool(System.Text.StringBuilder sb, string storageValue,
        TypeSymbol fieldType)
    {
        if (!FieldNeedsBoolStorage(type: fieldType))
        {
            return storageValue;
        }

        string t = NextTemp();
        EmitLine(sb: sb, line: $"  {t} = trunc i8 {storageValue} to i1");
        return t;
    }

    private string GetLlvmType(TypeSymbol type)
    {
        // Array[T, N] element-type consistency: the `@llvm("[{N} x {T}]")` template bakes the element's
        // STRUCTURAL type (RecordTypeSymbol.LlvmType → `{i32,i32}`), but record/variant VALUES carry the
        // NAMED struct type (`%"Record.X"`). LLVM's value-aggregate ops (insertvalue for array literals)
        // require the element type to match EXACTLY, so a named value into a structural-element array is a
        // hard type error. Rebuild the array type from the element's own GetLlvmType (the single source of
        // truth for the named form) so element positions everywhere — literal, param, alloca — agree.
        // Scalars/wrappers are skipped (their structural form already equals their named form).
        if (type is RecordTypeSymbol
            {
                IsGenericResolution: true,
                BackendType: ['[', ..],
                TypeArguments: [RecordTypeSymbol or VariantTypeSymbol, ConstGenericValueTypeSymbol]
            } arrayType)
        {
            TypeSymbol elem = arrayType.TypeArguments![index: 0];
            long count = ((ConstGenericValueTypeSymbol)arrayType.TypeArguments[index: 1]).Value;
            return $"[{count} x {GetLlvmType(type: elem)}]";
        }

        return type switch
        {
            // Records with @llvm annotation -> use backend type directly (skip generic definitions with template holes)
            RecordTypeSymbol
            {
                BackendType: not null, IsGenericDefinition: false
            } record => record.LlvmType,

            // Generic-definition record (unresolved) -> HARD ERROR. codegen never fails silently: a
            // generic-def type reaching the backend is an upstream monomorphization bug, not a `ptr` to
            // paper over. It must be a concrete instance (GenericMonomorphizationPass) before codegen.
            RecordTypeSymbol { IsGenericDefinition: true } genDefRecord => throw new
                InvalidOperationException(
                    message:
                    $"Generic-definition record '{genDefRecord.Name}' reached GetLlvmType " +
                    $"[inRoutine={_currentEmittingRoutine?.FullName}] — it must be monomorphized to a concrete " +
                    "instance before codegen. codegen is a never-fail translator; this leak is an upstream bug."),

            // Variants -> struct { tag, payload }. Variant is a RecordTypeSymbol subclass, so this
            // MUST precede the RecordTypeSymbol arms below or a variant would be treated as a record.
            VariantTypeSymbol variant => GetVariantTypeName(variant: variant),

            // Multi-member-variable records -> LLVM struct type.
            // Also ensure the struct declaration is emitted -> carrier types like Result[Result[T]]
            // may be created on-demand without being registered, so the type loop never sees them.
            RecordTypeSymbol record => EnsureRecordTypeDeclared(record: record),

            // Entities (and Crashable, an entity subclass) -> pointer to LLVM struct
            EntityTypeSymbol => "ptr",

            // A marker borrow protocol (Accessing[X]/Controlling[X]) is laid out as its inner X (an entity → ptr, a
            // value → the value's own layout).
            ProtocolTypeSymbol { TypeArguments: [{ } markerInner] } markerProto when
                Declaration.RuntimeContract.IsMarkerProtocol(
                    baseName: (markerProto.GenericDefinition ?? markerProto).BareName) =>
                GetLlvmType(type: markerInner),

            // Any other protocol -> HARD ERROR. A protocol reaching the backend (an iterator's
            // `Emittable[T]` return, a generic-def body, an unsubstituted protocol-typed slot) is an upstream
            // monomorphization gap. codegen never fails silently: surface it loudly so the leak is fixed
            // upstream, not masked by a type-erased `ptr`.
            ProtocolTypeSymbol proto => throw new InvalidOperationException(
                message:
                $"Protocol type '{proto.Name}' reached GetLlvmType [inRoutine={_currentEmittingRoutine?.FullName}] — " +
                "a non-marker protocol must be substituted/monomorphized before codegen. codegen is a never-fail " +
                "translator; this leak is an upstream bug."),

            // Routine types -> fat value { ptr fn, ptr bound } (v0.4.1). `fn` is the callee's bare
            // C-ABI symbol; `bound` is null (captureless) or a heap payload of pre-bound captures
            // (= C userdata). A captureless value is effectively the 1-word `fn`; the pair maps onto
            // C's (callback, userdata) convention. See [[cabi-callback-ffi]].
            RoutineTypeSymbol => "{ ptr, ptr }",

            // Const generic value -> the LLVM form of its DECLARED type (e.g. a `N: U32` const is `i32`,
            // not a blanket `i64`). ResolveConstGenericUnderlyingType maps it to the underlying primitive
            // (defaulting to U64 for an untyped literal); guard the degenerate case where that lookup fails
            // and returns the const itself, which would otherwise recurse into this same arm.
            ConstGenericValueTypeSymbol constGen =>
                ResolveConstGenericUnderlyingType(constVal: constGen) is { } underlying &&
                underlying is not ConstGenericValueTypeSymbol
                    ? GetLlvmType(type: underlying)
                    : throw new InvalidOperationException(
                        message: $"The type of the constant '{constGen.Name}' is not registered."),

            // Unresolved generic parameter -> illegal in codegen. All type parameters must be
            // substituted by GenericMonomorphizationPass before the backend is entered.
            GenericParameterTypeSymbol gp => throw new InvalidOperationException(
                message:
                $"GenericParameterTypeSymbol '{gp.Name}' reached GetLlvmType [inRoutine={_currentEmittingRoutine?.FullName}] " +
                "all generic parameters must be substituted before codegen entry. " +
                "Check that GenericMonomorphizationPass ran and GenericAstRewriter " +
                "annotated all expression ResolvedTypes."),

            // Error placeholder
            ErrorTypeSymbol => throw new InvalidOperationException(
                message:
                $"Error type found in codegen - semantic analysis should have caught this [inRoutine={_currentEmittingRoutine?.FullName}]"),

            // Unknown
            _ => throw new InvalidOperationException(
                message: $"Unknown type category: {type.Category} ({type.GetType().Name} '{type.FullName}') [inRoutine={_currentEmittingRoutine?.FullName}]")
        };
    }

    /// <summary>
    /// Gets the LLVM struct type name for a record, ensuring its declaration is emitted.
    /// Called from GetLLVMType so on-demand records (e.g., Result[Result[T]]) are always declared.
    /// </summary>
    private string EnsureRecordTypeDeclared(RecordTypeSymbol record)
    {
        string name = GetRecordTypeName(record: record);
        // Proactively declare if not yet emitted -> covers types created on-demand
        // that are never visited by the registry iteration in GenerateTypes().
        if (!_generatedTypes.Contains(item: name))
        {
            GenerateRecordType(record: record);
        }

        return name;
    }

    /// <summary>
    /// Realm-marked mangle base (option-b, ambient-bare): the ambient RF realm renders BARE
    /// (<see cref="TypeSymbol.FullName"/>) so pure-RF IR is byte-identical (zero golden regold); a
    /// non-ambient realm (the SF wrapper world-line) gets a <c>{Realm}::</c> prefix so an SF
    /// <c>Core.List[S32]</c> (layout <c>{ inner }</c>) never collides with the RF <c>Core.List[S32]</c>
    /// (real element storage) — they are DISTINCT LLVM structs/symbols in a mixed binary (bare `List`
    /// in a `.sf` file → SF wrapper delegating to an RF inner, both live at once). Generic args stay in
    /// FullName (ambient/bare) — only the owner's own realm is marked.
    /// </summary>
    private static string RealmMangleBase(TypeSymbol t)
    {
        return t.Realm == TypeModel.Realms.Shared
            ? t.FullName
            : $"{t.Realm}::{t.FullName}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for a record.
    /// </summary>
    private static string GetRecordTypeName(RecordTypeSymbol record)
    {
        // Module-qualified (TypeSymbol.FullName) so same-named records in different modules never
        // collide into one LLVM struct name (which LLVM would silently rename to `.0`).
        return $"%{Q(name: $"Record.{RealmMangleBase(t: record)}")}";
    }

    /// <summary>The LLVM struct name for an entity — no generation side effect. Used INSIDE
    /// GenerateEntityType (where ensuring would re-enter) and other name-only contexts. Uses the
    /// module-qualified <see cref="TypeSymbol.FullName"/> (e.g. <c>Entity.Random.Random</c>,
    /// <c>Entity.Core.List[Core.S64]</c>) so same-named entities in different modules never collide
    /// into one LLVM struct name (which LLVM would silently rename to <c>.0</c> and miscompile).</summary>
    private static string RawEntityTypeName(EntityTypeSymbol entity)
    {
        return $"%{Q(name: $"Entity.{RealmMangleBase(t: entity)}")}";
    }

    /// <summary>The bare LLVM struct name for a crashable — no generation side effect.</summary>
    private static string RawCrashableTypeName(CrashableTypeSymbol crashable)
    {
        return $"%{Q(name: $"Crashable.{RealmMangleBase(t: crashable)}")}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for an entity, ensuring its struct definition is emitted on
    /// first use. Entity structs are referenced only at use sites (alloc / field-access / size GEP),
    /// never as a by-value field (entity fields are `ptr`), so on-demand emission here lets the broad
    /// registry sweep be skipped — pruning entities the program never touches.
    /// </summary>
    private string GetEntityTypeName(EntityTypeSymbol entity)
    {
        string name = RawEntityTypeName(entity: entity);
        if (!_generatedTypes.Contains(item: name) && !entity.IsGenericDefinition &&
            !(entity.TypeArguments is { Count: > 0 } a &&
              a.Any(predicate: ContainsGenericParameter)))
        {
            GenerateEntityType(entity: entity);
        }

        return name;
    }

    /// <summary>
    /// Gets the LLVM struct type name for a crashable type, ensuring its struct definition is emitted
    /// on first use (crashables are referenced opaquely in size GEPs and field access).
    /// </summary>
    private string GetCrashableTypeName(CrashableTypeSymbol crashable)
    {
        string name = RawCrashableTypeName(crashable: crashable);
        if (!_generatedTypes.Contains(item: name) && !crashable.IsGenericDefinition &&
            !(crashable.TypeArguments is { Count: > 0 } a &&
              a.Any(predicate: ContainsGenericParameter)))
        {
            GenerateCrashableType(crashable: crashable);
        }

        return name;
    }

    /// <summary>The bare LLVM struct name for a variant — no generation side effect.</summary>
    private static string RawVariantTypeName(VariantTypeSymbol variant)
    {
        return $"%{Q(name: $"Variant.{variant.FullName}")}";
    }

    /// <summary>
    /// Gets the LLVM struct type name for a variant, ensuring its struct (tag + payload) is emitted
    /// on first use — variants are passed/returned by value, so the def must exist.
    /// </summary>
    private string GetVariantTypeName(VariantTypeSymbol variant)
    {
        string name = RawVariantTypeName(variant: variant);
        if (!_generatedTypes.Contains(item: name) && !variant.IsGenericDefinition &&
            !(variant.TypeArguments is { Count: > 0 } a &&
              a.Any(predicate: ContainsGenericParameter)))
        {
            GenerateVariantType(variant: variant);
        }

        return name;
    }

    /// <summary>
    /// Returns the named LLVM type for an error-handling carrier (Maybe[T], Result[T], Lookup[T]).
    /// Delegates to GetLLVMType -> carrier layouts come from their Standard library definitions.
    /// </summary>
    private string GetCarrierLlvmType(TypeSymbol type)
    {
        return GetLlvmType(type: type);
    }

    /// <summary>Returns true if <paramref name="type"/> is a Maybe[T], Result[T], or Lookup[T] carrier.</summary>
    private static bool IsCarrierType(TypeSymbol type)
    {
        return type is RecordTypeSymbol { CarrierKind: not CarrierKind.None };
    }

    /// <summary>Returns true if <paramref name="type"/> is a Maybe[T] carrier.</summary>
    private static bool IsMaybeType(TypeSymbol type)
    {
        return type is RecordTypeSymbol { CarrierKind: CarrierKind.Maybe };
    }

    /// <summary>
    /// Quotes an LLVM identifier if it contains characters that require quoting.
    /// LLVM allows any characters in quoted identifiers: @"Hijacked[Point].eq", %"Record.Hijacked[Point]".
    /// Unquoted identifiers only allow [a-zA-Z$._0-9-].
    /// </summary>
    private static string Q(string name)
    {
        bool needsQuoting = name.Any(predicate: c =>
            !char.IsLetterOrDigit(c: c) && c != '$' && c != '.' && c != '_' && c != '-');
        return needsQuoting
            ? $"\"{name}\""
            : name;
    }

    /// <summary>
    /// Gets the LLVM type for a function parameter or return type.
    /// For entities, this returns ptr (all entities are pointers).
    /// For records, this returns the struct type (passed by value).
    /// </summary>
    private string GetParameterLlvmType(TypeSymbol type)
    {
        return type switch
        {
            // Entities (and Crashable, an entity subclass) are always passed as pointers
            EntityTypeSymbol => "ptr",

            // Other types use normal mapping
            _ => GetLlvmType(type: type)
        };
    }

    /// <summary>
    /// Gets the size in bytes for a type. Delegates to <see cref="TypeSymbol.SizeBytes"/>
    /// so each type kind owns its own size rule.
    /// </summary>
    private int GetTypeSize(TypeSymbol type)
    {
        return type.SizeBytes(pointerSize: _pointerSizeBytes);
    }

    /// <summary>
    /// Aligns a size to a given alignment.
    /// </summary>
    private static int AlignTo(int size, int alignment)
    {
        return (size + alignment - 1) / alignment * alignment;
    }


    #endregion

    // Node layout (appended once at module end):
    //  !0 = root
    //  !1 = i1 !2 = i8 !3 = i16 !4 = i32
    //  !5 = i64 !6 = float !7 = double !8 = ptr
    //  !9 = half !10 = fp128 !11 = i128
    //  !12 = i1 access tag !13 = i8 !14 = i16 !15 = i32
    //  !16 = i64 access tag !17 = float !18 = double !19 = ptr
    //  !20 = half access tag !21 = fp128 !22 = i128

    /// <summary>
    /// Stores the TBAA metadata section state used by this builder phase.
    /// </summary>
    private static readonly string TbaaMetadataSection = "; TBAA metadata\n" +
                                                         "!0 = !{!\"RF TBAA Root\"}\n" +
                                                         "!1 = !{!\"i1\", !0}\n" +
                                                         "!2 = !{!\"i8\", !0}\n" +
                                                         "!3 = !{!\"i16\", !0}\n" +
                                                         "!4 = !{!\"i32\", !0}\n" +
                                                         "!5 = !{!\"i64\", !0}\n" +
                                                         "!6 = !{!\"float\", !0}\n" +
                                                         "!7 = !{!\"double\", !0}\n" +
                                                         "!8 = !{!\"ptr\", !0}\n" +
                                                         "!9 = !{!\"half\", !0}\n" +
                                                         "!10 = !{!\"fp128\", !0}\n" +
                                                         "!11 = !{!\"i128\", !0}\n" +
                                                         "!12 = !{!1,  !1,  i64 0}\n" +
                                                         "!13 = !{!2,  !2,  i64 0}\n" +
                                                         "!14 = !{!3,  !3,  i64 0}\n" +
                                                         "!15 = !{!4,  !4,  i64 0}\n" +
                                                         "!16 = !{!5,  !5,  i64 0}\n" +
                                                         "!17 = !{!6,  !6,  i64 0}\n" +
                                                         "!18 = !{!7,  !7,  i64 0}\n" +
                                                         "!19 = !{!8,  !8,  i64 0}\n" +
                                                         "!20 = !{!9,  !9,  i64 0}\n" +
                                                         "!21 = !{!10, !10, i64 0}\n" +
                                                         "!22 = !{!11, !11, i64 0}\n";

    private static readonly Dictionary<string, string> TbaaTagByLlvmType = new()
    {
        [key: "i1"] = ", !tbaa !12",
        [key: "i8"] = ", !tbaa !13",
        [key: "i16"] = ", !tbaa !14",
        [key: "i32"] = ", !tbaa !15",
        [key: "i64"] = ", !tbaa !16",
        [key: "float"] = ", !tbaa !17",
        [key: "double"] = ", !tbaa !18",
        [key: "ptr"] = ", !tbaa !19",
        [key: "half"] = ", !tbaa !20",
        [key: "fp128"] = ", !tbaa !21",
        [key: "i128"] = ", !tbaa !22"
    };

    /// <summary>
    /// Performs the apply TBAA step for this builder phase.
    /// </summary>
    private static string ApplyTbaa(string ir)
    {
        string[] lines = ir.Split(separator: '\n');
        var sb = new System.Text.StringBuilder(capacity: ir.Length + 2048);
        foreach (string line in lines)
        {
            sb.Append(value: TagLine(line: line))
              .Append(value: '\n');
        }

        sb.Append(value: TbaaMetadataSection);
        return sb.ToString();
    }

    /// <summary>
    /// Performs the tag line step for this builder phase.
    /// </summary>
    private static string TagLine(string line)
    {
        if (line.Contains(value: "!tbaa"))
        {
            return line;
        }

        ReadOnlySpan<char> t = line.AsSpan()
                                   .TrimStart();

        // load: " %x = load TYPE, ptr ..."
        int loadIdx = line.IndexOf(value: " = load ", comparisonType: StringComparison.Ordinal);
        if (loadIdx >= 0)
        {
            int typeStart = loadIdx + " = load ".Length;
            int comma = line.IndexOf(value: ',', startIndex: typeStart);
            if (comma > typeStart)
            {
                string llvmType = line[typeStart..comma]
                   .Trim();
                if (TbaaTagByLlvmType.TryGetValue(key: llvmType, value: out string? tag))
                {
                    return line + tag;
                }
            }

            return line;
        }

        // store: " store TYPE VALUE, ptr ..."
        if (t.StartsWith(value: "store ", comparisonType: StringComparison.Ordinal))
        {
            int storeStart =
                line.IndexOf(value: "store ", comparisonType: StringComparison.Ordinal) +
                "store ".Length;
            int space = line.IndexOf(value: ' ', startIndex: storeStart);
            if (space > storeStart)
            {
                string llvmType = line[storeStart..space]
                   .Trim();
                if (TbaaTagByLlvmType.TryGetValue(key: llvmType, value: out string? tag))
                {
                    return line + tag;
                }
            }
        }

        return line;
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Rebinds a semantically-resolved routine to the concrete owner/return type seen by codegen
    /// when the carried routine still points at a generic definition or partial resolution.
    /// </summary>
    private RoutineInfo? NormalizeResolvedRoutineReference(RoutineInfo? routine,
        TypeSymbol? receiverType, List<TypeSymbol> argTypes)
    {
        if (routine == null)
        {
            return null;
        }

        receiverType = NormalizeRoutineLookupType(type: receiverType);
        string lookupMemberRoutineName = GetMemberRoutineLookupName(routine: routine);

        bool ownerMismatch = receiverType != null &&
                             routine.OwnerType is { } ownerType and not ProtocolTypeSymbol &&
                             NormalizeRoutineLookupType(type: ownerType)
                               ?.FullName != receiverType.FullName;

        if (receiverType != null && (ownerMismatch ||
                                     routine.OwnerType is { IsGenericDefinition: true } ||
                                     routine.IsGenericDefinition ||
                                     RoutineHasUnresolvedTypeArguments(routine: routine)))
        {
            // Signature-only rebind by (name, argTypes); no name-only fallback.
            RoutineInfo? reboundMemberRoutine = _registry.LookupMemberRoutineOverload(
                type: receiverType,
                memberRoutineName: lookupMemberRoutineName,
                argTypes: argTypes);
            if (reboundMemberRoutine != null)
            {
                return reboundMemberRoutine;
            }
        }

        return routine;
    }

    /// <summary>
    /// Gets the member routine lookup name needed by this builder phase.
    /// </summary>
    private static string GetMemberRoutineLookupName(RoutineInfo routine)
    {
        // The bare memberRoutine name is already the structured RoutineInfo.Name; BaseName is
        // "Owner.name" (or "Module.name"), so its last dot-segment is exactly Name.
        return routine.Name;
    }

    /// <summary>
    /// Returns true when a carried routine still contains unresolved generic or error placeholders.
    /// </summary>
    private static bool RoutineHasUnresolvedTypeArguments(RoutineInfo routine)
    {
        if (routine.TypeArguments is { Count: > 0 } routineArgs &&
            HasUnresolvedParam(types: routineArgs))
        {
            return true;
        }

        TypeSymbol? owner = routine.OwnerType;
        return owner?.TypeArguments is { Count: > 0 } ownerArgs &&
               HasUnresolvedParam(types: ownerArgs);

        static bool HasUnresolvedParam(List<TypeSymbol> types)
        {
            foreach (TypeSymbol t in types)
            {
                if (t is GenericParameterTypeSymbol or ErrorTypeSymbol)
                {
                    return true;
                }

                if (t.TypeArguments is { Count: > 0 } inner && HasUnresolvedParam(types: inner))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Normalizes wrapper lookup types to their canonical registry-backed generic resolutions.
    /// </summary>
    private TypeSymbol? NormalizeRoutineLookupType(TypeSymbol? type)
    {
        return type;
    }
}
