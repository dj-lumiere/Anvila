using SyntaxTree;

namespace Builder.Declaration;

/// <summary>
/// What each file's imports let it name. An import that names members (<c>import IO/Console.[show]</c>,
/// <c>import Money.cents</c>) brings only those names bare. <c>as</c> gives a second name: after
/// <c>import Shop/Prices as Costs</c> the module is <c>Costs.</c> and <c>Prices.</c> (no bare names), after
/// <c>import Shop/Prices.total as price_total</c> the member is <c>price_total</c> and <c>Prices.total</c> (no bare
/// <c>total</c>). A module imported whole anywhere in the file brings every name. Naming the module in front
/// (<c>Prices.label(...)</c>) always reaches it. Recorded while declarations are collected, read by the analysis and
/// by the desugaring passes that resolve names before it (preset inlining).
/// </summary>
public sealed class ImportScopes(TypeRegistry registry)
{
    /// <summary>The import scope of one file.</summary>
    private sealed class FileScope
    {
        /// <summary>Per imported module, the names it brings bare. No entry: every name (a whole import).
        /// An empty set: none (the module came in only under an alias).</summary>
        public readonly Dictionary<string, HashSet<string>> Narrowed = new(comparer: StringComparer.OrdinalIgnoreCase);

        /// <summary>Alias written with <c>as</c> after a module → the module it stands for.</summary>
        public readonly Dictionary<string, string> Aliases = new(comparer: StringComparer.Ordinal);

        /// <summary>Alias written with <c>as</c> after one member → the module and the member it stands for.</summary>
        public readonly Dictionary<string, (string Module, string Member)> MemberAliases = new(comparer: StringComparer.Ordinal);

