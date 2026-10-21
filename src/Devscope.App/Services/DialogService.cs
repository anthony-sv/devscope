using System.Windows;
using Devscope.App.ViewModels;
using Devscope.App.Views;
using Devscope.Core.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace Devscope.App.Services;

/// <summary>Dialogs, behind an interface so view models never touch WPF windows directly.</summary>
public interface IDialogService
{
    string? PickFile(string title, string filter);

    string? PickFolder(string title);

    string? PickSaveFile(string title, string filter, string defaultName);

    bool Confirm(string message, string title);

    void ShowError(string message, string title);

    /// <summary>Opens the workspace editor. Returns the edited copy, or null when cancelled.</summary>
    Workspace? EditWorkspace(Workspace workspace);

    /// <summary>Opens the Settings window; saved changes are applied through <see cref="IUserSettingsStore.Changed"/>.</summary>
    void ShowSettings();

    /// <summary>Shows the diff window with the EAR diff's open tabs, or brings it to the front when already open.</summary>
    void ShowDiffWindow(EarDiffViewModel viewModel);
}

public sealed class DialogService(IServiceProvider services) : IDialogService
{
    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public bool Confirm(string message, string title) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowError(string message, string title) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public Workspace? EditWorkspace(Workspace workspace)
    {
        var viewModel = new WorkspaceEditorViewModel(workspace, this);
        var window = new WorkspaceEditorWindow { DataContext = viewModel, Owner = Owner };
        return window.ShowDialog() == true ? viewModel.ToModel() : null;
    }

    public void ShowSettings()
    {
        var window = new SettingsWindow { DataContext = services.GetRequiredService<SettingsViewModel>(), Owner = Owner };
        window.ShowDialog();
    }

    public void ShowDiffWindow(EarDiffViewModel viewModel)
    {
        // One window for all tabs. Not owned by the main window, so it can sit behind it or on another monitor;
        // the app still shuts down with the main window (ShutdownMode="OnMainWindowClose").
        var window = Application.Current.Windows.OfType<DiffWindow>().FirstOrDefault()
            ?? new DiffWindow { DataContext = viewModel };
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Maximized;
        }

        window.Activate();
    }

    private static Window Owner => Application.Current.MainWindow;
}
