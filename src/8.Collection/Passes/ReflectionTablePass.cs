using System.Globalization;
using System.Text;
using Builder.Desugaring;
using Builder.Instantiation;
using Builder.Instantiation.Passes;
using SyntaxTree;
using TypeModel;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Collection.Passes;

/// <summary>
/// Writes ObjectHacker's routine tables (<c>call</c>, <c>create</c>, <c>types(in:)</c>, <c>routines(in:)</c>) once the
/// demand walk has settled. A table lists only routines the walk reached, so it is written after the walk: written
/// during it, its dispatch calls would make every routine of a type reached. Each table routine is declared in the
/// library (<c>ObjectHacker/Reflect.rf</c>) with a body that lists nothing, and the walk builds it like any other
/// routine. Afterwards this pass gives each reached table the body for what the walk reached, analyzes and lowers
/// it, and the caller walks once more, so the helpers a dispatch body calls (argument conversions, the
/// <c>Unknown</c> of a result type) are built too. A rewritten table can make more types described (the type of a
/// parameter), so the caller repeats until no table changes.
/// <para>Tables cover the types and free routines of the program's own files. A library type lists nothing.</para>
/// </summary>
internal sealed class ReflectionTablePass(InstantiationContext ctx, DesugaringContext adapter)
{
    private const string ModuleName = "ObjectHacker";
    private const string ReflectedRoutines = "reflected_routines";
    private const string ReflectedInvoke = "reflected_invoke";
    private const string ReflectedCreators = "reflected_creators";
    private const string ReflectedCreate = "reflected_create";
    private const string ReachedTypes = "reached_types";
    private const string ReachedRoutines = "reached_routines";
    private const string ReachedInvoke = "reached_invoke";
    private const string ReflectedAnnotations = "reflected_annotations";

    /// <summary>The annotation that keeps every routine of a type in the program, so <c>call</c> reaches all of them.</summary>
    public const string ReflectableAnnotation = "reflectable";

    private static readonly HashSet<string> TableNames =
    [
        ReflectedRoutines, ReflectedInvoke, ReflectedCreators, ReflectedCreate, ReachedTypes, ReachedRoutines,
        ReachedInvoke, ReflectedAnnotations
    ];

    /// <summary>The text each table was last written from, by routine key: a table written again from the same
    /// text is left as it is.</summary>
    private readonly Dictionary<string, string> _written = new(comparer: StringComparer.Ordinal);

    private readonly Dictionary<string, string> _sourceTexts = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Writes every reached table whose content changed since it was last written. Returns whether any did, so the
    /// caller walks again.
    /// </summary>
    public bool Rebuild()
    {
        var fresh = new Dictionary<string, MonomorphizedBody>(comparer: StringComparer.Ordinal);
        foreach ((string key, MonomorphizedBody body) in ctx.InstantiatedGenericBodies.ToList())
        {
            if (!IsTable(info: body.Info))
            {
                continue;
            }

            var subs = new Substitutions();
            string? text = body.Info.Name switch
            {
                ReflectedRoutines => RoutineFactsText(owner: OwnerOf(info: body.Info), subs: subs),
                ReflectedInvoke => InvokeText(owner: OwnerOf(info: body.Info), subs: subs),
                ReflectedCreators => CreatorFactsText(owner: OwnerOf(info: body.Info), subs: subs),
                ReflectedCreate => CreateText(owner: OwnerOf(info: body.Info), subs: subs),
                ReachedTypes => ReachedTypesText(subs: subs),
                ReachedRoutines => ReachedRoutinesText(subs: subs),
                ReachedInvoke => ReachedInvokeText(subs: subs),
                ReflectedAnnotations => TypeAnnotationsText(owner: OwnerOf(info: body.Info), subs: subs),
                _ => null
            };
            if (text == null || _written.TryGetValue(key: key, value: out string? before) && before == text)
            {
                continue;
            }

            _written[key: key] = text;
            if (Environment.GetEnvironmentVariable(variable: "RF_DEBUG_REFLECT") is { Length: > 0 })
            {
                Console.Error.WriteLine(value: $"[reflect] {key}\n{text}");
            }

            Statement written = Parse(text: text, info: body.Info);
            written = GenericAstRewriter.RewriteStatement(stmt: written,
                subs: subs.Names,
                typeSubs: subs.Types,
                registry: ctx.Registry,
                enclosingRoutine: body.Info);
            written = ctx.AnalyzeMaterializedDeriveBody?.Invoke(arg1: body.Info, arg2: written) ?? written;
            fresh[key: key] = body with
            {
                Ast = body.Ast with
                {
                    Body = written as BlockStatement ??
                           new BlockStatement(Statements: [written], Location: written.Location)
                },
                IsSynthesized = false
            };
        }

        if (fresh.Count == 0)
        {
            return false;
        }

        GenericClosurePass.LowerFreshBodies(ctx: ctx, adapter: adapter, freshBodies: fresh);
        foreach ((string key, MonomorphizedBody body) in fresh)
        {
            ctx.InstantiatedGenericBodies[key: key] = body;
        }

        return true;
    }

