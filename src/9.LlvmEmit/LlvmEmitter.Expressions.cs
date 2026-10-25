using Builder.Diagnostics;
using System.Text;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.LlvmEmit;

/// <summary>
/// Expression code generation: allocation, member variable access, memberRoutine calls, operators.
/// </summary>
public partial class LlvmEmitter
{
    private const string FloatTypeName = "float";
    private const string DoubleTypeName = "double";
    private const string Fp128TypeName = "fp128";

    /// <summary>
    /// Emits code for any expression node.
    /// </summary>
    /// <param name="sb">The builder receiving emitted LLVM IR.</param>
    /// <param name="expr">The expression to emit.</param>
    /// <returns>The temporary value name produced for the expression.</returns>
    private string EmitExpression(StringBuilder sb, Expression expr)
    {
        SourceLocation? savedLoc = PushDebugLoc(sb: sb, loc: expr.Location);
        try
        {
            return expr switch
            {
                // Fundamental leaves: a literal value and a name reference.
                LiteralExpression literal => EmitLiteral(sb: sb, literal: literal),
                IdentifierExpression identifier => EmitIdentifier(sb: sb, identifier: identifier),
                // Field access (GEP + load) — fundamental IR, distinct from a member-routine call.
                MemberExpression memberAccess => EmitMemberVariableAccess(sb: sb,
                    expr: memberAccess),
                CreatorExpression constructor => EmitConstructorCall(sb: sb, expr: constructor),
                CallExpression call => EmitCallKeepingFailure(sb: sb, call: call),
                // Only the fundamental operator arms survive to codegen (Assign / Is / IdentityEqual /
                // Steal / flags-bitnot); every wired/comparison/membership/logical op is lowered upstream.
                BinaryExpression binary => EmitBinaryOp(sb: sb, binary: binary),
                UnaryExpression unary => EmitUnaryOp(sb: sb, unary: unary),
                GenericMemberRoutineCallExpression gmc => EmitGmceFallback(sb: sb, gmc: gmc),
                // Array[T,N] and BitArray[N] are inline IR constructs (insertvalue); all other
                // collection literals must be lowered to CreatorExpression + add calls before codegen.
                ListLiteralExpression list => EmitListLiteral(sb: sb, list: list),
                BitPackExpression pack => EmitBitArrayRuntime(sb: sb,
                    resolvedType: pack.ResolvedType!,
                    arguments: pack.Bits),
                // CarrierPayloadExpression is the RESULT of PatternLoweringPass (Maybe/Result payload
                // projection) — codegen consumes it, it is not a surface node.
                VariantWidenExpression widen => EmitVariantWiden(sb: sb, widen: widen),
                CarrierPayloadExpression payload => EmitCarrierPayloadExpression(sb: sb,
                    payload: payload),
                CrashableDispatchExpression dispatch => EmitCrashableDispatchExpression(sb: sb,
                    dispatch: dispatch),
                WrapperProjectionExpression projection => EmitWrapperProjection(sb: sb,
                    projection: projection),
                BackendCastExpression cast => EmitBackendCast(sb: sb, cast: cast),
                AddressOfExpression address => EmitLvalueAddress(sb: sb, expr: address.Target),
                TaggedCreatorExpression tagged => EmitTaggedCreator(sb: sb, tagged: tagged),
                ConstantDataExpression data => EmitConstantData(data: data),
                ZeroValueExpression { ResolvedType: { } zeroType } => GetZeroValue(type: zeroType),
                NativeRoutineExpression { Routine: IdentifierExpression { ResolvedRoutine: { } nativeRoutine } } =>
                    EmitNativeRoutineAddress(routine: nativeRoutine),
                NativeCallbackExpression callback => EmitForeignRoutineValueArg(sb: sb, valueExpr: callback.Value),
                TagOfExpression { Value: { ResolvedType: VariantTypeSymbol tagged } value } =>
                    EmitVariantTagAccess(sb: sb, variantValue: EmitExpression(sb: sb, expr: value), variant: tagged),
                EntityAllocationExpression { ResolvedType: EntityTypeSymbol entity } =>
                    EmitEntityAllocation(sb: sb, entity: entity),
                ClosureValueExpression closure => EmitClosureValueExpression(sb: sb, closure: closure),
                // Named arguments appear inside synthesized AST bodies (e.g., me.eq(you: you)).
                // The name is irrelevant to codegen -> just emit the inner value positionally.
                NamedArgumentExpression named => EmitExpression(sb: sb, expr: named.Value),
                // A move inside a synthesized body (a forwarder handing on an entity it took over) is the value
                // itself. Who ends the value was settled upstream, where the move kept it out of the teardown.
                StealExpression steal => EmitExpression(sb: sb, expr: steal.Operand),
                _ => throw new NotImplementedException(
                    message: $"Expression type not implemented: {expr.GetType().Name}")
            };
        }
        finally
        {
            PopDebugLoc(sb: sb, prev: savedLoc);
        }
    }

