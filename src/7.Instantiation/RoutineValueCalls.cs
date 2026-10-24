using Builder.Declaration;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Instantiation;

/// <summary>
/// Calls through a routine VALUE (a lambda or routine held in a local, a parameter or a routine-typed field) and
/// the iterator steps that may reach one, as recovery sees them. The routine a value holds has a recovering entry
/// (<see cref="IdentifierExpression.RecoverRoutine"/>), which the module's recovery table maps the routine's address to,
/// so the value itself stays C's two-word (callback, userdata) pair: the lookup-shaped recovery variant of the
/// routine, which returns <c>Lookup[T]</c> (<c>Check[None]</c> for a routine that returns nothing). A call of the
/// value made beneath <c>try</c>/<c>check</c> goes through that entry
/// (<see cref="CallExpression.RecoversThroughValue"/>), and its carrier is propagated like the carrier of any other
/// failable call, so a failure inside the lambda is recovered by the keyword above it. A call of the value made
/// with no keyword above it calls the routine itself and still crashes on failure. Only a value whose type is
/// written <c>failable Callable[...]</c> may fail: any other routine value is called as it is, a failure inside it
/// crashing there.
/// </summary>
internal static class RoutineValueCalls
{
    /// <summary>The routine type <paramref name="call"/> goes through, when it calls a routine value that may fail
    /// (one of a <c>failable Callable[...]</c> type).</summary>
    public static RoutineTypeSymbol? ValueType(CallExpression call)
    {
        if (call.ResolvedRoutine != null || call.RecoversThroughValue)
        {
            return null;
        }

        RoutineTypeSymbol? type = call.Callee switch
        {
            IdentifierExpression { ResolvedRoutine: null, ResolvedType: RoutineTypeSymbol local } => local,
            MemberExpression member when call.LoweringKind == CallLoweringKind.DynamicCall =>
                member.ResolvedType as RoutineTypeSymbol ?? FieldType(member: member),
            _ => null
        };
        return type is { IsFailable: true } ? type : null;
    }

    /// <summary>The declared type of the routine-typed field a member expression names.</summary>
    private static RoutineTypeSymbol? FieldType(MemberExpression member)
    {
        List<MemberVariableInfo>? fields = member.Object.ResolvedType switch
        {
            EntityTypeSymbol entity => entity.MemberVariables,
            RecordTypeSymbol record => record.MemberVariables,
            _ => null
        };
        return fields?.FirstOrDefault(predicate: f => f.Name == member.MemberName)?.Type as RoutineTypeSymbol;
    }

    /// <summary>
    /// The carrier a recovering call through a value of <paramref name="routineType"/> yields: what the routine's
    /// check recovery variant returns (<see cref="ErrorHandlingGenerator"/>), <c>Check[T]</c> (<c>Check[None]</c>
    /// for a routine that returns nothing).
    /// </summary>
    public static TypeSymbol? Carrier(RoutineTypeSymbol routineType, TypeRegistry registry)
    {
        TypeSymbol? returned = routineType.ReturnType;
        bool none = returned == null || returned.IsNone;
        TypeSymbol? definition = registry.LookupType(name: "Check");
        TypeSymbol? payload = none ? registry.LookupType(name: "None") : returned;
        return definition == null || payload == null
            ? null
            : registry.GetOrCreateResolution(genericDef: definition, typeArguments: [payload]);
    }

    /// <summary>The call of the value's recovering entry that stands for <paramref name="call"/>, typed as its
    /// carrier, or null when the carrier types are not registered.</summary>
    public static CallExpression? Recovering(CallExpression call, RoutineTypeSymbol routineType, TypeRegistry registry)
    {
        return Carrier(routineType: routineType, registry: registry) is { } carrier
            ? call with { RecoversThroughValue = true, ResolvedType = carrier, IsPreAnalyzed = true }
            : null;
    }

    /// <summary>Whether a recovering call through a value of <paramref name="routineType"/> can come back
    /// absent (a routine that returns nothing has the check-shaped carrier, which has no absent state).</summary>
    public static bool CanBeAbsent(RoutineTypeSymbol routineType)
    {
        return routineType.ReturnType is { IsNone: false };
    }

    /// <summary>
    /// Whether a value of <paramref name="type"/> may hold a routine value that may fail (of a
    /// <c>failable Callable[...]</c> type), at any depth of its fields and type arguments: an iterator adapter
    /// (<c>SelectEmittable</c>, <c>WhereEmittable</c>) holds the lambda it calls. A type still written with a
    /// parameter may hold one.
    /// </summary>
    public static bool MayHoldRoutineValue(TypeSymbol? type)
    {
        return MayHoldRoutineValue(type: type, seen: new HashSet<TypeSymbol>(comparer: ReferenceEqualityComparer.Instance));
    }

    private static bool MayHoldRoutineValue(TypeSymbol? type, HashSet<TypeSymbol> seen)
    {
        if (type == null || !seen.Add(item: type))
        {
            return false;
        }

        switch (type)
        {
            case RoutineTypeSymbol routine:
                return routine.IsFailable;
            case GenericParameterTypeSymbol or ProtocolTypeSymbol or ProtocolSelfTypeSymbol
                or AssociatedProjectionTypeSymbol:
                return true;
        }

        if (type.IsGenericDefinition)
        {
            return true;
        }

        if (type.TypeArguments?.Any(predicate: a => MayHoldRoutineValue(type: a, seen: seen)) == true)
        {
            return true;
        }

        List<MemberVariableInfo>? fields = type switch
        {
            EntityTypeSymbol entity => entity.MemberVariables,
            RecordTypeSymbol record => record.MemberVariables,
            _ => null
        };
        return fields?.Any(predicate: f => MayHoldRoutineValue(type: f.Type, seen: seen)) == true;
    }