    /// <summary>The routines the walk must start from so a <c>@reflectable</c> type keeps every routine it has, and a
    /// routine carrying one of the program's own annotations is there for the annotation to find.</summary>
    public static IEnumerable<(string Key, Statement Body)> ReflectableSeeds(InstantiationContext ctx)
    {
        foreach ((Program program, _, _) in ctx.UserPrograms)
        foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
        {
            if (decl is not { ResolvedInfo: { } info, Body: { } body } ||
                info.GenericParameters is { Count: > 0 })
            {
                continue;
            }

            if (info.OwnerType?.Annotations?.Contains(item: ReflectableAnnotation) == true ||
                ProgramAnnotationsOf(annotations: decl.Annotations).Count > 0)
            {
                yield return (info.RegistryKey, body);
            }
        }
    }

    /// <summary>The program's own annotations among <paramref name="annotations"/> (<c>Memoize(limit=100)</c>).</summary>
    private static List<string> ProgramAnnotationsOf(IEnumerable<string>? annotations)
    {
        return annotations?.Where(predicate: a =>
                               Parser.Parser.IsProgramAnnotation(name: Parser.Parser.AnnotationName(annotation: a),
                                   language: Language.Suflae))
                          .ToList() ?? [];
    }

    private static bool IsTable(RoutineInfo info)
    {
        RoutineInfo definition = info.GenericDefinition ?? info;
        return TableNames.Contains(item: info.Name) && definition.Module == ModuleName &&
               definition.Location?.FileName is { } file &&
               file.EndsWith(value: "Reflect.rf", comparisonType: StringComparison.Ordinal);
    }

    /// <summary>The type a per-type table is for (its one type argument), and the type whose routines it lists: a
    /// Suflae entity's handle lists the entity's routines.</summary>
    private (TypeSymbol Value, TypeSymbol Declared)? OwnerOf(RoutineInfo info)
    {
        if (info.TypeArguments is not [{ } value])
        {
            return null;
        }

        if (value.BareName == Builder.Declaration.RuntimeContract.Roamed && value.TypeArguments is [{ } inner])
        {
            return (value, inner);
        }

        return (HandleOf(type: value), value);
    }

    /// <summary>What a Suflae program holds of a type: a handle to an entity, the value itself otherwise. ObjectHacker
    /// is Suflae's, so every entity it meets is held this way.</summary>
    private TypeSymbol HandleOf(TypeSymbol type)
    {
        return type is EntityTypeSymbol &&
               ctx.Registry.LookupType(name: Builder.Declaration.RuntimeContract.Roamed) is { } roamed
            ? ctx.Registry.GetOrCreateResolution(genericDef: roamed, typeArguments: [type])
            : type;
    }

    // ============================================================================
    // What the program declares
    // ============================================================================

