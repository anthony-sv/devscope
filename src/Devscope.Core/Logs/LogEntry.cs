namespace Devscope.Core.Logs;

/// <summary>Normalized severity, detected heuristically from unstructured log text.</summary>
public enum LogSeverity
{
    Unknown,
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Fatal,
}

/// <summary>A concrete log file being read, and the environment it belongs to.</summary>
/// <param name="Environment">Environment name from the workspace (e.g. "dev", "prod-node2").</param>
/// <param name="FilePath">Full local or UNC path.</param>
/// <param name="ColorIndex">Stable index used by the UI to color the environment tag.</param>
public sealed record LogFileSource(string Environment, string FilePath, int ColorIndex)
{
    public string FileName { get; } = Path.GetFileName(FilePath);
}

/// <summary>
/// One logical log event. Multi-line events (stack traces, wrapped messages) are grouped
/// into a single entry whose <see cref="Text"/> contains every line.
/// </summary>
public sealed class LogEntry
{
    internal LogEntry(long id, LogFileSource source, string text, int lineCount, DateTimeOffset? timestamp, DateTimeOffset sortTime, LogSeverity severity, int revision = 0)
    {
        Id = id;
        Revision = revision;
        Source = source;
        Text = text;
        LineCount = lineCount;
        Timestamp = timestamp;
        SortTime = sortTime;
        Severity = severity;

        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        FirstLine = newline < 0 ? text : text[..newline].TrimEnd('\r');
    }

    /// <summary>Monotonic id in arrival order; used as a stable tiebreaker when sorting.</summary>
    public long Id { get; }

    /// <summary>
    /// 0 for a new event. Higher when lines (typically a stack trace) were written after the event was first
    /// emitted: the entry then replaces the earlier one with the same <see cref="Id"/>, which has the same position in time.
    /// </summary>
    public int Revision { get; }

    /// <summary>Whether this entry replaces an earlier entry with the same <see cref="Id"/>.</summary>
    public bool IsRevision => Revision > 0;

    public LogFileSource Source { get; }

    /// <summary>Full text of the event, lines separated by <c>\n</c>.</summary>
    public string Text { get; }

    public string FirstLine { get; }

    public int LineCount { get; }

    /// <summary>Timestamp parsed from the line, if any.</summary>
    public DateTimeOffset? Timestamp { get; }

    /// <summary>Parsed timestamp, or the last timestamp seen in the same file, used for merging files.</summary>
    public DateTimeOffset SortTime { get; }

    public LogSeverity Severity { get; }

    public bool IsMultiline => LineCount > 1;

    /// <summary>Orders by time, then by arrival.</summary>
    public static int CompareByTime(LogEntry? x, LogEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var byTime = x.SortTime.CompareTo(y.SortTime);
        return byTime != 0 ? byTime : x.Id.CompareTo(y.Id);
    }
}
