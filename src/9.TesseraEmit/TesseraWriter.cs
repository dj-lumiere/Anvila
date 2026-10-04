using System.Text;
using Builder.Backends;
using Builder.Declaration;
using Builder.Instantiation;
using Builder.LlvmEmit;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.TesseraEmit;

/// <summary>
/// Writes the Phase 9 program as one Tessera module: a record per struct record, an external declaration per C
/// routine called, a routine per routine body, and <c>main</c>. It translates and decides nothing about the
/// language: what to emit is the set the demand collector materialized, and every call is already resolved.
/// </summary>
internal sealed class TesseraWriter
{
    private readonly BackendInput _input;

    /// <summary>Tessera names of the routines this module defines, keyed by the routine's mangled symbol.</summary>
    private readonly Dictionary<string, string> _routineNames = new(comparer: StringComparer.Ordinal);

    private readonly HashSet<string> _usedNames = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, string> _recordNames = new(comparer: StringComparer.Ordinal);
    private readonly StringBuilder _records = new();
    private readonly Dictionary<string, string> _externNames = new(comparer: StringComparer.Ordinal);
    private readonly StringBuilder _externs = new();
    private readonly Dictionary<string, string> _texts = new(comparer: StringComparer.Ordinal);
    private readonly StringBuilder _globals = new();
    private readonly Dictionary<string, string> _entityRecords = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, string> _presets = new(comparer: StringComparer.Ordinal);

    /// <summary>Which routines can crash, so only their frames go on the crash trace.</summary>
    private readonly Collection.CrashReachability _crashReachability = new();

    /// <summary>The library this writer exports for Ingrid, or null for a program.</summary>
    private readonly TesseraLibrary? _library;

    /// <summary>A library's routines still to write: each is written once something written before calls it.</summary>
    private readonly Queue<(RoutineInfo Info, Statement Body)> _pending = new();

    /// <summary>The routine symbols a library has written or queued.</summary>
    private readonly HashSet<string> _demanded = new(comparer: StringComparer.Ordinal);

    /// <summary>A library's routine bodies by symbol, so a call can queue its callee.</summary>
    private readonly Dictionary<string, (RoutineInfo Info, Statement Body)> _bodies =
        new(comparer: StringComparer.Ordinal);

    /// <summary>The C symbol of each routine a library exports, by the routine's mangled symbol.</summary>
    private readonly Dictionary<string, string> _exports = new(comparer: StringComparer.Ordinal);

    public TesseraWriter(BackendInput input, TesseraLibrary? library = null)
    {
        _input = input;
        _library = library;
        foreach (string name in new[]
                 {
                     TesseraTrace.Push, TesseraTrace.Pop, TesseraTrace.UpdateLocation, "rf_runtime_init", "main",
                     "c_rf_set_trace_mode", "RfTraceFrame", "RF_TRACE_STACK", "RF_TRACE_DEPTH", "rf_trace_get_depth",
                     "rf_trace_get_frames"
                 })
        {
            _usedNames.Add(item: name);
        }
    }

    /// <summary>Whether the build keeps a crash trace: the debug and release modes, as in the LLVM emitter. A library
    /// keeps none (Ingrid's code has no RazorForge trace).</summary>
    public bool Traces => _library is null && Collection.TraceFrames.Enabled(mode: _input.BuildMode);

    /// <summary>Whether this writes an Ingrid library rather than a program.</summary>
    public bool IsLibrary => _library is not null;

    /// <summary>What a declaration that is not exported starts with: <c>private</c> in a library, whose only
    /// public names are its exports.</summary>
    public string Private => _library is null ? "" : "private ";

    /// <summary>What starts the declaration of <paramref name="routine"/>: its export attribute when a library
    /// exports it, <see cref="Private"/> otherwise.</summary>
    public string RoutinePrefix(RoutineInfo routine)
    {
        return _exports.TryGetValue(key: LlvmEmitter.MangleRoutineName(routine: routine), value: out string? symbol)
            ? $"#export(\"{symbol}\")\n"
            : Private;
    }

    /// <summary>Whether <paramref name="routine"/> pushes a trace frame: a routine of the program (not one the
    /// builder wrote), not <c>@inline</c> or <c>@untraced</c>, that can crash.</summary>
    public bool TracesRoutine(RoutineInfo routine)
    {
        return Traces && Collection.TraceFrames.Pushes(routine: routine, reachability: _crashReachability);
    }

    /// <summary>The Tessera names of the routines the module defines.</summary>
    public IReadOnlyCollection<string> DefinedRoutineNames => _routineNames.Values;

    /// <summary>Writes the module.</summary>
    public string Write()
    {
        if (_library is not null)
        {
            return WriteLibrary(library: _library);
        }

        List<(RoutineInfo Info, Statement Body)> routines = CollectRoutines();
        RegisterCrashReachability();
        foreach ((RoutineInfo info, Statement _) in routines)
        {
            string symbol = LlvmEmitter.MangleRoutineName(routine: info);
            _routineNames[key: symbol] = VerbatimName(text: symbol);
        }

        StringBuilder definitions = _definitions;
        foreach ((RoutineInfo info, Statement body) in routines)
        {
            try
            {
                definitions.Append(value: new TesseraRoutineWriter(module: this, routine: info, body: body).Write());
            }
            catch (NotSupportedException ex) when (!ex.Message.Contains(value: " (in ",
                                                       comparisonType: StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    message: $"{ex.Message} (in {LlvmEmitter.MangleRoutineName(routine: info)})", innerException: ex);
            }

            definitions.Append(value: '\n');
        }

        string main = WriteMain(routines: routines);
        return Assemble(main: main);
    }

