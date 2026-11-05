using System.Globalization;

namespace Devscope.Core.Logs;

/// <summary>
/// One end of a time-range filter, typed by the user: a date and time (<c>2026-10-16 10:30</c>), a date
/// (<c>2026-10-16</c>), a time of day on any date (<c>10:30</c>, <c>10:30:15.250</c>) or a time relative to now
/// (<c>-15m</c>, <c>-2h</c>, <c>-1d</c>).
/// </summary>
public sealed record LogTimeBound
{
    private static readonly (string Format, TimeSpan Precision)[] DateTimeFormats =
    [
        ("yyyy-MM-dd HH:mm:ss.FFFFFFF", TimeSpan.FromMilliseconds(1)),
        ("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", TimeSpan.FromMilliseconds(1)),
        ("yyyy-MM-dd HH:mm:ss", TimeSpan.FromSeconds(1)),
        ("yyyy-MM-dd'T'HH:mm:ss", TimeSpan.FromSeconds(1)),
        ("yyyy-MM-dd HH:mm", TimeSpan.FromMinutes(1)),
        ("yyyy-MM-dd'T'HH:mm", TimeSpan.FromMinutes(1)),
        ("yyyy-MM-dd", TimeSpan.FromDays(1)),
    ];

    private static readonly (string Format, TimeSpan Precision)[] TimeFormats =
    [
        ("H:mm:ss.FFFFFFF", TimeSpan.FromMilliseconds(1)),
        ("H:mm:ss", TimeSpan.FromSeconds(1)),
        ("H:mm", TimeSpan.FromMinutes(1)),
    ];

    private LogTimeBound(DateTimeOffset? instant, TimeOnly? timeOfDay)
    {
        Instant = instant;
        TimeOfDay = timeOfDay;
    }

    /// <summary>An absolute point in time, or null for a time-of-day bound.</summary>
    public DateTimeOffset? Instant { get; }

    /// <summary>A time of day that applies to every date, or null for an absolute bound.</summary>
    public TimeOnly? TimeOfDay { get; }

    /// <summary>Parses user input. Empty input yields true and a null bound (no limit).</summary>
    /// <param name="text">What the user typed.</param>
    /// <param name="isUpperBound">
    /// For the "to" end the bound covers the whole unit typed, so <c>10:30</c> includes 10:30:59 and a date includes that whole day.
    /// </param>
    /// <param name="now">Reference for relative input such as <c>-15m</c>.</param>
    /// <param name="bound">The parsed bound, or null when <paramref name="text"/> is empty.</param>
    public static bool TryParse(string? text, bool isUpperBound, DateTimeOffset now, out LogTimeBound? bound)
    {
        bound = null;
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return true;
        }

        if (TryParseRelative(value, out var ago))
        {
            bound = new LogTimeBound(now - ago, null);
            return true;
        }

        foreach (var (format, precision) in DateTimeFormats)
        {
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                var instant = new DateTimeOffset(parsed);
                bound = new LogTimeBound(isUpperBound ? instant + precision - TimeSpan.FromTicks(1) : instant, null);
                return true;
            }
        }

        foreach (var (format, precision) in TimeFormats)
        {
            if (TimeOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                // Clamp instead of wrapping past midnight (23:59 + 1 minute).
                var end = time.ToTimeSpan() + precision - TimeSpan.FromTicks(1);
                bound = new LogTimeBound(null, isUpperBound ? TimeOnly.FromTimeSpan(end < TimeSpan.FromDays(1) ? end : TimeSpan.FromDays(1) - TimeSpan.FromTicks(1)) : time);
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="time"/> is at or after this bound.</summary>
    public bool IsAtOrAfter(DateTimeOffset time) =>
        Instant is { } instant ? time >= instant : TimeOnly.FromDateTime(time.LocalDateTime) >= TimeOfDay!.Value;

    /// <summary>Whether <paramref name="time"/> is at or before this bound.</summary>
    public bool IsAtOrBefore(DateTimeOffset time) =>
        Instant is { } instant ? time <= instant : TimeOnly.FromDateTime(time.LocalDateTime) <= TimeOfDay!.Value;

    private static bool TryParseRelative(string value, out TimeSpan ago)
    {
        ago = default;
        if (value.Length < 3 || value[0] != '-')
        {
            return false;
        }

        if (!int.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            return false;
        }

        TimeSpan? unit = char.ToLowerInvariant(value[^1]) switch
        {
            's' => TimeSpan.FromSeconds(1),
            'm' => TimeSpan.FromMinutes(1),
            'h' => TimeSpan.FromHours(1),
            'd' => TimeSpan.FromDays(1),
            _ => null,
        };
        if (unit is null)
        {
            return false;
        }

        ago = unit.Value * amount;
        return true;
    }
}
