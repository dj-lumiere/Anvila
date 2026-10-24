using Builder.Declaration;
using Builder.Verification;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Instantiation;

/// <summary>
/// The failable member calls that operator sugar stands for, built ahead of the operator lowering so a
/// recovery keyword sees every failure under it. A checked operator (<c>a // b</c>, <c>a + b</c>, <c>-a</c>)
/// and a subscript (<c>xs[i]</c>) are failable member calls (<c>a.floordiv(you: b)</c>,
/// <c>xs.getitem(index: i)</c>), but they only become calls in Phase 6 (OperatorLoweringPass). A recovery
/// composition (<c>try a // b</c>) and a recovery variant body (the body behind <c>try f()</c>) are built
/// before that, so they resolve the same call here, from the operands' analyzed types, and treat it as an
/// ordinary failable call. An operator or subscript whose member routine cannot fail is left as it is.
/// </summary>
internal static class FailurePointCalls
{
    /// <summary>
    /// <c>left.op(you: right)</c> for a CHECKED arithmetic operator whose member routine on the left
    /// operand's type is failable, or null. Wrapping, clamping and non-arithmetic operators never fail.
    /// </summary>
    public static CallExpression? ForBinary(BinaryExpression bin, TypeRegistry registry)
    {
        if (bin.Operator is not (BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply
                or BinaryOperator.TrueDivide or BinaryOperator.FloorDivide or BinaryOperator.Modulo
                or BinaryOperator.Power) ||
            bin.Operator.GetMemberRoutineName() is not { } opMethod ||
            bin.Left.ResolvedType is not { } leftType || bin.Right.ResolvedType is not { } rightType)
        {
            return null;
        }

        RoutineInfo? opRoutine =
            registry.LookupMemberRoutineOverload(type: leftType,
                memberRoutineName: opMethod,
                argTypes: [rightType]) ??
            registry.LookupMemberRoutine(type: leftType, memberRoutineName: opMethod, isFailable: true);
        if (!CanFail(routine: opRoutine))
        {
            return null;
        }

        string paramName = opRoutine!.Parameters.Count > 0
            ? opRoutine.Parameters[index: 0].Name
            : "you";
        return MemberCall(receiver: bin.Left,
            memberName: opMethod,
            arguments:
            [
                new NamedArgumentExpression(Name: paramName, Value: bin.Right, Location: bin.Location)
                {
                    ResolvedType = rightType
                }
            ],
            routine: opRoutine,
            resultType: bin.ResolvedType,
            location: bin.Location);
    }

    /// <summary><c>operand.neg()</c> for a unary minus whose member routine is failable, or null.</summary>
    public static CallExpression? ForUnary(UnaryExpression unary, TypeRegistry registry)
    {
        if (unary.Operator != UnaryOperator.Minus ||
            unary.Operator.GetMemberRoutineName() is not { } opMethod ||
            unary.Operand.ResolvedType is not { } operandType)
        {
            return null;
        }

        RoutineInfo? opRoutine =
            registry.LookupMemberRoutineOverload(type: operandType, memberRoutineName: opMethod, argTypes: []) ??
            registry.LookupMemberRoutine(type: operandType, memberRoutineName: opMethod);
        return CanFail(routine: opRoutine)
            ? MemberCall(receiver: unary.Operand,
                memberName: opMethod,
                arguments: [],
                routine: opRoutine!,
                resultType: unary.ResolvedType,
                location: unary.Location)
            : null;
    }

    /// <summary>
    /// <c>xs.getitem(index: i)</c> for a plain element read whose analyzed <c>getitem</c> is failable, or
    /// null. A slice (range index), an end-relative index (<c>^n</c>), an entity element (read through a
    /// token, not <c>getitem</c>) and a type receiver (<c>T[U]</c>) keep their own lowering.
    /// </summary>
    public static CallExpression? ForIndex(IndexExpression idx, TypeRegistry registry)
    {
        if (idx.ResolvedGetItem is not { } getItem || !CanFail(routine: getItem) || idx.ReadsElementToken ||
            idx.ResolvedType is null or EntityTypeSymbol || idx.Object.ResolvedType is null ||
            idx.Index is RangeExpression or BackIndexExpression ||
            idx.Index.ResolvedType is not { } indexType ||
            indexType is RecordTypeSymbol { GenericDefinition.Name: "Range" } ||
            getItem.Parameters.Count != 1)
        {
            return null;
        }

        // A key the routine takes through a token (`Dict.getitem(key: Viewing[K])`) goes as the address of its place.
        TypeSymbol slot = getItem.Parameters[index: 0].Type;
        bool byToken = Declaration.ImplicitToken.IsTokenParameter(parameter: slot, tokenKind: out _, target: out _) &&
                       indexType.BareName != slot.BareName && indexType is not EntityTypeSymbol;
        Expression key = byToken
            ? new AddressOfExpression(Target: idx.Index, Location: idx.Location) { ResolvedType = slot }
            : CountedIndex(index: idx.Index, getItem: getItem, registry: registry) ?? idx.Index;
        return MemberCall(receiver: idx.Object,
            memberName: getItem.Name,
            arguments:
            [
                new NamedArgumentExpression(Name: getItem.Parameters[index: 0].Name,
                    Value: key,
                    Location: idx.Location) { ResolvedType = key.ResolvedType ?? indexType }
            ],
            routine: getItem,
            resultType: idx.ResolvedType,
            location: idx.Location);
    }

