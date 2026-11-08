using Builder.Declaration;
using Builder.Verification;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Writes the token a link is made from where a place that keeps one (<c>Modifying[Node]?</c> / <c>Viewing[Node]?</c>)
/// is given the entity or a wider token: <c>child.parent = node</c> becomes <c>child.parent = node.modify()</c>, the same
/// way a token parameter takes the entity. The optional then keeps the token's entity and its slot's generation. Covers assignments, typed declarations, call arguments and creator member
/// values. The analysis accepted each one through <see cref="ImplicitToken.LinkMade"/>.
/// </summary>
internal sealed class LinkConversionPass : AstRewriter
{
    private readonly TypeRegistry _registry;

    internal LinkConversionPass(PostprocessingContext ctx)
    {
        _registry = ctx.Registry;
    }

    /// <summary>Runs over the program's routine bodies.</summary>
    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(registry: _registry, program: program, lower: r => VisitStatement(stmt: r.Body));
    }

    /// <summary>
    /// The value made into a link of <paramref name="target"/>'s type when it is one the target takes that way, or the
    /// value itself.
    /// </summary>
    private Expression Convert(Expression value, TypeSymbol? target, bool wrap = false)
    {
        Expression token = MakeToken(value: value, target: target);
        // A link kept through a call or a subscript (`a.outs[k] = b`, `xs.add_last(value: b)`) is given the token as
        // its `Optional`, so its generation is taken here: the routine's own parameter is a type parameter.
        if (!wrap || target == null || !ImplicitToken.IsLink(type: target) ||
            token.ResolvedType?.BareName is not (RuntimeContract.Viewing or RuntimeContract.Modifying))
        {
            return token;
        }

        var present = new LiteralExpression(Value: true, LiteralType: Tokenizer.TokenType.True, Location: token.Location)
        {
            ResolvedType = _registry.LookupType(name: "Bool")
        };
        return new CreatorExpression(TypeName: RuntimeContract.OptionalCarrier,
            TypeArguments: [ExpressionLoweringPass.TypeInfoToExpr(type: target.TypeArguments![index: 0], loc: token.Location)],
            MemberVariables:
            [
                (RuntimeContract.Carrier.PresentField, present),
                (RuntimeContract.Carrier.ValueField, token)
            ],
            Location: token.Location) { ResolvedType = target, ConstructedType = target };
    }

    /// <summary>The token a link is made from (see <see cref="Convert"/>), or the value itself.</summary>
    private Expression MakeToken(Expression value, TypeSymbol? target)
    {
        if (target == null || value.ResolvedType is not { } source ||
            ImplicitToken.LinkMade(source: source, target: target) is not { } verb ||
            _registry.LookupMemberRoutineOverload(type: source, memberRoutineName: verb, argTypes: []) is not
                { } routine)
        {
            return value;
        }

        TypeSymbol made = target.TypeArguments![index: 0];
        // An entity element (`w.watch = w.nodes[0]`) is read as a token on it where it is, as `var b = boxes[0]` is.
        if (value is IndexExpression element && source is EntityTypeSymbol)
        {
            element.ReadsElementToken = true;
            element.ResolvedType = made;
            return element;
        }

        return new CallExpression(
            Callee: new MemberExpression(Object: value, MemberName: verb, Location: value.Location)
            {
                ResolvedType = made
            },
            Arguments: [],
            Location: value.Location)
        {
            ResolvedRoutine = routine,
            ResolvedType = made,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: routine),
            IsSynthesizedLowering = true
        };
    }

    /// <inheritdoc/>
    protected override Statement VisitAssignment(AssignmentStatement s)
    {
        Statement visited = base.VisitAssignment(s: s);
        if (visited is not AssignmentStatement assign)
        {
            return visited;
        }

        Expression value = Convert(value: assign.Value, target: assign.Target.ResolvedType,
            wrap: assign.Target is IndexExpression);
        return ReferenceEquals(objA: value, objB: assign.Value) ? assign : assign with { Value = value };
    }

    /// <inheritdoc/>
    protected override Expression VisitBinary(BinaryExpression e)
    {
        Expression visited = base.VisitBinary(e: e);
        if (visited is not BinaryExpression { Operator: BinaryOperator.Assign } assign)
        {
            return visited;
        }

        Expression value = Convert(value: assign.Right, target: assign.Left.ResolvedType,
            wrap: assign.Left is IndexExpression);
        return ReferenceEquals(objA: value, objB: assign.Right) ? assign : assign with { Right = value };
    }

    /// <inheritdoc/>
    protected override Statement VisitDeclarationStatement(DeclarationStatement s)
    {
        Statement visited = base.VisitDeclarationStatement(s: s);
        if (visited is not DeclarationStatement
            {
                Declaration: VariableDeclaration { Type.ResolvedType: { } declared, Initializer: { } initializer } declaration
            } statement)
        {
            return visited;
        }

        Expression value = Convert(value: initializer, target: declared);
        return ReferenceEquals(objA: value, objB: initializer)
            ? visited
            : statement with { Declaration = declaration with { Initializer = value } };
    }

    /// <inheritdoc/>
    protected override Expression VisitCall(CallExpression e)
    {
        Expression visited = base.VisitCall(e: e);
        if (visited is not CallExpression { ResolvedRoutine: { } routine } call)
        {
            return visited;
        }

        TypeSymbol? receiver = call.Callee is MemberExpression { Object.ResolvedType: { } owner } ? owner : null;
        List<Expression>? arguments = null;
        List<ParamInfo> parameters = routine.Parameters.Where(predicate: p => p.Name != "me").ToList();
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            Expression argument = call.Arguments[index: i];
            ParamInfo? parameter = argument is NamedArgumentExpression named
                ? parameters.FirstOrDefault(predicate: p => p.Name == named.Name)
                : i < parameters.Count
                    ? parameters[index: i]
                    : null;
            if (parameter == null)
            {
                continue;
            }

            TypeSymbol target = Substitute(type: parameter.Type, routine: routine, receiver: receiver);
            Expression inner = argument is NamedArgumentExpression wrapped ? wrapped.Value : argument;
            Expression converted = Convert(value: inner, target: target,
                wrap: parameter.Type is GenericParameterTypeSymbol);
            if (ReferenceEquals(objA: converted, objB: inner))
            {
                continue;
            }

            arguments ??= [.. call.Arguments];
            arguments[index: i] = argument is NamedArgumentExpression named2
                ? named2 with { Value = converted, ResolvedType = converted.ResolvedType }
                : converted;
        }

        return arguments == null ? call : call with { Arguments = arguments };
    }

    /// <summary>A parameter's type with the owner's type parameters read from the receiver
    /// (<c>T</c> of <c>List[T].add_last</c> is the <c>Modifying[Node]?</c> of a <c>List[Modifying[Node]?]</c>).</summary>
    private static TypeSymbol Substitute(TypeSymbol type, RoutineInfo routine, TypeSymbol? receiver)
    {
        if (type is not GenericParameterTypeSymbol parameter || receiver == null)
        {
            return type;
        }

        TypeSymbol owner = receiver.BareName is RuntimeContract.Viewing or RuntimeContract.Modifying &&
                           receiver.TypeArguments is [{ } reached]
            ? reached
            : receiver;
        List<string>? names = routine.OwnerType?.GenericParameters;
        int slot = names?.IndexOf(item: parameter.Name) ?? -1;
        return slot >= 0 && owner.TypeArguments is { } arguments && slot < arguments.Count ? arguments[index: slot] : type;
    }

    /// <inheritdoc/>
    protected override Expression VisitCreator(CreatorExpression e)
    {
        Expression visited = base.VisitCreator(e: e);
        if (visited is not CreatorExpression creator || creator.ResolvedType is not { } made)
        {
            return visited;
        }

        List<MemberVariableInfo>? members = made switch
        {
            EntityTypeSymbol entity => entity.MemberVariables,
            RecordTypeSymbol record => record.MemberVariables,
            _ => null
        };
        if (members == null)
        {
            return visited;
        }

        List<(string Name, Expression Value)>? values = null;
        for (int i = 0; i < creator.MemberVariables.Count; i++)
        {
            (string name, Expression value) = creator.MemberVariables[index: i];
            TypeSymbol? target = members.FirstOrDefault(predicate: m => m.Name == name)?.Type;
            Expression converted = Convert(value: value, target: target);
            if (ReferenceEquals(objA: converted, objB: value))
            {
                continue;
            }

            values ??= [.. creator.MemberVariables];
            values[index: i] = (name, converted);
        }

        return values == null ? creator : creator with { MemberVariables = values };
    }
}
