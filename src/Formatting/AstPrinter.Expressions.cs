using System.Text;
using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Formatting;

/// <summary>Where an expression stands, which decides what may follow it.</summary>
internal enum ExprContext
{
    /// <summary>The whole expression of a statement or clause: nothing binding follows it.</summary>
    Statement,

    /// <summary>An item of a comma-separated list: a comma may follow it.</summary>
    ListItem
}

/// <summary>
/// Printing of expressions, types and patterns. Parentheses are printed only where the parser needs them: each
/// node knows the precedence level the parser produces it at, each operand position knows the level the parser
/// reads there, and a node whose tail would swallow what follows it (a recovery prefix, a lambda, a
/// <c>with</c>) is parenthesized when something it would swallow follows.
/// </summary>
internal sealed partial class AstPrinter
{
    // Precedence levels, from the parser's call chain (a higher level binds tighter).
    private const int PAssign = 1;
    private const int PWith = 2;
    private const int PConditional = 3;
    private const int PCoalesce = 4;
    private const int POr = 5;
    private const int PRange = 6;
    private const int PAnd = 7;
    private const int PEquality = 8;
    private const int PComparison = 9;
    private const int PIs = 10;
    private const int PBitOr = 11;
    private const int PBitXor = 12;
    private const int PBitAnd = 13;
    private const int PShift = 14;
    private const int PAdditive = 15;
    private const int PMultiplicative = 16;
    private const int PPower = 17;
    private const int PUnary = 18;
    private const int PPostfix = 19;
    private const int PPrimary = 20;

    /// <summary>The nesting of brackets around the expression being printed (a <c>when</c> must be outside all).</summary>
    private int _bracketDepth;

    /// <summary>True while printing the condition of a condition-based <c>when</c> arm (bare lambdas read
    /// differently there).</summary>
    private bool _whenCondition;

    private Doc Expr(Expression expression, ExprContext context)
    {
        return Expr(expression: expression, min: 0, follow: 0, comma: context == ExprContext.ListItem);
    }

    /// <summary>
    /// <paramref name="expression"/> printed where the parser reads level <paramref name="min"/>, followed by an
    /// operator of level <paramref name="follow"/> (0 when nothing follows) or by a comma.
    /// </summary>
    private Doc Expr(Expression expression, int min, int follow, bool comma)
    {
        if (NeedsParentheses(expression: expression, min: min, follow: follow, comma: comma))
        {
            if (expression is WhenExpression)
            {
                throw Refuse(at: expression.Location,
                    reason: $"a {expression.GetType().Name} inside a larger expression");
            }

            _bracketDepth++;
            bool savedCondition = _whenCondition;
            _whenCondition = false;
            Doc inner = Bare(expression: expression, follow: 0, comma: false);
            _whenCondition = savedCondition;
            _bracketDepth--;
            return Doc.Concat(Doc.Text(text: "("), inner, Doc.Text(text: ")"));
        }

        return Bare(expression: expression, follow: follow, comma: comma);
    }

    private bool NeedsParentheses(Expression expression, int min, int follow, bool comma)
    {
        if (Precedence(expression: expression) < min)
        {
            return true;
        }

        return expression switch
        {
            RecoveryExpression => follow >= POr,
            LambdaExpression => follow >= PAssign,
            ConditionalExpression => follow >= PAssign,
            WithExpression => comma || follow >= PCoalesce,
            // `x have A and B` reads as a flags test, and a bare `x is T and y` is rejected.
            FlagsTestExpression => follow is POr or PAnd,
            BinaryExpression { Operator: BinaryOperator.Have or BinaryOperator.Lack, Right: IdentifierExpression } =>
                follow is POr or PAnd,
            IsPatternExpression { Pattern: TypePattern { VariableName: null, Bindings: null } } =>
                follow is POr or PAnd,
            _ => false
        };
    }

    /// <summary>The level the parser produces <paramref name="expression"/> at.</summary>
    private static int Precedence(Expression expression)
    {
        return expression switch
        {
            BinaryExpression binary => BinaryPrecedence(binary: binary),
            CompoundAssignmentExpression => PAssign,
            WithExpression => PWith,
            ConditionalExpression => PConditional,
            RangeExpression => PRange,
            ChainedComparisonExpression => PComparison,
            IsPatternExpression or FlagsTestExpression => PIs,
            UnaryExpression { Operator: UnaryOperator.ForceUnwrap } => PPostfix,
            UnaryExpression or StealExpression or BackIndexExpression or RecoveryExpression => PUnary,
            LiteralExpression { Value: string text } when text.StartsWith(value: '-') => PUnary,
            CallExpression or MemberExpression or IndexExpression or GenericMemberExpression
                or GenericMemberRoutineCallExpression or SpliceMemberExpression => PPostfix,
            _ => PPrimary
        };
    }

    private static int BinaryPrecedence(BinaryExpression binary)
    {
        return binary.Operator switch
        {
            BinaryOperator.Assign => PAssign,
            BinaryOperator.NoneCoalesce => PCoalesce,
            BinaryOperator.Or or BinaryOperator.But => POr,
            BinaryOperator.And => PAnd,
            BinaryOperator.NotEqual or BinaryOperator.IdentityEqual or BinaryOperator.IdentityNotEqual => PEquality,
            BinaryOperator.Equal or BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater
                or BinaryOperator.GreaterEqual or BinaryOperator.ThreeWayComparator => PComparison,
            BinaryOperator.Have or BinaryOperator.Lack when binary.Left is RangeExpression => PRange,
            BinaryOperator.Is or BinaryOperator.IsNot or BinaryOperator.Obeys or BinaryOperator.Disobeys
                or BinaryOperator.Have or BinaryOperator.Lack or BinaryOperator.In
                or BinaryOperator.NotIn => PIs,
            BinaryOperator.BitwiseOr => PBitOr,
            BinaryOperator.BitwiseXor => PBitXor,
            BinaryOperator.BitwiseAnd => PBitAnd,
            BinaryOperator.ArithmeticLeftShift or BinaryOperator.ArithmeticRightShift
                or BinaryOperator.LogicalRightShift => PShift,
            BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.AddWrap or BinaryOperator.SubtractWrap
                or BinaryOperator.AddClamp or BinaryOperator.SubtractClamp or BinaryOperator.AddUnchecked
                or BinaryOperator.SubtractUnchecked => PAdditive,
            BinaryOperator.Power or BinaryOperator.PowerWrap or BinaryOperator.PowerClamp
                or BinaryOperator.PowerUnchecked => PPower,
            _ => PMultiplicative
        };
    }

