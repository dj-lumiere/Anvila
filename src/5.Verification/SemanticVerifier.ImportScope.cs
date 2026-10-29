using TypeModel.Symbols;

namespace Builder.Verification;

/// <summary>
/// The current file's view of <see cref="Builder.Declaration.ImportScopes"/>: narrowed imports and <c>as</c>
/// aliases, asked with the file being analyzed and its module.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>Whether the current file's imports keep <paramref name="name"/> of <paramref name="module"/> from
    /// being named bare.</summary>
    internal bool IsNarrowedOut(string? module, string name) =>
        _registry.ImportScopes.IsNarrowedOut(file: _currentFilePath, currentModule: _currentModuleName, module: module,
            name: name);

    /// <summary>Why <paramref name="name"/> is not seen when an import narrowed it out, or null.</summary>
    private string? NarrowedImportHint(string name) =>
        _registry.ImportScopes.Hint(file: _currentFilePath, currentModule: _currentModuleName, name: name);

    /// <summary>The module an <c>as</c> alias of the current file stands for, or null.</summary>
    private string? ImportAliasModule(string name) =>
        _registry.ImportScopes.ModuleAlias(file: _currentFilePath, name: name);

    /// <summary>The module and member an <c>as</c> alias of one imported member stands for, or null.</summary>
    internal (string Module, string Member)? ImportMemberAlias(string name) =>
        _registry.ImportScopes.MemberAlias(file: _currentFilePath, name: name);

    /// <summary>The routine a call of <paramref name="name"/> reaches through a member alias, when nothing nearer
    /// (a variable, the current module's own routine) has that name.</summary>
    private (RoutineInfo Routine, string Member)? ImportedRoutineUnderAlias(string name)
    {
        if (ImportMemberAlias(name: name) is not var (module, member) ||
            _registry.LookupVariable(name: name) != null ||
            _currentModuleName != null && _registry.LookupRoutine(fullName: $"{_currentModuleName}.{name}") != null)
        {
            return null;
        }

        RoutineInfo? routine = _registry.LookupRoutine(fullName: $"{module}.{member}") ??
                               _registry.LookupGenericOverload(name: member, module: module);
        return routine is { OwnerType: null } ? (routine, member) : null;
    }
}