    /// <summary>
    /// An index of another integer type than the <c>index</c> parameter takes, as <c>i.to_index()</c> (a <c>U64</c>,
    /// failing with <c>NegativeIndexError</c> below 0) or <c>i.to_integer_count()</c> (an <c>Integer</c>), the same
    /// conversion OperatorLoweringPass gives a subscript outside a recovery. Null when no conversion is needed.
    /// </summary>
    private static CallExpression? CountedIndex(Expression index, RoutineInfo getItem, TypeRegistry registry)
    {
        if (getItem.Parameters[index: 0] is not { Name: "index", Type: var taken } ||
            index.ResolvedType is not { } given || given.FullName == taken.FullName)
        {
            return null;
        }

        string? conversion = taken.Name switch
        {
            "U64" => "to_index",
            "Integer" => "to_integer_count",
            _ => null
        };
        if (conversion == null ||
            (registry.LookupMemberRoutine(type: given, memberRoutineName: conversion, isFailable: true) ??
             registry.LookupMemberRoutine(type: given, memberRoutineName: conversion)) is not { } routine)
        {
            return null;
        }

        return MemberCall(receiver: index,
            memberName: conversion,
            arguments: [],
            routine: routine,
            resultType: routine.ReturnType,
            location: index.Location);
    }

    /// <summary>Whether a call of <paramref name="routine"/> can fail (declared or inferred).</summary>
    private static bool CanFail(RoutineInfo? routine)
    {
        return routine is { IsFailable: true } or { HasThrow: true };
    }

