using System.Windows;
using Devscope.App.ViewModels;

namespace Devscope.App.Views;

public partial class WorkspaceEditorWindow : Window
{
    public WorkspaceEditorWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is WorkspaceEditorViewModel oldVm)
            {
                oldVm.CloseRequested -= OnCloseRequested;
            }

            if (e.NewValue is WorkspaceEditorViewModel newVm)
            {
                newVm.CloseRequested += OnCloseRequested;
            }
        };
    }

    private void OnCloseRequested(object? sender, bool saved) => DialogResult = saved;
}
