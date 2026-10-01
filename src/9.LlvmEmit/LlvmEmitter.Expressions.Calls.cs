using System.Text;
using Builder.Lowering.Passes;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.LlvmEmit;

/// <summary>
/// Expression code generation for routine calls and compound assignment.
/// </summary>
public partial class LlvmEmitter
{
    /// <summary>
    /// Emit routine call as part of this builder phase.
    /// </summary>
    private string EmitRoutineCall(StringBuilder sb, RoutineCallRequest req)
    {
        (string functionName, List<Expression> arguments, RoutineInfo? resolvedRoutine,
            TypeSymbol? resolvedReturnType, List<TypeExpression>? typeArguments) = req;

        // The failable `!` is a structured flag on the request; the FunctionName is bare.
        bool isFailableCallSyntax = req.IsFailable;

        string? intrinsicCall = TryEmitRecoveredFreeIntrinsicCall(sb: sb,
            functionName: functionName,
            resolvedRoutine: resolvedRoutine,
            arguments: arguments,
            typeArguments: typeArguments,
            resolvedReturnType: resolvedReturnType);
        if (intrinsicCall != null)
        {
            return intrinsicCall;
        }

        // Indirect call through a local Routine-typed variable (e.g., compare(a: x, b: y) where
        // 'compare' is a parameter of type Routine[(T, T), Bool]). The variable holds the fat Routine
        // value `{ ptr fn, ptr bound }` (v0.4.1): load it and dispatch through EmitFatRoutineIndirectCall,
        // which branches on `bound == null` (captureless `fn(args)` vs capturing `fn(args, bound)`).
        if (_localVariables.TryGetValue(key: functionName, value: out TypeSymbol? localType) &&
            localType is RoutineTypeSymbol routineTypeInfo)
        {
            string llvmName =
                _localVarLlvmNames.GetValueOrDefault(key: functionName,
                    defaultValue: functionName);
            string fatVal = NextTemp();
            EmitLine(sb: sb, line: $"  {fatVal} = load {{ ptr, ptr }}, ptr %{llvmName}.addr");
            return EmitFatRoutineIndirectCall(sb: sb,
                fatVal: fatVal,
                routineType: routineTypeInfo,
                arguments: arguments);
        }

        // The routine the analyzer or monomorphization stamped. A construction that binds none was written
        // as a field-by-field creator expression or a cast (ConstructionLoweringPass).
        RoutineInfo routine = ResolveInitialFreeCallRoutine(resolvedRoutine: resolvedRoutine,
                                  typeArguments: typeArguments) ??
                              throw new InvalidOperationException(
                                  message: $"Call to '{functionName}' reached the LLVM emitter without a resolved " +
                                           $"routine in [{_currentRoutineDiagName}].");

        // RazorForge evaluates arguments in PARAMETER-DECLARATION order regardless of the call-site writing
        // order: each written argument binds to its declared slot (by name, else positionally) and the slots
        // are emitted in declaration order.
        var argValues = new List<string>();
        var argTypes = new List<string>();
        var argTypeInfos = new List<TypeSymbol>();
        EmitFreeCallArgumentsInDeclarationOrder(sb: sb,
            routine: routine,
            functionName: functionName,
            arguments: arguments,
            argValues: argValues,
            argTypes: argTypes,
            argTypeInfos: argTypeInfos);

        return EmitFreeCallInstruction(sb: sb,
            arguments: arguments,
            routine: routine,
            functionName: functionName,
            isFailableCallSyntax: isFailableCallSyntax,
            argValues: argValues,
            argTypes: argTypes);
    }

    /// <summary>
    /// Binds each written argument of a resolved free call to its declared parameter slot (named by
    /// name, else positionally) and emits the arguments in PARAMETER-DECLARATION order — evaluating
    /// each provided argument (with FFI C-function-pointer and coercion handling) or a default,
    /// filling <paramref name="argValues"/> / <paramref name="argTypes"/> / <paramref name="argTypeInfos"/>.
    /// </summary>
    private void EmitFreeCallArgumentsInDeclarationOrder(StringBuilder sb, RoutineInfo routine,
        string functionName, List<Expression> arguments, List<string> argValues,
        List<string> argTypes, List<TypeSymbol> argTypeInfos)
    {
        int paramCount = routine.Parameters.Count;

        // Bind each written argument to its declared parameter slot (named by name, else by position).
        Expression?[] slotArg = BindFreeCallArgumentsToSlots(routine: routine,
            arguments: arguments,
            paramCount: paramCount);

        // Emit slot-by-slot in declaration order: provided argument (evaluated here) or default.
        for (int p = 0; p < paramCount; p++)
        {
            ParamInfo param = routine.Parameters[index: p];
            Expression? bound = slotArg[p];
            if (bound != null)
            {
                EmitBoundFreeCallArgument(sb: sb,
                    bound: bound,
                    param: param,
                    callCtx: new FreeCallRoutineContext(Routine: routine,
                        FunctionName: functionName),
                    argValues: argValues,
                    argTypes: argTypes,
                    argTypeInfos: argTypeInfos);
            }
            else
            {
                // Semantic analysis writes every left-out default into the call (AppendDefaultArguments).
                throw new InvalidOperationException(
                    message: $"Call to '{functionName}' reached codegen without argument '{param.Name}'.");
            }
        }
    }

    /// <summary>The argument bound to each parameter slot (<c>CallArgumentOrderPass</c> listed them in
    /// parameter order), or null when no argument was supplied.</summary>
    private static Expression?[] BindFreeCallArgumentsToSlots(RoutineInfo routine,
        List<Expression> arguments, int paramCount)
    {
        var slotArg = new Expression?[paramCount];
        for (int p = 0; p < paramCount; p++)
        {
            slotArg[p] = CallArgumentOrderPass.ArgumentInSlot(arguments: arguments, routine: routine, slot: p);
        }

        return slotArg;
    }

    private readonly record struct FreeCallRoutineContext(
        RoutineInfo Routine,
        string FunctionName);

    /// <summary>
    /// Emits a single bound argument for a free call — handling FFI function-pointer, fat-Routine
    /// value, and normal coercion paths — and appends the result to the arg lists.
    /// </summary>
    private void EmitBoundFreeCallArgument(StringBuilder sb, Expression bound, ParamInfo param,
        FreeCallRoutineContext callCtx, List<string> argValues, List<string> argTypes,
        List<TypeSymbol> argTypeInfos)
    {
        RoutineInfo routine = callCtx.Routine;
        string functionName = callCtx.FunctionName;
        Expression argInner = bound is NamedArgumentExpression nb
            ? nb.Value
            : bound;
        bool paramTakesCFnPtr = param.Type?.Name == "CPtr" ||
                                routine.IsForeign && param.Type is RoutineTypeSymbol;

        // By-reference parameter: pass the argument's address, so the routine works on that storage.
        if (param.IsByReference)
        {
            argValues.Add(item: EmitLvalueAddress(sb: sb, expr: argInner));
            argTypeInfos.Add(item: param.Type);
            argTypes.Add(item: "ptr");
            return;
        }

        // FFI routine argument: a routine name (resolved by semantic analysis) at a CPtr/Routine param
        // → pass the C-ABI symbol.
        if (paramTakesCFnPtr && argInner is IdentifierExpression { ResolvedRoutine: { } refRoutine } &&
            param.Type is not null)
        {
            GenerateRoutineDeclaration(routine: refRoutine);
            argValues.Add(item: $"@{MangleRoutineName(routine: refRoutine)}");
            argTypeInfos.Add(item: param.Type);
            argTypes.Add(item: "ptr");
            return;
        }

        // FFI Routine VALUE argument: guard capturing-ness at runtime.
        if (paramTakesCFnPtr && GetExpressionType(expr: argInner) is RoutineTypeSymbol &&
            param.Type is not null)
        {
            string fnArg = EmitForeignRoutineValueArg(sb: sb, valueExpr: argInner);
            argValues.Add(item: fnArg);
            argTypeInfos.Add(item: param.Type);
            argTypes.Add(item: "ptr");
            return;
        }

        string value = EmitExpression(sb: sb, expr: bound);
        TypeSymbol? argType = GetExpressionType(expr: bound);
        if (argType == null)
        {
            throw new InvalidOperationException(
                message:
                $"Cannot determine type for argument in function call to '{functionName}'");
        }

        (string coercedValue, string coercedType) = CoerceCallArgumentToParameter(sb: sb,
            argValue: value,
            actualType: argType,
            parameterType: param.Type ?? argType,
            callee: routine);
        argValues.Add(item: coercedValue);
        argTypes.Add(item: coercedType);
        argTypeInfos.Add(item: argType);
    }