    /// <summary>
    /// The step of an <c>each</c> loop (<c>try iterator.emit()</c>, built by the loop's lowering) as the
    /// lookup-shaped step a recovery variant takes when the iterator may call a routine value: its end stays
    /// the absent state and a failure beneath it (inside the lambda it calls) comes back as the error state, so
    /// it can be propagated instead of ending the loop. Null when <paramref name="subject"/> is no such step, or
    /// the iterator holds no routine value.
    /// </summary>
    public static Expression? CheckIterationStep(Expression subject, TypeRegistry registry)
    {
        switch (subject)
        {
            // A step written in an analyzed body: the analysis bound it to the iterator's own try variant.
            case RecoveryExpression { Kind: RecoveryKind.Try, LoweredCall: CallExpression lowered }:
                return CheckIterationStep(subject: lowered, registry: registry);
            case CallExpression
            {
                IsSynthesizedLowering: true, Callee: MemberExpression { Object: var receiver },
                ResolvedRoutine: { Recovery: RecoveryKind.Try, RecoveryOf: { Name: "emit" } emit }
            } step when MayHoldRoutineValue(type: receiver.ResolvedType ?? emit.OwnerType):
                return registry.LookupRecoveryVariant(recovered: emit, kind: RecoveryKind.Check) is
                    { ReturnType: { } carrier } check && carrier.TypeArguments is { Count: > 0 }
                    ? step with { ResolvedRoutine = check, ResolvedType = carrier }
                    : null;
            case RecoveryExpression
            {
                Kind: RecoveryKind.Try, LoweredCall: null,
                Inner: CallExpression { IsSynthesizedLowering: true, Callee: MemberExpression { MemberName: "emit" } },
                ResolvedType.TypeArguments: [{ } element]
            } generic when registry.LookupType(name: "Check") is { } checkDefinition:
                return generic with
                {
                    Kind = RecoveryKind.Check,
                    ResolvedType = registry.GetOrCreateResolution(genericDef: checkDefinition,
                        typeArguments: [element])
                };
            default:
                return null;
        }
    }

    /// <summary>
    /// The arm a lookup-shaped iteration step gets in a recovery variant of <paramref name="kind"/>: a failure
    /// beneath the step (its error state) returns as the variant's failure, the error itself for check,
    /// an absent result for try.
    /// </summary>
    public static WhenClause StepFailureClause(ErrorHandlingVariantKind kind, TypeRegistry registry,
        SourceLocation loc)
    {
        bool keepsError = kind is ErrorHandlingVariantKind.Check;
        const string errName = "__rf_step_err";
        return new WhenClause(
            Pattern: new CrashablePattern(ErrorType: null,
                VariableName: keepsError ? errName : null,
                Location: loc),
            Body: keepsError
                ? new VariantReturnStatement(VariantKind: kind,
                    SiteKind: VariantSiteKind.FromThrow,
                    Value: new IdentifierExpression(Name: errName, Location: loc)
                    {
                        ResolvedType = registry.LookupType(name: RuntimeContract.Crashables)
                    },
                    Location: loc)
                : new VariantReturnStatement(VariantKind: kind,
                    SiteKind: VariantSiteKind.FromAbsent,
                    Value: null,
                    Location: loc),
            Location: loc);
    }

    /// <summary>The clauses of an iteration step's <c>when</c> with the failure arm of
    /// <see cref="StepFailureClause"/> put before its value arm (the <c>else</c>).</summary>
    public static List<WhenClause> WithStepFailureClause(List<WhenClause> clauses, ErrorHandlingVariantKind kind,
        TypeRegistry registry, SourceLocation loc)
    {
        var result = new List<WhenClause>(collection: clauses);
        int elseAt = result.FindIndex(match: c => c.Pattern is ElsePattern);
        result.Insert(index: elseAt < 0 ? result.Count : elseAt,
            item: StepFailureClause(kind: kind, registry: registry, loc: loc));
        return result;
    }

    /// <summary>Whether <paramref name="expr"/> is an <c>each</c> loop's step over an iterator that may call a
    /// routine value (see <see cref="CheckIterationStep"/>).</summary>
    public static bool IsRecoverableIterationStep(Expression expr)
    {
        return expr switch
        {
            RecoveryExpression { Kind: RecoveryKind.Try, LoweredCall: CallExpression lowered } =>
                IsRecoverableIterationStep(expr: lowered),
            CallExpression
            {
                IsSynthesizedLowering: true, Callee: MemberExpression { Object: var receiver },
                ResolvedRoutine: { Recovery: RecoveryKind.Try, RecoveryOf: { Name: "emit" } emit }
            } => MayHoldRoutineValue(type: receiver.ResolvedType ?? emit.OwnerType),
            RecoveryExpression
            {
                Kind: RecoveryKind.Try, LoweredCall: null,
                Inner: CallExpression { IsSynthesizedLowering: true, Callee: MemberExpression { MemberName: "emit" } }
            } => true,
            _ => false
        };
    }
}
