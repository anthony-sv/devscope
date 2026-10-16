using Devscope.Core.Logs;
using Devscope.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Devscope.Core.Tests.Logs;

public sealed class LogFilterAndSourceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static LogEntry Entry(string text, LogSeverity severity = LogSeverity.Info, string environment = "prod") =>
        new(1, new LogFileSource(environment, "x.log", 0), text, 1, null, DateTimeOffset.MinValue, severity);

    [Fact]
    public void Compile_TextSearchAndExclude_CaseInsensitiveByDefault()
    {
        var predicate = new LogFilter { Search = "order", Exclude = "heartbeat" }.Compile();

        Assert.True(predicate(Entry("Order 42 created")));
        Assert.False(predicate(Entry("order heartbeat")));
        Assert.False(predicate(Entry("payment done")));
    }

    [Fact]
    public void Compile_RegexWithSeverityAndEnvironment_CombinesWithAnd()
    {
        var predicate = new LogFilter
        {
            Search = @"user=\d+",
            UseRegex = true,
            Severities = new HashSet<LogSeverity> { LogSeverity.Error },
            Environments = new HashSet<string> { "beta" },
        }.Compile();

        Assert.True(predicate(Entry("failed for user=7", LogSeverity.Error, "beta")));
        Assert.False(predicate(Entry("failed for user=7", LogSeverity.Error, "prod")));
        Assert.False(predicate(Entry("failed for user=7", LogSeverity.Info, "beta")));
        Assert.False(predicate(Entry("failed for user=x", LogSeverity.Error, "beta")));
    }

    [Fact]
    public void Compile_InvalidRegex_ThrowsArgumentException() =>
        Assert.ThrowsAny<ArgumentException>(() => new LogFilter { Search = "([", UseRegex = true }.Compile());

    [Fact]
    public void CreateRegex_BackreferencePattern_FallsBackToBacktrackingEngine()
    {
        var regex = LogFilter.CreateRegex(@"(\w)\1", matchCase: false);

        Assert.Matches(regex, "aardvark");
    }

    [Fact]
    public void Resolve_FolderWithPatterns_FindsRecursivelyAndSkipsCompressed()
    {
        _temp.WriteFile("app/main.log", "x");
        _temp.WriteFile("app/module-a/a.log", "x");
        _temp.WriteFile("app/module-b/b.txt", "x");
        _temp.WriteFile("app/module-b/old.log.gz", "x");
        _temp.WriteFile("app/module-b/notes.md", "x");

        var files = LogSourceResolver.Resolve(new WorkspaceSource { Path = _temp.Combine("app"), IncludePatterns = "*.log;*.txt;*.gz" }, maxFiles: 100);

        Assert.Equal(["a.log", "b.txt", "main.log"], files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Resolve_NonRecursive_OnlyTopLevel()
    {
        _temp.WriteFile("app/main.log", "x");
        _temp.WriteFile("app/sub/a.log", "x");

        var files = LogSourceResolver.Resolve(new WorkspaceSource { Path = _temp.Combine("app"), Recursive = false }, maxFiles: 100);

        Assert.Equal("main.log", Path.GetFileName(Assert.Single(files)));
    }

    [Fact]
    public void Resolve_UnreachablePath_Throws() =>
        Assert.Throws<DirectoryNotFoundException>(() =>
            LogSourceResolver.Resolve(new WorkspaceSource { Path = _temp.Combine("missing") }, maxFiles: 10));

    [Fact]
    public async Task LogSession_TwoEnvironments_MergesInitialBatchByTime()
    {
        _temp.WriteFile("dev/app.log", "2026-10-16 10:00:00 INFO dev-1\n2026-10-16 10:00:02 INFO dev-2\n");
        _temp.WriteFile("prod/app.log", "2026-10-16 10:00:01 ERROR prod-1\n");
        var environments = new[]
        {
            new EnvironmentSelection("dev", [new WorkspaceSource { Path = _temp.Combine("dev") }], 0),
            new EnvironmentSelection("prod", [new WorkspaceSource { Path = _temp.Combine("prod") }], 1),
        };

        await using var session = new LogSession(environments, new LogSessionOptions { PollInterval = TimeSpan.FromMilliseconds(50) }, NullLogger<LogSession>.Instance);
        session.Start();

        // Initial batch holds all completed events; the last event of each file arrives on the next (quiet) poll.
        var received = new List<LogEntry>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (var batch in session.Batches.ReadAllAsync(timeout.Token))
        {
            received.AddRange(batch.Entries);
            if (received.Count >= 3)
            {
                break;
            }
        }

        received.Sort(LogEntry.CompareByTime);
        Assert.Equal(["dev-1", "prod-1", "dev-2"], received.Select(e => e.Text[(e.Text.LastIndexOf(' ') + 1)..]));
        Assert.Equal("prod", received[1].Source.Environment);
    }

    [Fact]
    public async Task JsonWorkspaceStore_RoundTrips()
    {
        var store = new JsonWorkspaceStore(_temp.Combine("ws", "workspaces.json"), NullLogger<JsonWorkspaceStore>.Instance);
        var workspace = new Workspace
        {
            Name = "Billing",
            Environments =
            [
                new WorkspaceEnvironment { Name = "local", Sources = [new WorkspaceSource { Path = @"D:\logs\app" }] },
                new WorkspaceEnvironment { Name = "prod-1", Sources = [new WorkspaceSource { Path = @"\\server2\logs\prod\app", Recursive = false }] },
            ],
            Highlights = [new HighlightRule { Pattern = "ORD-\\d+", IsRegex = true, Color = "#3FB950" }],
        };

        await store.SaveAsync([workspace], TestContext.Current.CancellationToken);
        var loaded = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(workspace.Id, loaded.Id);
        Assert.Equal(@"\\server2\logs\prod\app", loaded.Environments[1].Sources[0].Path);
        Assert.False(loaded.Environments[1].Sources[0].Recursive);
        Assert.True(loaded.Highlights[0].IsRegex);
    }
}
