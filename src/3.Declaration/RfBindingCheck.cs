using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// Checks every <c>@rf("...")</c> declaration of the standard library against the RazorForge declaration it names. Such
/// a declaration is how Suflae's library shows a shared type or routine in Suflae's own words: it creates nothing and
/// binds to the shared declaration, so the documentation, hover and go-to-definition show Suflae's view of it. A name
/// that resolves to nothing, to another kind of declaration, or to a routine with other parameters means the two
/// libraries have drifted apart, which <c>validate-stdlib</c> reports.
/// </summary>
internal static class RfBindingCheck
{
    /// <summary>The annotation that binds a declaration to a RazorForge one, as the parser stores it.</summary>
    private const string Prefix = "rf(";

    /// <summary>Whether the declaration is an <c>@rf("...")</c> binding.</summary>
    internal static bool IsBinding(SyntaxTree.Declaration declaration)
    {
        return AnnotationsOf(declaration: declaration)
                  ?.Any(predicate: a => a.StartsWith(value: Prefix, comparisonType: StringComparison.Ordinal)) == true;
    }

    /// <summary>The RazorForge name an <c>@rf("...")</c> declaration binds to, or null when it is written without one.</summary>
    internal static string? TargetOf(SyntaxTree.Declaration declaration)
    {
        string? annotation = AnnotationsOf(declaration: declaration)
                           ?.FirstOrDefault(predicate: a => a.StartsWith(value: Prefix,
                                comparisonType: StringComparison.Ordinal));
        if (annotation == null)
        {
            return null;
        }

        int open = annotation.IndexOf(value: '"');
        int close = annotation.LastIndexOf(value: '"');
        return open >= 0 && close > open + 1
            ? annotation[(open + 1)..close]
            : null;
    }

    /// <summary>
    /// Loads the module of every binding. A binding names a declaration of its own module, which the standard
    /// library loads only when something imports it, and a loaded module's routines get their signatures when its
    /// files are analyzed: so this runs before the library's bodies are validated, and <see cref="Check"/> after.
    /// </summary>
    internal static void LoadModules(TypeRegistry registry)
    {
        foreach ((SyntaxTree.Declaration declaration, string file, string module) in registry.RfBindings
                    .GroupBy(keySelector: b => b.Module)
                    .Select(selector: g => g.First())
                    .ToList())
        {
            if (!module.Equals(value: "Core", comparisonType: StringComparison.Ordinal))
            {
                registry.LoadModule(importPath: module, currentFile: file, location: declaration.Location,
                    effectiveModule: out _);
            }
        }
    }

    /// <summary>The problems found, one line each with the file and line of the <c>@rf</c> declaration.</summary>
    internal static List<string> Check(TypeRegistry registry)
    {
        var errors = new List<string>();
        foreach ((SyntaxTree.Declaration declaration, string file, string module) in registry.RfBindings.ToList())
        {
            string at = $"{file}:{declaration.Location.Line}";
            if (TargetOf(declaration: declaration) is not { } target)
            {
                errors.Add(item: $"{at}: @rf needs the RazorForge name in quotes, like @rf(\"S64\").");
                continue;
            }

            string? problem = declaration is RoutineDeclaration routine
                ? CheckRoutine(registry: registry, routine: routine, target: target, module: module)
                : CheckType(registry: registry, declaration: declaration, target: target, module: module);
            if (problem != null)
            {
                errors.Add(item: $"{at}: {problem}");
            }
        }

        return errors;
    }