    /// <summary><paramref name="expression"/> without parentheses of its own.</summary>
    private Doc Bare(Expression expression, int follow, bool comma)
    {
        if (CallChainDoc(expression: expression) is { } callChain)
        {
            return callChain;
        }

        switch (expression)
        {
            case LiteralExpression literal:
                return Doc.Text(text: LiteralText(literal: literal));
            case InsertedTextExpression inserted:
                return InsertedTextDoc(inserted: inserted);
            case IdentifierExpression identifier:
                return Doc.Text(text: IdentifierText(identifier: identifier));
            case BinaryExpression binary:
                return BinaryDoc(binary: binary, follow: follow);
            case CompoundAssignmentExpression compound:
                return Doc.Concat(Expr(expression: compound.Target, min: PWith, follow: PAssign, comma: false),
                    Doc.Text(text: " " + compound.Operator.ToStringRepresentation() + "= "),
                    Expr(expression: compound.Value, min: PAssign, follow: follow, comma: comma));
            case UnaryExpression unary:
                return UnaryDoc(unary: unary, follow: follow, comma: comma);
            case StealExpression steal:
                if (steal.IsImplicitMove)
                {
                    throw Unsupported(node: steal, at: steal.Location);
                }

                return Doc.Concat(Doc.Text(text: "steal "),
                    Expr(expression: steal.Operand, min: PUnary, follow: follow, comma: comma));
            case BackIndexExpression back:
                return Doc.Concat(Doc.Text(text: "^"),
                    Expr(expression: back.Operand, min: PUnary, follow: follow, comma: comma));
            case RecoveryExpression recovery:
                return RecoveryDoc(recovery: recovery);
            case CallExpression call:
                return CallDoc(call: call);
            case MemberExpression member:
                if (member.IsFailable)
                {
                    throw Unsupported(node: member, at: member.Location);
                }

                return Doc.Concat(Expr(expression: member.Object, min: PPostfix, follow: PPostfix, comma: false),
                    Doc.Text(text: "." + member.MemberName));
            case IndexExpression index:
                return Doc.Concat(Expr(expression: index.Object, min: PPostfix, follow: PPostfix, comma: false),
                    InBrackets(open: "[", close: "]",
                        content: () => Expr(expression: index.Index, min: 0, follow: 0, comma: false)));
            case GenericMemberExpression generic:
                return Doc.Concat(GenericHead(receiver: generic.Object, member: generic.MemberName),
                    TypeArgumentsDoc(arguments: generic.TypeArguments));
            case GenericMemberRoutineCallExpression genericCall:
                if (genericCall.IsMemoryOperation)
                {
                    throw Unsupported(node: genericCall, at: genericCall.Location);
                }

                return Doc.Concat(GenericHead(receiver: genericCall.Object, member: genericCall.MemberRoutineName),
                    TypeArgumentsDoc(arguments: genericCall.TypeArguments),
                    ArgumentList(arguments: genericCall.Arguments));
            case SpliceExpression splice:
                return Doc.Text(text: SpliceText(inner: splice.Inner));
            case SpliceMemberExpression spliceMember:
                return Doc.Concat(Expr(expression: spliceMember.Object, min: PPostfix, follow: PPostfix, comma: false),
                    Doc.Text(text: "." + SpliceText(inner: spliceMember.Selector.Inner)));
            case TupleLiteralExpression tuple:
                return tuple.Elements.Count == 1
                    ? Doc.Concat(Doc.Text(text: "("),
                        InBrackets(open: "", close: "",
                            content: () => Expr(expression: tuple.Elements[index: 0], context: ExprContext.ListItem)),
                        Doc.Text(text: ",)"))
                    : ListDoc(open: "(", close: ")", elements: tuple.Elements);
            case ListLiteralExpression list:
                if (list.ElementType != null || list.ArrayLength != null)
                {
                    throw Unsupported(node: list, at: list.Location);
                }

                return ListDoc(open: "[", close: "]", elements: list.Elements);
            case SetLiteralExpression set:
                if (set.ElementType != null)
                {
                    throw Unsupported(node: set, at: set.Location);
                }

                return ListDoc(open: "{", close: "}", elements: set.Elements);
            case DictLiteralExpression dict:
                return DictDoc(dict: dict);
            case RangeExpression range:
                return RangeDoc(range: range, follow: follow);
            case ChainedComparisonExpression chain:
                return ChainDoc(chain: chain, follow: follow);
            case ConditionalExpression conditional:
                return Doc.Concat(Doc.Text(text: "if "),
                    Expr(expression: conditional.Condition, min: PCoalesce, follow: 0, comma: false),
                    Doc.Text(text: " then "),
                    Expr(expression: conditional.TrueExpression, min: 0, follow: 0, comma: false),
                    Doc.Text(text: " else "),
                    Expr(expression: conditional.FalseExpression, min: 0, follow: follow, comma: comma));
            case WithExpression with:
                return WithDoc(with: with);
            case LambdaExpression lambda:
                return LambdaDoc(lambda: lambda);
            case IsPatternExpression isPattern:
                return IsPatternDoc(isPattern: isPattern);
            case FlagsTestExpression flags:
                return FlagsTestDoc(flags: flags);
            case WhenExpression whenExpression:
                return WhenExpressionDoc(when: whenExpression, follow: follow, comma: comma);
            default:
                throw Unsupported(node: expression, at: expression.Location);
        }
    }

