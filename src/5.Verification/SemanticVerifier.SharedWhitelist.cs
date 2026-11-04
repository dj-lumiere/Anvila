using Builder.Declaration;
using Builder.Diagnostics;
using SyntaxTree;
using TypeModel;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Mielia's programs see Revare's library only through Mielia's own: a type or routine of a Revare library
/// file is usable from a Mielia program when an <c>@rv</c> declaration of Mielia's library binds it. Everything else
/// the two languages share in the build (tokens, wrappers, the builder's helpers) stays out of reach.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>The bound names, rebuilt when a module loaded on demand brings more bindings.</summary>
    private HashSet<string>? _rfBoundNames;

    private int _rfBoundNamesFrom = -1;

    private HashSet<string> RvBoundNames()
    {
        int count = _registry.RvBindings.Count;
        if (_rfBoundNames == null || _rfBoundNamesFrom != count)
        {
            _rfBoundNames = RvBindingCheck.BoundNames(registry: _registry);
            _rfBoundNamesFrom = count;
        }

        return _rfBoundNames;
    }

    private HashSet<string>? _mieliaLibraryShapes;

    private int _mieliaLibraryShapesFrom = -1;

    /// <summary>
    /// The call shapes of the routines Mielia's own library declares. A call the overload resolution settles on the
    /// shared routine is still fine when Mielia's library declares the same shape itself (its own <c>Text()</c>).
    /// Rebuilt when a module loaded on demand brings more routines.
    /// </summary>
    private HashSet<string> MieliaLibraryShapes()
    {
        List<RoutineInfo> routines = _registry.GetAllRoutines(requireLive: false).ToList();
        if (_mieliaLibraryShapes == null || _mieliaLibraryShapesFrom != routines.Count)
        {
            _mieliaLibraryShapes = routines
                                  .Where(predicate: r =>
                                       _registry.IsLanguageLibraryFile(filePath: r.Location?.FileName,
                                           language: Language.Mielia))
                                  .Select(selector: r => RvBindingCheck.ShapeOf(routine: r))
                                  .ToHashSet(comparer: StringComparer.Ordinal);
            _mieliaLibraryShapesFrom = routines.Count;
        }

        return _mieliaLibraryShapes;
    }

    /// <summary>Whether a declaration at <paramref name="location"/> comes from Revare's library.</summary>
    private bool IsRevareLibraryDeclaration(SourceLocation? location)
    {
        return location is { FileName: { Length: > 0 } file } &&
               _registry.IsLanguageLibraryFile(filePath: file, language: Language.Revare);
    }

    /// <summary>Whether <paramref name="file"/> is a Mielia program file (not Mielia's library).</summary>
    private bool IsMieliaProgramFile(string? file)
    {
        return !string.IsNullOrEmpty(value: file) &&
               Builder.Frontends.Languages.HasSourceExtension(fileName: file) &&
               Builder.Frontends.Languages.OfFile(fileName: file) == Language.Mielia &&
               !IsStdlibFile(filePath: file);
    }

    /// <summary>
    /// Whether a Mielia program may not name <paramref name="type"/>: it is declared in Revare's library and no
    /// <c>@rv</c> declaration binds it.
    /// </summary>
    private bool IsUnsharedRevareType(TypeSymbol type)
    {
        TypeSymbol definition = type switch
        {
            RecordTypeSymbol { GenericDefinition: { } record } => record,
            EntityTypeSymbol { GenericDefinition: { } entity } => entity,
            ProtocolTypeSymbol { GenericDefinition: { } protocol } => protocol,
            _ => type
        };

        // A Mielia library type of the same name (Mielia's own `List`) is stamped with Mielia's realm.
        return definition.Realm == Realms.Shared &&
               _registry.RevareLibraryTypes.Contains(item: $"{definition.Module}.{definition.BareName}") &&
               !RvBoundNames().Contains(item: definition.BareName);
    }

    /// <summary>
    /// Reports every type name a Mielia program writes and every routine it calls that belongs to Revare's
    /// library without an <c>@rv</c> declaration binding it. The builder's own routines (synthesized ones) are not the
    /// program's to name, so they pass. Runs right after the program's bodies are analyzed, while its imports are
    /// still the ones type names resolve against.
    /// </summary>
    private void CheckSharedDeclarationsAreBound(Program program)
    {
        if (_registry.Language != Language.Mielia)
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

            // A type the builder completed (`to[List]` written, `List[Crate]` filled in) is checked as the type it
            // stands for. The arguments it spells are how the builder holds that type (a Mielia entity's handle), not
            // names the program wrote.
            if (node is TypeExpression { KnownType: not null })
            {
                return;
            }

            foreach (object child in AstWalker.EnumerateChildren(node: node))
            {
                Visit(node: child);
            }
        }

        void Check(Expression expression)
        {
            if (!IsMieliaProgramFile(file: expression.Location?.FileName))
            {
                return;
            }

            if (expression is TypeExpression { Realm: null } written)
            {
                // A type the builder wrote in (an implicit token's target) carries the type it stands for, which a
                // lookup by its name could find in the other library.
                if ((written.KnownType ?? LookupTypeWithImports(name: written.Name)) is { } type &&
                    IsUnsharedRevareType(type: type))
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
            if (definition is { IsCreator: true, OwnerType: { } created } && IsUnsharedRevareType(type: created))
            {
                ReportUnshared(name: created.BareName, location: expression.Location);
                return;
            }

            if (routine.IsSynthesized || !IsRevareLibraryDeclaration(location: definition.Location))
            {
                return;
            }

            string shape = RvBindingCheck.ShapeOf(routine: routine);
            HashSet<string> bound = RvBoundNames();
            if (!bound.Contains(item: shape) && !(definition.OwnerType is { } owner &&
                    ProtocolsOf(type: owner).Any(predicate: p =>
                        bound.Contains(item: RvBindingCheck.ShapeOf(routine: routine, ownerName: p.BareName)))) &&
                !MieliaLibraryShapes().Contains(item: shape))
            {
                ReportUnshared(name: shape, location: expression.Location);
            }
        }
    }

    /// <summary>Every protocol <paramref name="type"/> obeys, through the protocols those extend.</summary>
    internal static IEnumerable<ProtocolTypeSymbol> ProtocolsOf(TypeSymbol type)
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
        // A number every job changes has one way in Mielia, so the message names it.
        string way = name is "Unbroken" or "Atomic"
            ? " A number every job changes is a 'global' or a member variable of an entity: the builder keeps each " +
              "change whole, so write 'hits += 1' on it."
            : "";
        ReportError(code: SemanticDiagnosticCode.UnsharedRevareDeclaration,
            message: $"Mielia's library has no '{name}'.{way}",
            location: location);
    }
}