    private bool IsUserType(TypeSymbol type)
    {
        string? file = type.Location?.FileName;
        return file != null && ctx.UserPrograms.Any(predicate: p => p.FilePath == file);
    }

    /// <summary>The program's own routine declarations, with what the builder knows of each.</summary>
    private IEnumerable<(RoutineDeclaration Decl, RoutineInfo Info)> UserRoutines()
    {
        foreach ((Program program, _, _) in ctx.UserPrograms)
        foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
        {
            if (decl.ResolvedInfo is { } info && info.Name != "start" && !info.IsSynthesized &&
                !decl.Annotations.Contains(item: "test") &&
                !Builder.Declaration.RuntimeContract.ProgramAnnotation.IsWritten(name: info.Name))
            {
                yield return (decl, info);
            }
        }
    }

    private bool IsBuilt(RoutineInfo info)
    {
        return ctx.LiveRoutineKeys.Contains(item: info.RegistryKey);
    }

    private static bool IsGeneric(RoutineInfo info)
    {
        return info.GenericParameters is { Count: > 0 };
    }

    private static string KindOf(RoutineInfo info)
    {
        return info.Kind switch
        {
            RoutineKind.Creator => "CREATOR",
            RoutineKind.CommonRoutine => "COMMON",
            RoutineKind.MemberRoutine => "MEMBER",
            _ => "FREE"
        };
    }

    private static bool SameType(TypeSymbol? a, TypeSymbol b)
    {
        return a != null && a.FullName == b.FullName;
    }

    /// <summary>The member routines and common routines of a type, in the order the program declares them.</summary>
    private List<RoutineInfo> RoutinesOf(TypeSymbol declared)
    {
        return UserRoutines()
              .Where(predicate: r => SameType(a: r.Info.OwnerType, b: declared) &&
                                     r.Info.Kind is RoutineKind.MemberRoutine or RoutineKind.CommonRoutine)
              .Select(selector: r => r.Info)
              .ToList();
    }

    /// <summary>The creators a type has: the one the builder gives every record and entity (one argument per member
    /// variable, null here), then the ones the program declares.</summary>
    private List<RoutineInfo?> CreatorsOf(TypeSymbol declared)
    {
        var creators = new List<RoutineInfo?>();
        if (DeclaresMemberwiseCreator(type: declared))
        {
            creators.Add(item: null);
        }

        creators.AddRange(collection: UserRoutines()
                                     .Where(predicate: r => SameType(a: r.Info.OwnerType, b: declared) &&
                                                            r.Info.Kind == RoutineKind.Creator)
                                     .Select(selector: r => (RoutineInfo?)r.Info));
        return creators;
    }

    /// <summary>Whether the program declares <paramref name="type"/> as a record, an entity or a crashable, the kinds
    /// that are made from one value per member variable.</summary>
    private bool DeclaresMemberwiseCreator(TypeSymbol type)
    {
        // A choice, a flags type and a variant reach this list as records (structural lowering writes them so),
        // and none of them is made from member variables.
        if (type is ChoiceTypeSymbol or FlagsTypeSymbol or VariantTypeSymbol or TupleTypeSymbol)
        {
            return false;
        }

        foreach ((Program program, _, string module) in ctx.UserPrograms)
        foreach (SyntaxTree.Declaration decl in program.Declarations.OfType<SyntaxTree.Declaration>())
        {
            string? name = decl switch
            {
                RecordDeclaration r => r.Name,
                EntityDeclaration e => e.Name,
                CrashableDeclaration c => c.Name,
                _ => null
            };
            if (name != null && type.Name == name && (type.Module ?? "") == module)
            {
                return true;
            }
        }

        return false;
    }

    private static List<MemberVariableInfo> MemberVariablesOf(TypeSymbol type)
    {
        return type switch
        {
            RecordTypeSymbol record => record.MemberVariables,
            EntityTypeSymbol entity => entity.MemberVariables,
            _ => []
        };
    }

