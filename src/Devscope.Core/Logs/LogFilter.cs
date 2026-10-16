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

    /// <summary>Builds a predicate. Throws <see cref="ArgumentException"/> for an invalid regular expression.</summary>
    public Func<LogEntry, bool> Compile()
    {
        var include = BuildMatcher(Search);
        var exclude = BuildMatcher(Exclude);
        var severities = Severities is { Count: > 0 } s ? s : null;
        var environments = Environments is { Count: > 0 } e ? e : null;

        return entry =>
            (severities is null || severities.Contains(entry.Severity))
            && (environments is null || environments.Contains(entry.Source.Environment))
            && (include is null || include(entry.Text))
            && (exclude is null || !exclude(entry.Text));
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
