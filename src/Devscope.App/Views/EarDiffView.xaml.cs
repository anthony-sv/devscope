using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Devscope.App.ViewModels;

namespace Devscope.App.Views;

public partial class EarDiffView : UserControl
{
    public EarDiffView() => InitializeComponent();

    // Double-clicking a row opens it in the diff window (single click previews it).
    private void OnRowDoubleClick(object sender, RoutedEventArgs e)
    {
        if (sender is ListViewItem { Content: EarDiffRow row } && DataContext is EarDiffViewModel vm)
        {
            vm.OpenInWindowCommand.Execute(row);
            e.Handled = true;
        }
    }

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
}