    /// <summary>
    /// Emits a <see cref="ClosureValueExpression"/>: the fat Routine value <c>{ ptr fn, ptr bound }</c> pairing
    /// the lifted routine's symbol with the bound payload the expression builds. Indirect calls pass
    /// <c>bound</c> as the trailing argument when it is not null. See [[cabi-callback-ffi]].
    /// </summary>
    private string EmitClosureValueExpression(StringBuilder sb, ClosureValueExpression closure)
    {
        RoutineInfo lifted = closure.Function.ResolvedRoutine ??
                             throw new InvalidOperationException(
                                 message: "A closure value carries no lifted routine.");
        GenerateRoutineDeclaration(routine: lifted);
        string bound = EmitExpression(sb: sb, expr: closure.Bound);
        string t0 = NextTemp();
        EmitLine(sb: sb,
            line: $"  {t0} = insertvalue {RoutineValueLlvmType} undef, ptr @{MangleRoutineName(routine: lifted)}, 0");
        string t1 = NextTemp();
        EmitLine(sb: sb, line: $"  {t1} = insertvalue {RoutineValueLlvmType} {t0}, ptr {bound}, 1");
        return InsertRecoverEntry(sb: sb, partial: t1, fnSymbol: $"@{MangleRoutineName(routine: lifted)}",
            recover: closure.Function.RecoverRoutine);
    }

    /// <summary>Notes the recovering entry of the routine a value holds (its lookup-shaped recovery variant), which
    /// the module's recovery table (<see cref="RecoverEntryTable"/>) maps the routine's address to. A routine value is
    /// two words, so a record holding one still lays out as C's (callback, userdata) pair.</summary>
    private string InsertRecoverEntry(StringBuilder sb, string partial, string fnSymbol, RoutineInfo? recover)
    {
        if (recover != null)
        {
            GenerateRoutineDeclaration(routine: recover);
            _recoverEntries[key: fnSymbol] = $"@{MangleRoutineName(routine: recover)}";
        }

        return partial;
    }

    /// <summary>
    /// Materializes a plain (non-lambda) routine reference as a captureless heap closure
    /// <c>{ fn_ptr }</c> whose function slot holds a closure-ABI adapter thunk. This lets a bare
    /// routine name flow through the same indirect-call path as a lambda value.
    /// </summary>
    private string EmitRoutineValueClosure(StringBuilder sb, RoutineInfo routine, RoutineInfo? recover)
    {
        // v0.4.1: a plain (non-lambda) routine taken as a VALUE is CAPTURELESS — the fat Routine value
        // is `{ @sym, null }` where @sym is the callee's bare C-ABI symbol and bound is null. No heap
        // box, no adapter thunk: the fn field IS a raw C function pointer (drops straight into a C
        // callback slot / struct field), and a captureless value never leaks. See [[cabi-callback-ffi]].
        // (The demand collector discovers a routine used as a VALUE via the same IdentifierExpression the
        // walk visits, so its body is materialized upstream — codegen no longer tracks references itself.)
        // The declare is still needed: in a resident-JIT delta the body lives in the base dylib.
        GenerateRoutineDeclaration(routine: routine);
        string sym = $"@{MangleRoutineName(routine: routine)}";
        string t0 = NextTemp();
        EmitLine(sb: sb, line: $"  {t0} = insertvalue {RoutineValueLlvmType} undef, ptr {sym}, 0");
        string t1 = NextTemp();
        EmitLine(sb: sb, line: $"  {t1} = insertvalue {RoutineValueLlvmType} {t0}, ptr null, 1");
        return InsertRecoverEntry(sb: sb, partial: t1, fnSymbol: sym, recover: recover);
    }

