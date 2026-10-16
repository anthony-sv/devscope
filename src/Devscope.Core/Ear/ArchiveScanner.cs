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

    private const int ProgressInterval = 250;

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
                using var stream = OpenRead(root);
                ScanArchive(stream, container: string.Empty, state);
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
            Stream current;
            if (Directory.Exists(root))
            {
                current = OpenRead(Path.Combine(root, segments[0]));
            }
            else
            {
                using var outer = OpenRead(root);
                current = ExtractToMemory(outer, segments[0], cancellationToken);
            }

            try
            {
                foreach (var segment in segments.Skip(1))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var next = ExtractToMemory(current, segment, cancellationToken);
                    current.Dispose();
                    current = next;
                }

                if (current is MemoryStream memory)
                {
                    return memory.ToArray();
                }

                using var copy = new MemoryStream();
                current.CopyTo(copy);
                return copy.ToArray();
            }
            finally
            {
                current.Dispose();
            }
        }, cancellationToken);
    }

    private static void ScanDirectory(string root, ScanState state)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            var relative = ArchivePath.Normalize(Path.GetRelativePath(root, file));
            using var stream = OpenRead(file);

            if (ArchivePath.IsArchive(file))
            {
                state.Add(new ArchiveEntryInfo(relative, stream.Length, 0, IsArchive: true));
                ScanArchive(stream, relative, state);
            }
            else
            {
                var crc = new Crc32();
                crc.Append(stream);
                state.Add(new ArchiveEntryInfo(relative, stream.Length, crc.GetCurrentHashAsUInt32(), IsArchive: false));
            }
        }
    }

    private static void ScanArchive(Stream archiveStream, string container, ScanState state)
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

            var path = ArchivePath.Nest(container, ArchivePath.Normalize(entry.FullName));
            if (ArchivePath.IsArchive(entry.FullName) && entry.Length <= MaxNestedArchiveBytes)
            {
                // A re-packaged jar has a different CRC even when its content is identical,
                // so archive rows are compared through their children instead (CRC 0 here).
                state.Add(new ArchiveEntryInfo(path, entry.Length, 0, IsArchive: true));
                using var nested = new MemoryStream(checked((int)entry.Length));
                using (var entryStream = entry.Open())
                {
                    entryStream.CopyTo(nested);
                }

                nested.Position = 0;
                ScanArchive(nested, path, state);
            }
            else
            {
                state.Add(new ArchiveEntryInfo(path, entry.Length, entry.Crc32, IsArchive: false));
            }
        }
    }

    private static MemoryStream ExtractToMemory(Stream archiveStream, string entryName, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry(entryName)
            ?? zip.Entries.FirstOrDefault(e => ArchivePath.Normalize(e.FullName) == entryName)
            ?? throw new FileNotFoundException($"Entry '{entryName}' not found in archive.");

        cancellationToken.ThrowIfCancellationRequested();
        var buffer = new MemoryStream(checked((int)entry.Length));
        using (var entryStream = entry.Open())
        {
            entryStream.CopyTo(buffer);
        }

        buffer.Position = 0;
        return buffer;
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
