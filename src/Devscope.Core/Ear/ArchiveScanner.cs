using System.IO.Compression;
using System.IO.Hashing;

namespace Devscope.Core.Ear;

/// <summary>Lists every file inside an EAR/WAR/JAR (recursing into nested archives) or an exploded directory.</summary>
public interface IArchiveScanner
{
    /// <summary>Scans <paramref name="root"/>, which may be an archive file or a directory (local or UNC path).</summary>
    Task<IReadOnlyDictionary<string, ArchiveEntryInfo>> ScanAsync(
        string root, string side, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Reads the uncompressed bytes of one virtual path below <paramref name="root"/>.</summary>
    Task<byte[]> ReadEntryAsync(string root, string virtualPath, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class ArchiveScanner : IArchiveScanner
{
    /// <summary>Nested archives larger than this are listed but not expanded, to bound memory use.</summary>
    public const long MaxNestedArchiveBytes = 512L * 1024 * 1024;

    /// <summary>Default memory budget for unpacked nested archives.</summary>
    public const long DefaultCacheBytes = 256L * 1024 * 1024;

    private const int ProgressInterval = 250;

    // Unpacked nested archives (a WAR inside an EAR, a JAR inside that WAR), keyed by the physical file's size
    // and timestamp plus the chain of archive names inside it, so a redeployed file never serves stale bytes.
    // The scan fills it; selecting a row afterwards then skips re-inflating the WAR on every click.
    private readonly LruByteCache _nestedArchives = new(DefaultCacheBytes);

    /// <summary>Memory budget for unpacked nested archives; 0 disables the cache.</summary>
    public long CacheCapacityBytes
    {
        get => _nestedArchives.CapacityBytes;
        set => _nestedArchives.CapacityBytes = value;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, ArchiveEntryInfo>> ScanAsync(
        string root, string side, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        // Zip APIs are synchronous; run the whole walk off the caller's thread.
        return Task.Run<IReadOnlyDictionary<string, ArchiveEntryInfo>>(() =>
        {
            var state = new ScanState(side, progress, cancellationToken);
            if (Directory.Exists(root))
            {
                ScanDirectory(root, state);
            }
            else if (File.Exists(root))
            {
                var cachePrefix = CachePrefix(root);
                using var stream = OpenRead(root);
                ScanArchive(stream, container: string.Empty, cachePrefix, chain: string.Empty, state);
            }
            else
            {
                throw new FileNotFoundException($"'{root}' does not exist or is not reachable.", root);
            }

            progress?.Report(new ScanProgress(side, state.Results.Count, string.Empty));
            return state.Results;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<byte[]> ReadEntryAsync(string root, string virtualPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        return Task.Run(() =>
        {
            var segments = ArchivePath.Split(virtualPath);

            // The file on disk that holds the entry, and the archive segments still to open inside it.
            var isDirectory = Directory.Exists(root);
            var physical = isDirectory ? Path.Combine(root, segments[0]) : root;
            var inner = isDirectory ? segments[1..] : segments;

            if (inner.Length == 0)
            {
                using var file = OpenRead(physical);
                using var copy = new MemoryStream();
                file.CopyTo(copy);
                return copy.ToArray();
            }

            if (inner.Length == 1)
            {
                using var file = OpenRead(physical);
                return Extract(file, inner[0], cancellationToken);
            }

            var container = GetNestedArchive(physical, CachePrefix(physical), inner[..^1], cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(container, writable: false);
            return Extract(stream, inner[^1], cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Returns the archive at <paramref name="chain"/> inside <paramref name="physical"/>, unpacking each level at most once.</summary>
    private byte[] GetNestedArchive(string physical, string cachePrefix, string[] chain, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Not cancellable once started: other readers may be waiting for the same archive.
        return _nestedArchives.GetOrAdd(cachePrefix + string.Join(ArchivePath.NestedSeparator, chain), () =>
        {
            if (chain.Length == 1)
            {
                using var file = OpenRead(physical);
                return Extract(file, chain[0], CancellationToken.None);
            }

            var parent = GetNestedArchive(physical, cachePrefix, chain[..^1], CancellationToken.None);
            using var stream = new MemoryStream(parent, writable: false);
            return Extract(stream, chain[^1], CancellationToken.None);
        });
    }

    private void ScanDirectory(string root, ScanState state)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var relative = ArchivePath.Normalize(Path.GetRelativePath(root, file));

            if (ArchivePath.IsArchive(file))
            {
                var cachePrefix = CachePrefix(file);
                using var stream = OpenRead(file);
                state.Add(new ArchiveEntryInfo(relative, stream.Length, 0, IsArchive: true));
                ScanArchive(stream, relative, cachePrefix, chain: string.Empty, state);
            }
            else
            {
                using var stream = OpenRead(file);
                var crc = new Crc32();
                crc.Append(stream);
                state.Add(new ArchiveEntryInfo(relative, stream.Length, crc.GetCurrentHashAsUInt32(), IsArchive: false));
            }
        }
    }

    /// <param name="archiveStream">The archive being listed.</param>
    /// <param name="container">Its virtual path: the prefix of every entry found.</param>
    /// <param name="cachePrefix">Identifies the physical file the archive lives in.</param>
    /// <param name="chain">Its path inside that physical file; empty for the file itself.</param>
    /// <param name="state">Results and progress.</param>
    private void ScanArchive(Stream archiveStream, string container, string cachePrefix, string chain, ScanState state)
    {
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            state.CancellationToken.ThrowIfCancellationRequested();

            // Directory entries carry no content.
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var name = ArchivePath.Normalize(entry.FullName);
            var path = ArchivePath.Nest(container, name);
            if (ArchivePath.IsArchive(entry.FullName) && entry.Length <= MaxNestedArchiveBytes)
            {
                // A re-packaged jar has a different CRC even when its content is identical,
                // so archive rows are compared through their children instead (CRC 0 here).
                state.Add(new ArchiveEntryInfo(path, entry.Length, 0, IsArchive: true));
                var nestedChain = ArchivePath.Nest(chain, name);
                var bytes = _nestedArchives.GetOrAdd(cachePrefix + nestedChain, () => ReadAll(entry));
                using var nested = new MemoryStream(bytes, writable: false);
                ScanArchive(nested, path, cachePrefix, nestedChain, state);
            }
            else
            {
                state.Add(new ArchiveEntryInfo(path, entry.Length, entry.Crc32, IsArchive: false));
            }
        }
    }

    private static byte[] Extract(Stream archiveStream, string entryName, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry(entryName)
            ?? zip.Entries.FirstOrDefault(e => ArchivePath.Normalize(e.FullName) == entryName)
            ?? throw new FileNotFoundException($"Entry '{entryName}' not found in archive.");

        cancellationToken.ThrowIfCancellationRequested();
        return ReadAll(entry);
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        var buffer = new byte[checked((int)entry.Length)];
        using var entryStream = entry.Open();
        entryStream.ReadExactly(buffer);
        return buffer;
    }

    // Size and timestamp stand in for the content, so an archive redeployed under the same name is read again.
    private static string CachePrefix(string physicalPath)
    {
        var info = new FileInfo(physicalPath);
        return FormattableString.Invariant($"{Path.GetFullPath(physicalPath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|");
    }

    // Read-only, and tolerant of other processes (app servers) holding the file open.
    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 81920);

    private sealed class ScanState(string side, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        public Dictionary<string, ArchiveEntryInfo> Results { get; } = new(StringComparer.Ordinal);

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public void Add(ArchiveEntryInfo info)
        {
            Results[info.Path] = info;
            if (Results.Count % ProgressInterval == 0)
            {
                progress?.Report(new ScanProgress(side, Results.Count, info.Path));
            }
        }
    }
}
