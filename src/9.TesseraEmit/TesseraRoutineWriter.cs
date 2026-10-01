using System.Globalization;
using System.Text;
using Builder.LlvmEmit;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.TesseraEmit;

/// <summary>
/// Writes one routine as Tessera. Every local lives in a claimed stack slot, as in the LLVM emitter's
/// alloca-per-local output (LLVM promotes the slots to registers): a read is a <c>load</c>, a write a
/// <c>store</c>. A Tessera block sees only its own parameters and the routine's, so every block after
/// <c>entry</c> takes the local slots as parameters and every jump passes them on. Each value a statement
/// computes is bound to a temporary, in evaluation order.
/// </summary>
internal sealed class TesseraRoutineWriter
{
    private readonly TesseraWriter _module;
    private readonly RoutineInfo _routine;
    private readonly Statement _body;

    /// <summary>The slot claimed for each local declaration, made before the body is written.</summary>
    private readonly Dictionary<VariableDeclaration, Local> _declarationSlots = new();

    /// <summary>The local slots in claim order: the parameters every later block takes.</summary>
    private readonly List<Local> _slots = [];

    private readonly List<Dictionary<string, Local>> _scopes = [];
    private readonly List<Block> _blocks = [];
    private readonly Stack<(string Continue, string Break)> _loops = new();
    private Block _current = null!;
    private int _temps;
    private int _labels;

    /// <summary>A local: the place (pointer) that holds it, and its type.</summary>
    private sealed record Local(string Place, TypeSymbol Type);

    /// <summary>A computed operand: a value, or a place to read it from.</summary>
    private sealed record Operand(string Text, TypeSymbol? Type, bool IsPlace);

    private sealed class Block(string header)
    {
        public string Header { get; } = header;
        public List<string> Lines { get; } = [];
        public bool Terminated { get; set; }
    }

    public TesseraRoutineWriter(TesseraWriter module, RoutineInfo routine, Statement body)
    {
        _module = module;
        _routine = routine;
        _body = body;
    }

    private bool HasMe => _routine.OwnerType != null && !_routine.IsCreator && !_routine.IsCommon &&
                          !TesseraWriter.IsVoid(type: _routine.OwnerType);

    private bool MeByReference => LlvmEmitter.IsByRefMeRecord(ownerType: _routine.OwnerType);

    /// <summary>Writes the routine.</summary>
    public string Write()
    {
        if (_routine.FailableVariant != FailableVariant.None)
        {
            throw Unsupported(what: $"the {_routine.FailableVariant} recovery variant of {_routine.Name}");
        }

        var parameters = new List<string>();
        var scope = new Dictionary<string, Local>(comparer: StringComparer.Ordinal);
        _scopes.Add(item: scope);
        var entry = new Block(header: "entry()");
        _blocks.Add(item: entry);
        _current = entry;

        if (HasMe)
        {
            TypeSymbol owner = _routine.OwnerType!;
            if (MeByReference)
            {
                parameters.Add(item: $"%me: @{TypeText(type: owner)}");
                scope[key: "me"] = new Local(Place: "%me", Type: owner);
            }
            else
            {
                parameters.Add(item: $"%arg_me: {TypeText(type: owner)}");
                scope[key: "me"] = ClaimLocal(name: "me", type: owner, initial: "%arg_me");
            }
        }

        foreach (ParamInfo param in _routine.Parameters)
        {
            if (param.IsByReference)
            {
                parameters.Add(item: $"%arg_{param.Name}: @{TypeText(type: param.Type)}");
                scope[key: param.Name] = new Local(Place: $"%arg_{param.Name}", Type: param.Type);
            }
            else
            {
                parameters.Add(item: $"%arg_{param.Name}: {TypeText(type: param.Type)}");
                scope[key: param.Name] = ClaimLocal(name: param.Name, type: param.Type, initial: $"%arg_{param.Name}");
            }
        }

        ClaimDeclaredLocals();

        WriteStatement(statement: _body);
        if (!_current.Terminated)
        {
            Terminate(line: IsVoidReturn ? "return()" : "unreachable");
        }

        var text = new StringBuilder();
        text.Append(value: $"routine {_module.RoutineName(routine: _routine)}({string.Join(separator: ", ", values: parameters)})" +
                           $" -> {ReturnTypeText}\n");
        foreach (Block block in _blocks)
        {
            text.Append(value: $"    block {block.Header}:\n");
            foreach (string line in block.Lines)
            {
                text.Append(value: $"        {line}\n");
            }

            text.Append(value: '\n');
        }

        return text.ToString();
    }