    /// <summary>
    /// What Suflae's programs may use of RazorForge's library: the name of each bound type, and each bound routine as
    /// its call shape (see <see cref="ShapeOf(RoutineInfo)"/>). A protocol binding lends the shapes of the routines it
    /// lists.
    /// </summary>
    internal static HashSet<string> BoundNames(TypeRegistry registry)
    {
        var bound = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach ((SyntaxTree.Declaration declaration, _, _) in registry.RfBindings)
        {
            if (TargetOf(declaration: declaration) is not { } target)
            {
                continue;
            }

            if (declaration is RoutineDeclaration routine)
            {
                bound.Add(item: Shape(target: target,
                    parameters: routine.Parameters.Select(selector: p => p.Name)));
                continue;
            }

            bound.Add(item: target);
            if (declaration is ProtocolDeclaration protocol)
            {
                foreach (RoutineSignature signature in protocol.MemberRoutines)
                {
                    bound.Add(item: Shape(target: $"{target}.{WithoutMe(name: signature.Name)}",
                        parameters: signature.Parameters.Select(selector: p => p.Name)));
                }
            }
        }

        return bound;
    }

    /// <summary>
    /// A routine's call shape: <c>Type(a,b)</c> for a creator, <c>Type.name(a,b)</c> for a member routine and
    /// <c>name(a,b)</c> for a free one, the parameter names without <c>me</c>. Two overloads that differ only in
    /// parameter types share a shape, which is fine: a binding shows a routine by how it is called.
    /// </summary>
    internal static string ShapeOf(RoutineInfo routine)
    {
        return ShapeOf(routine: routine, ownerName: (routine.GenericDefinition ?? routine).OwnerType?.BareName);
    }

    /// <summary>
    /// The call shape with another owner: a routine a protocol declares (<c>Iterable[T].select</c>) is resolved
    /// as the receiver's own (<c>List.select</c>), and is bound under the protocol's name.
    /// </summary>
    internal static string ShapeOf(RoutineInfo routine, string? ownerName)
    {
        RoutineInfo definition = routine.GenericDefinition ?? routine;
        string name = WithoutMe(name: TypeSymbol.StripTypeArgs(name: definition.Name));
        string target = ownerName != null
            ? definition.IsCreator
                ? ownerName
                : $"{ownerName}.{name}"
            : name;
        return Shape(target: target, parameters: definition.Parameters.Select(selector: p => p.Name));
    }

    private static string Shape(string target, IEnumerable<string> parameters)
    {
        return $"{target}({string.Join(separator: ",", values: parameters.Where(predicate: n => n != "me"))})";
    }

    private static string WithoutMe(string name)
    {
        return name.StartsWith(value: "Me.", comparisonType: StringComparison.Ordinal)
            ? name["Me.".Length..]
            : name;
    }

    private static List<string>? AnnotationsOf(SyntaxTree.Declaration declaration)
    {
        return declaration switch
        {
            RoutineDeclaration routine => routine.Annotations,
            RecordDeclaration record => record.Annotations,
            _ => declaration.LeadingAnnotations
        };
    }

    /// <summary>The shared type a name means: as written, or in the binding file's module.</summary>
    private static TypeSymbol? FindType(TypeRegistry registry, string name, string module)
    {
        return registry.LookupType(name: name) ?? registry.LookupType(name: $"{module}.{name}");
    }