    /// <summary>
    /// Generates code for a memberRoutine call on an object.
    /// The object becomes the implicit 'me' parameter.
    /// </summary>
    private string EmitMemberRoutineCall(StringBuilder sb, MemberExpression member,
        List<Expression> arguments, RoutineInfo? resolvedRoutine = null,
        List<TypeExpression>? typeArguments = null,
        CallLoweringKind loweringKind = CallLoweringKind.Unknown)
    {
        // Synthesized bodies (e.g. hash, eq, cmp) are built programmatically and never
        // pass through SemanticVerifier, so they arrive with Unknown. Treat as DirectMemberRoutine.
        if (loweringKind == CallLoweringKind.Unknown)
        {
            loweringKind = CallLoweringKind.DirectMemberRoutine;
        }

        // Dynamic call through a callable FIELD on the receiver (e.g. `me.predicate(item)` in
        // a stdlib iterator emitter, where `predicate` is a `secret predicate: Routine[(T,), Bool]`
        // field). SA classifies these as DynamicCall. There is no memberRoutine named `predicate`. load
        // the stored function pointer from the field and call it indirectly — mirroring the
        // free-call indirect path for Routine-typed locals/params (see EmitRoutineCall).
        // SA also stamps DynamicCall on its generic fallback for calls it couldn't resolve to a
        // concrete routine (e.g. a call to an ordinary memberRoutine returning S64 on a generic receiver).
        // Those are NOT field invocations, so only take this path when the member is genuinely a
        // Routine-typed value — otherwise fall through to normal memberRoutine resolution.
        if (loweringKind == CallLoweringKind.DynamicCall &&
            (member.ResolvedType ?? GetMemberType(member: member)) is RoutineTypeSymbol)
        {
            return EmitDynamicMemberFieldCall(sb: sb, member: member, arguments: arguments);
        }

        // The cycle-collector hook intrinsics `<entity>.roam_trace_ref()` / `.roam_free_ref()` are
        // lowered to an explicit routine-VALUE reference (an IdentifierExpression with a stamped
        // ResolvedRoutine) by RoamHookRefLoweringPass, which runs post-monomorphization when the
        // concrete entity type is known. Codegen therefore never sees the `roam_*_ref` member call —
        // it materializes the closure through the pre-resolved-routine path in EmitIdentifier, with no
        // LookupMemberRoutine of its own.

        (string receiver, TypeSymbol? receiverType) =
            ResolveMemberRoutineCallReceiver(sb: sb, member: member);

        receiverType = NormalizeMemberReceiverType(member: member, receiverType: receiverType);

        // Transparent protocol (e.g., Accessing[Text] with no declared memberRoutines): dispatch through
        // the first concrete type argument T. Both representations are ptr in LLVM, so no cast needed.
        if (receiverType is ProtocolTypeSymbol
            {
                MemberRoutines.Count: 0, TypeArguments.Count: > 0
            } transparentProto)
        {
            receiverType = transparentProto.TypeArguments![index: 0];
        }

        string memberRoutineName = member.MemberName;

        // Use the SA/upstream-stamped routine when present; otherwise the signature-based overload path
        // below (memberRoutine == null branch) resolves it by (name, arg types). No premature name-only lookup.
        RoutineInfo? memberRoutine = resolvedRoutine;

        // A member conversion (`x.U64()`) arrives as the construction `U64(from: x)` (ExpressionLoweringPass).
        if (loweringKind == CallLoweringKind.TypeConstructor && memberRoutine != null)
        {
            throw new InvalidOperationException(
                message: $"Member conversion '.{member.MemberName}()' reached codegen unlowered.");
        }

        // A forwarded entity routine on a Consulting/Amending token already has the entity as its
        // receiver (WrapperProjectionLoweringPass).

        // Suflae `Roamed[E]` receiver transparency is now lowered to real AST nodes by
        // RoamedProjectionLoweringPass (Phase 8): a bare-`me` inner memberRoutine's receiver is rewritten to
        // `receiver.raw_inner()` and a wrapper-shadowed represent/diagnose is re-resolved to the
        // inner's routine (stamped on CallExpression.ResolvedRoutine). Codegen emits that call verbatim
        // — no projection or re-resolution here.

        // Member-conversions (`obj.Text()`, `index.U64!()`) are handled above via the
        // TypeConstructor intercept using the SA-stamped `create`. Any DirectMemberRoutine that
        // still reaches here with no resolved memberRoutine is a semantic-verifier contract violation.
        // SA contract: a member call either resolves to a concrete routine (stamped on the call)
        // or is rejected (RF-S458 for `.field()` typos, the dynamic-field `ptr` closure call is
        // classified DynamicCall and handled above). A non-null resolvedRoutine that codegen can't
        // re-find is a registry bug, not a fallback to paper over. The former zero-arg field-read
        // fallback ("`obj.field()` means read the field") was removed: `.field` (access) and
        // `.field()` (call) are distinct forms, so calling a data member is now an SA error, not a
        // silent field read (task #23 — codegen emits the resolved routine, it does not rediscover
        // intent).
        AssertMemberRoutineReachable(member: member, memberRoutine: memberRoutine,
            resolvedRoutine: resolvedRoutine, loweringKind: loweringKind,
            receiverType: receiverType, currentRoutineDiagName: _currentRoutineDiagName);

        // Build argument list: receiver first, then explicit arguments.
        // Skip the receiver for routines that don't take an implicit `me`:
        //   - creators (`create`) — owner-scoped but no receiver in the param list
        //   - common routines — explicitly declared without `me`
        // Prepending a phantom receiver for these shifts every actual argument by one
        // slot in the LLVM call, corrupting all reads (e.g. Moment.create(year:2026,...)
        // saw year=zeroinitializer-cast and emitted timestamps in the wrong century).
        bool memberRoutineTakesReceiver = InitializeMemberCallArgLists(receiver: receiver,
            receiverType: receiverType, memberRoutine: memberRoutine,
            argValues: out List<string> argValues, argTypes: out List<string> argTypes,
            argTypeInfos: out List<TypeSymbol> argTypeInfos);

        // Collect explicit argument TYPES in writing order (for overload resolution below). The
        // VALUES are emitted later, in parameter-declaration order, so member-call arguments
        // evaluate in declaration order — matching free routines — regardless of the call-site
        // writing order. argValues/argTypes hold only the receiver for now; the reordered slot loop
        // (or the unresolved-memberRoutine fallback) rebuilds them.
        // Synthesized/lowered bodies (programmatic eq/cmp/hash, operator-lowered calls) never
        // pass through SemanticVerifier, so they arrive without a stamped ResolvedRoutine. Once the
        // concrete argument types are known, resolve the exact overload here so failable operators
        // like add!/sub! do not degrade to undecorated placeholder symbols (Core.S32.add). This
        // is resolution for SA-bypassing bodies, NOT intent-rediscovery on user calls — every
        // SA-analyzed member call is already stamped or rejected (RF-S458). The former bare
        // `LookupMemberRoutine(name)` that resolved a non-failable name to its failable variant was
        // removed: that failability-masking is now an SA error (`obj.foo()` when only `foo!`
        // exists), so codegen no longer needs to paper over it (task #23).
        memberRoutine = FillArgTypeInfosAndNormalizeRoutine(memberRoutineName: memberRoutineName,
            arguments: arguments, argTypeInfos: argTypeInfos,
            memberRoutineTakesReceiver: memberRoutineTakesReceiver,
            receiverType: receiverType, memberRoutine: memberRoutine);

        // Last-chance: memberRoutine-generic on a concrete owner (e.g., Array[T,N].getitem[I]).
        // Neither OLP nor GenericAstRewriter may have resolved it; infer I from the actual
        // call-site argument types and request monomorphization now.
        // codegen NEVER infers method-generic type arguments from the call's argument types and
        // monomorphizes on the fly — that is upstream's job (GenericMonomorphizationPass / the demand
        // collector). A generic member routine reaching here WITHOUT explicit `[...]` type arguments (which
        // the block far below still instantiates) is an upstream resolution gap → hard error, never inference.
        ValidateGenericMemberRoutineResolved(receiverType: receiverType,
            memberRoutineName: memberRoutineName, typeArguments: typeArguments,
            memberRoutine: memberRoutine);

        // LLVM intrinsic template memberRoutine call (e.g., buf.read![U8](offset)) — emits its own
        // arguments (and reorders named args internally), so it bypasses the deferred slot loop
        // below. Checked here, after the memberRoutine is fully resolved, so the slot loop never emits its
        // arguments a second time.
        if (memberRoutine?.LlvmIrTemplate != null)
        {
            return EmitLlvmIntrinsicCall(sb: sb,
                routine: memberRoutine,
                receiver: receiver,
                arguments: arguments,
                typeArguments: typeArguments,
                resolvedReturnType: member.ResolvedType);
        }

        // Emit explicit arguments in PARAMETER-DECLARATION order, supplying defaults for any
        // unprovided slot (same contract as EmitRoutineCall's free-function fill — SA only
        // VALIDATES that unbound parameters have defaults; materializing them is codegen's job).
        // Named arguments may be written out of order or may skip middle parameters that have
        // defaults; binding each value to its declared slot fixes the silent miscompile of
        // reordered named calls (e.g. `k.sub3(c:1, a:100, b:10)`) and the misalignment of
        // middle-omitted named defaults. Because emission happens HERE, in declaration order, the
        // arguments' side effects also run in declaration order (matching free routines). The
        // receiver (if present) stays at index 0.
        if (memberRoutine is { IsGenericDefinition: false })
        {
            (argValues, argTypes, argTypeInfos) = EmitMemberCallArgumentsInDeclarationOrder(sb: sb,
                member: member,
                memberRoutine: memberRoutine,
                arguments: arguments,
                argCtx: new MemberCallArgContext(TakesReceiver: memberRoutineTakesReceiver,
                    Values: argValues,
                    Types: argTypes,
                    TypeInfos: argTypeInfos));
        }
        else
        {
            EmitMemberCallArgumentsInWritingOrder(sb: sb,
                member: member,
                arguments: arguments,
                argValues: argValues,
                argTypes: argTypes);
        }

        // Build the call -> for resolved generic types (e.g., List[Character].add_last),
        // use the resolved type name even if the memberRoutine was found via the base type
        string mangledName;
        ResolveMemberCallSymbol(member: member,
            resolvedRoutine: resolvedRoutine,
            typeArguments: typeArguments,
            loweringKind: loweringKind,
            receiverType: receiverType,
            memberRoutine: ref memberRoutine,
            mangledName: out mangledName);

        // Ensure the memberRoutine is declared (so the multi-pass stdlib loop can compile its body)
        // Skip for protocol-owned memberRoutines -> they can't be declared with protocol types in LLVM IR
        // the monomorphized version (with concrete receiver type) will generate its own declaration.
        // Use the semantic-layer-resolved return type.
        // Universal memberRoutine (OwnerType = GenericParameterTypeSymbol "T"): substitute T -> receiverType
        // BEFORE applying outer _typeSubstitutions -> the outer context may map T to something else
        // (e.g., T -> S64 in add_first[T=S64]), which would corrupt the universal T in Retained[T].
        // For resolved generic memberRoutines, also emit a declaration with the resolved name.
        TypeSymbol? resolvedReturnType = ResolveReturnTypeAndDeclareSymbol(memberRoutine: memberRoutine,
            mangledName: mangledName, receiverType: receiverType, argTypes: argTypes);

        return EmitMemberRoutineCallInstruction(sb: sb,
            arguments: arguments,
            spec: new MemberCallSpec(MemberRoutine: memberRoutine,
                MemberRoutineTakesReceiver: memberRoutineTakesReceiver,
                ResolvedReturnType: resolvedReturnType,
                MangledName: mangledName),
            argValues: argValues,
            argTypes: argTypes,
            argTypeInfos: argTypeInfos);
    }