    private bool IsVoidReturn => TesseraWriter.IsVoid(type: _routine.ReturnType);

    private string ReturnTypeText => IsVoidReturn
        ? "Void"
        : TypeText(type: _routine.ReturnType);

    private string TypeText(TypeSymbol? type)
    {
        return _module.TypeText(type: type);
    }

    // ── Slots and blocks ──────────────────────────────────────────────────────

    /// <summary>Claims a slot in the entry block, stores <paramref name="initial"/> into it if given.</summary>
    private Local ClaimLocal(string name, TypeSymbol type, string? initial)
    {
        var local = new Local(Place: $"%{name}_{_slots.Count}", Type: type);
        _slots.Add(item: local);
        _blocks[index: 0]
           .Lines.Insert(index: _slots.Count - 1, item: $"claim {local.Place} : @{TypeText(type: type)}");
        if (initial != null)
        {
            Emit(line: $"{local.Place}.store({initial})");
        }

        return local;
    }

    /// <summary>Claims a slot for every local the body declares, so later blocks can take them all.</summary>
    private void ClaimDeclaredLocals()
    {
        AstWalker.Walk(root: _body,
            visit: node =>
            {
                if (node is DeclarationStatement { Declaration: VariableDeclaration v })
                {
                    TypeSymbol type = v.Type?.ResolvedType ?? v.Initializer?.ResolvedType ??
                                      throw Unsupported(what: $"the untyped local '{v.Name}'");
                    _declarationSlots[key: v] = ClaimLocal(name: v.Name, type: type, initial: null);
                }
            });
    }

    private string SlotParameters => string.Join(separator: ", ",
        values: _slots.Select(selector: s => $"{s.Place}: @{TypeText(type: s.Type)}"));

    private string SlotArguments => string.Join(separator: ", ", values: _slots.Select(selector: s => s.Place));

    private string NewLabel(string kind)
    {
        return $"{kind}_{_labels++}";
    }

    /// <summary>A jump target: the block name with the slot arguments.</summary>
    private string Target(string label)
    {
        return $"{label}({SlotArguments})";
    }

    /// <summary>Starts writing into a new block named <paramref name="label"/>.</summary>
    private void StartBlock(string label)
    {
        var block = new Block(header: $"{label}({SlotParameters})");
        _blocks.Add(item: block);
        _current = block;
    }

    private void Emit(string line)
    {
        _current.Lines.Add(item: line);
    }

    private void Terminate(string line)
    {
        Emit(line: line);
        _current.Terminated = true;
    }

    private string Temp(TypeSymbol? type, string expression)
    {
        string name = $"%t{_temps++}";
        Emit(line: $"{name} : {TypeText(type: type)} = {expression}");
        return name;
    }

    // ── Statements ────────────────────────────────────────────────────────────

    private void WriteStatement(Statement statement)
    {
        if (_current.Terminated)
        {
            return; // unreachable code after a return / break / continue
        }

        switch (statement)
        {
            case BlockStatement block:
                _scopes.Add(item: new Dictionary<string, Local>(comparer: StringComparer.Ordinal));
                foreach (Statement s in block.Statements)
                {
                    WriteStatement(statement: s);
                }

                _scopes.RemoveAt(index: _scopes.Count - 1);
                break;
            case DeclarationStatement { Declaration: VariableDeclaration v }:
                WriteDeclaration(declaration: v);
                break;
            case ExpressionStatement e:
                WriteEffect(expression: e.Expression);
                break;
            case DiscardStatement d:
                WriteEffect(expression: d.Expression);
                break;
            case AssignmentStatement a:
                WriteAssignment(assignment: a);
                break;
            case ReturnStatement r:
                WriteReturn(statement: r);
                break;
            case IfStatement i:
                WriteIf(statement: i);
                break;
            case WhileStatement { ElseBranch: null } w:
                WriteWhile(condition: w.Condition, body: w.Body);
                break;
            case LoopStatement l:
                WriteWhile(condition: null, body: l.Body);
                break;
            case BreakStatement:
                Terminate(line: $"jump {Target(label: CurrentLoop.Break)}");
                break;
            case ContinueStatement:
                Terminate(line: $"jump {Target(label: CurrentLoop.Continue)}");
                break;
            default:
                throw Unsupported(what: $"the statement {statement.GetType().Name}");
        }
    }

    private (string Continue, string Break) CurrentLoop => _loops.Count > 0
        ? _loops.Peek()
        : throw Unsupported(what: "a break or continue outside a loop");