    /// <summary>A type binding: the RazorForge type exists, is the same kind, and has every member variable, case,
    /// flag or protocol routine the declaration lists (a declaration may list fewer: it shows what the language
    /// shows).</summary>
    private static string? CheckType(TypeRegistry registry, SyntaxTree.Declaration declaration, string target,
        string module)
    {
        if (FindType(registry: registry, name: target, module: module) is not { } type)
        {
            return $"@rf(\"{target}\") names no RazorForge type.";
        }

        (string Kind, bool Same) kind = declaration switch
        {
            RecordDeclaration => ("record", type is RecordTypeSymbol and not (VariantTypeSymbol or CrashableTypeSymbol
                or ChoiceTypeSymbol or FlagsTypeSymbol)),
            EntityDeclaration => ("entity", type is EntityTypeSymbol),
            CrashableDeclaration => ("crashable", type is CrashableTypeSymbol),
            ChoiceDeclaration => ("choice", type is ChoiceTypeSymbol),
            FlagsDeclaration => ("flags type", type is FlagsTypeSymbol),
            VariantDeclaration => ("variant", type is VariantTypeSymbol),
            ProtocolDeclaration => ("protocol", type is ProtocolTypeSymbol),
            _ => ("declaration", false)
        };
        if (!kind.Same)
        {
            return $"@rf(\"{target}\") on a {kind.Kind} names a {type.Category} in RazorForge.";
        }

        List<SyntaxTree.Declaration>? members = declaration switch
        {
            RecordDeclaration record => record.Members,
            EntityDeclaration entity => entity.Members,
            CrashableDeclaration crashable => crashable.Members,
            _ => null
        };
        List<MemberVariableInfo> sharedFields = type switch
        {
            RecordTypeSymbol record => record.MemberVariables.ToList(),
            EntityTypeSymbol entity => entity.MemberVariables.ToList(),
            _ => []
        };
        foreach (VariableDeclaration field in members?.OfType<VariableDeclaration>() ?? [])
        {
            MemberVariableInfo? match = sharedFields.FirstOrDefault(predicate: m => m.Name == field.Name);
            if (match == null)
            {
                return $"'{field.Name}' is not a member variable of RazorForge's {target}.";
            }

            if (!SameType(written: field.Type, shared: match.Type))
            {
                return $"'{field.Name}' is {match.Type.Name} in RazorForge's {target}, not {field.Type?.Name}.";
            }
        }

        switch (declaration)
        {
            case ChoiceDeclaration choice when type is ChoiceTypeSymbol sharedChoice:
                foreach (ChoiceCase choiceCase in choice.Cases.Where(predicate: c =>
                             sharedChoice.Cases.All(predicate: s => s.Name != c.Name)))
                {
                    return $"'{choiceCase.Name}' is not a case of RazorForge's {target}.";
                }

                break;
            case FlagsDeclaration flags when type is FlagsTypeSymbol sharedFlags:
                foreach (string flag in flags.Members.Where(predicate: f =>
                             sharedFlags.Members.All(predicate: s => s.Name != f)))
                {
                    return $"'{flag}' is not a flag of RazorForge's {target}.";
                }

                break;
            // A protocol of a module loaded on demand is only its shell until a program using it is analyzed: its
            // routines are checked once they are there.
            case ProtocolDeclaration protocol when type is ProtocolTypeSymbol { MemberRoutines.Count: > 0 } sharedProtocol:
                foreach (RoutineSignature signature in protocol.MemberRoutines)
                {
                    List<string> names = signature.Parameters.Where(predicate: p => p.Name != "me")
                                                  .Select(selector: p => p.Name)
                                                  .ToList();
                    // A protocol routine is written on `Me` (`routine Me.eq(you: Me)`): the name is what follows.
                    string name = signature.Name.StartsWith(value: "Me.", comparisonType: StringComparison.Ordinal)
                        ? signature.Name["Me.".Length..]
                        : signature.Name;
                    // The parameter names are compared once the protocol's routines have them (a protocol of a module
                    // loaded on demand may not have them filled in yet).
                    if (!sharedProtocol.MemberRoutines.Any(predicate: m =>
                            (m.Name == name || m.Name == "Me." + name) && (m.ParameterNames.Count == 0 ||
                                               m.ParameterNames.Where(predicate: n => n != "me")
                                                .SequenceEqual(second: names))))
                    {
                        return $"RazorForge's {target} has no routine {signature.Name}" +
                               $"({string.Join(separator: ", ", values: names)}).";
                    }
                }

                break;
        }

        return null;
    }

    /// <summary>Whether a type written in the declaration names the shared type, compared by name without type
    /// arguments. A missing written type matches anything.</summary>
    private static bool SameType(TypeExpression? written, TypeSymbol? shared)
    {
        return written == null || shared == null || written.Name == shared.BareName || written.Name == shared.Name ||
               TypeSymbol.StripTypeArgs(name: written.Name) == shared.BareName;
    }

