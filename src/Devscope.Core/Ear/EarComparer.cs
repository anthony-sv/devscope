using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Devscope.Core.Ear;

/// <summary>Compares two EARs (or exploded directories) file by file, including nested WARs and JARs.</summary>
public interface IEarComparer
{
    /// <summary>Scans both sides in parallel and returns every path with its status.</summary>
    Task<EarComparison> CompareAsync(
        string leftRoot, string rightRoot, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed partial class EarComparer(IArchiveScanner scanner, ILogger<EarComparer> logger) : IEarComparer
{
    /// <inheritdoc />
    public async Task<EarComparison> CompareAsync(
        string leftRoot, string rightRoot, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var leftTask = scanner.ScanAsync(leftRoot, "left", progress, cancellationToken);
        var rightTask = scanner.ScanAsync(rightRoot, "right", progress, cancellationToken);
        await Task.WhenAll(leftTask, rightTask).ConfigureAwait(false);

        var entries = Compare(await leftTask.ConfigureAwait(false), await rightTask.ConfigureAwait(false));
        stopwatch.Stop();

        LogCompared(leftRoot, rightRoot, entries.Count, stopwatch.Elapsed);
        return new EarComparison(leftRoot, rightRoot, entries, stopwatch.Elapsed);
    }

    /// <summary>Pure comparison of two scan results; exposed for testing.</summary>
    internal static List<EarDiffEntry> Compare(
        IReadOnlyDictionary<string, ArchiveEntryInfo> left,
        IReadOnlyDictionary<string, ArchiveEntryInfo> right)
    {
        var paths = new SortedSet<string>(left.Keys, StringComparer.Ordinal);
        paths.UnionWith(right.Keys);

        var statuses = new Dictionary<string, DiffStatus>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var l = left.GetValueOrDefault(path);
            var r = right.GetValueOrDefault(path);
            statuses[path] = (l, r) switch
            {
                (null, _) => DiffStatus.Added,
                (_, null) => DiffStatus.Removed,
                ({ IsArchive: true }, { IsArchive: true }) => DiffStatus.Unchanged, // derived from children below
                _ when l.IsArchive == r.IsArchive && l.Length == r.Length && l.Crc32 == r.Crc32 => DiffStatus.Unchanged,
                _ => DiffStatus.Modified,
            };
        }

        // An archive present on both sides is Modified when anything below it changed.
        foreach (var (path, status) in statuses.ToList())
        {
            if (status == DiffStatus.Unchanged)
            {
                continue;
            }

            for (var container = ArchivePath.GetContainer(path);
                 container.Length > 0 && statuses.TryGetValue(container, out var containerStatus) && containerStatus == DiffStatus.Unchanged;
                 container = ArchivePath.GetContainer(container))
            {
                statuses[container] = DiffStatus.Modified;
            }
        }

        return paths
            .Select(p => new EarDiffEntry(p, statuses[p], left.GetValueOrDefault(p), right.GetValueOrDefault(p)))
            .ToList();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Compared {Left} with {Right}: {Count} paths in {Elapsed}")]
    private partial void LogCompared(string left, string right, int count, TimeSpan elapsed);
}
