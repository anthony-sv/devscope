using Devscope.Core.Ear;
using Microsoft.Extensions.Logging.Abstractions;

namespace Devscope.Core.Tests.Ear;

public sealed class EarComparerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly EarComparer _comparer = new(new ArchiveScanner(), NullLogger<EarComparer>.Instance);

    public void Dispose() => _temp.Dispose();

    private static byte[] BuildEar(string serviceClass, string webXml, bool includeLegacy, bool includeNew)
    {
        var jarEntries = new Dictionary<string, object> { ["com/acme/Service.class"] = serviceClass };
        var jar = TempDirectory.Zip(jarEntries);

        var warEntries = new Dictionary<string, object>
        {
            ["WEB-INF/web.xml"] = webXml,
            ["WEB-INF/lib/acme-core.jar"] = jar,
            ["index.jsp"] = "<html/>",
        };
        if (includeLegacy)
        {
            warEntries["legacy.jsp"] = "old";
        }

        if (includeNew)
        {
            warEntries["new.jsp"] = "new";
        }

        return TempDirectory.Zip(new Dictionary<string, object>
        {
            ["META-INF/application.xml"] = "<application/>",
            ["web.war"] = TempDirectory.Zip(warEntries),
        });
    }

    [Fact]
    public async Task CompareAsync_NestedChanges_ReportsLeafStatusesAndMarksContainersModified()
    {
        // Arrange
        var left = _temp.WriteFile("left.ear", BuildEar("v1", "<web-app version=\"1\"/>", includeLegacy: true, includeNew: false));
        var right = _temp.WriteFile("right.ear", BuildEar("v2", "<web-app version=\"1\"/>", includeLegacy: false, includeNew: true));

        // Act
        var result = await _comparer.CompareAsync(left, right, progress: null, TestContext.Current.CancellationToken);
        var byPath = result.Entries.ToDictionary(e => e.Path);

        // Assert
        Assert.Equal(DiffStatus.Modified, byPath["web.war!/WEB-INF/lib/acme-core.jar!/com/acme/Service.class"].Status);
        Assert.Equal(DiffStatus.Removed, byPath["web.war!/legacy.jsp"].Status);
        Assert.Equal(DiffStatus.Added, byPath["web.war!/new.jsp"].Status);
        Assert.Equal(DiffStatus.Unchanged, byPath["web.war!/WEB-INF/web.xml"].Status);
        Assert.Equal(DiffStatus.Modified, byPath["web.war"].Status);
        Assert.Equal(DiffStatus.Modified, byPath["web.war!/WEB-INF/lib/acme-core.jar"].Status);
        Assert.Equal((1, 1, 1), (result.Modified, result.Added, result.Removed));
    }

    [Fact]
    public async Task CompareAsync_ArchiveVersusExplodedFolderWithSameContent_ReportsNoDifferences()
    {
        // Arrange: same content, once packaged and once exploded (as some servers deploy it).
        var ear = _temp.WriteFile("app.ear", TempDirectory.Zip(new Dictionary<string, object>
        {
            ["META-INF/application.xml"] = "<application/>",
            ["lib/util.jar"] = TempDirectory.Zip(new Dictionary<string, object> { ["Util.class"] = "bytes" }),
        }));
        _temp.WriteFile("exploded/META-INF/application.xml", "<application/>");
        _temp.WriteFile("exploded/lib/util.jar", TempDirectory.Zip(new Dictionary<string, object> { ["Util.class"] = "bytes" }));

        // Act
        var result = await _comparer.CompareAsync(ear, _temp.Combine("exploded"), null, TestContext.Current.CancellationToken);

        // Assert
        Assert.All(result.Entries, e => Assert.Equal(DiffStatus.Unchanged, e.Status));
        Assert.Equal(2, result.Unchanged);
    }

    [Fact]
    public async Task CompareAsync_MissingPath_ThrowsFileNotFound()
    {
        var existing = _temp.WriteFile("a.ear", TempDirectory.Zip(new Dictionary<string, object> { ["x"] = "y" }));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _comparer.CompareAsync(existing, _temp.Combine("missing.ear"), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadEntryAsync_DeeplyNestedEntry_ReturnsContent()
    {
        var ear = _temp.WriteFile("app.ear", BuildEar("class-bytes", "<web-app/>", false, false));
        var scanner = new ArchiveScanner();

        var bytes = await scanner.ReadEntryAsync(ear, "web.war!/WEB-INF/lib/acme-core.jar!/com/acme/Service.class", TestContext.Current.CancellationToken);

        Assert.Equal("class-bytes", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Compare_SameSizeDifferentCrc_IsModified()
    {
        var left = new Dictionary<string, ArchiveEntryInfo> { ["a.txt"] = new("a.txt", 3, 111, false) };
        var right = new Dictionary<string, ArchiveEntryInfo> { ["a.txt"] = new("a.txt", 3, 222, false) };

        var result = EarComparer.Compare(left, right);

        Assert.Equal(DiffStatus.Modified, Assert.Single(result).Status);
    }
}
