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
        TypeSymbol type = ResolveCreatorType(creator: expr);

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
            // A variant or Check/Lookup carrier is a TaggedCreatorExpression (ConstructionLoweringPass).
            VariantTypeSymbol or RecordTypeSymbol { CarrierKind: CarrierKind.Result or CarrierKind.Lookup } =>
                throw new InvalidOperationException(
                    message: $"A creator of '{type.FullName}' reached the LLVM emitter in [{_currentRoutineDiagName}]."),
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
    /// Emits a <see cref="TaggedCreatorExpression"/>: a variant or Check/Lookup carrier, built through memory so
    /// the payload is stored at its own width into the <c>[N x i8]</c> buffer (field 1). The whole value is
    /// zeroed first: a payload narrower than the buffer would otherwise leave undefined bytes, which become
    /// poison once the value is copied and let the optimizer fold every later tag comparison away.
    /// <code>
    ///   %tmp = alloca %T
    ///   store %T zeroinitializer, ptr %tmp
    ///   store i64 %tag, ptr (field 0)
    ///   store &lt;payload type&gt; %payload, ptr (field 1)   ; when there is a payload
    ///   %result = load %T, ptr %tmp
    /// </code>
    /// Mirror of the reader in <see cref="EmitCarrierPayloadExpression"/>.
    /// </summary>
    private string EmitTaggedCreator(StringBuilder sb, TaggedCreatorExpression tagged)
    {
        TypeSymbol type = tagged.ResolvedType ??
                          throw new InvalidOperationException(
                              message: $"A tagged creator at {tagged.Location} has no type.");
        string llvmType = type is RecordTypeSymbol and not VariantTypeSymbol
            ? EnsureRecordTypeDeclared(record: (RecordTypeSymbol)type)
            : GetLlvmType(type: type);
        string slot = NextTemp();
        EmitLine(sb: sb, line: $"  {slot} = alloca {llvmType}");
        EmitLine(sb: sb, line: $"  store {llvmType} zeroinitializer, ptr {slot}");

        string tag = EmitExpression(sb: sb, expr: tagged.Tag);
        string tagPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {tagPtr} = getelementptr {llvmType}, ptr {slot}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  store i64 {tag}, ptr {tagPtr}");

        if (tagged.Payload is { } payload)
        {
            string value = EmitExpression(sb: sb, expr: payload);
            TypeSymbol payloadType = GetExpressionType(expr: payload) ??
                                     throw new InvalidOperationException(
                                         message: $"The payload of a tagged creator at {tagged.Location} has no type.");
            string payloadPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {payloadPtr} = getelementptr {llvmType}, ptr {slot}, i32 0, i32 1");
            EmitLine(sb: sb, line: $"  store {GetLlvmType(type: payloadType)} {value}, ptr {payloadPtr}");
        }

        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = load {llvmType}, ptr {slot}");
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
        entity = EntityHavingMemberVariable(entity: entity,
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
        // A backend-represented record has no fields to read.
        if (record.BackendType != null)
        {
            throw new InvalidOperationException(
                message: $"A read of '{memberVariableName}' on the backend-represented '{record.FullName}' reached the " +
                         $"LLVM emitter in [{_currentRoutineDiagName}].");
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
        int index = tuple.MemberVariables.FindIndex(match: m => m.Name == memberVariableName);
        if (index < 0)
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
        entity = EntityHavingMemberVariable(entity: entity,
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
        // inserted by ScopeTeardownLoweringPass. So this method just emits the raw store — the RC balance
        // lives in the AST, keeping codegen out of refcount business (and letting the cycle-collector
        // lock cover field-write releases through the single RoamController.unhold chokepoint).
        EmitLine(sb: sb, line: $"  store {memberVariableType} {value}, ptr {memberVariablePtr}");
    }

    /// <summary>
    /// The entity, checked to have the member variable an access names: monomorphization finalizes every
    /// concrete entity's member list before the backend runs.
    /// </summary>
    /// <param name="entity">The entity type accessed.</param>
    /// <param name="memberVariableName">The member variable name being accessed.</param>
    private EntityTypeSymbol EntityHavingMemberVariable(EntityTypeSymbol entity,
        string memberVariableName)
    {
        return entity.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName)
            ? entity
            : throw new InvalidOperationException(
                message: $"The entity '{entity.FullName}' reached the LLVM emitter without its member variable " +
                         $"'{memberVariableName}' in [{_currentRoutineDiagName}].");
    }
}
