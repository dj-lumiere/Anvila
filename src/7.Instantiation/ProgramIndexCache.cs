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
    /// objects in the same order mean the same entries. <paramref name="sameShape"/> tells whether the kept list still
    /// matches, without making a new one (a build checks every stdlib program each round).</summary>
    public List<TEntry> EntriesOf(Program program, Func<Program, object?[]> shape, Func<Program, object?[], bool> sameShape,
        Func<Program, List<TEntry>> read)
    {
        if (_byProgram.TryGetValue(key: program, value: out (object?[] Shape, List<TEntry> Entries) kept) &&
            sameShape(arg1: program, arg2: kept.Shape))
        {
            return kept.Entries;
        }

        List<TEntry> entries = read(arg: program);
        _byProgram[key: program] = (shape(arg: program), entries);
        return entries;
    }

}
