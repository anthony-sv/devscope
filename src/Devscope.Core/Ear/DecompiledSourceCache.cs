using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Devscope.Core.Ear;

/// <summary>
/// Content-addressed store of decompiled sources on disk: a class is decompiled once, then reused across
/// comparisons, refreshes and restarts. Keys hash the class bytes and the decompiler jar, so a changed class or
/// a newer Vineflower never serves stale source. Best effort: I/O problems just mean a cache miss.
/// </summary>
public sealed class DecompiledSourceCache
{
    /// <summary>Trim to <see cref="MaxBytes"/> after this many writes.</summary>
    private const int TrimEveryWrites = 50;

    private int _writesSinceTrim;

    public DecompiledSourceCache(string folder, long maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = folder;
        MaxBytes = maxBytes;
    }

    /// <summary>Root folder of the cache files.</summary>
    public string Folder { get; }

    /// <summary>When false, lookups miss and nothing is written.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Size the folder is trimmed back to, dropping the least recently used sources first.</summary>
    public long MaxBytes { get; set; }

    /// <summary>Cache key for one class group: its file names and bytes plus the decompiler jar's identity.</summary>
    public static string KeyFor(DecompilerSetup setup, IReadOnlyDictionary<string, byte[]> classFiles)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(classFiles);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var jar = new FileInfo(setup.JarPath);
        Append(hash, FormattableString.Invariant($"{jar.Name}|{(jar.Exists ? jar.Length : 0)}"));

        // File names only: the same class decompiles the same wherever it is packaged.
        foreach (var (path, bytes) in classFiles.OrderBy(f => Path.GetFileName(f.Key), StringComparer.Ordinal))
        {
            Append(hash, FormattableString.Invariant($"|{Path.GetFileName(path)}|{bytes.Length}|"));
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public bool TryGet(string key, out string source)
    {
        source = string.Empty;
        if (!Enabled)
        {
            return false;
        }

        var file = FileFor(key);
        try
        {
            if (!File.Exists(file))
            {
                return false;
            }

            source = File.ReadAllText(file);

            // NTFS rarely updates last-access times, so the write time doubles as "last used" for trimming.
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Decompiled cache read failed for {file}: {ex.Message}");
            return false;
        }
    }

    public void Put(string key, string source)
    {
        if (!Enabled)
        {
            return;
        }

        var file = FileFor(key);
        var temp = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(temp, source);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Decompiled cache write failed for {file}: {ex.Message}");
            TryDeleteFile(temp);
        }

        if (Interlocked.Increment(ref _writesSinceTrim) >= TrimEveryWrites)
        {
            Interlocked.Exchange(ref _writesSinceTrim, 0);
            Trim();
        }
    }

    /// <summary>Total size of the cached sources in bytes.</summary>
    public long GetSizeBytes() => CachedFiles().Sum(f => f.Length);

    /// <summary>Deletes the least recently used sources until the folder fits <see cref="MaxBytes"/>.</summary>
    public void Trim()
    {
        var files = CachedFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var kept = 0L;
        foreach (var file in files)
        {
            kept += file.Length;
            if (kept > MaxBytes)
            {
                TryDeleteFile(file.FullName);
            }
        }
    }

    /// <summary>Deletes every cached source.</summary>
    public void Clear()
    {
        foreach (var file in CachedFiles())
        {
            TryDeleteFile(file.FullName);
        }
    }

    private string FileFor(string key) => Path.Combine(Folder, key[..2], key + ".java");

    private List<FileInfo> CachedFiles()
    {
        try
        {
            var folder = new DirectoryInfo(Folder);
            return folder.Exists
                ? [.. folder.EnumerateFiles("*.java", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not list {Folder}: {ex.Message}");
            return [];
        }
    }

    private static void Append(IncrementalHash hash, string text) => hash.AppendData(Encoding.UTF8.GetBytes(text));

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not delete {path}: {ex.Message}");
        }
    }
}

/// <summary>Serves decompiled sources from a <see cref="DecompiledSourceCache"/> and decompiles only the misses.</summary>
public sealed class CachingJavaDecompiler(IJavaDecompiler inner, DecompiledSourceCache cache) : IJavaDecompiler
{
    /// <inheritdoc />
    public async Task<string> DecompileAsync(DecompilerSetup setup, string mainClassFile, IReadOnlyDictionary<string, byte[]> classFiles, CancellationToken cancellationToken)
    {
        var key = DecompiledSourceCache.KeyFor(setup, classFiles);
        if (cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var source = await inner.DecompileAsync(setup, mainClassFile, classFiles, cancellationToken).ConfigureAwait(false);
        cache.Put(key, source);
        return source;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> DecompileManyAsync(DecompilerSetup setup, IReadOnlyList<ClassGroup> groups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        var misses = new List<ClassGroup>();
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var key = DecompiledSourceCache.KeyFor(setup, group.ClassFiles);
            if (cache.TryGet(key, out var cached))
            {
                results[group.MainClassPath] = cached;
            }
            else
            {
                keys[group.MainClassPath] = key;
                misses.Add(group);
            }
        }

        if (misses.Count > 0)
        {
            var decompiled = await inner.DecompileManyAsync(setup, misses, cancellationToken).ConfigureAwait(false);
            foreach (var (path, source) in decompiled)
            {
                results[path] = source;
                if (keys.TryGetValue(path, out var key))
                {
                    cache.Put(key, source);
                }
            }
        }

        return results;
    }
}
