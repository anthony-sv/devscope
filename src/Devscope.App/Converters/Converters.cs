using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Devscope.Core.Ear;
using Devscope.Core.Logs;

namespace Devscope.App.Converters;

/// <summary>Looks up a brush resource named after the enum value, e.g. <c>ErrorBrush</c> / <c>ErrorRowBrush</c>.</summary>
/// <remarks>Set <see cref="Suffix"/> to pick a family of brushes ("Brush", "RowBrush", "LineBrush").</remarks>
public sealed class EnumToBrushConverter : IValueConverter
{
    public string Suffix { get; set; } = "Brush";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            LogSeverity s => s.ToString(),
            DiffStatus d => d.ToString(),
            DiffLineKind k => k.ToString(),
            _ => null,
        };

        return key is not null && Application.Current.TryFindResource(key + Suffix) is Brush brush ? brush : Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps an environment colour index to a stable, distinguishable brush.</summary>
public sealed class IndexToBrushConverter : IValueConverter
{
    private static readonly Brush[] Palette =
    [
        Freeze("#0F6CBD"), Freeze("#8764B8"), Freeze("#038387"), Freeze("#C239B3"),
        Freeze("#CA5010"), Freeze("#498205"), Freeze("#986F0B"), Freeze("#4F6BED"),
    ];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index ? Palette[Math.Abs(index) % Palette.Length] : Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Short label for a severity badge.</summary>
public sealed class SeverityLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogSeverity.Fatal => "FATAL",
        LogSeverity.Error => "ERROR",
        LogSeverity.Warning => "WARN",
        LogSeverity.Info => "INFO",
        LogSeverity.Debug => "DEBUG",
        LogSeverity.Trace => "TRACE",
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats a nullable timestamp in local time with milliseconds.</summary>
public sealed class TimestampConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset ts ? ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats a byte count, or a dash when absent.</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long n when n >= 1024 * 1024 => $"{n / 1024d / 1024d:N1} MB",
        long n when n >= 1024 => $"{n / 1024d:N1} KB",
        long n => $"{n:N0} B",
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is non-null / non-empty / true; <c>Invert</c> flips it.</summary>
public sealed class VisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            _ => true,
        };
        return visible ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
