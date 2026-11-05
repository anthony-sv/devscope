using System.Text;

namespace Devscope.Core.Logs;

/// <summary>
/// Groups raw lines of one file into <see cref="LogEntry"/> events.
/// A line with a timestamp starts a new event; following lines without one (stack traces, wrapped
/// payloads) are attached to it. Files without timestamps produce one event per line, except for
/// obviously indented continuation lines.
/// </summary>
/// <remarks>
/// A live file is flushed whenever it goes quiet, but a logger often writes the header line and its stack trace
/// in separate writes. Continuation lines that arrive after the event was emitted are therefore not dropped into
/// events of their own: the event is reopened and emitted again as a revision (same <see cref="LogEntry.Id"/>,
/// higher <see cref="LogEntry.Revision"/>) that replaces the earlier one.
/// </remarks>
public sealed class LogEntryAssembler(LogFileSource source, DateOnly fallbackDate, Func<long> nextId)
{
    /// <summary>Upper bound on lines grouped into one event, so a single bad file cannot create a giant entry.</summary>
    public const int MaxLinesPerEntry = 1000;

    private readonly StringBuilder _pendingText = new();
    private int _pendingLines;
    private DateTimeOffset? _pendingTimestamp;
    private LogSeverity _pendingSeverity;
    private LogEntry? _pendingRevisionOf;
    private LogEntry? _lastEmitted;
    private DateTimeOffset _lastKnownTime = DateTimeOffset.MinValue;

    /// <summary>Whether an event is buffered waiting for possible continuation lines.</summary>
    public bool HasPending => _pendingLines > 0;

    /// <summary>Feeds one line (without line terminator). Completed events are added to <paramref name="output"/>.</summary>
    public void Add(string line, List<LogEntry> output)
    {
        var hasTimestamp = LogLineParser.TryParseTimestamp(line, fallbackDate, out var timestamp);
        if (!hasTimestamp && !HasPending && line.Length > 0 && _lastEmitted is { } last
            && last.LineCount < MaxLinesPerEntry && (last.Timestamp is not null || LogLineParser.LooksLikeContinuation(line)))
        {
            // The rest of an event that was already emitted (e.g. its stack trace was written a moment later).
            Reopen(last);
        }

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

        _lastEmitted = null;
        _pendingText.Append(line);
        _pendingLines = 1;
        _pendingTimestamp = hasTimestamp ? timestamp : null;
        _pendingSeverity = LogLineParser.DetectSeverity(line);
    }

    /// <summary>Emits the buffered event, if any.</summary>
    public void Flush(List<LogEntry> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!HasPending)
        {
            return;
        }

        LogEntry entry;
        if (_pendingRevisionOf is { } previous)
        {
            entry = new LogEntry(previous.Id, source, _pendingText.ToString(), _pendingLines, previous.Timestamp, previous.SortTime, _pendingSeverity, previous.Revision + 1);
        }
        else
        {
            if (_pendingTimestamp is { } ts)
            {
                _lastKnownTime = ts;
            }

            entry = new LogEntry(nextId(), source, _pendingText.ToString(), _pendingLines, _pendingTimestamp, _lastKnownTime, _pendingSeverity);
        }

        output.Add(entry);
        _lastEmitted = entry;
        ClearPending();
    }

    /// <summary>Discards buffered state, e.g. after the file was truncated or rotated.</summary>
    public void Reset()
    {
        ClearPending();
        _lastEmitted = null;
    }

    private void Reopen(LogEntry entry)
    {
        _pendingText.Append(entry.Text);
        _pendingLines = entry.LineCount;
        _pendingTimestamp = entry.Timestamp;
        _pendingSeverity = entry.Severity;
        _pendingRevisionOf = entry;
    }

    private void ClearPending()
    {
        _pendingText.Clear();
        _pendingLines = 0;
        _pendingTimestamp = null;
        _pendingSeverity = LogSeverity.Unknown;
        _pendingRevisionOf = null;
    }
}
