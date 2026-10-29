using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

#region Numeric Type Classification

/// <summary>
/// Classification of numeric types for type checking purposes.
/// </summary>
internal enum NumericTypeKind
{
    /// <summary>Not a numeric type.</summary>
    None,

    /// <summary>Signed fixed-width integers (s8, s16, s32, s64, s128).</summary>
    SignedInteger,

    /// <summary>Unsigned fixed-width integers (u8, u16, u32, u64, u128).</summary>
    UnsignedInteger,

    /// <summary>Binary floating point (b16, b32, b64, b128).</summary>
    BinaryFloat,

    /// <summary>Decimal floating point (d32, d64, d128).</summary>
    DecimalFloat,

    /// <summary>Arbitrary precision integer (Suflae Int).</summary>
    ArbitraryInteger,

    /// <summary>Arbitrary precision decimal (Suflae Decimal).</summary>
    ArbitraryDecimal,

    /// <summary>Exact rational number (Suflae Fraction).</summary>
    Fraction
}

#endregion

/// <summary>
/// Helper memberRoutines for analysis.
/// </summary>
public sealed partial class SemanticVerifier
{
    private const string MaybeTypeName = "Optional";
    private const string IterableProtocolName = "Emittable";

    #region Carrier Type Helpers

    /// <summary>
    /// Returns the base name ("Optional" or "Check") for a carrier type,
    /// or null if the type is not a carrier type.
    /// Works for both generic definitions (name == "Optional") and resolved instances (GenericDefinition.Name == "Optional").
    /// </summary>
    private static string? GetCarrierBaseName(TypeSymbol type)
    {
        if (type is not RecordTypeSymbol r)
        {
            return null;
        }

        string baseName = r.GenericDefinition?.Name ?? r.Name;
        return baseName is MaybeTypeName or "Check"
            ? baseName
            : null;
    }

    /// <summary>
    /// Returns true if the type is a carrier type (Optional, Result, or Lookup).
    /// </summary>
    private static bool IsCarrierType(TypeSymbol type)
    {
        return GetCarrierBaseName(type: type) != null;
    }

    /// <summary>
    /// Returns true if the type is an Optional carrier type.
    /// </summary>
    private static bool IsMaybeType(TypeSymbol type)
    {
        return GetCarrierBaseName(type: type) == MaybeTypeName;
    }

    /// <summary>
    /// Returns true if the type is a legal target for the value-position `none` literal:
    /// Optional[T] (its absent arm is matched by `is None`), or a variant
    /// that declares a None member. Result[T] is NOT included — Result's two arms are Ok/Err.
    /// </summary>
    private static bool IsNoneCarrierSlot(TypeSymbol type)
    {
        string? carrier = GetCarrierBaseName(type: type);
        if (carrier is MaybeTypeName)
        {
            return true;
        }

        return type is VariantTypeSymbol variant && variant.Members.Any(predicate: m => m.IsNone);
    }

    /// <summary>
    /// Checks if a pattern represents a None check.
    /// The parser creates TypePattern(type: "None") rather than NonePattern.
    /// </summary>
    private static bool IsNonePattern(Pattern pattern)
    {
        return pattern is NonePattern or TypePattern { Type.Name: "None" };
    }

    /// <summary>
    /// Checks if a pattern represents a None check.
    /// None is parsed as a regular type pattern.
    /// </summary>
    private static bool IsNoneTypePattern(Pattern pattern)
    {
        return pattern is TypePattern { Type.Name: "None" };
    }

    /// <summary>
    /// Checks if a pattern is the absent arm for a carrier type.
    /// Optional[T] uses `is None`. Result[T] has no absent state
    /// (only Crashable | T); when T == None, success matches `is None` in value position.
    /// </summary>
    private static bool IsAbsentPattern(Pattern pattern, TypeSymbol carrierType)
    {
        return GetCarrierBaseName(type: carrierType) switch
        {
            MaybeTypeName => IsNonePattern(pattern: pattern),
            _ => false
        };
    }

    #endregion

    #region Helper memberRoutines for Analysis

