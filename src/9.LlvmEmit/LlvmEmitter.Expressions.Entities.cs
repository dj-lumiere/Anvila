using System.Text;
using Builder.Desugaring.Passes;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.LlvmEmit;

/// <summary>
/// Expression code generation for entity construction and entity member operations.
/// </summary>
public partial class LlvmEmitter
{
    private string EmitEntityAllocation(StringBuilder sb, EntityTypeSymbol entity,
        List<string>? memberVariableValues = null)
    {
        string typeName = GetEntityTypeName(entity: entity);
        int size = entity.HeapBlockSize(pointerSize: _pointerSizeBytes);

        // Allocate memory
        // A future refactor (C41) should route through a typed allocator abstraction rather than calling rf_allocate_dynamic directly.
        string rawPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {rawPtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        // Initialize member variables
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            MemberVariableInfo memberVariable = entity.MemberVariables[index: i];
            string memberVariableType = GetLlvmType(type: memberVariable.Type);

            // Get member variable pointer using GEP
            string memberVariablePtr = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {memberVariablePtr} = getelementptr {typeName}, ptr {rawPtr}, i32 0, i32 {i}");

            // Get value to store
            string value;
            if (memberVariableValues != null && i < memberVariableValues.Count)
            {
                value = memberVariableValues[index: i];
            }
            else
            {
                value = GetZeroValue(type: memberVariable.Type);
            }

            // Store the value
            EmitLine(sb: sb,
                line: $"  store {memberVariableType} {value}, ptr {memberVariablePtr}");

            // Roamed[T] field: MOVE the argument's reference into the field — NO retain here. In Suflae,
            // SuflaeEntityLoweringPass.RetainConstructionArg has already turned every construction arg
            // into an OWNED rvalue (a `.roam()` copy of a borrowed handle, or a fresh promote like
            // `List().roam()`), so the field simply takes ownership of that one reference. Retaining
            // again would double-count: the borrow path leaves the `.roam()` temp unreleased, and the
            // fresh-promote path (an inlined single-use local, which TemporaryTeardownPass deliberately
            // does not free as a construction arg) leaks its promote — either way the controller count
            // never collapses to the cycle-internal state and cycle collection can't reap it.
        }

        return rawPtr;
    }

    /// <summary>
    /// Generates code for a constructor call expression.
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="expr">The constructor call expression.</param>
    /// <returns>The temporary variable holding the result.</returns>
    private string EmitConstructorCall(StringBuilder sb, CreatorExpression expr)
    {
        TypeSymbol? type = ResolveCreatorType(creator: expr);
        if (type == null)
        {
            throw new InvalidOperationException(
                message: $"Unknown type in constructor: {expr.TypeName}");
        }

        // A creator naming a `create` overload is that overload's call (ConstructionLoweringPass).
        if (expr.ResolvedCreatorRoutine != null)
        {
            throw new InvalidOperationException(
                message: $"A creator of '{type.Name}' naming a create overload reached the LLVM emitter in " +
                         $"[{_currentRoutineDiagName}].");
        }

        // Ordered most-derived-first: Variant/Crashable must precede their bases (Record/Entity),
        // since a base arm would otherwise capture them.
        return type switch
        {
            VariantTypeSymbol variant => EmitVariantConstruction(sb: sb,
                variant: variant,
                expr: expr),
            // Crashable types are entity-like (heap-allocated, ptr semantics).
            CrashableTypeSymbol crashable => EmitCrashableConstruction(sb: sb,
                crashable: crashable,
                expr: expr),
            EntityTypeSymbol entity => EmitEntityConstruction(sb: sb, entity: entity, expr: expr),
            RecordTypeSymbol record => EmitRecordConstruction(sb: sb, record: record, expr: expr),
            _ => throw new InvalidOperationException(
                message: $"Cannot construct type: {type.Category}")
        };
    }

