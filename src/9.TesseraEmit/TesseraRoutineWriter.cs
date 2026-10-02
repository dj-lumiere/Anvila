using System.Globalization;
using System.Text;
using Builder.LlvmEmit;
using Builder.Lowering.Passes;
using Builder.Tokenizer;
using Builder.Verification;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.TesseraEmit;

/// <summary>
/// Writes one routine as Tessera. Every local lives in a claimed stack slot, as in the LLVM emitter's
/// alloca-per-local output (LLVM promotes the slots to registers): a read is a <c>load</c>, a write a
/// <c>store</c>. A slot is claimed where its local is declared, with the value it starts with
/// (<c>claim %x : @T &lt;- value</c>). A Tessera block sees only its own parameters and the routine's, so a
/// block takes the slots visible where the statement that makes it stands, and every jump to it passes them.
/// Each value a statement computes is bound to a temporary, in evaluation order.
/// </summary>
internal sealed class TesseraRoutineWriter
{
    private readonly TesseraWriter _module;
    private readonly RoutineInfo _routine;
    private readonly Statement _body;

    /// <summary>The slots visible here, in claim order: the parameters a block made here takes.</summary>
    private List<Local> _visible = [];

    /// <summary>The slots each block takes, fixed when its label is made.</summary>
    private readonly Dictionary<string, List<Local>> _labelSlots = new(comparer: StringComparer.Ordinal);

    private int _slotCount;

    private readonly List<Dictionary<string, Local>> _scopes = [];
    private readonly List<Block> _blocks = [];
    private readonly Stack<(string Continue, string Break)> _loops = new();
    private Block _current = null!;

    /// <summary>Whether this routine keeps a crash trace frame.</summary>
    private bool _traced;
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

    private bool HasMe => _routine is { OwnerType: not null, IsCreator: false, IsCommon: false } &&
                          !TesseraWriter.IsVoid(type: _routine.OwnerType);

    private bool MeByReference => Declaration.ReceiverFacts.MeByReference(ownerType: _routine.OwnerType);

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

        _traced = _module.TracesRoutine(routine: _routine);
        if (_traced)
        {
            Emit(line: $"{TesseraTrace.Push}({TesseraTrace.CString(text: Collection.TraceFrames.Name(routine: _routine))}, " +
                       $"{TesseraTrace.CString(text: _routine.Location?.FileName ?? "")}, " +
                       $"{_routine.Location?.Line ?? 0}, {_routine.Location?.Column ?? 0})");
        }

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

    /// <summary>
    /// Claims a slot for a local where it is declared, starting with <paramref name="initial"/>, or
    /// <c>uninit</c> for a local declared without a value (a <c>lateinit</c>, or one the builder fills on every
    /// path before reading it).
    /// </summary>
    private Local ClaimLocal(string name, TypeSymbol type, string? initial)
    {
        var local = new Local(Place: $"%{name}_{_slotCount++}", Type: type);
        Emit(line: $"claim {local.Place} : @{TypeText(type: type)} <- {initial ?? "uninit"}");
        _visible.Add(item: local);
        return local;
    }

    /// <summary>A label for a block made at this point: it takes the slots visible here.</summary>
    private string NewLabel(string kind)
    {
        string label = $"{kind}_{_labels++}";
        _labelSlots[key: label] = [.. _visible];
        return label;
    }

    /// <summary>A jump target: the block name with the slots it takes.</summary>
    private string Target(string label)
    {
        return $"{label}({string.Join(separator: ", ", values: _labelSlots[key: label].Select(selector: s => s.Place))})";
    }

    /// <summary>Starts writing into a new block named <paramref name="label"/>, which sees the slots it takes.</summary>
    private void StartBlock(string label)
    {
        List<Local> slots = _labelSlots[key: label];
        var block = new Block(header: $"{label}({string.Join(separator: ", ", values: slots.Select(selector: s => $"{s.Place}: @{TypeText(type: s.Type)}"))})");
        _blocks.Add(item: block);
        _current = block;
        _visible = [.. slots];
    }

    private void Emit(string line)
    {
        _current.Lines.Add(item: line);
    }

