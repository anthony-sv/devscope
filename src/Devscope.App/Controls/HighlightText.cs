using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Devscope.App.ViewModels;

namespace Devscope.App.Controls;

/// <summary>
/// Attached properties that render <see cref="TextProperty"/> into a <see cref="TextBlock"/>, colouring every
/// match of the given <see cref="HighlightSpec"/>s. Used for search hits and workspace highlight rules.
/// </summary>
public static class HighlightText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(HighlightText), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty HighlightsProperty = DependencyProperty.RegisterAttached(
        "Highlights", typeof(IReadOnlyList<HighlightSpec>), typeof(HighlightText), new PropertyMetadata(null, OnChanged));

    private const int MaxHighlightedLength = 4000;

    private static readonly ConcurrentDictionary<string, (Brush Background, Brush Foreground)> BrushCache = new();

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static IReadOnlyList<HighlightSpec>? GetHighlights(DependencyObject element) => (IReadOnlyList<HighlightSpec>?)element.GetValue(HighlightsProperty);

    public static void SetHighlights(DependencyObject element, IReadOnlyList<HighlightSpec>? value) => element.SetValue(HighlightsProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        var text = GetText(block) ?? string.Empty;
        var highlights = GetHighlights(block);
        block.Inlines.Clear();
        if (highlights is not { Count: > 0 } || text.Length == 0)
        {
            block.Text = text;
            return;
        }

        var visible = text.Length > MaxHighlightedLength ? text[..MaxHighlightedLength] : text;
        var spans = FindSpans(visible, highlights);
        if (spans.Count == 0)
        {
            block.Text = text;
            return;
        }

        var position = 0;
        foreach (var (start, length, color) in spans)
        {
            if (start > position)
            {
                block.Inlines.Add(new Run(visible[position..start]));
            }

            var (background, foreground) = GetBrushes(color);
            block.Inlines.Add(new Run(visible.Substring(start, length)) { Background = background, Foreground = foreground });
            position = start + length;
        }

        block.Inlines.Add(new Run(text[position..]));
    }

    /// <summary>Non-overlapping matches; earlier specs (the search) win over later ones.</summary>
    private static List<(int Start, int Length, string Color)> FindSpans(string text, IReadOnlyList<HighlightSpec> highlights)
    {
        var taken = new List<(int Start, int Length, string Color)>();
        foreach (var spec in highlights)
        {
            try
            {
                foreach (Match match in spec.Regex.Matches(text))
                {
                    if (match.Length > 0 && !taken.Exists(t => match.Index < t.Start + t.Length && t.Start < match.Index + match.Length))
                    {
                        taken.Add((match.Index, match.Length, spec.Color));
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Skip this rule for this line rather than freezing the UI.
            }
        }

        taken.Sort((a, b) => a.Start.CompareTo(b.Start));
        return taken;
    }

    private static (Brush Background, Brush Foreground) GetBrushes(string color) => BrushCache.GetOrAdd(color, hex =>
    {
        var parsed = ColorConverter.ConvertFromString(hex) is Color c ? c : Colors.Gold;
        var background = new SolidColorBrush(parsed);
        background.Freeze();

        // Pick black or white text for contrast against the highlight colour.
        var luminance = (0.299 * parsed.R) + (0.587 * parsed.G) + (0.114 * parsed.B);
        var foreground = luminance > 140 ? Brushes.Black : Brushes.White;
        return (background, foreground);
    });
}