    private readonly record struct MemberCallSpec(
        RoutineInfo? MemberRoutine,
        bool MemberRoutineTakesReceiver,
        TypeSymbol? ResolvedReturnType,
        string MangledName);

    private readonly record struct FreeCallSpec(
        string MangledName,
        string ReturnType,
        string CallReturnType,
        bool IsCExtern);

    /// <summary>
    /// Applies ABI coercions (byval / register) to the explicit arguments and emits the final
    /// LLVM call instruction for a member routine — handling sret, coerced struct returns, void,
    /// and normal value returns. Extracted from <c>EmitMemberRoutineCall</c> to reduce complexity.
    /// </summary>
    private string EmitMemberRoutineCallInstruction(StringBuilder sb, List<Expression> arguments,
        MemberCallSpec spec, List<string> argValues, List<string> argTypes,
        List<TypeSymbol> argTypeInfos)
    {
        (RoutineInfo? memberRoutine, bool memberRoutineTakesReceiver, TypeSymbol? resolvedReturnType,
            string mangledName) = spec;

        CoerceMemberCallArguments(sb: sb,
            memberRoutine: memberRoutine,
            memberRoutineTakesReceiver: memberRoutineTakesReceiver,
            argValues: argValues,
            argTypes: argTypes,
            argTypeInfos: argTypeInfos);

        string returnType = resolvedReturnType != null
            ? GetLlvmType(type: resolvedReturnType)
            : "void";

        // ABI-Indirect struct return via a hidden sret pointer.
        TypeSymbol? sretOverride = memberRoutine?.OwnerType is GenericParameterTypeSymbol
            ? resolvedReturnType
            : null;
        if (memberRoutine != null &&
            ReturnsViaSret(routine: memberRoutine, overrideReturnType: sretOverride))
        {
            string sretPtr = NextTemp();
            EmitEntryAlloca(llvmName: sretPtr, llvmType: returnType);
            argTypes.Insert(index: 0, item: $"ptr sret({returnType})");
            argValues.Insert(index: 0, item: sretPtr);
            string sretArgs = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({sretArgs})");
            ConsumeTransferredCallOwnership(sb: sb, arguments: arguments);
            string sretResult = NextTemp();
            EmitLine(sb: sb, line: $"  {sretResult} = load {returnType}, ptr {sretPtr}");
            return sretResult;
        }

        // Coerced (Phase 2) struct return: call as the ABI integer form, reinterpret back to struct.
        string? memberRoutineCoerce = memberRoutine != null
            ? ReturnCoerceType(routine: memberRoutine)
            : null;
        if (memberRoutineCoerce != null)
        {
            string args = BuildCallArgs(types: argTypes, values: argValues);
            string r = NextTemp();
            EmitLine(sb: sb, line: $"  {r} = call {memberRoutineCoerce} @{mangledName}({args})");
            ConsumeTransferredCallOwnership(sb: sb, arguments: arguments);
            return CoerceAbiToStruct(sb: sb,
                abiValue: r,
                abiType: memberRoutineCoerce,
                structLlvm: returnType);
        }

        if (returnType == "void")
        {
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({args})");
            ConsumeTransferredCallOwnership(sb: sb, arguments: arguments);
            return "undef";
        }
        else
        {
            string result = NextTemp();
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  {result} = call {returnType} @{mangledName}({args})");
            ConsumeTransferredCallOwnership(sb: sb, arguments: arguments);
            return result;
        }
    }

    // ── Helpers extracted from EmitMemberRoutineCall to keep cognitive complexity ≤ 15 ────────────

    /// <summary>
    /// Throws when a member routine that must be resolved by the semantic verifier is missing.
    /// Covers two contract violations: a DirectMemberRoutine call with no resolved routine, and a
    /// SA-stamped routine that codegen can no longer locate in the registry.
    /// </summary>
    private static void AssertMemberRoutineReachable(MemberExpression member,
        RoutineInfo? memberRoutine, RoutineInfo? resolvedRoutine,
        CallLoweringKind loweringKind, TypeSymbol receiverType, string? currentRoutineDiagName)
    {
        if (memberRoutine == null && loweringKind is CallLoweringKind.DirectMemberRoutine)
        {
            throw new InvalidOperationException(
                message:
                $"member routine call .{member.MemberName} on {receiverType.FullName} reached codegen " +
                $"with loweringKind={loweringKind} but no resolved member routine [enclosing={currentRoutineDiagName}]. Semantic verifier" +
                $" must resolve this.");
        }

        if (memberRoutine == null && resolvedRoutine != null)
        {
            throw new InvalidOperationException(
                message:
                $"SA-resolved routine '{resolvedRoutine.RegistryKey}' could not be located as a " +
                $"member routine on {receiverType.FullName}.{member.MemberName} during codegen.");
        }
    }

    /// <summary>
    /// Initialises the three parallel argument accumulation lists for a member routine call,
    /// prepending the receiver to each when the routine takes an implicit <c>me</c>.
    /// Returns true when the routine takes a receiver (i.e. is neither common nor a creator).
    /// </summary>
    private bool InitializeMemberCallArgLists(string receiver, TypeSymbol receiverType,
        RoutineInfo? memberRoutine, out List<string> argValues, out List<string> argTypes,
        out List<TypeSymbol> argTypeInfos)
    {
        bool takesReceiver = !(memberRoutine?.IsCommon == true || memberRoutine?.IsCreator == true);
        argValues = takesReceiver ? new List<string> { receiver } : new List<string>();
        string receiverLlvmType = ReceiverPassedByRef(receiverType: receiverType)
            ? "ptr"
            : GetParameterLlvmType(type: receiverType);
        argTypes = takesReceiver ? new List<string> { receiverLlvmType } : new List<string>();
        argTypeInfos = takesReceiver ? new List<TypeSymbol> { receiverType } : new List<TypeSymbol>();
        return takesReceiver;
    }

