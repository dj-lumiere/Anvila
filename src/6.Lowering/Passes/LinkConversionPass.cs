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
    private readonly PostprocessingContext _ctx;

    internal LinkConversionPass(PostprocessingContext ctx)
    {
        _registry = ctx.Registry;
        _ctx = ctx;
    }

    /// <summary>Runs over the recovery variants' bodies, made from the routines' bodies before they were lowered.</summary>
    public void RunOnVariantBodies()
    {
        if (!_registry.Rules.ChecksAccessTokens)
        {
            return;
        }

        BodyDispatch.RunOnVariantBodies(registry: _registry, bodies: _ctx.VariantBodies,
            lower: (_, body) => VisitStatement(stmt: body));
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
        // A token on a link (a `for` element over a list of links) given where a link is kept hands over the link.
        if (target != null && ImplicitToken.IsLink(type: target) && LinkTaking.ReadTokenOnLink(value: value, registry: _registry) is { } read)
        {
            return read;
        }

        Expression token = MakeToken(value: value, target: target);
        // A link kept through a call or a subscript (`a.outs[k] = b`, `xs.add_last(value: b)`) is given the token as
        // its `Optional`, so its generation is taken here: the routine's own parameter is a type parameter.
        if (!wrap || target == null || !ImplicitToken.IsLinkShape(type: target) ||
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
        if (value is IndexExpression element && source is EntityTypeSymbol or GenericParameterTypeSymbol)
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

    /// <summary>The count of the tokens taken from links in this body, for unique local names.</summary>
    private int _taken;

    /// <inheritdoc/>
    protected override Statement VisitBlock(BlockStatement s)
    {
        // A statement that uses a link without taking it by `when` (`node.parent.rename()`) takes its token first,
        // and keeps it in use for as long as the statement runs.
        List<Statement>? statements = null;
        for (int i = 0; i < s.Statements.Count; i++)
        {
            Statement statement = s.Statements[index: i];
            List<Statement>? taken = TakeLinks(statement: statement);
            if (taken == null)
            {
                statements?.Add(item: statement);
                continue;
            }

            statements ??= [.. s.Statements.Take(count: i)];
            statements.AddRange(collection: taken);
        }

        return base.VisitBlock(s: statements == null ? s : s with { Statements = statements });
    }

    /// <summary>
    /// The statements <paramref name="statement"/> becomes when it uses links without taking them by <c>when</c>: each
    /// token taken into a local, a use kept on it, then the statement on the locals. A statement that names nothing new
    /// is put in a block of its own, so the uses end with it. Null when the statement takes no link.
    /// </summary>
    private List<Statement>? TakeLinks(Statement statement)
    {
        var collector = new LinkTakeCollector(registry: _registry, first: _taken);
        Statement rewritten = statement switch
        {
            ExpressionStatement expression => expression with { Expression = collector.VisitExpression(expr: expression.Expression) },
            AssignmentStatement assignment => assignment with
            {
                Target = collector.VisitExpression(expr: assignment.Target),
                Value = collector.VisitExpression(expr: assignment.Value)
            },
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } initializer } declaration } local =>
                local with { Declaration = declaration with { Initializer = collector.VisitExpression(expr: initializer) } },
            ReturnStatement { Value: { } value } result => result with { Value = collector.VisitExpression(expr: value) },
            IfStatement branch => branch with { Condition = collector.VisitExpression(expr: branch.Condition) },
            _ => statement
        };
        if (collector.Hoisted.Count == 0)
        {
            return null;
        }

        _taken = collector.Next;
        List<Statement> taken = [.. collector.Hoisted, rewritten];
        return statement is ExpressionStatement or AssignmentStatement or IfStatement
            ? [new BlockStatement(Statements: taken, Location: statement.Location)]
            : taken;
    }

    /// <inheritdoc/>
    protected override Expression VisitMember(MemberExpression e)
    {
        // A link used where its statement cannot keep the token in a local (a loop's condition, one side of `and`)
        // takes the token in place.
        Expression visited = base.VisitMember(e: e);
        return visited is MemberExpression { TakesLink: true } member &&
               LinkTaking.Take(link: member.Object, registry: _registry) is { } take
            ? member with { Object = take, TakesLink = false }
            : visited;
    }

    /// <summary>
    /// Takes the token of each link a statement uses into a local (<c>var __lk_N = take_modifying_link(link: ...)</c>,
    /// then <c>var __lu_N = __lk_N.begin_link_use()</c>), where the statement surely evaluates it: not inside one side
    /// of <c>and</c>/<c>or</c>, a conditional, a <c>when</c> or a lambda.
    /// </summary>
    private sealed class LinkTakeCollector(TypeRegistry registry, int first) : AstRewriter
    {
        public List<Statement> Hoisted { get; } = [];

        public int Next { get; private set; } = first;

        public override Expression VisitExpression(Expression expr)
        {
            return expr switch
            {
                BinaryExpression { Operator: BinaryOperator.And or BinaryOperator.Or } logical =>
                    logical with { Left = VisitExpression(expr: logical.Left) },
                ConditionalExpression or WhenExpression or BlockExpression or LambdaExpression or RecoveryExpression => expr,
                _ => base.VisitExpression(expr: expr)
            };
        }

        protected override Expression VisitMember(MemberExpression e)
        {
            Expression visited = base.VisitMember(e: e);
            if (visited is not MemberExpression { TakesLink: true } member ||
                LinkTaking.Take(link: member.Object, registry: registry) is not { } take)
            {
                return visited;
            }

            int n = Next++;
            string tokenName = $"{LinkTaking.TokenPrefix}{n}";
            Hoisted.Add(item: LinkTaking.Local(name: tokenName, value: take));
            if (LinkTaking.BeginUse(tokenName: tokenName, token: take.ResolvedType!, location: member.Location,
                    registry: registry) is { } begin)
            {
                Hoisted.Add(item: LinkTaking.Local(name: $"{LinkTaking.UsePrefix}{n}", value: begin, owesTeardown: true));
            }

            return member with
            {
                Object = new IdentifierExpression(Name: tokenName, Location: member.Location) { ResolvedType = take.ResolvedType },
                TakesLink = false
            };
        }
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
        // A construction written as a call of its type (`Creature(x: 0, parent: herd[0])`) is bound to its creator
        // later (CallBindingPass): its arguments are the member variables.
        if (visited is CallExpression
            {
                ResolvedRoutine: null, Callee: IdentifierExpression { Name: var typeName }, ResolvedType: { } made
            } construction && typeName == made.BareName && MembersOf(type: made) is { } members)
        {
            return ConvertConstruction(construction: construction, members: members);
        }

        // The take of a link's token is handed the link itself (or a token on it, read later), never a new link.
        if (visited is not CallExpression { ResolvedRoutine: { } routine } call ||
            (routine.GenericDefinition ?? routine).Name is RuntimeContract.TakeModifyingLink or RuntimeContract.TakeViewingLink)
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

    /// <summary>The member variables a construction of <paramref name="type"/> sets, or null for another type.</summary>
    private static List<MemberVariableInfo>? MembersOf(TypeSymbol type)
    {
        return type switch
        {
            EntityTypeSymbol entity => entity.MemberVariables,
            RecordTypeSymbol record => record.MemberVariables,
            _ => null
        };
    }

    /// <summary>A construction written as a call: each argument made into a link where its member variable keeps one.
    /// Arguments without a label fill the member variables in order.</summary>
    private Expression ConvertConstruction(CallExpression construction, List<MemberVariableInfo> members)
    {
        List<Expression>? arguments = null;
        for (int i = 0; i < construction.Arguments.Count; i++)
        {
            Expression argument = construction.Arguments[index: i];
            TypeSymbol? target = argument is NamedArgumentExpression named
                ? members.FirstOrDefault(predicate: m => m.Name == named.Name)?.Type
                : i < members.Count
                    ? members[index: i].Type
                    : null;
            Expression inner = argument is NamedArgumentExpression wrapped ? wrapped.Value : argument;
            Expression converted = Convert(value: inner, target: target);
            if (ReferenceEquals(objA: converted, objB: inner))
            {
                continue;
            }

            arguments ??= [.. construction.Arguments];
            arguments[index: i] = argument is NamedArgumentExpression named2
                ? named2 with { Value = converted, ResolvedType = converted.ResolvedType }
                : converted;
        }

        return arguments == null ? construction : construction with { Arguments = arguments };
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

        // A link written around a token on a link (`Creature(parent: at)`, `at` a `for` element over a list of links)
        // is that link itself.
        if (ImplicitToken.IsLink(type: made) &&
            creator.MemberVariables.FirstOrDefault(predicate: m => m.Name == RuntimeContract.Carrier.ValueField).Value is { } held &&
            LinkTaking.ReadTokenOnLink(value: held, registry: _registry) is { } read)
        {
            return read;
        }

        if (MembersOf(type: made) is not { } members)
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
