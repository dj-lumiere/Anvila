using System.Text.RegularExpressions;
using TypeModel.Enums;
using TypeModel.Types;

namespace Builder.Verification;

// `Stamped[E]` (Standard/System/Memory/Stamped.rf): an entity type the program stamps keeps its slot of the runtime's
// stamp table in four bytes after its member variables. Only the user entity types named as `Stamped[...]`'s argument
// in the program's own files get them: every other entity keeps its layout.
public sealed partial class SemanticVerifier
{
    private static readonly Regex StampedArgument = new(pattern: @"Stamped\[\s*([A-Za-z_][A-Za-z0-9_]*)",
        options: RegexOptions.Compiled);

    /// <summary>
    /// Finds the user entity types the program's files write as <c>Stamped[...]</c>'s first argument, after their
    /// declarations are collected, and records them for <see cref="EntityTypeSymbol.KeepsStamp"/>. An analysis of
    /// standard library files alone keeps the build's set. A name in a comment, or one that isn't a user entity, costs
    /// nothing. Suflae has no stamps.
    /// </summary>
    private void NoteStampedTypes(IEnumerable<string> filePaths)
    {
        var user = new HashSet<string>(collection: filePaths.Where(predicate: f => !_registry.IsStandardLibraryFile(filePath: f)),
            comparer: StringComparer.OrdinalIgnoreCase);
        if (user.Count == 0)
        {
            return;
        }

        EntityTypeSymbol.StampPointerSize = _target.PointerBitWidth / 8;
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        if (_registry.Language != Language.Suflae)
        {
            foreach (string file in user)
            {
                string text;
                try
                {
                    text = File.ReadAllText(path: file);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (!text.Contains(value: "Stamped[", comparisonType: StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match m in StampedArgument.Matches(input: text))
                {
                    names.Add(item: m.Groups[groupnum: 1].Value);
                }
            }
        }

        var stamped = new HashSet<string>(comparer: StringComparer.Ordinal);
        if (names.Count > 0)
        {
            foreach (TypeSymbol t in _registry.GetAllTypes())
            {
                if (t is EntityTypeSymbol entity && names.Contains(item: entity.BareName) &&
                    entity.Location?.FileName is { } declaredIn && user.Contains(item: declaredIn))
                {
                    stamped.Add(item: $"{entity.Module}.{entity.BareName}");
                }
            }
        }

        EntityTypeSymbol.StampedTypes = stamped;
    }
}