    /// <summary>
    /// Appends each explicit argument's type to <paramref name="argTypeInfos"/>, then resolves
    /// the member routine overload (when none was stamped upstream) and normalises the reference.
    /// </summary>
    private RoutineInfo? FillArgTypeInfosAndNormalizeRoutine(string memberRoutineName,
        List<Expression> arguments, List<TypeSymbol> argTypeInfos, bool memberRoutineTakesReceiver,
        TypeSymbol receiverType, RoutineInfo? memberRoutine)
    {
        foreach (Expression arg in arguments)
        {
            TypeSymbol? argType = GetExpressionType(expr: arg);
            if (argType == null)
            {
                throw new InvalidOperationException(
                    message:
                    $"Cannot determine type for argument in member routine call to '{memberRoutineName}'");
            }

            argTypeInfos.Add(item: argType);
        }

        int receiverSkip = memberRoutineTakesReceiver ? 1 : 0;
        if (memberRoutine == null)
        {
            // The analyzer or the lowering pass that built this call resolves it; the emitter does not.
            throw new InvalidOperationException(
                message:
                $"Member call '{receiverType.FullName}.{memberRoutineName}' reached the LLVM emitter unresolved " +
                $"in [{_currentRoutineDiagName}]. The pass that built it must stamp ResolvedRoutine.");
        }

        return NormalizeResolvedRoutineReference(routine: memberRoutine,
            receiverType: receiverType,
            argTypes: argTypeInfos.Skip(count: receiverSkip).ToList());
    }

    /// <summary>
    /// Validates that a method-generic member routine has been monomorphized upstream before
    /// reaching codegen. Codegen is a never-fail translator and never infers type arguments.
    /// </summary>
    private static void ValidateGenericMemberRoutineResolved(TypeSymbol receiverType,
        string memberRoutineName, List<TypeExpression>? typeArguments, RoutineInfo? memberRoutine)
    {
        RoutineInfo? genericMemberRoutineForInference = memberRoutine switch
        {
            { IsGenericDefinition: true, GenericParameters.Count: > 0 } genericDefMemberRoutine =>
                genericDefMemberRoutine,
            { GenericDefinition: { GenericParameters.Count: > 0 } genericDefinition } when
                RoutineHasUnresolvedTypeArguments(routine: memberRoutine) => genericDefinition,
            _ => null
        };

        if (genericMemberRoutineForInference is
                { OwnerType: not (null or GenericParameterTypeSymbol or ProtocolTypeSymbol) } &&
            !genericMemberRoutineForInference.OwnerType.IsGenericDefinition &&
            typeArguments is not { Count: > 0 })
        {
            throw new InvalidOperationException(
                message:
                $"Generic member routine '{receiverType.FullName}.{memberRoutineName}' reached codegen " +
                "unresolved (no explicit type arguments) — it must be monomorphized upstream. codegen is a " +
                "never-fail translator; it does not infer generic type arguments.");
        }
    }

    /// <summary>
    /// Emits a <c>GenerateRoutineDeclaration</c> for the routine (when not protocol-owned), resolves
    /// the return type (applying generic-param substitution for universal receivers), and emits a
    /// declaration for the mangled name when it has not been generated yet.
    /// </summary>
    private TypeSymbol? ResolveReturnTypeAndDeclareSymbol(RoutineInfo? memberRoutine,
        string mangledName, TypeSymbol receiverType, List<string> argTypes)
    {
        if (memberRoutine is { OwnerType: not ProtocolTypeSymbol })
        {
            GenerateRoutineDeclaration(routine: memberRoutine);
        }

        TypeSymbol? resolvedReturnType = memberRoutine?.ReturnType;
        if (resolvedReturnType != null)
        {
            if (memberRoutine?.OwnerType is GenericParameterTypeSymbol universalOwnerParam)
            {
                resolvedReturnType = SubstituteGenericParamInType(type: resolvedReturnType,
                    paramName: universalOwnerParam.Name,
                    concreteType: receiverType);
            }
            else
            {
                resolvedReturnType = ApplyTypeSubstitutions(type: resolvedReturnType);
            }
        }

        if (!_generatedRoutines.Contains(item: mangledName))
        {
            if (memberRoutine != null)
            {
                GenerateRoutineDeclaration(routine: memberRoutine, nameOverride: mangledName);
            }
            else
            {
                string retType = resolvedReturnType != null
                    ? GetLlvmType(type: resolvedReturnType)
                    : "void";
                _rfRoutineDeclarations[key: mangledName] =
                    $"declare {retType} @{mangledName}({string.Join(separator: ", ", values: argTypes)})";
                _generatedRoutines.Add(item: mangledName);
            }
        }

        return resolvedReturnType;
    }

    // ── Helpers extracted from EmitFreeCallInstruction to keep cognitive complexity ≤ 15 ──────────

    /// <summary>
    /// Iterates the argument lists in place and bitcasts any <c>half</c> (B16) argument to
    /// <c>i16</c>, as required by the C ABI on all supported targets.
    /// </summary>
    private void CoerceCExternB16Arguments(StringBuilder sb, List<string> argValues,
        List<string> argTypes)
    {
        for (int i = 0; i < argTypes.Count; i++)
        {
            if (argTypes[index: i] == "half")
            {
                string bits = NextTemp();
                EmitLine(sb: sb, line: $"  {bits} = bitcast half {argValues[index: i]} to i16");
                argValues[index: i] = bits;
                argTypes[index: i] = "i16";
            }
        }
    }

    /// <summary>
    /// Emits the final LLVM call instruction for a free-function call and returns the result
    /// temporary. Handles all four return paths: sret (ABI-indirect struct), coerced struct
    /// (Phase-2 ABI integer), void, and normal value (including C-extern B16 bitcast round-trip).
    /// </summary>
    private string EmitFreeCallAndGetResult(StringBuilder sb, List<Expression> arguments,
        RoutineInfo? routine, FreeCallSpec spec, List<string> argTypes, List<string> argValues)
    {
        (string mangledName, string returnType, string callReturnType, bool isCExtern) = spec;
        bool needsSret = routine != null && (isCExtern
            ? NeedsCExternSret(routine: routine)
            : ReturnsViaSret(routine: routine));
        if (needsSret)
        {
            string sretPtr = NextTemp();
            EmitEntryAlloca(llvmName: sretPtr, llvmType: returnType);
            argTypes.Insert(index: 0, item: $"ptr sret({returnType})");
            argValues.Insert(index: 0, item: sretPtr);
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({args})");
            ConsumeTransferredCallOwnership(sb: sb, arguments: arguments);
            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = load {returnType}, ptr {sretPtr}");
            return result;
        }

        string? calleeCoerce = routine != null && !isCExtern
            ? ReturnCoerceType(routine: routine)
            : null;
        if (calleeCoerce != null)
        {
            string result = NextTemp();
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  {result} = call {calleeCoerce} @{mangledName}({args})");
            ConsumeTransferredCallOwnership(sb: sb, arguments: arguments);
            return CoerceAbiToStruct(sb: sb,
                abiValue: result,
                abiType: calleeCoerce,
                structLlvm: returnType);
        }

        if (callReturnType == "void")
        {
            string args = BuildCallArgs(types: argTypes, values: argValues);
            EmitLine(sb: sb, line: $"  call void @{mangledName}({args})");
            ConsumeTransferredCallOwnership(sb: sb, arguments: arguments);
            return "undef";
        }

        string callResult = NextTemp();
        string argsStr = BuildCallArgs(types: argTypes, values: argValues);
        EmitLine(sb: sb, line: $"  {callResult} = call {callReturnType} @{mangledName}({argsStr})");
        ConsumeTransferredCallOwnership(sb: sb, arguments: arguments);
        if (isCExtern && returnType == "half" && callReturnType == "i16")
        {
            string halfResult = NextTemp();
            EmitLine(sb: sb, line: $"  {halfResult} = bitcast i16 {callResult} to half");
            return halfResult;
        }

        return callResult;
    }

    // ── Helper extracted from ResolveMemberCallSymbol to keep cognitive complexity ≤ 15 ──────────

    /// <summary>
    /// Requires an explicitly type-argumented member call to arrive already monomorphized: the
    /// instantiation happens before the emitter, which only checks it.
    /// </summary>
    private static void RequireConcreteMemberRoutine(TypeSymbol receiverType, MemberExpression member,
        RoutineInfo memberRoutine)
    {
        if (memberRoutine.IsGenericDefinition)
        {
            throw new InvalidOperationException(
                message:
                $"Explicit member routine generic call '{receiverType.FullName}.{member.MemberName}' reached LLVM codegen unresolved.");
        }
    }

    /// <summary>
    /// The written-argument index bound to each parameter slot (<c>CallArgumentOrderPass</c> listed them in
    /// parameter order), or -1 when no argument was provided for that slot.
    /// </summary>
    private static int[] BindArgumentsToParameterSlots(RoutineInfo memberRoutine,
        List<Expression> arguments, int paramCount)
    {
        int[] slotArgIndex = new int[paramCount];
        for (int p = 0; p < paramCount; p++)
        {
            slotArgIndex[p] = CallArgumentOrderPass.ArgumentInSlot(arguments: arguments,
                routine: memberRoutine,
                slot: p) is null
                ? -1
                : p;
        }

        return slotArgIndex;
    }

