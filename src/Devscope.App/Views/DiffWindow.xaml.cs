using System.Collections.Specialized;
using System.Windows;
using Devscope.App.ViewModels;

namespace Devscope.App.Views;

/// <summary>Files opened from the EAR diff list (double click), one tab each. Closes with its last tab.</summary>
public partial class DiffWindow : Window
{
    public DiffWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is EarDiffViewModel oldVm)
            {
                oldVm.OpenDocuments.CollectionChanged -= OnDocumentsChanged;
            }

            if (e.NewValue is EarDiffViewModel newVm)
            {
                newVm.OpenDocuments.CollectionChanged += OnDocumentsChanged;
            }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is EarDiffViewModel vm)
        {
            vm.OpenDocuments.CollectionChanged -= OnDocumentsChanged;
            vm.CloseAllDocuments();
        }

        base.OnClosed(e);
    }

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Deferred: closing clears the collection, which it may not do while still raising this event.
        if (sender is ICollection<DiffDocumentViewModel> { Count: 0 })
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (DataContext is EarDiffViewModel { OpenDocuments.Count: 0 })
                {
                    Close();
                }
            });
        }
    }
}