    /// <summary>
    /// Validates argument count and types for a routine call against the routine's parameter list.
    /// Reports errors for too-few arguments, too-many arguments (on non-variadic routines), and type mismatches.
    /// </summary>
    /// <summary>
    /// Named-argument punning (field-init shorthand): a bare identifier that matches a target parameter/field name is
    /// that name's argument, <c>name: name</c>. So <c>Point(x, y)</c> == <c>Point(x: x, y: y)</c> and
    /// <c>make_user(name, age: 31)</c> == <c>make_user(name: name, age: 31)</c> — bound by NAME (reorder-safe) and valid
    /// under the RF-S510 "3+ params need names" rule. All-or-nothing over the arguments written without a name: when one
    /// of them is not a bare identifier matching a distinct name nobody else takes, none is punned, so the call follows
    /// the normal positional rules (and a mix of names and positions stays the S512 error it is).
    /// </summary>
    private static void PunMatchingNamedArgs(List<Expression> arguments,
        IReadOnlyList<string> targetNames)
    {
        if (arguments.Count == 0)
        {
            return;
        }

        var names = new HashSet<string>(collection: targetNames, comparer: StringComparer.Ordinal);
        var taken = new HashSet<string>(
            collection: arguments.OfType<NamedArgumentExpression>().Select(selector: n => n.Name),
            comparer: StringComparer.Ordinal);
        bool any = false;
        foreach (Expression arg in arguments)
        {
            if (arg is NamedArgumentExpression)
            {
                continue;
            }

            if (arg is not IdentifierExpression id || !names.Contains(item: id.Name) || !taken.Add(item: id.Name))
            {
                return; // an argument without a name that is no pun: leave the call as written
            }

            any = true;
        }

        if (!any)
        {
            return;
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[index: i] is IdentifierExpression id)
            {
                arguments[index: i] = new NamedArgumentExpression(Name: id.Name, Value: id, Location: id.Location);
            }
        }
    }

    private void AnalyzeCallArguments(RoutineInfo routine, List<Expression> arguments,
        SourceLocation location, TypeSymbol? callObjectType = null)
    {
        foreach (Expression argument in arguments)
        {
            NoteStealDestination(value: argument, destination: $"the call to '{routine.Name}'");
        }

        _callArgumentsInAnalysis.Push(item: arguments);
        try
        {
            AnalyzeCallArgumentsCore(routine: routine,
                arguments: arguments,
                location: location,
                callObjectType: callObjectType);
        }
        finally
        {
            _callArgumentsInAnalysis.Pop();
        }
    }

    private void AnalyzeCallArgumentsCore(RoutineInfo routine, List<Expression> arguments,
        SourceLocation location, TypeSymbol? callObjectType)
    {
        List<ParamInfo> parameters = routine.Parameters;
        int totalParams = parameters.Count;

        // Field-init shorthand: pun bare identifiers matching parameter names into named args.
        PunMatchingNamedArgs(arguments: arguments,
            targetNames: parameters.Where(predicate: p => p.Name != "me")
                                   .Select(selector: p => p.Name)
                                   .ToList());

        // Step 1: Validate named argument ordering and build parameter bindings.
        Dictionary<int, Expression> boundParams = BuildArgumentBindings(routine: routine,
            arguments: arguments,
            parameters: parameters,
            totalParams: totalParams,
            location: location);

        // Step 2: Check argument count against required parameters.
        int positionalCount = arguments.Count(predicate: a => a is not NamedArgumentExpression);
        ValidateArgumentCount(routine: routine,
            arguments: arguments,
            parameters: parameters,
            totalParams: totalParams,
            boundParams: boundParams,
            positionalCount: positionalCount,
            location: location);

        // Step 3: Type-check each bound argument against its parameter.
        TypeCheckBoundArguments(routine: routine,
            arguments: arguments,
            parameters: parameters,
            totalParams: totalParams,
            boundParams: boundParams,
            callObjectType: callObjectType);
    }

    /// <summary>
    /// Validates named/positional argument ordering and builds the parameter-index to argument-expression
    /// map used by subsequent count and type checks. Reports S505 (unknown named), S506 (duplicate),
    /// S507 (positional after named), S510 (naming required), S512 (mixed style).
    /// Extracted from <see cref="AnalyzeCallArguments"/>.
    /// </summary>
    private Dictionary<int, Expression> BuildArgumentBindings(RoutineInfo routine,
        List<Expression> arguments, List<ParamInfo> parameters, int totalParams,
        SourceLocation location)
    {
        int nonMeParamCount =
            parameters.Count(predicate: p => p.Name != "me" && !p.HasDefaultValue);
        bool requiresNamedArgs = nonMeParamCount >= 3 && !routine.IsVariadic;
        bool recommendsNamedArgs = nonMeParamCount == 2 && !routine.IsVariadic;

        // @positional / Foreign routines relax naming requirements.
        bool isPositional = routine.Annotations.Contains(value: "positional") || routine.IsForeign;
        if (isPositional)
        {
            requiresNamedArgs = false;
            recommendsNamedArgs = false;
        }

        // S512: a call must be all-named OR all-positional — mixing is always an error.
        bool hasNamedArg = arguments.Any(predicate: a => a is NamedArgumentExpression { IsDefaultArgument: false });
        bool hasPositionalArg = arguments.Any(predicate: a => a is not NamedArgumentExpression);
        bool isMixed = hasNamedArg && hasPositionalArg;
        if (isMixed)
        {
            ReportError(code: SemanticDiagnosticCode.MixedPositionalAndNamedArguments,
                message:
                $"Call to '{routine.Name}' mixes positional and named arguments — a call must be " +
                "either all positional or all named.",
                location: location);
        }

        bool seenNamed = false;
        var boundParams = new Dictionary<int, Expression>();
        int positionalIndex = 0;

        foreach (Expression arg in arguments)
        {
            if (arg is NamedArgumentExpression named)
            {
                seenNamed = true;
                ProcessNamedArg(named: named,
                    routine: routine,
                    parameters: parameters,
                    totalParams: totalParams,
                    boundParams: boundParams);
            }
            else
            {
                ProcessPositionalArg(arg: arg,
                    routine: routine,
                    parameters: parameters,
                    totalParams: totalParams,
                    boundParams: boundParams,
                    ctx: new PositionalArgContext(RequiresNamedArgs: requiresNamedArgs,
                        RecommendsNamedArgs: recommendsNamedArgs,
                        IsPositional: isPositional,
                        IsMixed: isMixed,
                        SeenNamed: seenNamed,
                        NonMeParamCount: nonMeParamCount),
                    positionalIndex: ref positionalIndex);
            }
        }

        return boundParams;
    }

    /// <summary>
    /// Processes a single named argument: looks up the target parameter index, reports S505 (unknown
    /// parameter) or S506 (duplicate binding), and records the binding. Extracted from
    /// <see cref="BuildArgumentBindings"/>.
    /// </summary>
    private void ProcessNamedArg(NamedArgumentExpression named, RoutineInfo routine,
        List<ParamInfo> parameters, int totalParams, Dictionary<int, Expression> boundParams)
    {
        int paramIndex = -1;
        for (int j = 0; j < totalParams; j++)
        {
            if (parameters[index: j].Name == named.Name)
            {
                paramIndex = j;
                break;
            }
        }

        if (paramIndex == -1)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownNamedArgument,
                message: $"'{routine.Name}' has no parameter named '{named.Name}'.",
                location: named.Location);
            AnalyzeExpression(expression: named.Value);
        }
        else if (boundParams.ContainsKey(key: paramIndex))
        {
            ReportError(code: SemanticDiagnosticCode.DuplicateNamedArgument,
                message: $"Parameter '{named.Name}' of '{routine.Name}' is already bound.",
                location: named.Location);
            AnalyzeExpression(expression: named.Value);
        }
        else
        {
            boundParams[key: paramIndex] = named.Value;
        }
    }

    private readonly record struct PositionalArgContext(
        bool RequiresNamedArgs,
        bool RecommendsNamedArgs,
        bool IsPositional,
        bool IsMixed,
        bool SeenNamed,
        int NonMeParamCount);

    /// <summary>
    /// Processes a single positional argument: validates naming rules (S510/S507), determines
    /// whether the argument falls in a variadic slot, records the binding, and advances the positional
    /// index. Extracted from <see cref="BuildArgumentBindings"/>.
    /// </summary>
    private void ProcessPositionalArg(Expression arg, RoutineInfo routine,
        List<ParamInfo> parameters, int totalParams, Dictionary<int, Expression> boundParams,
        PositionalArgContext ctx, ref int positionalIndex)
    {
        bool requiresNamedArgs = ctx.RequiresNamedArgs;
        bool isPositional = ctx.IsPositional, isMixed = ctx.IsMixed, seenNamed = ctx.SeenNamed;
        int nonMeParamCount = ctx.NonMeParamCount;
        if (requiresNamedArgs && !isMixed)
        {
            ReportError(code: SemanticDiagnosticCode.NamedArgumentRequired,
                message:
                $"Routine '{routine.Name}' has {nonMeParamCount} parameters - all arguments must be named.",
                location: arg.Location);
        }
        else if (seenNamed && !isPositional && !isMixed)
        {
            ReportError(code: SemanticDiagnosticCode.PositionalAfterNamed,
                message:
                $"Positional argument cannot appear after named arguments in call to '{routine.Name}'.",
                location: arg.Location);
        }

        // Once we reach the varargs parameter, all subsequent positional args are variadic —
        // don't advance past it. Trailing params (sep, end) are filled via named args or defaults.
        bool inVariadicSlot = routine.IsVariadic && positionalIndex > 0 &&
                              positionalIndex - 1 < totalParams &&
                              parameters[index: positionalIndex - 1].IsVariadicParam;

        if (inVariadicSlot)
        {
            AnalyzeExpression(expression: arg);
        }
        else if (positionalIndex < totalParams)
        {
            if (boundParams.ContainsKey(key: positionalIndex))
            {
                ReportError(code: SemanticDiagnosticCode.DuplicateNamedArgument,
                    message:
                    $"Parameter '{parameters[index: positionalIndex].Name}' of '{routine.Name}' is already bound.",
                    location: arg.Location);
            }
            else
            {
                boundParams[key: positionalIndex] = arg;
            }
        }
        else if (!routine.IsVariadic)
        {
            boundParams[key: positionalIndex] = arg;
        }
        else
        {
            AnalyzeExpression(expression: arg);
        }

        if (!inVariadicSlot)
        {
            positionalIndex++;
        }
    }

    /// <summary>
    /// Validates the number of bound arguments against the routine's required parameter count.
    /// Reports S400 (too few arguments) and S401 (too many arguments). Extracted from
    /// <see cref="AnalyzeCallArguments"/>.
    /// </summary>
    private void ValidateArgumentCount(RoutineInfo routine, List<Expression> arguments,
        List<ParamInfo> parameters, int totalParams, Dictionary<int, Expression> boundParams,
        int positionalCount, SourceLocation location)
    {
        // A variadic parameter takes any number of values, none included (`total()`).
        int requiredParams = parameters.Count(predicate: p => !p.HasDefaultValue && !p.IsVariadicParam);
        int unboundRequired = 0;
        for (int i = 0; i < totalParams; i++)
        {
            if (!boundParams.ContainsKey(key: i) && !parameters[index: i].HasDefaultValue &&
                !parameters[index: i].IsVariadicParam)
            {
                unboundRequired++;
            }
        }

        if (unboundRequired > 0)
        {
            string msg = requiredParams == totalParams
                ? $"'{routine.Name}' expects {totalParams} argument(s), but got {arguments.Count}."
                : $"'{routine.Name}' expects at least {requiredParams} argument(s), but got {arguments.Count}.";
            ReportError(code: SemanticDiagnosticCode.TooFewArguments,
                message: msg,
                location: location);
        }
        else if (positionalCount > totalParams && !routine.IsVariadic)
        {
            ReportError(code: SemanticDiagnosticCode.TooManyArguments,
                message:
                $"'{routine.Name}' expects at most {totalParams} argument(s), but got {arguments.Count}.",
                location: location);
        }
    }

    /// <summary>
    /// A token parameter takes the entity itself at the call site: <c>total(items: xs)</c> reads as
    /// <c>total(items: xs.view())</c> for a <c>Viewing[T]</c> (or <c>Accessing[T]</c>) parameter, and
    /// <c>grow(items: xs)</c> as <c>grow(items: xs.modify())</c> for a <c>Modifying[T]</c> (or <c>Controlling[T]</c>)
    /// one: the same one-call token written out. A temporary works the same way (<c>total(items: make())</c>): it
    /// lives until the end of the statement, past the call.
    /// </summary>
    private Expression? ImplicitTokenArgument(Expression argExpr, TypeSymbol argType, TypeSymbol paramType)
    {
        if (!Declaration.ImplicitToken.IsTokenParameter(parameter: paramType, tokenKind: out string tokenKind,
                target: out TypeSymbol tokenTarget) || argType is ErrorTypeSymbol || argType.FullName == paramType.FullName)
        {
            return null;
        }

        string verb = tokenKind == Declaration.RuntimeContract.Viewing
            ? "view"
            : ModifyMemberRoutineName;
        SourceLocation location = argExpr.Location;
        Expression mint;
        if (Declaration.ImplicitToken.HandleOver(source: argType, tokenKind: tokenKind, target: tokenTarget) != null ||
            argType is EntityTypeSymbol && _registry.Rules.ChecksAccessTokens &&
            Declaration.ImplicitToken.Matches(value: argType, target: tokenTarget))
        {
            // An entity, or a handle or token over the value: its own `view()`/`modify()`.
            mint = new CallExpression(
                Callee: new MemberExpression(Object: argExpr, MemberName: verb, Location: location),
                Arguments: [],
                Location: location) { IsSynthesizedLowering = true };
        }
        else if (Declaration.ImplicitToken.Matches(value: argType, target: tokenTarget))
        {
            // A record, or a value of a type parameter: the token is the address of the place it is in (a
            // temporary gets a place of its own, which lives through the call).
            var address = new CallExpression(
                Callee: new MemberExpression(Object: argExpr,
                    MemberName: Declaration.RuntimeContract.RawPointer.Hijack,
                    Location: location),
                Arguments: [],
                Location: location) { IsSynthesizedLowering = true };
            mint = new GenericMemberRoutineCallExpression(
                Object: new IdentifierExpression(Name: tokenKind, Location: location),
                MemberRoutineName: tokenKind,
                TypeArguments:
                [
                    new TypeExpression(Name: tokenTarget.Name, GenericArguments: null, Location: location)
                    {
                        KnownType = tokenTarget
                    }
                ],
                Arguments: [address],
                IsMemoryOperation: false,
                Location: location) { IsFree = true };
        }
        else
        {
            return null;
        }

        AnalyzeExpression(expression: mint, expectedType: paramType);
        return mint;
    }

    /// <summary>
    /// A C string (<c>CStr</c>, <c>CWStr</c>) handed to a parameter of its pointer type (<c>CStrView</c>,
    /// <c>CWStrView</c>) becomes the pointer: <c>arg.view()</c>, valid while the C string lives, or for
    /// <c>steal arg</c>, <c>arg.leak()</c>, whose memory C keeps and nothing here tears down.
    /// Null when the argument is anything else.
    /// </summary>
    private Expression? ImplicitCStringView(Expression argExpr, TypeSymbol argType, TypeSymbol paramType)
    {
        if (argType is ErrorTypeSymbol ||
            !Declaration.RuntimeContract.CString.ViewOf.TryGetValue(key: argType.Name, value: out string? view) ||
            paramType.Name != view)
        {
            return null;
        }

        SourceLocation location = argExpr.Location;
        Expression value = UnwrapNamedArgument(argument: argExpr);
        string verb = value is StealExpression
            ? Declaration.RuntimeContract.CString.Leak
            : Declaration.RuntimeContract.CString.View;
        var call = new CallExpression(
            Callee: new MemberExpression(Object: value, MemberName: verb, Location: location),
            Arguments: [],
            Location: location) { IsSynthesizedLowering = true };
        AnalyzeExpression(expression: call, expectedType: paramType);
        return call;
    }

    /// <summary>Puts <paramref name="replacement"/> where <paramref name="original"/> stands in a call's
    /// argument list, inside its named-argument wrapper when it has one.</summary>
    private static void ReplaceArgument(List<Expression> arguments, Expression original,
        Expression replacement)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            if (ReferenceEquals(objA: arguments[index: i], objB: original))
            {
                arguments[index: i] = replacement;
                return;
            }

            if (arguments[index: i] is NamedArgumentExpression named &&
                ReferenceEquals(objA: named.Value, objB: original))
            {
                arguments[index: i] = named with { Value = replacement, ResolvedType = replacement.ResolvedType };
                return;
            }
        }
    }

    /// <summary>
    /// Type-checks each bound argument against its resolved parameter type, substituting owner/method
    /// generics where applicable, and reporting argument-type mismatch, nullable-entity, and C-boundary
    /// callback violations. Extracted from <see cref="AnalyzeCallArguments"/>.
    /// </summary>
    private void TypeCheckBoundArguments(RoutineInfo routine, List<Expression> arguments,
        List<ParamInfo> parameters, int totalParams, Dictionary<int, Expression> boundParams,
        TypeSymbol? callObjectType)
    {
        foreach (KeyValuePair<int, Expression> binding in boundParams)
        {
            if (binding.Key >= totalParams)
            {
                AnalyzeExpression(expression: binding.Value);
                continue;
            }

            ParamInfo param = parameters[index: binding.Key];
            TypeSymbol paramType = ResolveParamType(param: param,
                routine: routine,
                callObjectType: callObjectType);

            Expression argExpr = binding.Value;
            TypeSymbol argType = AnalyzeExpression(expression: argExpr,
                expectedType: CollectionLiteralConstraintType(routine: routine,
                    paramType: paramType,
                    argument: argExpr,
                    receiver: callObjectType) ?? paramType);
            TakeDangerousCallableArgument(routine: routine, paramType: paramType, argument: argExpr);
            if (paramType is RoutineTypeSymbol)
            {
                NoteCallableArgument(callee: routine, parameter: param.Name, argument: argExpr);
            }

            if ((ImplicitTokenArgument(argExpr: argExpr, argType: argType, paramType: paramType) ??
                 ImplicitCStringView(argExpr: argExpr, argType: argType, paramType: paramType)) is { } viewed)
            {
                ReplaceArgument(arguments: arguments, original: argExpr, replacement: viewed);
                argExpr = viewed;
                argType = viewed.ResolvedType ?? argType;
            }

            if (!param.IsNullable && SharedEntities.IsEntityRef(type: paramType) &&
                SharedEntities.IsNullableRead(expr: argExpr))
            {
                SharedEntities.ReportIntoNonNull(target: $"parameter '{param.Name}' of '{routine.Name}'",
                    value: argExpr,
                    optionalHint: $"{param.Name}: <Type>?");
            }

            if (argType.Category == TypeCategory.Error || paramType.Category == TypeCategory.Error)
            {
                continue;
            }

            if (!IsAssignableTo(source: argType, target: paramType) &&
                !IsBareRoutineRefToCPtr(argExpr: argExpr,
                    argType: argType,
                    paramType: paramType) &&
                !ContainsUnresolvedMemberRoutineGeneric(type: paramType,
                    genericParameters: routine.GenericParameters))
            {
                // A read token handed to a write-token parameter says what to pass instead, so it gets that message
                // alone rather than a bare type mismatch next to it.
                if (argType.BareName is Declaration.RuntimeContract.Viewing &&
                    paramType.BareName is Declaration.RuntimeContract.Modifying)
                {
                    ReportError(code: SemanticDiagnosticCode.WritableMemberRoutineThroughReadOnlyWrapper,
                        message:
                        $"You are handing a read token ('{argType.Name}') to '{param.Name}' of '{routine.Name}', " +
                        "which can change it. A read token only lets you read: pass a write token instead.",
                        location: argExpr.Location);
                }
                else if (OtherRealmOfSameType(source: argType, target: paramType) is { } crossing)
                {
                    ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
                        message:
                        $"Argument '{param.Name}' of '{routine.Name}' takes RF::{crossing.BareName}, and this is " +
                        $"SF::{crossing.BareName}: the two languages each have their own, which don't cross as they " +
                        $"are. Convert it at the boundary: '.to[RF::{crossing.BareName}]()'.",
                        location: argExpr.Location);
                }
                else
                {
                    ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
                        message:
                        $"Argument '{param.Name}' of '{routine.Name}': cannot convert '{argType.ShortTypeName}' to '{paramType.ShortTypeName}'.",
                        location: argExpr.Location);
                }
            }

            bool isCapturingLambdaLiteral = argExpr is LambdaExpression { Captures.Count: > 0 };
            if (IsForeignCapturingCallbackArg(routine: routine,
                    paramType: paramType,
                    argType: argType,
                    isCapturingLambdaLiteral: isCapturingLambdaLiteral))
            {
                ReportError(code: SemanticDiagnosticCode.ForeignCallbackMustBeNonCapturing,
                    message:
                    $"Argument '{param.Name}' of C routine '{routine.Name}' is a CAPTURING lambda, which cannot " +
                    "cross the C boundary — a routine handed to C becomes a raw function pointer with no " +
                    "environment, and a capture is an extra bound argument with no C slot. Pass a captureless " +
                    "callback and thread any state through an explicit userdata parameter.",
                    location: argExpr.Location);
            }

            Expression argValue = UnwrapNamedArgument(argument: argExpr);
            ValidateImplicitWrapperCopyArg(routine: routine,
                param: param,
                paramType: paramType,
                argExpr: argExpr,
                argValue: argValue,
                argType: argType);
            ValidateBareEntityConsumingArg(routine: routine,
                param: param,
                paramType: paramType,
                argValue: argValue,
                argType: argType);
        }
    }

    private static Expression UnwrapNamedArgument(Expression argument)
    {
        return argument is NamedArgumentExpression named
            ? named.Value
            : argument;
    }

    /// <summary>
    /// Resolves the effective type for <paramref name="param"/> by substituting owner-level or
    /// method-level generic parameters with the concrete types from <paramref name="callObjectType"/>.
    /// Returns the original parameter type when no substitution applies. Extracted from
    /// <see cref="TypeCheckBoundArguments"/>.
    /// </summary>
    private TypeSymbol ResolveParamType(ParamInfo param, RoutineInfo routine,
        TypeSymbol? callObjectType)
    {
        TypeSymbol paramType = param.Type;
        if (callObjectType == null)
        {
            return paramType;
        }

        if (routine.OwnerType is GenericParameterTypeSymbol genParamOwner)
        {
            var substitutions = new Dictionary<string, TypeSymbol>
            {
                [key: genParamOwner.Name] = callObjectType
            };
            return SubstituteWithMapping(type: paramType, substitutions: substitutions);
        }

        if (routine.OwnerType is { IsGenericDefinition: true })
        {
            // Owner like List[T] (gen-def) against receiver List[S64] — substitute T → S64 so
            // callback parameter types target-type lambda parameters correctly. Skip when
            // OwnerType is already a resolution (its Parameters are already substituted, applying
            // again would double-wrap).
            return SubstituteOwnerGenerics(paramType: paramType,
                lookupType: callObjectType,
                ownerType: routine.OwnerType) ?? paramType;
        }

        return paramType;
    }

    /// <summary>
    /// Reports RF-S420 (implicit wrapper copy) when a non-trivially-copyable argument is passed by
    /// reference into a non-borrow parameter without an explicit copy verb. Extracted from
    /// <see cref="AnalyzeCallArguments"/>.
    /// </summary>
    private void ValidateImplicitWrapperCopyArg(RoutineInfo routine, ParamInfo param,
        TypeSymbol paramType, Expression argExpr, Expression argValue,
        TypeSymbol argType)
    {
        // A token parameter (`Viewing[T]` / `Modifying[T]`) takes the source by reference: no copy or move
        // happens at the call site, so no verb is required.
        bool paramIsBorrow = Declaration.ImplicitToken.IsTokenParameter(parameter: paramType, tokenKind: out _, target: out _);
        if (ChecksOwnershipHere &&
            argValue is IdentifierExpression or MemberExpression &&
            !Wrappers.IsTriviallyAssignable(type: argType) && !paramIsBorrow)
        {
            (string Wrapper, string Path)? hint = Wrappers.FindNonTriviallyAssignableWrapper(type: argType);
            if (hint != null)
            {
                string verb = Wrappers.NonTriviallyAssignableWrappers[key: hint.Value.Wrapper];
                // A scoped access token (Viewing/Modifying) has NO copy verb —
                // it is a can't-escape borrow. Passing one as a call argument is always a by-reference
                // borrow (into a marker-bound / token param), never an implicit copy, so there is
                // nothing to force. Emitting RF-S420 here would ask the user to "spell out (none)".
                if (verb == Wrappers.ScopedNoEscapeHint)
                {
                    return;
                }

                string fieldNote = hint.Value.Path == "<value>"
                    ? $"argument of type '{argType.Name}' is a '{hint.Value.Wrapper}[…]' wrapper"
                    : $"field '{hint.Value.Path}' of type '{hint.Value.Wrapper}[…]'";
                ReportError(code: SemanticDiagnosticCode.ImplicitWrapperCopy,
                    message:
                    $"Implicit copy in call to '{routine.Name}': {fieldNote} requires an explicit copy verb. " +
                    $"Spell out '{verb}' at the call site, or reconstruct the record with each field's verb.",
                    location: argExpr.Location);
            }
        }
    }

    /// <summary>
    /// True when a routine argument is a CAPTURING lambda literal handed to a foreign (C::/LLVM::)
    /// routine's routine/Addr parameter — a statically-certain C-boundary capture violation. Extracted
    /// from <see cref="AnalyzeCallArguments"/>.
    /// </summary>
    private static bool IsForeignCapturingCallbackArg(RoutineInfo routine, TypeSymbol paramType,
        TypeSymbol argType, bool isCapturingLambdaLiteral)
    {
        return routine.IsForeign && argType is RoutineTypeSymbol &&
               (paramType is RoutineTypeSymbol || paramType.Name == Declaration.RuntimeContract.Addr) &&
               isCapturingLambdaLiteral;
    }

    /// <summary>
    /// Reports RF-S413 when a bare entity is passed to a consuming (bare-entity) parameter without an
    /// explicit <c>steal</c>. Extracted from <see cref="AnalyzeCallArguments"/>.
    /// </summary>
    private void ValidateBareEntityConsumingArg(RoutineInfo routine, ParamInfo param,
        TypeSymbol paramType, Expression argValue, TypeSymbol argType, bool isOperator = false)
    {
        // The old check false-positived because it looked at a stripped type; the reliable
        // signal is STRUCTURAL and read here at Phase 4, BEFORE MarkerProtocolDesugarPass strips
        // borrow params to their inner type. A consuming param is a bare EntityTypeSymbol, while
        // every borrow is a Protocol (Accessing/Controlling) or a Record wrapper
        // (Viewing/Modifying/…) — never a bare EntityTypeSymbol. Gating on EntityTypeSymbol directly
        // excludes all borrow forms with no name list. Verb-wrapped arguments (steal/copy/share)
        // are Steal/Call expressions, not Identifier/Member, so they are excluded automatically.
        // Safety comes from move tracking; this check makes the destructive transfer visible in source.
        // A builder-written body is analyzed after that strip, so a borrow parameter already reads as the bare
        // entity there (`eq(you: Accessing[Box])` as `eq(you: Box)`) and the structure says nothing.
        // In a generic body of the program a value of a type parameter not kept to values (`needs RecordType T`) may
        // be an entity, and a parameter of a type parameter keeps what it is given the same way
        // (`xs.add_last(value: x)` with `x: T`): the call hands it over, so it takes `steal` like an entity does,
        // checked on the template. The standard library's own generic bodies hand values to lookup helpers that only
        // read them, and are not judged by this.
        bool handsOverEntity = argType is EntityTypeSymbol && paramType is EntityTypeSymbol ||
                               !isOperator && argType is GenericParameterTypeSymbol &&
                               paramType is GenericParameterTypeSymbol && MayHoldEntity(type: argType) &&
                               !IsStdlibFile(filePath: _currentFilePath);
        if (ChecksHolderOf(type: argType) && !_isInCompilerGeneratedBody &&
            ReadsKeptEntity(value: argValue, includeVariables: true) && handsOverEntity)
        {
            ReportError(code: SemanticDiagnosticCode.BareEntityAssignment,
                message: KeptEntityMessage(
                    action: $"'{routine.Name}' keeps what its '{param.Name}' is given, and you are passing it",
                    value: argValue, type: argType),
                location: argValue.Location);
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> reads an entity that something else goes on owning: a variable
    /// (<c>x</c>), a field (<c>p.a</c>), or a container element (<c>boxes[0]</c>). Putting such a read into
    /// a place that owns what it holds (a variable, a field, a container slot, a consuming parameter, a
    /// returned value, a tuple) would make two owners of one entity, and for an element the container's
    /// <c>getitem</c> would quietly duplicate it first. A range slice (<c>xs[a til b]</c>) builds a fresh
    /// collection and a tuple item (<c>_t.item0</c>) is how destructuring moves out of a consumed tuple, so
    /// neither counts. <paramref name="includeVariables"/> is false where a bare variable is a move (a
    /// returned local).
    /// </summary>
    /// <summary>
    /// Whether single-ownership rules apply to the code being analyzed: the analysis language has them, and so does
    /// the language the routine is written in. A Suflae library body is analyzed in RazorForge mode, but Suflae
    /// shares its entities, so a second name of one is no second owner there.
    /// </summary>
    /// <summary>Whether putting a value of <paramref name="type"/> into a second place is checked here: everywhere a
    /// language checks every entity's holder, and in a program of a language whose entities are shared, for an entity
    /// from a `.rf` module, which is not shared there either (two names holding it would each release it).</summary>
    private bool ChecksHolderOf(TypeSymbol type)
    {
        return ChecksOwnershipHere || HoldsRazorForgeEntity(type: type);
    }

    /// <summary>Whether <paramref name="type"/> is a RazorForge library entity (`RF::System.List`) met in a program file
    /// of a language whose entities are shared. An entity of a `.rf` module the program imports is held there by a
    /// shared handle, as the language's own entities are.</summary>
    private bool HoldsRazorForgeEntity(TypeSymbol type)
    {
        return _registry.EntitiesAreShared && IsRazorForgeEntity(type: type) && !IsProgramModuleEntity(type: type) &&
               !_isInCompilerGeneratedBody && !IsStdlibFile(filePath: _currentFilePath);
    }

    /// <summary>Whether <paramref name="type"/> is an entity declared in a source file of the program (a `.rf`
    /// module it imports), not in the standard library.</summary>
    private bool IsProgramModuleEntity(TypeSymbol type)
    {
        return type is EntityTypeSymbol entity &&
               (entity.GenericDefinition ?? entity).Location?.FileName is { } file &&
               Builder.Frontends.Languages.HasSourceExtension(fileName: file) && !IsStdlibFile(filePath: file);
    }

    /// <summary>Whether <paramref name="type"/> is an entity written in RazorForge: one declared in a `.rf` file, or
    /// a RazorForge library entity with no declaring file of its own. A program's own entities of the build's
    /// language are not, whatever realm they are registered in.</summary>
    internal static bool IsRazorForgeEntity(TypeSymbol type)
    {
        if (type is not EntityTypeSymbol entity)
        {
            return false;
        }

        TypeSymbol declared = entity.GenericDefinition ?? entity;
        return declared.Location?.FileName is { } file && Builder.Frontends.Languages.HasSourceExtension(fileName: file)
            ? Builder.Frontends.Languages.OfFile(fileName: file) == TypeModel.Enums.Language.RazorForge
            : declared.Realm == TypeModel.Realms.Shared;
    }

    private bool ChecksOwnershipHere =>
        _registry.Rules.ChecksOwnership &&
        (_currentRoutine?.Location?.FileName is not { } file ||
         Builder.Frontends.Languages.For(language: Builder.Frontends.Languages.OfFile(fileName: file)).ChecksOwnership);

    private static bool ReadsKeptEntity(Expression value, bool includeVariables)
    {
        return value switch
        {
            IdentifierExpression => includeVariables,
            IndexExpression { Index: not RangeExpression } => true,
            MemberExpression { Object.ResolvedType: not TupleTypeSymbol } => true,
            _ => false
        };
    }

    /// <summary>
    /// The RF-S413 message for a read that <see cref="ReadsKeptEntity"/> flags. <paramref name="action"/>
    /// opens the sentence ("You are returning", "You are storing", ...) and the fix depends on the
    /// form: an element comes out of its container through a removing routine or is worked on where it
    /// sits, anything else is moved with <c>steal</c>. An explicit copy is offered only when the type has one.
    /// </summary>
    /// <summary>
    /// Whether a value of <paramref name="type"/> may be (or hold) a single-owner entity: an entity-kind type, or, in
    /// a generic body, one that holds a type parameter not kept to values (<c>needs RecordType T</c>), which an
    /// instantiation may make an entity. Checked on the generic body itself, so the error points at the template's
    /// own line, not at an instantiation of it.
    /// </summary>
    private bool MayHoldEntity(TypeSymbol type)
    {
        if (_registry.IsEntityKind(type: type))
        {
            return true;
        }

        if (type is not GenericParameterTypeSymbol && type.TypeArguments is not { Count: > 0 } && type is not TupleTypeSymbol)
        {
            return false;
        }

        List<GenericConstraintDeclaration> constraints =
        [
            .. _currentRoutine?.GenericConstraints ?? [],
            .. _currentRoutine?.OwnerType?.GenericConstraints ?? [],
            .. (_currentRoutine?.OwnerType as RecordTypeSymbol)?.GenericDefinition?.GenericConstraints ?? []
        ];
        return Builder.Declaration.TypeRegistry.EntityParameterIn(type: type, constraints: constraints) != null;
    }

    private string KeptEntityMessage(string action, Expression value, TypeSymbol type)
    {
        string holder = value switch
        {
            IdentifierExpression id => $"'{id.Name}'",
            IndexExpression => "its container",
            _ => "the value it belongs to"
        };
        if (!ChecksOwnershipHere && HoldsRazorForgeEntity(type: type))
        {
            return $"{action} {Article.For(name: type.Name)} '{type.Name}' that {holder} still holds. A RazorForge " +
                   "library entity is not shared: it has one original, and two names holding it would each release it. " +
                   "Make it where it goes " +
                   $"('{type.BareName}(...)' written in place), or keep working on it through {holder}.";
        }

        if (!_registry.IsEntityKind(type: type) &&
            Builder.Declaration.TypeRegistry.EntityParameterIn(type: type, constraints: []) is { } parameter)
        {
            string from = value is IdentifierExpression id
                ? $"'{id.Name}'"
                : "the value it belongs to";
            return $"{action} {Article.For(name: type.Name)} '{type.Name}' that {from} still holds. '{parameter}' may be an entity, and an " +
                   $"entity has one original, which this would hold here too with no manager. Hand it over with 'steal', or keep '{parameter}' " +
                   $"to values with 'needs RecordType {parameter}'.";
        }

        string copy = ImplementsProtocol(type: type, protocolName: "Copyable")
            ? ", or make an explicit copy with '.duplicate()'"
            : "";
        string fix = value is IndexExpression
            ? $"Take it out with a removing routine (e.g. 'remove_at'){copy}, or work on it where it sits " +
              "(a call or field write on the element reaches it in place)."
            : $"Hand it over with 'steal'{copy}, or put it under a manager to share it.";
        string written = WrittenTypeName(type: type);
        return $"{action} {Article.For(name: written)} '{written}' that {holder} still holds. An entity has one original, and " +
               $"this would hold it here too with no manager. {fix}";
    }

    /// <summary>
    /// Re-types lambda arguments after a memberRoutine-generic routine has been resolved. The initial
    /// <see cref="AnalyzeCallArguments"/> pass runs before memberRoutine-level generics are inferred, so a
    /// lambda parameter bound to a memberRoutine generic keeps it unresolved — e.g. `acc` in
    /// <c>accumulate[U](combiner: Callable[(U,T),U])</c> stays <c>U</c> rather than the inferred
    /// <c>S64</c>. Re-analyzing the lambda against the resolved parameter type rewrites
    /// <c>lambda.ResolvedType</c> with concrete parameter types so the post-processing lambda-lift
    /// mangles a defined symbol instead of one carrying an unbound generic.
    /// </summary>
    private void ReanalyzeLambdaArguments(RoutineInfo resolvedMemberRoutine,
        List<Expression> arguments, TypeSymbol? callObjectType)
    {
        IReadOnlyList<ParamInfo> parameters = resolvedMemberRoutine.Parameters;
        foreach (Expression argExpr in arguments)
        {
            Expression inner = argExpr is NamedArgumentExpression nae
                ? nae.Value
                : argExpr;
            if (inner is not LambdaExpression)
            {
                continue;
            }

            // Named arguments match by parameter name; a bare positional lambda matches the (single)
            // Routine-typed parameter — robust against the implicit `me` receiver offset.
            ParamInfo? param = argExpr is NamedArgumentExpression named
                ? parameters.FirstOrDefault(predicate: p => p.Name == named.Name)
                : parameters.FirstOrDefault(predicate: p => p.Type is RoutineTypeSymbol);
            if (param?.Type is not RoutineTypeSymbol)
            {
                continue;
            }

            TypeSymbol paramType = param.Type;
            // Owner-level generics (T) are substituted the same way AnalyzeCallArguments does, so a
            // `Callable[(U,T),U]` parameter is fully concrete once both U (memberRoutine) and T (owner) bind.
            if (callObjectType != null && resolvedMemberRoutine.OwnerType is
                    { IsGenericDefinition: true })
            {
                paramType = SubstituteOwnerGenerics(paramType: paramType,
                    lookupType: callObjectType,
                    ownerType: resolvedMemberRoutine.OwnerType) ?? paramType;
            }

            AnalyzeExpression(expression: inner, expectedType: paramType);
        }
    }

    /// <summary>
    /// Rewrites `show(x)` / `alert(x)` arguments in-place when `x` is a copy-restricted
    /// wrapper (Retained, Tracked, ...). Each such argument becomes `x.represent()`
    /// (for show) or `x.diagnose()` (for alert). The display protocols guarantee `@readonly`,
    /// so the memberRoutine call is a borrow — `x` is not consumed. The resulting `Text` matches
    /// the `show(value: Accessing[Text])` / `alert(value: Accessing[Text])` overload
    /// (value-record, no copy verb), so subsequent overload resolution picks that branch
    /// instead of the generic `show[T]` / `alert[T]` that would trigger S420.
    /// </summary>
    /// <remarks>
    /// Must run BEFORE overload resolution — rewriting after a routine has been bound to
    /// `show[T=Retained[...]]` leaves the call with a Text arg but mismatched callee, producing
    /// garbled output at runtime (the wrong function is called).
    ///
    /// Narrow scope (phase 1): only `show` and `alert` are eligible. Other `@readonly`
    /// routines are not rewritten — they don't have a canonical readonly accessor.
    /// </remarks>
    private void RewriteDisplayRoutineWrapperArgs(string callName, List<Expression> arguments)
    {
        bool isShow = callName == "show";
        bool isAlert = callName == "alert";
        if (!isShow && !isAlert)
        {
            return;
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            Expression slot = arguments[index: i];
            Expression innerExpr = slot is NamedArgumentExpression named
                ? named.Value
                : slot;

            // Type-probe before overload resolution. AnalyzeExpression is idempotent for
            // most expression kinds (the result is cached on `.ResolvedType`). Literals
            // re-analyze cheaply.
            TypeSymbol argType = AnalyzeExpression(expression: innerExpr);
            if (argType.Category == TypeCategory.Error)
            {
                continue;
            }

            // Rewrite for args that don't match the bare-Text/Bytes overloads:
            //   - copy-restricted wrappers (Retained, Tracked, …) — `Wrappers.IsTriviallyAssignable`
            //     returns false; we need the rewrite to avoid S420.
            //   - raw entities (List[T], Set[T], Dict[K,V]) — `Wrappers.IsTriviallyAssignable` returns
            //     true (fallback), but the generic `alert[T]` / `show[T]` monomorphization
            //     copies the entity ptr by value, which corrupts. Rewriting to `arg.diagnose()`
            //     extracts a Text and uses the cleaner `Accessing[Text]` overload instead.
            bool isEntity = argType is EntityTypeSymbol;
            if (!isEntity && Wrappers.IsTriviallyAssignable(type: argType))
            {
                continue;
            }

            string memberRoutineName = isAlert
                ? Declaration.RuntimeContract.Display.Diagnose
                : Declaration.RuntimeContract.Display.Represent;
            var memberAccess = new MemberExpression(Object: innerExpr,
                MemberName: memberRoutineName,
                Location: innerExpr.Location);
            var displayCall = new CallExpression(Callee: memberAccess,
                Arguments: [],
                Location: innerExpr.Location);
            // ResolvedType is left null — overload resolution will analyze the new
            // CallExpression and pick the Text-typed alert/show overload accordingly.

            arguments[index: i] = slot is NamedArgumentExpression na
                ? na with { Value = displayCall }
                : displayCall;
        }
    }

    /// <summary>
    /// Returns true if the expression can appear on the left-hand side of an assignment.
    /// Valid assignment targets are identifiers, member accesses, and index expressions.
    /// </summary>
    private static bool IsAssignableTarget(Expression target)
    {
        // SpliceMemberExpression (`obj.${m.name}`) becomes a real member access at expansion, so it is
        // an assignable target too — needed for decl-position expand column writes (`result.${m.name} = …`).
        return target is IdentifierExpression or MemberExpression or IndexExpression
            or SpliceMemberExpression;
    }

    /// <summary>
    /// True when <paramref name="argExpr"/> is a bare top-level routine NAME being passed where a
    /// <c>Addr</c> is expected — the FFI routine-pointer → C-function-pointer coercion. A bare
    /// routine reference cannot capture, so its native symbol is a valid C function pointer; codegen
    /// emits <c>ptr @&lt;routine&gt;</c>. Excludes routine-typed locals and lambdas (possible closures).
    /// </summary>
    private bool IsBareRoutineRefToCPtr(Expression argExpr, TypeSymbol argType,
        TypeSymbol paramType)
    {
        return paramType.Name == Declaration.RuntimeContract.Addr && argType is RoutineTypeSymbol &&
               argExpr is IdentifierExpression id &&
               _registry.LookupRoutineByName(name: id.Name) != null;
    }

    /// <summary>True when <paramref name="type"/> is <c>Roamed[E]</c> (record or wrapper form) for the given
    /// entity — used to treat a bare SF entity and its Roamed handle as mutually assignable.</summary>
    private static bool IsRoamedOfEntity(TypeSymbol type, EntityTypeSymbol entity)
    {
        string baseName = type switch
        {
            RecordTypeSymbol { GenericDefinition: { } gd } => gd.Name,
            _ => string.Empty
        };
        return baseName == Declaration.RuntimeContract.Roamed &&
               type.TypeArguments is [{ } inner] && inner.FullName == entity.FullName && inner.Realm == entity.Realm;
    }

    /// <summary>
    /// Returns true if a value of type <paramref name="source"/> can be assigned to a variable of type <paramref name="target"/>.
    /// Handles error types (to suppress cascading errors), generic resolution matching, and protocol conformance.
    /// No implicit numeric or widening conversions are performed.
    /// </summary>
    /// <summary>
    /// The Suflae type handed where the RazorForge type of the same name is wanted (`SF::System.List[S64]` for
    /// `RF::System.List[S64]`, either one maybe behind a shared handle), or null.
    /// </summary>
    private static TypeSymbol? OtherRealmOfSameType(TypeSymbol source, TypeSymbol target)
    {
        TypeSymbol from = SharedInner(type: source);
        TypeSymbol to = SharedInner(type: target);
        return from.FullName == to.FullName && from.Realm == "SF" && to.Realm == TypeModel.Realms.Shared
            ? from
            : null;

        static TypeSymbol SharedInner(TypeSymbol type)
        {
            return type is RecordTypeSymbol { GenericDefinition.Name: Declaration.RuntimeContract.Roamed, TypeArguments: [{ } inner] }
                ? inner
                : type;
        }
    }

    private bool IsAssignableTo(TypeSymbol source, TypeSymbol target)
    {
        if (source.Name == target.Name || source.FullName == target.FullName)
        {
            // One stdlib source makes a type in each language (`SF::System.List` and `RF::System.List`): the same name in
            // two realms is two types, which a value crosses only through a conversion (`xs.to[RF::List]()`).
            return source.Realm == target.Realm || source.Category == TypeCategory.Error;
        }

        // Shared entities: bare entity E and Roamed[E] are mutually assignable (the lowering pass
        // inserts roam).
        if (_registry.EntitiesAreShared &&
            IsEntityRoamedAssignable(source: source, target: target))
        {
            return true;
        }

        // Error types suppress cascading errors.
        if (source.Category == TypeCategory.Error || target.Category == TypeCategory.Error)
        {
            return true;
        }

        // Routine type (lambda) compatibility with covariant return type.
        if (source is RoutineTypeSymbol srcRoutine && target is RoutineTypeSymbol tgtRoutine)
        {
            return IsRoutineAssignableTo(src: srcRoutine, tgt: tgtRoutine);
        }

        // Variant auto-wrap: a value whose type matches a variant member is implicitly coerced.
        if (IsVariantMemberAssignable(source: source, target: target))
        {
            return true;
        }

        // Generic type matching.
        if (IsGenericDefinitionResolutionAssignable(source: source, target: target) ||
            IsTemplateSelfAssignable(source: source, target: target))
        {
            return true;
        }

        // Protocol conformance.
        if (target.Category == TypeCategory.Protocol)
        {
            return ImplementsProtocol(type: source, protocolName: target.Name);
        }

        // A value's token (`Viewing[S64]`) reads the value where an S64 is wanted.
        if (ValueTokenTarget(type: source) is { } tokenValue && IsAssignableTo(source: tokenValue, target: target))
        {
            return true;
        }

        // Const generic: `needs N is U64` — N is a U64 value at runtime.
        if (IsConstGenericAssignable(source: source, target: target))
        {
            return true;
        }

        // A routine value (a named routine or a lambda) goes where a `Callable[...]?` is wanted as a present one.
        if (source is RoutineTypeSymbol && IsMaybeType(type: target) &&
            target.TypeArguments is [RoutineTypeSymbol inner] && IsAssignableTo(source: source, target: inner))
        {
            return true;
        }

        // Optional auto-wrap cases.
        return IsMaybeAssignable(source: source, target: target);
    }

    /// <summary>
    /// Inside a template, <c>me</c> is typed as the generic definition (<c>SortedSet</c>, or the protocol
    /// <c>Iterable</c> in a protocol routine), while a type written in the body names the same thing with its
    /// parameters (<c>SortedSet[T]</c>, <c>Me</c>). Two resolutions of one generic type whose type arguments differ
    /// only that way (<c>Hijacked[SortedSet]</c> and <c>Hijacked[SortedSet[T]]</c>) are the same type.
    /// </summary>
    private bool IsTemplateSelfAssignable(TypeSymbol source, TypeSymbol target)
    {
        if (source is not { IsGenericResolution: true, TypeArguments: { } sourceArgs } ||
            target is not { IsGenericResolution: true, TypeArguments: { } targetArgs } ||
            source.BareName != target.BareName || source.Realm != target.Realm || sourceArgs.Count != targetArgs.Count)
        {
            return false;
        }

        for (int i = 0; i < sourceArgs.Count; i++)
        {
            TypeSymbol s = sourceArgs[index: i];
            TypeSymbol t = targetArgs[index: i];
            if (s.FullName == t.FullName && s.Realm == t.Realm || IsGenericDefinitionResolutionAssignable(source: s, target: t) ||
                IsOwnProtocolSelf(protocol: s, self: t) || IsTemplateSelfAssignable(source: s, target: t))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>me</c> in a routine of protocol <c>P</c> is typed as <c>P</c>, and <c>Me</c> is the type that obeys it: the
    /// same thing inside that protocol's own routines.
    /// </summary>
    private bool IsOwnProtocolSelf(TypeSymbol protocol, TypeSymbol self)
    {
        return self is ProtocolSelfTypeSymbol && protocol.Category == TypeCategory.Protocol &&
               _currentRoutine?.OwnerType is { } owner && owner.BareName == protocol.BareName;
    }

    private static bool IsVariantMemberAssignable(TypeSymbol source, TypeSymbol target)
    {
        // A Suflae entity is held in a variant as its handle (`Roamed[Player]`) while the program's own local is still
        // the entity itself during analysis: the two name the same member.
        TypeSymbol held = Unroamed(type: source);
        return target is VariantTypeSymbol variant && (variant.Members.Any(predicate: member =>
                   member.Type != null && (member.Type.Name == source.Name ||
                                           member.Type.FullName == source.FullName ||
                                           Unroamed(type: member.Type).FullName == held.FullName)) ||
               source is VariantTypeSymbol smaller && VariantWidensTo(source: smaller, target: variant));
    }

    /// <summary>Whether a value of <paramref name="source"/> is held as <paramref name="target"/> as it is: every type
    /// <paramref name="source"/> holds (its empty arm included) is one <paramref name="target"/> holds (<c>S64 | Text</c>
    /// as <c>S64 | Text | Bool</c>).</summary>
    internal static bool VariantWidensTo(VariantTypeSymbol source, VariantTypeSymbol target)
    {
        return source.Members.Count > 0 && !source.IsGenericDefinition && !target.IsGenericDefinition &&
               source.CarrierKind == CarrierKind.None && target.CarrierKind == CarrierKind.None &&
               source.Members.All(predicate: arm => target.Members.Any(predicate: other =>
                   arm.IsNone ? other.IsNone : other.Type?.FullName == arm.Type!.FullName));
    }

    private bool IsConstGenericAssignable(TypeSymbol source, TypeSymbol target)
    {
        return (source is GenericParameterTypeSymbol srcGen &&
                ConstGenericMatches(paramName: srcGen.Name, otherTypeName: target.Name)) ||
               (target is GenericParameterTypeSymbol tgtGen &&
                ConstGenericMatches(paramName: tgtGen.Name, otherTypeName: source.Name)) ||
               // In an instantiated body `N` is its argument (`4`), a value of the integer type it is used as.
               (source is ConstGenericValueTypeSymbol srcValue && IsIntegerType(type: target) &&
                (srcValue.ExplicitTypeName == null || srcValue.ExplicitTypeName == target.Name));
    }

    /// <summary>
    /// Returns true when the Suflae entity↔Roamed assignability rule applies: a bare entity and its
    /// <c>Roamed[E]</c> wrapper are mutually assignable in SF (the lowering pass inserts the roam call).
    /// </summary>
    private static bool IsEntityRoamedAssignable(TypeSymbol source, TypeSymbol target)
    {
        if (source is EntityTypeSymbol se && IsRoamedOfEntity(type: target, entity: se))
        {
            return true;
        }

        if (target is EntityTypeSymbol te && IsRoamedOfEntity(type: source, entity: te))
        {
            return true;
        }

        // Inside the entity's own template, `me` is the definition (`Memory`) while a handle written in the body
        // names it with its parameters (`Roamed[Memory[T]]`): the same entity.
        return source is EntityTypeSymbol { IsGenericDefinition: true } definition &&
               target is RecordTypeSymbol { GenericDefinition.Name: Declaration.RuntimeContract.Roamed,
                   TypeArguments: [{ } inner] } &&
               inner.Realm == definition.Realm &&
               IsGenericDefinitionResolutionAssignable(source: definition, target: inner);
    }

    /// <summary>
    /// Returns true when two routine types are mutually assignable: parameter types must be compatible
    /// (either direction for robustness) and the source return type must be assignable to the target
    /// return type (covariance). Extracted from <see cref="IsAssignableTo"/>.
    /// </summary>
    private bool IsRoutineAssignableTo(RoutineTypeSymbol src, RoutineTypeSymbol tgt)
    {
        if (src.ParameterTypes.Count != tgt.ParameterTypes.Count)
        {
            return false;
        }

        for (int i = 0; i < src.ParameterTypes.Count; i++)
        {
            if (!IsAssignableTo(source: src.ParameterTypes[index: i],
                    target: tgt.ParameterTypes[index: i]) && !IsAssignableTo(
                    source: tgt.ParameterTypes[index: i],
                    target: src.ParameterTypes[index: i]))
            {
                return false;
            }
        }

        if (src.ReturnType == null || tgt.ReturnType == null)
        {
            return src.ReturnType == null && tgt.ReturnType == null;
        }

        return IsAssignableTo(source: src.ReturnType, target: tgt.ReturnType);
    }

    /// <summary>
    /// Returns true when a generic-definition/resolution pair is assignable: a resolved type is
    /// assignable to its bare definition, and a generic definition is assignable to a parameterized
    /// form when all type args are unresolved generic parameters. Extracted from <see cref="IsAssignableTo"/>.
    /// </summary>
    private static bool IsGenericDefinitionResolutionAssignable(TypeSymbol source,
        TypeSymbol target)
    {
        // Resolution → definition (e.g. List[S64] → List).
        if (target.IsGenericDefinition && source.IsGenericResolution &&
            source.BareName == target.Name)
        {
            return true;
        }

        // Definition → parameterized form within a generic context (e.g. 'me: Total' → 'Total[T]').
        if (source.IsGenericDefinition &&
            target is { IsGenericResolution: true, TypeArguments: not null } &&
            target.TypeArguments.All(predicate: t => t is GenericParameterTypeSymbol) &&
            target.BareName == source.Name)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="source"/> is implicitly assignable to a <c>Optional[T]</c>
    /// <paramref name="target"/>: the None generic def to any Optional, and entity/record/wrapper types
    /// to <c>Optional[SameType]</c>. Extracted from <see cref="IsAssignableTo"/>.
    /// </summary>
    private static bool IsMaybeAssignable(TypeSymbol source, TypeSymbol target)
    {
        if (source is { IsGenericDefinition: true, Name: MaybeTypeName } &&
            IsMaybeType(type: target))
        {
            return true;
        }

        if ((source.Category == TypeCategory.Entity || source.Category == TypeCategory.Record ||
             source.Category == TypeCategory.Wrapper) && IsMaybeType(type: target) &&
            target.TypeArguments is { Count: 1 } &&
            IsAssignableToMaybeInner(source: source, maybeTarget: target))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles the <c>Optional[T]</c> auto-wrap case of <see cref="IsAssignableTo"/>: an entity/record/
    /// wrapper source is assignable to <c>Optional[SameType]</c>.
    /// The caller has already verified the source category and that <paramref name="maybeTarget"/> is a
    /// single-arg Optional.
    /// </summary>
    private static bool IsAssignableToMaybeInner(TypeSymbol source, TypeSymbol maybeTarget)
    {
        TypeSymbol typeArg = maybeTarget.TypeArguments![index: 0];
        return source.Name == typeArg.Name || source.FullName == typeArg.FullName ||
               source.FullName == typeArg.Name || source.Name == typeArg.FullName;
    }

    /// <summary>
    /// Strips the generic-arg suffix from a RAW type-name string (e.g. "List[S64]" -> "List").
    /// Prefer <see cref="TypeSymbol.BareName"/> when a TypeSymbol is in hand; this exists only for the
    /// few call sites that carry a bare string (e.g. a <c>TypeExpression.Name</c> or a protocol-name
    /// parameter) with no TypeSymbol to read <c>.BareName</c> from.
    /// </summary>
    private static string BareTypeName(string typeName)
    {
        return TypeSymbol.StripTypeArgs(name: typeName);
    }

    /// <summary>Returns true if the type is the built-in <c>Bool</c> type.</summary>
    private static bool IsBoolType(TypeSymbol type)
    {
        return type.Name is "Bool";
    }

    /// <summary>Returns true if the type is any numeric type (integer, binary float, or decimal float).</summary>
    private bool IsNumericType(TypeSymbol type)
    {
        return IsIntegerType(type: type) || IsFloatType(type: type) || IsDecimalType(type: type);
    }

    /// <summary>
    /// Returns true if the type is a generic parameter whose constraint resolves to a numeric type
    /// (e.g., a const-generic <c>N</c> declared as <c>needs N is U64</c>) or a const-generic value
    /// whose explicit type is numeric. Such parameters carry a numeric value at each
    /// monomorphization and are acceptable wherever a numeric value is expected.
    /// </summary>
    private bool IsNumericGenericParam(TypeSymbol type)
    {
        if (type is ConstGenericValueTypeSymbol)
        {
            return true;
        }

        if (type is not GenericParameterTypeSymbol gp)
        {
            return false;
        }

        // Search the active generic-constraint scope for a numeric const-generic constraint.
        // Constraints can live on the routine, the enclosing type, or on the routine's owner type
        // (e.g. Array[T,N] declares `needs N is U64`).
        IEnumerable<List<GenericConstraintDeclaration>?> sources =
        [
            _currentRoutine?.GenericConstraints,
            _currentType?.GenericConstraints,
            _currentRoutine?.OwnerType?.GenericConstraints
        ];
        return sources.Any(predicate: constraints =>
            HasNumericConstGenericConstraint(constraints: constraints, paramName: gp.Name));
    }

    /// <summary>
    /// Returns true when the given constraint list contains a const-generic constraint on
    /// <paramref name="paramName"/> whose bound resolves to a numeric type. Extracted from
    /// <see cref="IsNumericGenericParam"/>.
    /// </summary>
    private bool HasNumericConstGenericConstraint(
        IEnumerable<GenericConstraintDeclaration>? constraints, string paramName)
    {
        if (constraints == null)
        {
            return false;
        }

        foreach (GenericConstraintDeclaration c in constraints)
        {
            if (c.ParameterName != paramName || c.ConstraintType != ConstraintKind.ConstGeneric)
            {
                continue;
            }

            if (c.ConstraintTypes is not { Count: > 0 } types)
            {
                continue;
            }

            if (types.Any(predicate: boundExpr =>
                {
                    TypeSymbol? bound = LookupTypeWithImports(name: boundExpr.Name);
                    return bound != null && IsNumericType(type: bound);
                }))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the type implements the <c>Integral</c> protocol (i.e., is a fixed-width or
    /// arbitrary-precision integer type such as s32, u64, uaddr, or Suflae's Int).
    /// </summary>
    private bool IsIntegerType(TypeSymbol type)
    {
        // Check if type obeys the Integral protocol
        return ImplementsProtocol(type: type, protocolName: "Integral");
    }

    /// <summary>
    /// Returns true if the type implements the <c>BinaryFP</c> protocol (i.e., is a binary
    /// floating-point type such as b32 or b64).
    /// </summary>
    private static bool IsFloatType(TypeSymbol type)
    {
        return type.Name is "B16" or "B32" or "B64" or "B128";
    }

    /// <summary>
    /// Returns true if the type is a decimal floating-point type (D32, D64, D128, Decimal).
    /// </summary>
    private static bool IsDecimalType(TypeSymbol type)
    {
        return type.Name is "D32" or "D64" or "D128" or "Decimal";
    }

    /// <summary>Returns true if the type is a complex number type (C64, C128, C256).</summary>
    private static bool IsComplexType(TypeSymbol type)
    {
        return type.Name is "C64" or "C128" or "C256";
    }

    /// <summary>
    /// Checks if a type supports a specific binary operator by looking up the operator memberRoutine.
    /// </summary>
    /// <summary>
    /// Unwraps a token (<c>Viewing[T]</c> / <c>Modifying[T]</c>) to its referent <c>T</c>, returns the type
    /// unchanged otherwise. Comparison operands that are tokens are treated as their referent.
    /// </summary>
    private static TypeSymbol UnwrapBorrowProtocol(TypeSymbol type)
    {
        if (IsAccessToken(type: type) && type.TypeArguments is { Count: > 0 } args)
        {
            return args[index: 0];
        }

        return type;
    }

    /// <summary>Whether <paramref name="type"/> is a token on a value where it is stored (<c>Viewing[T]</c>,
    /// <c>Modifying[T]</c>): the address of that place.</summary>
    private static bool IsAccessToken(TypeSymbol type)
    {
        return type is RecordTypeSymbol token &&
               (token.GenericDefinition ?? token).BareName is Declaration.RuntimeContract.Viewing
                   or Declaration.RuntimeContract.Modifying;
    }

    /// <summary>
    /// The value a value's token reads (<c>Viewing[S64]</c> reads an <c>S64</c>), or null for anything else. Where
    /// the value itself is wanted (an operator, a comparison, an argument, a declared-type variable, a return) the
    /// token reads it (TokenReadLoweringPass inserts <c>access()</c>). An entity's token gives none: reading the
    /// entity out would make a second owner of it.
    /// </summary>
    private TypeSymbol? ValueTokenTarget(TypeSymbol type)
    {
        if (ProjectionReadTarget(type: type) is { } projected)
        {
            return projected;
        }

        return IsAccessToken(type: type) && type.TypeArguments is [{ } value] && value is not EntityTypeSymbol
            ? value
            : null;
    }

    /// <summary>The entity an entity's token is on (<c>Modifying[Random]</c> is on a <c>Random</c>), or null for
    /// anything else.</summary>
    private static TypeSymbol? EntityTokenTarget(TypeSymbol type)
    {
        return IsAccessToken(type: type) && type.TypeArguments is [EntityTypeSymbol entity] ? entity : null;
    }

    private bool SupportsOperator(TypeSymbol type, BinaryOperator op)
    {
        // Choice types support ==/!= natively: they carry a discrete integer discriminant, and
        // ExpressionLoweringPass lowers `a == b` / `a != b` to an S32 tag compare (there is no `eq`
        // member routine to find, so the routine-lookup below would spuriously reject them).
        if (type is ChoiceTypeSymbol &&
            op is BinaryOperator.Equal or BinaryOperator.NotEqual)
        {
            return true;
        }

        // Check the BASE wired memberRoutine, not the derived one: `!=`/`==` are both backed by `eq`
        // (ne is auto-derived from eq), and all ordering operators are backed by `compare`
        // (lt/le/gt/ge are auto-derived). Protocols (Equatable/Comparable) declare only the
        // base memberRoutine, so checking the derived name would spuriously fail for constrained generics
        // (e.g. `me[i] != other[i]` inside `List[T].eq needs T obeys Equatable`).
        string? memberRoutineName = op switch
        {
            BinaryOperator.Equal or BinaryOperator.NotEqual => "eq",
            BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater
                or BinaryOperator.GreaterEqual or BinaryOperator.ThreeWayComparator => "compare",
            _ => op.GetMemberRoutineName()
        };
        if (memberRoutineName == null)
        {
            return false;
        }

        // Use LookupMemberRoutine which handles generic resolutions (e.g., Hijacked[Point].eq).
        // A resolution whose owner is a ProtocolTypeSymbol is the ABSTRACT protocol declaration
        // (RF protocols have no default implementations) — for a CONCRETE receiver it would link
        // to nothing (e.g. `record Cat` with no `eq` resolving `==` to `Equatable.eq`). Only a
        // concrete implementation counts as support here; generic-parameter receivers get their
        // constraint-based support from the dedicated branch below.
        // A type with several routines of the name (`Decimal.eq(you: Decimal)` and `Decimal.eq(you: Int)`)
        // supports the operator when one of them takes its own type.
        RoutineInfo? resolved =
            _registry.LookupMemberRoutine(type: type, memberRoutineName: memberRoutineName) ??
            _registry.LookupMemberRoutineOverload(type: type, memberRoutineName: memberRoutineName, argTypes: [type]);
        if (resolved != null && resolved.OwnerType is not ProtocolTypeSymbol)
        {
            return true;
        }

        // Phase D: transparent wrappers (T, etc.) forward operator wired memberRoutines
        // to the inner T's implementation. Synthesize the forwarder lazily.
        if (Wrappers.IsWrapperType(type: type) && TrySynthesizeWrapperForwarder(wrapperType: type,
                memberRoutineName: memberRoutineName,
                isFailable: false) != null)
        {
            return true;
        }

        // For generic parameters, check if any constrained protocol declares the memberRoutine.
        if (type is GenericParameterTypeSymbol)
        {
            return GenericParamConstraintSupportsMemberRoutine(paramName: type.Name,
                memberRoutineName: memberRoutineName);
        }

        return false;
    }

    /// <summary>
    /// Returns true when an active constraint on the named generic parameter grants support for
    /// <paramref name="memberRoutineName"/>: an <c>obeys P</c> whose protocol declares the routine, or
    /// a <c>needs N is U64</c> const-generic whose underlying value type has it. Extracted from
    /// <see cref="SupportsOperator"/>.
    /// </summary>
    private bool GenericParamConstraintSupportsMemberRoutine(string paramName,
        string memberRoutineName)
    {
        foreach (GenericConstraintDeclaration c in ActiveConstraintsFor(paramName: paramName))
        {
            if (ObeysConstraintSupportsMemberRoutine(c: c, memberRoutineName: memberRoutineName))
            {
                return true;
            }

            if (ConstGenericConstraintSupportsMemberRoutine(c: c,
                    memberRoutineName: memberRoutineName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when an <c>obeys P</c> constraint's protocol (transitively) declares
    /// <paramref name="memberRoutineName"/>. Extracted from <see cref="GenericParamConstraintSupportsMemberRoutine"/>.
    /// </summary>
    private bool ObeysConstraintSupportsMemberRoutine(GenericConstraintDeclaration c,
        string memberRoutineName)
    {
        if (c is not { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null })
        {
            return false;
        }

        return c.ConstraintTypes.Any(predicate: protocolExpr =>
        {
            TypeSymbol? proto = _registry.LookupType(name: protocolExpr.Name);
            return proto is ProtocolTypeSymbol &&
                   ProtocolDeclaresMemberRoutine(proto: proto,
                       memberRoutineName: memberRoutineName);
        });
    }

    /// <summary>
    /// Returns true when a <c>needs N is T</c> const-generic constraint's underlying value type has
    /// <paramref name="memberRoutineName"/>. Extracted from <see cref="GenericParamConstraintSupportsMemberRoutine"/>.
    /// </summary>
    private bool ConstGenericConstraintSupportsMemberRoutine(GenericConstraintDeclaration c,
        string memberRoutineName)
    {
        if (c is not { ConstraintType: ConstraintKind.ConstGeneric, ConstraintTypes: not null })
        {
            return false;
        }

        return c.ConstraintTypes.Any(predicate: ct =>
        {
            TypeSymbol? underlying = _registry.LookupType(name: ct.Name);
            return underlying != null && underlying.Category != TypeCategory.Protocol &&
                   _registry.LookupMemberRoutine(type: underlying,
                       memberRoutineName: memberRoutineName) != null;
        });
    }

    /// <summary>
    /// Returns true if the protocol (or any protocol it transitively obeys) declares a memberRoutine
    /// matching the given name. e.g. <c>Comparable</c> obeys <c>Equatable</c>, so
    /// <c>eq</c> is reachable through <c>Comparable</c>.
    /// </summary>
    private static bool ProtocolDeclaresMemberRoutine(TypeSymbol proto, string memberRoutineName,
        HashSet<string>? visited = null)
    {
        if (proto is not ProtocolTypeSymbol p)
        {
            return false;
        }

        visited ??= new HashSet<string>(comparer: StringComparer.Ordinal);
        if (!visited.Add(item: p.Name))
        {
            return false;
        }

        if (p.MemberRoutines.Any(predicate: m => m.Name == memberRoutineName))
        {
            return true;
        }

        return p.ParentProtocols.Any(predicate: parent =>
            ProtocolDeclaresMemberRoutine(proto: parent,
                memberRoutineName: memberRoutineName,
                visited: visited));
    }

    /// <summary>
    /// The value type of a const generic parameter (<c>COUNT</c> under <c>needs COUNT is U64</c> is a <c>U64</c>),
    /// or null when <paramref name="type"/> is not one. A bare number beside it takes that type.
    /// </summary>
    private TypeSymbol? ConstGenericValueType(TypeSymbol type)
    {
        if (type is not GenericParameterTypeSymbol parameter)
        {
            return null;
        }

        foreach (GenericConstraintDeclaration c in ActiveConstraintsFor(paramName: parameter.Name))
        {
            if (c is not { ConstraintType: ConstraintKind.ConstGeneric, ConstraintTypes: { Count: > 0 } bounds })
            {
                continue;
            }

            foreach (TypeExpression bound in bounds)
            {
                if (LookupTypeWithImports(name: bound.Name) is { } valueType && IsNumericType(type: valueType))
                {
                    return valueType;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if the named generic parameter has a `is &lt;TypeName&gt;` const-generic
    /// constraint matching <paramref name="otherTypeName"/>. Note: ConstraintKind.ConstGeneric is
    /// also used for `is EntityType`/`is RecordType` etc., which don't represent value types —
    /// callers handle those via Category checks first.
    /// </summary>
    private bool ConstGenericMatches(string paramName, string otherTypeName)
    {
        return ActiveConstraintsFor(paramName: paramName)
              .Where(predicate: c => c is
                   { ConstraintType: ConstraintKind.ConstGeneric, ConstraintTypes: not null })
              .Any(predicate: c =>
                   c.ConstraintTypes!.Any(predicate: ct => ct.Name == otherTypeName));
    }

    /// <summary>
    /// Yields all active generic constraints for the named parameter from the current routine
    /// and its owner type.
    /// </summary>
    private IEnumerable<GenericConstraintDeclaration> ActiveConstraintsFor(string paramName)
    {
        if (_currentRoutine?.GenericConstraints != null)
        {
            foreach (GenericConstraintDeclaration c in _currentRoutine.GenericConstraints)
            {
                if (c.ParameterName == paramName)
                {
                    yield return c;
                }
            }
        }

        TypeSymbol? ownerType = _currentRoutine?.OwnerType;
        if (ownerType?.GenericConstraints != null)
        {
            foreach (GenericConstraintDeclaration c in ownerType.GenericConstraints)
            {
                if (c.ParameterName == paramName)
                {
                    yield return c;
                }
            }
        }

        // A language without single ownership (Suflae) has no "can this be copied?" question: a record is
        // copied and an entity is a shared handle (a record too), so every type parameter is copied by `=`.
        if (_currentRoutine?.Location is { FileName: { } file } site &&
            !Builder.Frontends.Languages.For(language: Builder.Frontends.Languages.OfFile(fileName: file)).ChecksOwnership)
        {
            yield return new GenericConstraintDeclaration(ParameterName: paramName,
                ConstraintType: ConstraintKind.RecordType,
                ConstraintTypes: null,
                Location: site);
        }
    }

    /// <summary>
    /// Checks if an operator is a comparison operator that returns Bool.
    /// Includes both identity operators and overloadable comparison/membership operators.
    /// Note: ThreeWayComparator (&lt;=&gt;) returns ComparisonSign, not Bool, so it is excluded.
    /// </summary>
    private static bool IsComparisonOperator(BinaryOperator op)
    {
        return op is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less
            or BinaryOperator.LessEqual or BinaryOperator.Greater or BinaryOperator.GreaterEqual
            or BinaryOperator.In or BinaryOperator.NotIn
            or BinaryOperator.Is or BinaryOperator.IsNot or BinaryOperator.Obeys;
    }

    /// <summary>Returns true if the operator is a short-circuit logical operator (<c>and</c> or <c>or</c>).</summary>
    private static bool IsLogicalOperator(BinaryOperator op)
    {
        return op is BinaryOperator.And or BinaryOperator.Or;
    }

    private static bool IsShiftOperator(BinaryOperator op)
    {
        return op is BinaryOperator.ArithmeticLeftShift or BinaryOperator.ArithmeticRightShift
            or BinaryOperator.LogicalRightShift;
    }

    /// <summary>
    /// Operator wired memberRoutines that choices are NOT allowed to define or call.
    /// Choices do not support any operators — use 'is' for case matching.
    /// </summary>
    private static readonly HashSet<string> OperatorWiredMemberRoutines =
    [
        // Arithmetic
        "add", "sub", "mul", "truediv", "floordiv", "mod", "pow",
        // Wrapping arithmetic
        "add_wrap", "sub_wrap", "mul_wrap", "pow_wrap",
        // Clamping arithmetic
        "add_clamp", "sub_clamp", "mul_clamp", "truediv_clamp", "pow_clamp",
        // Comparison
        "eq", "ne", "lt", "le", "gt", "ge", "compare",
        // Bitwise
        "bitand", "bitor", "bitxor",
        "shift_left", "arith_shift_right", "logical_shift_right",
        // Unary
        "neg", "bitnot",
        // Membership
        "has", "lacks", "has_key", "lacks_key",
        // Indexing
        "getitem", "setitem",
        // Iteration
        "emitter", "emit",
        // Context management
        "enter", "exit"
    ];

    /// <summary>Returns true if the given memberRoutine name is an operator wired (e.g., <c>add</c>, <c>eq</c>).</summary>
    private static bool IsOperatorWired(string name)
    {
        return OperatorWiredMemberRoutines.Contains(value: name);
    }

    /// <summary>
    /// Validates the operators that have their OWN operand rules and do not fall through to the generic
    /// compatibility/wired-support checks: variant (<c>is</c>/<c>isnot</c> only), membership
    /// (<c>in</c>/<c>notin</c> → right has <c>contains</c>), and container-first (<c>have</c>/<c>lack</c>
    /// → left is a flags type or has <c>contains</c>). Returns true when one of these applied (the caller
    /// returns), false when the caller should continue with the generic checks.
    /// </summary>
    private bool ValidateVariantOrMembershipOperands(TypeSymbol left, TypeSymbol right,
        BinaryOperator op, SourceLocation location)
    {
        // Variants cannot use equality or ordering operators (only 'is' and 'isnot')
        if (left.Category == TypeCategory.Variant || right.Category == TypeCategory.Variant)
        {
            if (op is not (BinaryOperator.Is or BinaryOperator.IsNot))
            {
                ReportError(code: SemanticDiagnosticCode.ComparisonOnVariantType,
                    message:
                    $"Comparison operator '{op.ToStringRepresentation()}' cannot be used with variant types. Use 'is' or 'isnot' for pattern matching.",
                    location: location);
            }

            return true;
        }

        // Membership operators (in, notin): check that right has contains accepting left
        if (op is BinaryOperator.In or BinaryOperator.NotIn)
        {
            if (LookupContains(container: right, element: left) == null)
            {
                ReportError(code: SemanticDiagnosticCode.IncompatibleComparisonTypes,
                    message:
                    $"Type '{right.Name}' does not support 'in'/'notin' (no 'has' member routine).",
                    location: location);
            }

            return true;
        }

        return false;
    }

    /// <summary>The <c>has</c> a membership operator calls on <paramref name="container"/>: picked by the element
    /// type, since a container may declare several (<c>Text.has</c> takes a <c>Char</c> or a <c>Text</c>) and a
    /// name alone cannot choose among them. A map has <c>has_key</c> instead.</summary>
    private RoutineInfo? LookupContains(TypeSymbol container, TypeSymbol element)
    {
        // A shared entity's handle answers for the entity (`other have v` on a `Roamed[Set[T]]`).
        if (container is RecordTypeSymbol { GenericDefinition.Name: Declaration.RuntimeContract.Roamed,
                TypeArguments: [EntityTypeSymbol shared] })
        {
            container = shared;
        }

        // A map answers for its keys (`d have k` is `d.has_key(k)`).
        return _registry.LookupMemberRoutineOverload(type: container,
                   memberRoutineName: "has",
                   argTypes: [element]) ??
               _registry.LookupMemberRoutine(type: container, memberRoutineName: "has") ??
               _registry.LookupMemberRoutineOverload(type: container,
                   memberRoutineName: "has_key",
                   argTypes: [element]);
    }

    /// <summary>
    /// Validates comparison operands for type compatibility and operator support.
    /// Called from both AnalyzeBinaryExpression (for non-desugared operators like is, obeys)
    /// and AnalyzeChainedComparisonExpression (for chained comparisons like a &lt; b &lt; c).
    /// </summary>
    private void ValidateComparisonOperands(TypeSymbol left, TypeSymbol right, BinaryOperator op,
        SourceLocation location)
    {
        // A `Accessing[T]` / `Controlling[T]` operand is a borrow that transparently forwards to its
        // referent: comparing it auto-dispatches `refer()` / `control()` to the inner `T`. Compare
        // against that referent so e.g. `me[i] == value` (with `value: Accessing[T]`) type-checks as
        // `T == T` and resolves operator support on `T`.
        left = UnwrapBorrowProtocol(type: left);
        right = UnwrapBorrowProtocol(type: right);

        // Variant / membership / container-first operators are validated on their own terms and DO NOT
        // fall through to the compatibility + wired-support checks below.
        if (ValidateVariantOrMembershipOperands(left: left, right: right, op: op, location: location))
        {
            return;
        }

        if (ReportAddressOperator(left: left, right: right, op: op, location: location))
        {
            return;
        }

        // Check that types are compatible (same type or error type). A type that writes its comparison against the
        // other type itself (Suflae's `Int.compare(you: Decimal)`) compares with it.
        string? comparison = op is BinaryOperator.Equal or BinaryOperator.NotEqual ? "eq" : "compare";
        if (!IsAssignableTo(source: left, target: right) &&
            !IsAssignableTo(source: right, target: left) &&
            !DeclaresOperatorRoutine(receiverType: left, operatorMemberRoutine: comparison, argType: right))
        {
            ReportError(code: SemanticDiagnosticCode.IncompatibleComparisonTypes,
                message:
                $"You compare {Article.For(name: left.Name)} '{left.Name}' with {Article.For(name: right.Name)} " +
                $"'{right.Name}', and values of these two types don't compare with each other. Convert one to the " +
                $"other's type first ('.to[{left.Name}]()' on the right one, or '.to[{right.Name}]()' on the left one).",
                location: location);
        }

        // For overloadable ordering/equality operators, verify the type actually implements the
        // backing wired memberRoutine (eq for ==/!=, compare for </<=/>/>=). These are desugared to memberRoutine
        // calls by OperatorLoweringPass (after SA), so without this check an unsupported operator
        // would slip past SA and surface as an undefined-symbol LINKERR at codegen — e.g. a record
        // with no eq whose `==` resolves to the abstract `Equatable.eq`. A LINKERR on SA-passing
        // code is a builder bug; catch it here with a clean diagnostic.
        if (op is not (BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater
            or BinaryOperator.GreaterEqual or BinaryOperator.Equal or BinaryOperator.NotEqual))
        {
            return;
        }

        if (!SupportsOperator(type: left, op: op))
        {
            ReportError(code: SemanticDiagnosticCode.OrderingNotSupported,
                message: MissingEqualityMessage(type: left, op: op) ??
                         $"Type '{left.Name}' does not support comparison operator '{op.ToStringRepresentation()}'.",
                location: location);
        }
    }

    /// <summary>
    /// The message for <c>==</c> / <c>!=</c> on a record or entity that has no <c>==</c> because a member variable has
    /// none: it names that member variable and its type. Null for any other operator or type, or when every member
    /// variable has <c>==</c>.
    /// </summary>
    private string? MissingEqualityMessage(TypeSymbol type, BinaryOperator op)
    {
        if (op is not (BinaryOperator.Equal or BinaryOperator.NotEqual) ||
            MemberVariableWithout(type: type, protocolName: "Equatable") is not ({ } owner, { } missing))
        {
            return null;
        }

        string kind = owner is EntityTypeSymbol ? "An entity" : "A record";
        return $"You compared two '{owner.Name}' values with '{op.ToStringRepresentation()}', but '{owner.Name}' " +
               $"has no '==': {LackingMemberText(missing: missing, protocolName: "Equatable", what: "'=='")}. {kind} " +
               $"has '==' when every member variable has one. Write 'routine {owner.Name}.eq(you: " +
               $"{owner.Name}) -> Bool' to say when two are equal, or compare the member variables you mean.";
    }

    /// <summary>
    /// What a member variable without <paramref name="what"/> (<c>'=='</c>, <c>hash</c>) is, down to the member variable
    /// that really lacks it: <c>its member variable 'b' is a 'B', which has none: 'b.c.f' is a 'Callable[(), None]',
    /// which has no '=='</c>. A record or entity lacks it through one of its own member variables, so the path follows
    /// those until a type that lacks it by itself.
    /// </summary>
    internal string LackingMemberText(MemberVariableInfo missing, string protocolName, string what)
    {
        var path = new List<MemberVariableInfo> { missing };
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        TypeSymbol next = Held(type: missing.Type);
        while (seen.Add(item: next.FullName) &&
               MemberVariableWithout(type: next, protocolName: protocolName) is (_, { } inner))
        {
            path.Add(item: inner);
            next = Held(type: inner.Type);
        }

        string first = Shown(field: missing);
        if (path.Count == 1)
        {
            return $"its member variable '{missing.Name}' is {Article.For(name: first)} '{first}', which has no {what}";
        }

        MemberVariableInfo last = path[^1];
        string lastType = Shown(field: last);
        return $"its member variable '{missing.Name}' is {Article.For(name: first)} '{first}', which has none: " +
               $"'{string.Join(separator: ".", values: path.Select(selector: f => f.Name))}' is " +
               $"{Article.For(name: lastType)} '{lastType}', which has no {what}";

        // `A?` is written for an optional member variable, and its own member variables are the `A`'s.
        static TypeSymbol Held(TypeSymbol type)
        {
            return type is { BareName: "Optional", TypeArguments: [{ } inner] } ? inner : type;
        }

        static string Shown(MemberVariableInfo field)
        {
            return field.Type is { BareName: "Optional", TypeArguments: [{ } inner] }
                ? MemberTypeName(type: inner) + "?"
                : MemberTypeName(type: field.Type) + (field.IsNullable ? "?" : "");
        }
    }

    /// <summary>A member variable's type as its declaration writes it, without modules (a routine type as its
    /// signature).</summary>
    internal static string MemberTypeName(TypeSymbol type)
    {
        return type.ShortTypeName;
    }

    /// <summary>The record or entity <paramref name="type"/> stands for (the entity a program's handle holds) and its
    /// first member variable whose type lacks what <paramref name="protocolName"/> asks for (<c>==</c> for
    /// <c>Equatable</c>, a hash for <c>Hashable</c>), or null when every one has it.</summary>
    internal (TypeSymbol Owner, MemberVariableInfo Field)? MemberVariableWithout(TypeSymbol type, string protocolName)
    {
        TypeSymbol owner = _registry.EntityOfHandleTakenAsMe(type: type) ?? type;
        List<MemberVariableInfo> fields = owner switch
        {
            VariantTypeSymbol or ChoiceTypeSymbol or FlagsTypeSymbol => [],
            RecordTypeSymbol record => record.MemberVariables,
            EntityTypeSymbol entity => entity.MemberVariables,
            _ => []
        };
        MemberVariableInfo? missing = fields.FirstOrDefault(predicate: f =>
            !_registry.MemberVariableHasValueRoutine(type: f.Type, protocolName: protocolName));
        return missing == null
            ? null
            : (owner, missing);
    }

    /// <summary>
    /// Reports an operator on an <c>Addr</c> operand that addresses don't have, pointing at what is written instead: an
    /// address names a place, so two are compared by identity (<c>===</c> / <c>!==</c>), it moves by a
    /// <c>ByteSize</c> with <c>+%</c> / <c>-%</c>, and <c>b -% a</c> is the distance from <c>a</c> to <c>b</c>. It has
    /// no ordering and no other arithmetic. Returns whether it reported one.
    /// </summary>
    private bool ReportAddressOperator(TypeSymbol left, TypeSymbol right, BinaryOperator op, SourceLocation location)
    {
        string addr = Declaration.RuntimeContract.Addr;
        if (left.BareName != addr && right.BareName != addr ||
            op is BinaryOperator.IdentityEqual or BinaryOperator.IdentityNotEqual or BinaryOperator.Assign or
                BinaryOperator.And or BinaryOperator.Or or BinaryOperator.NoneCoalesce)
        {
            return false;
        }

        // The wrapping moves and the distance: Addr +% ByteSize, Addr -% ByteSize, Addr -% Addr.
        bool moves = left.BareName == addr && right.BareName == "ByteSize" &&
                     op is BinaryOperator.AddWrap or BinaryOperator.SubtractWrap;
        bool measures = left.BareName == addr && right.BareName == addr && op is BinaryOperator.SubtractWrap;
        if (moves || measures)
        {
            return false;
        }

        string written = op.ToStringRepresentation();
        string message = op switch
        {
            BinaryOperator.Equal or BinaryOperator.NotEqual =>
                $"'{written}' compares two values, and an 'Addr' names a place rather than holding a value. Ask " +
                $"whether two addresses name the same place with '{(op == BinaryOperator.Equal ? "===" : "!==")}', or " +
                "whether one is the address 0 with 'is_hollow()'.",
            BinaryOperator.Add or BinaryOperator.Subtract =>
                $"'{written}' isn't defined on an 'Addr': an address moves by a 'ByteSize' only with '+%' and '-%', " +
                "which wrap around and check no bounds, and 'b -% a' is the distance from 'a' to 'b'. Write " +
                $"'{(op == BinaryOperator.Add ? "+%" : "-%")}' here.",
            _ =>
                $"'{written}' has no meaning on an 'Addr': an address names a place, with no ordering. It moves by a " +
                "'ByteSize' with '+%' / '-%', and 'b -% a' is the distance from 'a' to 'b'."
        };
        ReportError(code: SemanticDiagnosticCode.OrderingNotSupported, message: message, location: location);
        return true;
    }

    /// <summary>
    /// Validates that a chained comparison expression uses operators in a consistent direction.
    /// Valid patterns:
    /// - All ascending: a &lt; b &lt; c, a &lt;= b &lt; c, a == b &lt; c
    /// - All descending: a &gt; b &gt; c, a &gt;= b &gt; c, a == b &gt; c
    /// - Equality only: a == b == c
    /// Invalid: mixing ascending and descending (a &lt; b &gt; c)
    /// </summary>
    private void ValidateComparisonChain(ChainedComparisonExpression chain,
        SourceLocation location)
    {
        if (chain.Operators.Count < 2)
        {
            return; // No chain to validate
        }

        bool? isAscending = null;

        foreach (BinaryOperator op in chain.Operators)
        {
            // Equality operators are direction-neutral
            if (op == BinaryOperator.Equal)
            {
                continue;
            }

            // NotEqual cannot be used in chains, nor `<=>`, which gives an ordering rather than a truth
            if (op is BinaryOperator.NotEqual or BinaryOperator.ThreeWayComparator)
            {
                ReportError(code: SemanticDiagnosticCode.NotEqualInComparisonChain,
                    message: op == BinaryOperator.NotEqual
                        ? "The '!=' operator cannot be used in comparison chains."
                        : "The '<=>' operator gives an ordering, which no comparison chains on.",
                    location: location);
                return;
            }

            if (!TryTrackComparisonDirection(op: op,
                    isAscending: ref isAscending,
                    location: location))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Folds one directional comparison operator into the running <paramref name="isAscending"/>
    /// state, reporting a mixed-direction error if it conflicts. Returns false when a conflict was
    /// reported (the caller must stop). Extracted from <see cref="ValidateComparisonChain"/>.
    /// </summary>
    private bool TryTrackComparisonDirection(BinaryOperator op, ref bool? isAscending,
        SourceLocation location)
    {
        bool opIsAscending = op is BinaryOperator.Less or BinaryOperator.LessEqual;
        bool opIsDescending = op is BinaryOperator.Greater or BinaryOperator.GreaterEqual;

        if (opIsAscending)
        {
            if (isAscending == false)
            {
                ReportError(code: SemanticDiagnosticCode.MixedComparisonChainDirection,
                    message:
                    "Cannot mix ascending (<, <=) and descending (>, >=) operators in a comparison chain.",
                    location: location);
                return false;
            }

            isAscending = true;
        }
        else if (opIsDescending)
        {
            if (isAscending == true)
            {
                ReportError(code: SemanticDiagnosticCode.MixedComparisonChainDirection,
                    message:
                    "Cannot mix ascending (<, <=) and descending (>, >=) operators in a comparison chain.",
                    location: location);
                return false;
            }

            isAscending = false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the element type produced by iterating over <paramref name="iterableType"/>.
    /// The type must implement the <c>Iterable</c> protocol, whose <c>iter</c> returns a <c>Iterator[T]</c>.
    /// The element type is taken from the return type of the <c>iter</c> memberRoutine or the type's first generic argument.
    /// Reports an error and returns <see cref="ErrorTypeSymbol"/> if the type is not iterable or the element type cannot be determined.
    /// </summary>
    private TypeSymbol GetIterableElementType(TypeSymbol iterableType, SourceLocation location)
    {
        // A token on an iterable (`items: Viewing[List[T]]`) is iterated as the iterable it points at.
        if (iterableType.BareName is Declaration.RuntimeContract.Viewing or Declaration.RuntimeContract.Modifying &&
            iterableType.TypeArguments is [{ } tokenTarget])
        {
            iterableType = tokenTarget;
        }

        // A shared entity is iterated through its handle: the entity's own iterator is what walks it.
        if (iterableType is RecordTypeSymbol
            {
                GenericDefinition.Name: Declaration.RuntimeContract.Roamed, TypeArguments: [{ } sharedIterable]
            })
        {
            iterableType = sharedIterable;
        }

        // Protocol-typed receiver: if the static type IS `Iterable[T]` (or a
        // protocol that obeys Iterable), trust the dispatch and take the
        // element type from the type-arg. Any concrete value bound will
        // implement Iterable structurally.
        // What its iterator hands out is the iterator's `Item` (`S/Iter/Item`), which reads as the element type
        // and becomes the concrete iterator's `Item` (a token on a stored element, or a value) once the body is
        // instantiated.
        if (TryGetProtocolIterableElement(type: iterableType) is not null)
        {
            return IteratorItemOf(iterable: iterableType);
        }

        // Generic-parameter receiver constrained to Iterable[X]: the same `Item` projection, which reads as X.
        if (iterableType is GenericParameterTypeSymbol gp &&
            TryGetIterableElementFromGenericConstraint(paramName: gp.Name) is not null)
        {
            return IteratorItemOf(iterable: gp);
        }

        // Verify the type follows the Iterable protocol (or has an iter member routine).
        if (!IsOrObeysIterable(iterableType: iterableType))
        {
            ReportError(code: SemanticDiagnosticCode.TypeNotIterable,
                message: $"You walk '{iterableType.Name}' with 'for' here, but it has no 'emitter()'. A type that " +
                         "obeys 'Emittable' can be walked.",
                location: location);
            return ErrorTypeSymbol.Instance;
        }

        // Strategy 1 (ground truth): the element is exactly what the iterator's `emit!` returns: a token on a
        // stored element (`Modifying[T]`) where the protocol only says `T`.
        // Resolve `iterable.iter()` to the concrete iterator type, then that iterator's `emit!`
        // return type. This mirrors the for-loop lowering (IteratorInlineLoweringPass) and, unlike
        // Strategy 1, does NOT depend on the instance's ImplementedProtocols being populated — so it
        // works for a generic-instance collection (e.g. `Dict[Text, SerialValue]`) iterated inside a
        // CONCRETE memberRoutine compiled before that instance is monomorphized. There, ImplementedProtocols
        // is still empty and the naive `TypeArguments[0]` fallback below would wrongly pick the first
        // type arg (`K`, i.e. `Text` for a Dict) instead of `DictEntry[Text, SerialValue]`.
        RoutineInfo? iterMemberRoutine =
            _registry.LookupMemberRoutine(type: iterableType, memberRoutineName: "emitter");
        if (iterMemberRoutine?.ReturnType is { } iteratorType and not ErrorTypeSymbol)
        {
            // An iterator that is a shared entity comes back as its handle: its `emit!` is the entity's own.
            if (iteratorType is RecordTypeSymbol
                {
                    GenericDefinition.Name: Declaration.RuntimeContract.Roamed, TypeArguments: [{ } sharedIterator]
                })
            {
                iteratorType = sharedIterator;
            }

            RoutineInfo? emitMemberRoutine = _registry.LookupMemberRoutine(type: iteratorType,
                memberRoutineName: "emit",
                isFailable: true);
            if (emitMemberRoutine?.ReturnType is { } emittedType
                and not (ErrorTypeSymbol or GenericParameterTypeSymbol))
            {
                return emittedType;
            }
        }

        // Strategy 1.5: the element type from Iterable[X] protocol conformance.
        // This correctly handles chained generics like EnumerateIterator[T] obeys Iterable[Tuple[S64, T]]
        TypeSymbol? fromProtocols = TryGetElementFromIterableProtocols(iterableType: iterableType);
        if (fromProtocols != null)
        {
            return fromProtocols;
        }

        // Strategy 2: Look for iter memberRoutine to get element type from Iterator[T] return type
        TypeSymbol? fromIterReturn = TryGetElementFromIterReturnType(iterableType: iterableType);
        if (fromIterReturn != null)
        {
            return fromIterReturn;
        }

        // Fallback to type arguments if iter memberRoutine not found but protocol is implemented
        if (iterableType.TypeArguments is { Count: > 0 })
        {
            return iterableType.TypeArguments[index: 0];
        }

        ReportError(code: SemanticDiagnosticCode.TypeNotIterable,
            message:
            $"The builder cannot tell what '{iterableType.Name}' emits: its 'emitter()' must return an 'Emitter[T]'.",
            location: location);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>The `Item` of <paramref name="iterable"/>'s iterator: the projection <c>S/Iter/Item</c>.</summary>
    private static TypeSymbol IteratorItemOf(TypeSymbol iterable)
    {
        return new AssociatedProjectionTypeSymbol(
            baseType: new AssociatedProjectionTypeSymbol(baseType: iterable, slotName: "Emitter"),
            slotName: "Item");
    }

    private static TypeSymbol? TryGetProtocolIterableElement(TypeSymbol type)
    {
        return type is ProtocolTypeSymbol { TypeArguments: { Count: > 0 } arguments } protocol &&
               (protocol.GenericDefinition ?? protocol).BareName == IterableProtocolName
            ? arguments[index: 0]
            : null;
    }

    /// <summary>
    /// Walks the <c>Obeys Iterable[X]</c> constraints on the generic parameter named
    /// <paramref name="paramName"/> and returns the element type <c>X</c> when found.
    /// Returns null if no such constraint exists.
    /// </summary>
    private TypeSymbol? TryGetIterableElementFromGenericConstraint(string paramName)
    {
        foreach (GenericConstraintDeclaration c in ActiveConstraintsFor(paramName: paramName))
        {
            if (c is not { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null })
            {
                continue;
            }

            foreach (TypeExpression protocolExpr in c.ConstraintTypes)
            {
                if (BareTypeName(typeName: protocolExpr.Name) != IterableProtocolName)
                {
                    continue;
                }

                TypeSymbol resolved = _typeResolver.ResolveType(typeExpr: protocolExpr);
                if (resolved.TypeArguments is { Count: > 0 })
                {
                    return resolved.TypeArguments[index: 0];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="iterableType"/> directly implements the
    /// <c>Iterable</c> protocol or, for a generic resolution, exposes an <c>iter</c>
    /// member routine (structural Iterable check).
    /// </summary>
    private bool IsOrObeysIterable(TypeSymbol iterableType)
    {
        if (ImplementsProtocol(type: iterableType, protocolName: IterableProtocolName))
        {
            return true;
        }

        if (iterableType.IsGenericResolution)
        {
            return _registry.LookupMemberRoutine(type: iterableType, memberRoutineName: "emitter") !=
                   null;
        }

        return false;
    }

    /// <summary>
    /// Strategy 1 of <see cref="GetIterableElementType"/>: extracts the element type from an
    /// <c>Iterable[X]</c> entry in the type's implemented protocols, substituting generic parameters
    /// for a generic resolution. Returns null when no <c>Iterable</c> protocol entry is found.
    /// </summary>
    private static TypeSymbol? TryGetElementFromIterableProtocols(TypeSymbol iterableType)
    {
        List<TypeSymbol>? protocols = iterableType switch
        {
            RecordTypeSymbol record => record.ImplementedProtocols,
            EntityTypeSymbol entity => entity.ImplementedProtocols,
            _ => null
        };

        if (protocols == null)
        {
            return null;
        }

        foreach (TypeSymbol proto in protocols)
        {
            if (proto.BareName != IterableProtocolName ||
                proto.TypeArguments is not { Count: > 0 })
            {
                continue;
            }

            TypeSymbol elementType = proto.TypeArguments[index: 0];
            return SubstituteIterableElementTypeParams(iterableType: iterableType,
                elementType: elementType);
        }

        return null;
    }

    /// <summary>
    /// Substitutes the generic parameters of <paramref name="iterableType"/> into
    /// <paramref name="elementType"/> when the iterable is a generic resolution.
    /// Returns <paramref name="elementType"/> unchanged when no substitution applies.
    /// </summary>
    private static TypeSymbol SubstituteIterableElementTypeParams(TypeSymbol iterableType,
        TypeSymbol elementType)
    {
        if (iterableType is not { IsGenericResolution: true, TypeArguments: not null })
        {
            return elementType;
        }

        TypeSymbol? genericDef = iterableType switch
        {
            RecordTypeSymbol r => r.GenericDefinition,
            EntityTypeSymbol e => e.GenericDefinition,
            _ => null
        };

        if (genericDef?.GenericParameters == null)
        {
            return elementType;
        }

        // An instance's protocols are already its own (`List[Work[T]]` obeys `Iterable[Work[T]]`); only the
        // definition's (`Iterable[T]`) still name the definition's parameters. Substituting an instance's again
        // would read the `T` inside `Work[T]` as the list's slot and give `Work[Work[T]]`.
        List<TypeSymbol>? definitionProtocols = genericDef switch
        {
            RecordTypeSymbol r => r.ImplementedProtocols,
            EntityTypeSymbol e => e.ImplementedProtocols,
            _ => null
        };
        if (definitionProtocols == null ||
            !definitionProtocols.Any(predicate: p => ReferenceEquals(objA: p.TypeArguments?.FirstOrDefault(),
                objB: elementType)))
        {
            return elementType;
        }

        var substitution = new Dictionary<string, TypeSymbol>();
        for (int i = 0;
             i < genericDef.GenericParameters.Count && i < iterableType.TypeArguments.Count;
             i++)
        {
            substitution[key: genericDef.GenericParameters[index: i]] =
                iterableType.TypeArguments[index: i];
        }

        return SubstituteTypeParams(type: elementType, substitution: substitution);
    }

    /// <summary>
    /// Strategy 2 of <see cref="GetIterableElementType"/>: derives the element type from the return
    /// type of the type's <c>iter</c> memberRoutine (<c>Iterator[T]</c>), resolving a generic
    /// parameter against the iterable's bound type args. Returns null when no usable <c>iter</c>
    /// return type is found.
    /// </summary>
    private TypeSymbol? TryGetElementFromIterReturnType(TypeSymbol iterableType)
    {
        RoutineInfo? seqMemberRoutine2 =
            _registry.LookupRoutine(fullName: $"{iterableType.Name}.emitter");

        // Generic fallback: Range[S64].emitter -> Range.emitter via LookupMemberRoutine
        if (seqMemberRoutine2 == null)
        {
            seqMemberRoutine2 =
                _registry.LookupMemberRoutine(type: iterableType, memberRoutineName: "emitter");
        }

        if (seqMemberRoutine2?.ReturnType?.TypeArguments is not { Count: > 0 })
        {
            return null;
        }

        // Resolve generic type args: if return type arg is T and iterableType is Range[S64], resolve T -> S64
        TypeSymbol returnTypeArg = seqMemberRoutine2.ReturnType.TypeArguments[index: 0];
        if (returnTypeArg is GenericParameterTypeSymbol && iterableType is
                { IsGenericResolution: true, TypeArguments: not null })
        {
            TypeSymbol? genericDef = iterableType switch
            {
                RecordTypeSymbol r => r.GenericDefinition,
                EntityTypeSymbol e => e.GenericDefinition,
                _ => null
            };
            if (genericDef?.GenericParameters != null)
            {
                int paramIndex = genericDef.GenericParameters
                                           .ToList()
                                           .IndexOf(item: returnTypeArg.Name);
                if (paramIndex >= 0 && paramIndex < iterableType.TypeArguments.Count)
                {
                    return iterableType.TypeArguments[index: paramIndex];
                }
            }
        }

        return returnTypeArg;
    }

    #endregion

    /// <summary>
    /// True if `type` references any name listed in `genericParameters` via a
    /// `GenericParameterTypeSymbol` — i.e. an unresolved memberRoutine-level generic param.
    /// Used to suppress premature argument-type errors before generic inference runs.
    /// </summary>
    private static bool ContainsUnresolvedMemberRoutineGeneric(TypeSymbol type,
        List<string>? genericParameters)
    {
        if (genericParameters is null || genericParameters.Count == 0)
        {
            return false;
        }

        if (type is GenericParameterTypeSymbol gp && genericParameters.Contains(item: gp.Name))
        {
            return true;
        }

        if (type.TypeArguments is { Count: > 0 } args && args.Any(predicate: arg =>
                ContainsUnresolvedMemberRoutineGeneric(type: arg,
                    genericParameters: genericParameters)))
        {
            return true;
        }

        if (type is RoutineTypeSymbol routine)
        {
            if (routine.ParameterTypes.Any(predicate: pt =>
                    ContainsUnresolvedMemberRoutineGeneric(type: pt,
                        genericParameters: genericParameters)))
            {
                return true;
            }

            if (routine.ReturnType is { } ret &&
                ContainsUnresolvedMemberRoutineGeneric(type: ret,
                    genericParameters: genericParameters))
            {
                return true;
            }
        }

        return false;
    }
}
