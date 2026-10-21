using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Devscope.Core.Ear;

namespace Devscope.App.Controls;

/// <summary>
/// Attached properties that render one side of a diff line into a <see cref="TextBlock"/>: plain
/// <see cref="TextProperty"/>, or, when <see cref="SpansProperty"/> is set, with the changed words painted in
/// <see cref="ChangedBrushProperty"/>.
/// </summary>
public static class DiffText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(DiffText), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty SpansProperty = DependencyProperty.RegisterAttached(
        "Spans", typeof(IReadOnlyList<DiffSpan>), typeof(DiffText), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty ChangedBrushProperty = DependencyProperty.RegisterAttached(
        "ChangedBrush", typeof(Brush), typeof(DiffText), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    public static IReadOnlyList<DiffSpan>? GetSpans(DependencyObject element) => (IReadOnlyList<DiffSpan>?)element.GetValue(SpansProperty);

    public static void SetSpans(DependencyObject element, IReadOnlyList<DiffSpan>? value) => element.SetValue(SpansProperty, value);

    public static Brush? GetChangedBrush(DependencyObject element) => (Brush?)element.GetValue(ChangedBrushProperty);

    public static void SetChangedBrush(DependencyObject element, Brush? value) => element.SetValue(ChangedBrushProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        var spans = GetSpans(block);
        block.Inlines.Clear();
        if (spans is not { Count: > 0 })
        {
            block.Text = GetText(block) ?? string.Empty;
            return;
        }

        var brush = GetChangedBrush(block);
        foreach (var span in spans)
        {
            block.Inlines.Add(span.IsChanged ? new Run(span.Text) { Background = brush } : new Run(span.Text));
        }
    }
}
