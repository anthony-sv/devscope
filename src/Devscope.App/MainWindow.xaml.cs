using System.Windows;
using Devscope.App.ViewModels;

namespace Devscope.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
