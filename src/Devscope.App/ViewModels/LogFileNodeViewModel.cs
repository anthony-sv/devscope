using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Devscope.Core.Logs;

namespace Devscope.App.ViewModels;

/// <summary>
/// A folder or file in the Files tree of a log session. Unticked files are hidden from the list (they keep being read,
/// so ticking them again shows their events at once). A folder's box is ticked, unticked or mixed.
/// </summary>
public sealed partial class LogFileNodeViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _updating;

    public LogFileNodeViewModel(LogFileTreeNode node, LogFileNodeViewModel? parent, Func<string, bool> isChecked, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(isChecked);
        _onChanged = onChanged;
        Parent = parent;
        Name = node.Name;
        FullPath = node.FullPath;
        Environment = node.Environment;
        IsFile = node.IsFile;
        Children = [.. node.Children.Select(c => new LogFileNodeViewModel(c, this, isChecked, onChanged))];
        _isChecked = IsFile ? isChecked(FullPath) : AggregateChildren();
        _isExpanded = true;
    }

    public LogFileNodeViewModel? Parent { get; }

    public string Name { get; }

    public string FullPath { get; }

    public string Environment { get; }

    public bool IsFile { get; }

    public string ToolTip => FullPath.Length > 0 ? FullPath : Environment;

    public ObservableCollection<LogFileNodeViewModel> Children { get; }

    /// <summary>True, false, or null for a folder whose files are partly ticked.</summary>
    [ObservableProperty]
    private bool? _isChecked;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Events read from this file, or from every file below this folder.</summary>
    [ObservableProperty]
    private int _count;

    /// <summary>Errors and fatals among <see cref="Count"/>.</summary>
    [ObservableProperty]
    private int _errorCount;

    public IEnumerable<LogFileNodeViewModel> Files() => IsFile ? [this] : Children.SelectMany(c => c.Files());

    public IEnumerable<LogFileNodeViewModel> SelfAndDescendants() => [this, .. Children.SelectMany(c => c.SelfAndDescendants())];

    /// <summary>Sets the box without notifying the session (used for bulk changes, which refilter once).</summary>
    public void SetCheckedSilently(bool value)
    {
        _updating = true;
        try
        {
            IsChecked = value;
            foreach (var child in Children)
            {
                child.SetCheckedSilently(value);
            }
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Recomputes folder boxes from their files, bottom-up.</summary>
    public void RefreshFromChildren()
    {
        foreach (var child in Children)
        {
            child.RefreshFromChildren();
        }

        if (!IsFile)
        {
            _updating = true;
            IsChecked = AggregateChildren();
            _updating = false;
        }
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_updating)
        {
            return;
        }

        // A two-state CheckBox turns a mixed box off when clicked; null only comes from code, and means "untick" too.
        var target = value ?? false;
        _updating = true;
        try
        {
            if (value is null)
            {
                IsChecked = false;
            }

            foreach (var child in Children)
            {
                child.SetCheckedSilently(target);
            }
        }
        finally
        {
            _updating = false;
        }

        Parent?.OnChildChanged();
        _onChanged();
    }

    private void OnChildChanged()
    {
        _updating = true;
        IsChecked = AggregateChildren();
        _updating = false;
        Parent?.OnChildChanged();
    }

    private bool? AggregateChildren()
    {
        if (Children.Count == 0)
        {
            return true;
        }

        var all = Children.All(c => c.IsChecked == true);
        var none = Children.All(c => c.IsChecked == false);
        return all ? true : none ? false : null;
    }
}
