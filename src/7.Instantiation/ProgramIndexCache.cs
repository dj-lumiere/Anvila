using SyntaxTree;

namespace Builder.Instantiation;

/// <summary>
/// What an index read from each program's declarations holds for that program, kept while the program's declarations
/// stay the same objects. The demand collector rebuilds its indexes every round, and only the programs it analyzed on
/// demand in that round have changed: the others are read again only when something in them was replaced.
/// </summary>
/// <typeparam name="TEntry">One entry of the index.</typeparam>
internal sealed class ProgramIndexCache<TEntry>
{
    private readonly Dictionary<Program, (object?[] Shape, List<TEntry> Entries)> _byProgram =
        new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>The entries of <paramref name="program"/>, read with <paramref name="read"/> when its declarations
    /// changed since they were kept. <paramref name="shape"/> lists the objects the entries are made from: the same
    /// objects in the same order mean the same entries.</summary>
    public List<TEntry> EntriesOf(Program program, Func<Program, object?[]> shape, Func<Program, List<TEntry>> read)
    {
        object?[] now = shape(arg: program);
        if (_byProgram.TryGetValue(key: program, value: out (object?[] Shape, List<TEntry> Entries) kept) &&
            SameObjects(a: kept.Shape, b: now))
        {
            return kept.Entries;
        }

        List<TEntry> entries = read(arg: program);
        _byProgram[key: program] = (now, entries);
        return entries;
    }

    private static bool SameObjects(object?[] a, object?[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (!ReferenceEquals(objA: a[i], objB: b[i]))
            {
                return false;
            }
        }

        return true;
    }
}