    private void WriteDeclaration(VariableDeclaration declaration)
    {
        Local local = _declarationSlots[key: declaration];
        _scopes[^1][key: declaration.Name] = local;
        if (declaration.Initializer is { } initializer)
        {
            string value = Value(operand: Evaluate(expression: initializer));
            Emit(line: $"{local.Place}.store({value})");
        }
    }

    /// <summary>Evaluates an expression for its effect: a call is written as a statement.</summary>
    private void WriteEffect(Expression expression)
    {
        if (expression is CallExpression call)
        {
            WriteCall(call: call, asStatement: true);
            return;
        }

        _ = Evaluate(expression: expression);
    }

    private void WriteAssignment(AssignmentStatement assignment)
    {
        string value = Value(operand: Evaluate(expression: assignment.Value));
        Operand target = Evaluate(expression: assignment.Target);
        if (!target.IsPlace)
        {
            throw Unsupported(what: $"an assignment to {assignment.Target.GetType().Name}");
        }

        Emit(line: $"{target.Text}.store({value})");
    }

    private void WriteReturn(ReturnStatement statement)
    {
        if (statement.Value is null || TesseraWriter.IsVoid(type: statement.Value.ResolvedType) ||
            statement.Value is IdentifierExpression { Name: "None" })
        {
            if (statement.Value is CallExpression call)
            {
                WriteCall(call: call, asStatement: true);
            }

            Terminate(line: "return()");
            return;
        }

        string value = Value(operand: Evaluate(expression: statement.Value));
        Terminate(line: IsVoidReturn
            ? "return()"
            : $"return({value})");
    }

    private void WriteIf(IfStatement statement)
    {
        string condition = Value(operand: Evaluate(expression: statement.Condition));
        string thenLabel = NewLabel(kind: "then");
        string elseLabel = NewLabel(kind: "else");
        string endLabel = NewLabel(kind: "after_if");
        bool reachesEnd = false;

        Terminate(line: $"branch {condition} ? {Target(label: thenLabel)} : {Target(label: statement.ElseStatement is null ? endLabel : elseLabel)}");

        StartBlock(label: thenLabel);
        WriteStatement(statement: statement.ThenStatement);
        if (!_current.Terminated)
        {
            Terminate(line: $"jump {Target(label: endLabel)}");
            reachesEnd = true;
        }

        if (statement.ElseStatement is { } elseStatement)
        {
            StartBlock(label: elseLabel);
            WriteStatement(statement: elseStatement);
            if (!_current.Terminated)
            {
                Terminate(line: $"jump {Target(label: endLabel)}");
                reachesEnd = true;
            }
        }
        else
        {
            reachesEnd = true;
        }

        StartBlock(label: endLabel);
        if (!reachesEnd)
        {
            Terminate(line: "unreachable");
        }
    }

    private void WriteWhile(Expression? condition, Statement body)
    {
        string headLabel = NewLabel(kind: "loop");
        string bodyLabel = NewLabel(kind: "loop_body");
        string endLabel = NewLabel(kind: "after_loop");

        Terminate(line: $"jump {Target(label: headLabel)}");
        StartBlock(label: headLabel);
        if (condition is null)
        {
            Terminate(line: $"jump {Target(label: bodyLabel)}");
        }
        else
        {
            string test = Value(operand: Evaluate(expression: condition));
            Terminate(line: $"branch {test} ? {Target(label: bodyLabel)} : {Target(label: endLabel)}");
        }

        StartBlock(label: bodyLabel);
        _loops.Push(item: (headLabel, endLabel));
        WriteStatement(statement: body);
        _loops.Pop();
        if (!_current.Terminated)
        {
            Terminate(line: $"jump {Target(label: headLabel)}");
        }

        StartBlock(label: endLabel);
    }

    // ── Expressions ───────────────────────────────────────────────────────────

    /// <summary>The operand as a value: a place is loaded into a temporary.</summary>
    private string Value(Operand operand)
    {
        return operand.IsPlace
            ? Temp(type: operand.Type, expression: $"{operand.Text}.load()")
            : operand.Text;
    }

    /// <summary>The operand as a place: a value is spilled into a fresh slot.</summary>
    private string Place(Operand operand)
    {
        if (operand.IsPlace)
        {
            return operand.Text;
        }

        string slot = $"%spill{_temps++}";
        Emit(line: $"claim {slot} : @{TypeText(type: operand.Type)}");
        Emit(line: $"{slot}.store({operand.Text})");
        return slot;
    }

    /// <summary>A value usable as a method receiver: a bare literal is bound to a typed temporary first.</summary>
    private string Receiver(Operand operand)
    {
        string value = Value(operand: operand);
        return value.StartsWith(value: '%')
            ? value
            : Temp(type: operand.Type, expression: value);
    }

