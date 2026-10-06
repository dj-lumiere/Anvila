using Builder.Declaration;
using Builder.Diagnostics;
using SyntaxTree;
using TypeModel;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Suflae's programs see RazorForge's library only through Suflae's own: a type or routine of a RazorForge library
/// file is usable from a Suflae program when an <c>@rf</c> declaration of Suflae's library binds it. Everything else
/// the two languages share in the build (tokens, wrappers, the builder's helpers) stays out of reach.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>The bound names, rebuilt when a module loaded on demand brings more bindings.</summary>
    private HashSet<string>? _rfBoundNames;

    private int _rfBoundNamesFrom = -1;

    private HashSet<string> RfBoundNames()
    {
        int count = _registry.RfBindings.Count;
        if (_rfBoundNames == null || _rfBoundNamesFrom != count)
        {
            _rfBoundNames = RfBindingCheck.BoundNames(registry: _registry);
            _rfBoundNamesFrom = count;
        }

        return _rfBoundNames;
    }

    private HashSet<string>? _suflaeLibraryShapes;

    private int _suflaeLibraryShapesFrom = -1;

    /// <summary>
    /// The call shapes of the routines Suflae's own library declares. A call the overload resolution settles on the
    /// shared routine is still fine when Suflae's library declares the same shape itself (its own <c>Text()</c>).
    /// Rebuilt when a module loaded on demand brings more routines.
    /// </summary>
    private HashSet<string> SuflaeLibraryShapes()
    {
        List<RoutineInfo> routines = _registry.GetAllRoutines(requireLive: false).ToList();
        if (_suflaeLibraryShapes == null || _suflaeLibraryShapesFrom != routines.Count)
        {
            _suflaeLibraryShapes = routines
                                  .Where(predicate: r =>
                                       _registry.IsLanguageLibraryFile(filePath: r.Location?.FileName,
                                           language: Language.Suflae))
                                  .Select(selector: r => RfBindingCheck.ShapeOf(routine: r))
                                  .ToHashSet(comparer: StringComparer.Ordinal);
            _suflaeLibraryShapesFrom = routines.Count;
        }

        return _suflaeLibraryShapes;
    }

    /// <summary>Whether a declaration at <paramref name="location"/> comes from RazorForge's library.</summary>
    private bool IsRazorForgeLibraryDeclaration(SourceLocation? location)
    {
        return location is { FileName: { Length: > 0 } file } &&
               _registry.IsLanguageLibraryFile(filePath: file, language: Language.RazorForge);
    }

    /// <summary>Whether <paramref name="file"/> is a Suflae program file (not Suflae's library).</summary>
    private bool IsSuflaeProgramFile(string? file)
    {
        return !string.IsNullOrEmpty(value: file) &&
               Builder.Frontends.Languages.HasSourceExtension(fileName: file) &&
               Builder.Frontends.Languages.OfFile(fileName: file) == Language.Suflae &&
               !IsStdlibFile(filePath: file);
    }

    /// <summary>
    /// Whether a Suflae program may not name <paramref name="type"/>: it is declared in RazorForge's library and no
    /// <c>@rf</c> declaration binds it.
    /// </summary>
    private bool IsUnsharedRazorForgeType(TypeSymbol type)
    {
        TypeSymbol definition = type switch
        {
            RecordTypeSymbol { GenericDefinition: { } record } => record,
            EntityTypeSymbol { GenericDefinition: { } entity } => entity,
            ProtocolTypeSymbol { GenericDefinition: { } protocol } => protocol,
            _ => type
        };

        // A Suflae library type of the same name (Suflae's own `List`) is stamped with Suflae's realm.
        return definition.Realm == Realms.Shared &&
               _registry.RazorForgeLibraryTypes.Contains(item: $"{definition.Module}.{definition.BareName}") &&
               !RfBoundNames().Contains(item: definition.BareName);
    }

    /// <summary>
    /// Reports every type name a Suflae program writes and every routine it calls that belongs to RazorForge's
    /// library without an <c>@rf</c> declaration binding it. The builder's own routines (synthesized ones) are not the
    /// program's to name, so they pass. Runs right after the program's bodies are analyzed, while its imports are
    /// still the ones type names resolve against.
    /// </summary>
    private void CheckSharedDeclarationsAreBound(Program program)
    {
        if (_registry.Language != Language.Suflae)
        {
            return;
        }

        Visit(node: program);
        return;

        // What the builder writes into the program (a recipe routine, its danger block, the calls a lowering
        // inserts) is not the program naming anything.
        void Visit(object node)
        {
            if (node is RoutineDeclaration { IsBuilderWritten: true } or DangerStatement { IsBuilderWritten: true })
            {
                return;
            }

            if (node is Expression expression and not CallExpression { IsSynthesizedLowering: true })
            {
                Check(expression: expression);
            }

            foreach (object child in AstWalker.EnumerateChildren(node: node))
            {
                Visit(node: child);
            }
        }

        void Check(Expression expression)
        {
            if (!IsSuflaeProgramFile(file: expression.Location?.FileName))
            {
                return;
            }

            if (expression is TypeExpression { Realm: null } written)
            {
                if (LookupTypeWithImports(name: written.Name) is { } type && IsUnsharedRazorForgeType(type: type))
                {
                    ReportUnshared(name: type.BareName, location: written.Location);
                }

                return;
            }

            RoutineInfo? routine = expression switch
            {
                CallExpression call => call.ResolvedRoutine,
                GenericMemberRoutineCallExpression call => call.ResolvedRoutine,
                IdentifierExpression identifier => identifier.ResolvedRoutine,
                _ => null
            };
            if (routine == null)
            {
                return;
            }

            RoutineInfo definition = routine.GenericDefinition ?? routine;
            // A creator the builder writes (the member-wise one) still names its type.
            if (definition is { IsCreator: true, OwnerType: { } created } && IsUnsharedRazorForgeType(type: created))
            {
                ReportUnshared(name: created.BareName, location: expression.Location);
                return;
            }

            if (routine.IsSynthesized || !IsRazorForgeLibraryDeclaration(location: definition.Location))
            {
                return;
            }

            string shape = RfBindingCheck.ShapeOf(routine: routine);
            HashSet<string> bound = RfBoundNames();
            if (!bound.Contains(item: shape) && !(definition.OwnerType is { } owner &&
                    ProtocolsOf(type: owner).Any(predicate: p =>
                        bound.Contains(item: RfBindingCheck.ShapeOf(routine: routine, ownerName: p.BareName)))) &&
                !SuflaeLibraryShapes().Contains(item: shape))
            {
                ReportUnshared(name: shape, location: expression.Location);
            }
        }
    }

    /// <summary>Every protocol <paramref name="type"/> obeys, through the protocols those extend.</summary>
    private static IEnumerable<ProtocolTypeSymbol> ProtocolsOf(TypeSymbol type)
    {
        List<TypeSymbol> declared = type switch
        {
            RecordTypeSymbol record => (record.GenericDefinition ?? record).ImplementedProtocols,
            EntityTypeSymbol entity => (entity.GenericDefinition ?? entity).ImplementedProtocols,
            ProtocolTypeSymbol protocol => [protocol],
            _ => []
        };
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var pending = new Stack<ProtocolTypeSymbol>(collection: declared.OfType<ProtocolTypeSymbol>());
        while (pending.TryPop(result: out ProtocolTypeSymbol? protocol))
        {
            if (!seen.Add(item: protocol.BareName))
            {
                continue;
            }

            yield return protocol;
            foreach (ProtocolTypeSymbol parent in (protocol.GenericDefinition ?? protocol).ParentProtocols)
            {
                pending.Push(item: parent);
            }
        }
    }

    private void ReportUnshared(string name, SourceLocation location)
    {
        ReportError(code: SemanticDiagnosticCode.UnsharedRazorForgeDeclaration,
            message: $"Suflae's library has no '{name}'.",
            location: location);
    }
}
