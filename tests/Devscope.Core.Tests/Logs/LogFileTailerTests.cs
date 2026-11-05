using System.Text;
using Devscope.Core.Logs;

namespace Devscope.Core.Tests.Logs;

public sealed class LogFileTailerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private long _id;

    public void Dispose() => _temp.Dispose();

    private LogFileTailer CreateTailer(string path, long initialTailBytes = 0) =>
        new(new LogFileSource("test", path, 0), initialTailBytes, () => Interlocked.Increment(ref _id));

    private static async Task<List<LogEntry>> PollUntilQuietAsync(LogFileTailer tailer)
    {
        // First poll reads new data; the second (quiet) poll flushes the last buffered event.
        var entries = new List<LogEntry>(await tailer.PollAsync(TestContext.Current.CancellationToken));
        entries.AddRange(await tailer.PollAsync(TestContext.Current.CancellationToken));
        return entries;
    }

    [Fact]
    public async Task PollAsync_StackTrace_IsGroupedIntoOneEntry()
    {
        var path = _temp.WriteFile("app.log",
            "2026-10-16 10:00:00,000 INFO  start\r\n" +
            "2026-10-16 10:00:01,000 ERROR boom\r\n" +
            "java.lang.IllegalStateException: bad\r\n" +
            "\tat com.acme.Foo.bar(Foo.java:42)\r\n" +
            "2026-10-16 10:00:02,000 INFO  recovered\r\n");

        var entries = await PollUntilQuietAsync(CreateTailer(path));

        Assert.Equal(3, entries.Count);
        var error = entries[1];
        Assert.Equal(LogSeverity.Error, error.Severity);
        Assert.Equal(3, error.LineCount);
        Assert.Equal("2026-10-16 10:00:01,000 ERROR boom", error.FirstLine);
        Assert.Contains("\tat com.acme.Foo.bar", error.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollAsync_StackTraceWrittenAfterHeaderWasFlushed_RevisesTheSameEvent()
    {
        // The logger writes the header line, the file goes quiet (so the event is flushed), then the trace follows.
        var path = _temp.WriteFile("app.log", "2026-10-16 10:00:01,000 ERROR boom\n");
        var tailer = CreateTailer(path);
        var first = Assert.Single(await PollUntilQuietAsync(tailer));

        await File.AppendAllTextAsync(path,
            "java.lang.IllegalStateException: bad\n\tat com.acme.Foo.bar(Foo.java:42)\n2026-10-16 10:00:02,000 INFO  next\n",
            TestContext.Current.CancellationToken);
        var second = await PollUntilQuietAsync(tailer);

        Assert.Equal(2, second.Count);
        var revised = second[0];
        Assert.True(revised.IsRevision);
        Assert.Equal(first.Id, revised.Id);
        Assert.Equal(first.SortTime, revised.SortTime);
        Assert.Equal(3, revised.LineCount);
        Assert.Equal(LogSeverity.Error, revised.Severity);
        Assert.EndsWith("\tat com.acme.Foo.bar(Foo.java:42)", revised.Text, StringComparison.Ordinal);
        Assert.False(second[1].IsRevision);
        Assert.Equal("2026-10-16 10:00:02,000 INFO  next", second[1].Text);
    }

    [Fact]
    public async Task PollAsync_TraceArrivingInSeveralWrites_KeepsRevisingOneEvent()
    {
        var path = _temp.WriteFile("app.log", "2026-10-16 10:00:01,000 ERROR boom\n");
        var tailer = CreateTailer(path);
        var first = Assert.Single(await PollUntilQuietAsync(tailer));

        await File.AppendAllTextAsync(path, "java.lang.IllegalStateException: bad\n", TestContext.Current.CancellationToken);
        var second = Assert.Single(await PollUntilQuietAsync(tailer));
        await File.AppendAllTextAsync(path, "\tat com.acme.Foo.bar(Foo.java:42)\n", TestContext.Current.CancellationToken);
        var third = Assert.Single(await PollUntilQuietAsync(tailer));

        Assert.Equal(first.Id, third.Id);
        Assert.Equal(1, second.Revision);
        Assert.Equal(2, third.Revision);
        Assert.Equal(3, third.LineCount);
    }

    [Fact]
    public async Task PollAsync_UntimedFileAfterFlush_PlainLineStartsNewEvent()
    {
        var path = _temp.WriteFile("app.out", "first line\n");
        var tailer = CreateTailer(path);
        await PollUntilQuietAsync(tailer);

        await File.AppendAllTextAsync(path, "second line\n    indented continuation\n", TestContext.Current.CancellationToken);
        var entries = await PollUntilQuietAsync(tailer);

        var entry = Assert.Single(entries);
        Assert.False(entry.IsRevision);
        Assert.Equal("second line\n    indented continuation", entry.Text);
    }

    [Fact]
    public void CollapseRevisions_KeepsNewestRevisionInPlaceOfTheOriginal()
    {
        var path = _temp.WriteFile("app.log", string.Empty);
        var assembler = new LogEntryAssembler(new LogFileSource("test", path, 0), new DateOnly(2026, 10, 16), () => Interlocked.Increment(ref _id));
        var output = new List<LogEntry>();
        assembler.Add("2026-10-16 10:00:01,000 ERROR boom", output);
        assembler.Flush(output);
        assembler.Add("\tat a.B.c(B.java:1)", output);
        assembler.Flush(output);
        assembler.Add("2026-10-16 10:00:02,000 INFO next", output);
        assembler.Flush(output);

        var collapsed = LogSession.CollapseRevisions(output);

        Assert.Equal(3, output.Count);
        Assert.Equal(2, collapsed.Count);
        Assert.Equal(2, collapsed[0].LineCount);
        Assert.Equal("2026-10-16 10:00:02,000 INFO next", collapsed[1].Text);
    }

    [Fact]
    public async Task PollAsync_AppendedLines_AreReadIncrementally()
    {
        var path = _temp.WriteFile("app.log", "2026-10-16 10:00:00 INFO one\n");
        var tailer = CreateTailer(path);
        var first = await PollUntilQuietAsync(tailer);

        await File.AppendAllTextAsync(path, "2026-10-16 10:00:01 WARN two\n", TestContext.Current.CancellationToken);
        var second = await PollUntilQuietAsync(tailer);

        Assert.Equal("2026-10-16 10:00:00 INFO one", Assert.Single(first).Text);
        Assert.Equal(LogSeverity.Warning, Assert.Single(second).Severity);
    }

    [Fact]
    public async Task PollAsync_FileTruncated_StartsOver()
    {
        var path = _temp.WriteFile("app.log", "2026-10-16 10:00:00 INFO a fairly long first line\n");
        var tailer = CreateTailer(path);
        await PollUntilQuietAsync(tailer);

        await File.WriteAllTextAsync(path, "2026-10-16 11:00:00 INFO new\n", TestContext.Current.CancellationToken);
        var entries = await PollUntilQuietAsync(tailer);

        Assert.Equal("2026-10-16 11:00:00 INFO new", Assert.Single(entries).Text);
    }

    [Fact]
    public async Task PollAsync_InitialTail_SkipsPartialFirstLine()
    {
        var content = new StringBuilder();
        for (var i = 0; i < 100; i++)
        {
            content.Append(System.Globalization.CultureInfo.InvariantCulture, $"2026-10-16 10:00:{i % 60:00} INFO line {i:000}\n");
        }

        var path = _temp.WriteFile("big.log", content.ToString());

        var entries = await PollUntilQuietAsync(CreateTailer(path, initialTailBytes: 100));

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.StartsWith("2026-10-16", e.Text, StringComparison.Ordinal));
        Assert.EndsWith("line 099", entries[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollAsync_LastLineWithoutNewline_IsEmittedWhenQuiet()
    {
        var path = _temp.WriteFile("app.log", "plain line one\nplain line two");

        var entries = await PollUntilQuietAsync(CreateTailer(path));

        Assert.Equal(["plain line one", "plain line two"], entries.Select(e => e.Text));
    }

    [Fact]
    public async Task PollAsync_Utf16File_IsDecoded()
    {
        var path = _temp.WriteFile("win.log", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("2026-10-16 10:00:00 ERROR ünïcödé\r\n")).ToArray());

        var entries = await PollUntilQuietAsync(CreateTailer(path));

        Assert.Equal("2026-10-16 10:00:00 ERROR ünïcödé", Assert.Single(entries).Text);
    }

    [Fact]
    public async Task PollAsync_MissingFile_ReportsErrorWithoutThrowing()
    {
        var tailer = CreateTailer(_temp.Combine("nope.log"));

        var entries = await tailer.PollAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entries);
        Assert.NotNull(tailer.LastError);
    }
}