    /// <summary>
    /// Binds each written explicit member-call argument to its declared slot (named by name, else
    /// positionally) and emits the arguments in PARAMETER-DECLARATION order — evaluating each provided
    /// argument or a default — producing fresh (values, types, typeInfos) lists (with the receiver, if
    /// present, kept at index 0). Used when the resolved member routine is concrete.
    /// </summary>
    private (List<string> Values, List<string> Types, List<TypeSymbol> TypeInfos)
        EmitMemberCallArgumentsInDeclarationOrder(StringBuilder sb, MemberExpression member,
            RoutineInfo memberRoutine, List<Expression> arguments, MemberCallArgContext argCtx)
    {
        int paramCount = memberRoutine.Parameters.Count;

        // Bind each written explicit argument to its declared parameter slot.
        int[] slotArgIndex = BindArgumentsToParameterSlots(memberRoutine: memberRoutine,
            arguments: arguments,
            paramCount: paramCount);

        var reorderedValues = new List<string>();
        var reorderedTypes = new List<string>();
        var reorderedTypeInfos = new List<TypeSymbol>();
        if (argCtx.TakesReceiver)
        {
            reorderedValues.Add(item: argCtx.Values[index: 0]);
            reorderedTypes.Add(item: argCtx.Types[index: 0]);
            reorderedTypeInfos.Add(item: argCtx.TypeInfos[index: 0]);
        }

        for (int p = 0; p < paramCount; p++)
        {
            ParamInfo param = memberRoutine.Parameters[index: p];
            int boundArg = slotArgIndex[p];
            if (boundArg >= 0)
            {
                // Emit the bound argument HERE (in declaration order) so its side effects run
                // in declaration order.
                Expression boundExpr = arguments[index: boundArg];
                string boundValue = EmitExpression(sb: sb, expr: boundExpr);
                TypeSymbol? boundType = GetExpressionType(expr: boundExpr);
                if (boundType == null)
                {
                    throw new InvalidOperationException(
                        message:
                        $"Cannot determine type for argument in member routine call to '{member.MemberName}'");
                }

                reorderedValues.Add(item: boundValue);
                reorderedTypes.Add(item: GetLlvmType(type: boundType));
                reorderedTypeInfos.Add(item: boundType);
                continue;
            }

            // Semantic analysis writes every left-out default into the call (AppendDefaultArguments).
            throw new InvalidOperationException(
                message: $"Member routine call '{member.MemberName}' reached codegen without argument '{param.Name}'.");
        }

        return (reorderedValues, reorderedTypes, reorderedTypeInfos);
    }

    /// <summary>
    /// Emits a member call's explicit arguments in WRITING order — for an unresolved or still-generic
    /// member routine (no parameter list to bind against, or a synthesized/operator body with
    /// positional args). Appends to <paramref name="argValues"/> / <paramref name="argTypes"/>.
    /// </summary>
    private void EmitMemberCallArgumentsInWritingOrder(StringBuilder sb, MemberExpression member,
        List<Expression> arguments, List<string> argValues, List<string> argTypes)
    {
        // memberRoutine unresolved or still a generic definition — the declaration-order slot loop
        // doesn't apply (no parameter list to bind against, or this is a synthesized/operator
        // body with positional args). Emit explicit arguments in writing order so the call (or
        // the error path below) has its values. (argTypeInfos already holds their types.)
        foreach (Expression arg in arguments)
        {
            string value = EmitExpression(sb: sb, expr: arg);
            TypeSymbol? argType = GetExpressionType(expr: arg);
            if (argType == null)
            {
                throw new InvalidOperationException(
                    message:
                    $"Cannot determine type for argument in member routine call to '{member.MemberName}'");
            }

            argValues.Add(item: value);
            argTypes.Add(item: GetLlvmType(type: argType));
        }
    }

    /// <summary>
    /// Emits an indirect call through a callable FIELD on a receiver — the member form of
    /// EmitRoutineCall's local-variable indirect path. Used for stdlib iterator emitters that
    /// store a lambda in a field (e.g. <c>secret predicate: Routine[(T,), Bool]</c>) and invoke
    /// it as <c>me.predicate(item)</c>. Loads the function pointer from the field, then calls it
    /// indirectly with the supplied arguments.
    /// </summary>
    private string EmitDynamicMemberFieldCall(StringBuilder sb, MemberExpression member,
        List<Expression> arguments)
    {
        TypeSymbol? calleeType = member.ResolvedType ?? GetMemberType(member: member);
        if (calleeType is not RoutineTypeSymbol routineType)
        {
            throw new InvalidOperationException(
                message: $"DynamicCall on member '.{member.MemberName}' but the field's type is " +
                         $"'{calleeType?.FullName ?? "<null>"}', not a Routine type. " +
                         $"Routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} " +
                         $"(owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"}).");
        }