    private static List<ParamInfo> ParametersOf(RoutineInfo info)
    {
        return info.Parameters.Where(predicate: p => p.Name != "me").ToList();
    }

    // ============================================================================
    // Writing the tables
    // ============================================================================

    /// <summary>The placeholder names a table's text uses for types and routines, which the AST rewriter replaces.</summary>
    private sealed class Substitutions
    {
        public Dictionary<string, string> Names { get; } = new(comparer: StringComparer.Ordinal);
        public Dictionary<string, TypeSymbol> Types { get; } = new(comparer: StringComparer.Ordinal);
        private readonly Dictionary<string, string> _byType = new(comparer: StringComparer.Ordinal);

        public string Type(TypeSymbol type)
        {
            if (_byType.TryGetValue(key: type.InstanceArgumentName, value: out string? known))
            {
                return known;
            }

            string name = $"Reflected_T{_byType.Count}";
            _byType[key: type.InstanceArgumentName] = name;
            Types[key: name] = type;
            return name;
        }

        /// <summary>A placeholder that names <paramref name="type"/> where a call constructs it.</summary>
        public string Creator(TypeSymbol type)
        {
            string name = $"Reflected_C{Names.Count}";
            Names[key: name] = type.FullName;
            return name;
        }

        public string Routine(RoutineInfo routine)
        {
            string name = $"reflected_r{Names.Count}";
            Names[key: name] = routine.Module is { Length: > 0 } module
                ? $"{module}.{routine.Name}"
                : routine.Name;
            return name;
        }
    }

    private static string Quote(string text)
    {
        return "\"" + text.Replace(oldValue: "\\", newValue: "\\\\")
                          .Replace(oldValue: "\"", newValue: "\\\"")
                          .Replace(oldValue: "{", newValue: "{{")
                          .Replace(oldValue: "}", newValue: "}}") + "\"";
    }

    private static string Bool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FullNameOf(RoutineInfo info)
    {
        return info.OwnerType is { } owner
            ? $"{owner.FullName}.{info.Name}"
            : info.Module is { Length: > 0 } module
                ? $"{module}.{info.Name}"
                : info.Name;
    }

    private static string? DisplayNameOf(RoutineInfo info)
    {
        return info.OwnerType is { } owner
            ? $"{owner.Name}.{info.Name}"
            : info.Name;
    }

    /// <summary>One <c>facts.add_last(...)</c> for a routine, with the list of its parameters before it.</summary>
    private void AppendFact(StringBuilder text, Substitutions subs, int index, string name, string fullName,
        string module, string kind, bool built, bool failable, bool generic, TypeSymbol? returns,
        List<(string Name, TypeSymbol Type, string? Default)> parameters, IEnumerable<string>? annotations = null)
    {
        string notes = $"n{index}";
        text.AppendLine(value: $"    var {notes} = new_unknown_list()");
        AppendAnnotationValues(text: text, subs: subs, list: notes, annotations: annotations);
        string list = $"p{index}";
        text.AppendLine(value: $"    var {list} = new_parameter_facts()");
        for (int i = 0; i < parameters.Count; i++)
        {
            (string pName, TypeSymbol pType, string? pDefault) = parameters[index: i];
            string t = subs.Type(type: pType);
            string defaultValue = "none";
            if (pDefault != null)
            {
                text.AppendLine(value: $"    var d{index}_{i}: {t} = {pDefault}");
                defaultValue = $"Unknown(from: d{index}_{i})";
            }

            text.AppendLine(value:
                $"    {list}.add_last(value: parameter_fact[{t}](name: {Quote(text: pName)}, order: {i}, has_default: {Bool(value: pDefault != null)}, default: {defaultValue}))");
        }

        bool hasReturns = returns != null && returns.Name != "None" && returns.Name != "Blank";
        string r = subs.Type(type: hasReturns ? returns! : ctx.Registry.LookupType(name: "Bool")!);
        text.AppendLine(value:
            $"    facts.add_last(value: routine_fact[{r}](name: {Quote(text: name)}, full_name: {Quote(text: fullName)}, module_name: {Quote(text: module)}, kind: {Quote(text: kind)}, index: {index}, is_built: {Bool(value: built)}, is_failable: {Bool(value: failable)}, is_generic: {Bool(value: generic)}, has_returns: {Bool(value: hasReturns)}, parameters: steal {list}, annotations: steal {notes}))");
    }