    /// <summary>
    /// Generates a variable reference.
    /// </summary>
    private string EmitIdentifier(StringBuilder sb, IdentifierExpression identifier)
    {
        // Const generic value: the monomorphizer baked the numeric value onto the identifier's
        // ResolvedType (ConstGenericValueTypeSymbol) — read it off the node, not a codegen-time map.
        if (identifier.ResolvedType is ConstGenericValueTypeSymbol constVal)
        {
            return constVal.Value.ToString();
        }

        // Aggregate (Array[T,N]) presets are not inlined — they live in a shared `@preset.*`
        // constant global. A value-position read loads the whole array from the global.
        if (!_localVariables.ContainsKey(key: identifier.Name) &&
            ResolveAggregatePreset(name: identifier.Name) is { } aggregatePreset)
        {
            string presetSym = EmitOrGetPresetGlobal(preset: aggregatePreset);
            string arrLlvm = GetLlvmType(type: aggregatePreset.Type);
            string loaded = NextTemp();
            EmitLine(sb: sb, line: $"  {loaded} = load {arrLlvm}, ptr {presetSym}");
            return loaded;
        }

        // Scalar presets must be inlined before backend entry. Codegen should not read declaration
        // AST to recover their values on demand.
        // A local of the routine shadows a preset of the same name (`each letter in word` beside a preset
        // `letter`).
        if (!_localVariables.ContainsKey(key: identifier.Name) &&
            _registry.LookupVariable(name: identifier.Name) is { IsPreset: true })
        {
            throw new InvalidOperationException(
                message:
                $"Preset identifier '{identifier.Name}' reached LLVM codegen. PresetInliningPass must inline presets before backend entry.");
        }

        // A pre-resolved routine-VALUE reference (set by a lowering pass, e.g. an unbound member-
        // routine hook). The routine is already known, so skip name-based lookup — the bare name may
        // be a memberRoutine that lookup cannot resolve without the owner type. Falls through the same
        // closure-materialization path (memberRoutines are handled via the member-aware closure path). The
        // node keeps the surrounding-context type (e.g. Addr for a hook field), so we gate on the
        // resolved routine alone rather than its ResolvedType label.
        if (identifier.ResolvedRoutine is { } preResolved)
        {
            return EmitPreResolvedRoutineValue(sb: sb, preResolved: preResolved, recover: identifier.RecoverRoutine);
        }

        // Look up the variable in local variables first
        if (!_localVariables.TryGetValue(key: identifier.Name, value: out TypeSymbol? varType))
        {
            // Suflae module-level `global`: load from its `@global` symbol.
            if (TryGetModuleGlobal(name: identifier.Name, variable: identifier.ResolvedVariable,
                    slot: out (TypeSymbol Type, string Symbol) gslot))
            {
                string gLlvmType = GetLlvmType(type: gslot.Type);
                string gTmp = NextTemp();
                EmitLine(sb: sb, line: $"  {gTmp} = load {gLlvmType}, ptr {gslot.Symbol}");
                return gTmp;
            }

            throw new InvalidOperationException(
                message:
                $"Unknown identifier '{identifier.Name}' in routine [{_currentEmittingRoutine?.OwnerType?.FullName ?? _currentEmittingRoutine?.Module}.{_currentEmittingRoutine?.Name}]");
        }

        // Variables are stored in allocas (%name.addr), need to load them
        // Use unique LLVM name to handle shadowing
        string llvmName =
            _localVarLlvmNames.TryGetValue(key: identifier.Name, value: out string? unique)
                ? unique
                : identifier.Name;
        // A None local is the empty record `{}` in its slot (see GetValueLlvmType), never `void`.
        string llvmType = GetValueLlvmType(type: varType);
        string tmp = NextTemp();
        EmitLine(sb: sb, line: $"  {tmp} = load {llvmType}, ptr %{llvmName}.addr");

        // Runtime use-after-steal net, written by StealGuardLoweringPass: a moved-out slot holds null, and
        // a null load makes the guard's crash_report call instead of handing out a stale pointer.
        if (identifier.StealGuardCrash is { } crash)
        {
            string isNull = NextTemp();
            EmitLine(sb: sb, line: $"  {isNull} = icmp eq ptr {tmp}, null");
            string crashLabel = NextLabel(prefix: "uas.crash");
            string okLabel = NextLabel(prefix: "uas.ok");
            EmitLine(sb: sb, line: $"  br i1 {isNull}, label %{crashLabel}, label %{okLabel}");
            EmitLine(sb: sb, line: $"{crashLabel}:");
            EmitExpression(sb: sb, expr: crash);
            EmitLine(sb: sb, line: "  unreachable");
            EmitLine(sb: sb, line: $"{okLabel}:");
        }

        return tmp;
    }

    /// <summary>
    /// Materializes a value for an identifier whose routine was pre-resolved by a lowering pass: the
    /// closure-materialization path (lambda vs plain routine).
    /// </summary>
    private string EmitPreResolvedRoutineValue(StringBuilder sb, RoutineInfo preResolved, RoutineInfo? recover)
    {
        // A captureless lambda or plain routine: `{ @fn, null }` (a capturing lambda is a ClosureValueExpression).
        return EmitRoutineValueClosure(sb: sb, routine: preResolved, recover: recover);
    }

    /// <summary>
    /// A <see cref="NativeRoutineExpression"/>: the routine's bare `@sym`. Declared like a call target: in a
    /// resident-JIT delta the body lives in the base dylib, so without the declare the reference is undefined.
    /// </summary>
    private string EmitNativeRoutineAddress(RoutineInfo routine)
    {
        GenerateRoutineDeclaration(routine: routine);
        return $"@{MangleRoutineName(routine: routine)}";
    }