    /// <summary>
    /// Writes an Ingrid library: each export under its C symbol, then every routine the written ones call, private to
    /// the file, so the file holds exactly the exports' closure. An export is a routine without <c>me</c>, with the C
    /// ABI's shapes in its signature.
    /// </summary>
    private string WriteLibrary(TesseraLibrary library)
    {
        foreach ((RoutineInfo info, Statement body) in CollectRoutines())
        {
            _bodies[key: LlvmEmitter.MangleRoutineName(routine: info)] = (info, body);
        }

        foreach ((RoutineInfo export, string exportSymbol) in library.Exports)
        {
            string symbol = LlvmEmitter.MangleRoutineName(routine: export);
            if (export is { OwnerType: not null, IsCommon: false } || export.Parameters.Any(predicate: p => p.IsByReference) ||
                !_bodies.ContainsKey(key: symbol))
            {
                throw new NotSupportedException(
                    message: $"The Ingrid export {exportSymbol} must be a routine without `me`, with a body, that " +
                             "takes its parameters by value.");
            }

            _exports[key: symbol] = exportSymbol;
            _usedNames.Add(item: exportSymbol);
            _routineNames[key: symbol] = exportSymbol;
        }

        foreach (string symbol in _bodies.Keys.Where(predicate: s => !_exports.ContainsKey(key: s)))
        {
            _routineNames[key: symbol] = VerbatimName(text: symbol);
        }

        foreach ((RoutineInfo export, string _) in library.Exports)
        {
            Demand(symbol: LlvmEmitter.MangleRoutineName(routine: export));
        }

        while (_pending.TryDequeue(result: out (RoutineInfo Info, Statement Body) next))
        {
            try
            {
                _definitions.Append(value: new TesseraRoutineWriter(module: this, routine: next.Info, body: next.Body)
                   .Write());
            }
            catch (NotSupportedException ex) when (!ex.Message.Contains(value: " (in ",
                                                       comparisonType: StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    message: $"{ex.Message} (in {LlvmEmitter.MangleRoutineName(routine: next.Info)})",
                    innerException: ex);
            }

            _definitions.Append(value: '\n');
        }

        var module = new StringBuilder();
        module.Append(value: library.Header);
        if (_textBytes != null)
        {
            module.Append(value: "import Standard::Collections\nimport Standard::Format\nimport Standard::Os\n\n");
        }

        module.Append(value: _records);
        module.Append(value: _globals);
        module.Append(value: _externs);
        module.Append(value: _definitions);
        return module.ToString();
    }

    /// <summary>Queues a library routine for writing the first time something calls it.</summary>
    private void Demand(string symbol)
    {
        if (_library is not null && _demanded.Add(item: symbol) && _bodies.TryGetValue(key: symbol,
                value: out (RoutineInfo, Statement) routine))
        {
            _pending.Enqueue(item: routine);
        }
    }

    private string? _textBytes;

    /// <summary>
    /// The library routine that turns a builder Text (one U32 code point per character) into the UTF-8 Bytes
    /// Tessera's <c>crash</c> takes, for a crash message the program computes. Written once, on first use. The
    /// memory stays allocated: the crash ends the program.
    /// </summary>
    public string TextBytes()
    {
        if (_textBytes != null)
        {
            return _textBytes;
        }

        _textBytes = VerbatimName(text: "text bytes");
        _definitions.Append(value: $$"""
            /// A crash message as UTF-8.
            private routine {{_textBytes}}(text: {{TypeText(type: TextType)}}) -> Bytes
                shared data  : @U32        = text.data
                shared count : U64         = text.count
                shared utf8  : @List<Byte> <- .construct()

                block entry()
                    jump each(0)

                block each(i: U64)
                    done : Bool = i.ge(count)
                    branch done
                        ? return(utf8.to<Bytes>())
                        : continue
                    code      : U32  = data.stride(i.to<USize>()).load()
                    character : Char = code.to<Char>()
                    utf8.write("{character}")
                    jump each(i.add(1))


            """);
        return _textBytes;
    }

    /// <summary>A source file as a library's <c>#source</c> names it (<see cref="TesseraLibrary.SourceFile"/>).</summary>
    public string SourceName(string file)
    {
        return _library?.SourceFile(arg: file) ?? file;
    }

    /// <summary>The routines translated so far.</summary>
    private readonly StringBuilder _definitions = new();

    /// <summary>
    /// The module as far as translation got, for <c>[debug] dump-tessera</c> when it stops at an unsupported
    /// construct: everything written before that point, then the reason as a comment.
    /// </summary>
    public string PartialModule(string stoppedBecause)
    {
        return Assemble(main: $"// Translation stopped here: {stoppedBecause}\n");
    }

    private string Assemble(string main)
    {
        StringBuilder definitions = _definitions;
        var module = new StringBuilder();
        module.Append(value: "// Generated by the RazorForge builder's Tessera backend.\n\n");
        // In every mode: crash_report reads the trace (a build without a trace pushes nothing, so its depth is 0).
        module.Append(value: TesseraTrace.Support);

        module.Append(value: _records);
        module.Append(value: _globals);
        module.Append(value: _externs);
        module.Append(value: definitions);
        module.Append(value: main);
        return module.ToString();
    }

    /// <summary>Registers every body the LLVM emitter registers for crash reachability: the user routines and
    /// every materialized body.</summary>
    private void RegisterCrashReachability()
    {
        foreach ((Program program, string _, string _) in _input.UserPrograms)
        {
            foreach (RoutineDeclaration routine in program.Declarations.OfType<RoutineDeclaration>())
            {
                if (routine.ResolvedInfo is { } info)
                {
                    _crashReachability.Add(routine: info, body: routine.Body);
                }
            }
        }

        foreach ((string _, MonomorphizedBody body) in _input.InstantiatedGenericBodies ??
                                                       new Dictionary<string, MonomorphizedBody>())
        {
            _crashReachability.Add(routine: body.Info, body: body.Ast.Body);
        }
    }

