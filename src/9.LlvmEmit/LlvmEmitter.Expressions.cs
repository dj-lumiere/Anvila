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
                CallExpression call => EmitCall(sb: sb, call: call),
                // Only the fundamental operator arms survive to codegen (Assign / Is / IdentityEqual /
                // Steal / flags-bitnot); every wired/comparison/membership/logical op is lowered upstream.
                BinaryExpression binary => EmitBinaryOp(sb: sb, binary: binary),
                UnaryExpression unary => EmitUnaryOp(sb: sb, unary: unary),
                GenericMemberRoutineCallExpression gmc => EmitGmceFallback(sb: sb, gmc: gmc),
                // Array[T,N] and BitArray[N] are inline IR constructs (insertvalue); all other
                // collection literals must be lowered to CreatorExpression + add calls before codegen.
                ListLiteralExpression list when IsArrayOrBitArrayLiteral(type: list.ResolvedType)
                    => EmitListLiteral(sb: sb, list: list),
                // CarrierPayloadExpression is the RESULT of PatternLoweringPass (Maybe/Result payload
                // projection) — codegen consumes it, it is not a surface node.
                CarrierPayloadExpression payload => EmitCarrierPayloadExpression(sb: sb,
                    payload: payload),
                CrashableDispatchExpression dispatch => EmitCrashableDispatchExpression(sb: sb,
                    dispatch: dispatch),
                WrapperProjectionExpression projection => EmitWrapperProjection(sb: sb,
                    projection: projection),
                BackendCastExpression cast => EmitBackendScalarCast(sb: sb,
                    value: EmitExpression(sb: sb, expr: cast.Value),
                    sourceType: GetExpressionType(expr: cast.Value),
                    targetType: cast.ResolvedType ??
                                throw new InvalidOperationException(message: "A backend cast has no target type.")),
                ClosureValueExpression closure => EmitClosureValueExpression(sb: sb, closure: closure),
                // Named arguments appear inside synthesized AST bodies (e.g., me.eq(you: you)).
                // The name is irrelevant to codegen -> just emit the inner value positionally.
                NamedArgumentExpression named => EmitExpression(sb: sb, expr: named.Value),
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
            line: $"  {t0} = insertvalue {{ ptr, ptr }} undef, ptr @{MangleRoutineName(routine: lifted)}, 0");
        string fat = NextTemp();
        EmitLine(sb: sb, line: $"  {fat} = insertvalue {{ ptr, ptr }} {t0}, ptr {bound}, 1");
        return fat;
    }

    /// <summary>
    /// Materializes a plain (non-lambda) routine reference as a captureless heap closure
    /// <c>{ fn_ptr }</c> whose function slot holds a closure-ABI adapter thunk. This lets a bare
    /// routine name flow through the same indirect-call path as a lambda value.
    /// </summary>
    private string EmitRoutineValueClosure(StringBuilder sb, RoutineInfo routine)
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
        EmitLine(sb: sb, line: $"  {t0} = insertvalue {{ ptr, ptr }} undef, ptr {sym}, 0");
        string fat = NextTemp();
        EmitLine(sb: sb, line: $"  {fat} = insertvalue {{ ptr, ptr }} {t0}, ptr null, 1");
        return fat;
    }

    /// <summary>
    /// Reorders call arguments into the routine's parameter-declaration order. Named arguments may be
    /// written in any order, and a caller that binds arguments to parameters positionally would otherwise
    /// put values into the wrong parameter slots. Only applied when every parameter is provided (defaults
    /// are not materialized); otherwise the original list is returned unchanged.
    /// </summary>
    private static List<Expression> ReorderCallArgsToParamOrder(List<Expression> arguments,
        RoutineInfo routine)
    {
        int paramCount = routine.Parameters.Count;
        if (arguments.Count != paramCount)
        {
            return arguments;
        }

        if (!arguments.Any(predicate: a => a is NamedArgumentExpression))
        {
            return arguments;
        }

        var ordered = new Expression?[paramCount];
        var leftovers = new List<Expression>();
        foreach (Expression a in arguments)
        {
            PlaceArgument(a: a,
                routine: routine,
                ordered: ordered,
                leftovers: leftovers);
        }

        return BuildOrderedResult(ordered: ordered,
            leftovers: leftovers,
            fallback: arguments,
            paramCount: paramCount);
    }

    /// <summary>
    /// Places a single call argument into its named slot in <paramref name="ordered"/>, or into
    /// <paramref name="leftovers"/> when no matching parameter name is found or the slot is already
    /// taken.
    /// </summary>
    private static void PlaceArgument(Expression a, RoutineInfo routine, Expression?[] ordered,
        List<Expression> leftovers)
    {
        if (a is not NamedArgumentExpression na)
        {
            leftovers.Add(item: a);
            return;
        }

        int p = FindParamIndex(routine: routine, name: na.Name);
        if (p >= 0 && ordered[p] == null)
        {
            ordered[p] = a;
        }
        else
        {
            leftovers.Add(item: a);
        }
    }

    /// <summary>
    /// Returns the zero-based index of the parameter named <paramref name="name"/> in
    /// <paramref name="routine"/>, or <c>-1</c> if not found.
    /// </summary>
    private static int FindParamIndex(RoutineInfo routine, string name)
    {
        for (int k = 0; k < routine.Parameters.Count; k++)
        {
            if (routine.Parameters[index: k].Name == name)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>
    /// Fills any unoccupied slots in <paramref name="ordered"/> from <paramref name="leftovers"/>
    /// in order, then returns the assembled list if it is complete, or <paramref name="fallback"/>
    /// if any slot remained unfilled.
    /// </summary>
    private static List<Expression> BuildOrderedResult(Expression?[] ordered,
        List<Expression> leftovers, List<Expression> fallback, int paramCount)
    {
        int next = 0;
        var result = new List<Expression>(capacity: paramCount);
        foreach (Expression? slot in ordered)
        {
            if (slot != null)
            {
                result.Add(item: slot);
            }
            else if (next < leftovers.Count)
            {
                result.Add(item: leftovers[index: next++]);
            }
        }

        return result.Count == paramCount
            ? result
            : fallback;
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
        if (ResolveAggregatePreset(name: identifier.Name) is { } aggregatePreset)
        {
            string presetSym = EmitOrGetPresetGlobal(preset: aggregatePreset);
            string arrLlvm = GetLlvmType(type: aggregatePreset.Type);
            string loaded = NextTemp();
            EmitLine(sb: sb, line: $"  {loaded} = load {arrLlvm}, ptr {presetSym}");
            return loaded;
        }

        // Scalar presets must be inlined before backend entry. Codegen should not read declaration
        // AST to recover their values on demand.
        if (_registry.LookupVariable(name: identifier.Name) is { IsPreset: true })
        {
            throw new InvalidOperationException(
                message:
                $"Preset identifier '{identifier.Name}' reached LLVM codegen. PresetInliningPass must inline presets before backend entry.");
        }

        // A pre-resolved routine-VALUE reference (set by a lowering pass, e.g. an unbound member-
        // routine hook). The routine is already known, so skip name-based lookup — the bare name may
        // be a memberRoutine that lookup cannot resolve without the owner type. Falls through the same
        // closure-materialization path (memberRoutines are handled via the member-aware closure path). The
        // node keeps the surrounding-context type (e.g. CPtr for a hook field), so we gate on the
        // resolved routine alone rather than its ResolvedType label.
        if (identifier.ResolvedRoutine is { } preResolved)
        {
            return EmitPreResolvedRoutineValue(sb: sb, preResolved: preResolved);
        }

        // Look up the variable in local variables first
        if (!_localVariables.TryGetValue(key: identifier.Name, value: out TypeSymbol? varType))
        {
            // Suflae module-level `global`: load from its `@global` symbol.
            if (_moduleGlobals.TryGetValue(key: identifier.Name,
                    value: out (TypeSymbol Type, string Symbol) gslot))
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
        string llvmType = GetLlvmType(type: varType);
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
    /// Materializes a value for an identifier whose routine was pre-resolved by a lowering pass.
    /// Cycle-collector roam hooks (`roam_trace` / `roam_free`) emit a bare captureless
    /// `@sym` (they are invoked natively through a CPtr slot, not as a fat Routine value); every
    /// other routine value flows through the closure-materialization path (lambda vs plain routine).
    /// </summary>
    private string EmitPreResolvedRoutineValue(StringBuilder sb, RoutineInfo preResolved)
    {
        if (preResolved.Name is "roam_trace" or "roam_free")
        {
            // Declare it like a call target: in a resident-JIT delta the body lives in the base dylib, so
            // without the declare the bare `@sym` reference is an undefined value at IR parse.
            GenerateRoutineDeclaration(routine: preResolved);
            return $"@{MangleRoutineName(routine: preResolved)}";
        }

        // A captureless lambda or plain routine: `{ @fn, null }` (a capturing lambda is a ClosureValueExpression).
        return EmitRoutineValueClosure(sb: sb, routine: preResolved);
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
        if (!_traceCurrentRoutine)
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

        EmitLine(sb: sb, line: $"  call void @_rf_trace_update_loc(i32 {line}, i32 {col})");
    }

    /// <summary>
    /// Generates code for a function/memberRoutine call.
    /// Handles both standalone function calls and memberRoutine calls on objects.
    /// </summary>
    private string EmitCall(StringBuilder sb, CallExpression call)
    {
        EmitTraceLocUpdate(sb: sb, location: call.Location);

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
    /// Emit backend scalar cast as part of this compiler phase.
    /// </summary>
    private string EmitBackendScalarCast(StringBuilder sb, string value, TypeSymbol? sourceType,
        TypeSymbol targetType)
    {
        string targetLlvm = GetLlvmType(type: targetType);
        string sourceLlvm = sourceType != null
            ? GetLlvmType(type: sourceType)
            : targetLlvm;

        if (sourceLlvm == targetLlvm)
        {
            return value;
        }

        if (targetLlvm == "ptr" && sourceLlvm != "ptr")
        {
            string cast = NextTemp();
            EmitLine(sb: sb, line: $"  {cast} = inttoptr {sourceLlvm} {value} to ptr");
            return cast;
        }

        if (targetLlvm != "ptr" && sourceLlvm == "ptr")
        {
            string cast = NextTemp();
            EmitLine(sb: sb, line: $"  {cast} = ptrtoint ptr {value} to {targetLlvm}");
            return cast;
        }

        if (TryGetLlvmIntegerWidth(llvmType: sourceLlvm, bitWidth: out int sourceIntBits) &&
            TryGetLlvmIntegerWidth(llvmType: targetLlvm, bitWidth: out int targetIntBits))
        {
            string integerResult = NextTemp();
            if (sourceIntBits > targetIntBits)
            {
                EmitLine(sb: sb,
                    line: $"  {integerResult} = trunc {sourceLlvm} {value} to {targetLlvm}");
            }
            else if (sourceIntBits < targetIntBits)
            {
                string op = IsUnsignedIntegerType(type: targetType)
                    ? "zext"
                    : "sext";
                EmitLine(sb: sb,
                    line: $"  {integerResult} = {op} {sourceLlvm} {value} to {targetLlvm}");
            }
            else
            {
                EmitLine(sb: sb,
                    line: $"  {integerResult} = bitcast {sourceLlvm} {value} to {targetLlvm}");
            }

            return integerResult;
        }

        return EmitScalarWidthOrFloatCast(sb: sb,
            value: value,
            sourceType: sourceType,
            targetType: targetType,
            sourceLlvm: sourceLlvm,
            targetLlvm: targetLlvm);
    }

    /// <summary>
    /// Emits the float↔float / float↔int / int-width-change cast for two same-kind-or-mixed scalar
    /// LLVM types (both already known to be non-ptr and not equal). The signedness comes from the
    /// RazorForge <paramref name="sourceType"/>/<paramref name="targetType"/>.
    /// </summary>
    private string EmitScalarWidthOrFloatCast(StringBuilder sb, string value, TypeSymbol? sourceType,
        TypeSymbol targetType, string sourceLlvm, string targetLlvm)
    {
        bool sourceIsFloat =
            sourceLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;
        bool targetIsFloat =
            targetLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;
        bool targetUnsigned = IsUnsignedIntegerType(type: targetType);

        string result = NextTemp();
        if (sourceIsFloat && targetIsFloat)
        {
            EmitFloatToFloatCast(sb: sb,
                result: result,
                value: value,
                sourceLlvm: sourceLlvm,
                targetLlvm: targetLlvm);
        }
        else if (sourceIsFloat)
        {
            EmitFloatToIntCast(sb: sb,
                result: result,
                value: value,
                sourceLlvm: sourceLlvm,
                targetLlvm: targetLlvm,
                targetUnsigned: targetUnsigned);
        }
        else if (targetIsFloat)
        {
            EmitIntToFloatCast(sb: sb,
                result: result,
                value: value,
                sourceLlvm: sourceLlvm,
                targetLlvm: targetLlvm,
                sourceType: sourceType);
        }
        else
        {
            EmitIntWidthCast(sb: sb,
                result: result,
                value: value,
                sourceLlvm: sourceLlvm,
                targetLlvm: targetLlvm,
                targetUnsigned: targetUnsigned);
        }

        return result;
    }

    /// <summary>Emits a float-to-float cast (fptrunc or fpext) based on relative bit widths.</summary>
    private void EmitFloatToFloatCast(StringBuilder sb, string result, string value,
        string sourceLlvm, string targetLlvm)
    {
        string op = GetTypeBitWidth(llvmType: sourceLlvm) > GetTypeBitWidth(llvmType: targetLlvm)
            ? "fptrunc"
            : "fpext";
        EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
    }

    /// <summary>Emits a float-to-integer cast (fptoui or fptosi) based on target signedness.</summary>
    private static void EmitFloatToIntCast(StringBuilder sb, string result, string value,
        string sourceLlvm, string targetLlvm, bool targetUnsigned)
    {
        string op = targetUnsigned
            ? "fptoui"
            : "fptosi";
        EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
    }

    /// <summary>Emits an integer-to-float cast (uitofp or sitofp) based on source signedness.</summary>
    private static void EmitIntToFloatCast(StringBuilder sb, string result, string value,
        string sourceLlvm, string targetLlvm, TypeSymbol? sourceType)
    {
        bool sourceUnsigned = IsUnsignedIntegerType(type: sourceType);
        string op = sourceUnsigned
            ? "uitofp"
            : "sitofp";
        EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
    }

    /// <summary>
    /// Emits an integer width-change cast: trunc (narrowing), zext/sext (widening), or bitcast
    /// (same width). Widening sign depends on the RazorForge <paramref name="targetUnsigned"/> flag.
    /// </summary>
    private void EmitIntWidthCast(StringBuilder sb, string result, string value,
        string sourceLlvm, string targetLlvm, bool targetUnsigned)
    {
        int srcBits = GetTypeBitWidth(llvmType: sourceLlvm);
        int dstBits = GetTypeBitWidth(llvmType: targetLlvm);
        if (srcBits > dstBits)
        {
            EmitLine(sb: sb, line: $"  {result} = trunc {sourceLlvm} {value} to {targetLlvm}");
        }
        else if (srcBits < dstBits)
        {
            string op = targetUnsigned
                ? "zext"
                : "sext";
            EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
        }
        else
        {
            EmitLine(sb: sb, line: $"  {result} = bitcast {sourceLlvm} {value} to {targetLlvm}");
        }
    }

    /// <summary>
    /// Attempts to get LLVM integer width and reports whether it succeeded.
    /// </summary>
    private static bool TryGetLlvmIntegerWidth(string llvmType, out int bitWidth)
    {
        bitWidth = 0;
        if (!llvmType.StartsWith(value: 'i') || llvmType.Length < 2)
        {
            return false;
        }

        return int.TryParse(s: llvmType.AsSpan(start: 1), result: out bitWidth);
    }

    /// <summary>
    /// Emits a primitive type cast (trunc/zext/sext/bitcast) from one LLVM primitive type to another.
    /// Used when an explicitly typed variable declaration has an initializer of a different type.
    /// </summary>
    private string EmitPrimitiveCast(StringBuilder sb, string value, string fromLlvm,
        string toLlvm)
    {
        if (fromLlvm == toLlvm)
        {
            return value;
        }

        bool fromIsFloat = fromLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;
        bool toIsFloat = toLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;

        string cast = NextTemp();
        if (fromIsFloat && toIsFloat)
        {
            string op = GetTypeBitWidth(llvmType: fromLlvm) > GetTypeBitWidth(llvmType: toLlvm)
                ? "fptrunc"
                : "fpext";
            EmitLine(sb: sb, line: $"  {cast} = {op} {fromLlvm} {value} to {toLlvm}");
        }
        else if (fromIsFloat)
        {
            EmitLine(sb: sb, line: $"  {cast} = fptosi {fromLlvm} {value} to {toLlvm}");
        }
        else if (toIsFloat)
        {
            EmitLine(sb: sb, line: $"  {cast} = sitofp {fromLlvm} {value} to {toLlvm}");
        }
        else
        {
            int srcBits = GetTypeBitWidth(llvmType: fromLlvm);
            int dstBits = GetTypeBitWidth(llvmType: toLlvm);
            string op = "bitcast";
            if (srcBits > dstBits)
            {
                op = "trunc";
            }
            else if (srcBits < dstBits)
            {
                op = "zext";
            }

            EmitLine(sb: sb, line: $"  {cast} = {op} {fromLlvm} {value} to {toLlvm}");
        }

        return cast;
    }

    /// <summary>
    /// Emit binary op as part of this compiler phase.
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
            // `x in coll` / `x notin coll` are lowered to `coll.contains(x)` / `coll.notcontains(x)`
            // member calls by OperatorLoweringPass before codegen (membership operators reverse
            // receiver/argument there). A bare BinaryExpression(In/NotIn) reaching codegen means a body
            // skipped that lowering — fix the pass, not here.
            BinaryOperator.In or BinaryOperator.NotIn => throw new InvalidOperationException(
                message:
                $"BinaryExpression({binary.Operator}) must be lowered to a contains/notcontains member " +
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
            // obeys/disobeys are folded to a compile-time Bool literal by ExpressionLoweringPass
            // (SA validates the conformance and gates any error). They must never reach codegen.
            BinaryOperator.Obeys or BinaryOperator.Disobeys => throw new InvalidOperationException(
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
    /// Emit binary assign as part of this compiler phase.
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
                EmitVariableAssignment(sb: sb, varName: id.Name, value: value);
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
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = icmp {cmpOp} ptr {left}, {right}");
        return result;
    }


    /// <summary>
    /// Emit unary op as part of this compiler phase.
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
    /// Resolves generic type parameters in a member's type using the owner's type arguments.
    /// Builds a substitution map from the owner and delegates to SubstituteTypeParams.
    /// </summary>
    private TypeSymbol ResolveGenericMemberType(TypeSymbol memberType, TypeSymbol ownerType)
    {
        TypeSymbol? ownerGenericDef = ownerType switch
        {
            RecordTypeSymbol r => r.GenericDefinition,
            EntityTypeSymbol e => e.GenericDefinition,
            _ => null
        };
        if (ownerGenericDef?.GenericParameters == null || ownerType.TypeArguments == null)
        {
            return memberType;
        }

        var subs = new Dictionary<string, TypeSymbol>();
        for (int i = 0;
             i < ownerGenericDef.GenericParameters.Count && i < ownerType.TypeArguments.Count;
             i++)
        {
            subs[key: ownerGenericDef.GenericParameters[index: i]] =
                ownerType.TypeArguments[index: i];
        }

        if (subs.Count == 0)
        {
            return memberType;
        }

        return SubstituteTypeParams(type: memberType, substitutions: subs);
    }

    /// <summary>
    /// Emits code for a <see cref="CarrierPayloadExpression"/>: loads the inline payload
    /// (field 1 — a [P x i8] buffer where P = max(sizeof(T), 8)) from a Result/Lookup carrier
    /// as the concrete type.
    ///
    /// <list type="bullet">
    /// <item>Entity / crashable types: payload slot holds a ptr — <c>load ptr</c>.</item>
    /// <item>Record / primitive types: payload slot holds the value inline — <c>load &lt;type&gt;</c>.</item>
    /// </list>
    /// </summary>
    private string EmitCarrierPayloadExpression(StringBuilder sb, CarrierPayloadExpression payload)
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
        EmitLine(sb: sb, line: $"  {spillAddr} = alloca {carrierLlvmType}");
        EmitLine(sb: sb, line: $"  store {carrierLlvmType} {carrierVal}, ptr {spillAddr}");

        TypeSymbol? concreteType = payload.ResolvedType ?? payload.ConcreteType.ResolvedType ??
            _registry.LookupType(name: payload.ConcreteType.Name);

        string payloadPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {payloadPtr} = getelementptr {carrierLlvmType}, ptr {spillAddr}, i32 0, i32 1");

        string loadType;
        if (concreteType is EntityTypeSymbol or CrashableTypeSymbol)
        {
            loadType = "ptr";
        }
        else if (concreteType != null)
        {
            loadType = GetLlvmType(type: concreteType);
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
    /// Emits a <see cref="CrashableDispatchExpression"/>: runtime dispatch of a zero-arg Crashable member
    /// (represent/diagnose/crash_message/crash_title, all <c>-&gt; Text</c>) on a type-erased error stored
    /// in a Result/Lookup carrier. Reads <c>type_id</c> (field 0) and the entity pointer (field 1) from the
    /// carrier, then <c>switch</c>es on <c>type_id</c> to the concrete crashable's member.
    ///
    /// <para>This replaces the old build-time <c>is Crashable</c> fan-out (one clause per registered
    /// crashable baked into the carrier body). The switch is emitted per-build over the CURRENTLY registered
    /// crashable set, so a warm daemon compile includes user-defined crashables registered after the stdlib
    /// snapshot — the fan-out freeze bug that made a warm carrier fall through to its type-name else arm.</para>
    /// </summary>
    private string EmitCrashableDispatchExpression(StringBuilder sb,
        CrashableDispatchExpression dispatch)
    {
        // Spill the carrier value so we can GEP its type_id (field 0) and payload entity ptr (field 1).
        string carrierVal = EmitExpression(sb: sb, expr: dispatch.Carrier);
        TypeSymbol carrierType = dispatch.Carrier.ResolvedType!;
        string carrierLlvmType = GetCarrierLlvmType(type: carrierType);

        string spillAddr = NextTemp();
        EmitLine(sb: sb, line: $"  {spillAddr} = alloca {carrierLlvmType}");
        EmitLine(sb: sb, line: $"  store {carrierLlvmType} {carrierVal}, ptr {spillAddr}");

        string typeIdPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {typeIdPtr} = getelementptr {carrierLlvmType}, ptr {spillAddr}, i32 0, i32 0");
        string typeId = NextTemp();
        EmitLine(sb: sb, line: $"  {typeId} = load i64, ptr {typeIdPtr}");

        string payloadPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {payloadPtr} = getelementptr {carrierLlvmType}, ptr {spillAddr}, i32 0, i32 1");
        string entity = NextTemp();
        EmitLine(sb: sb, line: $"  {entity} = load ptr, ptr {payloadPtr}");

        // Enumerate the crashables that get a dispatch arm. Only a crashable whose dispatched member is LIVE
        // (demand-reached) gets one: a crashable is thrown before it can land in a carrier, so the reached set
        // covers every type_id the carrier can actually hold. This makes the arm set DETERMINISTIC across a
        // cold compile (registry holds only reached crashables) and a warm/daemon compile (registry holds the
        // whole stdlib) — enumerating ALL registered crashables diverged the two (warm emitted arms + member
        // definitions for never-thrown stdlib errors like IOError; cold did not). The collector seeds exactly
        // these members live per reached crashable, so codegen and the collector agree on the same set.
        var arms = new List<(long id, RoutineInfo routine, string mangled, string label)>();
        string? retLlvm = null;
        foreach (TypeSymbol t in _registry.GetTypesByCategory(category: TypeCategory.Crashable))
        {
            if (t is not CrashableTypeSymbol crashable)
            {
                continue;
            }

            RoutineInfo? routine = _registry.LookupMemberRoutine(type: crashable,
                memberRoutineName: dispatch.MemberName,
                isFailable: false);
            if (routine is null or { IsGenericDefinition: true })
            {
                continue;
            }

            if (!_liveRoutineKeys.Contains(item: routine.RegistryKey))
            {
                continue;
            }

            GenerateRoutineDeclaration(routine: routine);
            string mangled = MangleRoutineName(routine: routine);
            retLlvm ??= routine.ReturnType != null
                ? GetLlvmType(type: routine.ReturnType)
                : "ptr";
            long id = unchecked((long)TypeIdHelper.ComputeTypeId(fullName: crashable.FullName));
            arms.Add(item: (id, routine, mangled, NextLabel(prefix: "crd.case")));
        }

        retLlvm ??= "ptr";

        // No registered crashables (shouldn't happen where a Crashable arm exists) — yield a zero result.
        if (arms.Count == 0)
        {
            return retLlvm == "ptr"
                ? "null"
                : "zeroinitializer";
        }

        // Shared result slot: every arm writes its Text here, and we load it ONCE after the merge. This
        // sidesteps a phi over values whose call ABI differs (sret vs coerced vs direct) — each arm just
        // materializes into the slot per its own ABI.
        string resultSlot = NextTemp();
        EmitEntryAlloca(llvmName: resultSlot, llvmType: retLlvm);

        string mergeLabel = NextLabel(prefix: "crd.merge");
        string defaultLabel = NextLabel(prefix: "crd.default");

        var switchArms = new StringBuilder();
        foreach ((long id, _, _, string label) in arms)
        {
            switchArms.Append(value: $"    i64 {id}, label %{label}\n");
        }

        EmitLine(sb: sb, line: $"  switch i64 {typeId}, label %{defaultLabel} [\n{switchArms}  ]");

        foreach ((_, RoutineInfo routine, string mangled, string label) in arms)
        {
            EmitLine(sb: sb, line: $"{label}:");
            EmitCrashableMemberCallIntoSlot(sb: sb,
                routine: routine,
                mangled: mangled,
                entity: entity,
                retLlvm: retLlvm,
                resultSlot: resultSlot);
            EmitLine(sb: sb, line: $"  br label %{mergeLabel}");
        }

        // The Crashable arm only fires for a real crashable type_id, so the default is unreachable.
        EmitLine(sb: sb, line: $"{defaultLabel}:");
        EmitLine(sb: sb, line: "  unreachable");

        EmitLine(sb: sb, line: $"{mergeLabel}:");
        string phiResult = NextTemp();
        EmitLine(sb: sb, line: $"  {phiResult} = load {retLlvm}, ptr {resultSlot}");
        return phiResult;
    }

    /// <summary>
    /// Emits one crashable-dispatch arm's call to a concrete crashable member, materializing its
    /// <c>Text</c> result into <paramref name="resultSlot"/> per the member's return ABI (sret / coerced /
    /// direct). Mirrors the return-ABI handling in <c>EmitCall</c> so the type_id switch respects the same
    /// contract the callee's declaration/definition were emitted under.
    /// </summary>
    private void EmitCrashableMemberCallIntoSlot(StringBuilder sb, RoutineInfo routine,
        string mangled, string entity, string retLlvm,
        string resultSlot)
    {
        if (ReturnsViaSret(routine: routine))
        {
            EmitLine(sb: sb,
                line: $"  call void @{mangled}(ptr sret({retLlvm}) {resultSlot}, ptr {entity})");
            return;
        }

        string? coerce = ReturnCoerceType(routine: routine);
        if (coerce != null)
        {
            string c = NextTemp();
            EmitLine(sb: sb, line: $"  {c} = call {coerce} @{mangled}(ptr {entity})");
            // Store the coerced integer form; the later `load {retLlvm}` reinterprets it (opaque ptr).
            EmitLine(sb: sb, line: $"  store {coerce} {c}, ptr {resultSlot}");
            return;
        }

        string r = NextTemp();
        EmitLine(sb: sb, line: $"  {r} = call {retLlvm} @{mangled}(ptr {entity})");
        EmitLine(sb: sb, line: $"  store {retLlvm} {r}, ptr {resultSlot}");
    }
}
