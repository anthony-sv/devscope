using CommunityToolkit.Mvvm.ComponentModel;

namespace Devscope.App.ViewModels;

/// <summary>A top-level page shown in the navigation rail.</summary>
public interface IPage
{
    string Title { get; }

    /// <summary>Segoe Fluent Icons glyph.</summary>
    string Glyph { get; }
}

/// <summary>Shell view model: navigation between the tools.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(EarDiffViewModel earDiff, LogsViewModel logs)
    {
        Pages = [earDiff, logs];
        _selectedPage = earDiff;
    }

    public IReadOnlyList<IPage> Pages { get; }

    [ObservableProperty]
    private IPage _selectedPage;
}
