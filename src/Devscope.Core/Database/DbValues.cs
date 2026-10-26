using System.Data.SqlTypes;
using System.Globalization;
using System.Text;

namespace Devscope.Core.Database;

/// <summary>
/// Comparison, display and T-SQL literals of the values read from a database: strings, numbers of any width,
/// dates, GUIDs, binary and booleans, as <see cref="IRowSource"/> returns them.
/// </summary>
public static class DbValues
{
    /// <summary>
    /// Orders key values the way SQL Server sorts them under a binary collation: NULL first; text by UTF-16 code unit,
    /// with trailing spaces ignored; GUIDs in SQL Server's byte order; numbers by value across widths.
    /// </summary>
    public static int CompareKeys(object? a, object? b)
    {
        if (a is null)
        {
            return b is null ? 0 : -1;
        }

        if (b is null)
        {
            return 1;
        }

        switch (a, b)
        {
            case (string x, string y):
                return CompareText(x, y);
            case (Guid x, Guid y):
                return new SqlGuid(x).CompareTo(new SqlGuid(y));
            case (byte[] x, byte[] y):
                return CompareBinary(x, y);
            case (DateTime x, DateTime y):
                return x.CompareTo(y);
            case (DateTimeOffset x, DateTimeOffset y):
                return x.UtcDateTime.CompareTo(y.UtcDateTime);
            case (TimeSpan x, TimeSpan y):
                return x.CompareTo(y);
            case (bool x, bool y):
                return x.CompareTo(y);
        }

        if (IsNumber(a) && IsNumber(b))
        {
            return CompareNumbers(a, b);
        }

        // Mixed kinds only happen with a mistaken key choice; any total order will do.
        var byKind = Rank(a).CompareTo(Rank(b));
        return byKind != 0 ? byKind : string.CompareOrdinal(Canonical(a), Canonical(b));
    }

