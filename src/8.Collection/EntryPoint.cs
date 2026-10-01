using TypeModel.Symbols;

namespace Builder.Collection;

/// <summary>The routine a program starts in, chosen the same way for every backend.</summary>
internal static class EntryPoint
{
    private const string Start = "start";

    /// <summary>
    /// The entry module's <c>start()</c> among <paramref name="defined"/>, else the only <c>start()</c> there when
    /// the build names no entry module or the named one has none; null when there is no single one (a build without
    /// an entry point, such as the resident standard-library base).
    /// </summary>
    public static RoutineInfo? StartOf(IEnumerable<RoutineInfo> defined, string? entryModule)
    {
        List<RoutineInfo> starts = defined.Where(predicate: r => r is { OwnerType: null, Name: Start, Parameters.Count: 0 })
                                          .Distinct()
                                          .ToList();
        return (string.IsNullOrEmpty(value: entryModule)
                   ? null
                   : starts.FirstOrDefault(predicate: r => r.Module == entryModule)) ??
               (starts.Count == 1
                   ? starts[0]
                   : null);
    }
}