    /// <summary>
    /// Emits construction of a variant value from the implicit auto-wrap rewrite
    /// (e.g. <c>var a: Number = 42_s64</c> becomes <c>Number(S64: 42_s64)</c> at AST level).
    /// The CreatorExpression carries one MemberVariable whose Name matches a variant member's
    /// type name (or "None"/"None" for the zero-tag). Emits:
    /// <code>
    ///   %tmp = alloca %Variant.X
    ///   %tag_ptr = getelementptr %Variant.X, ptr %tmp, i32 0, i32 0
    ///   store i64 &lt;FNV-1a(member.FullName)&gt;, ptr %tag_ptr
    ///   %pay_ptr = getelementptr %Variant.X, ptr %tmp, i32 0, i32 1
    ///   store &lt;val_ty&gt; %val, ptr %pay_ptr     ; skipped for the None/None arm
    ///   %result = load %Variant.X, ptr %tmp
    /// </code>
    /// </summary>
    private string EmitVariantConstruction(StringBuilder sb, VariantTypeSymbol variant,
        CreatorExpression expr)
    {
        if (expr.MemberVariables.Count != 1)
        {
            throw new InvalidOperationException(
                message:
                $"Variant '{variant.Name}' construction expects exactly one tagged value, got {expr.MemberVariables.Count}.");
        }

        (string memberName, Expression valueExpr) = expr.MemberVariables[index: 0];
        VariantMemberInfo? member =
            variant.Members.FirstOrDefault(predicate: m => m.Name == memberName);
        if (member == null)
        {
            throw new InvalidOperationException(
                message: $"Variant '{variant.Name}' has no member '{memberName}'.");
        }

        string variantLlvm = GetLlvmType(type: variant);
        string slot = NextTemp();
        EmitLine(sb: sb, line: $"  {slot} = alloca {variantLlvm}");
        // Zero the WHOLE variant before writing the tag + arm value. An arm whose payload is narrower than
        // the union's `[N x i8]` (e.g. an `S32` arm in `{ i64, [24 x i8] }`) would otherwise leave the trailing
        // payload bytes uninitialized. Those undef bytes become `poison` once the variant is loaded by value and
        // copied (into a Dict slot, passed to `represent`), and the optimizer then treats every `tag == <const>`
        // arm-dispatch comparison downstream as UB and folds the arm branches away — so `represent` silently
        // drops the arm's value (a fieldless `SerialValue()` instead of `SerialValue(5)`). A defined zero payload
        // keeps the value well-defined end-to-end.
        EmitLine(sb: sb, line: $"  store {variantLlvm} zeroinitializer, ptr {slot}");

        // type_id = FNV-1a(member.Type.FullName); 0 for None/None.
        ulong typeId = member.IsNone
            ? 0UL
            : TypeIdHelper.ComputeTypeId(fullName: member.Type!.FullName);
        string tagPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {tagPtr} = getelementptr {variantLlvm}, ptr {slot}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  store i64 {typeId}, ptr {tagPtr}");

        // None arm (or any zero-sized payload type) carries no Assignable value — only the
        // tag matters. Skip both value emission and the payload store. The user-level form
        // `None()` parses as a CreatorExpression but has nothing to construct; treating it
        // as a pure marker mirrors how the None type behaves elsewhere.
        bool isNoneArm = member.IsNone || member.Type is not null && (member.Type.Name == "None" ||
            member.Type.FullName.EndsWith(value: ".None"));
        if (!isNoneArm)
        {
            string val = EmitExpression(sb: sb, expr: valueExpr);
            string valLlvm = GetLlvmType(type: valueExpr.ResolvedType ?? member.Type!);
            string payPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {payPtr} = getelementptr {variantLlvm}, ptr {slot}, i32 0, i32 1");
            EmitLine(sb: sb, line: $"  store {valLlvm} {val}, ptr {payPtr}");
        }

        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = load {variantLlvm}, ptr {slot}");
        return result;
    }

