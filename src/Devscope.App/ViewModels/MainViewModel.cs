using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;

namespace Devscope.App.ViewModels;

/// <summary>A top-level page shown in the navigation rail.</summary>
public interface IPage
{
    string Title { get; }

    /// <summary>Segoe Fluent Icons glyph.</summary>
    string Glyph { get; }
}

/// <summary>Shell view model: navigation between the tools, and the Settings window.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    public MainViewModel(EarDiffViewModel earDiff, LogsViewModel logs, IDialogService dialogs)
    {
        _dialogs = dialogs;
        Pages = [earDiff, logs];
        _selectedPage = earDiff;
    }

    public IReadOnlyList<IPage> Pages { get; }

    [ObservableProperty]
    private IPage _selectedPage;

    [RelayCommand]
    private void OpenSettings() => _dialogs.ShowSettings();
}
