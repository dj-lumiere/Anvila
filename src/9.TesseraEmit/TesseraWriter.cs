using Builder.Diagnostics;
using System.Globalization;
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
/// routine called, a routine per routine body, and Tessera's entry point <c>start</c>. It translates and decides
/// nothing about the language: what to emit is the set the demand collector materialized, and every call is already
/// resolved.
/// </summary>
internal sealed class TesseraWriter
{
    private readonly BackendInput _input;

    /// <summary>
    /// The module in Tessera's current spellings: this writer still writes some of the old ones (`Void`, `#name`
    /// annotations, `require`), which the builder now reads only through its migration.
    /// </summary>
    private static string Current(string text) =>
        Tessera.NoneSpelling.Rewrite(text: Tessera.StructuredMigration.Spellings(text: text));

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

    /// <summary>Whether this writes the compile daemon's resident base: every routine, no <c>start</c>.</summary>
    private readonly bool _residentBase;

    /// <summary>The routine symbols the resident base defines, when this writes a delta against it: such a routine is
    /// declared, not written (<see cref="ResidentDeclaration"/>).</summary>
    private readonly IReadOnlySet<string> _resident;

    public TesseraWriter(BackendInput input, TesseraLibrary? library = null, bool residentBase = false)
    {
        _input = input;
        _library = library;
        _residentBase = residentBase;
        _resident = input.ResidentSymbols is { Count: > 0 } resident
            ? resident as IReadOnlySet<string> ?? new HashSet<string>(collection: resident, comparer: StringComparer.Ordinal)
            : new HashSet<string>();
        foreach (string name in new[]
                 {
                     TesseraTrace.Push, TesseraTrace.Pop, TesseraTrace.UpdateLocation, "rf_runtime_init", "start", "c_rf_exit_code",
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

    /// <summary>What a declaration that is not exported starts with: <c>secret</c> in a library, whose only
    /// public names are its exports.</summary>
    public string Private => _library is null ? "" : "secret ";

    /// <summary>What starts the declaration of <paramref name="routine"/>: its export attribute when a library
    /// exports it, <see cref="Private"/> otherwise.</summary>
    public string RoutinePrefix(RoutineInfo routine)
    {
        // Every routine written here is generated code, which Tessera lets use blocks and jumps.
        return _exports.TryGetValue(key: LlvmEmitter.MangleRoutineName(routine: routine), value: out string? symbol)
            ? $"@generated\n@export(\"{symbol}\")\n"
            : $"@generated\n{Private}";
    }

    /// <summary>Whether <paramref name="routine"/> pushes a trace frame: a routine of the program (not one the
    /// builder wrote), not <c>@inline</c> or <c>@untraced</c>, that can crash.</summary>
    public bool TracesRoutine(RoutineInfo routine)
    {
        return Traces && Collection.TraceFrames.Pushes(routine: routine, reachability: _crashReachability);
    }

    /// <summary>The Tessera names of the routines the module defines.</summary>
    public IReadOnlyCollection<string> DefinedRoutineNames =>
        _routineNames.Where(predicate: r => !_residentDeclarations.Contains(item: r.Key))
                     .Select(selector: r => r.Value)
                     .ToList();

    /// <summary>The routine symbols of the routines the module defines (the builder's names, not Tessera's).</summary>
    public IReadOnlyCollection<string> DefinedRoutineSymbols =>
        _routineNames.Keys.Where(predicate: s => !_residentDeclarations.Contains(item: s)).ToList();

    /// <summary>Whether the module defines the routine of this symbol.</summary>
    public bool DefinesRoutine(string symbol) =>
        _routineNames.ContainsKey(key: symbol) && !_residentDeclarations.Contains(item: symbol);

    /// <summary>Writes the module.</summary>
    public string Write()
    {
        if (_library is not null)
        {
            return WriteLibrary(library: _library);
        }

        List<(RoutineInfo Info, Statement Body)> routines = CollectRoutines();
        RegisterCrashReachability();
        // A delta writes no routine the resident base defines: each one it calls is declared on first call.
        routines.RemoveAll(match: r => _resident.Contains(item: LlvmEmitter.MangleRoutineName(routine: r.Info)));
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

        string entry = _residentBase ? "" : WriteStart(routines: routines);
        return Assemble(main: entry);
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
            module.Append(value: "import Standard/Collections\nimport Standard/Os\n\n");
        }

        module.Append(value: _records);
        module.Append(value: _globals);
        module.Append(value: _externs);
        module.Append(value: _definitions);
        // Every record says how it is made and released (a construct taking its fields, a destruct), and the lines
        // after a routine's head are its entry block: `block entry()` is written only where a block jumps back to it.
        return Current(text: Tessera.EntryMigration.Rewrite(text: Tessera.ConstructDestructMigration.Rewrite(text: module.ToString())));
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
    /// The library routine that writes a builder Text (one U32 code point per character) as UTF-8 into the caller's
    /// list and gives the bytes (a <c>@[Byte]</c>) Tessera's <c>crash</c> takes, for a crash message the program
    /// computes. Written once, on first use. The list stays allocated: the crash ends the program.
    /// </summary>
    public string TextBytes()
    {
        if (_textBytes != null)
        {
            return _textBytes;
        }

        _textBytes = VerbatimName(text: "text bytes");
        _definitions.Append(value: $$"""
            ### A crash message as UTF-8.
            secret routine {{_textBytes}}(text: {{TypeText(type: TextType)}}, into: @List[Byte]) -> @[Byte]
                local data: @U32 = text.data
                local count: U64 = text.count

                each i in 0 til count
                    code: U32 = data.stride(i).peek()
                    character: Char = code.to[Char]()
                    into.write("{character}")
                return into.getview()


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
        return Assemble(main: $"# Translation stopped here: {stoppedBecause}\n");
    }

    private string Assemble(string main)
    {
        StringBuilder definitions = _definitions;
        var module = new StringBuilder();
        module.Append(value: "# Generated by the RazorForge builder's Tessera backend.\n\n");
        // In every mode: crash_report reads the trace (a build without a trace pushes nothing, so its depth is 0).
        module.Append(value: TesseraTrace.Support);

        module.Append(value: _records);
        module.Append(value: _globals);
        module.Append(value: _externs);
        module.Append(value: definitions);
        module.Append(value: main);
        // Every record says how it is made and released (a construct taking its fields, a destruct), and the lines
        // after a routine's head are its entry block: `block entry()` is written only where a block jumps back to it.
        return Current(text: Tessera.EntryMigration.Rewrite(text: Tessera.ConstructDestructMigration.Rewrite(text: module.ToString())));
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
                message: $"The Tessera backend found no live crashable with {Article.For(name: member)} '{member}' to dispatch to.");
        }

        string name = VerbatimName(text: $"crashable dispatch {member} {ArmsTag(arms: arms)}");
        _crashableDispatches[key: member] = (name, result);
        string idType = TypeText(type: typeIdType);
        string resultType = TypeText(type: result);
        var text = new StringBuilder();
        text.Append(value: $"{Private}routine {name}(type_id: {idType}, error: Addr) -> {resultType}\n");
        for (int i = 0; i < arms.Count; i++)
        {
            (ulong id, RoutineInfo routine) = arms[index: i];
            string receiver = CrashObjectReceiver(routine: routine);
            text.Append(value: $"    is_{i}: Bool = ieq[{idType}](type_id, 0x{id:X})\n")
                .Append(value: $"    if is_{i}\n")
                .Append(value: $"        r_{i}: {resultType} = {RoutineName(routine: routine)}({receiver})\n")
                .Append(value: $"        return r_{i}\n");
        }

        text.Append(value: "    unreachable\n\n");
        _definitions.Append(value: text);
        return (name, result);
    }

    /// <summary>
    /// What a dispatch over <paramref name="arms"/> is named by besides its member: a hash of the arms, since its body
    /// is the build's live crashables. A resident base and a delta (or two deltas) with other crashables then name
    /// two routines, and ones with the same arms one routine, which the delta can take from the base.
    /// </summary>
    private static string ArmsTag(List<(ulong TypeId, RoutineInfo Member)> arms)
    {
        string text = string.Join(separator: "\n",
            values: arms.Select(selector: a => $"{a.TypeId:X} {LlvmEmitter.MangleRoutineName(routine: a.Member)}"));
        return Convert.ToHexString(inArray: System.Security.Cryptography.SHA256.HashData(
            source: Encoding.UTF8.GetBytes(s: text)))[..16];
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

        string error = $"error.to[@{TypeText(type: crashable)}]()";
        return Declaration.ReceiverFacts.MeByReference(ownerType: crashable)
            ? error
            : $"{error}.peek()";
    }

    /// <summary>System's <c>Crashables</c>, which a carrier's error slot holds.</summary>
    public TypeSymbol CrashablesType => _input.Registry.LookupType(name: Declaration.RuntimeContract.Crashables) ??
                                        throw new NotSupportedException(message: "System.Crashables is not registered.");

    private string? _crashableDestroy;

    /// <summary>
    /// The routine that gives back what a caught crashable holds: given its type id and its address in its
    /// <c>Crashables</c>, it calls that crashable's <c>destruct</c> (a crashable whose type has none live has nothing to
    /// give back in its fields). Written once.
    /// </summary>
    public string CrashableDestroy(TypeSymbol typeIdType)
    {
        if (_crashableDestroy != null)
        {
            return _crashableDestroy;
        }

        _liveKeySet ??= new HashSet<string>(collection: LiveKeys, comparer: StringComparer.Ordinal);
        List<(ulong TypeId, RoutineInfo Member)> arms = Collection.CrashableDispatchArms.For(
            memberName: Declaration.RuntimeContract.Destroy, registry: _input.Registry, liveRoutineKeys: _liveKeySet);
        string name = VerbatimName(text: $"crashable destroy {ArmsTag(arms: arms)}");
        _crashableDestroy = name;
        string idType = TypeText(type: typeIdType);
        var text = new StringBuilder();
        text.Append(value: $"{Private}routine {name}(type_id: {idType}, error: Addr) -> Void\n");
        for (int i = 0; i < arms.Count; i++)
        {
            (ulong id, RoutineInfo routine) = arms[index: i];
            text.Append(value: $"    is_{i}: Bool = ieq[{idType}](type_id, 0x{id:X})\n")
                .Append(value: $"    if is_{i}\n")
                .Append(value: $"        {RoutineName(routine: routine)}({CrashObjectReceiver(routine: routine)})\n")
                .Append(value: "        return\n");
        }

        text.Append(value: "    return\n\n");
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
            if (_resident.Contains(item: symbol))
            {
                return ResidentDeclaration(routine: routine, symbol: symbol);
            }

            throw new NotSupportedException(
                message: $"The Tessera backend found a call to {symbol}, which has no body in this build.");
        }

        Demand(symbol: symbol);
        return name;
    }

    /// <summary>
    /// A routine of the resident base, which a delta calls but does not define: written with its signature and a body
    /// that is never built, since its Tessera symbol is one the base provides (<c>Tessera.Compiler.ProvidedSymbols</c>),
    /// so Tessera declares it and the call links to the base's definition. The signature is the one the base's routine
    /// has (<see cref="TesseraRoutineWriter.WriteDeclarationOnly"/>), so the two symbols are one. Written once, on the
    /// first call.
    /// </summary>
    private string ResidentDeclaration(RoutineInfo routine, string symbol)
    {
        string name = VerbatimName(text: symbol);
        _routineNames[key: symbol] = name;
        _residentDeclarations.Add(item: symbol);
        _definitions.Append(value: new TesseraRoutineWriter(module: this, routine: routine, body: new BlockStatement(
                Statements: [], Location: routine.Location ?? new SourceLocation(FileName: "", Line: 0, Column: 0, Position: 0)))
           .WriteDeclarationOnly());
        _definitions.Append(value: '\n');
        return name;
    }

    /// <summary>The routines of the resident base this delta declared.</summary>
    private readonly HashSet<string> _residentDeclarations = new(comparer: StringComparer.Ordinal);

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
            // A C parameter's name is only a label, and Tessera reserves `me` for a receiver.
            values: routine.Parameters.Select(selector: p =>
                $"{(p.Name == "me" ? "me_" : ValueName(name: p.Name))}: {TypeText(type: p.Type)}"));
        _externs.Append(value: $"@external(\"c\")\n@symbol(\"{symbol}\")\n" +
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
                                       .Select(selector: m => $"sizeof[{TypeText(type: m.Type)}]()")
                                       .ToList();
        var text = new StringBuilder();
        text.Append(value: $"{Private}record {name}\n    tag: U64\n");
        if (payloads.Count > 0)
        {
            text.Append(value: $"    payload: Array[Byte, max({string.Join(separator: ", ", values: payloads)})]\n");
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
            _records.Append(value: $"{Private}record {_routineValueRecord}\n    fn: Addr\n    bound: Addr\n    recover: Addr\n\n");
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
        return $"Callable[{list}, {TypeText(type: routineType.ReturnType)}]";
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
            .Append(value: "    fn: Addr = value.fn\n")
            .Append(value: "    bound: Addr = value.bound\n")
            .Append(value: "    address: U64 = ptrtoint[Addr, U64](bound)\n")
            .Append(value: "    unbound: Bool = address.eq(0)\n")
            // An Addr is not callable: the code address goes through a slot that is read back as the Callable.
            .Append(value: "    local slot: @Addr <- fn\n");
        foreach ((string keyword, string callable, bool withPayload) in new[]
                 {
                     ("if unbound", plain, false),
                     ("else", bound, true)
                 })
        {
            string arguments = Arguments(prefix: "a");
            if (withPayload)
            {
                arguments = arguments.Length > 0
                    ? $"{arguments}, bound"
                    : "bound";
            }

            text.Append(value: $"    {keyword}\n")
                .Append(value: $"        callee: {callable} = slot.to[@{callable}]().peek()\n");
            if (returns)
            {
                text.Append(value: $"        result: {returnType} = callee.call({arguments})\n")
                    .Append(value: "        return result\n");
            }
            else
            {
                text.Append(value: $"        callee.call({arguments})\n")
                    .Append(value: "        return\n");
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
        _externs.Append(value: $"@external(\"c\")\n@symbol(\"{symbol}\")\n" +
                               $"{Private}routine {name}({parameters}) -> {returnType}\n\n");
        return name;
    }

    /// <summary>
    /// The read-only memory holding <paramref name="data"/>'s elements (a Tessera preset in memory, a private constant
    /// as in the LLVM emitter, so each module keeps its own), laid down once per distinct content; its name is the
    /// address. Empty data is the hollow address, `.hollow()`.
    /// </summary>
    public string ConstantData(ConstantDataExpression data)
    {
        if (data.Elements.Count == 0)
        {
            return ".hollow()";
        }

        string type = $"Array[{TypeText(type: data.ElementType)}, {data.Elements.Count}]";
        // Each element is given as the long with its bits: a U64 element (an Integer literal's limbs) is written
        // unsigned.
        bool unsigned64 = data.ElementType.Name == "U64";
        string initial = $"[{string.Join(separator: ", ", values: data.Elements.Select(selector: e =>
            unsigned64 ? unchecked((ulong)e).ToString(provider: CultureInfo.InvariantCulture) : e.ToString(provider: CultureInfo.InvariantCulture)))}]";
        // The same elements of another element type are another table.
        string key = $"{type} {initial}";
        if (_texts.TryGetValue(key: key, value: out string? name))
        {
            return name;
        }

        name = UniqueName(wanted: $"RF_DATA_{_texts.Count}");
        _texts[key: key] = name;
        _globals.Append(value: $"{Private}preset {name}: @{type} <- {initial}\n\n");
        // The value is a pointer to the first element.
        name = $"{name}.to[@{TypeText(type: data.ElementType)}]()";
        _texts[key: key] = name;
        return name;
    }

    /// <summary>
    /// The aggregate preset (an <c>Array[T, N]</c> or <c>BitArray[N]</c> table) <paramref name="name"/> names,
    /// seen from <paramref name="routine"/>: the bare name, then qualified by the routine's module, as in the LLVM
    /// emitter. Its table is read-only memory (a Tessera preset in memory), declared once; the preset's name is the
    /// table's address. Null when the
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
        _globals.Append(value: $"{Private}preset {global}: @{type} <- [{string.Join(separator: ", ", values: values)}]\n\n");
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
        text.Append(value: $"{Private}record {name}\n");
        foreach (MemberVariableInfo field in entity.MemberVariables)
        {
            text.Append(value: $"    {field.Name}: {TypeText(type: field.Type)}\n");
        }

        _records.Append(value: text)
                .Append(value: '\n');
        return name;
    }

    /// <summary>The words a Tessera statement, target, or expression starts with, plus `Me` (the receiver's type) and
    /// `self`/`Self` (words Tessera doesn't read as names): a value of such a name is written between backticks.</summary>
    private static readonly HashSet<string> TesseraKeywords =
    [
        "jump", "branch", "when", "return", "unreachable", "continue", "else", "block", "claim", "local", "todo", "and", "or", "is", "as",
        "not", "if", "each", "while", "until", "becomes", "break", "pass", "needs", "obeys", "protocol",
        "true", "false", "null", "routine", "record", "choice", "variant", "preset", "global", "concept", "conform",
        "define", "secret", "private", "internal", "module", "import", "Me", "self", "Self",
    ];

    /// <summary>A value named as the builder's program names it: a Tessera keyword goes between backticks.</summary>
    public static string ValueName(string name) => TesseraKeywords.Contains(item: name) ? $"`{name}`" : name;

    /// <summary>Whether an operand's text is a value's name (a binding, a parameter, a preset), as opposed to a
    /// literal or an expression.</summary>
    public static bool IsName(string text) =>
        text.Length > 0
        && (text[0] == '`' || ((char.IsLetter(c: text[0]) || text[0] == '_') && text.All(predicate: c => char.IsLetterOrDigit(c: c) || c == '_')))
        && text is not ("true" or "false");

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
            TypeSymbol pointer = _input.Registry.LookupType(name: Declaration.RuntimeContract.Addr) ??
                                 throw new NotSupportedException(message: "The Tessera backend found no Addr type.");
            return _input.Registry.GetOrCreateTupleType(elementTypes: [pointer, pointer, pointer]);
        }
    }

    /// <summary>The builder's <c>U64</c> type.</summary>
    public TypeSymbol U64Type => _input.Registry.LookupType(name: "U64") ??
                                 throw new NotSupportedException(message: "The Tessera backend found no U64 type.");

    /// <summary>The builder's <c>Bool</c> type.</summary>
    public TypeSymbol BoolType => _input.Registry.LookupType(name: "Bool") ??
                                  throw new NotSupportedException(message: "The Tessera backend found no Bool type.");

    /// <summary>The builder's <c>Addr</c> type: an untyped address, Tessera's <c>Addr</c>.</summary>
    public TypeSymbol PointerType => _input.Registry.LookupType(name: Declaration.RuntimeContract.Addr) ??
                                     throw new NotSupportedException(message: "The Tessera backend found no Addr type.");

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
            "B16" or "B32" or "B64" or "BF16" => record.BareName,
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
                    ProtocolTypeSymbol => "Addr",
                    _ => $"@{TypeText(type: target)}"
                }
                : "Addr";
        }

        // Array[T, N]: a fixed array of N values of T.
        if (backend.StartsWith(value: '[') &&
            record.TypeArguments is [var element, ConstGenericValueTypeSymbol { Value: var count }])
        {
            return $"Array[{TypeText(type: element)}, {count}]";
        }

        // Any other integer-backed type (a choice, flags, Char, a C integer alias) is the integer
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

        foreach (MemberVariableInfo field in record.MemberVariables)
        {
            text.Append(value: $"    {field.Name}: {TypeText(type: field.Type)}\n");
        }

        _records.Append(value: text)
                .Append(value: '\n');
        return name;
    }

    // ── Entry ─────────────────────────────────────────────────────────────────

    /// <summary>The deadlock check, the access-order check and the language, as the runtime's AccessManager takes
    /// them.</summary>
    private string AccessCheckArguments()
    {
        (int deadlockCheck, int accessOrder, int language) =
            LlvmEmitter.AccessChecks(registry: _input.Registry, mode: _input.BuildMode);
        return $"{deadlockCheck}, {accessOrder}, {language}";
    }

    /// <summary>Tessera's entry point <c>start</c>: initializes the runtime, runs the entry module's <c>start</c>, and
    /// hands the exit status the program set (the runtime's <c>rf_exit_code</c>) to Tessera's <c>set_exit_code</c>,
    /// which the <c>main</c> Tessera writes returns.</summary>
    private string WriteStart(List<(RoutineInfo Info, Statement Body)> routines)
    {
        string? entry = _input.EntryModule;
        RoutineInfo start = Collection.EntryPoint.StartOf(defined: routines.Select(selector: r => r.Info),
                                entryModule: entry) ??
                            throw new NotSupportedException(
                                message: $"The Tessera backend found no start() routine in the entry module '{entry}'.");

        return "@external(\"c\")\n@symbol(\"rf_runtime_init\")\n" +
               "routine rf_runtime_init() -> Void\n\n" +
               "@external(\"c\")\n@symbol(\"__rf_set_trace_mode\")\n" +
               "routine c_rf_set_trace_mode(mode: S32) -> Void\n\n" +
               "@external(\"c\")\n@symbol(\"rf_set_stack_overflow_report\")\n" +
               "routine c_rf_set_stack_overflow_report(report: Addr) -> Void\n\n" +
               // RazorForge reports a stack overflow, Suflae (no "stack" in its vocabulary) running out of memory.
               $"@external(\"c\")\n@symbol(\"{(_input.Registry.Language == TypeModel.Enums.Language.Suflae ? "tessera_deep_calls_report" : "tessera_stack_overflow_report")}\")\n" +
               "routine c_tessera_stack_overflow_report(stack_size: U64) -> Void\n\n" +
               "@external(\"c\")\n@symbol(\"rf_set_crash_report\")\n" +
               "routine c_rf_set_crash_report(report: Addr) -> Void\n\n" +
               "@external(\"c\")\n@symbol(\"tessera_runtime_crash_report\")\n" +
               "routine c_tessera_runtime_crash_report(name: CStrView, message: CStrView, place: Addr) -> Void\n\n" +
               "@external(\"c\")\n@symbol(\"tessera_set_library_folder\")\n" +
               "routine c_tessera_set_library_folder(library: CStrView) -> Void\n\n" +
               "@external(\"c\")\n@symbol(\"rf_exit_code\")\n" +
               "routine c_rf_exit_code() -> S32\n\n" +
               "@external(\"c\")\n@symbol(\"rf_access_configure\")\n" +
               "routine c_rf_access_configure(deadlock_check: U32, access_order: U32, language: U32, place: Addr) -> Void\n\n" +
               "routine start() -> Void\n" +
               "    rf_runtime_init()\n" +
               // Trace mode 2 is the shadow stack (debug and release), 0 none, as the LLVM emitter sets it.
               $"    c_rf_set_trace_mode({(Traces ? 2 : 0)})\n" +
               // A stack overflow is reported by Ingrid's crash report, linked in with the crash trace.
               "    c_rf_set_stack_overflow_report(c_tessera_stack_overflow_report.addr())\n" +
               // So is a crash the runtime raises (a deadlock, a failed allocation), named by where the program called
               // into the standard library.
               "    c_rf_set_crash_report(c_tessera_runtime_crash_report.addr())\n" +
               $"    c_tessera_set_library_folder({TesseraTrace.CString(text: _input.Registry.StdlibPath is { } stdlib ? Path.GetFullPath(path: stdlib) : "")})\n" +
               // The AccessManager's checks and the language its reports speak. The places in its reports come from
               // the LLVM emitter's crash trace, which this backend doesn't keep: they are left out.
               $"    c_rf_access_configure({AccessCheckArguments()}, .hollow())\n" +
               $"    {RoutineName(routine: start)}()\n" +
               "    Standard/Os/set_exit_code(c_rf_exit_code())\n" +
               "    return\n";
    }
}