    /// <summary>
    /// Generates code to construct an entity with member variable values.
    /// </summary>
    private string EmitEntityConstruction(StringBuilder sb, EntityTypeSymbol entity,
        CreatorExpression expr)
    {
        // An entity with member variables is built from one value per member variable: an empty creator
        // would skip the allocations its `create` makes.
        if (expr.MemberVariables.Count == 0 && entity.MemberVariables.Count > 0)
        {
            throw new InvalidOperationException(
                message: $"An empty creator of entity '{entity.Name}' reached the LLVM emitter in " +
                         $"[{_currentRoutineDiagName}]; it is built by its create routine.");
        }

        // Evaluate all member variable value expressions first
        var memberVariableValues = new List<string>();
        foreach ((string _, Expression fieldExpr) in expr.MemberVariables)
        {
            string value = EmitExpression(sb: sb, expr: fieldExpr);
            memberVariableValues.Add(item: value);
        }

        // Field initializers with `steal` transfer ownership from local entity vars into
        // the new entity. Drop the source locals from the cleanup set so the function-exit
        // rf_invalidate pass doesn't free the same allocation now held by the field. Roamed[T] fields
        // now MOVE too (EmitEntityAllocation no longer retains them): their arg is an owned rvalue
        // (`.roam()` / fresh promote), so consuming is a no-op for the rvalue shape and correctly drops
        // any bare source local — matching every other moved field.
        foreach ((string _, Expression fieldExpr) in expr.MemberVariables)
        {
            NullStampStolenLocal(sb: sb, expr: fieldExpr);
        }

        // Allocate and initialize
        return EmitEntityAllocation(sb: sb,
            entity: entity,
            memberVariableValues: memberVariableValues);
    }

    /// <summary>
    /// Generates code to construct a record (value type).
    /// </summary>
    private string EmitRecordConstruction(StringBuilder sb, RecordTypeSymbol record,
        CreatorExpression expr)
    {
        // A backend-represented record built from nothing is its zero value. Built from one value it is a
        // BackendCastExpression (ConstructionLoweringPass).
        if (record.BackendType != null)
        {
            return expr.MemberVariables.Count == 0
                ? GetZeroValue(type: record)
                : throw new InvalidOperationException(
                    message: $"A creator of backend-represented '{record.Name}' with values reached the LLVM " +
                             $"emitter in [{_currentRoutineDiagName}].");
        }

        // Result[T] / Lookup[T]: the payload is an inline byte buffer sized to max(sizeof(T), 8), and
        // the success `T` is stored inline at its FULL width. `insertvalue` cannot put a typed T into a
        // `[N x i8]` field, so build the carrier through memory: alloca, zero, then a typed store of the
        // payload into the buffer (writes sizeof(T) bytes — no truncation; an error is stored as its
        // 8-byte entity pointer). Maybe[T] keeps its `{present, T}` layout and the memberwise path.
        if (record.CarrierKind is CarrierKind.Result or CarrierKind.Lookup)
        {
            return EmitInlineCarrierConstruction(sb: sb, record: record, expr: expr);
        }

        // Multi-member-variable record: build the struct value. The CreatorExpression carries member
        // values POSITIONALLY (already field-ordered by the SA/lowering that produced it), so field i
        // takes MemberVariables[i] when present.
        return EmitMemberwiseRecordStruct(sb: sb,
            record: record,
            valueForField: (i, field) =>
            {
                if (i >= expr.MemberVariables.Count)
                {
                    return null;
                }

                return EmitExpression(sb: sb, expr: expr.MemberVariables[index: i].Value);
            });
    }