    // ── What to emit ──────────────────────────────────────────────────────────

    /// <summary>
    /// The routine bodies to define: the user programs' routines, then every body the demand collector
    /// materialized (reached standard library routines, monomorphized and synthesized bodies). Mirrors the LLVM
    /// emitter's set, including its bookkeeping skips (templates, an empty synthesized sentinel, a duplicate).
    /// </summary>
    private List<(RoutineInfo, Statement)> CollectRoutines()
    {
        var result = new List<(RoutineInfo, Statement)>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        void Add(RoutineInfo info, Statement? body)
        {
            if (body is null || !seen.Add(item: LlvmEmitter.MangleRoutineName(routine: info)))
            {
                return;
            }

            result.Add(item: (info, body));
        }

        foreach ((Program program, string _, string _) in _input.UserPrograms)
        {
            foreach (RoutineDeclaration routine in program.Declarations.OfType<RoutineDeclaration>())
            {
                if (routine.ResolvedInfo is { } info && !IsSkippedUserRoutine(info: info))
                {
                    Add(info: info, body: BodyOf(routine: routine, info: info));
                }
            }
        }

        foreach ((string _, MonomorphizedBody body) in _input.InstantiatedGenericBodies ??
                                                       new Dictionary<string, MonomorphizedBody>())
        {
            if (body.Info.OwnerType is { IsGenericDefinition: true } ||
                body.Info.OwnerType?.TypeArguments?.Any(predicate: a => a is BuildtimeConstGenericTypeSymbol) ==
                true ||
                body.IsSynthesized && body.Ast.Body is BlockStatement { Statements.Count: 0 })
            {
                continue;
            }

            if (body.IsSynthesized)
            {
                Add(info: body.Info, body: body.Ast.Body);
            }
            else if (!LlvmEmitter.SkipsDefinition(routineInfo: body.Info, liveRoutineKeys: LiveKeys))
            {
                Add(info: body.Info, body: BodyOf(routine: body.Ast, info: body.Info));
            }
        }

        return result;
    }

    /// <summary>A user routine with no body of its own to emit: a foreign declaration, or one the shared
    /// definition gate skips (a template, or one the demand collector did not reach).</summary>
    private bool IsSkippedUserRoutine(RoutineInfo info)
    {
        return info.CallingConvention != null ||
               LlvmEmitter.SkipsDefinition(routineInfo: info, liveRoutineKeys: LiveKeys);
    }

    private IReadOnlyCollection<string> LiveKeys => _input.LiveRoutineKeys ?? [];

    private HashSet<string>? _liveKeySet;

    private readonly Dictionary<string, (string Name, TypeSymbol Result)> _crashableDispatches =
        new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// The routine that calls a Crashable member (<c>represent</c>, <c>diagnose</c>, …) on a type-erased error:
    /// given the carrier's type id and the error's entity address, it compares the id with each live crashable's
    /// (<c>CrashableDispatchArms</c>, the set the LLVM emitter switches over) and returns that crashable's member.
    /// An id no crashable has cannot reach it, so the chain ends unreachable. Written once per member, so the
    /// expression calling it stays one operation in its routine.
    /// </summary>
    public (string Name, TypeSymbol Result) CrashableDispatch(string member, TypeSymbol typeIdType)
    {
        if (_crashableDispatches.TryGetValue(key: member, value: out (string, TypeSymbol) existing))
        {
            return existing;
        }

        _liveKeySet ??= new HashSet<string>(collection: LiveKeys, comparer: StringComparer.Ordinal);
        List<(ulong TypeId, RoutineInfo Member)> arms = Collection.CrashableDispatchArms.For(memberName: member,
            registry: _input.Registry, liveRoutineKeys: _liveKeySet);
        if (arms is not [{ Member.ReturnType: { } result }, ..])
        {
            throw new NotSupportedException(
                message: $"The Tessera backend found no live crashable with a '{member}' to dispatch to.");
        }

        string name = VerbatimName(text: $"crashable dispatch {member}");
        _crashableDispatches[key: member] = (name, result);
        string idType = TypeText(type: typeIdType);
        string resultType = TypeText(type: result);
        var text = new StringBuilder();
        text.Append(value: $"{Private}routine {name}(type_id: {idType}, error: Addr) -> {resultType}\n");
        for (int i = 0; i < arms.Count; i++)
        {
            (ulong id, RoutineInfo routine) = arms[index: i];
            string receiver = CrashObjectReceiver(routine: routine);
            text.Append(value: $"    block {(i == 0 ? "entry" : $"next_{i}")}()\n")
                .Append(value: $"        is_{i} : Bool = ieq<{idType}>(type_id, 0x{id:X})\n")
                .Append(value: $"        branch is_{i} ? case_{i}() : next_{i + 1}()\n\n")
                .Append(value: $"    block case_{i}()\n")
                .Append(value: $"        r_{i} : {resultType} = {RoutineName(routine: routine)}({receiver})\n")
                .Append(value: $"        return(r_{i})\n\n");
        }

        text.Append(value: $"    block next_{arms.Count}()\n        unreachable\n\n");
        _definitions.Append(value: text);
        return (name, result);
    }

    /// <summary>The receiver a crash-object dispatch passes a crashable's member: the crashable inside the object (a
    /// crashable is a record, whose member takes it by reference).</summary>
    private string CrashObjectReceiver(RoutineInfo routine)
    {
        if (routine.OwnerType is not CrashableTypeSymbol crashable)
        {
            throw new NotSupportedException(
                message: $"The Tessera backend dispatches a caught error's members only on crashables, not on {routine.OwnerType?.FullName}.");
        }

        string error = $"error.to<@{CrashObjectRecord(crashable: crashable)}>().error";
        return Declaration.ReceiverFacts.MeByReference(ownerType: crashable)
            ? error
            : $"{error}.load()";
    }

