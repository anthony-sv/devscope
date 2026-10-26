using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Devscope.App.ViewModels;

namespace Devscope.App.Views;

public partial class DbDiffView : UserControl
{
    public DbDiffView() => InitializeComponent();

    // The "..." button opens its menu on a normal click.
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // Ctrl+mouse wheel zooms the script text instead of scrolling.
    private void OnScriptMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && sender is FrameworkElement { DataContext: ScriptDiffViewModel { Zoom: var zoom } })
        {
            (e.Delta > 0 ? zoom.ZoomInCommand : zoom.ZoomOutCommand).Execute(null);
            e.Handled = true;
        }
    }
}