    /// <summary>Lines that add the value of each of the program's own annotations to <paramref name="list"/>.</summary>
    private void AppendAnnotationValues(StringBuilder text, Substitutions subs, string list,
        IEnumerable<string>? annotations)
    {
        foreach (string annotation in ProgramAnnotationsOf(annotations: annotations))
        {
            if (AnnotationType(name: Parser.Parser.AnnotationName(annotation: annotation)) is not { } type)
            {
                continue;
            }

            text.AppendLine(value:
                $"    {list}.add_last(value: Unknown(from: {subs.Creator(type: type)}({Parser.Parser.ProgramAnnotationArguments(annotation: annotation)})))");
        }
    }

    /// <summary>The record a program's annotation names, looked for in the program's modules.</summary>
    private TypeSymbol? AnnotationType(string name)
    {
        foreach ((_, _, string module) in ctx.UserPrograms)
        {
            if ((string.IsNullOrEmpty(value: module)
                    ? ctx.Registry.LookupType(name: name)
                    : ctx.Registry.LookupType(name: $"{module}.{name}")) is RecordTypeSymbol record &&
                IsUserType(type: record))
            {
                return record;
            }
        }

        return null;
    }

    private string? TypeAnnotationsText((TypeSymbol Value, TypeSymbol Declared)? owner, Substitutions subs)
    {
        var text = new StringBuilder();
        text.AppendLine(value: "    var notes = new_unknown_list()");
        if (owner is { } o && IsUserType(type: o.Declared))
        {
            AppendAnnotationValues(text: text, subs: subs, list: "notes", annotations: o.Declared.Annotations);
        }

        text.AppendLine(value: "    return notes");
        return text.ToString();
    }

    private string? DefaultText(ParamInfo parameter)
    {
        if (parameter.DefaultValue?.Location is not { FileName: { Length: > 0 } file, Position: >= 0 } location)
        {
            return null;
        }

        if (!_sourceTexts.TryGetValue(key: file, value: out string? source))
        {
            try
            {
                source = File.ReadAllText(path: file);
            }
            catch (IOException)
            {
                return null;
            }

            _sourceTexts[key: file] = source;
        }

        // The default runs to the parameter list's next comma or its closing parenthesis, outside any brackets
        // or text of its own.
        int depth = 0;
        bool inText = false;
        int i = location.Position;
        for (; i < source.Length; i++)
        {
            char c = source[index: i];
            if (inText)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inText = false;
                }

                continue;
            }

            if (c == '"')
            {
                inText = true;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (c is ',' or '\n' && depth == 0)
            {
                break;
            }
        }

