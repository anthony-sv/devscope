using Devscope.Core.Logs;

namespace Devscope.Core.Tests.Logs;

public sealed class LogLineParserTests
{
    private static readonly DateOnly FileDate = new(2026, 10, 16);

    [Theory]
    [InlineData("2026-10-16 12:34:56,789 INFO  [main] Started", 2026, 10, 16, 12, 34, 56, 789)]
    [InlineData("2026-10-16T12:34:56.789+02:00 level=info msg=ok", 2026, 10, 16, 12, 34, 56, 789)]
    [InlineData("[2026-10-16 12:34:56] ERROR boom", 2026, 10, 16, 12, 34, 56, 0)]
    [InlineData("2026/10/16 12:34:56 warn", 2026, 10, 16, 12, 34, 56, 0)]
    [InlineData("[10/16/26 12:34:56:789 CEST] 0000004a SystemOut     O hello", 2026, 10, 16, 12, 34, 56, 789)]
    [InlineData("16/10/2026 12:34:56 something", 2026, 10, 16, 12, 34, 56, 0)]
    [InlineData("16-Oct-2026 12:34:56.789 INFO [main] org.apache.catalina.startup.Catalina.start", 2026, 10, 16, 12, 34, 56, 789)]
    [InlineData("12:34:56.789 [main] DEBUG c.a.Foo - hi", 2026, 10, 16, 12, 34, 56, 789)]
    public void TryParseTimestamp_CommonFormats_Parses(string line, int y, int mo, int d, int h, int mi, int s, int ms)
    {
        Assert.True(LogLineParser.TryParseTimestamp(line, FileDate, out var ts));
        Assert.Equal(new DateTime(y, mo, d, h, mi, s, ms), ts.DateTime);
    }

    [Theory]
    [InlineData("\tat com.acme.Foo.bar(Foo.java:42)")]
    [InlineData("Caused by: java.lang.IllegalStateException")]
    [InlineData("just a plain message")]
    [InlineData("2026-13-45 99:99:99 not a date")]
    public void TryParseTimestamp_NonTimestampLines_ReturnsFalse(string line) =>
        Assert.False(LogLineParser.TryParseTimestamp(line, FileDate, out _));

    [Fact]
    public void TryParseTimestamp_ExplicitUtc_KeepsOffset()
    {
        Assert.True(LogLineParser.TryParseTimestamp("2026-10-16T10:00:00Z hi", FileDate, out var ts));
        Assert.Equal(TimeSpan.Zero, ts.Offset);
    }

    [Theory]
    [InlineData("2026-10-16 12:34:56,789 ERROR [main] Failed", LogSeverity.Error)]
    [InlineData("2026-10-16 12:34:56,789 [WARN] slow", LogSeverity.Warning)]
    [InlineData("2026-10-16 12:34:56 SEVERE: java.util.logging style", LogSeverity.Error)]
    [InlineData("{\"time\":\"x\",\"level\":\"debug\",\"msg\":\"m\"}", LogSeverity.Debug)]
    [InlineData("ts=1 level=fatal msg=down", LogSeverity.Fatal)]
    [InlineData("[10/16/26 12:34:56:789 CEST] 0000004a SystemErr     R java.lang.NullPointerException", LogSeverity.Error)]
    [InlineData("[10/16/26 12:34:56:789 CEST] 0000004a WebContainer  W SRVE0190E: File not found", LogSeverity.Warning)]
    [InlineData("2026-10-16 12:34:56 [INF] Serilog style", LogSeverity.Info)]
    [InlineData("user info updated", LogSeverity.Unknown)]
    [InlineData("com.acme.ERROR_CODES loaded", LogSeverity.Unknown)]
    public void DetectSeverity_CommonFormats(string line, LogSeverity expected) =>
        Assert.Equal(expected, LogLineParser.DetectSeverity(line));
}
