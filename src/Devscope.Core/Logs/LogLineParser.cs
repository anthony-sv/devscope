using System.Globalization;
using System.Text.RegularExpressions;

namespace Devscope.Core.Logs;

/// <summary>
/// Best-effort detection of timestamps and severities in unstructured log lines.
/// Covers ISO-8601 / log4j / logback / Serilog, Tomcat, WebSphere (SystemOut.log) and time-only formats.
/// </summary>
public static partial class LogLineParser
{
    /// <summary>How far into a line we look for a severity token.</summary>
    private const int SeveritySearchWindow = 200;

    /// <summary>Tries to read a timestamp at the start of <paramref name="line"/>.</summary>
    /// <param name="line">The raw line.</param>
    /// <param name="fallbackDate">Date used for time-only formats (typically the file's last write date).</param>
    /// <param name="timestamp">The parsed timestamp.</param>
    public static bool TryParseTimestamp(ReadOnlySpan<char> line, DateOnly fallbackDate, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (line.Length < 8)
        {
            return false;
        }

        var head = line.Length > 64 ? line[..64].ToString() : line.ToString();

        var m = IsoRegex().Match(head);
        if (m.Success)
        {
            return TryBuild(Int(m, "y"), Int(m, "M"), Int(m, "d"), Int(m, "H"), Int(m, "m"), Int(m, "s"), m.Groups["f"].Value, m.Groups["tz"].Value, out timestamp);
        }

        m = SlashRegex().Match(head);
        if (m.Success)
        {
            // WebSphere writes M/d/yy; most European formats write d/M/yyyy. Disambiguate when possible.
            var a = Int(m, "a");
            var b = Int(m, "b");
            var year = Int(m, "y");
            year = year < 100 ? 2000 + year : year;
            var (month, day) = a > 12 ? (b, a) : (a, b);
            return TryBuild(year, month, day, Int(m, "H"), Int(m, "m"), Int(m, "s"), m.Groups["f"].Value, string.Empty, out timestamp);
        }

        m = MonthNameRegex().Match(head);
        if (m.Success && DateTime.TryParseExact(m.Groups["mon"].Value, "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthDate))
        {
            return TryBuild(Int(m, "y"), monthDate.Month, Int(m, "d"), Int(m, "H"), Int(m, "m"), Int(m, "s"), m.Groups["f"].Value, string.Empty, out timestamp);
        }

        m = TimeOnlyRegex().Match(head);
        if (m.Success)
        {
            return TryBuild(fallbackDate.Year, fallbackDate.Month, fallbackDate.Day, Int(m, "H"), Int(m, "m"), Int(m, "s"), m.Groups["f"].Value, string.Empty, out timestamp);
        }

        return false;
    }

    /// <summary>Detects the severity of a line from common level tokens.</summary>
    public static LogSeverity DetectSeverity(ReadOnlySpan<char> line)
    {
        var window = (line.Length > SeveritySearchWindow ? line[..SeveritySearchWindow] : line).ToString();

        var m = WebSphereLevelRegex().Match(window);
        if (m.Success)
        {
            return m.Groups["l"].ValueSpan[0] switch
            {
                'F' => LogSeverity.Fatal,
                'E' or 'R' => LogSeverity.Error,
                'W' => LogSeverity.Warning,
                'I' or 'A' or 'O' or 'C' => LogSeverity.Info,
                'D' or 'Z' or '1' or '2' or '3' => LogSeverity.Debug,
                _ => LogSeverity.Unknown,
            };
        }

        m = LevelTokenRegex().Match(window);
        if (m.Success)
        {
            return Map(m.Groups["l"].Value);
        }

        m = KeyValueLevelRegex().Match(window);
        return m.Success ? Map(m.Groups["l"].Value) : LogSeverity.Unknown;
    }

    /// <summary>Lines that clearly continue the previous event even when the file has no timestamps.</summary>
    public static bool LooksLikeContinuation(ReadOnlySpan<char> line) =>
        line.Length > 0 && (char.IsWhiteSpace(line[0])
            || line.StartsWith("at ", StringComparison.Ordinal)
            || line.StartsWith("Caused by:", StringComparison.Ordinal)
            || line.StartsWith("...", StringComparison.Ordinal));

    private static LogSeverity Map(string token) => token.ToUpperInvariant() switch
    {
        "TRACE" or "FINEST" or "FINER" or "VERBOSE" or "VRB" or "TRC" => LogSeverity.Trace,
        "DEBUG" or "FINE" or "CONFIG" or "DBG" => LogSeverity.Debug,
        "INFO" or "INFORMATION" or "NOTICE" or "AUDIT" or "INF" => LogSeverity.Info,
        "WARN" or "WARNING" or "WRN" => LogSeverity.Warning,
        "ERROR" or "SEVERE" or "ERR" => LogSeverity.Error,
        "FATAL" or "CRITICAL" or "CRIT" or "FTL" or "EMERG" or "ALERT" => LogSeverity.Fatal,
        _ => LogSeverity.Unknown,
    };

    private static int Int(Match m, string group) =>
        int.Parse(m.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);

    private static bool TryBuild(int year, int month, int day, int hour, int minute, int second, string fraction, string zone, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        var ticks = 0L;
        if (fraction.Length > 0)
        {
            var digits = fraction.Length > 7 ? fraction[..7] : fraction.PadRight(7, '0');
            ticks = long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(ticks);
        TimeSpan offset;
        if (zone.Length == 0)
        {
            offset = TimeZoneInfo.Local.GetUtcOffset(local);
        }
        else if (zone is "Z" or "z")
        {
            offset = TimeSpan.Zero;
        }
        else
        {
            var sign = zone[0] == '-' ? -1 : 1;
            var digits = zone[1..].Replace(":", string.Empty, StringComparison.Ordinal);
            offset = sign * new TimeSpan(int.Parse(digits[..2], CultureInfo.InvariantCulture), int.Parse(digits[2..], CultureInfo.InvariantCulture), 0);
        }

        timestamp = new DateTimeOffset(local, offset);
        return true;
    }

    // 2026-10-16 12:34:56,789 | 2026-10-16T12:34:56.789+02:00 | [2026-10-16 12:34:56] | 2026/10/16 12:34:56
    [GeneratedRegex(@"^\W{0,2}(?<y>\d{4})[-/](?<M>\d{2})[-/](?<d>\d{2})[T ](?<H>\d{2}):(?<m>\d{2}):(?<s>\d{2})(?:[.,:](?<f>\d{1,9}))?\s?(?<tz>Z|[+-]\d{2}:?\d{2})?", RegexOptions.CultureInvariant)]
    private static partial Regex IsoRegex();

    // [10/16/26 12:34:56:789 CEST] (WebSphere) | 16/10/2026 12:34:56
    [GeneratedRegex(@"^\W{0,2}(?<a>\d{1,2})/(?<b>\d{1,2})/(?<y>\d{2}|\d{4})[ ,]+(?<H>\d{1,2}):(?<m>\d{2}):(?<s>\d{2})(?:[.,:](?<f>\d{1,3}))?", RegexOptions.CultureInvariant)]
    private static partial Regex SlashRegex();

    // 16-Oct-2026 12:34:56.789 (Tomcat) | 16 Oct 2026 12:34:56
    [GeneratedRegex(@"^\W{0,2}(?<d>\d{1,2})[- ](?<mon>[A-Za-z]{3})[- ](?<y>\d{4})[ :](?<H>\d{2}):(?<m>\d{2}):(?<s>\d{2})(?:[.,](?<f>\d{1,3}))?", RegexOptions.CultureInvariant)]
    private static partial Regex MonthNameRegex();

    // 12:34:56.789 (logback default pattern)
    [GeneratedRegex(@"^\W{0,2}(?<H>\d{2}):(?<m>\d{2}):(?<s>\d{2})[.,](?<f>\d{1,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex TimeOnlyRegex();

    // [10/16/26 12:34:56:789 CEST] 0000004a SystemOut     O message
    [GeneratedRegex(@"^\[[^\]]+\]\s+[0-9a-fA-F]{8}\s+\S+\s+(?<l>[FEWAIOCDRZ123])\s", RegexOptions.CultureInvariant)]
    private static partial Regex WebSphereLevelRegex();

    [GeneratedRegex(@"(?<![\w.])\[?(?<l>TRACE|DEBUG|INFO|INFORMATION|NOTICE|AUDIT|WARN|WARNING|ERROR|SEVERE|FATAL|CRITICAL|CRIT|FINEST|FINER|FINE|CONFIG|VRB|DBG|INF|WRN|ERR|FTL)\]?(?![\w.])", RegexOptions.CultureInvariant)]
    private static partial Regex LevelTokenRegex();

    // level=error | "level":"warn" | severity: Error
    [GeneratedRegex(@"""?(?:level|severity|lvl)""?\s*[:=]\s*""?(?<l>[A-Za-z]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueLevelRegex();
}