        /// <summary>Modules imported whole at least once, which no other import of the file may narrow.</summary>
        public readonly HashSet<string> Whole = new(comparer: StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, FileScope> _byFile = new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets every file (a new build).</summary>
    public void Clear() => _byFile.Clear();

    /// <summary>Records what <paramref name="import"/> of <paramref name="file"/> lets the file name, the import
    /// resolved to <paramref name="module"/>.</summary>
    public void Record(string file, ImportDeclaration import, string module)
    {
        if (!_byFile.TryGetValue(key: file, value: out FileScope? scope))
        {
            scope = new FileScope();
            _byFile[key: file] = scope;
        }

        // `import Money.cents`: one member of module `Money`.
        List<string>? names = import.SpecificImports;
        string? member = null;
        if (names == null && import.ModulePath.LastIndexOf(value: '.') is var dot && dot > 0 &&
            !string.Equals(a: import.ModulePath, b: module, comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            member = import.ModulePath[(dot + 1)..];
            names = [member];
        }

        if (import.Alias != null && member != null)
        {
            scope.MemberAliases[key: import.Alias] = (module, member);
            names = [];
        }
        else if (import.Alias != null)
        {
            scope.Aliases[key: import.Alias] = module;
            names ??= [];
        }

        if (names == null)
        {
            scope.Whole.Add(item: module);
            scope.Narrowed.Remove(key: module);
            return;
        }

        if (scope.Whole.Contains(item: module))
        {
            return;
        }

        if (!scope.Narrowed.TryGetValue(key: module, value: out HashSet<string>? allowed))
        {
            allowed = new HashSet<string>(comparer: StringComparer.Ordinal);
            scope.Narrowed[key: module] = allowed;
        }

        allowed.UnionWith(other: names);
    }

    /// <summary>
    /// Whether the imports of <paramref name="file"/> keep <paramref name="name"/> of <paramref name="module"/> from
    /// being named bare: the module came in narrowed to other names, or only under an alias. The file's own module
    /// (<paramref name="currentModule"/>) and System are never narrowed. A dotted import whose last part is not a
    /// member of the module (<c>import Fun2.A</c> naming the file <c>Fun2/A</c>) brings the whole module.
    /// </summary>
    public bool IsNarrowedOut(string file, string? currentModule, string? module, string name)
    {
        if (string.IsNullOrEmpty(value: module) || module == "System" || module == currentModule ||
            !_byFile.TryGetValue(key: file, value: out FileScope? scope) ||
            !scope.Narrowed.TryGetValue(key: module, value: out HashSet<string>? allowed) || allowed.Contains(item: name))
        {
            return false;
        }

        return allowed.Count == 0 || allowed.Any(predicate: member => IsModuleMember(module: module, name: member));
    }

    /// <summary>The module an <c>as</c> alias of <paramref name="file"/> stands for, or null.</summary>
    public string? ModuleAlias(string file, string name)
    {
        return _byFile.TryGetValue(key: file, value: out FileScope? scope) &&
               scope.Aliases.TryGetValue(key: name, value: out string? module)
            ? module
            : null;
    }

    /// <summary>The module and member an <c>as</c> alias of one member stands for in <paramref name="file"/>
    /// (<c>import Shop/Prices.total as price_total</c>), or null.</summary>
    public (string Module, string Member)? MemberAlias(string file, string name)
    {
        return !name.Contains(value: '.') && _byFile.TryGetValue(key: file, value: out FileScope? scope) &&
               scope.MemberAliases.TryGetValue(key: name, value: out (string Module, string Member) target)
            ? target
            : null;
    }

    /// <summary>When <paramref name="name"/> is a member of a module <paramref name="file"/> imported narrowed (or
    /// only under an alias), says so and how to reach it. Null otherwise.</summary>
    public string? Hint(string file, string? currentModule, string name)
    {
        if (!_byFile.TryGetValue(key: file, value: out FileScope? scope))
        {
            return null;
        }

        foreach ((string module, HashSet<string> allowed) in scope.Narrowed)
        {
            if (!IsNarrowedOut(file: file, currentModule: currentModule, module: module, name: name) ||
                !IsModuleMember(module: module, name: name))
            {
                continue;
            }

            // Only a routine is reached by putting its module in front (`Prices.label(...)`).
            string leaf = module[(module.LastIndexOf(value: '/') + 1)..];
            string orQualified = IsModuleRoutine(module: module, name: name) ? $" or '{leaf}.{name}'" : "";
            string orWriteQualified = orQualified.Length > 0 ? $", or write '{leaf}.{name}'" : "";
            string? memberAlias = scope.MemberAliases.FirstOrDefault(predicate: a =>
                string.Equals(a: a.Value.Module, b: module, comparisonType: StringComparison.OrdinalIgnoreCase) &&
                a.Value.Member == name).Key;
            if (memberAlias != null)
            {
                return $" You imported '{module}.{name}' as '{memberAlias}': write '{memberAlias}'{orQualified}.";
            }

            if (allowed.Count == 0)
            {
                string? alias = scope.Aliases.FirstOrDefault(predicate: a => string.Equals(a: a.Value, b: module,
                    comparisonType: StringComparison.OrdinalIgnoreCase)).Key;
                return alias != null
                    ? $" '{name}' is in '{module}', which you imported as '{alias}': write '{alias}.{name}'{orQualified}."
                    : $" '{name}' is in '{module}', and your imports bring only the names after 'as' from it: " +
                      $"add it to the import ('import {module}.{name}'){orWriteQualified}.";
            }

            List<string> sorted = allowed.Order(comparer: StringComparer.Ordinal).ToList();
            string quoted = string.Join(separator: ", ", values: sorted.Select(selector: n => $"'{n}'"));
            string names = string.Join(separator: ", ", values: sorted);
            return $" '{name}' is in '{module}', but your import brings only {quoted} from it: add it to the " +
                   $"import ('import {module}.[{names}, {name}]'){orWriteQualified}.";
        }

        return null;
    }

    /// <summary>Whether <paramref name="module"/> declares a free routine named <paramref name="name"/>.</summary>
    private bool IsModuleRoutine(string module, string name)
    {
        string qualified = $"{module}.{name}";
        return registry.LookupRoutine(fullName: qualified) is { OwnerType: null } ||
               registry.FreeRoutinesWithBaseName(baseName: qualified).Any() ||
               registry.LookupGenericOverload(name: name, module: module) != null;
    }

    /// <summary>Whether <paramref name="module"/> declares a type, free routine, preset or global named
    /// <paramref name="name"/>.</summary>
    private bool IsModuleMember(string module, string name)
    {
        string qualified = $"{module}.{name}";
        return registry.LookupType(name: qualified) != null || registry.LookupRoutine(fullName: qualified) != null ||
               registry.FreeRoutinesWithBaseName(baseName: qualified).Any() ||
               registry.LookupGenericOverload(name: name, module: module) != null ||
               registry.LookupVariable(name: qualified) != null;
    }
}
