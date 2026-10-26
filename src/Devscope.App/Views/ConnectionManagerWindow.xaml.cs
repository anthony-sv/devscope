using System.Windows;
using Devscope.App.ViewModels;

namespace Devscope.App.Views;

public partial class ConnectionManagerWindow : Window
{
    public ConnectionManagerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ConnectionManagerViewModel oldVm)
            {
                oldVm.CloseRequested -= OnCloseRequested;
            }

            if (e.NewValue is ConnectionManagerViewModel newVm)
            {
                newVm.CloseRequested += OnCloseRequested;
            }
        };
    }

    private void OnCloseRequested(object? sender, bool saved) => DialogResult = saved;
}
