using SyntaxTree;
using Builder.Declaration;
using TypeModel.Enums;
using TypeModel.Types;

namespace Builder.Verification;

// `Stamped[E]` (Standard/System/Memory/Stamped.rvr): an entity type the program stamps keeps its slot of the runtime's
// stamp table in four bytes after its member variables. Only the user entity types named as `Stamped[...]`'s argument
// in the program's own files get them: every other entity keeps its layout.
public sealed partial class SemanticVerifier
{
    /// <summary>
    /// Finds the user entity types the program's files write as <c>Stamped[...]</c>'s first argument (any type written
    /// in a declaration, a signature or a body), after their declarations are collected, and marks them
    /// <see cref="EntityTypeSymbol.KeepsStamp"/>. The mark is on each analysis's own symbols, so builds running side by
    /// side in one process (the daemon, the test harness) never see each other's. An analysis of standard library files
    /// alone marks nothing. Mielia has no stamps.
    /// </summary>
    private void NoteStampedTypes(IEnumerable<(Program Program, string FilePath)> files)
    {
        var userFiles = files.Where(predicate: f => !_registry.IsStandardLibraryFile(filePath: f.FilePath)).ToList();
        if (userFiles.Count == 0)
        {
            return;
        }

        var user = new HashSet<string>(collection: userFiles.Select(selector: f => f.FilePath),
            comparer: StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        if (_registry.Language != Language.Mielia)
        {
            foreach ((Program program, string _) in userFiles)
            {
                AstWalker.Walk(root: program,
                    visit: node =>
                    {
                        if (node is TypeExpression
                            {
                                Name: RuntimeContract.StampedTypeName, GenericArguments: [var stampedOf, ..]
                            })
                        {
                            names.Add(item: stampedOf.Name);
                        }
                    });
            }
        }

        int pointerSize = _target.PointerBitWidth / 8;
        foreach (TypeSymbol t in _registry.GetAllTypes())
        {
            if (t is EntityTypeSymbol entity && entity.Location?.FileName is { } declaredIn &&
                user.Contains(item: declaredIn))
            {
                entity.KeepsStamp = names.Contains(item: entity.BareName) ||
                                    names.Contains(item: $"{entity.Module}.{entity.BareName}");
                entity.StampPointerSize = pointerSize;
            }
        }
    }
}
