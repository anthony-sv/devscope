using System.Windows.Controls;
using System.Windows.Input;
using Devscope.App.ViewModels;

namespace Devscope.App.Views;

public partial class DiffDocumentView : UserControl
{
    public DiffDocumentView() => InitializeComponent();

    // Ctrl+mouse wheel zooms the diff text instead of scrolling.
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && DataContext is DiffDocumentViewModel { Zoom: var zoom })
        {
            (e.Delta > 0 ? zoom.ZoomInCommand : zoom.ZoomOutCommand).Execute(null);
            e.Handled = true;
        }
    }
}