        // The field holds the fat Routine value `{ ptr fn, ptr bound }` (v0.4.1). Load it and dispatch
        // through EmitFatRoutineIndirectCall (branch on `bound == null`).
        string fatVal = EmitMemberVariableAccess(sb: sb, expr: member);
        return EmitFatRoutineIndirectCall(sb: sb,
            fatVal: fatVal,
            routineType: routineType,
            arguments: arguments);
    }

    /// <summary>
    /// Emits a Routine-typed VALUE argument at a C boundary (v0.4.1). Capturing-ness is not in the
    /// <c>Routine[T]</c> type (it is erased), so it is guarded at RUNTIME on the fat value's
    /// <c>bound</c> word: captureless (<c>bound == null</c>) hands C the bare 1-word <c>fn</c>; a
    /// capturing value crashes (a capture has no C slot — thread state through explicit userdata).
    /// Returns the SSA <c>ptr</c> (the <c>fn</c>) to pass as the C callback argument.
    /// </summary>
    private string EmitForeignRoutineValueArg(StringBuilder sb, Expression valueExpr)
    {
        string val = EmitExpression(sb: sb, expr: valueExpr);
        string fn = NextTemp();
        EmitLine(sb: sb, line: $"  {fn} = extractvalue {{ ptr, ptr }} {val}, 0");
        string bnd = NextTemp();
        EmitLine(sb: sb, line: $"  {bnd} = extractvalue {{ ptr, ptr }} {val}, 1");
        string isCap = NextTemp();
        EmitLine(sb: sb, line: $"  {isCap} = icmp ne ptr {bnd}, null");
        string lcap = NextLabel(prefix: "cbound.cap");
        string lok = NextLabel(prefix: "cbound.ok");
        EmitLine(sb: sb, line: $"  br i1 {isCap}, label %{lcap}, label %{lok}");
        EmitLine(sb: sb, line: $"{lcap}:");
        _rfRoutineDeclarations[key: "__rf_throw"] = "declare void @__rf_throw(ptr, ptr)";
        string errSym = EmitCStringConstant(value: "ForeignCallbackCaptureError");
        string msgSym =
            EmitCStringConstant(
                value:
                "A capturing routine cannot cross the C boundary; pass a captureless callback and thread " +
                "state through an explicit userdata parameter.");
        EmitLine(sb: sb, line: $"  call void @__rf_throw(ptr {errSym}, ptr {msgSym})");
        EmitLine(sb: sb, line: "  unreachable");
        EmitLine(sb: sb, line: $"{lok}:");
        return fn;
    }

    /// <summary>
    /// Emits an indirect call through a fat Routine value <c>{ ptr fn, ptr bound }</c> (v0.4.1).
    /// Extracts <c>fn</c> and <c>bound</c>, evaluates the explicit args once, then branches on
    /// <c>bound == null</c>: captureless calls <c>fn(args)</c>, capturing calls <c>fn(args, ptr bound)</c>
    /// (bound = C userdata, passed TRAILING). The call follows the same platform ABI as the routine's
    /// definition: an ABI-indirect result comes back through an sret slot, a small struct result in an
    /// integer register, and struct arguments are passed indirectly or register-coerced. Returns the result
    /// SSA, or <c>"undef"</c> for a void return.
    /// </summary>
    private string EmitFatRoutineIndirectCall(StringBuilder sb, string fatVal,
        RoutineTypeSymbol routineType, List<Expression> arguments)
    {
        string fn = NextTemp();
        EmitLine(sb: sb, line: $"  {fn} = extractvalue {{ ptr, ptr }} {fatVal}, 0");
        string bound = NextTemp();
        EmitLine(sb: sb, line: $"  {bound} = extractvalue {{ ptr, ptr }} {fatVal}, 1");

        // The signature the definition was emitted with, for the ABI decisions shared with direct calls.
        var signature = new RoutineInfo(name: "<routine value>")
        {
            Parameters = routineType.ParameterTypes
                                    .Select(selector: (t, i) => new ParamInfo(name: $"p{i}", type: t))
                                    .ToList(),
            ReturnType = routineType.ReturnType
        };

        // Evaluate the explicit arguments once; both branches reuse them.
        var argValues = new List<string>();
        var argTypes = new List<string>();
        for (int i = 0; i < arguments.Count; i++)
        {
            Expression arg = arguments[index: i];
            string v = EmitExpression(sb: sb, expr: arg);
            TypeSymbol? argType = GetExpressionType(expr: arg);
            if (argType != null && i < routineType.ParameterTypes.Count)
            {
                (string cv, string ct) = CoerceCallArgumentToParameter(sb: sb,
                    argValue: v,
                    actualType: argType,
                    parameterType: routineType.ParameterTypes[index: i],
                    callee: signature);
                argValues.Add(item: cv);
                argTypes.Add(item: ct);
                continue;
            }

            argValues.Add(item: v);
            argTypes.Add(item: argType != null
                ? GetParameterLlvmType(type: argType)
                : GetExpressionLlvmType(expr: arg));
        }

        string retLlvm = routineType.ReturnType != null
            ? GetLlvmType(type: routineType.ReturnType)
            : "void";
        string? sretSlot = null;
        if (routineType.ReturnType != null && ReturnsViaSret(routine: signature))
        {
            sretSlot = NextTemp();
            EmitEntryAlloca(llvmName: sretSlot, llvmType: retLlvm);
            argTypes.Insert(index: 0, item: $"ptr sret({retLlvm})");
            argValues.Insert(index: 0, item: sretSlot);
        }

        string? retCoerce = sretSlot == null && routineType.ReturnType != null
            ? ReturnCoerceType(routine: signature)
            : null;
        string callRet = sretSlot != null
            ? "void"
            : retCoerce ?? retLlvm;

        string baseArgs = BuildCallArgs(types: argTypes, values: argValues);
        string capArgs = argTypes.Count > 0
            ? $"{baseArgs}, ptr {bound}"
            : $"ptr {bound}";

        string isNull = NextTemp();
        EmitLine(sb: sb, line: $"  {isNull} = icmp eq ptr {bound}, null");
        string lless = NextLabel(prefix: "cl.less");
        string lcap = NextLabel(prefix: "cl.cap");
        string lmerge = NextLabel(prefix: "cl.merge");
        EmitLine(sb: sb, line: $"  br i1 {isNull}, label %{lless}, label %{lcap}");

        if (callRet == "void")
        {
            EmitLine(sb: sb, line: $"{lless}:");
            EmitLine(sb: sb, line: $"  call void {fn}({baseArgs})");
            EmitLine(sb: sb, line: $"  br label %{lmerge}");
            EmitLine(sb: sb, line: $"{lcap}:");
            EmitLine(sb: sb, line: $"  call void {fn}({capArgs})");
            EmitLine(sb: sb, line: $"  br label %{lmerge}");
            EmitLine(sb: sb, line: $"{lmerge}:");
            if (sretSlot == null)
            {
                return "undef";
            }

            string loaded = NextTemp();
            EmitLine(sb: sb, line: $"  {loaded} = load {retLlvm}, ptr {sretSlot}");
            return loaded;
        }

        string r0 = NextTemp();
        EmitLine(sb: sb, line: $"{lless}:");
        EmitLine(sb: sb, line: $"  {r0} = call {callRet} {fn}({baseArgs})");
        EmitLine(sb: sb, line: $"  br label %{lmerge}");
        string r1 = NextTemp();
        EmitLine(sb: sb, line: $"{lcap}:");
        EmitLine(sb: sb, line: $"  {r1} = call {callRet} {fn}({capArgs})");
        EmitLine(sb: sb, line: $"  br label %{lmerge}");
        EmitLine(sb: sb, line: $"{lmerge}:");
        string result = NextTemp();
        EmitLine(sb: sb,
            line: $"  {result} = phi {callRet} [ {r0}, %{lless} ], [ {r1}, %{lcap} ]");
        return retCoerce != null
            ? CoerceAbiToStruct(sb: sb, abiValue: result, abiType: retCoerce, structLlvm: retLlvm)
            : result;
    }

    /// <summary>
    /// Builds a comma-separated argument list for a call instruction.
    /// </summary>
    private static string BuildCallArgs(List<string> types, List<string> values)
    {
        if (types.Count != values.Count || types.Count == 0)
        {
            return "";
        }

        return string.Join(separator: ", ",
            values: types.Select(selector: (t, i) => $"{t} {values[index: i]}"));
    }

    private (string Value, string LlvmType) CoerceCallArgumentToParameter(StringBuilder sb,
        string argValue, TypeSymbol actualType, TypeSymbol parameterType,
        RoutineInfo callee)
    {
        // ABI-Indirect struct value arg: spill to a stack slot and pass `ptr byval(%T)`.
        if (TryCoerceArgToByval(sb: sb,
                argValue: argValue,
                actualType: actualType,
                parameterType: parameterType,
                callee: callee,
                newValue: out string byvalValue,
                newType: out string byvalType))
        {
            return (byvalValue, byvalType);
        }

        // ABI-Coerce small struct value arg: reinterpret into the integer register form.
        if (TryCoerceArgToRegister(sb: sb,
                argValue: argValue,
                parameterType: parameterType,
                callee: callee,
                newValue: out string regValue,
                newType: out string regType))
        {
            return (regValue, regType);
        }

        string expectedLlvm = GetParameterLlvmType(type: parameterType);
        string actualLlvm = GetLlvmType(type: actualType);
        if (actualLlvm == expectedLlvm)
        {
            return (argValue, expectedLlvm);
        }

        if (actualType is RecordTypeSymbol
            {
                BackendType: null,
                MemberVariables.Count: 1
            } record)
        {
            TypeSymbol fieldType = record.MemberVariables[index: 0].Type;
            string fieldLlvm = GetParameterLlvmType(type: fieldType);
            if (fieldLlvm == expectedLlvm)
            {
                string extracted = NextTemp();
                EmitLine(sb: sb, line: $"  {extracted} = extractvalue {actualLlvm} {argValue}, 0");
                return (extracted, expectedLlvm);
            }
        }

        return (argValue, expectedLlvm);
    }

    /// <summary>
    /// Performs the consume transferred call ownership step for this builder phase.
    /// </summary>
    private void ConsumeTransferredCallOwnership(StringBuilder sb, IEnumerable<Expression> arguments)
    {
        foreach (Expression argument in arguments)
        {
            NullStampStolenLocal(sb: sb, expr: argument);
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Attempts to emit recovered free intrinsic call and reports whether it succeeded.
    /// </summary>
    private string? TryEmitRecoveredFreeIntrinsicCall(StringBuilder sb, string functionName,
        RoutineInfo? resolvedRoutine, List<Expression> arguments,
        List<TypeExpression>? typeArguments, TypeSymbol? resolvedReturnType)
    {
        // `hollow[T]()` — the entity-footprint alloc primitive (@innate, bodyless). Heap-allocate the
        // concrete entity's STRUCT footprint zeroed, exactly like a `create` prologue with no args, so a
        // SoA entity (SplitList) starts with null columns + zero counts. Only entity return types are
        // valid; a non-entity `hollow` is a stdlib authoring error.
        // `hollow` is a bare global @innate primitive — an equality check against the name constant,
        // NOT a suffix-parse of a qualified name string.
        if (functionName == "hollow")
        {
            if (resolvedReturnType is not EntityTypeSymbol hollowEntity)
            {
                throw new InvalidOperationException(
                    message:
                    $"hollow[T]() requires an entity type argument, got '{resolvedReturnType?.FullName ?? "<null>"}'.");
            }

            return EmitEntityAllocation(sb: sb, entity: hollowEntity);
        }

        if (resolvedRoutine?.LlvmIrTemplate != null)
        {
            return EmitLlvmIntrinsicCall(sb: sb,
                routine: resolvedRoutine,
                receiver: null,
                arguments: arguments,
                typeArguments: typeArguments,
                resolvedReturnType: resolvedReturnType);
        }

        return null;
    }

    /// <summary>
    /// The free call's routine as the analyzer or monomorphization stamped it. A call left without one is
    /// a construction (<c>ConstructedType</c>) or a routine value, which the caller handles; the emitter
    /// never looks a routine up by name.
    /// </summary>
    private RoutineInfo? ResolveInitialFreeCallRoutine(RoutineInfo? resolvedRoutine,
        List<TypeExpression>? typeArguments)
    {
        if (resolvedRoutine == null || typeArguments is not { Count: > 0 })
        {
            return resolvedRoutine;
        }

        return RequireConcreteFreeRoutine(routine: resolvedRoutine);
    }

    /// <summary>
    /// Requires a free call with explicit type arguments to arrive already monomorphized: a generic free
    /// routine, or a <c>create</c> still owned by a generic definition, means an earlier pass skipped it.
    /// </summary>
    private RoutineInfo RequireConcreteFreeRoutine(RoutineInfo routine)
    {
        if (routine is { OwnerType: null, IsGenericDefinition: true } or
            { IsCreator: true, OwnerType.IsGenericDefinition: true })
        {
            throw new InvalidOperationException(
                message:
                $"Generic call '{routine.FullName}' reached the LLVM emitter uninstantiated in " +
                $"[{_currentRoutineDiagName}]. Monomorphization must bind it before emission.");
        }

        return routine;
    }

    private (string Receiver, TypeSymbol? ReceiverType) ResolveMemberRoutineCallReceiver(
        StringBuilder sb, MemberExpression member)
    {
        // Const-generic value receiver: `N.represent()` where N is bound to a literal (e.g. 4
        // for `Array[S64, 4]`). Without this check, the typewise-receiver branch below treats N
        // as a type identifier and synthesizes a zero receiver — `Array.diagnose` then prints
        // `count: 0` instead of the actual N. Substitute the const value before falling through.
        if (member.Object is IdentifierExpression constId &&
            !_localVariables.ContainsKey(key: constId.Name) &&
            constId.ResolvedType is ConstGenericValueTypeSymbol constVal)
        {
            return (constVal.Value.ToString(),
                ResolveConstGenericUnderlyingType(constVal: constVal));
        }

        // A Suflae module `global` receiver (`counter.add(...)`) is a VALUE, not a type name — it is
        // excluded here so it falls through to the value path below, where EmitExpression loads it from
        // its `@global` symbol (otherwise this typewise branch would synthesize a zero receiver).
        if (member.Object is IdentifierExpression typeId &&
            !_localVariables.ContainsKey(key: typeId.Name) &&
            !_moduleGlobals.ContainsKey(key: typeId.Name) &&
            ResolveAggregatePreset(name: typeId.Name) == null)
        {
            // Aggregate-preset receivers are NOT typewise/static receivers — they are by-ref values
            // whose storage is the `@preset.*` global. Fall through so EmitLvalueAddress returns it.
            // `common`/static calls on a bare TYPE name (e.g. `Real.zero()`, `Real(value: 2)` lowered
            // to `Real.create(...)`) carry no value expression, so GetExpressionType is null on some
            // stdlib paths where SA didn't stamp the receiver's type. Resolve the type by name
            // (module-aware) before giving up — the synthesized zero receiver below is correct for a
            // static memberRoutine (it has no `me` to read).
            TypeSymbol? typeAsReceiver = GetExpressionType(expr: member.Object) ??
                                       LookupTypeInCurrentModule(name: typeId.Name);
            if (typeAsReceiver == null)
            {
                throw new InvalidOperationException(
                    message:
                    $"Typewise/common member routine receiver '{typeId.Name}' reached LLVM codegen without a semantic receiver type.");
            }

            string llvmType = GetLlvmType(type: typeAsReceiver);
            string receiver = "0";
            if (llvmType.StartsWith(value: '%') || llvmType.StartsWith(value: '{'))
            {
                receiver = "zeroinitializer";
            }
            else if (llvmType == "ptr")
            {
                receiver = "null";
            }

            return (receiver, typeAsReceiver);
        }

        TypeSymbol? receiverType = GetExpressionType(expr: member.Object);
        if (ReceiverPassedByRef(receiverType: receiverType))
        {
            // Struct-record memberRoutines take `me` by reference: pass the receiver's storage address
            // (spilling an rvalue receiver to a temp), matching the by-ref `me` parameter ABI.
            return (EmitLvalueAddress(sb: sb, expr: member.Object), receiverType);
        }

        string emittedReceiver = EmitExpression(sb: sb, expr: member.Object);
        return (emittedReceiver, receiverType);
    }

    /// <summary>
    /// Computes a pointer to the storage of an lvalue expression — used by the
    /// `record.get_address()` intercept. Recurses through member-access chains; the root
    /// must be a named local/parameter (its alloca name resolves to <c>%name.addr</c>).
    /// Field walks emit one <c>getelementptr</c> per hop. Entity-rooted field chains use
    /// the entity ptr (already a pointer) as the GEP base; record-rooted chains use the
    /// root alloca's address.
    /// </summary>
    private string EmitLvalueAddress(StringBuilder sb, Expression expr)
    {
        return expr switch
        {
            NamedArgumentExpression named =>
                // `f(name: lvalue)` — the address of the named argument is the address of its value.
                EmitLvalueAddress(sb: sb, expr: named.Value),
            IdentifierExpression id => EmitIdentifierLvalueAddress(id: id),
            MemberExpression member => EmitMemberLvalueAddress(sb: sb, member: member, expr: expr),
            // A record behind a pointer wrapper: the wrapper pointer is the record's storage.
            WrapperProjectionExpression { Kind: WrapperProjectionKind.RecordAddress } projection =>
                EmitExpression(sb: sb, expr: projection.Wrapper),
            _ => EmitSpillToTempAddress(sb: sb, expr: expr)
        };
    }

    /// <summary>Address of a named local/parameter — or an aggregate preset's <c>@preset.*</c> global.</summary>
    private string EmitIdentifierLvalueAddress(IdentifierExpression id)
    {
        // Aggregate (Array[T,N]) preset: its storage IS the shared `@preset.*` constant global, so
        // its address is the global symbol — the by-ref `me` receiver and `.hijack()` read in place.
        if (ResolveAggregatePreset(name: id.Name) is { } aggregatePreset)
        {
            return EmitOrGetPresetGlobal(preset: aggregatePreset);
        }

        if (!_localVariables.ContainsKey(key: id.Name))
        {
            // Suflae module-level `global`: its storage IS the `@global` symbol, so that is its address.
            if (_moduleGlobals.TryGetValue(key: id.Name,
                    value: out (TypeSymbol Type, string Symbol) gslot))
            {
                return gslot.Symbol;
            }

            throw new InvalidOperationException(
                message: $"Cannot take address of '{id.Name}' — not a local variable or " +
                         $"parameter. Bind it to a `var` first.");
        }

        string llvmName = _localVarLlvmNames.TryGetValue(key: id.Name, value: out string? unique)
            ? unique
            : id.Name;
        return $"%{llvmName}.addr";
    }

    /// <summary>
    /// Address of a member-access chain: resolves the field index + base pointer for a record /
    /// crashable / entity parent and emits a <c>getelementptr</c>, or spills to a temp when the
    /// member is a genuine rvalue chain with no known field offset.
    /// </summary>
    private string EmitMemberLvalueAddress(StringBuilder sb, MemberExpression member,
        Expression expr)
    {
        TypeSymbol? parentType = GetExpressionType(expr: member.Object);
        if (parentType == null)
        {
            throw new InvalidOperationException(
                message:
                $"Cannot determine type of parent expression for '.{member.MemberName}' " +
                "in address-of chain.");
        }

        (int fieldIndex, string? parentLlvmType, string? basePtr) =
            ResolveMemberFieldLocation(sb: sb, member: member, parentType: parentType);

        if (fieldIndex < 0 || basePtr == null || parentLlvmType == null)
        {
            // Not a stored field at a known offset (a genuine rvalue member chain, or a parent kind
            // with no struct fields): materialize the value and address the temporary.
            return EmitSpillToTempAddress(sb: sb, expr: expr);
        }

        string fieldPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {fieldPtr} = getelementptr {parentLlvmType}, ptr {basePtr}, i32 0, i32 {fieldIndex}");
        return fieldPtr;
    }

    /// <summary>
    /// Resolves the (field index, parent LLVM type name, base pointer) for a member access. Record
    /// parents recurse for their storage address (and get the stale-carrier-shell refresh);
    /// entity/crashable parents are already ptr values. Returns index -1 when the member isn't a
    /// stored field.
    /// </summary>
    private (int FieldIndex, string? ParentLlvmType, string? BasePtr) ResolveMemberFieldLocation(
        StringBuilder sb, MemberExpression member, TypeSymbol parentType)
    {
        switch (parentType)
        {
            case RecordTypeSymbol recordParent:
                int recIdx = ResolveRecordFieldIndex(record: recordParent,
                    memberVariableName: member.MemberName);
                return recIdx < 0
                    ? (-1, null, null)
                    : (recIdx, EnsureRecordTypeDeclared(record: recordParent),
                        EmitLvalueAddress(sb: sb, expr: member.Object));
            case CrashableTypeSymbol crashableParent:
                int crIdx = IndexOfMemberVariable(memberVariables: crashableParent.MemberVariables,
                    name: member.MemberName);
                return crIdx < 0
                    ? (-1, null, null)
                    : (crIdx, GetCrashableTypeName(crashable: crashableParent),
                        EmitExpression(sb: sb, expr: member.Object));
            case EntityTypeSymbol entityParent:
                int enIdx = IndexOfMemberVariable(memberVariables: entityParent.MemberVariables,
                    name: member.MemberName);
                return enIdx < 0
                    ? (-1, null, null)
                    : (enIdx, GetEntityTypeName(entity: entityParent),
                        EmitExpression(sb: sb, expr: member.Object));
            default:
                return (-1, null, null);
        }
    }

    /// <summary>Index of the named member variable, or -1 if absent.</summary>
    private static int IndexOfMemberVariable(List<MemberVariableInfo> memberVariables, string name)
    {
        for (int i = 0; i < memberVariables.Count; i++)
        {
            if (memberVariables[index: i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds a record field's index, or -1.
    /// </summary>
    private static int ResolveRecordFieldIndex(RecordTypeSymbol record, string memberVariableName)
    {
        return IndexOfMemberVariable(memberVariables: record.MemberVariables, name: memberVariableName);
    }

    /// <summary>
    /// Materializes an expression's value into a fresh entry alloca and returns the alloca pointer.
    /// Used by <see cref="EmitLvalueAddress"/> when an address is needed but the expression has no
    /// stable storage — a genuine rvalue (call result, constructor, literal). The temporary is a
    /// callee-local copy: correct for reads and value-identity operations, but a write through it
    /// would not reach an original (rvalues have none).
    /// </summary>
    private string EmitSpillToTempAddress(StringBuilder sb, Expression expr)
    {
        TypeSymbol? exprType = GetExpressionType(expr: expr);
        if (exprType == null)
        {
            throw new InvalidOperationException(
                message: $"Cannot take address of expression form '{expr.GetType().Name}' — " +
                         "unknown type, cannot spill to a temporary.");
        }

        string val = EmitExpression(sb: sb, expr: expr);
        string llvm = GetLlvmType(type: exprType);
        string slot = NextTemp();
        EmitEntryAlloca(llvmName: slot, llvmType: llvm);
        EmitLine(sb: sb, line: $"  store {llvm} {val}, ptr {slot}");
        return slot;
    }

    /// <summary>
    /// Whether a memberRoutine receiver of this type is passed by reference (a <c>ptr</c> to its storage):
    /// storage-backed records — struct records (no <c>@llvm</c> backend) and aggregate-backed
    /// <c>@llvm</c> records (<c>[N x T]</c>, e.g. Array/BitArray). Shares the exact predicate with
    /// the callee-side <c>IsByRefMeReceiver</c> (via <c>IsByRefMeRecord</c>) so call sites pass the
    /// receiver's address and the matching <c>ptr</c> argument type. Scalar <c>@llvm</c> records stay
    /// by value.
    /// </summary>
    private static bool ReceiverPassedByRef(TypeSymbol? receiverType)
    {
        return IsByRefMeRecord(ownerType: receiverType);
    }

    private string EmitFreeCallInstruction(StringBuilder sb, List<Expression> arguments,
        RoutineInfo? routine, string functionName, bool isFailableCallSyntax,
        List<string> argValues, List<string> argTypes)
    {
        // Build the call
        string mangledName = routine != null
            ? MangleRoutineName(routine: routine)
            : DecorateRoutineSymbolName(baseName: SanitizeLlvmName(name: functionName),
                isFailable: isFailableCallSyntax);

        // Ensure the function is declared (generates 'declare' and tracks in _generatedRoutines)
        if (routine != null)
        {
            GenerateRoutineDeclaration(routine: routine);
        }
        else
        {
            _generatedRoutines.Add(item: mangledName);
        }

        // For external("C") functions, B16 (half) params must be bitcast to i16 (C ABI)
        bool isCExtern = routine is { CallingConvention: "C" };
        if (isCExtern)
        {
            CoerceCExternB16Arguments(sb: sb, argValues: argValues, argTypes: argTypes);
        }

        string returnType = routine?.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";
        // Failable routines return T directly -> they crash on failure, no carrier needed

        string callReturnType = isCExtern && returnType == "half"
            ? "i16"
            : returnType;

        // Struct returns classified Indirect by the ABI come back through a hidden sret pointer:
        // external("C") returning structs > 8 bytes (Win-x64 MSVC), or an RF routine whose return
        // is ABI-Indirect. The declaration, definition, and every return already agree (see
        // ReturnsViaSret); the call must pass the result slot as the first argument and load it back.
        // Coerced (Phase 2) struct return: the callee returns the ABI integer form; call it as that,
        // then reinterpret the result back into the struct value.
        return EmitFreeCallAndGetResult(sb: sb, arguments: arguments,
            routine: routine,
            spec: new FreeCallSpec(MangledName: mangledName, ReturnType: returnType,
                CallReturnType: callReturnType, IsCExtern: isCExtern),
            argTypes: argTypes, argValues: argValues);
    }

    private void CoerceMemberCallArguments(StringBuilder sb, RoutineInfo? memberRoutine,
        bool memberRoutineTakesReceiver, List<string> argValues, List<string> argTypes,
        List<TypeSymbol> argTypeInfos)
    {
        // Coerce explicit struct value args to byval (the ABI-Indirect arg form) before the call.
        if (memberRoutine != null)
        {
            int recvOffset = memberRoutineTakesReceiver
                ? 1
                : 0;
            int explicitCount = Math.Min(val1: argValues.Count - recvOffset,
                val2: memberRoutine.Parameters.Count);
            for (int i = 0; i < explicitCount; i++)
            {
                int ai = i + recvOffset;
                if (TryCoerceArgToByval(sb: sb,
                        argValue: argValues[index: ai],
                        actualType: argTypeInfos[index: ai],
                        parameterType: memberRoutine.Parameters[index: i].Type,
                        callee: memberRoutine,
                        newValue: out string bv,
                        newType: out string bt))
                {
                    argValues[index: ai] = bv;
                    argTypes[index: ai] = bt;
                }
                else if (TryCoerceArgToRegister(sb: sb,
                             argValue: argValues[index: ai],
                             parameterType: memberRoutine.Parameters[index: i].Type,
                             callee: memberRoutine,
                             newValue: out string rv,
                             newType: out string rt))
                {
                    argValues[index: ai] = rv;
                    argTypes[index: ai] = rt;
                }
            }
        }
    }


    private void ResolveMemberCallSymbol(MemberExpression member, RoutineInfo? resolvedRoutine,
        List<TypeExpression>? typeArguments, CallLoweringKind loweringKind, TypeSymbol receiverType,
        ref RoutineInfo? memberRoutine, out string mangledName)
    {
        if (typeArguments is { Count: > 0 } && memberRoutine != null)
        {
            RequireConcreteMemberRoutine(receiverType: receiverType, member: member,
                memberRoutine: memberRoutine);
            mangledName = MangleRoutineName(routine: memberRoutine);
        }
        else if (memberRoutine != null)
        {
            // When the memberRoutine is fully concrete (non-generic owner, concrete type),
            // MangleRoutineName produces the correct name directly -> no registry re-lookup needed.
            // Fall back to ResolveMemberRoutine only when the carried routine still has a generic/universal owner
            // (e.g., owner is GenericParameterTypeSymbol or the generic definition itself), in which case
            // we re-derive from the concrete receiverType.
            if (memberRoutine is
                {
                    IsGenericDefinition: false,
                    OwnerType: not GenericParameterTypeSymbol and not { IsGenericDefinition: true }
                })
            {
                mangledName = MangleRoutineName(routine: memberRoutine);
            }
            else
            {
                // Owner is still generic -> re-derive concrete memberRoutine from receiverType.
                ResolvedMemberRoutine? resolved = ResolveMemberRoutine(receiverType: receiverType,
                    memberRoutineName: memberRoutine.Name);
                mangledName = resolved?.MangledName ??
                              Q(name: DecorateRoutineSymbolName(
                                  baseName:
                                  $"{receiverType.FullName}.{SanitizeLlvmName(name: member.MemberName)}",
                                  isFailable: memberRoutine.IsFailable));
            }
        }
        else
        {
            throw new InvalidOperationException(
                message:
                $"member routine '{member.MemberName}' on '{receiverType.FullName}' could not be resolved after all re-lookup attempts. " +
                $"loweringKind={loweringKind}, resolvedRoutine={resolvedRoutine?.RegistryKey ?? "<null>"}. " +
                $"Routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"}).");
        }
    }

    private TypeSymbol NormalizeMemberReceiverType(MemberExpression member, TypeSymbol? receiverType)
    {
        switch (receiverType)
        {
            case null:
            {
                string objDesc = member.Object switch
                {
                    IdentifierExpression id => $"identifier '{id.Name}'",
                    CallExpression { Callee: MemberExpression m2 } c =>
                        $"call .{m2.MemberName}() (ResolvedType={c.ResolvedType?.Name ?? "null"})",
                    _ => member.Object.GetType()
                               .Name
                };
                throw new InvalidOperationException(
                    message:
                    $"Cannot determine receiver type for member routine call .{member.MemberName} on {objDesc}");
            }
        }

        return receiverType;
    }
}

/// <summary>
/// Bundles the initial argument lists for <see cref="LlvmEmitter.EmitMemberCallArgumentsInDeclarationOrder"/>
/// — the receiver flag plus the three parallel accumulation lists — so the method stays within the parameter-count limit.
/// </summary>
internal sealed record MemberCallArgContext(
    bool TakesReceiver,
    List<string> Values,
    List<string> Types,
    List<TypeSymbol> TypeInfos);

/// <summary>
/// Bundles the arguments for <see cref="LlvmEmitter"/> free-function call emission.
/// Replaces the 8-parameter overload so callers pass a single context value.
/// </summary>
internal sealed record RoutineCallRequest(
    string FunctionName,
    List<Expression> Arguments,
    RoutineInfo? ResolvedRoutine,
    TypeSymbol? ResolvedReturnType,
    List<TypeExpression>? TypeArguments)
{
    /// <summary>
    /// True when the call site used the failable `!` marker. The <see cref="FunctionName"/> is BARE
    /// — this structured flag records failability instead of a trailing `!` in the name.
    /// </summary>
    public bool IsFailable { get; init; }
}
