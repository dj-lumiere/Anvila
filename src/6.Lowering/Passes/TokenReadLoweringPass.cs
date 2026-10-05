using Builder.Declaration;
using Builder.Instantiation;
using Builder.Verification;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Reads a value through its token where the value itself is wanted. A token (<c>Viewing[T]</c>,
/// <c>Modifying[T]</c>) is the address of a value where it is stored; an iterator hands one out for each
/// element, and so does an element read into a variable. Member access through a token already reaches the
/// value. An operator, a comparison, an argument to a parameter of the value's own type, a declared-type
/// variable, an assignment, a return, or a condition needs the value instead, so the token becomes
/// <c>token.access()</c> there: a record read out of its place as a holder of its own, as <c>getitem</c> gives
/// one. Semantic analysis already accepted the token in those places (it typed them as the value).
/// An entity's token is left alone: reading the entity out would make a second owner of it.
/// </summary>
internal sealed class TokenReadLoweringPass(PostprocessingContext ctx) : AstRewriter
{
    private const string AccessMemberRoutine = "access";

    /// <summary>The return type of the body being lowered, when known.</summary>
    private TypeSymbol? _returnType;

    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(registry: ctx.Registry,
            program: program,
            lower: r => LowerBody(body: r.Body, returnType: r.ResolvedInfo?.ReturnType));
    }

    public void RunOnVariantBodies()
    {
        BodyDispatch.RunOnVariantBodies(registry: ctx.Registry,
            bodies: ctx.VariantBodies,
            lower: (key, body) => LowerBody(body: body,
                returnType: ctx.Registry.LookupRoutine(fullName: key)?.ReturnType));
    }

    public void RunOnInstantiatedGenericBodies(Dictionary<string, MonomorphizedBody> instantiatedGenericBodies)
    {
        BodyDispatch.RunOnInstantiatedGenericBodies(registry: ctx.Registry,
            bodies: instantiatedGenericBodies,
            lower: (_, entry) => LowerBody(body: entry.Ast.Body, returnType: entry.Info.ReturnType));
    }

    private Statement LowerBody(Statement body, TypeSymbol? returnType)
    {
        TypeSymbol? previous = _returnType;
        _returnType = returnType;
        Statement lowered = VisitStatement(stmt: body);
        _returnType = previous;
        return lowered;
    }

    // Statement positions

    protected override Statement VisitDeclarationStatement(DeclarationStatement s)
    {
        Statement visited = base.VisitDeclarationStatement(s: s);
        if (visited is not DeclarationStatement
            {
                Declaration: VariableDeclaration { Type: { } declared, Initializer: { } init } decl
            } decl2 ||
            IsTokenTypeName(name: declared.Name))
        {
            return visited;
        }

        Expression read = ReadAs(expr: init, target: declared.ResolvedType);
        return ReferenceEquals(objA: read, objB: init)
            ? visited
            : decl2 with { Declaration = decl with { Initializer = read } };
    }

    protected override Statement VisitAssignment(AssignmentStatement s)
    {
        Statement visited = base.VisitAssignment(s: s);
        if (visited is not AssignmentStatement assign || IsToken(type: assign.Target.ResolvedType))
        {
            return visited;
        }

        Expression read = Read(expr: assign.Value);
        return ReferenceEquals(objA: read, objB: assign.Value)
            ? visited
            : assign with { Value = read };
    }

    protected override Statement VisitReturn(ReturnStatement s)
    {
        Statement visited = base.VisitReturn(s: s);
        if (visited is not ReturnStatement { Value: { } value } ret || IsToken(type: _returnType))
        {
            return visited;
        }

        // A recovery variant's return wrapped in its carrier (`return Maybe(present: true, value: x)`) hands out
        // what the carrier holds: read `x` when the carrier holds the value, not a token.
        if (value is CreatorExpression
            {
                ResolvedType: RecordTypeSymbol { TypeArguments: [{ } held] } carrier,
                MemberVariables: var members
            } creator &&
            carrier.BareName is "Maybe" or "Check" or "Lookup" && !IsToken(type: held))
        {
            var readMembers = members.Select(selector: m => (m.Name, m.Value is null ? m.Value : ReadAs(expr: m.Value, target: held))).ToList();
            return readMembers.Select(selector: m => m.Item2)
                              .SequenceEqual(second: members.Select(selector: m => m.Value))
                ? visited
                : ret with { Value = creator with { MemberVariables = readMembers } };
        }

        Expression read = ReadAs(expr: value, target: _returnType);
        return ReferenceEquals(objA: read, objB: value)
            ? visited
            : ret with { Value = read };
    }

    protected override Statement VisitIf(IfStatement s)
    {
        Statement visited = base.VisitIf(s: s);
        return visited is IfStatement ifs && Read(expr: ifs.Condition) is var cond &&
               !ReferenceEquals(objA: cond, objB: ifs.Condition)
            ? ifs with { Condition = cond }
            : visited;
    }

    protected override Statement VisitWhile(WhileStatement s)
    {
        Statement visited = base.VisitWhile(s: s);
        return visited is WhileStatement ws && Read(expr: ws.Condition) is var cond &&
               !ReferenceEquals(objA: cond, objB: ws.Condition)
            ? ws with { Condition = cond }
            : visited;
    }

    // Expression positions

    protected override Expression VisitBinary(BinaryExpression e)
    {
        Expression visited = base.VisitBinary(e: e);
        if (visited is not BinaryExpression bin)
        {
            return visited;
        }

        // `(latest = item)` written as an expression: the value goes into a non-token place as a value.
        if (bin.Operator == BinaryOperator.Assign)
        {
            Expression assigned = IsToken(type: bin.Left.ResolvedType)
                ? bin.Right
                : ReadAs(expr: bin.Right, target: bin.Left.ResolvedType);
            return ReferenceEquals(objA: assigned, objB: bin.Right)
                ? visited
                : bin with { Right = assigned };
        }

        Expression left = Read(expr: bin.Left);
        Expression right = Read(expr: bin.Right);
        return ReferenceEquals(objA: left, objB: bin.Left) && ReferenceEquals(objA: right, objB: bin.Right)
            ? visited
            : bin with { Left = left, Right = right };
    }

    protected override Expression VisitUnary(UnaryExpression e)
    {
        Expression visited = base.VisitUnary(e: e);
        return visited is UnaryExpression unary && Read(expr: unary.Operand) is var operand &&
               !ReferenceEquals(objA: operand, objB: unary.Operand)
            ? unary with { Operand = operand }
            : visited;
    }

    protected override Expression VisitChainedComparison(ChainedComparisonExpression e)
    {
        Expression visited = base.VisitChainedComparison(e: e);
        if (visited is not ChainedComparisonExpression chain)
        {
            return visited;
        }

        List<Expression> operands = chain.Operands.Select(selector: Read).ToList();
        return operands.SequenceEqual(second: chain.Operands)
            ? visited
            : chain with { Operands = operands };
    }

    protected override Expression VisitIndex(IndexExpression e)
    {
        Expression visited = base.VisitIndex(e: e);
        return visited is IndexExpression index && Read(expr: index.Index) is var position &&
               !ReferenceEquals(objA: position, objB: index.Index)
            ? index with { Index = position }
            : visited;
    }

    protected override Expression VisitCall(CallExpression e)
    {
        Expression visited = base.VisitCall(e: e);
        if (visited is not CallExpression call)
        {
            return visited;
        }

        // A call through a routine value (`me.transform(item)`) has no routine of its own: its parameters are
        // the routine type's.
        if (call.ResolvedRoutine is not { } routine)
        {
            return call.Callee.ResolvedType is RoutineTypeSymbol routineType
                ? ReadRoutineValueArguments(call: call, routineType: routineType)
                : visited;
        }

        // A parameter of the value's own type takes the value. A parameter typed as a token, or a parameter of
        // its own (a marker parameter `you: Accessing[T]` binds the token itself), keeps the token.
        List<ParamInfo> parameters = routine.Parameters.Where(predicate: p => p.Name != "me").ToList();
        bool changed = false;
        var arguments = new List<Expression>(capacity: call.Arguments.Count);
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            Expression arg = call.Arguments[index: i];
            ParamInfo? param = arg is NamedArgumentExpression named
                ? parameters.FirstOrDefault(predicate: p => p.Name == named.Name)
                : i < parameters.Count
                    ? parameters[index: i]
                    : null;
            if (param == null || IsToken(type: param.Type) || param.Type is ProtocolTypeSymbol ||
                IsMarkerParameter(routine: routine, type: param.Type))
            {
                arguments.Add(item: arg);
                continue;
            }

            Expression value = arg is NamedArgumentExpression n ? n.Value : arg;
            Expression read = Read(expr: value);
            if (ReferenceEquals(objA: read, objB: value))
            {
                arguments.Add(item: arg);
                continue;
            }

            changed = true;
            arguments.Add(item: arg is NamedArgumentExpression wrapper
                ? wrapper with { Value = read, ResolvedType = read.ResolvedType }
                : read);
        }

        return changed
            ? call with { Arguments = arguments }
            : visited;
    }

    /// <summary>The arguments of a call through a routine value, read where its routine type takes the value.</summary>
    private Expression ReadRoutineValueArguments(CallExpression call, RoutineTypeSymbol routineType)
    {
        bool changed = false;
        var arguments = new List<Expression>(capacity: call.Arguments.Count);
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            Expression arg = call.Arguments[index: i];
            if (i >= routineType.ParameterTypes.Count || IsToken(type: routineType.ParameterTypes[index: i]))
            {
                arguments.Add(item: arg);
                continue;
            }

            Expression value = arg is NamedArgumentExpression n ? n.Value : arg;
            Expression read = Read(expr: value);
            changed |= !ReferenceEquals(objA: read, objB: value);
            arguments.Add(item: arg is NamedArgumentExpression wrapper && !ReferenceEquals(objA: read, objB: value)
                ? wrapper with { Value = read, ResolvedType = read.ResolvedType }
                : ReferenceEquals(objA: read, objB: value) ? arg : read);
        }

        return changed
            ? call with { Arguments = arguments }
            : call;
    }

    /// <summary>
    /// <paramref name="expr"/> read as <paramref name="target"/> wants it: a tuple literal element by element (an
    /// iterator handing out `(index, item)` as a `Tuple[U64, T]`), anything else read through its token when the
    /// target is not itself a token.
    /// </summary>
    private Expression ReadAs(Expression expr, TypeSymbol? target)
    {
        if (expr is TupleLiteralExpression tuple && target is TupleTypeSymbol { ElementTypes: var elements } &&
            elements.Count == tuple.Elements.Count)
        {
            List<Expression> read = tuple.Elements.Select(selector: (element, i) =>
                                               IsToken(type: elements[index: i]) ? element : Read(expr: element))
                                          .ToList();
            return read.SequenceEqual(second: tuple.Elements)
                ? expr
                : tuple with { Elements = read, ResolvedType = target };
        }

        // The same tuple once it is a construction (`Tuple[U64, Item](item0: index, item1: item)`).
        if (expr is CreatorExpression { ResolvedType: TupleTypeSymbol, MemberVariables: var members } creator &&
            target is TupleTypeSymbol { ElementTypes: var slots } targetTuple && slots.Count == members.Count)
        {
            var readMembers = members.Select(selector: (m, i) =>
                                          (m.Name, IsToken(type: slots[index: i]) ? m.Value : Read(expr: m.Value)))
                                     .ToList();
            return readMembers.Select(selector: m => m.Item2).SequenceEqual(second: members.Select(selector: m => m.Value))
                ? expr
                : creator with
                {
                    MemberVariables = readMembers, ResolvedType = targetTuple, ConstructedType = targetTuple
                };
        }

        // A place typed as an iterator's `Item` (`S/Iter/Item`, a token once instantiated) or as the token itself
        // keeps the token.
        return IsToken(type: target) || target is AssociatedProjectionTypeSymbol ||
               target != null && expr.ResolvedType?.FullName == target.FullName
            ? expr
            : Read(expr: expr);
    }

    // Reading through a token

    /// <summary>
    /// <paramref name="expr"/> read through its token (<c>expr.access()</c>) when it is a value's token, else
    /// unchanged. An entity's token and a token whose value type is still a parameter are left as they are:
    /// the second is read once its body is instantiated.
    /// </summary>
    private Expression Read(Expression expr)
    {
        if (expr.ResolvedType is not RecordTypeSymbol { TypeArguments: [{ } value] } token ||
            !IsToken(type: token) ||
            value is GenericParameterTypeSymbol or ErrorTypeSymbol ||
            value is EntityTypeSymbol ||
            ctx.Registry.LookupMemberRoutine(type: token, memberRoutineName: AccessMemberRoutine) is not { } access)
        {
            return expr;
        }

        // Bound as analysis binds a member call: the routine substituted for the token's own type. Which
        // kind's body (record or entity) it runs is picked when it is instantiated.
        return new CallExpression(
            Callee: new MemberExpression(Object: expr, MemberName: AccessMemberRoutine, Location: expr.Location),
            Arguments: [],
            Location: expr.Location)
        {
            ResolvedRoutine = access,
            ResolvedType = value,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: access),
            IsSynthesizedLowering = true
        };
    }

    /// <summary>Whether <paramref name="type"/> is a parameter of <paramref name="routine"/>'s own bound by a marker
    /// (`you: Accessing[T]` is `[__T0 obeys Accessing[T]](you: __T0)`): it takes the token itself. Any other
    /// parameter of its own (`nested_repr(value: T)`) takes the value.</summary>
    private static bool IsMarkerParameter(RoutineInfo routine, TypeSymbol type)
    {
        return type is GenericParameterTypeSymbol bound &&
               routine.GenericConstraints?.Any(predicate: c =>
                   c is { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: [{ } marker] } &&
                   c.ParameterName == bound.Name &&
                   RuntimeContract.IsMarkerProtocol(baseName: TypeSymbol.StripTypeArgs(name: marker.Name))) == true;
    }

    private static bool IsToken(TypeSymbol? type)
    {
        return type is RecordTypeSymbol token && IsTokenTypeName(name: (token.GenericDefinition ?? token).BareName);
    }

    private static bool IsTokenTypeName(string name)
    {
        return TypeSymbol.StripTypeArgs(name: name) is RuntimeContract.Viewing or RuntimeContract.Modifying;
    }
}