    /// <summary>
    /// A chain of at least two member calls (<c>xs.where(...).select(...)</c>). With three calls or more it always
    /// breaks before each <c>.</c> after its head, one level in, and with two only when it does not fit (the lexers
    /// read a line that starts with <c>.</c> as the continuation of the one before). The head is the receiver and
    /// the plain member accesses before the first call (<c>me.items</c>). Null when the expression is not such a
    /// chain.
    /// </summary>
    private Doc? CallChainDoc(Expression expression)
    {
        var segments = new List<(bool Dot, Expression Node)>();
        Expression current = expression;
        while (true)
        {
            switch (current)
            {
                case CallExpression { IsFailable: false, TypeArguments: null } call:
                    segments.Add(item: (false, call));
                    current = call.Callee;
                    continue;
                case MemberExpression { IsFailable: false } member:
                    segments.Add(item: (true, member));
                    current = member.Object;
                    continue;
                case IndexExpression index:
                    segments.Add(item: (false, index));
                    current = index.Object;
                    continue;
                case SpliceMemberExpression spliceMember:
                    segments.Add(item: (true, spliceMember));
                    current = spliceMember.Object;
                    continue;
                case GenericMemberExpression generic when !IsFreeGeneric(receiver: generic.Object,
                    member: generic.MemberName):
                    segments.Add(item: (true, generic));
                    current = generic.Object;
                    continue;
                case GenericMemberRoutineCallExpression { IsMemoryOperation: false } genericCall
                    when !IsFreeGeneric(receiver: genericCall.Object, member: genericCall.MemberRoutineName):
                    segments.Add(item: (true, genericCall));
                    current = genericCall.Object;
                    continue;
                case UnaryExpression
                {
                    Operator: UnaryOperator.ForceUnwrap,
                    Operand: not RecoveryExpression and not UnaryExpression { Operator: UnaryOperator.ForceUnwrap }
                } unwrap:
                    segments.Add(item: (false, unwrap));
                    current = unwrap.Operand;
                    continue;
            }

            break;
        }

        segments.Reverse();
        int calls = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            if (IsCallingDot(segments: segments, i: i))
            {
                calls++;
            }
        }

        if (calls < 2)
        {
            return null;
        }

        var head = new List<Doc> { Expr(expression: current, min: PPostfix, follow: PPostfix, comma: false) };
        var tail = new List<Doc>();
        bool inTail = false;
        for (int i = 0; i < segments.Count; i++)
        {
            inTail = inTail || IsCallingDot(segments: segments, i: i);
            Doc segment = SegmentDoc(node: segments[index: i].Node);
            if (!inTail)
            {
                head.Add(item: segment);
                continue;
            }

            if (segments[index: i].Dot)
            {
                tail.Add(item: Doc.SoftLine);
            }

            tail.Add(item: segment);
        }

