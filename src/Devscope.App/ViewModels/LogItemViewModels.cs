using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Devscope.Core.Logs;
using Devscope.Core.Workspaces;

namespace Devscope.App.ViewModels;

/// <summary>A saved workspace in the sidebar.</summary>
public sealed partial class WorkspaceItemViewModel : ObservableObject
{
    public WorkspaceItemViewModel(Workspace model) => Update(model);

    public Workspace Model { get; private set; } = null!;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<EnvironmentItemViewModel> _environments = [];

    public void Update(Workspace model)
    {
        Model = model;
        Name = model.Name;
        Environments = model.Environments.Select((e, i) => new EnvironmentItemViewModel(e, i)).ToList();
    }
}

/// <summary>An environment of the selected workspace; checked ones are opened.</summary>
public sealed partial class EnvironmentItemViewModel(WorkspaceEnvironment model, int colorIndex) : ObservableObject
{
    public WorkspaceEnvironment Model { get; } = model;

    public string Name => Model.Name;

    public int ColorIndex { get; } = colorIndex;

    public string Summary => Model.Sources.Count == 1 ? Model.Sources[0].Path : $"{Model.Sources.Count} paths";

    [ObservableProperty]
    private bool _isSelected = true;
}

/// <summary>A toggle chip in the filter bar (severity or environment) with a live count.</summary>
public sealed partial class FilterChipViewModel<T>(T value, string label, int colorIndex, Action onChanged) : ObservableObject
{
    public T Value { get; } = value;

    public string Label { get; } = label;

    public int ColorIndex { get; } = colorIndex;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private int _count;

    partial void OnIsEnabledChanged(bool value) => onChanged();
}

/// <summary>A colour applied to every match of <see cref="Regex"/> in the log list.</summary>
public sealed record HighlightSpec(Regex Regex, string Color, bool IsSearch);