    /// <summary>
    /// Whether two column values are the same data: exact text (case and trailing spaces count), numbers by value
    /// (5 equals 5.00), date-times with their offset.
    /// </summary>
    public static bool AreEqual(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        switch (a, b)
        {
            case (string x, string y):
                return string.Equals(x, y, StringComparison.Ordinal);
            case (byte[] x, byte[] y):
                return x.AsSpan().SequenceEqual(y);
            case (DateTimeOffset x, DateTimeOffset y):
                return x.EqualsExact(y);
        }

        if (IsNumber(a) && IsNumber(b))
        {
            return CompareNumbers(a, b) == 0;
        }

        return a.GetType() == b.GetType()
            ? a.Equals(b)
            : string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);
    }

    /// <summary>Text form under which equal values (see <see cref="AreEqual"/>) are equal, for hashing.</summary>
    public static string Canonical(object? value) => value switch
    {
        null => "\0",
        string s => "s" + s,
        bool b => b ? "b1" : "b0",
        byte[] bytes => "x" + Convert.ToHexString(bytes),
        DateTime d => "d" + d.Ticks.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset o => string.Create(CultureInfo.InvariantCulture, $"o{o.UtcTicks}:{o.Offset.Ticks}"),
        TimeSpan t => "t" + t.Ticks.ToString(CultureInfo.InvariantCulture),
        Guid g => "g" + g.ToString("N"),
        _ when IsNumber(value) => "n" + CanonicalNumber(value),
        _ => "?" + Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Like <see cref="Canonical"/>, but equal for keys that compare equal: text ignores trailing spaces and binary
    /// trailing zero bytes, as in SQL Server.
    /// </summary>
    public static string CanonicalKey(object? value) => value switch
    {
        string s => "s" + s.TrimEnd(' '),
        byte[] bytes => "x" + Convert.ToHexString(bytes.AsSpan(0, bytes.AsSpan().LastIndexOfAnyExcept((byte)0) + 1)),
        _ => Canonical(value),
    };

    /// <summary>A value for display: quoted text, NULL, ISO dates, 0x-prefixed binary; shortened beyond <paramref name="maxLength"/>.</summary>
    public static string Format(object? value, int maxLength = 120)
    {
        var text = value switch
        {
            null => "NULL",
            string s => "'" + s.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "'",
            byte[] bytes => "0x" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, maxLength))),
            bool b => b ? "1" : "0",
            DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
            TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        return text.Length > maxLength ? string.Concat(text.AsSpan(0, maxLength), "…") : text;
    }

    /// <summary>A value as a T-SQL literal, e.g. <c>N'it''s'</c>, <c>NULL</c>, <c>'2026-01-31T10:00:00.000'</c>, <c>0x0A0B</c>.</summary>
    /// <param name="value">The value.</param>
    /// <param name="dataType">The target column's type; decides how many fractional seconds a date-time gets.</param>
    public static string ToSqlLiteral(object? value, string? dataType = null)
    {
        var type = dataType?.ToLowerInvariant() ?? string.Empty;
        return value switch
        {
            null => "NULL",
            string s => "N'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
            bool b => b ? "1" : "0",
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            Guid g => "'" + g.ToString("D") + "'",
            DateTime d when type == "date" => "'" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'",
            DateTime d when type is "datetime" or "smalldatetime" => "'" + d.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "'",
            DateTime d => "'" + d.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "'",
            DateTimeOffset o => "'" + o.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture) + "'",
            TimeSpan t => "'" + t.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture) + "'",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            _ when IsNumber(value) => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            _ => "N'" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'",
        };
    }

    /// <summary>Text comparison as SQL Server does it: the shorter value is padded with spaces.</summary>
    internal static int CompareText(string x, string y)
    {
        var common = Math.Min(x.Length, y.Length);
        var prefix = x.AsSpan(0, common).SequenceCompareTo(y.AsSpan(0, common));
        if (prefix != 0)
        {
            return Math.Sign(prefix);
        }

        var (longer, sign) = x.Length > y.Length ? (x, 1) : (y, -1);
        for (var i = common; i < longer.Length; i++)
        {
            if (longer[i] != ' ')
            {
                return longer[i] > ' ' ? sign : -sign;
            }
        }

        return 0;
    }

    /// <summary>Binary comparison as SQL Server does it: the shorter value is padded with zero bytes, so 0x01 equals 0x0100.</summary>
    internal static int CompareBinary(byte[] x, byte[] y)
    {
        var common = Math.Min(x.Length, y.Length);
        var prefix = x.AsSpan(0, common).SequenceCompareTo(y.AsSpan(0, common));
        if (prefix != 0)
        {
            return Math.Sign(prefix);
        }

        var (longer, sign) = x.Length > y.Length ? (x, 1) : (y, -1);
        return longer.AsSpan(common).ContainsAnyExcept((byte)0) ? sign : 0;
    }

    private static bool IsNumber(object? value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal or double or float or SqlDecimal;

    private static int CompareNumbers(object a, object b)
    {
        if (a is double or float || b is double or float)
        {
            return ToDouble(a).CompareTo(ToDouble(b));
        }

        if (a is SqlDecimal || b is SqlDecimal)
        {
            return ToSqlDecimal(a).CompareTo(ToSqlDecimal(b));
        }

        return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
    }

    private static double ToDouble(object value) => value is SqlDecimal d ? d.ToDouble() : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static SqlDecimal ToSqlDecimal(object value) => value is SqlDecimal d ? d : new SqlDecimal(Convert.ToDecimal(value, CultureInfo.InvariantCulture));

    /// <summary>The same text for the same number in any type: 5, 5.00 and 5.0 (as double) are all "5".</summary>
    private static string CanonicalNumber(object value)
    {
        string text;
        if (value is double or float)
        {
            var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(d) || Math.Abs(d) >= 7.9e28 || (double)(decimal)d != d)
            {
                return d.ToString("R", CultureInfo.InvariantCulture);
            }

            text = ((decimal)d).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text is "-0" ? "0" : text;
    }

    private static int Rank(object value) => value switch
    {
        bool => 1,
        string => 2,
        DateTime => 3,
        DateTimeOffset => 4,
        TimeSpan => 5,
        Guid => 6,
        byte[] => 7,
        _ when IsNumber(value) => 0,
        _ => 8,
    };

    /// <summary>Appends a value to a row hash in its canonical form.</summary>
    internal static void AppendCanonical(StringBuilder sb, object? value) => sb.Append(Canonical(value)).Append('\u001F');
}