        string text = source[location.Position..i].Trim();
        return text.Length > 0 ? text : null;
    }

    private List<(string Name, TypeSymbol Type, string? Default)> DescribeParameters(RoutineInfo info)
    {
        return ParametersOf(info: info)
              .Select(selector: p => (p.Name, p.Type, p.DefaultValue != null ? DefaultText(parameter: p) : null))
              .ToList();
    }

    private string? RoutineFactsText((TypeSymbol Value, TypeSymbol Declared)? owner, Substitutions subs)
    {
        var text = new StringBuilder();
        text.AppendLine(value: "    var facts = new_routine_facts()");
        if (owner is { } o && IsUserType(type: o.Declared))
        {
            List<RoutineInfo> routines = RoutinesOf(declared: o.Declared);
            for (int i = 0; i < routines.Count; i++)
            {
                RoutineInfo r = routines[index: i];
                AppendFact(text: text, subs: subs, index: i, name: r.Name, fullName: FullNameOf(info: r),
                    module: o.Declared.Module ?? "", kind: KindOf(info: r), built: IsBuilt(info: r),
                    failable: r.IsFailable, generic: IsGeneric(info: r), returns: r.ReturnType,
                    parameters: DescribeParameters(info: r), annotations: r.Annotations);
            }
        }

        text.AppendLine(value: "    return facts");
        return text.ToString();
    }

    private string? CreatorFactsText((TypeSymbol Value, TypeSymbol Declared)? owner, Substitutions subs)
    {
        var text = new StringBuilder();
        text.AppendLine(value: "    var facts = new_routine_facts()");
        if (owner is { } o && IsUserType(type: o.Declared))
        {
            List<RoutineInfo?> creators = CreatorsOf(declared: o.Declared);
            for (int i = 0; i < creators.Count; i++)
            {
                RoutineInfo? r = creators[index: i];
                string full = $"{o.Declared.FullName}";
                if (r == null)
                {
                    AppendFact(text: text, subs: subs, index: i, name: o.Declared.Name, fullName: full,
                        module: o.Declared.Module ?? "", kind: "CREATOR", built: true, failable: false,
                        generic: false, returns: o.Value,
                        parameters: MemberVariablesOf(type: o.Declared)
                                   .Select(selector: m => (m.Name, m.Type, (string?)null))
                                   .ToList());
                    continue;
                }

                AppendFact(text: text, subs: subs, index: i, name: o.Declared.Name, fullName: full,
                    module: o.Declared.Module ?? "", kind: "CREATOR", built: IsBuilt(info: r),
                    failable: r.IsFailable, generic: IsGeneric(info: r), returns: o.Value,
                    parameters: DescribeParameters(info: r));
            }
        }

        text.AppendLine(value: "    return facts");
        return text.ToString();
    }

    /// <summary>Lines that read every argument of a call into a local of its parameter's type (<c>a0</c>, <c>a1</c>,
    /// ...), a parameter with a default keeping it when the call leaves the argument out. Returns the named
    /// argument list for the call.</summary>
    private string ReadArguments(StringBuilder text, Substitutions subs, string routine,
        List<(string Name, TypeSymbol Type, string? Default)> parameters, string indent)
    {
        var arguments = new List<string>();
        for (int i = 0; i < parameters.Count; i++)
        {
            (string name, TypeSymbol type, string? defaultText) = parameters[index: i];
            string t = subs.Type(type: type);
            string read = $"unknown_argument[{t}](args: args, name: {Quote(text: name)}, routine_name: {Quote(text: routine)})";
            if (defaultText == null)
            {
                text.AppendLine(value: $"{indent}var a{i} = {read}");
            }
            else
            {
                text.AppendLine(value: $"{indent}var a{i}: {t} = {defaultText}");
                text.AppendLine(value: $"{indent}if has_argument(args: args, name: {Quote(text: name)})");
                text.AppendLine(value: $"{indent}    a{i} = {read}");
            }

            arguments.Add(item: $"{name}: a{i}");
        }

        return string.Join(separator: ", ", values: arguments);
    }

    /// <summary>One <c>if index == i</c> branch: read the arguments, call, and return what the call gives as an
    /// <c>Unknown</c> (an <c>Unknown</c> keeping <c>none</c> when it gives nothing).</summary>
    private void AppendDispatch(StringBuilder text, Substitutions subs, int index, string call, string routine,
        TypeSymbol? returns, List<(string Name, TypeSymbol Type, string? Default)> parameters, bool generic,
        string keep = "Unknown(from: {0})")
    {
        text.AppendLine(value: $"    if index == {index}");
        if (generic)
        {
            // choose_routine never hands out a routine with type parameters of its own.
            text.AppendLine(value: "        return unknown_nothing()");
            return;
        }

        string arguments = ReadArguments(text: text, subs: subs, routine: routine, parameters: parameters,
            indent: "        ");
        string fullCall = $"{call}({arguments})";
        if (returns == null || returns.Name is "None" or "Blank")
        {
            text.AppendLine(value: $"        {fullCall}");
            text.AppendLine(value: "        return unknown_nothing()");
        }
        else
        {
            text.AppendLine(value: "        return " + string.Format(provider: CultureInfo.InvariantCulture, format: keep, arg0: fullCall));
        }
    }

    private string? InvokeText((TypeSymbol Value, TypeSymbol Declared)? owner, Substitutions subs)
    {
        var text = new StringBuilder();
        if (owner is { } o && IsUserType(type: o.Declared))
        {
            List<RoutineInfo> routines = RoutinesOf(declared: o.Declared);
            string self = subs.Type(type: o.Value);
            for (int i = 0; i < routines.Count; i++)
            {
                RoutineInfo r = routines[index: i];
                if (!IsBuilt(info: r))
                {
                    continue;
                }

                string call = r.Kind == RoutineKind.CommonRoutine
                    ? $"{subs.Type(type: o.Declared)}.{r.Name}"
                    : $"target.kept_as[{self}]().{r.Name}";
                AppendDispatch(text: text, subs: subs, index: i, call: call, routine: DisplayNameOf(info: r)!,
                    returns: r.ReturnType, parameters: DescribeParameters(info: r), generic: IsGeneric(info: r));
            }
        }

        text.AppendLine(value: "    return unknown_nothing()");
        return text.ToString();
    }

    private string? CreateText((TypeSymbol Value, TypeSymbol Declared)? owner, Substitutions subs)
    {
        var text = new StringBuilder();
        if (owner is { } o && IsUserType(type: o.Declared))
        {
            List<RoutineInfo?> creators = CreatorsOf(declared: o.Declared);
            string self = subs.Creator(type: o.Declared);
            for (int i = 0; i < creators.Count; i++)
            {
                RoutineInfo? r = creators[index: i];
                if (r != null && !IsBuilt(info: r))
                {
                    continue;
                }

                List<(string Name, TypeSymbol Type, string? Default)> parameters = r == null
                    ? MemberVariablesOf(type: o.Declared)
                     .Select(selector: m => (m.Name, m.Type, (string?)null))
                     .ToList()
                    : DescribeParameters(info: r);
                // The creator every entity has makes the entity itself, which a Suflae value is a handle to.
                string keep = r == null && o.Declared is EntityTypeSymbol &&
                              !ReferenceEquals(objA: o.Value, objB: o.Declared)
                    ? $"entity_unknown[{subs.Type(type: o.Declared)}](value: {{0}})"
                    : "Unknown(from: {0})";
                AppendDispatch(text: text, subs: subs, index: i, call: self, routine: o.Declared.Name,
                    returns: o.Value, parameters: parameters, generic: r != null && IsGeneric(info: r), keep: keep);
            }
        }

        text.AppendLine(value: "    return unknown_nothing()");
        return text.ToString();
    }

    /// <summary>The program's own types the walk reached: a type one of whose routines it reached, or one it made a
    /// value of.</summary>
    private List<TypeSymbol> ReachedUserTypes()
    {
        var found = new List<TypeSymbol>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach ((Program program, _, string module) in ctx.UserPrograms)
        foreach (SyntaxTree.Declaration decl in program.Declarations.OfType<SyntaxTree.Declaration>())
        {
            string? name = decl switch
            {
                RecordDeclaration r => r.Name,
                EntityDeclaration e => e.Name,
                ChoiceDeclaration c => c.Name,
                FlagsDeclaration f => f.Name,
                VariantDeclaration v => v.Name,
                CrashableDeclaration x => x.Name,
                _ => null
            };
            if (name == null)
            {
                continue;
            }

            TypeSymbol? type = (string.IsNullOrEmpty(value: module)
                ? null
                : ctx.Registry.LookupType(name: $"{module}.{name}")) ?? ctx.Registry.LookupType(name: name);
            if (type == null || type.IsGenericDefinition || !IsUserType(type: type) ||
                !seen.Add(item: type.FullName))
            {
                continue;
            }

            if (ctx.LiveOwnerTypeNames.Contains(item: type.FullName) ||
                ctx.LiveOwnerTypeNames.Contains(item: HandleOf(type: type).FullName))
            {
                found.Add(item: type);
            }
        }

        return found;
    }

    private string? ReachedTypesText(Substitutions subs)
    {
        var text = new StringBuilder();
        TypeSymbol? listType = ctx.InstantiatedGenericBodies.Values
                                  .FirstOrDefault(predicate: b => b.Info.Name == ReachedTypes && IsTable(info: b.Info))
                                 ?.Info.ReturnType;
        if (listType == null)
        {
            return null;
        }

        text.AppendLine(value: "    var facts = new_type_facts()");
        foreach (TypeSymbol type in ReachedUserTypes())
        {
            TypeSymbol value = HandleOf(type: type);
            text.AppendLine(value: $"    facts.add_last(value: type_fact_of[{subs.Type(type: value)}]())");
        }

        text.AppendLine(value: "    return facts");
        return text.ToString();
    }

    private List<RoutineInfo> FreeRoutines()
    {
        return UserRoutines()
              .Where(predicate: r => r.Info.Kind == RoutineKind.FreeRoutine && r.Info.OwnerType == null)
              .Select(selector: r => r.Info)
              .ToList();
    }

    private string? ReachedRoutinesText(Substitutions subs)
    {
        TypeSymbol? listType = ctx.InstantiatedGenericBodies.Values
                                  .FirstOrDefault(predicate: b => b.Info.Name == ReachedRoutines && IsTable(info: b.Info))
                                 ?.Info.ReturnType;
        if (listType == null)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine(value: "    var facts = new_routine_facts()");
        List<RoutineInfo> routines = FreeRoutines();
        for (int i = 0; i < routines.Count; i++)
        {
            RoutineInfo r = routines[index: i];
            AppendFact(text: text, subs: subs, index: i, name: r.Name, fullName: FullNameOf(info: r),
                module: r.Module ?? "", kind: "FREE", built: IsBuilt(info: r), failable: r.IsFailable,
                generic: IsGeneric(info: r), returns: r.ReturnType, parameters: DescribeParameters(info: r),
                annotations: r.Annotations);
        }

        text.AppendLine(value: "    return facts");
        return text.ToString();
    }

    private string? ReachedInvokeText(Substitutions subs)
    {
        var text = new StringBuilder();
        List<RoutineInfo> routines = FreeRoutines();
        for (int i = 0; i < routines.Count; i++)
        {
            RoutineInfo r = routines[index: i];
            if (!IsBuilt(info: r))
            {
                continue;
            }

            AppendDispatch(text: text, subs: subs, index: i, call: subs.Routine(routine: r), routine: r.Name,
                returns: r.ReturnType, parameters: DescribeParameters(info: r), generic: IsGeneric(info: r));
        }

        text.AppendLine(value: "    return unknown_nothing()");
        return text.ToString();
    }

    // ============================================================================
    // Parsing a table's text
    // ============================================================================

    private static Statement Parse(string text, RoutineInfo info)
    {
        string file = (info.GenericDefinition ?? info).Location?.FileName ?? "Reflect.rf";
        string source = $"module {ModuleName}\n\nroutine reflected_table()\n{text}";
        Language language = Builder.Frontends.Languages.OfFile(fileName: file);
        List<Builder.Tokenizer.Token> tokens =
            Builder.Tokenizer.Lexers.Tokenize(source: source, fileName: file, language: language);
        var parser = new Parser.Parser(tokens: tokens, language: language, fileName: file);
        Program program = parser.Parse();
        if (parser.GetErrors() is { Count: > 0 } errors)
        {
            throw new InvalidOperationException(
                message: $"The builder wrote a table for '{info.Name}' that does not parse ({errors[index: 0]}):\n{source}");
        }

        return program.Declarations.OfType<RoutineDeclaration>().Single().Body;
    }
}
