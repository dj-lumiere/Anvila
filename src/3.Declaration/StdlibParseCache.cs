using System.Collections.Concurrent;

namespace Builder.Declaration;

/// <summary>
/// The parses of standard-library files a warm daemon's builds import, kept for the daemon's lifetime. Each request
/// otherwise tokenizes and parses every imported standard-library file again: <c>import Numerics</c> alone is about
/// 7,500 lines, which cost ~70 ms per edit, and the garbage it left behind set off a collection later in the same
/// build.
/// <para>
/// A cached unit is shared, not cloned. The build driver reads a standard-library unit (its module, imports and
/// top-level declarations for the import index) and then drops it, because the standard library itself is analyzed
/// from the daemon's warm snapshot, so nothing writes to the shared tree after its parse.
/// </para>
/// <para>
/// An entry is keyed by the file's full path and checked against its last-write time and length, so an edited file
/// is parsed again on the next build. The stamp is taken before the file is read: a file that changes during the
/// read is stored under the older stamp and is parsed again next time. A new builder build restarts the daemon,
/// which empties the cache with the process.
/// </para>
/// </summary>
internal static class StdlibParseCache
{
    /// <summary>What identifies one version of a file on disk.</summary>
    internal readonly record struct FileStamp(long LastWriteTicks, long Length);

    private static readonly ConcurrentDictionary<string, (FileStamp Stamp, FileBuildUnit Unit)> Units =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>The file's current stamp, or the default stamp when it can't be read (which never matches an
    /// entry, so the caller parses the file and reports what is wrong with it).</summary>
    internal static FileStamp StampOf(string filePath)
    {
        try
        {
            var info = new FileInfo(fileName: filePath);
            return info.Exists
                ? new FileStamp(LastWriteTicks: info.LastWriteTimeUtc.Ticks, Length: info.Length)
                : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>Returns the cached parse of the file when it was taken from this same version of it.</summary>
    internal static bool TryGet(string filePath, FileStamp stamp, out FileBuildUnit? unit)
    {
        if (stamp != default &&
            Units.TryGetValue(key: Path.GetFullPath(path: filePath),
                value: out (FileStamp Stamp, FileBuildUnit Unit) entry) &&
            entry.Stamp == stamp)
        {
            unit = entry.Unit;
            return true;
        }

        unit = null;
        return false;
    }

    /// <summary>Keeps a parse taken from the version of the file that <paramref name="stamp"/> names.</summary>
    internal static void Store(string filePath, FileStamp stamp, FileBuildUnit unit)
    {
        if (stamp == default)
        {
            return;
        }

        Units[key: Path.GetFullPath(path: filePath)] = (stamp, unit);
    }
}
