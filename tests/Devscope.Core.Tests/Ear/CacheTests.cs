using Devscope.Core.Ear;

namespace Devscope.Core.Tests.Ear;

public sealed class CacheTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void LruByteCache_OverCapacity_EvictsLeastRecentlyUsed()
    {
        var cache = new LruByteCache(capacityBytes: 25);
        var loads = new List<string>();
        byte[] Load(string key)
        {
            loads.Add(key);
            return new byte[10];
        }

        cache.GetOrAdd("a", () => Load("a"));
        cache.GetOrAdd("b", () => Load("b"));
        cache.GetOrAdd("a", () => Load("a")); // a is now the most recent
        cache.GetOrAdd("c", () => Load("c")); // evicts b
        cache.GetOrAdd("a", () => Load("a"));
        cache.GetOrAdd("b", () => Load("b"));

        Assert.Equal(["a", "b", "c", "b"], loads);
        Assert.True(cache.SizeBytes <= 25);
    }

    [Fact]
    public async Task LruByteCache_ConcurrentMisses_RunFactoryOnce()
    {
        var cache = new LruByteCache(capacityBytes: 1024);
        var calls = 0;
        using var gate = new ManualResetEventSlim();

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() => cache.GetOrAdd("war", () =>
        {
            Interlocked.Increment(ref calls);
            gate.Wait(TestContext.Current.CancellationToken);
            return [1, 2, 3];
        }))).ToList();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        gate.Set();
        var results = await Task.WhenAll(readers);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal([1, 2, 3], r));
    }

    [Fact]
    public void LruByteCache_FactoryThrows_NextCallRetries()
    {
        var cache = new LruByteCache(capacityBytes: 1024);

        Assert.Throws<IOException>(() => cache.GetOrAdd("x", () => throw new IOException("busy")));
        var value = cache.GetOrAdd("x", () => [7]);

        Assert.Equal([7], value);
    }

    [Fact]
    public async Task ArchiveScanner_ReadNestedEntry_ReturnsContentAndSeesRedeploy()
    {
        var scanner = new ArchiveScanner();
        var ct = TestContext.Current.CancellationToken;
        var ear = _temp.WriteFile("app.ear", BuildEar("v1"));
        await scanner.ScanAsync(ear, "left", progress: null, ct);

        var first = await scanner.ReadEntryAsync(ear, "web.war!/WEB-INF/lib/core.jar!/com/acme/A.class", ct);

        // Same name, new content and timestamp: the cached WAR must not be reused.
        File.WriteAllBytes(ear, BuildEar("version-2"));
        File.SetLastWriteTimeUtc(ear, DateTime.UtcNow.AddMinutes(1));
        var second = await scanner.ReadEntryAsync(ear, "web.war!/WEB-INF/lib/core.jar!/com/acme/A.class", ct);

        Assert.Equal("v1", System.Text.Encoding.UTF8.GetString(first));
        Assert.Equal("version-2", System.Text.Encoding.UTF8.GetString(second));
    }

    [Fact]
    public async Task ArchiveScanner_CacheDisabled_StillReadsNestedEntries()
    {
        var scanner = new ArchiveScanner { CacheCapacityBytes = 0 };
        var ear = _temp.WriteFile("app.ear", BuildEar("v1"));

        var bytes = await scanner.ReadEntryAsync(ear, "web.war!/WEB-INF/lib/core.jar!/com/acme/A.class", TestContext.Current.CancellationToken);

        Assert.Equal("v1", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void DecompiledSourceCache_PutThenGet_HitsOnlyForSameBytes()
    {
        var cache = new DecompiledSourceCache(_temp.Combine("cache"), maxBytes: 1024 * 1024);
        var setup = Setup();
        var v1 = DecompiledSourceCache.KeyFor(setup, Classes([1, 2]));
        var v2 = DecompiledSourceCache.KeyFor(setup, Classes([1, 3]));

        cache.Put(v1, "class A {}");

        Assert.True(cache.TryGet(v1, out var source));
        Assert.Equal("class A {}", source);
        Assert.False(cache.TryGet(v2, out _));
        Assert.Equal(v1, DecompiledSourceCache.KeyFor(setup, new Dictionary<string, byte[]> { ["other/dir/A.class"] = [1, 2] }));
    }

    [Fact]
    public void DecompiledSourceCache_Disabled_NeitherReadsNorWrites()
    {
        var cache = new DecompiledSourceCache(_temp.Combine("cache"), maxBytes: 1024 * 1024) { Enabled = false };
        var key = DecompiledSourceCache.KeyFor(Setup(), Classes([1]));

        cache.Put(key, "class A {}");
        cache.Enabled = true;

        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    public void DecompiledSourceCache_Trim_KeepsMostRecentlyUsedWithinLimit()
    {
        var cache = new DecompiledSourceCache(_temp.Combine("cache"), maxBytes: 250);
        var keys = Enumerable.Range(0, 5).Select(i => DecompiledSourceCache.KeyFor(Setup(), Classes([(byte)i]))).ToList();
        for (var i = 0; i < keys.Count; i++)
        {
            cache.Put(keys[i], new string('x', 100));
            File.SetLastWriteTimeUtc(Directory.GetFiles(cache.Folder, keys[i] + ".java", SearchOption.AllDirectories)[0], DateTime.UtcNow.AddMinutes(i));
        }

        cache.Trim();

        Assert.True(cache.GetSizeBytes() <= 250);
        Assert.True(cache.TryGet(keys[^1], out _));
        Assert.False(cache.TryGet(keys[0], out _));
    }

    [Fact]
    public async Task CachingJavaDecompiler_DecompilesOnlyMisses()
    {
        var cache = new DecompiledSourceCache(_temp.Combine("cache"), maxBytes: 1024 * 1024);
        var inner = new CountingDecompiler();
        var decompiler = new CachingJavaDecompiler(inner, cache);
        var setup = Setup();
        var ct = TestContext.Current.CancellationToken;
        var a = new ClassGroup("p/A.class", new Dictionary<string, byte[]> { ["p/A.class"] = [1] });
        var b = new ClassGroup("p/B.class", new Dictionary<string, byte[]> { ["p/B.class"] = [2] });

        await decompiler.DecompileManyAsync(setup, [a], ct);
        var both = await decompiler.DecompileManyAsync(setup, [a, b], ct);
        var single = await decompiler.DecompileAsync(setup, "A.class", new Dictionary<string, byte[]> { ["A.class"] = [1] }, ct);

        Assert.Equal(["p/A.class", "p/B.class"], inner.Decompiled);
        Assert.Equal("source of p/A.class", both["p/A.class"]);
        Assert.Equal("source of p/B.class", both["p/B.class"]);
        Assert.Equal("source of p/A.class", single);
    }

    private DecompilerSetup Setup() => new(_temp.WriteFile("java.exe", "fake"), _temp.WriteFile("vineflower-1.0.jar", "jar"));

    private static Dictionary<string, byte[]> Classes(byte[] bytes) => new() { ["com/acme/A.class"] = bytes };

    private static byte[] BuildEar(string classContent)
    {
        var jar = TempDirectory.Zip(new Dictionary<string, object> { ["com/acme/A.class"] = classContent });
        var war = TempDirectory.Zip(new Dictionary<string, object> { ["WEB-INF/lib/core.jar"] = jar });
        return TempDirectory.Zip(new Dictionary<string, object> { ["web.war"] = war });
    }

    private sealed class CountingDecompiler : IJavaDecompiler
    {
        public List<string> Decompiled { get; } = [];

        public Task<string> DecompileAsync(DecompilerSetup setup, string mainClassFile, IReadOnlyDictionary<string, byte[]> classFiles, CancellationToken cancellationToken)
        {
            Decompiled.Add(mainClassFile);
            return Task.FromResult($"source of {mainClassFile}");
        }

        public Task<IReadOnlyDictionary<string, string>> DecompileManyAsync(DecompilerSetup setup, IReadOnlyList<ClassGroup> groups, CancellationToken cancellationToken)
        {
            Decompiled.AddRange(groups.Select(g => g.MainClassPath));
            return Task.FromResult<IReadOnlyDictionary<string, string>>(groups.ToDictionary(g => g.MainClassPath, g => $"source of {g.MainClassPath}"));
        }
    }
}