    private static CallExpression MemberCall(Expression receiver, string memberName, List<Expression> arguments,
        RoutineInfo routine, TypeSymbol? resultType, SourceLocation location)
    {
        // A bare literal on the left (`10` in `try 10 // n`) took the other operand's type, but as the receiver of the
        // call it is analyzed again with nothing beside it to take a type from. It keeps the type it took as its own
        // literal token, so the call stays the routine of that type.
        Expression pinned = receiver is LiteralExpression { ResolvedType: not null } literal
            ? UndecidedLiteralConformance.Conform(literal: literal)
            : receiver;
        var callee = new MemberExpression(Object: pinned, MemberName: memberName, Location: location)
        {
            IsFailable = true
        };
        return new CallExpression(Callee: callee, Arguments: arguments, Location: location)
        {
            ResolvedType = resultType,
            ResolvedRoutine = routine,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: routine)
        };
    }

    /// <summary>
    /// Rewrites every checked operator, failable subscript and statement-level compound assignment
    /// (<c>x += d</c> on a type without an in-place routine) of an analyzed body into the failable member
    /// call it stands for, so the recovery variant built from the body propagates their failures. Lambda
    /// bodies are not entered (their failures belong to whoever calls the lambda), and a nested recovery
    /// keyword keeps its own expression.
    /// </summary>
    public static Statement Resolve(Statement body, TypeRegistry registry)
    {
        return new Rewriter(registry: registry).VisitStatement(stmt: body);
    }

    /// <summary>
    /// Whether an analyzed body can fail where it stands: a <c>throw</c> or <c>absent</c>, a checked
    /// operator or subscript whose routine is failable, a call that can fail under recovery
    /// (<see cref="TypeRegistry.CanFailUnderRecovery"/>, which follows the user routines it calls), a call
    /// through a routine value, or the step of an <c>each</c> loop over an iterator that may call one. A
    /// lambda body and a nested recovery keyword do not count, for the same reasons as in
    /// <see cref="Resolve"/>.
    /// </summary>
    public static bool HasFailurePoint(Statement body, TypeRegistry registry)
    {
        var scanner = new Scanner(registry: registry);
        scanner.VisitStatement(stmt: body);
        return scanner.Found;
    }

    /// <summary>Whether an analyzed expression (a lambda's body) can fail where it stands, as
    /// <see cref="HasFailurePoint(Statement, TypeRegistry)"/> reads a body.</summary>
    public static bool HasFailurePoint(Expression body, TypeRegistry registry)
    {
        var scanner = new Scanner(registry: registry);
        scanner.VisitExpression(expr: body);
        return scanner.Found;
    }

    private sealed class Scanner(TypeRegistry registry) : AstRewriter
    {
        public bool Found { get; private set; }

        public override Statement VisitStatement(Statement stmt)
        {
            if (Found)
            {
                return stmt;
            }

            if (stmt is ThrowStatement { IsFatal: false })
            {
                Found = true;
                return stmt;
            }

            if (stmt is ExpressionStatement { Expression: CompoundAssignmentExpression compound } &&
                compound.Target.ResolvedType is { } targetType &&
                ForBinary(bin: new BinaryExpression(Left: compound.Target,
                        Operator: compound.Operator,
                        Right: compound.Value,
                        Location: compound.Location) { ResolvedType = targetType },
                    registry: registry) != null)
            {
                Found = true;
                return stmt;
            }

            return base.VisitStatement(stmt: stmt);
        }

        public override Expression VisitExpression(Expression expr)
        {
            if (Found)
            {
                return expr;
            }

            Found = expr switch
            {
                // A call through a routine value (the lambda may fail), and the step of an `each` loop over an
                // iterator that may call one.
                CallExpression call when RoutineValueCalls.ValueType(call: call) != null => true,
                _ when RoutineValueCalls.IsRecoverableIterationStep(expr: expr) => true,
                CallExpression call => registry.CanFailUnderRecovery(routine: call.ResolvedRoutine),
                GenericMemberRoutineCallExpression generic =>
                    registry.CanFailUnderRecovery(routine: generic.ResolvedRoutine),
                CreatorExpression creator => registry.CanFailUnderRecovery(routine: creator.ResolvedCreatorRoutine),
                BinaryExpression bin => ForBinary(bin: bin, registry: registry) != null,
                UnaryExpression unary => ForUnary(unary: unary, registry: registry) != null,
                IndexExpression idx => registry.CanFailUnderRecovery(routine: idx.ResolvedGetItem),
                _ => false
            };
            return Found
                ? expr
                : base.VisitExpression(expr: expr);
        }

        protected override Expression VisitRecovery(RecoveryExpression e)
        {
            return e;
        }
    }

    private sealed class Rewriter(TypeRegistry registry) : AstRewriter
    {
        protected override Expression VisitBinary(BinaryExpression e)
        {
            // `d[k] = v` writes through `setitem`, not a `getitem` read: the target keeps its subscript (only what
            // it indexes and the key are rewritten), or the write would first read a key that is not there yet.
            if (e is { Operator: BinaryOperator.Assign, Left: IndexExpression written })
            {
                Expression indexed = VisitExpression(expr: written.Object);
                Expression key = VisitExpression(expr: written.Index);
                Expression value = VisitExpression(expr: e.Right);
                if (ReferenceEquals(objA: indexed, objB: written.Object) &&
                    ReferenceEquals(objA: key, objB: written.Index) && ReferenceEquals(objA: value, objB: e.Right))
                {
                    return e;
                }

                IndexExpression kept = written with { Object = indexed, Index = key };
                kept.ResolvedType = written.ResolvedType;
                BinaryExpression assigned = e with { Left = kept, Right = value };
                assigned.ResolvedType = e.ResolvedType;
                return assigned;
            }

            Expression rewritten = base.VisitBinary(e: e);
            return rewritten is BinaryExpression bin && ForBinary(bin: bin, registry: registry) is { } call
                ? call
                : rewritten;
        }

        protected override Expression VisitUnary(UnaryExpression e)
        {
            Expression rewritten = base.VisitUnary(e: e);
            return rewritten is UnaryExpression unary && ForUnary(unary: unary, registry: registry) is { } call
                ? call
                : rewritten;
        }

        protected override Expression VisitIndex(IndexExpression e)
        {
            Expression rewritten = base.VisitIndex(e: e);
            return rewritten is IndexExpression idx && ForIndex(idx: idx, registry: registry) is { } call
                ? call
                : rewritten;
        }

        protected override Expression VisitRecovery(RecoveryExpression e)
        {
            return e;
        }

        protected override Statement VisitExpressionStatement(ExpressionStatement s)
        {
            // `x += d` as a statement is `x = x + d` when the type has no in-place routine (the Phase-6
            // fallback), so its checked operator is spelled out here and becomes a failable call too.
            if (s.Expression is CompoundAssignmentExpression
                {
                    Target: IdentifierExpression or MemberExpression { Object: IdentifierExpression }
                } compound &&
                compound.Target.ResolvedType is { } targetType &&
                (compound.Operator.GetInPlaceMemberRoutineName() is not { } inPlace ||
                 registry.LookupMemberRoutine(type: targetType, memberRoutineName: inPlace) == null))
            {
                Expression value = VisitExpression(expr: compound.Value);
                var bin = new BinaryExpression(Left: compound.Target with { },
                    Operator: compound.Operator,
                    Right: value,
                    Location: compound.Location) { ResolvedType = targetType };
                if (ForBinary(bin: bin, registry: registry) is { } call)
                {
                    return new AssignmentStatement(Target: compound.Target, Value: call, Location: s.Location);
                }
            }

            return base.VisitExpressionStatement(s: s);
        }
    }
}
