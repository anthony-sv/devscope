namespace Devscope.Core.Ear;

/// <summary>A single file found while scanning an archive or exploded directory.</summary>
/// <param name="Path">Virtual path (see <see cref="ArchivePath"/>).</param>
/// <param name="Length">Uncompressed size in bytes.</param>
/// <param name="Crc32">IEEE CRC-32 of the uncompressed content (the same value zip files store).</param>
/// <param name="IsArchive">Whether this entry is itself an archive whose children were scanned.</param>
public sealed record ArchiveEntryInfo(string Path, long Length, uint Crc32, bool IsArchive);

/// <summary>Outcome of comparing one path between the left and right side.</summary>
public enum DiffStatus
{
    Unchanged,
    Modified,
    Added,
    Removed,
}

/// <summary>One row of an EAR comparison.</summary>
public sealed record EarDiffEntry(string Path, DiffStatus Status, ArchiveEntryInfo? Left, ArchiveEntryInfo? Right)
{
    /// <summary>Whether this row is a nested archive (its status is derived from its children).</summary>
    public bool IsArchive => (Left ?? Right)!.IsArchive;

    /// <summary>File name without the containing path.</summary>
    public string Name
    {
        get
        {
            var separator = Path.LastIndexOf(ArchivePath.NestedSeparator, StringComparison.Ordinal);
            var leaf = separator < 0 ? Path : Path[(separator + ArchivePath.NestedSeparator.Length)..];
            return leaf[(leaf.LastIndexOf('/') + 1)..];
        }
    }

    /// <summary>The archive this row lives in, or empty for top-level rows.</summary>
    public string Container => ArchivePath.GetContainer(Path);
}

/// <summary>Result of an EAR comparison.</summary>
public sealed record EarComparison(
    string LeftRoot,
    string RightRoot,
    IReadOnlyList<EarDiffEntry> Entries,
    TimeSpan Elapsed)
{
    public int Added => Count(DiffStatus.Added);

    public int Removed => Count(DiffStatus.Removed);

    public int Modified => Count(DiffStatus.Modified);

    public int Unchanged => Count(DiffStatus.Unchanged);

    private int Count(DiffStatus status) => Entries.Count(e => !e.IsArchive && e.Status == status);
}

/// <summary>Progress notification for long scans.</summary>
public readonly record struct ScanProgress(string Side, int EntriesScanned, string CurrentPath);