    /// <summary>
    /// Emits a call to <c>@_rf_trace_update_loc</c> with the call site's source line/col baked
    /// in as constants. This refreshes the topmost shadow-stack frame so a subsequent throw
    /// produces a stack trace pointing at the actual call site within the enclosing routine
    /// rather than the routine's declaration line. Skips when trace emission is disabled or
    /// when the location is unset (synthesized AST nodes).
    /// </summary>
    private void EmitTraceLocUpdate(StringBuilder sb, SourceLocation? location)
    {
        if (!_traceCurrentRoutine || _traceHidesPosition)
        {
            return;
        }

        if (location == null)
        {
            return;
        }

        int line = location.Line;
        int col = location.Column;
        if (line <= 0 && col <= 0)
        {
            return;
        }

        EmitLine(sb: sb, line: $"  call void @_rf_trace_update_loc(i64 {TraceSlotValue}, i32 {line}, i32 {col})");
    }

    /// <summary>
    /// Generates code for a function/memberRoutine call.
    /// Handles both standalone function calls and memberRoutine calls on objects.
    /// </summary>
    private string EmitCall(StringBuilder sb, CallExpression call)
    {
        EmitTraceLocUpdate(sb: sb, location: call.Location);

        if (call.RecoversThroughValue)
        {
            return EmitRecoveringValueCall(sb: sb, call: call);
        }

        // Module-qualified call `Module.routine(...)`: SA resolved it to a module-level routine
        // (OwnerType == null) even though the callee is syntactically a member access. There is no
        // receiver, so emit it as a free call rather than a memberRoutine call.
        if (call.Callee is MemberExpression && call.ResolvedRoutine is
                { OwnerType: null } moduleRoutine)
        {
            return EmitRoutineCall(sb: sb,
                req: new RoutineCallRequest(FunctionName: moduleRoutine.BaseName,
                    Arguments: call.Arguments,
                    ResolvedRoutine: moduleRoutine,
                    ResolvedReturnType: call.ResolvedType,
                    TypeArguments: call.TypeArguments) { IsFailable = call.IsFailable });
        }

        return call.Callee switch
        {
            // Determine if this is a memberRoutine call (callee is MemberExpression) or standalone function call
            MemberExpression member => EmitMemberRoutineCall(sb: sb,
                member: member,
                arguments: call.Arguments,
                resolvedRoutine: call.ResolvedRoutine,
                typeArguments: call.TypeArguments,
                loweringKind: call.LoweringKind),
            IdentifierExpression id => EmitRoutineCall(sb: sb,
                req: new RoutineCallRequest(FunctionName: id.Name,
                    Arguments: call.Arguments,
                    ResolvedRoutine: call.ResolvedRoutine,
                    ResolvedReturnType: call.ResolvedType,
                    TypeArguments: call.TypeArguments) { IsFailable = call.IsFailable }),
            _ => throw new NotImplementedException(
                message: $"Cannot emit call for callee type: {call.Callee.GetType().Name}")
        };
    }

    /// <summary>
    /// A call through a routine value (a local or parameter, or a routine-typed field) made beneath a recovery
    /// keyword (<see cref="CallExpression.RecoversThroughValue"/>): loads the value and calls its recovering
    /// entry, yielding the call's carrier.
    /// </summary>
    private string EmitRecoveringValueCall(StringBuilder sb, CallExpression call)
    {
        TypeSymbol carrier = call.ResolvedType ??
                             throw new InvalidOperationException(
                                 message: $"A recovering routine-value call at {call.Location} has no carrier type.");
        string fatVal;
        TypeSymbol? valueType;
        switch (call.Callee)
        {
            case IdentifierExpression id when _localVariables.TryGetValue(key: id.Name, value: out TypeSymbol? localType):
                string llvmName = _localVarLlvmNames.GetValueOrDefault(key: id.Name, defaultValue: id.Name);
                fatVal = NextTemp();
                EmitLine(sb: sb, line: $"  {fatVal} = load {RoutineValueLlvmType}, ptr %{llvmName}.addr");
                valueType = localType;
                break;
            case MemberExpression member:
                valueType = member.ResolvedType ?? GetMemberType(member: member);
                fatVal = EmitMemberVariableAccess(sb: sb, expr: member);
                break;
            default:
                valueType = call.Callee.ResolvedType;
                fatVal = EmitExpression(sb: sb, expr: call.Callee);
                break;
        }

        if (valueType is not RoutineTypeSymbol routineType)
        {
            throw new InvalidOperationException(
                message: $"A recovering routine-value call at {call.Location} calls {Article.For(name: valueType?.Name)} '{valueType?.Name}', not a " +
                         "routine value.");
        }

        return EmitRecoveringRoutineValueCall(sb: sb,
            fatVal: fatVal,
            routineType: routineType,
            arguments: call.Arguments,
            carrier: carrier);
    }

