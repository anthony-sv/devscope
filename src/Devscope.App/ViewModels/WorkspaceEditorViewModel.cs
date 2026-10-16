using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;
using Devscope.Core.Logs;
using Devscope.Core.Workspaces;

namespace Devscope.App.ViewModels;

/// <summary>Edits a copy of a <see cref="Workspace"/>: environments, their paths, and highlight rules.</summary>
public sealed partial class WorkspaceEditorViewModel : ObservableObject
{
    /// <summary>Colours offered for highlight rules; chosen to read well on light and dark backgrounds.</summary>
    public static IReadOnlyList<string> HighlightColors { get; } =
        ["#E3B341", "#F85149", "#3FB950", "#58A6FF", "#BC8CFF", "#FF7B72", "#39C5CF", "#F778BA"];

    private readonly Guid _id;
    private readonly IDialogService _dialogs;

    public WorkspaceEditorViewModel(Workspace workspace, IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _dialogs = dialogs;
        _id = workspace.Id;
        _name = workspace.Name;
        Environments = [.. workspace.Environments.Select(e => new EnvironmentEditorViewModel(e, this))];
        Highlights = [.. workspace.Highlights.Select(h => new HighlightEditorViewModel { Pattern = h.Pattern, IsRegex = h.IsRegex, Color = h.Color })];
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string? _validationError;

    public ObservableCollection<EnvironmentEditorViewModel> Environments { get; }

    public ObservableCollection<HighlightEditorViewModel> Highlights { get; }

    /// <summary>Raised when the dialog should close; true means saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void AddEnvironment() => Environments.Add(new EnvironmentEditorViewModel(
        new WorkspaceEnvironment { Name = $"env{Environments.Count + 1}", Sources = [new WorkspaceSource()] }, this));

    [RelayCommand]
    private void DuplicateEnvironment(EnvironmentEditorViewModel environment)
    {
        // Clusters usually have identical layouts on each node: copy, then just change the server name.
        var copy = environment.ToModel();
        copy.Name += "-copy";
        Environments.Insert(Environments.IndexOf(environment) + 1, new EnvironmentEditorViewModel(copy, this));
    }

    [RelayCommand]
    private void RemoveEnvironment(EnvironmentEditorViewModel environment) => Environments.Remove(environment);

    [RelayCommand]
    private void AddHighlight() => Highlights.Add(new HighlightEditorViewModel { Color = HighlightColors[Highlights.Count % HighlightColors.Count] });

    [RelayCommand]
    private void RemoveHighlight(HighlightEditorViewModel highlight) => Highlights.Remove(highlight);

    [RelayCommand]
    private async Task TestPathsAsync()
    {
        var sources = Environments.SelectMany(e => e.Sources).ToList();
        foreach (var source in sources)
        {
            source.Status = "checking…";
        }

        // Network paths can take seconds to time out; check them in parallel off the UI thread.
        await Task.WhenAll(sources.Select(async source =>
        {
            var model = source.ToModel();
            source.Status = await Task.Run(() =>
            {
                try
                {
                    var files = LogSourceResolver.Resolve(model, maxFiles: 10_000);
                    return files.Count == 0 ? "⚠ reachable, but no matching files" : $"✓ {files.Count:N0} file(s)";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return "✗ " + ex.Message;
                }
            });
        }));
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ValidationError = "Give the workspace a name.";
            return;
        }

        if (Environments.Any(e => string.IsNullOrWhiteSpace(e.Name)))
        {
            ValidationError = "Every environment needs a name.";
            return;
        }

        if (Environments.GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            ValidationError = "Environment names must be unique.";
            return;
        }

        if (!Environments.SelectMany(e => e.Sources).Any(s => !string.IsNullOrWhiteSpace(s.Path)))
        {
            ValidationError = "Add at least one log file or folder path.";
            return;
        }

        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    internal string? PickFolder() => _dialogs.PickFolder("Log folder");

    public Workspace ToModel() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        Environments = [.. Environments.Select(e => e.ToModel())],
        Highlights = [.. Highlights.Where(h => !string.IsNullOrWhiteSpace(h.Pattern))
            .Select(h => new HighlightRule { Pattern = h.Pattern, IsRegex = h.IsRegex, Color = h.Color })],
    };
}

public sealed partial class EnvironmentEditorViewModel : ObservableObject
{
    private readonly WorkspaceEditorViewModel _owner;

    public EnvironmentEditorViewModel(WorkspaceEnvironment model, WorkspaceEditorViewModel owner)
    {
        _owner = owner;
        _name = model.Name;
        Sources = [.. model.Sources.Select(s => new SourceEditorViewModel(s))];
    }

    [ObservableProperty]
    private string _name;

    public ObservableCollection<SourceEditorViewModel> Sources { get; }

    [RelayCommand]
    private void AddSource() => Sources.Add(new SourceEditorViewModel(new WorkspaceSource()));

    [RelayCommand]
    private void RemoveSource(SourceEditorViewModel source) => Sources.Remove(source);

    [RelayCommand]
    private void BrowseSource(SourceEditorViewModel source)
    {
        if (_owner.PickFolder() is { } folder)
        {
            source.Path = folder;
        }
    }

    public WorkspaceEnvironment ToModel() => new()
    {
        Name = Name.Trim(),
        Sources = [.. Sources.Where(s => !string.IsNullOrWhiteSpace(s.Path)).Select(s => s.ToModel())],
    };
}

public sealed partial class SourceEditorViewModel(WorkspaceSource model) : ObservableObject
{
    [ObservableProperty]
    private string _path = model.Path;

    [ObservableProperty]
    private string _includePatterns = model.IncludePatterns;

    [ObservableProperty]
    private bool _recursive = model.Recursive;

    [ObservableProperty]
    private string? _status;

    public WorkspaceSource ToModel() => new() { Path = Path.Trim().Trim('"'), IncludePatterns = IncludePatterns, Recursive = Recursive };
}

public sealed partial class HighlightEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string _pattern = string.Empty;

    [ObservableProperty]
    private bool _isRegex;

    [ObservableProperty]
    private string _color = WorkspaceEditorViewModel.HighlightColors[0];
}
