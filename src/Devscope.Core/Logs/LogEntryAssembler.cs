using System.Text;

namespace Devscope.Core.Logs;

/// <summary>
/// Groups raw lines of one file into <see cref="LogEntry"/> events.
/// A line with a timestamp starts a new event; following lines without one (stack traces, wrapped
/// payloads) are attached to it. Files without timestamps produce one event per line, except for
/// obviously indented continuation lines.
/// </summary>
public sealed class LogEntryAssembler(LogFileSource source, DateOnly fallbackDate, Func<long> nextId)
{
    /// <summary>Upper bound on lines grouped into one event, so a single bad file cannot create a giant entry.</summary>
    public const int MaxLinesPerEntry = 1000;

    private readonly StringBuilder _pendingText = new();
    private int _pendingLines;
    private DateTimeOffset? _pendingTimestamp;
    private LogSeverity _pendingSeverity;
    private DateTimeOffset _lastKnownTime = DateTimeOffset.MinValue;

    /// <summary>Whether an event is buffered waiting for possible continuation lines.</summary>
    public bool HasPending => _pendingLines > 0;

    /// <summary>Feeds one line (without line terminator). Completed events are added to <paramref name="output"/>.</summary>
    public void Add(string line, List<LogEntry> output)
    {
        var hasTimestamp = LogLineParser.TryParseTimestamp(line, fallbackDate, out var timestamp);
        if (!hasTimestamp && HasPending && _pendingLines < MaxLinesPerEntry
            && (_pendingTimestamp is not null || LogLineParser.LooksLikeContinuation(line)))
        {
            _pendingText.Append('\n').Append(line);
            _pendingLines++;

            // Severity is usually on the first line, but take it from the body if the header had none.
            if (_pendingSeverity == LogSeverity.Unknown)
            {
                _pendingSeverity = LogLineParser.DetectSeverity(line);
            }

            return;
        }

        Flush(output);
        if (line.Length == 0)
        {
            return;
        }

        _pendingText.Append(line);
        _pendingLines = 1;
        _pendingTimestamp = hasTimestamp ? timestamp : null;
        _pendingSeverity = LogLineParser.DetectSeverity(line);
    }

    /// <summary>Emits the buffered event, if any.</summary>
    public void Flush(List<LogEntry> output)
    {
        if (!HasPending)
        {
            return;
        }

        if (_pendingTimestamp is { } ts)
        {
            _lastKnownTime = ts;
        }

        output.Add(new LogEntry(nextId(), source, _pendingText.ToString(), _pendingLines, _pendingTimestamp, _lastKnownTime, _pendingSeverity));
        _pendingText.Clear();
        _pendingLines = 0;
        _pendingTimestamp = null;
        _pendingSeverity = LogSeverity.Unknown;
    }

    /// <summary>Discards buffered state, e.g. after the file was truncated or rotated.</summary>
    public void Reset()
    {
        _pendingText.Clear();
        _pendingLines = 0;
        _pendingTimestamp = null;
        _pendingSeverity = LogSeverity.Unknown;
    }
}
