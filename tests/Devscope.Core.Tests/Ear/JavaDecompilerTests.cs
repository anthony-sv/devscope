using Devscope.Core.Ear;

namespace Devscope.Core.Tests.Ear;

public sealed class JavaDecompilerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Fixture(string version, string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", version, file);

    private static Dictionary<string, byte[]> ClassFiles(string version) => new(StringComparer.Ordinal)
    {
        ["InvoiceService.class"] = File.ReadAllBytes(Fixture(version, "InvoiceService.class")),
        ["InvoiceService$Result.class"] = File.ReadAllBytes(Fixture(version, "InvoiceService$Result.class")),
    };

    /// <summary>Real Java + the jar fetched by tools/Get-Vineflower.ps1; tests that need them are skipped otherwise.</summary>
    private static DecompilerSetup RequireDecompiler()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "tools", "vineflower")))
        {
            folder = folder.Parent;
        }

        var searchFolders = folder is null ? [] : new[] { Path.Combine(folder.FullName, "tools", "vineflower") };
        var setup = DecompilerLocator.Locate(javaOverride: null, jarOverride: null, searchFolders, out var message);
        Assert.SkipWhen(setup is null, message);
        return setup!;
    }

    [Fact]
    public async Task DecompileAsync_RealClassWithInnerClass_ReturnsJavaSource()
    {
        var setup = RequireDecompiler();

        var source = await new VineflowerDecompiler().DecompileAsync(setup, "InvoiceService.class", ClassFiles("v2"), TestContext.Current.CancellationToken);

        Assert.Contains("package com.acme.billing;", source, StringComparison.Ordinal);
        Assert.Contains("public int postedCount()", source, StringComparison.Ordinal);
        Assert.Contains("public static class Result", source, StringComparison.Ordinal);
        Assert.Contains("new IllegalArgumentException(\"invoiceId\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecompiledVersions_DiffShowsMethodLevelChanges()
    {
        var setup = RequireDecompiler();
        var decompiler = new VineflowerDecompiler();
        var ct = TestContext.Current.CancellationToken;

        var left = await decompiler.DecompileAsync(setup, "InvoiceService.class", ClassFiles("v1"), ct);
        var right = await decompiler.DecompileAsync(setup, "InvoiceService.class", ClassFiles("v2"), ct);
        var diff = ContentDiff.BuildText(left, right, "Decompiled with Vineflower");

        Assert.True(diff.IsText);
        Assert.StartsWith("Decompiled with Vineflower · ", diff.Summary, StringComparison.Ordinal);
        Assert.Contains(diff.Lines, l => l.LeftText.Contains("retries = 3", StringComparison.Ordinal) && l.RightText.Contains("retries = 5", StringComparison.Ordinal));
        Assert.Contains(diff.Lines, l => l.RightKind == DiffLineKind.Inserted && l.RightText.Contains("postedCount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DecompileAsync_JavaMissing_ThrowsInvalidOperation()
    {
        var setup = new DecompilerSetup(_temp.Combine("no-java.exe"), _temp.Combine("vineflower.jar"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VineflowerDecompiler().DecompileAsync(setup, "A.class", new Dictionary<string, byte[]> { ["A.class"] = [0xCA] }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Locate_JarInSearchFolder_PicksHighestVersionName()
    {
        var java = _temp.WriteFile("jdk/bin/java.exe", "fake");
        _temp.WriteFile("tools/vineflower-1.10.0.jar", "old");
        var newest = _temp.WriteFile("tools/vineflower-1.12.0.jar", "new");

        var setup = DecompilerLocator.Locate(java, jarOverride: null, [_temp.Combine("missing"), _temp.Combine("tools")], out var message);

        Assert.NotNull(setup);
        Assert.Equal(Path.GetFullPath(newest), Path.GetFullPath(setup.JarPath));
        Assert.StartsWith("Ready", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Locate_ExplicitPathsThatDoNotExist_ExplainsWhich()
    {
        var java = _temp.WriteFile("java.exe", "fake");

        var setup = DecompilerLocator.Locate(java, _temp.Combine("nope.jar"), [], out var message);

        Assert.Null(setup);
        Assert.Contains("nope.jar", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeClassVersionChange_DifferentMajors_DescribesUpgrade()
    {
        byte[] java17 = [0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 61];
        byte[] java21 = [0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 65];

        Assert.Equal("Java 17 → Java 21", ContentDiff.DescribeClassVersionChange(java17, java21));
        Assert.Null(ContentDiff.DescribeClassVersionChange(java17, java17));
    }
}
