using System.Windows;
using System.Windows.Input;

namespace Devscope.App.Views;

public partial class LogEventWindow : Window
{
    public LogEventWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Body.Focus();
    }

    private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();
}