    private static string? CheckRoutine(TypeRegistry registry, RoutineDeclaration routine, string target,
        string module)
    {
        // `Type.name` is a member routine. A bare name is the type's creator when the routine is the type's
        // (`routine Duration(...)`), else a free routine (`show`, or `Module.show`).
        List<RoutineInfo> named;
        string what;
        int dot = target.LastIndexOf(value: '.');
        TypeSymbol? owner = dot > 0
            ? FindType(registry: registry, name: target[..dot], module: module)
            : null;
        if (owner != null)
        {
            string name = target[(dot + 1)..];
            named = registry.GetMemberRoutinesForType(type: owner)
                            .Where(predicate: r => r.Name == name)
                            .ToList();
            // A routine another module writes on the type (`Integer.from_digit_bytes` in IO/BytesIO), or one on an
            // instance of a generic type (`Iterable[Text].join`), is not among the type's own: look for it by owner.
            if (named.Count == 0)
            {
                named = registry.GetAllRoutines(requireLive: false)
                                .Where(predicate: r => r.Name == name && r.OwnerType?.BareName == owner.BareName)
                                .ToList();
            }
            what = $"{target[..dot]}.{name}";
        }
        else if (routine.OwnerName == null && FindType(registry: registry, name: target, module: module) is { } created)
        {
            named = [];
            registry.CollectCreatorCandidates(type: created, candidates: named);
            what = $"the creator {target}";
        }
        else
        {
            string name = dot > 0
                ? target[(dot + 1)..]
                : target;
            string? routineModule = dot > 0
                ? target[..dot]
                : null;
            named = registry.GetAllRoutines(requireLive: false)
                            .Where(predicate: r => r.OwnerType == null && r.Name == name &&
                                                   (routineModule == null || r.Module == routineModule))
                            .ToList();
            what = target;
        }

        // An owner of another module than the binding's (`Integer`, in Numerics, seen from IO/BytesIO) is not found by
        // name from here: the routine is, by its owner's name.
        if (named.Count == 0 && dot > 0)
        {
            string ownerName = target[..dot];
            string name = target[(dot + 1)..];
            named = registry.GetAllRoutines(requireLive: false)
                            .Where(predicate: r => r.Name == name && r.OwnerType?.BareName == ownerName)
                            .ToList();
            what = target;
        }

        if (named.Count == 0)
        {
            return $"RazorForge has no routine {what}.";
        }

        List<string> parameters = routine.Parameters.Where(predicate: p => p.Name != "me")
                                         .Select(selector: p => p.Name)
                                         .ToList();
        List<RoutineInfo> shaped = named.Where(predicate: r => r.Parameters.Where(predicate: p => p.Name != "me")
                                                               .Select(selector: p => p.Name)
                                                               .SequenceEqual(second: parameters))
                                        .ToList();
        if (shaped.Count == 0)
        {
            return $"RazorForge's {what} takes no ({string.Join(separator: ", ", values: parameters)}).";
        }

        // Parameter and return types are compared by name as written, without type arguments. A creator's return
        // is its type, so only a written return of another routine is compared.
        List<Parameter> writtenParameters = routine.Parameters.Where(predicate: p => p.Name != "me")
                                                   .ToList();
        bool matches = shaped.Any(predicate: r =>
        {
            List<ParamInfo> sharedParameters = r.Parameters.Where(predicate: p => p.Name != "me")
                                                .ToList();
            return writtenParameters.Select(selector: (p, i) => SameType(written: p.Type,
                                        shared: sharedParameters[index: i].Type))
                                    .All(predicate: same => same) &&
                   (r.IsCreator || SameType(written: routine.ReturnType, shared: r.ReturnType));
        });
        return matches
            ? null
            : $"RazorForge's {what} has other parameter or return types than this declaration.";
    }
}