    private readonly Dictionary<string, string> _crashObjectRecords = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// The record a caught crashable lives in on the heap: its type id, so the object says what it holds, then the
    /// crashable. A carrier's error slot holds the address of one. Declared on first use.
    /// </summary>
    public string CrashObjectRecord(CrashableTypeSymbol crashable)
    {
        if (_crashObjectRecords.TryGetValue(key: crashable.FullName, value: out string? existing))
        {
            return existing;
        }

        string name = VerbatimName(text: $"crash object {crashable.FullName}");
        _crashObjectRecords[key: crashable.FullName] = name;
        _records.Append(value: $"{Private}record {name}\n    type_id : U64\n    error : {TypeText(type: crashable)}\n\n");
        return name;
    }

    private string? _crashObjectDestroy;

    /// <summary>
    /// The routine that frees a caught crashable: given the carrier's type id and the address of the object the
    /// error lives in, it calls that crashable's <c>destroy</c> (a crashable whose type has none live has nothing
    /// to free in its fields), then frees the object. Written once.
    /// </summary>
    public string CrashObjectDestroy(TypeSymbol typeIdType)
    {
        if (_crashObjectDestroy != null)
        {
            return _crashObjectDestroy;
        }

        _liveKeySet ??= new HashSet<string>(collection: LiveKeys, comparer: StringComparer.Ordinal);
        List<(ulong TypeId, RoutineInfo Member)> arms = Collection.CrashableDispatchArms.For(
            memberName: Declaration.RuntimeContract.Destroy, registry: _input.Registry, liveRoutineKeys: _liveKeySet);
        string free = RuntimeRoutine(symbol: "rf_invalidate", parameters: "ptr: Addr", returnType: "Void");
        string name = VerbatimName(text: "crash object destroy");
        _crashObjectDestroy = name;
        string idType = TypeText(type: typeIdType);
        var text = new StringBuilder();
        text.Append(value: $"{Private}routine {name}(type_id: {idType}, error: Addr) -> Void\n");
        for (int i = 0; i < arms.Count; i++)
        {
            (ulong id, RoutineInfo routine) = arms[index: i];
            text.Append(value: $"    block {(i == 0 ? "entry" : $"next_{i}")}()\n")
                .Append(value: $"        is_{i} : Bool = ieq<{idType}>(type_id, 0x{id:X})\n")
                .Append(value: $"        branch is_{i} ? case_{i}() : next_{i + 1}()\n\n")
                .Append(value: $"    block case_{i}()\n")
                .Append(value: $"        {RoutineName(routine: routine)}({CrashObjectReceiver(routine: routine)})\n")
                .Append(value: "        jump free()\n\n");
        }

        text.Append(value: $"    block {(arms.Count == 0 ? "entry" : $"next_{arms.Count}")}()\n        jump free()\n\n")
            .Append(value: $"    block free()\n        {free}(error)\n        return()\n\n");
        _definitions.Append(value: text);
        return name;
    }

