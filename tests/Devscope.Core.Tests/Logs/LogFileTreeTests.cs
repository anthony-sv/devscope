using Devscope.Core.Logs;

namespace Devscope.Core.Tests.Logs;

public sealed class LogFileTreeTests
{
    private static readonly char S = Path.DirectorySeparatorChar;

    private static string P(params string[] parts) => string.Join(S, parts);

    [Fact]
    public void Build_OneEnvironment_GroupsFilesBySubfolderUnderTheSourceRoot()
    {
        var root = P("", "logs", "app");
        var status = new SourceStatus("local", root, 3, null, root,
            [P(root, "SubFolder1", "X.log"), P(root, "SubFolder1", "Y.log"), P(root, "SubFolder2", "G.log"), P(root, "main.log")]);

        var tree = LogFileTree.Build([status]);

        var source = Assert.Single(tree);
        Assert.Equal(root, source.Name);
        Assert.Equal(["SubFolder1", "SubFolder2", "main.log"], source.Children.Select(c => c.Name));
        Assert.Equal(["X.log", "Y.log"], source.Children[0].Children.Select(c => c.Name));
        Assert.True(source.Children[0].Children[0].IsFile);
        Assert.Equal(4, source.Files().Count());
    }

    [Fact]
    public void Build_SeveralEnvironments_AddsEnvironmentLevelAndListsSingleFileSources()
    {
        var file = P("", "srv", "prod", "app.log");
        var tree = LogFileTree.Build(
        [
            new SourceStatus("prod-1", file, 1, null, file, [file]),
            new SourceStatus("prod-2", P("", "srv2"), 1, null, P("", "srv2"), [P("", "srv2", "a", "b", "c.log")]),
        ]);

        Assert.Equal(["prod-1", "prod-2"], tree.Select(n => n.Name));
        Assert.Equal("app.log", Assert.Single(tree[0].Children).Name);

        // a/b has a single folder child, so it is shown as one node.
        var source = Assert.Single(tree[1].Children);
        var folder = Assert.Single(source.Children);
        Assert.Equal(P("a", "b"), folder.Name);
        Assert.Equal("c.log", Assert.Single(folder.Children).Name);
    }

    [Fact]
    public void Build_SourceWithoutFiles_IsLeftOut()
    {
        var tree = LogFileTree.Build([new SourceStatus("local", P("", "nothing"), 0, "No matching files")]);

        Assert.Empty(tree);
    }
}
