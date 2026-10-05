using Builder.Instantiation;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    /// <summary>Each parameter default, analyzed once in its routine's declaration context. Keyed by the
    /// routine's registry key and the parameter name.</summary>
    private readonly Dictionary<(string Routine, string Parameter), Expression> _analyzedDefaults = new();

    /// <summary>
    /// Writes each parameter a call leaves out but that has a default into the call as a named argument
    /// (<see cref="NamedArgumentExpression.IsDefaultArgument"/>), so the default is lowered and emitted like
    /// any argument. Positional arguments fill the parameters after <c>me</c> in order, named ones by name.
    /// The value is a fresh copy of the default analyzed in the routine's own declaration context (its
    /// module's imports and presets), not the caller's. A member conversion (<c>x.Real()</c>) binds its
    /// receiver to the creator's first parameter.
    /// </summary>
    private void AppendDefaultArguments(Expression call)
    {
        (RoutineInfo? routine, List<Expression>? arguments) = call switch
        {
            CallExpression { ResolvedRoutine: { } r } c => (r, c.Arguments),
            GenericMemberRoutineCallExpression { ResolvedRoutine: { } r } g => (r, g.Arguments),
            _ => (null, null)
        };
        if (routine == null || arguments == null)
        {
            return;
        }

        AppendDefaultArguments(call: call, routine: routine, arguments: arguments);
    }

    /// <summary>
    /// Writes the defaults of the parameters of <paramref name="routine"/> that <paramref name="arguments"/>
    /// leaves out into the list, as <see cref="AppendDefaultArguments(Expression)"/> does for a bound call. A
    /// memberwise construction is not bound to its synthesized creator, so it calls this directly to get the
    /// defaults of the member variables it leaves out (<c>User(name: "Ada")</c> with <c>age: Integer = 30</c>).
    /// </summary>
    private void AppendDefaultArguments(Expression call, RoutineInfo routine, List<Expression> arguments)
    {
        if (!routine.Parameters.Any(predicate: p => p.HasDefaultValue))
        {
            return;
        }

        var named = arguments.OfType<NamedArgumentExpression>()
                             .Select(selector: n => n.Name)
                             .ToHashSet(comparer: StringComparer.Ordinal);
        int positional = arguments.Count(predicate: a => a is not NamedArgumentExpression);
        // A member conversion `x.Type()` binds its receiver to the creator's first parameter.
        if (call is CallExpression { LoweringKind: CallLoweringKind.TypeConstructor, Callee: MemberExpression })
        {
            positional++;
        }

        List<ParamInfo> parameters = routine.Parameters.Where(predicate: p => p.Name != "me").ToList();
        for (int i = 0; i < parameters.Count; i++)
        {
            ParamInfo parameter = parameters[index: i];
            if (i < positional || named.Contains(item: parameter.Name) || !parameter.HasDefaultValue)
            {
                continue;
            }

            Expression value = GenericAstRewriter.DeepCloneExpression(
                expr: AnalyzedDefault(routine: routine, parameter: parameter)) with { IsPreAnalyzed = true };
            arguments.Add(item: new NamedArgumentExpression(Name: parameter.Name,
                Value: value,
                Location: value.Location)
            {
                ResolvedType = value.ResolvedType,
                IsDefaultArgument = true
            });
        }
    }

    /// <summary>
    /// The default of <paramref name="parameter"/> analyzed against the parameter's type in the declaration
    /// context of <paramref name="routine"/>. A default naming a preset is that preset's value.
    /// </summary>
    private Expression AnalyzedDefault(RoutineInfo routine, ParamInfo parameter)
    {
        RoutineInfo declaring = routine.GenericDefinition ?? routine;
        (string, string) key = (declaring.RegistryKey, parameter.Name);
        if (_analyzedDefaults.TryGetValue(key: key, value: out Expression? analyzed))
        {
            return analyzed;
        }

        string previousFilePath = _currentFilePath;
        var previousImports = new HashSet<string>(collection: _importedModules,
            comparer: StringComparer.OrdinalIgnoreCase);
        var previousSymbols = new HashSet<string>(collection: _importedSymbolNames,
            comparer: StringComparer.Ordinal);
        string? previousModuleName = _currentModuleName;

        RestoreImportScopeForCompilerGeneratedBody(routineInfo: declaring);
        string? ownerModule = declaring.OwnerType?.Module ?? declaring.Module;
        if (!string.IsNullOrEmpty(value: ownerModule))
        {
            _currentModuleName = ownerModule;
        }

        Expression value = GenericAstRewriter.DeepCloneExpression(expr: parameter.DefaultValue!);
        AnalyzeExpression(expression: value, expectedType: parameter.Type);
        if (value is IdentifierExpression presetName &&
            _registry.LookupVariable(name: presetName.Name) is { IsPreset: true, PresetValue: { } presetValue })
        {
            value = GenericAstRewriter.DeepCloneExpression(expr: presetValue);
            value.ResolvedType ??= presetName.ResolvedType ?? parameter.Type;
        }

        _currentFilePath = previousFilePath;
        _currentModuleName = previousModuleName;
        _importedModules.Clear();
        _importedModules.UnionWith(other: previousImports);
        _importedSymbolNames.Clear();
        _importedSymbolNames.UnionWith(other: previousSymbols);

        _analyzedDefaults[key: key] = value;
        return value;
    }
}