    private Operand Evaluate(Expression expression)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                return new Operand(Text: LiteralText(literal: literal), Type: literal.ResolvedType, IsPlace: false);
            case IdentifierExpression identifier:
                return new Operand(Text: Lookup(name: identifier.Name).Place, Type: Lookup(name: identifier.Name).Type,
                    IsPlace: true);
            case MemberExpression member:
                return EvaluateField(member: member);
            case CallExpression call:
                return WriteCall(call: call, asStatement: false) ??
                       throw Unsupported(what: $"a call to a routine with no value used as a value ({DescribeCall(call: call)})");
            case NamedArgumentExpression named:
                return Evaluate(expression: named.Value);
            case CreatorExpression creator:
                return EvaluateCreator(creator: creator);
            case BackendCastExpression cast:
            {
                Operand inner = Evaluate(expression: cast.Value);
                string from = TypeText(type: inner.Type);
                string to = TypeText(type: cast.ResolvedType);
                return from == to
                    ? inner with { Type = cast.ResolvedType }
                    : throw Unsupported(what: $"a representation cast from {from} to {to}");
            }
            default:
                throw Unsupported(what: $"the expression {expression.GetType().Name}");
        }
    }

    private Local Lookup(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[index: i]
               .TryGetValue(key: name, value: out Local? local))
            {
                return local;
            }
        }

        throw Unsupported(what: $"the name '{name}', which is not a local of {_routine.Name}");
    }

    private Operand EvaluateField(MemberExpression member)
    {
        Operand owner = Evaluate(expression: member.Object);
        if (owner.Type is not RecordTypeSymbol { BackendType: null })
        {
            throw Unsupported(what: $"the member '{member.MemberName}' of {owner.Type?.FullName ?? "an untyped value"}");
        }

        if (owner.IsPlace)
        {
            return new Operand(Text: $"{owner.Text}.{member.MemberName}", Type: member.ResolvedType, IsPlace: true);
        }

        string record = Receiver(operand: owner);
        return new Operand(Text: Temp(type: member.ResolvedType, expression: $"{record}.{member.MemberName}"),
            Type: member.ResolvedType, IsPlace: false);
    }

    private static string LiteralText(LiteralExpression literal)
    {
        return literal.Value switch
        {
            // The parser keeps an integer literal's text (`48_s32`, `0xFF_u8`): the digits without the suffix.
            string text when literal.LiteralType == TokenType.IntegerLiteral =>
                LlvmEmitter.StripNumericSuffix(text: text.TrimEnd(trimChar: 'n')
                                                         .TrimEnd(trimChar: '_')),
            string text when LlvmEmitter.IsIntegerLiteralType(type: literal.LiteralType) =>
                LlvmEmitter.StripNumericSuffix(text: text),
            bool b => b
                ? "true"
                : "false",
            IFormattable number when literal.Value is not (double or float) =>
                number.ToString(format: null, formatProvider: CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException(
                message: $"The Tessera backend does not translate the literal {literal.Value} ({literal.LiteralType}) yet.")
        };
    }

    // ── Calls ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a call. As a statement a call to a routine is written bare; otherwise its value is bound to a
    /// temporary and returned (null for a routine without a value).
    /// </summary>
    private Operand? WriteCall(CallExpression call, bool asStatement)
    {
        RoutineInfo? routine = call.ResolvedRoutine;
        if (routine is null && call.LoweringKind == CallLoweringKind.TypeConstructor &&
            call.ResolvedType is RecordTypeSymbol { BackendType: null } constructed)
        {
            return new Operand(Text: Temp(type: constructed, expression: RecordLiteral(call: call, record: constructed)),
                Type: constructed, IsPlace: false);
        }

        if (routine is null)
        {
            throw Unsupported(what: $"the unresolved call {DescribeCall(call: call)}");
        }

        if (routine.LlvmIrTemplate != null)
        {
            string operation = TesseraIntrinsics.Translate(routine: routine,
                arguments: OrderedArguments(call: call, routine: routine)
                          .Select(selector: a => a is null
                               ? throw Unsupported(what: $"a missing argument to {routine.Name}")
                               : Evaluate(expression: a))
                          .Select(selector: o => (Receiver(operand: o), o.Type))
                          .ToList(),
                resultType: call.ResolvedType);
            return new Operand(Text: Temp(type: call.ResolvedType, expression: operation), Type: call.ResolvedType,
                IsPlace: false);
        }

        var arguments = new List<string>();
        bool hasReceiver = routine.OwnerType != null && !routine.IsCreator && !routine.IsCommon &&
                           !TesseraWriter.IsVoid(type: routine.OwnerType);
        if (hasReceiver)
        {
            if (call.Callee is not MemberExpression { Object: var receiverExpression })
            {
                throw Unsupported(what: $"a member call without a receiver ({DescribeCall(call: call)})");
            }

            Operand receiver = Evaluate(expression: receiverExpression);
            arguments.Add(item: LlvmEmitter.IsByRefMeRecord(ownerType: routine.OwnerType)
                ? Place(operand: receiver)
                : Value(operand: receiver));
        }

        List<Expression?> ordered = OrderedArguments(call: call, routine: routine);
        for (int i = 0; i < routine.Parameters.Count; i++)
        {
            Expression argument = ordered[index: i] ??
                                  throw Unsupported(what: $"a default argument for '{routine.Parameters[index: i].Name}' of {routine.Name}");
            Operand operand = Evaluate(expression: argument);
            arguments.Add(item: routine.Parameters[index: i].IsByReference
                ? Place(operand: operand)
                : Value(operand: operand));
        }

        string text = $"{_module.RoutineName(routine: routine)}({string.Join(separator: ", ", values: arguments)})";
        if (TesseraWriter.IsVoid(type: routine.ReturnType))
        {
            Emit(line: text);
            return null;
        }

        if (asStatement)
        {
            Emit(line: text);
            return null;
        }

        return new Operand(Text: Temp(type: routine.ReturnType, expression: text), Type: routine.ReturnType,
            IsPlace: false);
    }

    /// <summary>The call's arguments in the routine's parameter order: a named argument goes to the parameter of
    /// its name, a positional one to the next. A parameter left without one is null.</summary>
    private List<Expression?> OrderedArguments(CallExpression call, RoutineInfo routine)
    {
        var ordered = new Expression?[routine.Parameters.Count];
        int next = 0;
        foreach (Expression argument in call.Arguments)
        {
            int index = argument is NamedArgumentExpression named
                ? routine.Parameters.FindIndex(match: p => p.Name == named.Name)
                : next;
            if (index < 0 || index >= ordered.Length)
            {
                throw Unsupported(what: $"an argument that matches no parameter of {routine.Name}");
            }

            ordered[index] = argument;
            next = index + 1;
        }

        return [.. ordered];
    }

    /// <summary>A struct record built from named field values: a Tessera record literal.</summary>
    private string RecordLiteral(CallExpression call, RecordTypeSymbol record)
    {
        var fields = new List<string>();
        foreach (Expression argument in call.Arguments)
        {
            if (argument is not NamedArgumentExpression named ||
                record.MemberVariables.All(predicate: f => f.Name != named.Name))
            {
                throw Unsupported(what: $"a construction of {record.FullName} not written as field: value pairs");
            }

            fields.Add(item: $"{named.Name}: {Value(operand: Evaluate(expression: named.Value))}");
        }

        return $"{TypeText(type: record)} {{ {string.Join(separator: ", ", values: fields)} }}";
    }

    /// <summary>A record built from its field values, given in field order: a Tessera record literal.</summary>
    private Operand EvaluateCreator(CreatorExpression creator)
    {
        TypeSymbol? type = creator.ConstructedType is not (null or ErrorTypeSymbol)
            ? creator.ConstructedType
            : creator.ResolvedType;
        if (type is not RecordTypeSymbol { BackendType: null, CarrierKind: CarrierKind.None } record ||
            type is VariantTypeSymbol)
        {
            throw Unsupported(what: $"a construction of {type?.FullName ?? creator.TypeName}");
        }

        if (creator.MemberVariables.Count != record.MemberVariables.Count)
        {
            throw Unsupported(what: $"a construction of {record.FullName} that leaves fields unset");
        }

        var fields = new List<string>();
        for (int i = 0; i < record.MemberVariables.Count; i++)
        {
            fields.Add(item: $"{record.MemberVariables[index: i].Name}: " +
                             $"{Value(operand: Evaluate(expression: creator.MemberVariables[index: i].Value))}");
        }

        string literal = $"{TypeText(type: record)} {{ {string.Join(separator: ", ", values: fields)} }}";
        return new Operand(Text: Temp(type: record, expression: literal), Type: record, IsPlace: false);
    }

    private static string DescribeCall(CallExpression call)
    {
        return call.Callee switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression m => "." + m.MemberName,
            _ => call.Callee.GetType().Name
        };
    }

    private NotSupportedException Unsupported(string what)
    {
        return new NotSupportedException(
            message: $"The Tessera backend does not translate {what} yet (in {_routine.OwnerType?.FullName ?? _routine.Module}.{_routine.Name}).");
    }
}
