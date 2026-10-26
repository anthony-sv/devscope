using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Devscope.App.Controls;

/// <summary>View-only behaviours that would otherwise live in code-behind.</summary>
public static class Behaviors
{
    /// <summary>Keeps a list scrolled to its last item while enabled (log "follow" mode).</summary>
    public static readonly DependencyProperty AutoScrollToEndProperty = DependencyProperty.RegisterAttached(
        "AutoScrollToEnd", typeof(bool), typeof(Behaviors), new PropertyMetadata(false, OnAutoScrollChanged));

    /// <summary>Scrolls the selected item into view whenever the selection changes (e.g. "next change").</summary>
    public static readonly DependencyProperty ScrollSelectionIntoViewProperty = DependencyProperty.RegisterAttached(
        "ScrollSelectionIntoView", typeof(bool), typeof(Behaviors), new PropertyMetadata(false, OnScrollSelectionChanged));

    /// <summary>Lets a file or folder be dropped from Explorer onto a TextBox to fill in its path.</summary>
    public static readonly DependencyProperty AcceptsPathDropProperty = DependencyProperty.RegisterAttached(
        "AcceptsPathDrop", typeof(bool), typeof(Behaviors), new PropertyMetadata(false, OnAcceptsPathDropChanged));

    /// <summary>
    /// Index of the <see cref="GridView"/> column that takes the width the other columns leave, so a narrow list
    /// never pushes its last columns out of view.
    /// </summary>
    public static readonly DependencyProperty FillColumnProperty = DependencyProperty.RegisterAttached(
        "FillColumn", typeof(int), typeof(Behaviors), new PropertyMetadata(-1, OnFillColumnChanged));

    /// <summary>Narrowest the <see cref="FillColumnProperty"/> column gets; below that the list scrolls sideways.</summary>
    public const double MinFillColumnWidth = 160;

    /// <summary>
    /// Two-way binding for <see cref="PasswordBox.Password"/>, which is not a dependency property. The default is null,
    /// so the first bound value (even an empty one) hooks the box up.
    /// </summary>
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password", typeof(string), typeof(Behaviors),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    private static readonly DependencyProperty IsUpdatingPasswordProperty = DependencyProperty.RegisterAttached(
        "IsUpdatingPassword", typeof(bool), typeof(Behaviors), new PropertyMetadata(false));

    public static string? GetPassword(DependencyObject element) => (string?)element.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject element, string? value) => element.SetValue(PasswordProperty, value);

    public static int GetFillColumn(DependencyObject element) => (int)element.GetValue(FillColumnProperty);

    public static void SetFillColumn(DependencyObject element, int value) => element.SetValue(FillColumnProperty, value);

    public static bool GetAutoScrollToEnd(DependencyObject element) => (bool)element.GetValue(AutoScrollToEndProperty);

    public static void SetAutoScrollToEnd(DependencyObject element, bool value) => element.SetValue(AutoScrollToEndProperty, value);

    public static bool GetScrollSelectionIntoView(DependencyObject element) => (bool)element.GetValue(ScrollSelectionIntoViewProperty);

    public static void SetScrollSelectionIntoView(DependencyObject element, bool value) => element.SetValue(ScrollSelectionIntoViewProperty, value);

    public static bool GetAcceptsPathDrop(DependencyObject element) => (bool)element.GetValue(AcceptsPathDropProperty);

    public static void SetAcceptsPathDrop(DependencyObject element, bool value) => element.SetValue(AcceptsPathDropProperty, value);

    private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list)
        {
            return;
        }

        // ItemCollection survives ItemsSource replacement (it raises Reset), so one subscription is enough;
        // the handler checks the flag, so toggling follow on and off needs no unsubscribe.
        if (!(bool)list.GetValue(IsHookedProperty))
        {
            ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) => ScrollToEnd(list);
            list.SetValue(IsHookedProperty, true);
        }

        ScrollToEnd(list);
    }

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked", typeof(bool), typeof(Behaviors), new PropertyMetadata(false));

    private static void ScrollToEnd(ListBox list)
    {
        if (!GetAutoScrollToEnd(list) || list.Items.Count == 0)
        {
            return;
        }

        // Defer so a burst of appends results in a single scroll.
        list.Dispatcher.InvokeAsync(
            () =>
            {
                if (list.Items.Count > 0)
                {
                    list.ScrollIntoView(list.Items[^1]);
                }
            },
            DispatcherPriority.Background);
    }

    private static void OnScrollSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list)
        {
            return;
        }

        list.SelectionChanged -= OnSelectionChanged;
        if ((bool)e.NewValue)
        {
            list.SelectionChanged += OnSelectionChanged;
        }
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: { } item } list)
        {
            list.ScrollIntoView(item);
        }
    }

    private static void OnFillColumnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list)
        {
            return;
        }

        list.SizeChanged -= OnFillListSizeChanged;
        if ((int)e.NewValue >= 0)
        {
            list.SizeChanged += OnFillListSizeChanged;
        }
    }

    private static void OnFillListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var list = (ListView)sender;
        var index = GetFillColumn(list);
        if (list.View is not GridView view || index >= view.Columns.Count)
        {
            return;
        }

        var others = view.Columns.Where((_, i) => i != index).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);

        // Leaves room for the vertical scroll bar and the borders, so no horizontal scroll bar appears.
        var available = list.ActualWidth - others - SystemParameters.VerticalScrollBarWidth - 8;
        view.Columns[index].Width = Math.Max(MinFillColumnWidth, available);
    }

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }

        box.PasswordChanged -= OnPasswordBoxChanged;
        if (!(bool)box.GetValue(IsUpdatingPasswordProperty))
        {
            box.Password = (string?)e.NewValue ?? string.Empty;
        }

        box.PasswordChanged += OnPasswordBoxChanged;
    }

    private static void OnPasswordBoxChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(IsUpdatingPasswordProperty, true);
        try
        {
            SetPassword(box, box.Password);
        }
        finally
        {
            box.SetValue(IsUpdatingPasswordProperty, false);
        }
    }

    private static void OnAcceptsPathDropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.AllowDrop = (bool)e.NewValue;
        box.PreviewDragOver -= OnPreviewDragOver;
        box.PreviewDrop -= OnPreviewDrop;
        if ((bool)e.NewValue)
        {
            box.PreviewDragOver += OnPreviewDragOver;
            box.PreviewDrop += OnPreviewDrop;
        }
    }

    private static void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (sender is TextBox box && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            box.Text = paths[0];
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
}
