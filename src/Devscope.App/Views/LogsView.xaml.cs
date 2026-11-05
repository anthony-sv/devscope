using System.Windows;
using System.Windows.Controls;
using Devscope.App.ViewModels;
using Devscope.Core.Logs;

namespace Devscope.App.Views;

public partial class LogsView : UserControl
{
    public LogsView() => InitializeComponent();

    // Double-clicking an event opens it, with its whole stack trace, in a window of its own.
    private void OnEntryDoubleClick(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem { Content: LogEntry entry } && DataContext is LogsViewModel vm)
        {
            vm.SelectedEntry = entry;
            vm.OpenEntryCommand.Execute(null);
            e.Handled = true;
        }
    }
}
