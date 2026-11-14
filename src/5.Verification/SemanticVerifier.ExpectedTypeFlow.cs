using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// The type a value is expected to have flows inward from where the value goes, never from later uses of a variable.
/// It reaches a call whose type arguments come only from its result (<c>x.to()</c>, <c>parse(text)</c>) through
/// the operands of an operator whose routine takes and gives <c>Me</c>, through the other side of a comparison, and
/// down a chain of member calls that give <c>Me</c> (<c>i.to().sqrt().sin()</c>). A binding, an assignment, a
/// compound assignment, an argument and a <c>return</c> give the first expected type.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>A call made on a receiver's result that must give the receiver's own type (or that type's
    /// Optional, when <paramref name="AllowsMaybe"/>) for the expected type to pass through it.</summary>
    private readonly record struct ChainRequirement(string Name, int ArgumentCount, bool AllowsMaybe);

    /// <summary>A type an enclosing operator, comparison or call hands a call, with what that type must have for
    /// it to hold (nothing, for an operand or a comparison side).</summary>
    private readonly record struct DerivedExpectation(TypeSymbol Type, List<ChainRequirement> Requirements);

    /// <summary>The calls handed a derived expectation that have not read it yet. Keyed by node identity.</summary>
    private readonly Dictionary<Expression, DerivedExpectation> _pendingDerivedExpectations =
        new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>The derived expectation a call holds while it is analyzed. It binds only a type argument that
    /// nothing but the result names (a <c>to()</c> family, <c>ask[T]() -> T</c>), never a gap the receiver or the
    /// arguments leave, as a written type would.</summary>
    private readonly Dictionary<Expression, TypeSymbol> _resultOnlyExpectations =
        new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>The calls whose expected type came through a chain and was dropped, with the call on their result
    /// that did not give the same type back. Read by the RV-S645 message.</summary>
    private readonly Dictionary<Expression, string> _chainExpectationDropped =
        new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Analyzes <paramref name="expression"/> where an enclosing operator, comparison or call expects
    /// <paramref name="expected"/> of it. A call takes it as a derived expectation (see
    /// <see cref="_resultOnlyExpectations"/>). An operator or a negation built on a call takes it as its expected
    /// type and hands it on to its operands.
    /// </summary>
    private TypeSymbol AnalyzeWithDerivedExpectation(Expression expression, TypeSymbol? expected,
        List<ChainRequirement>? requirements = null)
    {
        if (expected == null || expression is not CallExpression { IsPreAnalyzed: false } call)
        {
            return AnalyzeExpression(expression: expression, expectedType: expected);
        }

        _pendingDerivedExpectations[key: call] = new DerivedExpectation(Type: expected, Requirements: requirements ?? []);
        try
        {
            return AnalyzeExpression(expression: call);
        }
        finally
        {
            _pendingDerivedExpectations.Remove(key: call);
        }
    }

    /// <summary>
    /// Takes the derived expectation handed to <paramref name="call"/> and holds it while the call is analyzed:
    /// the type, unless it came from the call made on its result and that call doesn't give the same type back on
    /// it. Gives what to pass to <see cref="ReleaseDerivedExpectation"/>.
    /// </summary>
    private bool HoldDerivedExpectation(CallExpression call)
    {
        if (!_pendingDerivedExpectations.Remove(key: call, value: out DerivedExpectation derived))
        {
            return false;
        }

        foreach (ChainRequirement requirement in derived.Requirements)
        {
            if (!GivesOwnTypeBack(type: derived.Type, requirement: requirement))
            {
                _chainExpectationDropped[key: call] = requirement.Name;
                return false;
            }
        }

        _resultOnlyExpectations[key: call] = derived.Type;
        return true;
    }

    /// <summary>Ends what <see cref="HoldDerivedExpectation"/> started.</summary>
    private void ReleaseDerivedExpectation(CallExpression call, bool held)
    {
        if (held)
        {
            _resultOnlyExpectations.Remove(key: call);
        }
    }

    /// <summary>The type a call's result-only type argument comes from: the written expected type, else a derived
    /// one the call holds.</summary>
    private TypeSymbol? ResultOnlyExpectation(Expression call, TypeSymbol? expectedType)
    {
        return expectedType ?? _resultOnlyExpectations.GetValueOrDefault(key: call);
    }

    /// <summary>
    /// Analyzes the receiver of <paramref name="member"/>. When it is a call itself, the type this call goes to is
    /// handed to it, holding only if the routine called on it gives that type back (`i.to().sqrt()` where a B128
    /// goes: `sqrt` on a B128 gives a B128).
    /// </summary>
    private TypeSymbol AnalyzeChainReceiver(CallExpression call, MemberExpression member, TypeSymbol? expectedType)
    {
        TypeSymbol? expected = ResultOnlyExpectation(call: call, expectedType: expectedType);
        if (!IsUsableExpectation(type: expected) || member.Object is not CallExpression)
        {
            return AnalyzeExpression(expression: member.Object);
        }

        // `x.to().f()` where a `T?` goes: `f` may give `Me?`, so the receiver is expected to be the `T`.
        bool throughMaybe = IsMaybeType(type: expected!) && expected!.TypeArguments is [_];
        TypeSymbol hint = throughMaybe ? expected!.TypeArguments![index: 0] : expected!;
        return AnalyzeWithDerivedExpectation(expression: member.Object, expected: hint,
            requirements: [new ChainRequirement(Name: member.MemberName, ArgumentCount: call.Arguments.Count,
                AllowsMaybe: throughMaybe)]);
    }

    /// <summary>Whether every routine of <paramref name="type"/> named by <paramref name="requirement"/> that takes
    /// its argument count gives <paramref name="type"/> back (or its Optional, when allowed), and one does.</summary>
    private bool GivesOwnTypeBack(TypeSymbol type, ChainRequirement requirement)
    {
        List<RoutineInfo> candidates = MemberRoutinesTaking(type: type, name: requirement.Name,
            argumentCount: requirement.ArgumentCount);
        return candidates.Count > 0 && candidates.All(predicate: routine =>
            routine.ReturnType is { } given &&
            (IsOwnType(given: given, type: type, routine: routine) ||
             requirement.AllowsMaybe && IsMaybeType(type: given) && given.TypeArguments is [var payload] &&
             IsOwnType(given: payload, type: type, routine: routine)));
    }

    /// <summary>Whether <paramref name="type"/>'s operator routine <paramref name="operatorName"/> takes
    /// <paramref name="type"/>. When <paramref name="givesMe"/>, the routines that give <paramref name="type"/> back
    /// are the ones asked (`B64.add(you: C128) -> C128` gives something else), and at least one must exist.
    /// Otherwise every one-argument routine of that name has to take it.</summary>
    private bool OperatorTakesOwnType(TypeSymbol type, string operatorName, bool givesMe)
    {
        List<RoutineInfo> candidates = MemberRoutinesTaking(type: type, name: operatorName, argumentCount: 1);
        if (givesMe)
        {
            candidates = candidates.Where(predicate: routine =>
                                        routine.ReturnType is { } given &&
                                        IsOwnType(given: given, type: type, routine: routine))
                                   .ToList();
        }

        return candidates.Count > 0 && candidates.All(predicate: routine =>
            routine.Parameters.Count >= 1 &&
            IsOwnType(given: routine.Parameters[index: 0].Type, type: type, routine: routine));
    }

    /// <summary>The member routines of <paramref name="type"/> named <paramref name="name"/> that take
    /// <paramref name="argumentCount"/> arguments.</summary>
    private List<RoutineInfo> MemberRoutinesTaking(TypeSymbol type, string name, int argumentCount)
    {
        var candidates = new List<RoutineInfo>();
        _registry.CollectMemberRoutineCandidates(type: type, memberRoutineName: name, candidates: candidates);
        if (candidates.Count == 0 && _registry.LookupMemberRoutine(type: type, memberRoutineName: name) is { } single)
        {
            candidates.Add(item: single);
        }

        // A protocol's own declaration of the routine stands for the type's, which is the one asked.
        return candidates.Where(predicate: routine => routine.OwnerType is not ProtocolTypeSymbol &&
                                                      RoutineCanAcceptArgCount(routine: routine, argCount: argumentCount))
                         .ToList();
    }

    /// <summary>Whether <paramref name="given"/>, written in <paramref name="routine"/>, is <paramref name="type"/>
    /// itself (spelled out or as the routine's owner, which is how `Me` reads).</summary>
    private static bool IsOwnType(TypeSymbol given, TypeSymbol type, RoutineInfo routine)
    {
        return given.FullName == type.FullName ||
               routine.OwnerType is { } owner && given.FullName == owner.FullName &&
               (owner.FullName == type.FullName || owner.IsGenericDefinition && owner.BareName == type.BareName);
    }

    /// <summary>Whether <paramref name="type"/> can be handed on as an expected type.</summary>
    private static bool IsUsableExpectation(TypeSymbol? type)
    {
        return type is not (null or ErrorTypeSymbol or GenericParameterTypeSymbol or FlagsTypeSymbol);
    }

    /// <summary>Whether <paramref name="expression"/> may need the type its value goes to (a call, or arithmetic
    /// or a negation built on one). A literal is left to the literal rules, a name has its own type.</summary>
    private static bool TakesTypeFromWhereItGoes(Expression expression)
    {
        return expression switch
        {
            CallExpression => true,
            UnaryExpression { Operator: UnaryOperator.Minus or UnaryOperator.BitwiseNot } unary =>
                TakesTypeFromWhereItGoes(expression: unary.Operand),
            BinaryExpression { Operator: not BinaryOperator.Assign } binary =>
                TakesTypeFromWhereItGoes(expression: binary.Left) || TakesTypeFromWhereItGoes(expression: binary.Right),
            _ => false
        };
    }

    /// <summary>The operator routine whose parameter decides what both sides of a comparison expect.</summary>
    private static string? ComparisonRoutine(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Equal or BinaryOperator.NotEqual => "eq",
            BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater or BinaryOperator.GreaterEqual
                or BinaryOperator.ThreeWayComparator => "compare",
            _ => null
        };
    }

    /// <summary>The operands of an operator or a comparison whose routine fits more than one way, with the routines
    /// that fit. Read by the RV-S645 message.</summary>
    private readonly Dictionary<Expression, string> _expectationNotes = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>The one-argument operator routines of <paramref name="type"/> named <paramref name="operatorName"/>,
    /// only those that give <paramref name="gives"/> back when it is given.</summary>
    private List<RoutineInfo> OperatorRoutines(TypeSymbol type, string operatorName, TypeSymbol? gives)
    {
        return MemberRoutinesTaking(type: type, name: operatorName, argumentCount: 1)
              .Where(predicate: routine => routine.Parameters.Count >= 1 &&
                                           (gives == null || routine.ReturnType is { } given &&
                                               IsOwnType(given: given, type: gives, routine: routine)))
              .ToList();
    }

    /// <summary>The one type the parameter of <paramref name="routines"/> takes (`Me` read as
    /// <paramref name="owner"/>), or null when they take none or several.</summary>
    private static TypeSymbol? OneParameterType(List<RoutineInfo> routines, TypeSymbol owner)
    {
        List<TypeSymbol> taken = routines.Select(selector: routine =>
                                             IsOwnType(given: routine.Parameters[index: 0].Type, type: owner, routine: routine)
                                                 ? owner
                                                 : routine.Parameters[index: 0].Type)
                                         .DistinctBy(keySelector: t => t.FullName)
                                         .ToList();
        return taken is [var only] && IsUsableExpectation(type: only) ? only : null;
    }

    /// <summary>Notes on each operand of <paramref name="binary"/> that may need a type that the operator routine
    /// <paramref name="routines"/> fits more than one way, so it gives no type.</summary>
    private void NoteSeveralOperatorRoutines(BinaryExpression binary, TypeSymbol owner, List<RoutineInfo> routines)
    {
        if (routines.Count < 2)
        {
            return;
        }

        string listed = string.Join(separator: ", ", values: routines.Select(selector: routine =>
            $"{routine.Name}({routine.Parameters[index: 0].Name}: {routine.Parameters[index: 0].Type.Name}) -> " +
            $"{routine.ReturnType?.Name ?? "None"}"));
        string note = $" '{owner.Name}.{routines[index: 0].Name}' fits more than one way here ({listed}), so it doesn't " +
                      "say one type.";
        foreach (Expression operand in new[] { binary.Left, binary.Right })
        {
            if (operand is CallExpression && TakesTypeFromWhereItGoes(expression: operand))
            {
                _expectationNotes[key: operand] = note;
            }
        }
    }

    /// <summary>What the operands of <paramref name="binary"/> expect from the type its result goes to. The left
    /// operand is that type when the operator routines of that type that give it back take one parameter type, and
    /// the right operand is that parameter type (`B128.mul(you: B128) -> B128` gives both B128).</summary>
    private (TypeSymbol? Left, TypeSymbol? Right) OperandExpectationFromResult(BinaryExpression binary,
        TypeSymbol? expectedType)
    {
        if (!IsUsableExpectation(type: expectedType) || binary.Operator == BinaryOperator.Assign ||
            ComparisonRoutine(op: binary.Operator) != null ||
            binary.Operator.GetMemberRoutineName() is not { } operatorName ||
            !TakesTypeFromWhereItGoes(expression: binary.Left) && !TakesTypeFromWhereItGoes(expression: binary.Right))
        {
            return (null, null);
        }

        List<RoutineInfo> giving = OperatorRoutines(type: expectedType!, operatorName: operatorName, gives: expectedType);
        if (OneParameterType(routines: giving, owner: expectedType!) is { } parameter)
        {
            return (expectedType, parameter);
        }

        NoteSeveralOperatorRoutines(binary: binary, owner: expectedType!, routines: giving);
        return (null, null);
    }

    /// <summary>What the right side of <paramref name="binary"/> expects from the left side's type: the one type the
    /// left side's operator routine takes (an argument expects its parameter's type).</summary>
    private TypeSymbol? RightExpectationFromLeft(BinaryExpression binary, TypeSymbol leftType)
    {
        if (binary.Operator == BinaryOperator.Assign || !IsUsableExpectation(type: leftType) ||
            !TakesTypeFromWhereItGoes(expression: binary.Right) ||
            (ComparisonRoutine(op: binary.Operator) ?? binary.Operator.GetMemberRoutineName()) is not { } routineName)
        {
            return null;
        }

        List<RoutineInfo> routines = OperatorRoutines(type: leftType, operatorName: routineName, gives: null);
        if (OneParameterType(routines: routines, owner: leftType) is { } parameter)
        {
            return parameter;
        }

        NoteSeveralOperatorRoutines(binary: binary, owner: leftType, routines: routines);
        return null;
    }

    /// <summary>What the left side of <paramref name="binary"/> expects from the right side's type when nothing is
    /// expected of the whole: the right side's type, when its operator routine takes that type (`x.to() * y` with a
    /// B128 `y`: `B128.mul(you: B128)`).</summary>
    private TypeSymbol? LeftExpectationFromRight(BinaryExpression binary, TypeSymbol rightType)
    {
        if (!IsUsableExpectation(type: rightType) ||
            (ComparisonRoutine(op: binary.Operator) ?? binary.Operator.GetMemberRoutineName()) is not { } routineName)
        {
            return null;
        }

        return OperatorRoutines(type: rightType, operatorName: routineName, gives: null)
              .Any(predicate: routine => IsOwnType(given: routine.Parameters[index: 0].Type, type: rightType,
                   routine: routine))
            ? rightType
            : null;
    }

    /// <summary>The text of RV-S645 (RV-S161 for a free routine) for <paramref name="call"/>, which calls
    /// <paramref name="name"/>, naming the places the type argument was looked for.</summary>
    private string ResultTypeArgumentUnknownMessage(Expression call, string name, string? typeParameter,
        bool isMember = true)
    {
        string what = typeParameter == null ? "its type argument" : $"its type argument '{typeParameter}'";
        string dropped = _chainExpectationDropped.TryGetValue(key: call, value: out string? next)
            ? $" '.{next}()' on its result doesn't give back the type it is called on, so the type that call goes " +
              "to doesn't reach this one."
            : "";
        dropped += _expectationNotes.GetValueOrDefault(key: call) ?? "";
        return $"You called '{name}()', which takes {what} from where the result goes, and nothing here gives " +
               "one. The builder looks at a written variable type, an assignment target, a parameter, a return " +
               "type, the result of an operator the call is an operand of, the other operand of an operator or a " +
               $"comparison, and a call on the result that gives back its own type.{dropped} Write the type in the call " +
               $"('{(isMember ? "." : "")}{name}[S64]()'), or give the variable a type.";
    }
}
