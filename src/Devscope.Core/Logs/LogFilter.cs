using System.Text.RegularExpressions;

namespace Devscope.Core.Logs;

/// <summary>User-defined filter over log entries. Immutable; compile once and reuse for many entries.</summary>
public sealed record LogFilter
{
    public string Search { get; init; } = string.Empty;

    public string Exclude { get; init; } = string.Empty;

    /// <summary>Interpret <see cref="Search"/> and <see cref="Exclude"/> as regular expressions.</summary>
    public bool UseRegex { get; init; }

    public bool MatchCase { get; init; }

    /// <summary>Severities to show; null or empty shows all.</summary>
    public IReadOnlySet<LogSeverity>? Severities { get; init; }

    /// <summary>Environments to show; null or empty shows all.</summary>
    public IReadOnlySet<string>? Environments { get; init; }

    /// <summary>Full paths of the files to show (compared ignoring case); null or empty shows all.</summary>
    public IReadOnlySet<string>? Files { get; init; }

    /// <summary>Column filter: text in the environment name or the file path.</summary>
    public string SourceText { get; init; } = string.Empty;

    /// <summary>Column filter: text (or regex, with <see cref="UseRegex"/>) in the first line of the event.</summary>
    public string MessageText { get; init; } = string.Empty;

    /// <summary>Column filter: events at or after this time. Events without a timestamp are hidden while it is set.</summary>
    public LogTimeBound? From { get; init; }

    /// <summary>Column filter: events at or before this time. Events without a timestamp are hidden while it is set.</summary>
    public LogTimeBound? To { get; init; }

    /// <summary>Only events with more than one line, such as stack traces.</summary>
    public bool MultilineOnly { get; init; }

    /// <summary>Builds a predicate. Throws <see cref="ArgumentException"/> for an invalid regular expression.</summary>
    public Func<LogEntry, bool> Compile()
    {
        var include = BuildMatcher(Search);
        var exclude = BuildMatcher(Exclude);
        var severities = Severities is { Count: > 0 } s ? s : null;
        var environments = Environments is { Count: > 0 } e ? e : null;
        var files = Files is { Count: > 0 } f ? new HashSet<string>(f, StringComparer.OrdinalIgnoreCase) : null;
        var message = BuildMatcher(MessageText);
        var sourceText = string.IsNullOrWhiteSpace(SourceText) ? null : SourceText.Trim();
        var from = From;
        var to = To;
        var multilineOnly = MultilineOnly;

        return entry =>
            (severities is null || severities.Contains(entry.Severity))
            && (environments is null || environments.Contains(entry.Source.Environment))
            && (files is null || files.Contains(entry.Source.FilePath))
            && (!multilineOnly || entry.IsMultiline)
            && (sourceText is null
                || entry.Source.Environment.Contains(sourceText, StringComparison.OrdinalIgnoreCase)
                || entry.Source.FilePath.Contains(sourceText, StringComparison.OrdinalIgnoreCase))
            && ((from is null && to is null) || IsInRange(entry, from, to))
            && (message is null || message(entry.FirstLine))
            && (include is null || include(entry.Text))
            && (exclude is null || !exclude(entry.Text));
    }

    private static bool IsInRange(LogEntry entry, LogTimeBound? from, LogTimeBound? to)
    {
        DateTimeOffset? time = entry.Timestamp ?? (entry.SortTime == DateTimeOffset.MinValue ? null : entry.SortTime);
        return time is { } t && (from is null || from.IsAtOrAfter(t)) && (to is null || to.IsAtOrBefore(t));
    }

    /// <summary>Builds a regex that finds every match of <see cref="Search"/>, for highlighting. Null when there is no search.</summary>
    public Regex? BuildHighlightRegex() =>
        string.IsNullOrEmpty(Search) ? null : CreateRegex(UseRegex ? Search : Regex.Escape(Search), MatchCase);

    /// <summary>
    /// Creates a regex that is safe to run on untrusted patterns: the non-backtracking engine runs in linear time;
    /// patterns it cannot handle (backreferences, lookarounds) fall back to the backtracking engine with a timeout.
    /// </summary>
    public static Regex CreateRegex(string pattern, bool matchCase)
    {
        var options = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(pattern, options | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, options, TimeSpan.FromMilliseconds(250));
        }
    }

    private Func<string, bool>? BuildMatcher(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        if (UseRegex)
        {
            var regex = CreateRegex(pattern, MatchCase);
            return text =>
            {
                try
                {
                    return regex.IsMatch(text);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            };
        }

        var comparison = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return text => text.Contains(pattern, comparison);
    }
}