    /// <summary>A declaration's body, or the body the builder wrote for a declaration without one.</summary>
    private Statement? BodyOf(RoutineDeclaration routine, RoutineInfo info)
    {
        bool isStub = routine.Body is null or BlockStatement { Statements.Count: 0 };
        return isStub && _input.SynthesizedBodies?.TryGetValue(key: info.RegistryKey, value: out Statement? stub) ==
               true
            ? stub
            : routine.Body;
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    /// <summary>The Tessera routine a call to <paramref name="routine"/> goes to: its definition in this module,
    /// or for a C routine, its external declaration.</summary>
    public string RoutineName(RoutineInfo routine)
    {
        if (routine.CallingConvention == "C")
        {
            return ExternName(routine: routine);
        }

        string symbol = LlvmEmitter.MangleRoutineName(routine: routine);
        if (!_routineNames.TryGetValue(key: symbol, value: out string? name))
        {
            throw new NotSupportedException(
                message: $"The Tessera backend found a call to {symbol}, which has no body in this build.");
        }

        Demand(symbol: symbol);
        return name;
    }

    private string ExternName(RoutineInfo routine)
    {
        string symbol = routine.LinkSymbol ?? routine.Name;
        if (_externNames.TryGetValue(key: symbol, value: out string? name))
        {
            return name;
        }

        name = UniqueName(wanted: "c_" + Sanitize(text: symbol));
        _externNames[key: symbol] = name;
        string parameters = string.Join(separator: ", ",
            // A C parameter's name is only a label; Tessera reserves `self` for a receiver.
            values: routine.Parameters.Select(selector: p =>
                $"{(p.Name == "self" ? "self_" : ValueName(name: p.Name))}: {TypeText(type: p.Type)}"));
        _externs.Append(value: $"#[external(\"c\"), symbol(\"{symbol}\")]\n" +
                               $"{Private}routine {name}({parameters}) -> {TypeText(type: routine.ReturnType)}\n\n");
        return name;
    }

    /// <summary>A C routine of the runtime the module calls by itself (not through a <c>C::</c> declaration),
    /// declared once under its own symbol.</summary>
    private readonly Dictionary<string, string> _variantRecords = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// The record a variant is, laid out as the LLVM emitter lays it out: the live case's type id, then the
    /// bytes of the largest payload, which every case shares (a payload is stored and read at its own type
    /// through a cast of those bytes). Tessera sizes the bytes, from the payload types themselves.
    /// </summary>
    public string VariantRecord(VariantTypeSymbol variant)
    {
        if (_variantRecords.TryGetValue(key: variant.FullName, value: out string? name))
        {
            return name;
        }

        name = VerbatimName(text: $"Variant.{variant.FullName}");
        _variantRecords[key: variant.FullName] = name;
        List<string> payloads = variant.Members
                                       .Where(predicate: m => m is { IsNone: false, Type: not null })
                                       .Select(selector: m => $"sizeof<{TypeText(type: m.Type)}>()")
                                       .ToList();
        var text = new StringBuilder();
        if (payloads.Count == 0)
        {
            // Only payload-less cases: the type id alone, still a record as in the LLVM emitter.
            text.Append(value: "#aggregate\n");
        }

        text.Append(value: $"{Private}record {name}\n    tag : U64\n");
        if (payloads.Count > 0)
        {
            text.Append(value: $"    payload : Array<Byte, max({string.Join(separator: ", ", values: payloads)})>\n");
        }

        _records.Append(value: text)
                .Append(value: '\n');
        return name;
    }

    private string? _routineValueRecord;

    /// <summary>
    /// The record every routine value is, whatever its signature (the LLVM emitter's <c>{ ptr, ptr }</c>):
    /// the code address, and the bound payload a capturing lambda passes as a trailing argument (null for a
    /// plain routine or a captureless lambda). Declared on first use.
    /// </summary>
    public string RoutineValueRecord
    {
        get
        {
            if (_routineValueRecord != null)
            {
                return _routineValueRecord;
            }

            _routineValueRecord = VerbatimName(text: "Routine value");
            _records.Append(value: $"{Private}record {_routineValueRecord}\n    fn : Addr\n    bound : Addr\n\n");
            return _routineValueRecord;
        }
    }

    /// <summary>
    /// The Tessera <c>Callable</c> a routine value's code address is called as: the routine type's parameters,
    /// and with <paramref name="withBound"/> the bound payload after them.
    /// </summary>
    public string CallableText(RoutineTypeSymbol routineType, bool withBound)
    {
        List<string> parameters = routineType.ParameterTypes.Select(selector: TypeText).ToList();
        if (withBound)
        {
            parameters.Add(item: "Addr");
        }

        string list = parameters.Count == 1
            ? $"({parameters[index: 0]},)"
            : $"({string.Join(separator: ", ", values: parameters)})";
        return $"Callable<{list}, {TypeText(type: routineType.ReturnType)}>";
    }

    private readonly Dictionary<string, string> _routineValueCalls = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// The routine that calls a routine value of <paramref name="routineType"/>: a plain routine or captureless
    /// lambda (bound null) gets the arguments, a capturing lambda the bound payload after them. Written once per
    /// signature, so a call through a routine value stays one operation in its routine and a value computed
    /// before it in the same expression is still in scope after it.
    /// </summary>
    public string RoutineValueCall(RoutineTypeSymbol routineType)
    {
        string plain = CallableText(routineType: routineType, withBound: false);
        if (_routineValueCalls.TryGetValue(key: plain, value: out string? existing))
        {
            return existing;
        }

        // The signature names the routine. Its type names are backtick names themselves, so their backticks go.
        string name = VerbatimName(text: $"call {plain.Replace(oldValue: "`", newValue: "")}");
        _routineValueCalls[key: plain] = name;
        string bound = CallableText(routineType: routineType, withBound: true);
        bool returns = !IsVoid(type: routineType.ReturnType);
        string returnType = returns
            ? TypeText(type: routineType.ReturnType)
            : "Void";
        List<string> types = routineType.ParameterTypes.Select(selector: TypeText).ToList();
        string Names(string prefix) => string.Concat(values: types.Select(selector: (_, i) => $", {prefix}{i}"));
        string Parameters(string prefix) =>
            string.Concat(values: types.Select(selector: (t, i) => $", {prefix}{i}: {t}"));
        string Arguments(string prefix) => string.Join(separator: ", ",
            values: types.Select(selector: (_, i) => $"{prefix}{i}"));

        var text = new StringBuilder();
        text.Append(value: $"{Private}routine {name}(value: {RoutineValueRecord}{Parameters(prefix: "a")}) -> {returnType}\n")
            .Append(value: "    block entry()\n")
            .Append(value: "        fn : Addr = value.fn\n")
            .Append(value: "        bound : Addr = value.bound\n")
            .Append(value: "        address : U64 = ptrtoint<Addr, U64>(bound)\n")
            .Append(value: "        unbound : Bool = address.eq(0)\n")
            .Append(value: $"        branch unbound ? plain(fn{Names(prefix: "a")}) : bound(fn, bound{Names(prefix: "a")})\n\n");
        foreach ((string block, string callable, string extra, string prefix) in new[]
                 {
                     ("plain", plain, "", "p"),
                     ("bound", bound, ", payload: Addr", "b")
                 })
        {
            string arguments = Arguments(prefix: prefix);
            if (extra.Length > 0)
            {
                arguments = arguments.Length > 0
                    ? $"{arguments}, payload"
                    : "payload";
            }

            // An Addr is not callable: the code address goes through a slot that is read back as the Callable.
            text.Append(value: $"    block {block}(code: Addr{extra}{Parameters(prefix: prefix)})\n")
                .Append(value: "        claim slot : @Addr <- code\n")
                .Append(value: $"        callee : {callable} = slot.to<@{callable}>().load()\n");
            if (returns)
            {
                text.Append(value: $"        result : {returnType} = callee.call({arguments})\n")
                    .Append(value: "        return(result)\n\n");
            }
            else
            {
                text.Append(value: $"        callee.call({arguments})\n")
                    .Append(value: "        return()\n\n");
            }
        }

        _definitions.Append(value: text);
        return name;
    }

    public string RuntimeRoutine(string symbol, string parameters, string returnType)
    {
        if (_externNames.TryGetValue(key: symbol, value: out string? name))
        {
            return name;
        }

        name = UniqueName(wanted: "c_" + Sanitize(text: symbol));
        _externNames[key: symbol] = name;
        _externs.Append(value: $"#[external(\"c\"), symbol(\"{symbol}\")]\n" +
                               $"{Private}routine {name}({parameters}) -> {returnType}\n\n");
        return name;
    }

    /// <summary>
    /// The global holding <paramref name="data"/>'s elements, laid down once per distinct content; its name is the
    /// address. Empty data is the null address.
    /// </summary>
    public string ConstantData(ConstantDataExpression data)
    {
        if (data.Elements.Count == 0)
        {
            return "null";
        }

        string type = $"Array<{TypeText(type: data.ElementType)}, {data.Elements.Count}>";
        string initial = $"{type} {{ {string.Join(separator: ", ", values: data.Elements)} }}";
        if (_texts.TryGetValue(key: initial, value: out string? name))
        {
            return name;
        }

        name = UniqueName(wanted: $"RF_DATA_{_texts.Count}");
        _texts[key: initial] = name;
        _globals.Append(value: $"{Private}global {name}: @{type} <- {initial}\n\n");
        // The value is a pointer to the first element.
        name = $"{name}.to<@{TypeText(type: data.ElementType)}>()";
        _texts[key: initial] = name;
        return name;
    }

    /// <summary>
    /// The aggregate preset (an <c>Array[T, N]</c> or <c>BitArray[N]</c> table) <paramref name="name"/> names,
    /// seen from <paramref name="routine"/>: the bare name, then qualified by the routine's module, as in the LLVM
    /// emitter. Its table is a global, declared once; the global's name is the table's address. Null when the
    /// name is not an aggregate preset.
    /// </summary>
    public (string Global, TypeSymbol Type)? AggregatePreset(string name, RoutineInfo routine)
    {
        string? module = routine.OwnerType?.Module ?? routine.Module;
        VariableInfo? preset = _input.Registry.LookupVariable(name: name) is { IsPresettableAggregate: true } direct
            ? direct
            : module != null && !name.Contains(value: '.') &&
              _input.Registry.LookupVariable(name: $"{module}.{name}") is { IsPresettableAggregate: true } qualified
                ? qualified
                : null;
        if (preset is null)
        {
            return null;
        }

        if (_presets.TryGetValue(key: preset.QualifiedName, value: out string? global))
        {
            return (global, preset.Type);
        }

        global = UniqueName(wanted: $"RF_PRESET_{_presets.Count}");
        _presets[key: preset.QualifiedName] = global;
        var elements = ((ListLiteralExpression)preset.PresetValue!).Elements;
        string type = TypeText(type: preset.Type);
        // One element per slot (a BitArray table is registered as its packed bytes).
        IEnumerable<string> values = elements.Select(selector: e => e is LiteralExpression literal
            ? TesseraRoutineWriter.ConstantText(literal: literal)
            : throw new NotSupportedException(
                message: $"The Tessera backend found a non-literal element in the preset {preset.QualifiedName}."));
        _globals.Append(value: $"{Private}global {global}: @{type} <- {type} {{ {string.Join(separator: ", ", values: values)} }}\n\n");
        return (global, preset.Type);
    }

    /// <summary>The Tessera record laid out like an entity's heap block (its fields in order, no header), declared
    /// on first use. An entity value is the block's address; a field is read through this record.</summary>
    public string EntityRecord(EntityTypeSymbol entity)
    {
        if (_entityRecords.TryGetValue(key: entity.FullName, value: out string? name))
        {
            return name;
        }

        name = VerbatimName(text: entity.FullName + " block");
        _entityRecords[key: entity.FullName] = name;
        var text = new StringBuilder();
        if (entity.MemberVariables.Count < 2)
        {
            text.Append(value: "#aggregate\n");
        }

        text.Append(value: $"{Private}record {name}\n");
        foreach (MemberVariableInfo field in entity.MemberVariables)
        {
            text.Append(value: $"    {field.Name} : {TypeText(type: field.Type)}\n");
        }

        _records.Append(value: text)
                .Append(value: '\n');
        return name;
    }

    /// <summary>The words a Tessera statement, target, or expression starts with: a value of such a name is written
    /// between backticks.</summary>
    private static readonly HashSet<string> TesseraKeywords =
    [
        "jump", "branch", "when", "return", "unreachable", "continue", "else", "block", "claim", "uninit",
        "true", "false", "null", "routine", "record", "choice", "variant", "preset", "global", "concept", "conform",
        "define", "private", "internal", "module", "import",
    ];

    /// <summary>A value named as the builder's program names it: a Tessera keyword goes between backticks.</summary>
    public static string ValueName(string name) => TesseraKeywords.Contains(item: name) ? $"`{name}`" : name;

    /// <summary>Whether an operand's text is a value's name (a binding, a parameter, a preset), as opposed to a
    /// literal or an expression.</summary>
    public static bool IsName(string text) =>
        text.Length > 0
        && (text[0] == '`' || ((char.IsLetter(c: text[0]) || text[0] == '_') && text.All(predicate: c => char.IsLetterOrDigit(c: c) || c == '_')))
        && text is not ("true" or "false" or "null");

    private string UniqueName(string wanted)
    {
        string name = wanted;
        for (int i = 2; !_usedNames.Add(item: name); i++)
        {
            name = $"{wanted}_{i}";
        }

        return name;
    }

    /// <summary>A builder name as it is, between backticks: a Tessera backtick name holds any character but a
    /// backtick or a line break, so the name needs no rewriting, and two different names never meet.</summary>
    private string VerbatimName(string text)
    {
        // The attribute prefix stays: a recovery variant (`[member, try] f()`) shares the bare name of its routine.
        text = text.Trim(trimChar: '"');
        if (_library is not null)
        {
            // A library is formatted (and checked against the formatter), which breaks a long line after a comma
            // inside its first bracketed list, a name's own brackets included. Its names keep their words but not
            // their commas.
            text = text.Replace(oldValue: ", ", newValue: " ").Replace(oldChar: ',', newChar: ' ');
        }

        if (text.Length == 0 || text.IndexOfAny(anyOf: ['`', '\n', '\r']) >= 0)
        {
            throw new NotSupportedException(message: $"The Tessera backend can't name '{text}' between backticks.");
        }

        string name = $"`{text}`";
        return _usedNames.Add(item: name)
            ? name
            : throw new InvalidOperationException(message: $"The Tessera backend named '{text}' twice.");
    }

    /// <summary>A routine symbol without the attribute prefix the LLVM path writes first (<c>[member] </c>).</summary>
    private static string WithoutAttributes(string text) =>
        text.StartsWith(value: '[') && text.IndexOf(value: "] ", comparisonType: StringComparison.Ordinal) is var close and > 0
            ? text[(close + 2)..]
            : text;

    /// <summary>A Tessera identifier for a builder name: letters and digits kept, every other run of
    /// characters one underscore, the routine attribute prefix (<c>[member] </c>) dropped.</summary>
    private static string Sanitize(string text)
    {
        string body = WithoutAttributes(text: text.Trim(trimChar: '"'));
        var sb = new StringBuilder(capacity: body.Length);
        foreach (char c in body)
        {
            if (char.IsAsciiLetterOrDigit(c: c))
            {
                sb.Append(value: c);
            }
            else if (sb.Length > 0 && sb[^1] != '_')
            {
                sb.Append(value: '_');
            }
        }

        string name = sb.ToString()
                        .TrimEnd(trimChar: '_');
        return name.Length == 0 || char.IsAsciiDigit(c: name[0])
            ? "rf_" + name
            : name;
    }

    // ── Types ─────────────────────────────────────────────────────────────────

    /// <summary>The Tessera spelling of a builder type.</summary>
    public string TypeText(TypeSymbol? type)
    {
        switch (type)
        {
            case null:
                return "Void";
            case RecordTypeSymbol { BackendType: "void" }:
                return "Void";
            case VariantTypeSymbol variant:
                return VariantRecord(variant: variant);
            case EntityTypeSymbol:
                return "Addr";
            case RoutineTypeSymbol:
                return RoutineValueRecord;
            // A marker borrow protocol (Accessing[X], Controlling[X]) is laid out as its inner X, as in the LLVM
            // emitter: an entity is its block's address, a value its own layout.
            case ProtocolTypeSymbol { TypeArguments: [{ } markerInner] } marker when RuntimeContract.IsMarkerProtocol(
                baseName: (marker.GenericDefinition ?? marker).BareName):
                return TypeText(type: markerInner);
            case ConstGenericValueTypeSymbol constant:
                return TypeText(type: ConstantType(constant: constant));
            case RecordTypeSymbol { BackendType: { } backend } record:
                return PrimitiveTypeText(record: record, backend: backend);
            case RecordTypeSymbol { IsGenericDefinition: false } record:
                return RecordName(record: record);
            default:
                throw new NotSupportedException(
                    message: $"The Tessera backend does not translate {type.GetType().Name} '{type.FullName}' yet.");
        }
    }

    /// <summary>The integer type of a const generic value: its declared type, else <c>U64</c> (as in the LLVM
    /// emitter).</summary>
    public TypeSymbol ConstantType(ConstGenericValueTypeSymbol constant)
    {
        return _input.Registry.LookupType(name: constant.ExplicitTypeName ?? "U64") ??
               throw new NotSupportedException(
                   message: $"The Tessera backend found no type '{constant.ExplicitTypeName ?? "U64"}' for a constant.");
    }

    /// <summary>The builder's <c>Text</c> type.</summary>
    public TypeSymbol TextType => _input.Registry.LookupType(name: "Text") ??
                                  throw new NotSupportedException(message: "The Tessera backend found no Text type.");

    /// <summary>A cancellation node: the three untyped addresses the coroutine runtime links a local by.</summary>
    public TypeSymbol CancellationNodeType
    {
        get
        {
            TypeSymbol pointer = _input.Registry.LookupType(name: Declaration.RuntimeContract.CPtr) ??
                                 throw new NotSupportedException(message: "The Tessera backend found no CPtr type.");
            return _input.Registry.GetOrCreateTupleType(elementTypes: [pointer, pointer, pointer]);
        }
    }

    /// <summary>The builder's <c>U64</c> type.</summary>
    public TypeSymbol U64Type => _input.Registry.LookupType(name: "U64") ??
                                 throw new NotSupportedException(message: "The Tessera backend found no U64 type.");

    /// <summary>The builder's <c>Bool</c> type.</summary>
    public TypeSymbol BoolType => _input.Registry.LookupType(name: "Bool") ??
                                  throw new NotSupportedException(message: "The Tessera backend found no Bool type.");

    /// <summary>The builder's <c>CPtr</c> type: an untyped address, Tessera's <c>Addr</c>.</summary>
    public TypeSymbol PointerType => _input.Registry.LookupType(name: "CPtr") ??
                                     throw new NotSupportedException(message: "The Tessera backend found no CPtr type.");

    /// <summary>The builder's <c>Address</c> type.</summary>
    public TypeSymbol AddressType => _input.Registry.LookupType(name: "Address") ??
                                     throw new NotSupportedException(message: "The Tessera backend found no Address type.");

    /// <summary>True for the unit type <c>None</c>, which has no value.</summary>
    public static bool IsVoid(TypeSymbol? type)
    {
        return type is null or RecordTypeSymbol { BackendType: "void" };
    }

    private string PrimitiveTypeText(RecordTypeSymbol record, string backend)
    {
        string? byName = record.BareName switch
        {
            "S8" or "S16" or "S32" or "S64" or "S128" or "S256" or "U8" or "U16" or "U32" or "U64" or "U128" or "U256" =>
                record.BareName,
            "Bool" => "Bool",
            "Byte" => "U8",
            "B16" => "F16",
            "B32" => "F32",
            "B64" => "F64",
            "BF16" => "BF16",
            _ => null
        };
        if (byName != null)
        {
            return byName;
        }

        // Hijacked[T] is a pointer to a T: Tessera's @T (an entity's points at its heap block). Any other pointer
        // record is an untyped address.
        if (backend == "ptr")
        {
            // A pointer to a protocol has no layout to point at: an untyped address (a marker protocol points at
            // what it stands for).
            return record is { GenericDefinition.Name: RuntimeContract.Hijacked, TypeArguments: [var target] }
                ? target switch
                {
                    EntityTypeSymbol entity => $"@{EntityRecord(entity: entity)}",
                    ProtocolTypeSymbol { TypeArguments: [{ } inner] } marker when RuntimeContract.IsMarkerProtocol(
                        baseName: (marker.GenericDefinition ?? marker).BareName) => $"@{TypeText(type: inner)}",
                    ProtocolTypeSymbol => "Addr",
                    _ => $"@{TypeText(type: target)}"
                }
                : "Addr";
        }

        // Array[T, N]: a fixed array of N values of T.
        if (backend.StartsWith(value: '[') &&
            record.TypeArguments is [var element, ConstGenericValueTypeSymbol { Value: var count }])
        {
            return $"Array<{TypeText(type: element)}, {count}>";
        }

        // Any other integer-backed type (a choice, flags, Address, Character, a C integer alias) is the integer
        // of its width. A choice is signed, as in the LLVM emitter; the rest are unsigned bit carriers, and the
        // primitives they reach check signedness against the operand type.
        if (backend.Length > 1 && backend[0] == 'i' && int.TryParse(s: backend[1..], result: out int bits))
        {
            return record is ChoiceTypeSymbol
                ? $"S{bits}"
                : $"U{bits}";
        }

        throw new NotSupportedException(
            message: $"The Tessera backend has no translation for the primitive type {record.FullName} ({backend}).");
    }

    /// <summary>The Tessera record for a struct record, declared on first use with its fields in order.</summary>
    private string RecordName(RecordTypeSymbol record)
    {
        if (_recordNames.TryGetValue(key: record.FullName, value: out string? name))
        {
            return name;
        }

        name = VerbatimName(text: record.FullName);
        _recordNames[key: record.FullName] = name;
        var text = new StringBuilder();
        text.Append(value: $"{Private}record {name}\n");
        if (record.MemberVariables.Count < 2)
        {
            // Tessera gives a one-field record the field's own representation unless it is #aggregate. The
            // builder lays every struct record out as a struct, so keep that.
            text.Insert(index: 0, value: "#aggregate\n");
        }

        foreach (MemberVariableInfo field in record.MemberVariables)
        {
            text.Append(value: $"    {field.Name} : {TypeText(type: field.Type)}\n");
        }

        _records.Append(value: text)
                .Append(value: '\n');
        return name;
    }

    // ── Entry ─────────────────────────────────────────────────────────────────

    /// <summary><c>main</c>: initializes the runtime, then runs the entry module's <c>start</c>.</summary>
    private string WriteMain(List<(RoutineInfo Info, Statement Body)> routines)
    {
        string? entry = _input.EntryModule;
        RoutineInfo start = Collection.EntryPoint.StartOf(defined: routines.Select(selector: r => r.Info),
                                entryModule: entry) ??
                            throw new NotSupportedException(
                                message: $"The Tessera backend found no start() routine in the entry module '{entry}'.");

        return "#[external(\"c\"), symbol(\"rf_runtime_init\")]\n" +
               "routine rf_runtime_init() -> Void\n\n" +
               "#[external(\"c\"), symbol(\"__rf_set_trace_mode\")]\n" +
               "routine c_rf_set_trace_mode(mode: S32) -> Void\n\n" +
               "#[external(\"c\"), symbol(\"rf_set_stack_overflow_report\")]\n" +
               "routine c_rf_set_stack_overflow_report(report: Addr) -> Void\n\n" +
               // RazorForge reports a stack overflow, Suflae (no "stack" in its vocabulary) running out of memory.
               $"#[external(\"c\"), symbol(\"{(_input.Registry.Language == TypeModel.Enums.Language.Suflae ? "tessera_deep_calls_report" : "tessera_stack_overflow_report")}\")]\n" +
               "routine c_tessera_stack_overflow_report(stack_size: U64) -> Void\n\n" +
               "routine main() -> S32\n" +
               "    block entry()\n" +
               "        rf_runtime_init()\n" +
               // Trace mode 2 is the shadow stack (debug and release), 0 none, as the LLVM emitter sets it.
               $"        c_rf_set_trace_mode({(Traces ? 2 : 0)})\n" +
               // A stack overflow is reported by Ingrid's crash report, linked in with the crash trace.
               "        c_rf_set_stack_overflow_report(c_tessera_stack_overflow_report.addr())\n" +
               $"        {RoutineName(routine: start)}()\n" +
               "        return(0)\n";
    }
}
