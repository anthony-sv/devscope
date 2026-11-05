using Devscope.Core.Logs;

namespace Devscope.Core.Tests.Logs;

public sealed class LogColumnFilterTests
{
    private static readonly DateTimeOffset At1030 = new(new DateTime(2026, 10, 16, 10, 30, 15, DateTimeKind.Local));

    private static LogEntry Entry(
        string text = "message",
        string environment = "prod",
        string file = @"C:\logs\node1\app.log",
        DateTimeOffset? timestamp = null,
        int lines = 1) =>
        new(1, new LogFileSource(environment, file, 0), text, lines, timestamp, timestamp ?? DateTimeOffset.MinValue, LogSeverity.Info);

    [Fact]
    public void Compile_Files_ShowsOnlySelectedFilesIgnoringCase()
    {
        var predicate = new LogFilter { Files = new HashSet<string> { @"c:\LOGS\node1\app.log" } }.Compile();

        Assert.True(predicate(Entry()));
        Assert.False(predicate(Entry(file: @"C:\logs\node2\app.log")));
    }

    [Fact]
    public void Compile_SourceText_MatchesEnvironmentOrPath()
    {
        var predicate = new LogFilter { SourceText = "node2" }.Compile();

        Assert.True(predicate(Entry(file: @"C:\logs\node2\app.log")));
        Assert.True(predicate(Entry(environment: "prod-node2")));
        Assert.False(predicate(Entry()));
    }

    [Fact]
    public void Compile_MessageText_LooksOnlyAtFirstLine()
    {
        var predicate = new LogFilter { MessageText = "timeout" }.Compile();

        Assert.True(predicate(Entry("ERROR timeout calling billing")));
        Assert.False(predicate(Entry("ERROR failed\n\tCaused by: timeout", lines: 2)));
    }

    [Fact]
    public void Compile_MultilineOnly_HidesSingleLineEvents()
    {
        var predicate = new LogFilter { MultilineOnly = true }.Compile();

        Assert.True(predicate(Entry("boom\n\tat x", lines: 2)));
        Assert.False(predicate(Entry("fine")));
    }

    [Fact]
    public void Compile_TimeOfDayRange_IncludesTheWholeUpperMinuteAndHidesUntimed()
    {
        Assert.True(LogTimeBound.TryParse("10:00", isUpperBound: false, At1030, out var from));
        Assert.True(LogTimeBound.TryParse("10:30", isUpperBound: true, At1030, out var to));
        var predicate = new LogFilter { From = from, To = to }.Compile();

        Assert.True(predicate(Entry(timestamp: At1030)));
        Assert.False(predicate(Entry(timestamp: At1030.AddMinutes(1))));
        Assert.False(predicate(Entry(timestamp: At1030.AddHours(-1))));
        Assert.False(predicate(Entry()));
    }

    [Theory]
    [InlineData("2026-10-16 10:30", false, "2026-10-16T10:30:00")]
    [InlineData("2026-10-16 10:30", true, "2026-10-16T10:30:59.9999999")]
    [InlineData("2026-10-16", true, "2026-10-16T23:59:59.9999999")]
    [InlineData("2026-10-16T10:30:15", false, "2026-10-16T10:30:15")]
    public void TryParse_DateTimes_AreAbsolute(string text, bool upper, string expectedLocal)
    {
        Assert.True(LogTimeBound.TryParse(text, upper, At1030, out var bound));

        Assert.Equal(DateTime.Parse(expectedLocal, System.Globalization.CultureInfo.InvariantCulture), bound!.Instant!.Value.LocalDateTime);
        Assert.Null(bound.TimeOfDay);
    }

    [Fact]
    public void TryParse_Relative_CountsBackFromNow()
    {
        Assert.True(LogTimeBound.TryParse("-15m", isUpperBound: false, At1030, out var bound));

        Assert.Equal(At1030.AddMinutes(-15), bound!.Instant);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_Empty_MeansNoBound(string text)
    {
        Assert.True(LogTimeBound.TryParse(text, isUpperBound: false, At1030, out var bound));
        Assert.Null(bound);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("25:00")]
    [InlineData("-5x")]
    public void TryParse_Garbage_Fails(string text) =>
        Assert.False(LogTimeBound.TryParse(text, isUpperBound: false, At1030, out _));
}
