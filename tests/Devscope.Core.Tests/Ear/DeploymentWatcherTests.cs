using Devscope.Core.Ear;

namespace Devscope.Core.Tests.Ear;

public sealed class DeploymentWatcherTests : IDisposable
{
    private static readonly DeploymentWatcherOptions Fast = new()
    {
        QuietPeriod = TimeSpan.FromMilliseconds(200),
        LocalPollInterval = TimeSpan.FromMilliseconds(200),
        NetworkPollInterval = TimeSpan.FromMilliseconds(200),
    };

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ArchiveRewritten_RaisesChangedOnce()
    {
        var ear = _temp.WriteFile("app.ear", "v1");
        using var watcher = new DeploymentWatcher([ear], Fast);
        var changes = 0;
        var changed = new TaskCompletionSource();
        watcher.Changed += (_, _) =>
        {
            Interlocked.Increment(ref changes);
            changed.TrySetResult();
        };
        await watcher.StartAsync();

        File.WriteAllText(ear, "version 2");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(600, TestContext.Current.CancellationToken);

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ExplodedFolderGainsFile_RaisesChanged()
    {
        _temp.WriteFile("app/WEB-INF/web.xml", "<web-app/>");
        using var watcher = new DeploymentWatcher([_temp.Combine("app")], Fast);
        var changed = new TaskCompletionSource();
        watcher.Changed += (_, _) => changed.TrySetResult();
        await watcher.StartAsync();

        _temp.WriteFile("app/WEB-INF/classes/A.class", "new");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NothingChanges_DoesNotRaise()
    {
        var ear = _temp.WriteFile("app.ear", "v1");
        using var watcher = new DeploymentWatcher([ear], Fast);
        var raised = false;
        watcher.Changed += (_, _) => raised = true;
        await watcher.StartAsync();

        await Task.Delay(800, TestContext.Current.CancellationToken);

        Assert.False(raised);
    }

    [Fact]
    public async Task ArchiveDeleted_WaitsUntilItIsBack()
    {
        var ear = _temp.WriteFile("app.ear", "v1");
        using var watcher = new DeploymentWatcher([ear], Fast);
        var changed = new TaskCompletionSource();
        watcher.Changed += (_, _) => changed.TrySetResult();
        await watcher.StartAsync();

        File.Delete(ear);
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(changed.Task.IsCompleted);

        File.WriteAllText(ear, "redeployed");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(@"\\server\share\app.ear", true)]
    [InlineData(@"C:\deploy\app.ear", false)]
    public void IsNetworkPath_DetectsUncPaths(string path, bool expected) =>
        Assert.Equal(expected, DeploymentWatcher.IsNetworkPath(path));
}