    /// <summary>
    /// Builds a Result[T]/Lookup[T] carrier through an alloca so the success payload can be stored at
    /// its full width into the inline <c>[N x i8]</c> buffer (field 1). Zero-inits, stores the
    /// <c>type_id</c> (field 0, i64), and — when a payload member is present — typed-stores it into the
    /// buffer (an entity/crashable error stores its <c>ptr</c>; a value type stores its own LLVM type).
    /// Mirror of the reader in <see cref="EmitCarrierPayloadExpression"/>.
    /// </summary>
    private string EmitInlineCarrierConstruction(StringBuilder sb, RecordTypeSymbol record,
        CreatorExpression expr)
    {
        string carrier = EnsureRecordTypeDeclared(record: record);
        string slot = NextTemp();
        EmitLine(sb: sb, line: $"  {slot} = alloca {carrier}");
        EmitLine(sb: sb, line: $"  store {carrier} zeroinitializer, ptr {slot}");

        for (int i = 0; i < expr.MemberVariables.Count && i < record.MemberVariables.Count; i++)
        {
            MemberVariableInfo field = record.MemberVariables[index: i];
            Expression valueExpr = expr.MemberVariables[index: i].Value;
            string value = EmitExpression(sb: sb, expr: valueExpr);

            string fieldPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldPtr} = getelementptr {carrier}, ptr {slot}, i32 0, i32 {i}");

            // The payload field is the byte buffer: store the value at its OWN width (full T, or an
            // 8-byte entity pointer for an error). type_id and any other field use their storage type.
            string storeType;
            if (field.Name == "payload")
            {
                TypeSymbol? payloadType = GetExpressionType(expr: valueExpr);
                if (payloadType is EntityTypeSymbol or CrashableTypeSymbol)
                {
                    storeType = "ptr";
                }
                else if (payloadType != null)
                {
                    storeType = GetLlvmType(type: payloadType);
                }
                else
                {
                    storeType = "i64";
                }
            }
            else
            {
                value = CoerceBoolToStorage(sb: sb, value: value, fieldType: field.Type);
                storeType = GetFieldStorageLlvmType(type: field.Type);
            }

            EmitLine(sb: sb, line: $"  store {storeType} {value}, ptr {fieldPtr}");
        }

        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {carrier}, ptr {slot}");
        return loaded;
    }

    /// <summary>
    /// The single memberwise record struct-builder shared by both construction overloads (the
    /// <see cref="CreatorExpression"/> form and the positional/named <c>List&lt;Expression&gt;</c> form).
    /// Walks the record's declared fields in layout order, emits each via
    /// <paramref name="valueForField"/> (returning <c>null</c> leaves the field at its
    /// zeroinitializer value), Bool-coerces to storage width, and chains <c>insertvalue</c>s.
    ///
    /// <para>ACCEPTED BOUNDARY (Track D1): construction is still built inline in codegen rather than
    /// dispatched to a synthesized memberwise <c>create</c> routine. A full synthesis-pass move was
    /// deferred because it is refcount-parity-critical: entity construction interleaves heap alloc,
    /// per-field ownership CONSUMPTION (<c>ConsumeTransferredCallOwnership</c> / <c>…LocalOwnership</c>),
    /// Roamed-field moves, and the retain bumps <c>RcRetainLoweringPass</c> inserts keyed off these exact
    /// construction sites — plus variant/collection special construction. This helper only dedups the
    /// value-record struct build (no ownership semantics), which is safe to unify.</para>
    /// </summary>
    private string EmitMemberwiseRecordStruct(StringBuilder sb, RecordTypeSymbol record,
        Func<int, MemberVariableInfo, string?> valueForField)
    {
        string typeName = EnsureRecordTypeDeclared(record: record);
        string result = "zeroinitializer";
        for (int i = 0; i < record.MemberVariables.Count; i++)
        {
            MemberVariableInfo field = record.MemberVariables[index: i];
            string? value = valueForField(arg1: i, arg2: field);
            if (value == null)
            {
                continue;
            }

            // Bool fields are stored as i8 in the aggregate — zext the i1 value to its storage form.
            value = CoerceBoolToStorage(sb: sb, value: value, fieldType: field.Type);
            string memberVariableType = GetFieldStorageLlvmType(type: field.Type);

            string newResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {newResult} = insertvalue {typeName} {result}, {memberVariableType} {value}, {i}");
            result = newResult;
        }

        return result;
    }

    /// <summary>
    /// Emits crashable type construction: heap-allocate and initialize fields. Mirrors entity construction —
    /// crashable types have entity (ptr) semantics. The creator carries one value per member variable in
    /// declaration order.
    /// </summary>
    private string EmitCrashableConstruction(StringBuilder sb, CrashableTypeSymbol crashable,
        CreatorExpression expr)
    {
        string typeName = GetCrashableTypeName(crashable: crashable);
        string sizeTemp = NextTemp();
        EmitLine(sb: sb, line: $"  {sizeTemp} = getelementptr {typeName}, ptr null, i32 1");
        string size = NextTemp();
        EmitLine(sb: sb, line: $"  {size} = ptrtoint ptr {sizeTemp} to i64");
        string crashablePtr = NextTemp();
        EmitLine(sb: sb, line: $"  {crashablePtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        for (int i = 0; i < expr.MemberVariables.Count && i < crashable.MemberVariables.Count; i++)
        {
            string value = EmitExpression(sb: sb, expr: expr.MemberVariables[index: i].Value);
            string fieldType = GetLlvmType(type: crashable.MemberVariables[index: i].Type);
            string fieldPtr = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {fieldPtr} = getelementptr {typeName}, ptr {crashablePtr}, i32 0, i32 {i}");
            EmitLine(sb: sb, line: $"  store {fieldType} {value}, ptr {fieldPtr}");
        }

        return crashablePtr;
    }

    /// <summary>
    /// Generates code to read a member variable from an entity/record.
    /// For entities: GEP + load
    /// For records: extractvalue
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="expr">The member access expression.</param>
    /// <returns>The temporary variable holding the member variable value.</returns>
    private string EmitMemberVariableAccess(StringBuilder sb, MemberExpression expr)
    {
        string memberName = expr.MemberName;

        // Evaluate the target expression
        string target = EmitExpression(sb: sb, expr: expr.Object);

        // Get the target type
        TypeSymbol? targetType = GetExpressionType(expr: expr.Object);
        if (targetType == null)
        {
            throw new InvalidOperationException(
                message: "Cannot determine type of member variable access target");
        }

        targetType = MarkerProtocolInner(type: targetType) ?? targetType;

        // A marker borrow protocol receiver (Accessing[X]/Controlling[X]) is transparent to its inner X.

        // A field read through an entity wrapper arrives with its object projected to the entity
        // (WrapperProjectionLoweringPass), so a wrapper-of-entity object here is an upstream gap.
        if (targetType is RecordTypeSymbol unprojected &&
            GetGenericBaseName(type: unprojected) is { } unprojectedBase &&
            WrapperTypeNames.Contains(item: unprojectedBase) &&
            unprojected.TypeArguments is [EntityTypeSymbol, ..] &&
            !unprojected.MemberVariables.Any(predicate: mv => mv.Name == memberName))
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberName}' is read through '{unprojected.Name}' without a wrapper projection, in routine: {_currentEmittingRoutine?.RegistryKey ?? "<unknown>"}");
        }

        // Most-derived-first: Crashable (an Entity) and Variant (a Record) precede their bases.
        return targetType switch
        {
            CrashableTypeSymbol crashable => EmitCrashableMemberVariableRead(sb: sb,
                crashablePtr: target,
                crashable: crashable,
                memberVariableName: memberName),
            EntityTypeSymbol entity => EmitEntityMemberVariableRead(sb: sb,
                entityPtr: target,
                entity: entity,
                memberVariableName: memberName),
            TupleTypeSymbol tuple => EmitTupleMemberVariableRead(sb: sb,
                tupleValue: target,
                tuple: tuple,
                memberVariableName: memberName),
            // Synthetic type_id access generated by PatternLoweringPass for variant subjects.
            VariantTypeSymbol variant when memberName == "type_id" => EmitVariantTagAccess(sb: sb,
                variantValue: target,
                variant: variant),
            RecordTypeSymbol record => EmitRecordMemberVariableRead(sb: sb,
                recordValue: target,
                record: record,
                memberVariableName: memberName),
            _ => throw new InvalidOperationException(
                message:
                $"Cannot access member variable '{memberName}' on type: {targetType.Name} (category: {targetType.Category}), in routine: {_currentEmittingRoutine?.RegistryKey ?? "<unknown>"}")
        };
    }

    /// <summary>
    /// Reads a field of a record through its storage address (GEP + load).
    /// </summary>
    private string EmitRecordFieldReadAtAddress(StringBuilder sb, string recordAddress,
        RecordTypeSymbol record, string memberName)
    {
        int fieldIndex = IndexOfMemberVariable(memberVariables: record.MemberVariables, name: memberName);
        if (fieldIndex < 0)
        {
            throw new InvalidOperationException(
                message: $"Member variable '{memberName}' not found on record '{record.Name}'");
        }

        string recordTypeName = EnsureRecordTypeDeclared(record: record);
        string fieldPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {fieldPtr} = getelementptr {recordTypeName}, ptr {recordAddress}, i32 0, i32 {fieldIndex}");
        string loaded = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {loaded} = load {GetLlvmType(type: record.MemberVariables[index: fieldIndex].Type)}, ptr {fieldPtr}");
        return loaded;
    }

    /// <summary>
    /// Emits a <see cref="WrapperProjectionExpression"/>: the entity pointer behind a wrapper, read from the
    /// controller's <c>data</c> field, taken as the wrapper pointer itself, or extracted from the struct
    /// wrapper's <c>Hijacked[T]</c> field, or the record value loaded from a record wrapper's pointer, as the
    /// projection is stamped. A record field access goes through the address instead
    /// (<see cref="EmitLvalueAddress"/>), so this load serves only a use of the whole record.
    /// </summary>
    private string EmitWrapperProjection(StringBuilder sb, WrapperProjectionExpression projection)
    {
        string wrapper = EmitExpression(sb: sb, expr: projection.Wrapper);
        switch (projection.Kind)
        {
            case WrapperProjectionKind.ControllerData:
                return EmitEntityMemberVariableRead(sb: sb,
                    entityPtr: wrapper,
                    entity: projection.Controller ??
                            throw new InvalidOperationException(
                                message: "A controller-data wrapper projection carries no controller type."),
                    memberVariableName: Declaration.RuntimeContract.ControllerData);
            case WrapperProjectionKind.Direct:
                return wrapper;
            case WrapperProjectionKind.RecordAddress:
                string recordValue = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {recordValue} = load {GetLlvmType(type: projection.ResolvedType!)}, ptr {wrapper}");
                return recordValue;
            default:
                string recordTypeName = EnsureRecordTypeDeclared(
                    record: (RecordTypeSymbol)projection.Wrapper.ResolvedType!);
                string entityPtr = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {entityPtr} = extractvalue {recordTypeName} {wrapper}, {projection.FieldIndex}");
                return entityPtr;
        }
    }

    /// <summary>
    /// Generates code to read a member variable from an entity (pointer type).
    /// Uses GEP to get member variable address, then load.
    /// </summary>
    private string EmitEntityMemberVariableRead(StringBuilder sb, string entityPtr,
        EntityTypeSymbol entity, string memberVariableName)
    {
        // Refresh stale generic resolutions (member variables may be empty or missing the target member)
        entity = RefreshEntityMemberVariables(entity: entity,
            memberVariableName: memberVariableName);

        // Ensure entity type struct definition exists in LLVM IR
        GenerateEntityType(entity: entity);

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            if (entity.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = entity.MemberVariables[index: i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            string memberList = string.Join(separator: ", ",
                values: entity.MemberVariables.Select(selector: mv => mv.Name));
            string genDefName = entity.GenericDefinition?.FullName ?? "(null)";
            string genDefMembers = entity.GenericDefinition != null
                ? string.Join(separator: ", ",
                    values: entity.GenericDefinition.MemberVariables.Select(
                        selector: mv => mv.Name))
                : "(null)";
            string typeArgNames = entity.TypeArguments != null
                ? string.Join(separator: ", ",
                    values: entity.TypeArguments.Select(selector: t => t.FullName))
                : "(null)";
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on entity '{entity.FullName}' (members: [{memberList}], GenericDef={genDefName}, GenericDefMembers=[{genDefMembers}], TypeArgs=[{typeArgNames}])");
        }

        string typeName = GetEntityTypeName(entity: entity);
        string memberVariableType = GetLlvmType(type: memberVariable.Type);

        // GEP to get member variable pointer
        string memberVariablePtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {memberVariablePtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {memberVariableIndex}");

        // Load the member variable value
        string value = NextTemp();
        EmitLine(sb: sb, line: $"  {value} = load {memberVariableType}, ptr {memberVariablePtr}");

        return value;
    }

    /// <summary>
    /// Generates code to read a member variable from a crashable type (heap-allocated, pointer).
    /// Uses GEP + load, same structural pattern as entities.
    /// </summary>
    private string EmitCrashableMemberVariableRead(StringBuilder sb, string crashablePtr,
        CrashableTypeSymbol crashable, string memberVariableName)
    {
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < crashable.MemberVariables.Count; i++)
        {
            if (crashable.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = crashable.MemberVariables[index: i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on crashable '{crashable.Name}'");
        }

        string typeName = GetCrashableTypeName(crashable: crashable);
        string memberVariableType = GetLlvmType(type: memberVariable.Type);

        string memberVariablePtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {memberVariablePtr} = getelementptr {typeName}, ptr {crashablePtr}, i32 0, i32 {memberVariableIndex}");

        string value = NextTemp();
        EmitLine(sb: sb, line: $"  {value} = load {memberVariableType}, ptr {memberVariablePtr}");

        return value;
    }

    /// <summary>
    /// Generates code to read a member variable from a record (value type).
    /// Uses extractvalue instruction.
    /// </summary>
    private string EmitRecordMemberVariableRead(StringBuilder sb, string recordValue,
        RecordTypeSymbol record, string memberVariableName)
    {
        // Hijacked[T] (@llvm("ptr")): .address -> ptrtoint ptr to i64
        if (record is { BackendType: not null, LlvmType: "ptr" } &&
            memberVariableName == "address")
        {
            string addr = NextTemp();
            EmitLine(sb: sb, line: $"  {addr} = ptrtoint ptr {recordValue} to i64");
            return addr;
        }

        // Backend-annotated or single-member-variable wrapper: the value IS the field
        if (record.BackendType != null)
        {
            return recordValue;
        }

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < record.MemberVariables.Count; i++)
        {
            if (record.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = record.MemberVariables[index: i];
                break;
            }
        }

        // Fallback: stale generic-instance resolutions (e.g. Maybe[Bool] cached from the
        // pre-registered carrier shell before Maybe's source body was resolved) may have empty
        // MemberVariables. Refresh from the GenericDefinition and retry.
        if (memberVariableIndex < 0 && record.GenericDefinition is RecordTypeSymbol gdef &&
            record.TypeArguments != null && gdef.MemberVariables.Count > 0)
        {
            var fresh = (RecordTypeSymbol)gdef.CreateInstance(typeArguments: record.TypeArguments);
            record.MemberVariables = fresh.MemberVariables;
            for (int i = 0; i < record.MemberVariables.Count; i++)
            {
                if (record.MemberVariables[index: i].Name == memberVariableName)
                {
                    memberVariableIndex = i;
                    memberVariable = record.MemberVariables[index: i];
                    break;
                }
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on record '{record.FullName}'");
        }

        string typeName = EnsureRecordTypeDeclared(record: record);

        // A Bool field is stored as i8 in the aggregate — trunc back to the i1 register form.
        string value = NextTemp();
        EmitLine(sb: sb,
            line: $"  {value} = extractvalue {typeName} {recordValue}, {memberVariableIndex}");
        value = CoerceStorageToBool(sb: sb, storageValue: value, fieldType: memberVariable.Type);

        return value;
    }


    /// <summary>
    /// Extracts the i64 type_id tag (field 0) from a variant struct value via <c>extractvalue</c>.
    /// Generated by <see cref="Builder.Lowering.Passes.PatternLoweringPass"/> for variant <c>TypePattern</c> conditions.
    /// </summary>
    private string EmitVariantTagAccess(StringBuilder sb, string variantValue,
        VariantTypeSymbol variant)
    {
        string typeName = GetVariantTypeName(variant: variant);
        string tag = NextTemp();
        EmitLine(sb: sb, line: $"  {tag} = extractvalue {typeName} {variantValue}, 0");
        return tag;
    }

    /// <summary>
    /// Generates code to read a field from a tuple value (value type — uses extractvalue).
    /// </summary>
    private string EmitTupleMemberVariableRead(StringBuilder sb, string tupleValue,
        TupleTypeSymbol tuple, string memberVariableName)
    {
        // Field names are item0, item1, ... — parse the index directly
        if (!memberVariableName.StartsWith(value: "item",
                comparisonType: StringComparison.Ordinal) ||
            !int.TryParse(s: memberVariableName.AsSpan(start: 4), result: out int index) ||
            index < 0 || index >= tuple.ElementTypes.Count)
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on tuple '{tuple.Name}'");
        }

        string tupleTypeName = GetLlvmType(type: tuple);
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = extractvalue {tupleTypeName} {tupleValue}, {index}");
        // A Bool element is stored as i8 in the aggregate — trunc back to the i1 register form.
        result = CoerceStorageToBool(sb: sb,
            storageValue: result,
            fieldType: tuple.ElementTypes[index: index]);
        return result;
    }

    /// <summary>
    /// Generates code to write a member variable on an entity.
    /// </summary>
    private void EmitEntityMemberVariableWrite(StringBuilder sb, string entityPtr,
        EntityTypeSymbol entity, string memberVariableName, string value,
        TypeSymbol? valueType = null)
    {
        // Refresh stale generic resolutions
        entity = RefreshEntityMemberVariables(entity: entity,
            memberVariableName: memberVariableName);

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            if (entity.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = entity.MemberVariables[index: i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on entity '{entity.Name}'");
        }

        string typeName = GetEntityTypeName(entity: entity);
        string memberVariableType = GetLlvmType(type: memberVariable.Type);

        // Maybe auto-wrap (bare `T` -> `Maybe[T]` on a nullable member store) is now an AST rewrite:
        // ExpressionLoweringPass.TryWrapMemberMaybe boxes the value into a Maybe CreatorExpression
        // before codegen, so the { i1, T } aggregate is built through the normal record-creator path
        // rather than hand-emitted here (D3).
        _ = valueType;

        // GEP to get member variable pointer
        string memberVariablePtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {memberVariablePtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {memberVariableIndex}");

        // Roamed[T] field reassignment uses COPY semantics (biased RC — aliasing is free): the field
        // must drop its old strong ref and take a fresh one on the new value, or the count is off by
        // one and teardown double-frees. BOTH sides are now explicit AST calls, NOT codegen emits:
        // the retain-new `.share()` on the RHS, and the release-old (snapshot-then-`.destroy()`)
        // inserted by RcRetainLoweringPass. So this method just emits the raw store — the RC balance
        // lives in the AST, keeping codegen out of refcount business (and letting the cycle-collector
        // lock cover field-write releases through the single RoamController.unhold chokepoint).
        EmitLine(sb: sb, line: $"  store {memberVariableType} {value}, ptr {memberVariablePtr}");
    }

    /// <summary>
    /// Refreshes entity member variables for resolved generic types that may have stale or empty members.
    /// Tries the generic definition first, then falls back to registry lookup.
    /// </summary>
    /// <param name="entity">The entity type to refresh.</param>
    /// <param name="memberVariableName">The member variable name being probed.</param>
    private EntityTypeSymbol RefreshEntityMemberVariables(EntityTypeSymbol entity,
        string memberVariableName)
    {
        if (entity.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName))
        {
            return entity;
        }

        // Non-generic entities can also be observed before pass 1c repopulates their member list.
        // Structural re-lookup / re-instantiation only — no AST rebuild or name-based type re-resolution.
        TypeSymbol? directLookup = _registry.LookupType(name: entity.FullName) ??
                                 LookupTypeInCurrentModule(name: entity.FullName) ??
                                 _registry.LookupType(name: entity.Name) ??
                                 LookupTypeInCurrentModule(name: entity.Name);
        if (directLookup is EntityTypeSymbol directEntity &&
            directEntity.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName))
        {
            return directEntity;
        }

        if (!entity.IsGenericResolution || entity.TypeArguments == null)
        {
            return entity;
        }

        // Try GenericDefinition if available
        if (entity.GenericDefinition is { MemberVariables.Count: > 0 } genDef &&
            TryReinstantiateEntity(genericDef: genDef,
                typeArguments: entity.TypeArguments,
                memberVariableName: memberVariableName,
                refreshed: out EntityTypeSymbol? fromGenDef))
        {
            return fromGenDef!;
        }

        // Fallback: look up the generic definition from the registry
        string baseName = GetGenericBaseName(type: entity) ?? entity.Name;
        var lookupDef = LookupTypeInCurrentModule(name: baseName) as EntityTypeSymbol;
        if (lookupDef is { IsGenericDefinition: true, MemberVariables.Count: > 0 } &&
            TryReinstantiateEntity(genericDef: lookupDef,
                typeArguments: entity.TypeArguments,
                memberVariableName: memberVariableName,
                refreshed: out EntityTypeSymbol? fromLookup))
        {
            return fromLookup!;
        }

        return entity;
    }

    /// <summary>
    /// Re-instantiates <paramref name="genericDef"/> with the given type arguments and returns the
    /// fresh resolution when it carries the requested member variable. Returns false otherwise.
    /// </summary>
    private static bool TryReinstantiateEntity(EntityTypeSymbol genericDef,
        List<TypeSymbol> typeArguments, string memberVariableName, out EntityTypeSymbol? refreshed)
    {
        refreshed = genericDef.CreateInstance(typeArguments: typeArguments) as EntityTypeSymbol;
        return refreshed != null &&
               refreshed.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName);
    }
}
