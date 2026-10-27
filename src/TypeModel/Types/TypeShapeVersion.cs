namespace TypeModel.Types;

/// <summary>
/// A process-wide count of changes to what a type obeys, holds and declares (its protocols, member variables,
/// protocol member routines and parents) and to what the type registries hold (types and routines). A memo of
/// an answer that depends on any of these keeps the count it was made at and is stale once the count has moved.
/// </summary>
public static class TypeShapeVersion
{
    private static long _current;

    /// <summary>The count now.</summary>
    public static long Current => Interlocked.Read(location: ref _current);

    /// <summary>Records one change.</summary>
    public static void Bump()
    {
        Interlocked.Increment(location: ref _current);
    }
}
