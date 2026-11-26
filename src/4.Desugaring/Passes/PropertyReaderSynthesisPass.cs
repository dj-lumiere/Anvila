using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Desugaring.Passes;

/// <summary>
/// A protocol may require a <c>@property</c> (<c>@property routine Me.hp() -> S64</c>), and a member variable of the
/// same name fills it (<c>hp: S64</c>). For each record or entity that obeys such a protocol and has the member
/// variable but writes no routine of that name, this pass adds the routine that reads it:
/// <code>
/// @property
/// routine Slime.hp() -> S64
///     return me.hp
/// </code>
/// The routine is an ordinary declaration of the program from then on: it is analyzed, checked against the protocol
/// and lowered like one the program wrote. A read of the member variable on the type itself still reads the member
/// variable. The routine is what a stored value of the protocol and a type parameter bound by it call.
/// </summary>
internal static class PropertyReaderSynthesisPass
{
    internal static void Run(List<(Program Program, string FilePath)> files, TypeRegistry registry)
    {
        // Several modules may each declare a type or a protocol of the same name: a file's own module comes first.
        var written = new Dictionary<(string Module, string Name), ProtocolDeclaration>();
        var writtenAnywhere = new Dictionary<string, ProtocolDeclaration>(comparer: StringComparer.Ordinal);
        var routines = new HashSet<(string Module, string Owner, string Name)>();
        foreach ((Program program, string filePath) in files)
        {
            string module = ModuleOf(program: program, filePath: filePath);
            foreach (ProtocolDeclaration protocol in program.Declarations.OfType<ProtocolDeclaration>())
            {
                written.TryAdd(key: (module, protocol.Name), value: protocol);
                writtenAnywhere.TryAdd(key: protocol.Name, value: protocol);
            }

            foreach (RoutineDeclaration routine in program.Declarations.OfType<RoutineDeclaration>())
            {
                if (routine is { OwnerName: { } owner, MemberRoutineName: { } member })
                {
                    routines.Add(item: (module, owner, member));
                }
            }
        }

        foreach ((Program program, string filePath) in files)
        {
            string module = ModuleOf(program: program, filePath: filePath);
            var added = new List<RoutineDeclaration>();
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                (string Name, List<string>? Generics, List<TypeExpression> Protocols, List<SyntaxTree.Declaration> Members)?
                    type = node switch
                    {
                        RecordDeclaration r => (r.Name, r.GenericParameters, r.Protocols, r.Members),
                        EntityDeclaration e => (e.Name, e.GenericParameters, e.Protocols, e.Members),
                        _ => null
                    };
                if (type is not { } t || t.Protocols.Count == 0)
                {
                    continue;
                }

                var required = new HashSet<string>(comparer: StringComparer.Ordinal);
                var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
                foreach (TypeExpression obeyed in t.Protocols)
                {
                    CollectProperties(name: obeyed.Name, module: module, written: written,
                        writtenAnywhere: writtenAnywhere, registry: registry, seen: seen, required: required);
                }

                foreach (VariableDeclaration field in t.Members.OfType<VariableDeclaration>())
                {
                    if (field.Type == null || !required.Contains(item: field.Name) ||
                        routines.Contains(item: (module, t.Name, field.Name)) ||
                        t.Members.OfType<RoutineDeclaration>().Any(predicate: r => r.Name == field.Name))
                    {
                        continue;
                    }

                    added.Add(item: Reader(owner: t.Name, generics: t.Generics, field: field));
                    routines.Add(item: (module, t.Name, field.Name));
                }
            }

            program.Declarations.AddRange(collection: added);
        }
    }

    /// <summary>Adds the names of the <c>@property</c> routines <paramref name="name"/> requires, its parents' too.
    /// A protocol of these files is read from its declaration, one of the standard library from the registry.</summary>
    private static void CollectProperties(string name, string module,
        Dictionary<(string Module, string Name), ProtocolDeclaration> written,
        Dictionary<string, ProtocolDeclaration> writtenAnywhere, TypeRegistry registry, HashSet<string> seen,
        HashSet<string> required)
    {
        if (!seen.Add(item: name))
        {
            return;
        }

        if ((written.GetValueOrDefault(key: (module, name)) ?? writtenAnywhere.GetValueOrDefault(key: name)) is
            { } declaration)
        {
            foreach (RoutineSignature signature in declaration.MemberRoutines)
            {
                if (signature.Annotations?.Contains(item: RoutineInfo.PropertyAnnotation) == true)
                {
                    required.Add(item: signature.Name.StartsWith(value: "Me.", comparisonType: StringComparison.Ordinal)
                        ? signature.Name[3..]
                        : signature.Name);
                }
            }

            foreach (TypeExpression parent in declaration.ParentProtocols)
            {
                CollectProperties(name: parent.Name, module: module, written: written,
                    writtenAnywhere: writtenAnywhere, registry: registry, seen: seen, required: required);
            }

            return;
        }

        if (registry.LookupType(name: name) is ProtocolTypeSymbol library)
        {
            CollectLibraryProperties(protocol: library, seen: seen, required: required);
        }
    }

    private static void CollectLibraryProperties(ProtocolTypeSymbol protocol, HashSet<string> seen,
        HashSet<string> required)
    {
        foreach (ProtocolMemberRoutineInfo routine in protocol.MemberRoutines.Where(predicate: r => r.IsProperty))
        {
            required.Add(item: routine.Name);
        }

        foreach (ProtocolTypeSymbol parent in protocol.ParentProtocols)
        {
            if (seen.Add(item: parent.Name))
            {
                CollectLibraryProperties(protocol: parent, seen: seen, required: required);
            }
        }
    }

    /// <summary>The module a file declares (<c>module Path</c>), or the file itself when it declares none.</summary>
    private static string ModuleOf(Program program, string filePath)
    {
        return program.Declarations.OfType<ModuleDeclaration>().FirstOrDefault()?.Path ?? filePath;
    }

    /// <summary><c>@property routine Owner[T].name() -> Type</c> whose body returns <c>me.name</c>.</summary>
    private static RoutineDeclaration Reader(string owner, List<string>? generics, VariableDeclaration field)
    {
        SourceLocation loc = field.Location;
        bool generic = generics is { Count: > 0 };
        var body = new BlockStatement(
            Statements:
            [
                new ReturnStatement(
                    Value: new MemberExpression(Object: new IdentifierExpression(Name: "me", Location: loc),
                        MemberName: field.Name,
                        Location: loc),
                    Location: loc)
            ],
            Location: loc);
        return new RoutineDeclaration(Name: field.Name,
            Parameters: [],
            ReturnType: field.Type,
            Body: body,
            // The protocol's routine is called wherever the protocol is, whatever the member variable's own visibility.
            Visibility: VisibilityModifier.Open,
            Annotations: [RoutineInfo.PropertyAnnotation],
            Location: loc,
            GenericParameters: generic ? [.. generics!] : null)
        {
            IsBuilderWritten = true,
            OwnerName = owner,
            MemberRoutineName = field.Name,
            HasReceiverTypeArgs = generic,
            RenderedReceiver = generic ? $"{owner}[{string.Join(separator: ", ", values: generics!)}]" : owner,
            ReceiverType = new TypeExpression(Name: owner,
                GenericArguments: generic
                    ? generics!.Select(selector: g => new TypeExpression(Name: g, GenericArguments: null, Location: loc))
                               .ToList()
                    : null,
                Location: loc)
        };
    }
}