    /// <summary>
    /// Carries out a representation cast: the conversion <c>RepresentationCastPass</c> stamped on it, as the
    /// LLVM instruction of that name.
    /// </summary>
    private string EmitBackendCast(StringBuilder sb, BackendCastExpression cast)
    {
        string value = EmitExpression(sb: sb, expr: cast.Value);
        if (cast.Conversion == RepresentationConversion.Same)
        {
            return value;
        }

        string source = GetLlvmType(type: GetExpressionType(expr: cast.Value) ??
                                          throw new InvalidOperationException(message: "A converting cast has an untyped value."));
        if (cast.Conversion == RepresentationConversion.SpillToAddress)
        {
            string slot = NextTemp();
            EmitEntryAlloca(llvmName: slot, llvmType: source);
            EmitLine(sb: sb, line: $"  store {source} {value}, ptr {slot}");
            return slot;
        }

        string target = GetLlvmType(type: cast.ResolvedType ??
                                          throw new InvalidOperationException(message: "A backend cast has no target type."));
        string instruction = cast.Conversion switch
        {
            RepresentationConversion.Truncate => "trunc",
            RepresentationConversion.ZeroExtend => "zext",
            RepresentationConversion.SignExtend => "sext",
            RepresentationConversion.Bitcast => "bitcast",
            RepresentationConversion.IntToPointer => "inttoptr",
            RepresentationConversion.PointerToInt => "ptrtoint",
            RepresentationConversion.FloatTruncate => "fptrunc",
            RepresentationConversion.FloatExtend => "fpext",
            RepresentationConversion.FloatToSigned => "fptosi",
            RepresentationConversion.FloatToUnsigned => "fptoui",
            RepresentationConversion.SignedToFloat => "sitofp",
            RepresentationConversion.UnsignedToFloat => "uitofp",
            _ => throw new InvalidOperationException(message: $"Unknown representation conversion {cast.Conversion}.")
        };
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = {instruction} {source} {value} to {target}");
        return result;
    }

