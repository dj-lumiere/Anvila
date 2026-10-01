using System.Text;
using Builder.Backends;
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

    public TesseraWriter(BackendInput input)
    {
        _input = input;
        foreach (string name in new[]
                 {
                     TesseraTrace.Push, TesseraTrace.Pop, TesseraTrace.UpdateLocation, "rf_runtime_init", "main",
                     "c_rf_set_trace_mode", "RfTraceFrame", "RF_TRACE_STACK", "RF_TRACE_DEPTH"
                 })
        {
            _usedNames.Add(item: name);
        }
    }

    /// <summary>Whether the build keeps a crash trace: the debug and release modes, as in the LLVM emitter.</summary>
    public bool Traces => _input.BuildMode is Targeting.RfBuildMode.Debug or Targeting.RfBuildMode.Release;

    /// <summary>Whether <paramref name="routine"/> pushes a trace frame: a routine of the program (not one the
    /// builder wrote), not <c>@inline</c> or <c>@untraced</c>, that can crash.</summary>
    public bool TracesRoutine(RoutineInfo routine)
    {
        return Traces && !routine.IsSynthesized && !routine.Annotations.Contains(value: "inline") &&
               !routine.Annotations.Contains(value: "untraced") && _crashReachability.CanCrash(routine: routine);
    }

    /// <summary>The Tessera names of the routines the module defines.</summary>
    public IReadOnlyCollection<string> DefinedRoutineNames => _routineNames.Values;

    /// <summary>Writes the module.</summary>
    public string Write()
    {
        List<(RoutineInfo Info, Statement Body)> routines = CollectRoutines();
        RegisterCrashReachability();
        foreach ((RoutineInfo info, Statement _) in routines)
        {
            _routineNames[key: LlvmEmitter.MangleRoutineName(routine: info)] =
                UniqueName(wanted: Sanitize(text: LlvmEmitter.MangleRoutineName(routine: info)));
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
        return _routineNames.TryGetValue(key: symbol, value: out string? name)
            ? name
            : throw new NotSupportedException(
                message: $"The Tessera backend found a call to {symbol}, which has no body in this build.");
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
            values: routine.Parameters.Select(selector: p => $"%{p.Name}: {TypeText(type: p.Type)}"));
        _externs.Append(value: $"#[external(\"c\"), symbol(\"{symbol}\")]\n" +
                               $"routine {name}({parameters}) -> {TypeText(type: routine.ReturnType)}\n\n");
        return name;
    }

    /// <summary>A C routine of the runtime the module calls by itself (not through a <c>C::</c> declaration),
    /// declared once under its own symbol.</summary>
    public string RuntimeRoutine(string symbol, string parameters, string returnType)
    {
        if (_externNames.TryGetValue(key: symbol, value: out string? name))
        {
            return name;
        }

        name = UniqueName(wanted: "c_" + Sanitize(text: symbol));
        _externNames[key: symbol] = name;
        _externs.Append(value: $"#[external(\"c\"), symbol(\"{symbol}\")]\n" +
                               $"routine {name}({parameters}) -> {returnType}\n\n");
        return name;
    }

    /// <summary>
    /// The global holding a text literal's characters, UTF-32 code points as the builder lays out every
    /// <c>Text</c>, declared once per distinct literal. Null for the empty text, which has no buffer.
    /// </summary>
    public string? TextData(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (_texts.TryGetValue(key: value, value: out string? name))
        {
            return name;
        }

        name = UniqueName(wanted: $"RF_TEXT_{_texts.Count}");
        _texts[key: value] = name;
        List<int> codePoints = value.EnumerateRunes()
                                    .Select(selector: rune => rune.Value)
                                    .ToList();
        string type = $"Array<U32, {codePoints.Count}>";
        _globals.Append(value: $"global {name}: @{type} <- {type} {{ {string.Join(separator: ", ", values: codePoints)} }}\n\n");
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
        IEnumerable<string> values = preset.Type is RecordTypeSymbol { BareName: "BitArray" } ||
                                     preset.Type.Name.StartsWith(value: "BitArray", comparisonType: StringComparison.Ordinal)
            ? PackBits(elements: elements)
            : elements.Select(selector: e => e is LiteralExpression literal
                ? TesseraRoutineWriter.ConstantText(literal: literal)
                : throw new NotSupportedException(
                    message: $"The Tessera backend found a non-literal element in the preset {preset.QualifiedName}."));
        _globals.Append(value: $"global {global}: @{type} <- {type} {{ {string.Join(separator: ", ", values: values)} }}\n\n");
        return (global, preset.Type);
    }

    /// <summary>Bool literals packed eight to a byte, lowest bit first, as a <c>BitArray</c> lays them out.</summary>
    private static IEnumerable<string> PackBits(List<Expression> elements)
    {
        for (int start = 0; start < elements.Count; start += 8)
        {
            int value = 0;
            for (int bit = 0; bit < 8 && start + bit < elements.Count; bit++)
            {
                if (elements[index: start + bit] is LiteralExpression { Value: true })
                {
                    value |= 1 << bit;
                }
            }

            yield return value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The Tessera record laid out like an entity's heap block (its fields in order, no header), declared
    /// on first use. An entity value is the block's address; a field is read through this record.</summary>
    public string EntityRecord(EntityTypeSymbol entity)
    {
        if (_entityRecords.TryGetValue(key: entity.FullName, value: out string? name))
        {
            return name;
        }

        name = UniqueName(wanted: Sanitize(text: entity.FullName) + "_Block");
        _entityRecords[key: entity.FullName] = name;
        var text = new StringBuilder();
        if (entity.MemberVariables.Count < 2)
        {
            text.Append(value: "#aggregate\n");
        }

        text.Append(value: $"record {name}\n");
        foreach (MemberVariableInfo field in entity.MemberVariables)
        {
            text.Append(value: $"    {field.Name} : {TypeText(type: field.Type)}\n");
        }

        _records.Append(value: text)
                .Append(value: '\n');
        return name;
    }

    private string UniqueName(string wanted)
    {
        string name = wanted;
        for (int i = 2; !_usedNames.Add(item: name); i++)
        {
            name = $"{wanted}_{i}";
        }

        return name;
    }

    /// <summary>A Tessera identifier for a builder name: letters and digits kept, every other run of
    /// characters one underscore, the routine attribute prefix (<c>[member] </c>) dropped.</summary>
    private static string Sanitize(string text)
    {
        text = text.Trim(trimChar: '"');
        string body = text.StartsWith(value: '[') && text.IndexOf(value: "] ", comparisonType: StringComparison.Ordinal)
            is var close and > 0
            ? text[(close + 2)..]
            : text;
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
                throw new NotSupportedException(
                    message: $"The Tessera backend does not translate variant types yet ({variant.FullName}).");
            case TupleTypeSymbol { ElementTypes.Count: >= 2 and <= 4 } tuple:
                return $"({string.Join(separator: ", ", values: tuple.ElementTypes.Select(selector: TypeText))})";
            case EntityTypeSymbol:
                return "Addr";
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
            "S8" or "S16" or "S32" or "S64" or "S128" or "U8" or "U16" or "U32" or "U64" or "U128" => record.BareName,
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

        if (backend == "ptr")
        {
            return "Addr";
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

        name = UniqueName(wanted: Sanitize(text: record.FullName));
        _recordNames[key: record.FullName] = name;
        var text = new StringBuilder();
        text.Append(value: $"record {name}\n");
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
        RoutineInfo start = routines.Select(selector: r => r.Info)
                                    .FirstOrDefault(predicate: r =>
                                         r.OwnerType == null &&
                                         LlvmEmitter.MangleRoutineName(routine: r)
                                                    .Trim(trimChar: '"')
                                                    .EndsWith(value: entry is { Length: > 0 }
                                                         ? $"{entry}.start()"
                                                         : ".start()",
                                                     comparisonType: StringComparison.Ordinal)) ??
                            throw new NotSupportedException(
                                message: $"The Tessera backend found no start() routine in the entry module '{entry}' " +
                                         "among: " + string.Join(separator: ", ",
                                             values: routines.Where(predicate: r => r.Info.OwnerType == null)
                                                             .Select(selector: r =>
                                                                  LlvmEmitter.MangleRoutineName(routine: r.Info))));

        return "#[external(\"c\"), symbol(\"rf_runtime_init\")]\n" +
               "routine rf_runtime_init() -> Void\n\n" +
               "#[external(\"c\"), symbol(\"__rf_set_trace_mode\")]\n" +
               "routine c_rf_set_trace_mode(%mode: S32) -> Void\n\n" +
               "routine main() -> S32\n" +
               "    block entry():\n" +
               "        rf_runtime_init()\n" +
               // Trace mode 2 is the shadow stack (debug and release), 0 none, as the LLVM emitter sets it.
               $"        c_rf_set_trace_mode({(Traces ? 2 : 0)})\n" +
               $"        {RoutineName(routine: start)}()\n" +
               "        return(0)\n";
    }
}
