using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;
using Devscope.Core.Ear;

namespace Devscope.App.ViewModels;

/// <summary>Builds the diff of one entry, reporting what it is doing ("Reading…", "Decompiling…").</summary>
public delegate Task<ContentDiffResult> DiffLoader(EarDiffEntry entry, Action<string> reportStatus, CancellationToken cancellationToken);

/// <summary>
/// The diff of one file, with change-to-change navigation. Shown in the EAR diff page's preview pane and in each
/// tab of the diff window.
/// </summary>
public sealed partial class DiffDocumentViewModel(DiffLoader load, DiffZoom zoom) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cts;

    /// <summary>Text zoom, shared by all documents.</summary>
    public DiffZoom Zoom { get; } = zoom;
    private int[] _changeRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntry))]
    private EarDiffEntry? _entry;

    [ObservableProperty]
    private ContentDiffResult? _detail;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadingText = string.Empty;

    [ObservableProperty]
    private SideBySideLine? _selectedLine;

    public bool HasEntry => Entry is not null;

    /// <summary>Shows <paramref name="entry"/>, cancelling a load still running; null empties the document.</summary>
    /// <remarks>Fire-and-forget safe: nothing escapes, or the failure would stay invisible until the faulted task is finalized.</remarks>
    public async Task LoadAsync(EarDiffEntry? entry)
    {
        // Swap in this call's source before awaiting, so overlapping calls each cancel only their predecessor
        // and the field never points at a disposed source.
        var previous = _cts;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        try
        {
            if (previous is not null)
            {
                await previous.CancelAsync();
                previous.Dispose();
            }

            if (ct.IsCancellationRequested)
            {
                return; // a newer load took over while the previous one was cancelled
            }

            Entry = entry;
            SetDetail(null);
            if (entry is null || entry.IsArchive)
            {
                IsLoading = false;
                LoadingText = string.Empty;
                return;
            }

            IsLoading = true;
            LoadingText = "Reading…";
            var detail = await load(entry, status => LoadingText = status, ct);
            if (!ct.IsCancellationRequested)
            {
                SetDetail(detail);
            }
        }
        catch (OperationCanceledException)
        {
            // Another file was selected.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!ct.IsCancellationRequested)
            {
                SetDetail(new ContentDiffResult(false, [], $"Could not read entry: {ex.Message}"));
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("EAR diff detail", ex);
            if (!ct.IsCancellationRequested)
            {
                SetDetail(new ContentDiffResult(false, [], $"Could not show this entry: {ex.Message}{Environment.NewLine}Details were written to {CrashLog.FilePath}"));
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
                LoadingText = string.Empty;
            }
        }
    }

    /// <summary>Shows a diff that was already built (e.g. by the preview), without loading again.</summary>
    public void Show(EarDiffEntry entry, ContentDiffResult detail)
    {
        _cts?.Cancel();
        Entry = entry;
        IsLoading = false;
        LoadingText = string.Empty;
        SetDetail(detail);
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
        if (Detail is not { IsText: true } detail || _changeRows.Length == 0)
        {
            return;
        }

        var current = SelectedLine is null ? -1 : IndexOf(detail.Lines, SelectedLine);
        var target = forward
            ? _changeRows.FirstOrDefault(i => i > current, _changeRows[0])
            : _changeRows.LastOrDefault(i => i < current, _changeRows[^1]);
        SelectedLine = detail.Lines[target];
    }

    private static int IndexOf(IReadOnlyList<SideBySideLine> lines, SideBySideLine line)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (ReferenceEquals(lines[i], line))
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
