using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.Core.Database;
using Devscope.Core.Ear;

namespace Devscope.App.ViewModels;

/// <summary>Side-by-side diff of one database object's scripts, with change-to-change navigation.</summary>
public sealed partial class ScriptDiffViewModel(DiffZoom zoom) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cts;
    private int[] _changeRows = [];

    /// <summary>Text zoom, shared with the EAR diff.</summary>
    public DiffZoom Zoom { get; } = zoom;

    [ObservableProperty]
    private ContentDiffResult? _detail;

    [ObservableProperty]
    private SideBySideLine? _selectedLine;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Diffs the scripts of <paramref name="entry"/> off the UI thread; null empties the view.</summary>
    /// <remarks>Fire-and-forget safe: a newer call cancels this one, and nothing escapes.</remarks>
    public async Task LoadAsync(DbDiffEntry? entry)
    {
        var previous = _cts;
        var cts = new CancellationTokenSource();
        _cts = cts;
        if (previous is not null)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        SetDetail(null);
        if (entry is null)
        {
            IsLoading = false;
            return;
        }

        IsLoading = true;
        try
        {
            var prefix = entry.Status switch
            {
                DiffStatus.Removed => "Only in left",
                DiffStatus.Added => "Only in right",
                _ => null,
            };
            var detail = await Task.Run(() => ContentDiff.BuildText(entry.LeftScript, entry.RightScript, prefix), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                SetDetail(detail);
            }
        }
        catch (OperationCanceledException)
        {
            // Another object was selected.
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private void SetDetail(ContentDiffResult? detail)
    {
        _changeRows = detail is null
            ? []
            : detail.Lines.Select((line, index) => (line, index)).Where(x => x.line.IsChange).Select(x => x.index).ToArray();
        Detail = detail;
        SelectedLine = detail is not null && _changeRows.Length > 0 ? detail.Lines[_changeRows[0]] : null;
    }

    [RelayCommand]
    private void NextChange() => MoveToChange(forward: true);

    [RelayCommand]
    private void PreviousChange() => MoveToChange(forward: false);

    private void MoveToChange(bool forward)
    {
        if (Detail is not { } detail || _changeRows.Length == 0)
        {
            return;
        }

        var current = -1;
        for (var i = 0; SelectedLine is not null && i < detail.Lines.Count; i++)
        {
            if (ReferenceEquals(detail.Lines[i], SelectedLine))
            {
                current = i;
                break;
            }
        }

        var target = forward
            ? _changeRows.FirstOrDefault(i => i > current, _changeRows[0])
            : _changeRows.LastOrDefault(i => i < current, _changeRows[^1]);
        SelectedLine = detail.Lines[target];
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