    /// <summary>
    /// Emit binary op as part of this builder phase.
    /// </summary>
    private string EmitBinaryOp(StringBuilder sb, BinaryExpression binary)
    {
        // Wired/comparison/membership/logical operators are all lowered to member calls or folded to
        // literals UPSTREAM (OperatorLoweringPass / ExpressionLoweringPass) and throw here if they leak.
        // The arms that remain are FUNDAMENTAL IR primitives with no member routine to dispatch to:
        // Assign (storage), Is/IsNot (choice-discriminant icmp), IdentityEqual/NotEqual (ptr icmp).
        return binary.Operator switch
        {
            BinaryOperator.And => throw new InvalidOperationException(
                message:
                $"BinaryExpression(And) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. In routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            BinaryOperator.Or => throw new InvalidOperationException(
                message:
                $"BinaryExpression(Or) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. In routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            BinaryOperator.Assign => EmitBinaryAssign(sb: sb, binary: binary),
            // `x in coll` / `x notin coll` are lowered to `coll.has(x)` / `coll.lacks(x)`
            // member calls by OperatorLoweringPass before codegen (membership operators reverse
            // receiver/argument there). A bare BinaryExpression(In/NotIn) reaching codegen means a body
            // skipped that lowering — fix the pass, not here.
            BinaryOperator.In or BinaryOperator.NotIn => throw new InvalidOperationException(
                message:
                $"BinaryExpression({binary.Operator}) must be lowered to a has/lacks member " +
                $"call by OperatorLoweringPass before codegen (right={binary.Right.GetType().Name}, loc={binary.Location})"),
            // `is` / `isnot` are lowered UPSTREAM to a structural comparison: variant → type_id compare
            // (ExpressionLoweringPass), choice → S32 discriminant equality (ExpressionLoweringPass +
            // PatternLoweringPass, via S32.eq). A bare BinaryExpression(Is/IsNot) reaching codegen means a
            // body skipped that lowering — fix the pass, not here.
            BinaryOperator.Is or BinaryOperator.IsNot => throw new InvalidOperationException(
                message:
                $"BinaryExpression({binary.Operator}) must be lowered to a type_id / discriminant compare " +
                $"before codegen (left={binary.Left.ResolvedType?.Name ?? "?"}, loc={binary.Location})"),
            // Reference identity (===, !==): a raw pointer compare on the operands. Every entity and
            // forwarding wrapper lowers to a `ptr` (see GetLlvmType), and that pointer IS the object
            // reference member forwarding dispatches on — so comparing the two pointers answers
            // "same object?". SA already restricted the operands to reference-carrying types.
            BinaryOperator.IdentityEqual => EmitIdentityCompare(sb: sb,
                binary: binary,
                cmpOp: "eq"),
            BinaryOperator.IdentityNotEqual => EmitIdentityCompare(sb: sb,
                binary: binary,
                cmpOp: "ne"),
            // obeys/disobeys are folded to a buildtime Bool literal by ExpressionLoweringPass
            // (SA validates the conformance and gates any error). They must never reach codegen.
            BinaryOperator.Obeys => throw new InvalidOperationException(
                message:
                $"BinaryExpression({binary.Operator}) must be folded to a Bool literal by " +
                $"ExpressionLoweringPass before codegen (loc={binary.Location})"),
            // (Flags bitor/bitand/bitxor/eq/ne are now real member-routine calls — synthesized as
            // @llvm_ir intrinsic bodies by WiredRoutinePass and lowered by OperatorLoweringPass — so
            // they never reach codegen as a bare BinaryExpression. bitnot stays as EmitBitwiseNot.)
            _ => throw new InvalidOperationException(
                message:
                $"BinaryExpression({binary.Operator}) must be lowered to a wired call before codegen " +
                $"(left={binary.Left.GetType().Name}, loc={binary.Location})")
        };
    }

    /// <summary>
    /// Emit binary assign as part of this builder phase.
    /// </summary>
    private string EmitBinaryAssign(StringBuilder sb, BinaryExpression binary)
    {
        // An index assignment left here has no setitem (a raw store). Every other one was lowered to a
        // setitem call by OperatorLoweringPass.
        if (binary.Left is IndexExpression idxLhs)
        {
            EmitIndexAssignment(sb: sb, index: idxLhs, rhs: binary.Right);
            return "undef";
        }

        string value = EmitExpression(sb: sb, expr: binary.Right);

        switch (binary.Left)
        {
            case IdentifierExpression id:
                EmitVariableAssignment(sb: sb, varName: id.Name, value: value, variable: id.ResolvedVariable);
                NullStampStolenLocalUnlessSame(sb: sb, expr: binary.Right, target: id);
                break;
            case MemberExpression member:
            {
                EmitMemberVariableAssignment(sb: sb,
                    member: member,
                    value: value,
                    valueType: GetExpressionType(expr: binary.Right));
                // Ownership transfer: `me.field = local` hands the local's heap allocation to the
                // field, so a stolen local's slot is null-stamped.
                NullStampStolenLocal(sb: sb, expr: binary.Right);

                break;
            }
            default:
                throw new NotImplementedException(
                    message:
                    $"Assignment target not implemented for expression type: {binary.Left.GetType().Name}");
        }

        return value;
    }

    /// <summary>
    /// Emits <c>===</c> / <c>!==</c> as a pointer-identity compare. Both operands lower to a <c>ptr</c>
    /// (entity or forwarding wrapper), so <c>icmp eq/ne ptr</c> answers "same object?". Returns the i1.
    /// </summary>
    private string EmitIdentityCompare(StringBuilder sb, BinaryExpression binary, string cmpOp)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);
        string type = binary.Left.ResolvedType is { } leftType ? GetLlvmType(type: leftType) : "ptr";
        string compared = "ptr";
        // A handle that holds its manager's address as a field (`Watched[T]` is `{ ptr }`) is the same object exactly
        // when the addresses match: compare the field.
        if (type.StartsWith(value: "%", comparisonType: StringComparison.Ordinal) || type.StartsWith(value: "{", comparisonType: StringComparison.Ordinal))
        {
            string l = NextTemp();
            string r = NextTemp();
            EmitLine(sb: sb, line: $"  {l} = extractvalue {type} {left}, 0");
            EmitLine(sb: sb, line: $"  {r} = extractvalue {type} {right}, 0");
            string field = binary.Left.ResolvedType is RecordTypeSymbol { MemberVariables: [var first, ..] }
                ? GetLlvmType(type: first.Type)
                : throw new InvalidOperationException(
                    message: $"An identity compare of '{type}' has no field to compare at {binary.Location}.");
            (left, right, compared) = (l, r, field);
        }
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = icmp {cmpOp} {compared} {left}, {right}");
        return result;
    }


    /// <summary>
    /// Emit unary op as part of this builder phase.
    /// </summary>
    private string EmitUnaryOp(StringBuilder sb, UnaryExpression unary)
    {
        // Wired unary operators lower to member calls upstream (and throw here if they leak). The
        // arms that remain are fundamental: Steal (pure passthrough) and flags BitwiseNot below —
        // BitwiseNot on FlagsTypeSymbol is intentionally left unlowered by OperatorLoweringPass
        // (flags have no bitnot body to avoid synthesizer recursion). Emit `xor x, -1` directly
        // on the underlying integer type, mirroring EmitFlagsBitwiseOp.
        if (unary.Operator == UnaryOperator.BitwiseNot &&
            GetExpressionType(expr: unary.Operand) is FlagsTypeSymbol flagsType)
        {
            string operand = EmitExpression(sb: sb, expr: unary.Operand);
            string llvmType = GetLlvmType(type: flagsType);
            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = xor {llvmType} {operand}, -1");
            return result;
        }

        return unary.Operator switch
        {
            UnaryOperator.Not => throw new InvalidOperationException(
                message:
                $"UnaryExpression(Not) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. Routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            UnaryOperator.Steal => EmitExpression(sb: sb, expr: unary.Operand),
            _ => throw new InvalidOperationException(
                message:
                $"UnaryExpression({unary.Operator}) must be lowered to a wired call before codegen")
        };
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Emits code for a <see cref="CarrierPayloadExpression"/>: loads the inline payload
    /// (field 1 — a [P x i8] buffer where P = max(sizeof(T), 8)) from a Result/Lookup carrier
    /// as the concrete type.
    ///
    /// <list type="bullet">
    /// <item>Entity types: payload slot holds a ptr — <c>load ptr</c>.</item>
    /// <item>A crashable in a recovery carrier: the slot holds its object's address — <c>load ptr</c>, then the
    /// crashable from the object.</item>
    /// <item>Record / primitive types: payload slot holds the value inline — <c>load &lt;type&gt;</c>.</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// A call, with the failure it gives kept: a call of a <c>check</c> variant (or anything that returns a
    /// <c>Check</c>/<c>Lookup</c>) copies its failure out of the flow's mold into the carrier, except one the builder
    /// writes to pass the failure straight on (<see cref="CallExpression.IsFailurePassOn"/>).
    /// </summary>
    private string EmitCallKeepingFailure(StringBuilder sb, CallExpression call)
    {
        string value = EmitCall(sb: sb, call: call);
        if (value.Length == 0 || !KeepsFailure(call: call))
        {
            return value;
        }

        return EmitKeepFailure(sb: sb, value: value, carrier: call.ResolvedType!);
    }

    /// <summary>Whether a call gives a <c>Check</c>/<c>Lookup</c> whose failure is copied out of the flow's mold where it
    /// lands: any call that returns one, except one the builder writes to pass the failure straight on.</summary>
    private static bool KeepsFailure(CallExpression call)
    {
        return !call.IsFailurePassOn && call.ResolvedType is VariantTypeSymbol carrier && IsRecoveryCarrier(carrier: carrier);
    }

    private string EmitCarrierPayloadExpression(StringBuilder sb, CarrierPayloadExpression payload)
    {
        return EmitCarrierPayloadCore(sb: sb, payload: payload);
    }

    /// <summary>Emits a <see cref="VariantWidenExpression"/>: the smaller variant written over the start of a zeroed
    /// larger one, which is then read whole.</summary>
    private string EmitVariantWiden(StringBuilder sb, VariantWidenExpression widen)
    {
        string value = EmitExpression(sb: sb, expr: widen.Value);
        string fromType = GetLlvmType(type: widen.Value.ResolvedType!);
        string toType = GetLlvmType(type: widen.ResolvedType!);
        string slot = NextTemp();
        EmitEntryAlloca(llvmName: slot, llvmType: toType);
        EmitLine(sb: sb, line: $"  store {toType} zeroinitializer, ptr {slot}");
        EmitLine(sb: sb, line: $"  store {fromType} {value}, ptr {slot}");
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = load {toType}, ptr {slot}");
        return result;
    }

    private string EmitCarrierPayloadCore(StringBuilder sb, CarrierPayloadExpression payload)
    {
        // EmitExpression returns a loaded struct value (not a pointer); GEP needs a pointer.
        // Spill the carrier value to a temp alloca first.
        string carrierVal = EmitExpression(sb: sb, expr: payload.Carrier);

        TypeSymbol carrierType = payload.Carrier.ResolvedType!;
        // Both a Result/Lookup/Maybe carrier and a general user variant store their payload at field 1
        // ({ tag/flag, payload }); pick the right aggregate LLVM type for each.
        string carrierLlvmType =
            carrierType is VariantTypeSymbol variant && !IsCarrierType(type: carrierType)
                ? GetVariantTypeName(variant: variant)
                : GetCarrierLlvmType(type: carrierType);

        string spillAddr = NextTemp();
        EmitEntryAlloca(llvmName: spillAddr, llvmType: carrierLlvmType);
        EmitLine(sb: sb, line: $"  store {carrierLlvmType} {carrierVal}, ptr {spillAddr}");

        TypeSymbol? concreteType = payload.ResolvedType ?? payload.ConcreteType.ResolvedType ??
            _registry.LookupType(name: payload.ConcreteType.Name);

        string payloadPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {payloadPtr} = getelementptr {carrierLlvmType}, ptr {spillAddr}, i32 0, i32 1");

        if (concreteType is CrashableTypeSymbol crashable && IsRecoveryCarrier(carrier: carrierType))
        {
            // The error slot holds a Crashables, and the crashable sits in its room: a failure a pattern reads was
            // received by a check, which kept it (EmitKeepFailure).
            string crashableType = EnsureRecordTypeDeclared(record: crashable);
            string roomPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {roomPtr} = getelementptr {CrashablesLlvmType()}, ptr {payloadPtr}, i32 0, i32 {CrashablesRoomField}");
            string error = NextTemp();
            EmitLine(sb: sb, line: $"  {error} = load {crashableType}, ptr {roomPtr}, align 8");
            return error;
        }

        string loadType;
        if (concreteType is EntityTypeSymbol)
        {
            loadType = "ptr";
        }
        else if (concreteType != null)
        {
            // A None payload (`Check[None]`) is the empty record `{}`, as a None value is everywhere.
            loadType = GetValueLlvmType(type: concreteType);
        }
        else
        {
            loadType = "i64";
        }

        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {loadType}, ptr {payloadPtr}");
        return loaded;
    }

    /// <summary>
    /// Emits a <see cref="CrashableDispatchExpression"/>: a member of a caught error of unknown type. The receiver is a
    /// <c>Crashables</c>: <c>crash_type_id</c> reads its type id, and every other member goes through the module's
    /// dispatch function for it (<see cref="EnsureCrashableDispatch"/>), which switches on the type id over the
    /// crashables the program throws.
    /// </summary>
    private string EmitCrashableDispatchExpression(StringBuilder sb,
        CrashableDispatchExpression dispatch)
    {
        string crashablesVal = EmitExpression(sb: sb, expr: dispatch.Carrier);
        string crashablesLlvmType = GetLlvmType(type: dispatch.Carrier.ResolvedType!);
        string spillAddr = NextTemp();
        EmitEntryAlloca(llvmName: spillAddr, llvmType: crashablesLlvmType);
        EmitLine(sb: sb, line: $"  store {crashablesLlvmType} {crashablesVal}, ptr {spillAddr}");
        string idPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {idPtr} = getelementptr {crashablesLlvmType}, ptr {spillAddr}, i32 0, i32 0");
        string typeId = NextTemp();
        EmitLine(sb: sb, line: $"  {typeId} = load i64, ptr {idPtr}");
        if (dispatch.MemberName == Declaration.RuntimeContract.CrashTypeId)
        {
            return typeId;
        }

        if (!Declaration.RuntimeContract.CrashDispatchMembers.Contains(value: dispatch.MemberName))
        {
            throw new InvalidOperationException(
                message: $"'{dispatch.MemberName}' is not a member a caught error is dispatched to.");
        }

        // A failure is read where a check kept it, in its room. Only teardown also meets one still flying (a carrier
        // the builder passes a failure on through and then drops), whose crashable is in the flow's mold.
        string roomPtr;
        if (dispatch.MemberName == Declaration.RuntimeContract.Destroy)
        {
            roomPtr = EmitCaughtCrashableAddress(sb: sb, at: spillAddr);
        }
        else
        {
            roomPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {roomPtr} = getelementptr {crashablesLlvmType}, ptr {spillAddr}, i32 0, i32 {CrashablesRoomField}");
        }

        (string function, string returns) = EnsureCrashableDispatch(member: dispatch.MemberName);
        if (returns == "void")
        {
            EmitLine(sb: sb, line: $"  call void {function}(ptr {roomPtr}, i64 {typeId})");
            return string.Empty;
        }

        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = call {returns} {function}(ptr {roomPtr}, i64 {typeId})");
        return result;
    }

    /// <summary>
    /// Emits a call of a crashable member (<paramref name="callee"/>, a routine or a routine address), materializing its
    /// <c>Text</c> result into <paramref name="resultSlot"/> per the member's return ABI (sret / coerced /
    /// direct). Mirrors the return-ABI handling in <c>EmitCall</c> so the type_id switch respects the same
    /// contract the callee's declaration/definition were emitted under.
    /// </summary>
    private void EmitCrashableMemberCallIntoSlot(StringBuilder sb, RoutineInfo routine,
        string callee, string entity, string retLlvm,
        string resultSlot)
    {
        if (ReturnsViaSret(routine: routine))
        {
            EmitLine(sb: sb,
                line: $"  call void {callee}(ptr sret({retLlvm}) {resultSlot}, ptr {entity})");
            return;
        }

        string? coerce = ReturnCoerceType(routine: routine);
        if (coerce != null)
        {
            string c = NextTemp();
            EmitLine(sb: sb, line: $"  {c} = call {coerce} {callee}(ptr {entity})");
            // Store the coerced integer form; the later `load {retLlvm}` reinterprets it (opaque ptr).
            EmitLine(sb: sb, line: $"  store {coerce} {c}, ptr {resultSlot}");
            return;
        }

        string r = NextTemp();
        EmitLine(sb: sb, line: $"  {r} = call {retLlvm} {callee}(ptr {entity})");
        EmitLine(sb: sb, line: $"  store {retLlvm} {r}, ptr {resultSlot}");
    }
}