    private void Terminate(string line)
    {
        // Every return pops the frame the routine pushed. (The value is already computed: it is a temporary.)
        if (_traced && line.StartsWith(value: "return(", comparisonType: StringComparison.Ordinal))
        {
            Emit(line: $"{TesseraTrace.Pop}()");
        }

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
            {
                // A scope's locals stop being visible (and stop being passed to blocks) where the scope ends.
                _scopes.Add(item: new Dictionary<string, Local>(comparer: StringComparer.Ordinal));
                int visibleBefore = _visible.Count;
                foreach (Statement s in block.Statements)
                {
                    WriteStatement(statement: s);
                }

                _scopes.RemoveAt(index: _scopes.Count - 1);
                if (_visible.Count > visibleBefore)
                {
                    _visible.RemoveRange(index: visibleBefore, count: _visible.Count - visibleBefore);
                }

                break;
            }
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
                WriteAssignment(target: a.Target, value: a.Value);
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
            case DangerStatement danger:
                // `danger` only lifts the build's safety checks: its body runs like any block.
                WriteStatement(statement: danger.Body);
                break;
            case CrashStatement crash:
                // The report never returns: it prints the crash and ends the program.
                WriteCall(call: crash.Report, asStatement: true);
                Terminate(line: "unreachable");
                break;
            case BreakStatement:
                Terminate(line: $"jump {Target(label: CurrentLoop.Break)}");
                break;
            case ContinueStatement:
                Terminate(line: $"jump {Target(label: CurrentLoop.Continue)}");
                break;
            case CancellationPushStatement push:
                WriteCancellationPush(push: push);
                break;
            case CancellationPopStatement pop:
                Emit(line: $"{_module.RuntimeRoutine(symbol: Declaration.RuntimeContract.Runtime.CoroCfPop, parameters: "%node: Addr", returnType: "Void")}" +
                           $"({Lookup(name: CancellationNodeName(local: pop.Local)).Place})");
                break;
            default:
                throw Unsupported(what: $"the statement {statement.GetType().Name}");
        }
    }

    /// <summary>
    /// Links a local into the running coroutine's cancellation chain: a node slot for it, the local (its address for a
    /// value, the reference it holds for an entity) and its own <c>destroy</c>, which an abandoned coroutine runs.
    /// </summary>
    private void WriteCancellationPush(CancellationPushStatement push)
    {
        Local local = Lookup(name: push.Local);
        string value = push.PassesAddress
            ? local.Place
            : Temp(type: local.Type, expression: $"{local.Place}.load()");
        Local node = ClaimLocal(name: CancellationNodeName(local: push.Local), type: _module.CancellationNodeType,
            initial: null);
        _scopes[^1][key: CancellationNodeName(local: push.Local)] = node;
        string runtime = _module.RuntimeRoutine(symbol: Declaration.RuntimeContract.Runtime.CoroCfPush,
            parameters: "%node: Addr, %value: Addr, %destroy: Addr", returnType: "Void");
        Emit(line: $"{runtime}({node.Place}, {value}, {_module.RoutineName(routine: push.Destroy)}.addr())");
    }

    /// <summary>The slot name of a local's cancellation node.</summary>
    private static string CancellationNodeName(string local)
    {
        return $"cfnode_{local}";
    }

    private (string Continue, string Break) CurrentLoop => _loops.Count > 0
        ? _loops.Peek()
        : throw Unsupported(what: "a break or continue outside a loop");

    private void WriteDeclaration(VariableDeclaration declaration)
    {
        TypeSymbol type = declaration.LocalType ??
                          throw Unsupported(what: $"the untyped local '{declaration.Name}'");
        string? value = declaration.Initializer is { } initializer
            ? Value(operand: Evaluate(expression: initializer))
            : null;
        _scopes[^1][key: declaration.Name] = ClaimLocal(name: declaration.Name, type: type, initial: value);
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

    /// <summary>Stores a value into a local or a field. The value is computed first, then the place.</summary>
    private string WriteAssignment(Expression target, Expression value)
    {
        string computed = Value(operand: Evaluate(expression: value));
        Operand place = Evaluate(expression: target);
        if (!place.IsPlace)
        {
            throw Unsupported(what: $"an assignment to {target.GetType().Name}");
        }

        Emit(line: $"{place.Text}.store({computed})");
        return computed;
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

    /// <summary>
    /// A value moved to another representation: the conversion <c>RepresentationCastPass</c> stamped on the
    /// cast, as Tessera's raw operation of the same instruction.
    /// </summary>
    private Operand EvaluateRepresentationCast(Operand inner, BackendCastExpression cast)
    {
        TypeSymbol? target = cast.ResolvedType;
        string from = TypeText(type: inner.Type);
        string to = TypeText(type: target);
        if (cast.Conversion == RepresentationConversion.SpillToAddress)
        {
            return new Operand(Text: Place(operand: inner), Type: target, IsPlace: false);
        }

        if (cast.Conversion == RepresentationConversion.Same)
        {
            // Pointers share a representation, but a typed pointer @T is reached from another pointer by casting it.
            // An integer of one width trades its bits between signed and unsigned (Tessera keeps them apart).
            return from == to
                ? inner with { Type = target }
                : to is ['@', .. var pointee]
                    ? new Operand(Text: Temp(type: target, expression: $"{Value(operand: inner)}.cast<{pointee}>()"),
                        Type: target, IsPlace: false)
                    : new Operand(Text: Temp(type: target, expression: $"bitcast<{from}, {to}>({Value(operand: inner)})"),
                        Type: target, IsPlace: false);
        }

        string operation = cast.Conversion switch
        {
            RepresentationConversion.Truncate => "trunc",
            RepresentationConversion.ZeroExtend => "zext",
            RepresentationConversion.SignExtend => "sext",
            RepresentationConversion.Bitcast => "bitcast",
            RepresentationConversion.IntToPointer => "inttoptr",
            RepresentationConversion.PointerToInt => "ptrtoint",
            RepresentationConversion.FloatTruncate => "fptrunc",
            RepresentationConversion.FloatExtend => "fpext",
            RepresentationConversion.FloatToSigned => "fptosi",
            RepresentationConversion.FloatToUnsigned => "fptoui",
            RepresentationConversion.SignedToFloat => "sitofp",
            RepresentationConversion.UnsignedToFloat => "uitofp",
            _ => throw Unsupported(what: $"the representation conversion {cast.Conversion}")
        };
        return new Operand(Text: Temp(type: target, expression: $"{operation}<{from}, {to}>({Value(operand: inner)})"),
            Type: target, IsPlace: false);
    }

    /// <summary>The operand as a value: a place is loaded into a temporary.</summary>
    private string Value(Operand operand)
    {
        return operand.IsPlace
            ? Temp(type: operand.Type, expression: $"{operand.Text}.load()")
            : operand.Text;
    }

    /// <summary>
    /// A pointer value as the pointer type it is handed to: the same address, cast when it is untyped (an
    /// <c>Addr</c>, e.g. a pointer the builder typed through a protocol) and the parameter says what is there.
    /// </summary>
    private string Typed(string value, TypeSymbol? from, TypeSymbol? to)
    {
        return TypeText(type: from) == "Addr" && TypeText(type: to) is ['@', .. var pointee]
            ? $"{value}.cast<{pointee}>()"
            : value;
    }

    /// <summary>The operand as a place: a value is spilled into a fresh slot.</summary>
    private string Place(Operand operand)
    {
        if (operand.IsPlace)
        {
            return operand.Text;
        }

        string slot = $"%spill{_temps++}";
        Emit(line: $"claim {slot} : @{TypeText(type: operand.Type)} <- {operand.Text}");
        return slot;
    }

    /// <summary>The all-zero value of a type: a literal for a number, Bool or address, else a zeroed slot.</summary>
    private string ZeroValue(TypeSymbol? type)
    {
        string text = TypeText(type: type);
        if (text.Length > 1 && text[0] is 'S' or 'U' && char.IsAsciiDigit(c: text[1]))
        {
            return "0";
        }

        switch (text)
        {
            case "Bool":
                return "false";
            case "F16" or "BF16" or "F32" or "F64":
                return "0.0";
            case "Addr":
                return "null";
            case ['@', ..]:
                return "null";
        }

        string slot = $"%zero{_temps++}";
        Emit(line: $"claim {slot} : @{text} <- uninit");
        Emit(line: $"{slot}.zeroinit(1)");
        return $"{slot}.load()";
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
            case EntityAllocationExpression { ResolvedType: EntityTypeSymbol entity }:
            {
                string allocate = _module.RuntimeRoutine(symbol: "rf_allocate_dynamic", parameters: "%size: U64",
                    returnType: "Addr");
                string block = Temp(type: entity, expression: $"{allocate}({entity.HeapBlockSize(pointerSize: 8)})");
                Emit(line: $"{block}.cast<{_module.EntityRecord(entity: entity)}>().zeroinit(1)");
                return new Operand(Text: block, Type: entity, IsPlace: false);
            }
            case ZeroValueExpression { ResolvedType: { } zeroType }:
                return new Operand(Text: Temp(type: zeroType, expression: ZeroValue(type: zeroType)), Type: zeroType,
                    IsPlace: false);
            case ConstantDataExpression data:
                return new Operand(Text: _module.ConstantData(data: data), Type: data.ResolvedType, IsPlace: false);
            case LiteralExpression { Value: string text, LiteralType: TokenType.B16Literal or TokenType.B32Literal or
                TokenType.B64Literal } literal:
                return new Operand(Text: Temp(type: literal.ResolvedType, expression: FloatLiteral(text: text,
                        type: literal.LiteralType)),
                    Type: literal.ResolvedType, IsPlace: false);
            case LiteralExpression literal:
                return new Operand(Text: LiteralText(literal: literal), Type: literal.ResolvedType, IsPlace: false);
            case IdentifierExpression { ResolvedType: ConstGenericValueTypeSymbol constant }:
                // A const generic parameter: the monomorphizer stamped its value on the reference.
                return new Operand(Text: constant.Value.ToString(provider: CultureInfo.InvariantCulture),
                    Type: _module.ConstantType(constant: constant), IsPlace: false);
            case IdentifierExpression { ResolvedRoutine: { } named }:
                // A routine taken as a value: its code address, nothing bound.
                return RoutineValue(routine: named, bound: "null");
            case ClosureValueExpression closure:
                return RoutineValue(
                    routine: closure.Function.ResolvedRoutine ??
                             throw Unsupported(what: "a closure value without its lifted routine"),
                    bound: Value(operand: Evaluate(expression: closure.Bound)));
            case IdentifierExpression identifier:
            {
                Local local = Lookup(name: identifier.Name);
                return new Operand(Text: local.Place, Type: local.Type, IsPlace: true);
            }
            case MemberExpression member:
                return EvaluateField(member: member);
            case CallExpression call:
                return WriteCall(call: call, asStatement: false) ??
                       throw Unsupported(what: $"a call to a routine with no value used as a value ({DescribeCall(call: call)})");
            case NamedArgumentExpression named:
                return Evaluate(expression: named.Value);
            case CreatorExpression creator:
                return EvaluateCreator(creator: creator);
            case CarrierPayloadExpression payload:
                return EvaluatePayload(payload: payload);
            case TaggedCreatorExpression tagged:
                return EvaluateTaggedCreator(tagged: tagged);
            case WrapperProjectionExpression projection:
                return EvaluateProjection(projection: projection);
            case NativeRoutineExpression { Routine: IdentifierExpression { ResolvedRoutine: { } native } } address:
                // The routine's code address, as native code takes it.
                return new Operand(Text: Temp(type: address.ResolvedType, expression: $"{_module.RoutineName(routine: native)}.addr()"),
                    Type: address.ResolvedType, IsPlace: false);
            case ListLiteralExpression { ResolvedType: { } arrayType } list:
            {
                // Only a fixed-array literal reaches a backend (every other collection literal is lowered to calls).
                List<string> elements = list.Elements.Select(selector: element => Value(operand: Evaluate(expression: element)))
                                            .ToList();
                return new Operand(
                    Text: Temp(type: arrayType,
                        expression: $"{TypeText(type: arrayType)} {{ {string.Join(separator: ", ", values: elements)} }}"),
                    Type: arrayType, IsPlace: false);
            }
            case AddressOfExpression address:
            {
                Operand storage = Evaluate(expression: address.Target);
                return storage.IsPlace
                    ? new Operand(Text: storage.Text, Type: address.ResolvedType, IsPlace: false)
                    : throw Unsupported(what: "the address of a value that has no storage");
            }
            case BinaryExpression { Operator: BinaryOperator.IdentityEqual or BinaryOperator.IdentityNotEqual } identity:
                return EvaluateIdentity(identity: identity);
            case BinaryExpression { Operator: BinaryOperator.Assign } assign:
                return new Operand(Text: WriteAssignment(target: assign.Left, value: assign.Right),
                    Type: assign.Right.ResolvedType, IsPlace: false);
            case BackendCastExpression cast:
            {
                Operand inner = Evaluate(expression: cast.Value);
                if (inner.Type is null)
                {
                    // An untyped value (a bare literal) is written in the cast's own representation.
                    return inner with { Type = cast.ResolvedType };
                }

                return EvaluateRepresentationCast(inner: inner, cast: cast);
            }
            default:
                throw Unsupported(what: $"the expression {expression.GetType().Name}");
        }
    }

    /// <summary>A routine value: the routine's code address and the bound payload (an address, or null).</summary>
    private Operand RoutineValue(RoutineInfo routine, string bound)
    {
        string record = _module.RoutineValueRecord;
        string name = $"%t{_temps++}";
        Emit(line: $"{name} : {record} = {record} {{ fn: {_module.RoutineName(routine: routine)}.addr(), bound: {bound} }}");
        return new Operand(Text: name, Type: null, IsPlace: false);
    }

    /// <summary>
    /// The routine value a call goes through, when it calls one: a local holding a routine
    /// (<c>compare(a: x, b: y)</c>), or a routine-typed field (<c>me.predicate(item)</c>).
    /// </summary>
    private (Operand Value, RoutineTypeSymbol Type)? IndirectCallee(CallExpression call)
    {
        if (call.Callee is IdentifierExpression id && TryLookup(name: id.Name) is { Type: RoutineTypeSymbol localType } local)
        {
            return (new Operand(Text: local.Place, Type: localType, IsPlace: true), localType);
        }

        if (call.LoweringKind == CallLoweringKind.DynamicCall &&
            call.Callee is MemberExpression { ResolvedType: RoutineTypeSymbol fieldType } field)
        {
            return (Evaluate(expression: field), fieldType);
        }

        return null;
    }

    /// <summary>
    /// A call through a routine value. A plain routine or captureless lambda (bound null) is called with the
    /// arguments, a capturing lambda with the bound payload after them. Blocks see only their own parameters,
    /// so the address, the payload and the arguments wait in slots, and both branches leave the result in one.
    /// </summary>
    private Operand? WriteIndirectCall(CallExpression call, Operand callee, RoutineTypeSymbol type)
    {
        string fat = Receiver(operand: callee);
        Local fn = ClaimLocal(name: "fn", type: _module.PointerType, initial: $"{fat}.fn");
        Local bound = ClaimLocal(name: "bound", type: _module.PointerType, initial: $"{fat}.bound");
        var arguments = new List<Local>();
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            Operand argument = Evaluate(expression: call.Arguments[index: i]);
            TypeSymbol parameter = type.ParameterTypes[index: i];
            arguments.Add(item: ClaimLocal(name: "arg", type: parameter,
                initial: Typed(value: Value(operand: argument), from: argument.Type, to: parameter)));
        }

        bool returns = !TesseraWriter.IsVoid(type: type.ReturnType);
        Local? result = returns
            ? ClaimLocal(name: "result", type: type.ReturnType!, initial: null)
            : null;
        string address = Temp(type: _module.AddressType, expression: $"ptrtoint<Addr, U64>({bound.Place}.load())");
        string unbound = Temp(type: _module.BoolType, expression: $"{address}.eq(0)");
        string plainLabel = NewLabel(kind: "plain_call");
        string boundLabel = NewLabel(kind: "bound_call");
        string afterLabel = NewLabel(kind: "after_call");
        Terminate(line: $"branch {unbound} ? {Target(label: plainLabel)} : {Target(label: boundLabel)}");

        foreach ((string label, bool withBound) in new[] { (plainLabel, false), (boundLabel, true) })
        {
            StartBlock(label: label);
            string callable = _module.CallableText(routineType: type, withBound: withBound);
            string target = $"%callee{_temps++}";
            Emit(line: $"{target} : {callable} = {fn.Place}.cast<{callable}>().load()");
            IEnumerable<string> values = arguments.Select(selector: a => Temp(type: a.Type, expression: $"{a.Place}.load()"));
            if (withBound)
            {
                values = values.Append(element: $"{bound.Place}.load()");
            }

            string invocation = $"{target}.call({string.Join(separator: ", ", values: values.ToList())})";
            if (result is null)
            {
                Emit(line: invocation);
            }
            else
            {
                Emit(line: $"{result.Place}.store({Temp(type: result.Type, expression: invocation)})");
            }

            Terminate(line: $"jump {Target(label: afterLabel)}");
        }

        StartBlock(label: afterLabel);
        return result is null
            ? null
            : new Operand(Text: result.Place, Type: result.Type, IsPlace: true);
    }

    private Local? TryLookup(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[index: i]
               .TryGetValue(key: name, value: out Local? local))
            {
                return local;
            }
        }

        return null;
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

        // An aggregate preset (a constant table): its global is the place that holds it.
        if (_module.AggregatePreset(name: name, routine: _routine) is var (global, type))
        {
            return new Local(Place: global, Type: type);
        }

        throw Unsupported(what: $"the name '{name}', which is not a local of {_routine.Name}");
    }

    /// <summary>
    /// The entity or record a wrapper stands for (WrapperProjectionLoweringPass decides how it is read out): the
    /// controller's <c>data</c> field, the wrapper pointer itself, the struct wrapper's pointer field, or, for a
    /// record behind a pointer, that pointer as the record's place.
    /// </summary>
    private Operand EvaluateProjection(WrapperProjectionExpression projection)
    {
        Operand wrapper = Evaluate(expression: projection.Wrapper);
        TypeSymbol inner = projection.ResolvedType ??
                           throw Unsupported(what: "a wrapper projection without a type");
        switch (projection.Kind)
        {
            case WrapperProjectionKind.Direct:
                return new Operand(Text: Value(operand: wrapper), Type: inner, IsPlace: false);
            case WrapperProjectionKind.RecordAddress:
            {
                // The pointer is the record's storage: an untyped address is cast to say what is there.
                string pointer = Receiver(operand: wrapper);
                return new Operand(Text: TypeText(type: wrapper.Type).StartsWith(value: '@')
                        ? pointer
                        : $"{pointer}.cast<{TypeText(type: inner)}>()",
                    Type: inner, IsPlace: true);
            }
            case WrapperProjectionKind.ControllerData:
            {
                EntityTypeSymbol controller = projection.Controller ??
                                              throw Unsupported(what: "a controller projection without its controller");
                string block = Receiver(operand: wrapper);
                return new Operand(
                    Text: Temp(type: inner,
                        expression: $"{block}.cast<{_module.EntityRecord(entity: controller)}>()." +
                                    $"{Declaration.RuntimeContract.ControllerData}.load()"),
                    Type: inner, IsPlace: false);
            }
            default:
            {
                var record = (RecordTypeSymbol)projection.Wrapper.ResolvedType!;
                string field = record.MemberVariables[index: projection.FieldIndex].Name;
                return new Operand(Text: Temp(type: inner,
                        expression: wrapper.IsPlace
                            ? $"{wrapper.Text}.{field}.load()"
                            : $"{Receiver(operand: wrapper)}.{field}"),
                    Type: inner, IsPlace: false);
            }
        }
    }

    /// <summary>
    /// <c>a === b</c> / <c>a !== b</c>: whether two references point at the same thing, compared as addresses.
    /// </summary>
    private Operand EvaluateIdentity(BinaryExpression identity)
    {
        string Address(Expression side)
        {
            Operand operand = Evaluate(expression: side);
            return Temp(type: _module.AddressType,
                expression: $"ptrtoint<{TypeText(type: operand.Type)}, U64>({Value(operand: operand)})");
        }

        string left = Address(side: identity.Left);
        string right = Address(side: identity.Right);
        string compare = identity.Operator == BinaryOperator.IdentityEqual ? "eq" : "ne";
        return new Operand(Text: Temp(type: identity.ResolvedType, expression: $"{left}.{compare}({right})"),
            Type: identity.ResolvedType, IsPlace: false);
    }

    /// <summary>A marker borrow protocol (Accessing[X], Controlling[X]) stands for its inner X.</summary>
    private static TypeSymbol? Unmarked(TypeSymbol? type)
    {
        return type is ProtocolTypeSymbol { TypeArguments: [{ } inner] } marker &&
               Declaration.RuntimeContract.IsMarkerProtocol(baseName: (marker.GenericDefinition ?? marker).BareName)
            ? inner
            : type;
    }

    private Operand EvaluateField(MemberExpression member)
    {
        Operand owner = Evaluate(expression: member.Object);
        owner = owner with { Type = Unmarked(type: owner.Type) };
        if (owner.Type is EntityTypeSymbol entity)
        {
            string block = Receiver(operand: owner);
            return new Operand(Text: $"{block}.cast<{_module.EntityRecord(entity: entity)}>().{member.MemberName}",
                Type: member.ResolvedType, IsPlace: true);
        }

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

    /// <summary>
    /// A float literal as its exact bits (<c>F64.from_bits(0x...)</c>), so Tessera does no rounding of its own.
    /// The value is the one the LLVM emitter writes: the text read as a double, then narrowed to the literal's
    /// width; a hexadecimal literal is encoded exactly.
    /// </summary>
    private static string FloatLiteral(string text, TokenType type)
    {
        (string tessera, int mantissa, int exponent) = type switch
        {
            TokenType.B16Literal => ("F16", 10, 5),
            TokenType.B32Literal => ("F32", 23, 8),
            _ => ("F64", 52, 11)
        };
        string digits = LlvmEmitter.StripNumericSuffix(text: text);
        UInt128 bits;
        if (NumericLiteralParser.IsHexFloatText(text: text))
        {
            string cleaned = SemanticVerifier.StripHexFloatSuffix(rawValue: text)
                                             .Replace(oldValue: "_", newValue: "");
            if (NumericLiteralParser.TryEncodeHexFloat(cleaned: cleaned, mantBits: mantissa, expBits: exponent,
                    bits: out bits) != NumericLiteralParser.HexFloatStatus.Exact)
            {
                throw new NotSupportedException(message: $"The Tessera backend cannot encode the literal {text}.");
            }
        }
        else
        {
            double value = digits switch
            {
                "inf" => double.PositiveInfinity,
                "nan" => double.NaN,
                _ => double.Parse(s: digits, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture)
            };
            bits = type switch
            {
                TokenType.B16Literal => BitConverter.HalfToUInt16Bits(value: (Half)value),
                TokenType.B32Literal => BitConverter.SingleToUInt32Bits(value: (float)value),
                _ => BitConverter.DoubleToUInt64Bits(value: value)
            };
        }

        return $"{tessera}.from_bits(0x{bits:X})";
    }

    /// <summary>A scalar literal as a Tessera constant, usable in a global's initializer.</summary>
    internal static string ConstantText(LiteralExpression literal)
    {
        return literal is { Value: string text, LiteralType: TokenType.B16Literal or TokenType.B32Literal or
            TokenType.B64Literal }
            ? FloatLiteral(text: text, type: literal.LiteralType)
            : LiteralText(literal: literal);
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
        // The trace's top frame moves to this call, so a crash under it points here.
        if (_traced && call.Location is { } at && (at.Line > 0 || at.Column > 0))
        {
            Emit(line: $"{TesseraTrace.UpdateLocation}({at.Line}, {at.Column})");
        }

        if (IndirectCallee(call: call) is var (callee, routineType))
        {
            return WriteIndirectCall(call: call, callee: callee, type: routineType);
        }

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
                resultType: call.ResolvedType,
                typeText: TypeText,
                zero: ZeroValue,
                spill: (value, type) => Place(operand: new Operand(Text: value, Type: type, IsPlace: false)),
                emit: Emit);
            if (TesseraWriter.IsVoid(type: routine.ReturnType))
            {
                Emit(line: operation);
                return null;
            }

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

            // A call on a type name has the type as its receiver (CallBindingPass): the routine reads no `me`.
            Operand receiver = receiverExpression is TypeExpression { ResolvedType: { } receiverType }
                ? new Operand(Text: Temp(type: receiverType, expression: ZeroValue(type: receiverType)), Type: receiverType,
                    IsPlace: false)
                : Evaluate(expression: receiverExpression);
            arguments.Add(item: Declaration.ReceiverFacts.MeByReference(ownerType: routine.OwnerType)
                ? Place(operand: receiver)
                : Typed(value: Value(operand: receiver), from: receiver.Type, to: routine.OwnerType));
        }

        List<Expression?> ordered = OrderedArguments(call: call, routine: routine);
        for (int i = 0; i < routine.Parameters.Count; i++)
        {
            Expression argument = ordered[index: i] ??
                                  throw Unsupported(what: $"a default argument for '{routine.Parameters[index: i].Name}' of {routine.Name}");
            Operand operand = Evaluate(expression: argument);
            arguments.Add(item: routine.Parameters[index: i].IsByReference
                ? Place(operand: operand)
                : Typed(value: Value(operand: operand), from: operand.Type, to: routine.Parameters[index: i].Type));
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

    /// <summary>The call's arguments by parameter (<c>CallArgumentOrderPass</c> listed them in parameter order).
    /// A parameter left without one is null.</summary>
    private static List<Expression?> OrderedArguments(CallExpression call, RoutineInfo routine)
    {
        return Enumerable.Range(start: 0, count: routine.Parameters.Count)
                         .Select(selector: slot => CallArgumentOrderPass.ArgumentInSlot(arguments: call.Arguments,
                              routine: routine,
                              slot: slot))
                         .ToList();
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
        TypeSymbol? type = creator.ConstructedType;
        if (type is EntityTypeSymbol entity)
        {
            return EvaluateEntityCreator(creator: creator, entity: entity);
        }

        if (type is TupleTypeSymbol tuple)
        {
            var items = creator.MemberVariables
                               .Select(selector: m => Value(operand: Evaluate(expression: m.Value)))
                               .ToList();
            // A tuple literal is written like a record's, `{ a, b }`; its type comes from the binding.
            return new Operand(Text: Temp(type: tuple, expression: $"{{ {string.Join(separator: ", ", values: items)} }}"),
                Type: tuple, IsPlace: false);
        }

        // A backend-represented record (a number, an Array) built from nothing is its zero value.
        if (type is RecordTypeSymbol { BackendType: not null } && creator.MemberVariables.Count == 0)
        {
            return new Operand(Text: Temp(type: type, expression: ZeroValue(type: type)), Type: type, IsPlace: false);
        }

        if (type is not RecordTypeSymbol
            {
                BackendType: null, CarrierKind: CarrierKind.None or CarrierKind.Maybe
            } record || type is VariantTypeSymbol)
        {
            throw Unsupported(what: $"a construction of {type?.FullName ?? creator.TypeName}");
        }

        if (creator.MemberVariables.Count > record.MemberVariables.Count)
        {
            throw Unsupported(what: $"a construction of {record.FullName} with more values than fields");
        }

        // Values come in field order; a field left without one is zero, as in the LLVM emitter (an absent
        // Maybe is built from its `present` flag alone).
        var fields = new List<string>();
        for (int i = 0; i < record.MemberVariables.Count; i++)
        {
            MemberVariableInfo field = record.MemberVariables[index: i];
            string value = i < creator.MemberVariables.Count &&
                           Evaluate(expression: creator.MemberVariables[index: i].Value) is var operand
                ? Typed(value: Value(operand: operand), from: operand.Type, to: field.Type)
                : ZeroValue(type: field.Type);
            fields.Add(item: $"{field.Name}: {value}");
        }

        string literal = $"{TypeText(type: record)} {{ {string.Join(separator: ", ", values: fields)} }}";
        return new Operand(Text: Temp(type: record, expression: literal), Type: record, IsPlace: false);
    }

    /// <summary>
    /// An entity built from its field values: a heap block from the runtime, its fields stored in order (one
    /// left out is zero, as the runtime hands out zeroed blocks). The value is the block's address.
    /// </summary>
    private Operand EvaluateEntityCreator(CreatorExpression creator, EntityTypeSymbol entity)
    {
        if (creator.MemberVariables.Count == 0 && entity.MemberVariables.Count > 0)
        {
            throw Unsupported(what: $"an empty creator of the entity {entity.FullName}");
        }

        // A value is handed to its field's type: an untyped address is cast to the pointer the field holds.
        var values = creator.MemberVariables
                            .Select(selector: (m, i) =>
                             {
                                 Operand operand = Evaluate(expression: m.Value);
                                 return Typed(value: Value(operand: operand), from: operand.Type,
                                     to: entity.MemberVariables[index: i].Type);
                             })
                            .ToList();
        string allocate = _module.RuntimeRoutine(symbol: "rf_allocate_dynamic", parameters: "%size: U64",
            returnType: "Addr");
        string block = Temp(type: entity, expression: $"{allocate}({entity.HeapBlockSize(pointerSize: 8)})");
        for (int i = 0; i < values.Count; i++)
        {
            Emit(line: $"{block}.cast<{_module.EntityRecord(entity: entity)}>()." +
                       $"{entity.MemberVariables[index: i].Name}.store({values[index: i]})");
        }

        return new Operand(Text: block, Type: entity, IsPlace: false);
    }

    /// <summary>A Check/Lookup carrier built from its tag and payload: zeroed storage, the tag in the first field,
    /// the payload stored at its own type into the second field's bytes.</summary>
    private Operand EvaluateTaggedCreator(TaggedCreatorExpression tagged)
    {
        if (tagged.ResolvedType is not RecordTypeSymbol { MemberVariables.Count: 2 } carrier ||
            carrier is VariantTypeSymbol)
        {
            throw Unsupported(what: $"a tagged construction of {tagged.ResolvedType?.FullName ?? "an untyped value"}");
        }

        string tag = Value(operand: Evaluate(expression: tagged.Tag));
        string slot = $"%carrier{_temps++}";
        Emit(line: $"claim {slot} : @{TypeText(type: carrier)} <- uninit");
        Emit(line: $"{slot}.zeroinit(1)");
        Emit(line: $"{slot}.{carrier.MemberVariables[index: 0].Name}.store({tag})");
        if (tagged.Payload is { } payload)
        {
            string value = Value(operand: Evaluate(expression: payload));
            Emit(line: $"{slot}.{carrier.MemberVariables[index: 1].Name}.cast<{TypeText(type: payload.ResolvedType)}>()" +
                       $".store({value})");
        }

        return new Operand(Text: slot, Type: carrier, IsPlace: true);
    }

    /// <summary>The value a carrier (or variant) holds in its payload, read at the type the arm matched.</summary>
    private Operand EvaluatePayload(CarrierPayloadExpression payload)
    {
        Operand carrier = Evaluate(expression: payload.Carrier);
        if (carrier.Type is not RecordTypeSymbol { MemberVariables.Count: >= 2 } record ||
            carrier.Type is VariantTypeSymbol)
        {
            throw Unsupported(what: $"a payload read from {carrier.Type?.FullName ?? "an untyped value"}");
        }

        TypeSymbol valueType = payload.ResolvedType ?? payload.ConcreteType.ResolvedType ??
                               throw Unsupported(what: "a payload read of an unresolved type");
        return new Operand(Text: $"{Place(operand: carrier)}.{record.MemberVariables[index: 1].Name}" +
                                 $".cast<{TypeText(type: valueType)}>()",
            Type: valueType, IsPlace: true);
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