        // Three calls or more are always one call per line, fitting or not.
        Doc chain = Doc.Concat(Doc.Concat(parts: head), Doc.Nest(indent: IndentWidth, content: Doc.Concat(parts: tail)));
        return calls >= 3
            ? Doc.BrokenGroup(content: chain)
            : Doc.Group(content: chain);
    }

    /// <summary>Whether segment <paramref name="i"/> is a member that is called (<c>.name(...)</c>).</summary>
    private static bool IsCallingDot(List<(bool Dot, Expression Node)> segments, int i)
    {
        return segments[index: i].Node is GenericMemberRoutineCallExpression ||
               segments[index: i].Dot && i + 1 < segments.Count && segments[index: i + 1].Node is CallExpression;
    }

    private static bool IsFreeGeneric(Expression receiver, string member)
    {
        return receiver is IdentifierExpression identifier && identifier.Name == member;
    }

    /// <summary>One link of a call chain, without its receiver.</summary>
    private Doc SegmentDoc(Expression node)
    {
        return node switch
        {
            CallExpression call => ArgumentList(arguments: call.Arguments),
            MemberExpression member => Doc.Text(text: "." + member.MemberName),
            IndexExpression index => InBrackets(open: "[", close: "]",
                content: () => Expr(expression: index.Index, min: 0, follow: 0, comma: false)),
            SpliceMemberExpression spliceMember => Doc.Text(text: "." + SpliceText(inner: spliceMember.Selector.Inner)),
            GenericMemberExpression generic => Doc.Concat(Doc.Text(text: "." + generic.MemberName),
                TypeArgumentsDoc(arguments: generic.TypeArguments)),
            GenericMemberRoutineCallExpression genericCall => Doc.Concat(
                Doc.Text(text: "." + genericCall.MemberRoutineName),
                TypeArgumentsDoc(arguments: genericCall.TypeArguments),
                ArgumentList(arguments: genericCall.Arguments)),
            UnaryExpression => Doc.Text(text: "!!"),
            _ => throw Unsupported(node: node, at: node.Location)
        };
    }

    /// <summary>A bracketed part: its content is printed with the bracket depth raised.</summary>
    private Doc InBrackets(string open, string close, Func<Doc> content)
    {
        _bracketDepth++;
        bool savedCondition = _whenCondition;
        _whenCondition = false;
        Doc inner = content();
        _whenCondition = savedCondition;
        _bracketDepth--;
        return Doc.Concat(Doc.Text(text: open), inner, Doc.Text(text: close));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // OPERATORS
    // ═══════════════════════════════════════════════════════════════════════════

    private Doc BinaryDoc(BinaryExpression binary, int follow)
    {
        if (binary.Operator == BinaryOperator.Assign)
        {
            return AssignmentDoc(binary: binary, follow: follow);
        }

        if (binary.Operator is BinaryOperator.In or BinaryOperator.NotIn or BinaryOperator.Is
            or BinaryOperator.IsNot)
        {
            throw Refuse(at: binary.Location, reason: $"a binary '{binary.Operator}' the parser does not produce");
        }

        int level = BinaryPrecedence(binary: binary);
        string op = binary.Operator.ToStringRepresentation();

        if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            return LogicalChainDoc(binary: binary, follow: follow);
        }

        if (binary.Operator is BinaryOperator.Have or BinaryOperator.Lack && binary.Left is RangeExpression range)
        {
            // `0 to 10 have 10`: the range parser applies the test to the whole range.
            return Doc.Concat(RangeDoc(range: range, follow: PIs),
                Doc.Text(text: " " + op + " "),
                Expr(expression: binary.Right, min: PBitOr, follow: follow, comma: false));
        }

        (int leftMin, int rightMin) = level switch
        {
            PPower => (PUnary, PPower),
            PComparison => (PIs, PIs),
            PIs => (PIs, PBitOr),
            _ => (level, level + 1)
        };
        return Doc.Concat(Operand(expression: binary.Left, min: leftMin, follow: level, parentLevel: level),
            Doc.Text(text: " " + op + " "),
            Operand(expression: binary.Right, min: rightMin, follow: follow, parentLevel: level));
    }

    /// <summary>
    /// An operand of a binary operator at <paramref name="parentLevel"/>. Parentheses the grammar does not need
    /// are kept where they say how an expression groups at a glance: a bitwise or shift expression compared
    /// (<c>(a &amp; m) == 0</c>), a shift inside a bitwise one (<c>a &amp; (1 &lt;&lt; i)</c>), and an <c>and</c>
    /// inside an <c>or</c>.
    /// </summary>
    private Doc Operand(Expression expression, int min, int follow, int parentLevel)
    {
        int level = expression is BinaryExpression binary && binary.Operator != BinaryOperator.Assign
            ? BinaryPrecedence(binary: binary)
            : 0;
        bool clarify = parentLevel switch
        {
            PEquality or PComparison => level is PBitOr or PBitXor or PBitAnd or PShift,
            PBitOr or PBitXor or PBitAnd => level == PShift,
            POr => level == PAnd,
            _ => false
        };
        return clarify
            ? InBrackets(open: "(", close: ")",
                content: () => Expr(expression: expression, min: 0, follow: 0, comma: false))
            : Expr(expression: expression, min: min, follow: follow, comma: false);
    }

    /// <summary>
    /// A chain of <c>and</c> (or of <c>or</c>/<c>but</c>) operands. When it does not fit, each further operand
    /// starts a continuation line, one level in, with its operator first.
    /// </summary>
    private Doc LogicalChainDoc(BinaryExpression binary, int follow)
    {
        int level = BinaryPrecedence(binary: binary);
        var operands = new List<(string? Op, Expression Operand)>();
        Expression current = binary;
        while (current is BinaryExpression b && BinaryPrecedence(binary: b) == level &&
               b.Operator is BinaryOperator.And or BinaryOperator.Or or BinaryOperator.But)
        {
            operands.Insert(index: 0, item: (b.Operator.ToStringRepresentation(), b.Right));
            current = b.Left;
        }

        operands.Insert(index: 0, item: (null, current));
        var rest = new List<Doc>();
        for (int i = 1; i < operands.Count; i++)
        {
            rest.Add(item: Doc.Line);
            rest.Add(item: Doc.Text(text: operands[index: i].Op + " "));
            rest.Add(item: Operand(expression: operands[index: i].Operand,
                min: level + 1,
                follow: i == operands.Count - 1
                    ? follow
                    : level,
                parentLevel: level));
        }

        return Doc.Group(content: Doc.Concat(
            Operand(expression: operands[index: 0].Operand, min: level, follow: level, parentLevel: level),
            Doc.Nest(indent: IndentWidth, content: Doc.Concat(parts: rest))));
    }

    /// <summary>
    /// An assignment. <c>a +%= b</c> parses to <c>a = a +% b</c> with the target node itself as the left operand,
    /// which is how the compound spelling is recognized.
    /// </summary>
    private Doc AssignmentDoc(BinaryExpression binary, int follow)
    {
        if (binary.Right is BinaryExpression { Operator: var op } value && ReferenceEquals(objA: value.Left, objB: binary.Left) &&
            op is BinaryOperator.AddWrap or BinaryOperator.SubtractWrap or BinaryOperator.MultiplyWrap
                or BinaryOperator.PowerWrap or BinaryOperator.AddClamp or BinaryOperator.SubtractClamp
                or BinaryOperator.MultiplyClamp or BinaryOperator.TrueDivClamp or BinaryOperator.PowerClamp
                or BinaryOperator.NoneCoalesce)
        {
            return Doc.Concat(Expr(expression: binary.Left, min: PWith, follow: PAssign, comma: false),
                Doc.Text(text: " " + op.ToStringRepresentation() + "= "),
                Expr(expression: value.Right, min: PAssign, follow: follow, comma: false));
        }

        return Doc.Concat(Expr(expression: binary.Left, min: PWith, follow: PAssign, comma: false),
            Doc.Text(text: " = "),
            Expr(expression: binary.Right, min: PAssign, follow: follow, comma: false));
    }

    private Doc UnaryDoc(UnaryExpression unary, int follow, bool comma)
    {
        switch (unary.Operator)
        {
            case UnaryOperator.ForceUnwrap:
                // `try route()!!` means `(try route())!!`: the parser applies a trailing `!!` to the recovery. The
                // recovery still reads everything up to `??` after it, so it keeps parentheses when more follows.
                if (unary.Operand is RecoveryExpression recovery && follow < POr)
                {
                    return Doc.Concat(RecoveryDoc(recovery: recovery), Doc.Text(text: "!!"));
                }

                if (unary.Operand is UnaryExpression { Operator: UnaryOperator.ForceUnwrap, Operand: RecoveryExpression } &&
                    follow < POr)
                {
                    return Doc.Concat(UnaryDoc(unary: (UnaryExpression)unary.Operand, follow: follow, comma: false),
                        Doc.Text(text: "!!"));
                }

                return Doc.Concat(Expr(expression: unary.Operand, min: PPostfix, follow: PPostfix, comma: false),
                    Doc.Text(text: "!!"));
            case UnaryOperator.Not:
                return Doc.Concat(Doc.Text(text: "not "),
                    Expr(expression: unary.Operand, min: PUnary, follow: follow, comma: comma));
            case UnaryOperator.Minus or UnaryOperator.BitwiseNot:
                string op = unary.Operator == UnaryOperator.Minus
                    ? "-"
                    : "~";
                // `-5` is one literal: the minus of a literal itself keeps its parentheses.
                bool literalOperand = unary.Operand is LiteralExpression;
                Doc operand = Expr(expression: unary.Operand,
                    min: literalOperand && unary.Operator == UnaryOperator.Minus
                        ? PPrimary + 1
                        : PUnary,
                    follow: follow,
                    comma: comma);
                string flat = operand.Flat();
                if (flat.Length > 0 && flat[index: 0] is '-' or '%' or '^' or '!' or '=' or '>' or '~' or '*' or '+')
                {
                    operand = Doc.Concat(Doc.Text(text: "("), operand, Doc.Text(text: ")"));
                }

                return Doc.Concat(Doc.Text(text: op), operand);
            default:
                throw Unsupported(node: unary, at: unary.Location);
        }
    }

    private Doc RecoveryDoc(RecoveryExpression recovery)
    {
        string keyword = recovery.Kind switch
        {
            RecoveryKind.Try => "try ",
            RecoveryKind.Grab => "grab ",
            _ => "lookup "
        };
        // A trailing `!!` on the inner expression would be read as applying to the recovery.
        Doc inner = recovery.Inner is UnaryExpression { Operator: UnaryOperator.ForceUnwrap }
            ? InBrackets(open: "(", close: ")",
                content: () => Expr(expression: recovery.Inner, min: 0, follow: 0, comma: false))
            : Expr(expression: recovery.Inner, min: POr, follow: 0, comma: false);
        return Doc.Concat(Doc.Text(text: keyword), inner);
    }

    private Doc RangeDoc(RangeExpression range, int follow)
    {
        if (range.IsDescending)
        {
            throw Unsupported(node: range, at: range.Location);
        }

        var parts = new List<Doc>
        {
            Expr(expression: range.Start, min: PAnd, follow: PRange, comma: false),
            Doc.Text(text: range.IsExclusive
                ? " til "
                : " to "),
            Expr(expression: range.End, min: PAnd, follow: range.Step != null
                ? PRange
                : follow, comma: false)
        };
        if (range.Step != null)
        {
            parts.Add(item: Doc.Text(text: " by "));
            parts.Add(item: Expr(expression: range.Step, min: PAnd, follow: follow, comma: false));
        }

        return Doc.Concat(parts: parts);
    }

    private Doc ChainDoc(ChainedComparisonExpression chain, int follow)
    {
        var parts = new List<Doc>();
        for (int i = 0; i < chain.Operands.Count; i++)
        {
            if (i > 0)
            {
                parts.Add(item: Doc.Text(text: " " + chain.Operators[index: i - 1].ToStringRepresentation() + " "));
            }

            parts.Add(item: Operand(expression: chain.Operands[index: i],
                min: PIs,
                follow: i == chain.Operands.Count - 1
                    ? follow
                    : PComparison,
                parentLevel: PComparison));
        }

        return Doc.Concat(parts: parts);
    }

    private Doc WithDoc(WithExpression with)
    {
        var parts = new List<Doc>
        {
            Expr(expression: with.Base, min: PCoalesce, follow: PWith, comma: false),
            Doc.Text(text: " with ")
        };
        for (int i = 0; i < with.Updates.Count; i++)
        {
            (List<string>? path, Expression? index, Expression value) = with.Updates[index: i];
            if (i > 0)
            {
                parts.Add(item: Doc.Text(text: ", "));
            }

            if (path != null)
            {
                parts.Add(item: Doc.Text(text: "." + string.Join(separator: ".", values: path) + " = "));
            }
            else if (index != null)
            {
                parts.Add(item: InBrackets(open: "[", close: "] = ",
                    content: () => Expr(expression: index, min: 0, follow: 0, comma: false)));
            }
            else
            {
                throw Refuse(at: with.Location, reason: "a with update without a target");
            }

            parts.Add(item: Expr(expression: value, min: PCoalesce, follow: 0, comma: i < with.Updates.Count - 1));
        }

        return Doc.Concat(parts: parts);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CALLS AND ACCESS
    // ═══════════════════════════════════════════════════════════════════════════

    private Doc CallDoc(CallExpression call)
    {
        if (call.IsFailable || call.TypeArguments != null)
        {
            throw Unsupported(node: call, at: call.Location);
        }

        return Doc.Concat(Expr(expression: call.Callee, min: PPostfix, follow: PPostfix, comma: false),
            ArgumentList(arguments: call.Arguments));
    }

    private Doc ArgumentList(List<Expression> arguments)
    {
        _bracketDepth++;
        bool savedCondition = _whenCondition;
        _whenCondition = false;
        var items = new List<Doc>();
        foreach (Expression argument in arguments)
        {
            items.Add(item: ArgumentDoc(argument: argument));
        }

        _whenCondition = savedCondition;
        _bracketDepth--;
        return BracketList(open: "(", close: ")", items: items);
    }

    private Doc ArgumentDoc(Expression argument)
    {
        switch (argument)
        {
            case NamedArgumentExpression named:
                if (named.IsDefaultArgument)
                {
                    throw Unsupported(node: named, at: named.Location);
                }

                return Doc.Concat(Doc.Text(text: named.Name + ": "), ArgumentValue(value: named.Value));
            case DictEntryLiteralExpression entry:
                // `name: value` would read as a named argument, so an identifier key keeps parentheses.
                Doc key = entry.Key is IdentifierExpression
                    ? InBrackets(open: "(", close: ")",
                        content: () => Expr(expression: entry.Key, min: 0, follow: 0, comma: false))
                    : Expr(expression: entry.Key, min: 0, follow: 0, comma: false);
                return Doc.Concat(key, Doc.Text(text: ": "),
                    Expr(expression: entry.Value, context: ExprContext.ListItem));
            default:
                return ArgumentValue(value: argument);
        }
    }

    /// <summary>An argument's value. A <c>_</c> lambda is written as its body (the parser wraps the argument).</summary>
    private Doc ArgumentValue(Expression value)
    {
        if (value is LambdaExpression { Parameters: [{ Name: Parser.Parser.HoleParamName }] } hole)
        {
            if (hole.Captures != null)
            {
                throw Refuse(at: hole.Location, reason: "a placeholder lambda with captures");
            }

            return Expr(expression: hole.Body, context: ExprContext.ListItem);
        }

        return Expr(expression: value, context: ExprContext.ListItem);
    }

    private Doc ListDoc(string open, string close, List<Expression> elements)
    {
        _bracketDepth++;
        bool savedCondition = _whenCondition;
        _whenCondition = false;
        var items = elements.Select(selector: e => Expr(expression: e, context: ExprContext.ListItem))
                            .ToList();
        _whenCondition = savedCondition;
        _bracketDepth--;
        return BracketList(open: open, close: close, items: items);
    }

    private Doc DictDoc(DictLiteralExpression dict)
    {
        if (dict.KeyType != null || dict.ValueType != null)
        {
            throw Unsupported(node: dict, at: dict.Location);
        }

        if (dict.Pairs.Count == 0)
        {
            return Doc.Text(text: "{:}");
        }

        _bracketDepth++;
        bool savedCondition = _whenCondition;
        _whenCondition = false;
        var items = dict.Pairs.Select(selector: p => Doc.Concat(
                             Expr(expression: p.Key, min: 0, follow: 0, comma: false),
                             Doc.Text(text: ": "),
                             Expr(expression: p.Value, context: ExprContext.ListItem)))
                        .ToList();
        _whenCondition = savedCondition;
        _bracketDepth--;
        return BracketList(open: "{", close: "}", items: items);
    }

    /// <summary>
    /// The receiver and name of a generic access: <c>List[...]</c> when the parser folded a free name into both,
    /// <c>xs.member[...]</c> otherwise.
    /// </summary>
    private Doc GenericHead(Expression receiver, string member)
    {
        if (receiver is IdentifierExpression identifier && identifier.Name == member)
        {
            return Doc.Text(text: IdentifierText(identifier: identifier));
        }

        return Doc.Concat(Expr(expression: receiver, min: PPostfix, follow: PPostfix, comma: false),
            Doc.Text(text: "." + member));
    }

    private Doc TypeArgumentsDoc(List<TypeExpression> arguments)
    {
        var text = new StringBuilder(value: "[");
        for (int i = 0; i < arguments.Count; i++)
        {
            if (i > 0)
            {
                text.Append(value: ", ");
            }

            text.Append(value: TypeArgumentText(type: arguments[index: i]));
        }

        return Doc.Text(text: text.Append(value: ']')
                                  .ToString());
    }

    /// <summary>
    /// A type argument written in an expression bracket (<c>xs.get[T]()</c>): read as an expression and converted,
    /// so it is spelled the way that conversion reads back (<c>Maybe[T]</c>, never <c>T?</c>).
    /// </summary>
    private string TypeArgumentText(TypeExpression type)
    {
        if (type.SpliceHandle != null)
        {
            return Splices.TypeOf(handle: type.SpliceHandle);
        }

        if (type.BuildtimeValue != null)
        {
            return SpliceText(inner: type.BuildtimeValue, allowTypeof: false);
        }

        if (type.IsRvalue)
        {
            throw Refuse(at: type.Location, reason: "a type argument with the removed rvalue mark");
        }

        string name = Literals.LooksNumeric(name: type.Name)
            ? Literals.CanonicalNumber(text: type.Name)
            : type.Name;
        if (type.Realm != null)
        {
            name = type.Realm + "::" + name;
        }

        if (type.GenericArguments == null)
        {
            return name;
        }

        if (name.Contains(value: '/'))
        {
            throw Refuse(at: type.Location, reason: "a projected type with type arguments in an expression bracket");
        }

        return name + "[" + string.Join(separator: ", ",
            values: type.GenericArguments.Select(selector: TypeArgumentText)) + "]";
    }

    private string IdentifierText(IdentifierExpression identifier)
    {
        if (identifier.Name == Parser.Parser.HoleParamName)
        {
            return "_";
        }

        return identifier.Realm != null
            ? identifier.Realm + "::" + identifier.Name
            : identifier.Name;
    }

    /// <summary>A splice: brace-less (<c>$nameof(m)</c>) where that form holds the inner expression, braced otherwise.</summary>
    private string SpliceText(Expression inner, bool allowTypeof = true)
    {
        bool braceless = !Splices.PreferBraced && inner switch
        {
            IdentifierExpression { Realm: null } => true,
            CallExpression { Callee: IdentifierExpression { Realm: null } callee } =>
                allowTypeof || callee.Name != "typeof",
            _ => false
        };
        string text = Expr(expression: inner, context: ExprContext.Statement)
           .Flat();
        return braceless
            ? "$" + text
            : "${" + text + "}";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // LAMBDAS, PATTERNS, WHEN
    // ═══════════════════════════════════════════════════════════════════════════

    private Doc LambdaDoc(LambdaExpression lambda)
    {
        if (lambda.Parameters.Any(predicate: p => p.Name == Parser.Parser.HoleParamName))
        {
            throw Refuse(at: lambda.Location, reason: "a placeholder lambda that is not a call argument");
        }

        string parameters;
        if (lambda.Parameters.Count == 1 && lambda.Parameters[index: 0].Type == null && !_whenCondition)
        {
            parameters = lambda.Parameters[index: 0].Name;
        }
        else
        {
            var list = new List<string>();
            foreach (Parameter parameter in lambda.Parameters)
            {
                if (parameter.DefaultValue != null || parameter.IsVariadic)
                {
                    throw Refuse(at: lambda.Location, reason: "a lambda parameter with a default or variadic mark");
                }

                list.Add(item: parameter.Type == null
                    ? parameter.Name
                    : parameter.Name + ": " + TypeDoc(type: parameter.Type)
                       .Flat());
            }

            parameters = "(" + string.Join(separator: ", ", values: list) + ")";
        }

        string captures = lambda.Captures switch
        {
            null => "",
            [var single] => " given " + single,
            var many => " given (" + string.Join(separator: ", ", values: many) + ")"
        };
        return Doc.Concat(Doc.Text(text: parameters + captures + " => "),
            Expr(expression: lambda.Body, min: 0, follow: 0, comma: false));
    }

    private Doc IsPatternDoc(IsPatternExpression isPattern)
    {
        Doc subject = Expr(expression: isPattern.Expression, min: PIs, follow: PIs, comma: false);
        string op = isPattern.IsNegated
            ? " isnot "
            : " is ";
        switch (isPattern.Pattern)
        {
            case TypePattern { Bindings: null } typePattern:
                return Doc.Concat(subject, Doc.Text(text: op + IsTypeText(type: typePattern.Type) +
                                                           (typePattern.VariableName != null
                                                               ? " " + typePattern.VariableName
                                                               : "")));
            case TypeDestructuringPattern destructuring when !isPattern.IsNegated:
                return Doc.Concat(subject, Doc.Text(text: op + IsTypeText(type: destructuring.Type) + " " +
                                                           BindingsText(bindings: destructuring.Bindings)));
            default:
                throw Unsupported(node: isPattern.Pattern, at: isPattern.Location);
        }
    }

    /// <summary>The type after <c>is</c>: <c>None</c>, a type, or a dotted case name (<c>Color.RED</c>).</summary>
    private string IsTypeText(TypeExpression type)
    {
        if (type.Name.Contains(value: '.') && !type.Name.Contains(value: '/') && type.Realm == null)
        {
            if (type.GenericArguments != null)
            {
                throw Refuse(at: type.Location, reason: "a dotted case name with type arguments after 'is'");
            }

            return type.Name;
        }

        return TypeDoc(type: type)
           .Flat();
    }

    private Doc FlagsTestDoc(FlagsTestExpression flags)
    {
        string keyword = flags.Kind == FlagsTestKind.Have
            ? " have "
            : " lack ";
        return Doc.Concat(Expr(expression: flags.Subject, min: PIs, follow: PIs, comma: false),
            Doc.Text(text: keyword + FlagsText(names: flags.TestFlags, connective: flags.Connective,
                excluded: flags.ExcludedFlags, at: flags.Location)));
    }

    private string FlagsText(List<string> names, FlagsTestConnective connective, List<string>? excluded,
        SourceLocation at)
    {
        string joined = string.Join(separator: connective == FlagsTestConnective.And
                ? " and "
                : " or ",
            values: names);
        if (excluded is not { Count: > 0 })
        {
            return joined;
        }

        if (connective != FlagsTestConnective.And)
        {
            throw Refuse(at: at, reason: "a flags test with 'or' and 'but'");
        }

        return joined + " but " + string.Join(separator: " and ", values: excluded);
    }

    /// <summary>
    /// A <c>when</c> expression: its head is the end of the line, and its arms follow as a block one level in.
    /// The parser reads the arms from the next lines, so nothing may follow the head on its line.
    /// </summary>
    private Doc WhenExpressionDoc(WhenExpression when, int follow, bool comma)
    {
        if (_bracketDepth > 0 || follow > 0 || comma)
        {
            throw Refuse(at: when.Location, reason: "a when expression that is not the end of its line");
        }

        int depth = _currentDepth + 1;
        bool conditionBased = when.Expression == null;
        _deferredBlocks.Add(item: () => PrintWhenArms(clauses: when.Clauses, expansion: null, depth: depth,
            conditionBased: conditionBased, isExpression: true, at: when.Location));
        return conditionBased
            ? Doc.Text(text: "when")
            : Doc.Concat(Doc.Text(text: "when "), WhenSubject(subject: when.Expression!));
    }

    /// <summary>The binding list of a destructuring: <c>(a, field: b, (c, d), _)</c>.</summary>
    private string BindingsText(List<DestructuringBinding> bindings)
    {
        return "(" + string.Join(separator: ", ", values: bindings.Select(selector: BindingText)) + ")";
    }

    private string BindingText(DestructuringBinding binding)
    {
        if (binding.NestedPattern != null)
        {
            if (binding.NestedPattern is not DestructuringPattern nested)
            {
                throw Unsupported(node: binding.NestedPattern, at: binding.Location);
            }

            return binding.MemberVariableName != null
                ? binding.MemberVariableName + ": " + BindingsText(bindings: nested.Bindings)
                : BindingsText(bindings: nested.Bindings);
        }

        if (binding.MemberVariableName == null)
        {
            return binding.BindingName ?? throw Refuse(at: binding.Location, reason: "an empty binding");
        }

        return binding.MemberVariableName == binding.BindingName
            ? binding.BindingName
            : binding.MemberVariableName + ": " + binding.BindingName;
    }

    private Doc PatternDoc(Pattern pattern, bool conditionBased)
    {
        switch (pattern)
        {
            case ElsePattern elsePattern:
                return Doc.Text(text: elsePattern.VariableName != null
                    ? "else " + elsePattern.VariableName
                    : "else");
            case ExpressionPattern expressionPattern when conditionBased:
                _whenCondition = true;
                Doc condition = Expr(expression: expressionPattern.Expression, context: ExprContext.Statement);
                _whenCondition = false;
                return condition;
            case ExpressionPattern expressionPattern:
                return Expr(expression: expressionPattern.Expression, min: PPrimary, follow: 0, comma: false);
            case WildcardPattern:
                return Doc.Text(text: "_");
            case LiteralPattern literal:
                return Doc.Text(text: LiteralPatternText(literal: literal));
            case TypePattern typePattern:
                return Doc.Text(text: "is " + TypePatternText(typePattern: typePattern));
            case NegatedTypePattern negated:
                return Doc.Concat(Doc.Text(text: "isnot "), TypeDoc(type: negated.Type));
            case FlagsPattern flags:
                return Doc.Text(text: (flags.IsNegated
                    ? "lack "
                    : "have ") + FlagsText(names: flags.FlagNames, connective: flags.Connective,
                    excluded: flags.ExcludedFlags, at: flags.Location));
            case ComparisonPattern comparison:
                return Doc.Concat(Doc.Text(text: ComparisonOperator(type: comparison.Operator) + " "),
                    ComparisonValueDoc(value: comparison.Value));
            case GuardPattern guard:
                return Doc.Concat(PatternDoc(pattern: guard.InnerPattern, conditionBased: conditionBased),
                    Doc.Text(text: " and "),
                    Expr(expression: guard.Guard, context: ExprContext.Statement));
            case SpliceTypePattern splice:
                return Doc.Text(text: "is " + Splices.TypeOf(handle: splice.HandleName) + (splice.VariableName != null
                    ? " " + splice.VariableName
                    : ""));
            default:
                throw Unsupported(node: pattern, at: pattern.Location);
        }
    }

    private string TypePatternText(TypePattern typePattern)
    {
        TypeExpression type = typePattern.Type;
        if (type.Realm != null || type.SpliceHandle != null || type.BuildtimeValue != null || type.IsRvalue)
        {
            throw Refuse(at: type.Location, reason: "a when-arm type of this shape");
        }

        var text = new StringBuilder(value: type.Name);
        if (type.GenericArguments != null)
        {
            text.Append(value: '[')
                .Append(value: string.Join(separator: ", ",
                     values: type.GenericArguments.Select(selector: a => TypeDoc(type: a)
                                                                        .Flat())))
                .Append(value: ']');
        }

        if (typePattern.Bindings != null)
        {
            text.Append(value: ' ')
                .Append(value: BindingsText(bindings: typePattern.Bindings));
        }
        else if (typePattern.VariableName != null)
        {
            text.Append(value: ' ')
                .Append(value: typePattern.VariableName);
        }

        return text.ToString();
    }

    private static string ComparisonOperator(TokenType type)
    {
        return type switch
        {
            TokenType.Equal => "==",
            TokenType.NotEqual => "!=",
            TokenType.Less => "<",
            TokenType.LessEqual => "<=",
            TokenType.Greater => ">",
            TokenType.GreaterEqual => ">=",
            _ => throw new FormatRefusedException(reason: $"a comparison pattern with '{type}'")
        };
    }

    /// <summary>
    /// The value of a comparison arm (<c>== Status.ACTIVE</c>): the parser reads a primary with member accesses and
    /// calls after it, so anything else is parenthesized.
    /// </summary>
    private Doc ComparisonValueDoc(Expression value)
    {
        bool plain = IsPrimaryChain(expression: value);
        return plain
            ? Bare(expression: value, follow: 0, comma: false)
            : InBrackets(open: "(", close: ")",
                content: () => Expr(expression: value, min: 0, follow: 0, comma: false));
    }

    private static bool IsPrimaryChain(Expression expression)
    {
        return expression switch
        {
            MemberExpression { IsFailable: false } member => IsPrimaryChain(expression: member.Object),
            CallExpression call => IsPrimaryChain(expression: call.Callee),
            LiteralExpression { Value: string text } => !text.StartsWith(value: '-'),
            LiteralExpression or IdentifierExpression or TupleLiteralExpression or ListLiteralExpression
                or SetLiteralExpression or DictLiteralExpression or InsertedTextExpression => true,
            _ => false
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TYPES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>A type in a type position (an annotation, a parameter, a return type, a constraint).</summary>
    private Doc TypeDoc(TypeExpression type)
    {
        return Doc.Text(text: TypeText(type: type));
    }

    private string TypeText(TypeExpression type)
    {
        if (type.IsRvalue)
        {
            throw Refuse(at: type.Location, reason: "a type with the removed rvalue mark");
        }

        if (type.ConformanceConditions != null)
        {
            // Printed by the obeys list that owns them.
        }

        if (type.SpliceHandle != null)
        {
            return Splices.TypeOf(handle: type.SpliceHandle);
        }

        if (type.BuildtimeValue != null)
        {
            return SpliceText(inner: type.BuildtimeValue, allowTypeof: false);
        }

        List<TypeExpression>? arguments = type.GenericArguments;

        // `T?` is `Maybe[T]` located at `T` itself.
        if (type is { Name: "Maybe", Realm: null } && arguments is [var inner] && inner.Location == type.Location)
        {
            return TypeText(type: inner) + "?";
        }

        // `(A, B)` is `Tuple[A, B]` located at its parenthesis.
        if (type is { Name: "Tuple", Realm: null } && arguments != null &&
            _index.Tokens[index: _index.TokenIndexAt(position: type.Location.Position)].Type == TokenType.LeftParen)
        {
            return arguments.Count switch
            {
                0 => "()",
                1 => "(" + TypeText(type: arguments[index: 0]) + ",)",
                _ => "(" + string.Join(separator: ", ", values: arguments.Select(selector: TypeText)) + ")"
            };
        }

        string name = Literals.LooksNumeric(name: type.Name)
            ? Literals.CanonicalNumber(text: type.Name)
            : type.Name;
        if (type.Realm != null)
        {
            name = type.Realm + "::" + name;
        }

        return arguments == null
            ? name
            : name + "[" + string.Join(separator: ", ", values: arguments.Select(selector: TypeText)) + "]";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // LITERALS
    // ═══════════════════════════════════════════════════════════════════════════

    private string LiteralText(LiteralExpression literal)
    {
        return LiteralValueText(value: literal.Value, type: literal.LiteralType, at: literal.Location);
    }

    private string LiteralPatternText(LiteralPattern literal)
    {
        return LiteralValueText(value: literal.Value, type: literal.LiteralType, at: literal.Location);
    }

    private string LiteralValueText(object? value, TokenType type, SourceLocation at)
    {
        switch (type)
        {
            case TokenType.True:
                return "true";
            case TokenType.False:
                return "false";
            case TokenType.NoneValue:
                return "none";
            case TokenType.TextLiteral:
                return Literals.QuoteText(value: (string)value!, bytes: false);
            case TokenType.BytesLiteral:
                return "b" + Literals.QuoteText(value: (string)value!, bytes: true);
            case TokenType.RawText or TokenType.BytesRawLiteral:
                string raw = (string)value!;
                if (raw.Contains(value: '"'))
                {
                    throw Refuse(at: at, reason: "a raw text literal holding a quote");
                }

                return (type == TokenType.RawText
                    ? "r\""
                    : "br\"") + raw + "\"";
            case TokenType.CharacterLiteral or TokenType.ByteLetterLiteral:
                return CharacterLiteralSource(at: at, type: type);
        }

        if (value is "inf" or "nan" or "-inf" or "-nan")
        {
            // `inf_b64` lexes to the word alone, with the width in the token type.
            string suffix = type switch
            {
                TokenType.B16Literal => "b16",
                TokenType.B32Literal => "b32",
                TokenType.B64Literal => "b64",
                TokenType.B128Literal => "b128",
                TokenType.D32Literal => "d32",
                TokenType.D64Literal => "d64",
                TokenType.D128Literal => "d128",
                _ => throw Refuse(at: at, reason: $"an '{value}' literal of type {type}")
            };
            return value + "_" + suffix;
        }

        if (value is string text)
        {
            return Literals.CanonicalNumber(text: text);
        }

        throw Refuse(at: at, reason: $"a literal of type {type} holding {value?.GetType().Name ?? "nothing"}");
    }

    /// <summary>A character literal as written: the parser keeps only a partly decoded value.</summary>
    private string CharacterLiteralSource(SourceLocation at, TokenType type)
    {
        int i = _index.TokenIndexAt(position: at.Position);
        if (_index.Tokens[index: i].Type != type)
        {
            throw Refuse(at: at, reason: "a character literal the formatter cannot find in the source");
        }

        return _index.Tokens[index: i].Text;
    }

    private Doc InsertedTextDoc(InsertedTextExpression inserted)
    {
        var parts = new List<Doc> { Doc.Text(text: inserted.IsRaw
            ? "rf\""
            : "f\"") };
        foreach (InsertedTextPart part in inserted.Parts)
        {
            switch (part)
            {
                case TextPart textPart:
                    if (inserted.IsRaw)
                    {
                        if (textPart.Text.Contains(value: '"'))
                        {
                            throw Refuse(at: textPart.Location, reason: "a raw formatted literal holding a quote");
                        }

                        parts.Add(item: Doc.Text(text: textPart.Text.Replace(oldValue: "{", newValue: "{{")
                                                                .Replace(oldValue: "}", newValue: "}}")));
                    }
                    else
                    {
                        var escaped = new StringBuilder();
                        Literals.AppendEscaped(text: escaped, value: textPart.Text, bytes: false, formatted: true);
                        parts.Add(item: Doc.Text(text: escaped.ToString()));
                    }

                    break;
                case ExpressionPart expressionPart:
                    _bracketDepth++;
                    bool savedCondition = _whenCondition;
                    _whenCondition = false;
                    string hole = Expr(expression: expressionPart.Expression, context: ExprContext.Statement)
                       .Flat();
                    _whenCondition = savedCondition;
                    _bracketDepth--;
                    if (hole.Contains(value: '\n'))
                    {
                        throw Refuse(at: expressionPart.Location, reason: "a line break inside an f-string hole");
                    }

                    parts.Add(item: Doc.Text(text: "{" + hole + (expressionPart.FormatSpec != null
                        ? ":" + expressionPart.FormatSpec
                        : "") + "}"));
                    break;
                default:
                    throw Unsupported(node: part, at: part.Location);
            }
        }

        parts.Add(item: Doc.Text(text: "\""));
        return Doc.Concat(parts: parts);
    }
}
