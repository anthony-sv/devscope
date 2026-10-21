using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;
using Devscope.Core.Ear;

namespace Devscope.App.ViewModels;

/// <summary>Columns the EAR diff list can be sorted by.</summary>
public enum EarDiffSortKey
{
    Path,
    Status,
    Left,
    Right,
}

/// <summary>
/// Feature 1: compare two EARs (or exploded deployments) entry by entry, including nested WARs/JARs.
/// Changed classes are decompiled in the background, and the comparison can refresh itself when either side
/// is redeployed. A single click previews a file's diff; a double click opens it in a tab of the diff window.
/// </summary>
public sealed partial class EarDiffViewModel : ObservableObject, IPage, IDisposable
{
    private const string ArchiveFilter = "Java archives (*.ear;*.war;*.jar;*.zip)|*.ear;*.war;*.jar;*.rar;*.sar;*.zip|All files (*.*)|*.*";

    /// <summary>Classes per Java process when decompiling in the background.</summary>
    private const int PrefetchBatchSize = 100;

    private readonly IEarComparer _comparer;
    private readonly IArchiveScanner _scanner;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IUserSettingsStore _settings;
    private readonly IJavaDecompiler _decompiler;
    private readonly SynchronizationContext? _ui;
    private readonly DiffZoom _zoom;

    // Decompiled source keyed by root, path, and the size and CRC of the class and its inner classes,
    // so entries survive an auto-refresh unless their bytes changed.
    private readonly ConcurrentDictionary<string, Task<string>> _decompiled = new(StringComparer.Ordinal);

    // Modified files whose text is identical (only line endings, a byte order mark or compiler output differ), keyed
    // by path and both sides' size and CRC (see SameTextKey) so the verdict survives refreshes. UI thread only.
    private readonly HashSet<string> _sameText = new(StringComparer.Ordinal);
    private Dictionary<string, EarDiffRow> _rowsByPath = new(StringComparer.Ordinal);
    private CancellationTokenSource? _prefetchCts;
    private CancellationTokenSource? _refreshCts;
    private DeploymentWatcher? _watcher;
    private ClassIndex? _classIndex;
    private DecompilerSetup? _decompilerSetup;
    private bool _restoringSelection;
    private bool _isRefreshing;
    private bool _refreshPending;

    public EarDiffViewModel(
        IEarComparer comparer,
        IArchiveScanner scanner,
        IJavaDecompiler decompiler,
        IDialogService dialogs,
        IShellService shell,
        IUserSettingsStore settings)
    {
        _comparer = comparer;
        _scanner = scanner;
        _decompiler = decompiler;
        _dialogs = dialogs;
        _shell = shell;
        _settings = settings;
        _leftPath = settings.Current.LeftEarPath;
        _rightPath = settings.Current.RightEarPath;
        _autoRefresh = settings.Current.AutoRefresh;
        _ui = SynchronizationContext.Current;
        _zoom = new DiffZoom(settings);
        Preview = new DiffDocumentViewModel(LoadDiffAsync, _zoom);
        _settings.Changed += OnSettingsChanged;
        LocateDecompiler();
    }

    public string Title => "EAR diff";

    public string Glyph => "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    private string _leftPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    private string _rightPath;

    [ObservableProperty]
    private string _statusText = "Pick two EAR files (or exploded folders). Local and \\\\server\\share paths both work; nothing is uploaded.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyPropertyChangedFor(nameof(ChangedCount))]
    private EarComparison? _comparison;

    [ObservableProperty]
    private ObservableCollection<EarDiffRow> _rows = [];

    /// <summary>The row whose diff the preview pane shows.</summary>
    [ObservableProperty]
    private EarDiffRow? _selectedRow;

    /// <summary>The diff of <see cref="SelectedRow"/>, shown next to the list.</summary>
    public DiffDocumentViewModel Preview { get; }

    /// <summary>Tabs of the diff window, opened by double-clicking rows.</summary>
    public ObservableCollection<DiffDocumentViewModel> OpenDocuments { get; } = [];

    [ObservableProperty]
    private DiffDocumentViewModel? _activeDocument;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showModified = true;

    [ObservableProperty]
    private bool _showAdded = true;

    [ObservableProperty]
    private bool _showRemoved = true;

    [ObservableProperty]
    private bool _showUnchanged;

    [ObservableProperty]
    private bool _showArchives;

    /// <summary>List modified files whose text is identical too (tagged "same text"); hidden by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SameTextHint), nameof(SameTextAction))]
    private bool _showSameText;

    /// <summary>Modified files (not archive rows) of the comparison whose text is identical.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangedCount), nameof(SameTextHint))]
    private int _sameTextCount;

    /// <summary>Modified files whose content actually differs.</summary>
    public int ChangedCount => (Comparison?.Modified ?? 0) - SameTextCount;

    public string SameTextHint
    {
        get
        {
            var one = SameTextCount == 1;
            var files = one ? "1 modified file has" : $"{SameTextCount:N0} modified files have";
            const string why = "identical text (only line endings, a byte order mark or compiler output differ)";
            return ShowSameText
                ? $"{files} {why}; {(one ? "it is" : "they are")} tagged “same text” in the list."
                : $"{files} {why} and {(one ? "is" : "are")} hidden.";
        }
    }

    public string SameTextAction => ShowSameText ? "Hide them" : "Show them";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathHeader), nameof(StatusHeader), nameof(LeftHeader), nameof(RightHeader))]
    private EarDiffSortKey _sortKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PathHeader), nameof(StatusHeader), nameof(LeftHeader), nameof(RightHeader))]
    private bool _sortDescending;

    public string PathHeader => Header("File", EarDiffSortKey.Path);

    public string StatusHeader => Header("Status", EarDiffSortKey.Status);

    public string LeftHeader => Header("Left", EarDiffSortKey.Left);

    public string RightHeader => Header("Right", EarDiffSortKey.Right);

    [ObservableProperty]
    private string _decompilerStatus = string.Empty;

    /// <summary>Progress of background decompiling; empty when idle.</summary>
    [ObservableProperty]
    private string _backgroundStatus = string.Empty;

    /// <summary>Rescan automatically when either compared deployment changes on disk.</summary>
    [ObservableProperty]
    private bool _autoRefresh;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecompilerHeader))]
    private bool _isDecompilerReady;

    public string DecompilerHeader => IsDecompilerReady
        ? "Java decompiler: ready"
        : "Java decompiler: not set up, so .class files show a binary summary";

    public bool HasResult => Comparison is not null;

    partial void OnSearchTextChanged(string value) => ApplyRowFilter();

    partial void OnShowModifiedChanged(bool value) => ApplyRowFilter();

    partial void OnShowAddedChanged(bool value) => ApplyRowFilter();

    partial void OnShowRemovedChanged(bool value) => ApplyRowFilter();

    partial void OnShowUnchangedChanged(bool value) => ApplyRowFilter();

    partial void OnShowArchivesChanged(bool value) => ApplyRowFilter();

    partial void OnShowSameTextChanged(bool value) => ApplyRowFilter();

    partial void OnSelectedRowChanged(EarDiffRow? value)
    {
        if (!_restoringSelection)
        {
            ShowPreview(value?.Entry);
        }
    }

    private void ShowPreview(EarDiffEntry? entry)
    {
        // Records compare by value: a refreshed row whose bytes did not change keeps its open diff.
        if (entry != Preview.Entry)
        {
            _ = Preview.LoadAsync(entry);
        }
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        _settings.Current.AutoRefresh = value;
        _settings.Save();
        RestartWatcher();
    }

    [RelayCommand]
    private void OpenSettings() => _dialogs.ShowSettings();

    [RelayCommand]
    private void ToggleSameText() => ShowSameText = !ShowSameText;

    [RelayCommand]
    private void SortBy(EarDiffSortKey key)
    {
        if (SortKey == key)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortKey = key;
            SortDescending = false;
        }

        ApplyRowFilter();
    }

    private string Header(string label, EarDiffSortKey key) =>
        SortKey == key ? $"{label} {(SortDescending ? "▼" : "▲")}" : label;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        LocateDecompiler();
        if (Comparison is { } comparison)
        {
            StartPrefetch(comparison);
        }
    }

    private void LocateDecompiler()
    {
        var setup = DecompilerSettings.Locate(_settings.Current, out var message);
        if (setup != _decompilerSetup)
        {
            _decompilerSetup = setup;
            _decompiled.Clear();

            // Class verdicts came from the old decompiler; text verdicts are recomputed on the next comparison.
            _sameText.RemoveWhere(key => IsClassFile(key[..key.IndexOf('|', StringComparison.Ordinal)]));
            ApplyRowFilter();
        }

        DecompilerStatus = message;
        IsDecompilerReady = setup is not null;
    }

    [RelayCommand]
    private void BrowseLeftFile() => LeftPath = _dialogs.PickFile("Left (old) archive", ArchiveFilter) ?? LeftPath;

    [RelayCommand]
    private void BrowseLeftFolder() => LeftPath = _dialogs.PickFolder("Left (old) exploded deployment") ?? LeftPath;

    [RelayCommand]
    private void BrowseRightFile() => RightPath = _dialogs.PickFile("Right (new) archive", ArchiveFilter) ?? RightPath;

    [RelayCommand]
    private void BrowseRightFolder() => RightPath = _dialogs.PickFolder("Right (new) exploded deployment") ?? RightPath;

    [RelayCommand]
    private void Swap() => (LeftPath, RightPath) = (RightPath, LeftPath);

    private bool CanCompare() => !string.IsNullOrWhiteSpace(LeftPath) && !string.IsNullOrWhiteSpace(RightPath);

    [RelayCommand(CanExecute = nameof(CanCompare), IncludeCancelCommand = true)]
    private async Task CompareAsync(CancellationToken cancellationToken)
    {
        var left = LeftPath.Trim().Trim('"');
        var right = RightPath.Trim().Trim('"');
        _settings.Current.LeftEarPath = left;
        _settings.Current.RightEarPath = right;
        _settings.Save();

        StopBackgroundWork();
        CloseAllDocuments();
        Comparison = null;
        _decompiled.Clear();
        Rows = [];
        _rowsByPath = new(StringComparer.Ordinal);
        SameTextCount = 0;
        _ = Preview.LoadAsync(null);
        StatusText = "Scanning…";

        var scanned = new Dictionary<string, int>();
        var progress = new Progress<ScanProgress>(p =>
        {
            scanned[p.Side] = p.EntriesScanned;
            StatusText = $"Scanning… left {scanned.GetValueOrDefault("left"):N0} · right {scanned.GetValueOrDefault("right"):N0} entries";
        });

        try
        {
            var result = await _comparer.CompareAsync(left, right, progress, cancellationToken);
            _sameText.UnionWith((await FindSameTextFilesAsync(result, cancellationToken)).Select(SameTextKey));
            ShowComparison(result, previousRow: null);
            StatusText = Summarize(result);
            StartPrefetch(result);
            RestartWatcher();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = "Comparison failed.";
            _dialogs.ShowError(ex.Message, "Could not compare");
        }
    }

    private static string Summarize(EarComparison result) => result.Added + result.Removed + result.Modified == 0
        ? $"Identical content ({result.Unchanged:N0} files) · {result.Elapsed.TotalSeconds:N1}s"
        // The status chips carry the counts, and keep them current as identical-text classes are found.
        : $"Compared {result.Added + result.Removed + result.Modified + result.Unchanged:N0} files · {result.Elapsed.TotalSeconds:N1}s";

    /// <summary>Shows a comparison, keeping the selected row (and its open diff, if that file did not change).</summary>
    private void ShowComparison(EarComparison result, EarDiffEntry? previousRow)
    {
        Comparison = result;
        RebuildRows(previousRow is null ? null : result.Entries.FirstOrDefault(e => e.Path == previousRow.Path));
    }

    private void StopBackgroundWork()
    {
        _prefetchCts?.Cancel();
        _refreshCts?.Cancel();
        BackgroundStatus = string.Empty;
        StopWatcher();
    }

    private void RestartWatcher()
    {
        StopWatcher();
        if (!AutoRefresh || Comparison is not { } comparison)
        {
            return;
        }

        _watcher = new DeploymentWatcher([comparison.LeftRoot, comparison.RightRoot]);
        _watcher.Changed += OnDeploymentChanged;
        _ = _watcher.StartAsync();
    }

    private void StopWatcher()
    {
        if (_watcher is not null)
        {
            _watcher.Changed -= OnDeploymentChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    // Raised on a thread-pool thread.
    private void OnDeploymentChanged(object? sender, EventArgs e)
    {
        if (_ui is null)
        {
            _ = RefreshAsync();
        }
        else
        {
            _ui.Post(_ => _ = RefreshAsync(), null);
        }
    }

    private async Task RefreshAsync()
    {
        if (Comparison is not { } previous || CompareCommand.IsRunning)
        {
            return;
        }

        // Changes that arrive mid-rescan are picked up by one more pass afterwards.
        if (_isRefreshing)
        {
            _refreshPending = true;
            return;
        }

        _isRefreshing = true;
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        try
        {
            do
            {
                _refreshPending = false;
                StatusText = "Change detected on disk, rescanning…";
                var result = await _comparer.CompareAsync(previous.LeftRoot, previous.RightRoot, progress: null, ct);
                _sameText.UnionWith((await FindSameTextFilesAsync(result, ct)).Select(SameTextKey));
                ct.ThrowIfCancellationRequested();

                ShowComparison(result, Preview.Entry);
                RefreshDocuments(result);
                StatusText = $"{Summarize(result)} · refreshed {DateTime.Now:T}";
                StartPrefetch(result);
                previous = result;
            }
            while (_refreshPending);
        }
        catch (OperationCanceledException)
        {
            // A manual comparison took over.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Typically a half-written archive; the watcher fires again once it settles.
            StatusText = $"Auto-refresh failed: {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>Reloads tabs whose file changed in a refresh, and closes those whose file is gone.</summary>
    private void RefreshDocuments(EarComparison comparison)
    {
        foreach (var document in OpenDocuments.ToList())
        {
            var entry = document.Entry is { } old ? comparison.Entries.FirstOrDefault(e => e.Path == old.Path) : null;
            if (entry is null || entry.IsArchive)
            {
                CloseDocument(document);
            }
            else if (entry != document.Entry)
            {
                _ = document.LoadAsync(entry);
            }
        }
    }

    /// <summary>Starts decompiling changed classes in the background, which also finds classes with identical source.</summary>
    private void StartPrefetch(EarComparison comparison)
    {
        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        _prefetchCts = null;
        BackgroundStatus = string.Empty;
        if (_decompilerSetup is not { } setup || !_settings.Current.PrefetchDecompile)
        {
            return;
        }

        _prefetchCts = new CancellationTokenSource();
        _ = PrefetchAsync(comparison, setup, _prefetchCts.Token);
    }

    /// <summary>
    /// Finds modified text files that differ only in line endings or a byte order mark. Runs before the rows are
    /// shown, so they never move once listed.
    /// </summary>
    private async Task<IReadOnlyCollection<EarDiffEntry>> FindSameTextFilesAsync(EarComparison comparison, CancellationToken ct)
    {
        var candidates = comparison.Entries
            .Where(e => e.Status == DiffStatus.Modified && !e.IsArchive && !IsClassFile(e.Path)
                && Math.Max(e.Left!.Length, e.Right!.Length) <= ContentDiff.MaxTextBytes && !_sameText.Contains(SameTextKey(e)))
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        StatusText = $"Checking {candidates.Count:N0} changed text files…";
        var found = new ConcurrentBag<EarDiffEntry>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };
        await Parallel.ForEachAsync(candidates, options, async (entry, token) =>
        {
            try
            {
                var left = await _scanner.ReadEntryAsync(comparison.LeftRoot, entry.Path, token);
                var right = await _scanner.ReadEntryAsync(comparison.RightRoot, entry.Path, token);
                if (ContentDiff.HasSameText(entry.Path, left, right))
                {
                    found.Add(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Stays with the changed files; opening it reports the error.
            }
        });

        return found;
    }

    /// <summary>Decompiles every changed class, batched per archive so each Java process handles many classes.</summary>
    private async Task PrefetchAsync(EarComparison comparison, DecompilerSetup setup, CancellationToken ct)
    {
        // Outer classes only: inner classes are decompiled together with them. A class whose only change is in an
        // inner class (Foo$1) is decompiled too, so its rows can be checked for identical source.
        var byPath = comparison.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var outers = comparison.Entries
            .Where(e => !e.IsArchive && e.Status != DiffStatus.Unchanged && IsClassFile(e.Path))
            .Select(e => byPath.GetValueOrDefault(OuterClassPath(e.Path)))
            .OfType<EarDiffEntry>()
            .Distinct()
            .ToList();

        var work = new List<ClassGroupWork>();
        var pairs = new Dictionary<string, ClassPair>(StringComparer.Ordinal);
        foreach (var entry in outers)
        {
            var left = entry.Left is null ? null : FindClassGroup(comparison, isLeft: true, entry);
            var right = entry.Right is null ? null : FindClassGroup(comparison, isLeft: false, entry);
            work.AddRange(new[] { left, right }.OfType<ClassGroupWork>().Where(g => !_decompiled.ContainsKey(g.Key)));

            // Same decompiled source on both sides: the outer class and its inner classes have the same text.
            if (left is not null && right is not null)
            {
                var modified = left.RelatedPaths.Union(right.RelatedPaths)
                    .Select(p => byPath[p])
                    .Where(e => e.Status == DiffStatus.Modified)
                    .ToList();
                if (modified.Count > 0)
                {
                    var pair = new ClassPair(left.Key, right.Key, modified);
                    pairs[left.Key] = pair;
                    pairs[right.Key] = pair;
                }
            }
        }

        // Pairs already decompiled (cached from an earlier pass) are tagged right away.
        MarkSameText(comparison, pairs.Values.Distinct().Where(HasSameSource).SelectMany(p => p.Modified));
        if (work.Count == 0)
        {
            return;
        }

        var sameText = new Progress<IReadOnlyList<EarDiffEntry>>(entries =>
        {
            if (!ct.IsCancellationRequested)
            {
                MarkSameText(comparison, entries);
            }
        });

        // Within one archive every path is unique, so a batch never has two classes writing the same file.
        var batches = work.GroupBy(g => (g.Root, g.Entry.Container)).SelectMany(g => g.Chunk(PrefetchBatchSize)).ToList();
        var done = 0;
        var failed = 0;
        var progress = new Progress<int>(n =>
        {
            if (!ct.IsCancellationRequested)
            {
                BackgroundStatus = $"Decompiling changed classes in the background… {n:N0} / {work.Count:N0}";
            }
        });
        ((IProgress<int>)progress).Report(0);

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_settings.Current.MaxDecompilerProcesses, 1, 8), CancellationToken = ct };
        try
        {
            await Parallel.ForEachAsync(batches, options, async (batch, token) =>
            {
                try
                {
                    var groups = new List<ClassGroup>(batch.Length);
                    foreach (var item in batch)
                    {
                        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                        foreach (var path in item.RelatedPaths)
                        {
                            files[PathInContainer(item.Entry.Container, path)] = await _scanner.ReadEntryAsync(item.Root, path, token);
                        }

                        groups.Add(new ClassGroup(PathInContainer(item.Entry.Container, item.Entry.Path), files));
                    }

                    var sources = await _decompiler.DecompileManyAsync(setup, groups, token);
                    foreach (var item in batch)
                    {
                        if (sources.TryGetValue(PathInContainer(item.Entry.Container, item.Entry.Path), out var source))
                        {
                            _decompiled.TryAdd(item.Key, Task.FromResult(source));
                        }
                        else
                        {
                            Interlocked.Increment(ref failed);
                        }
                    }

                    var same = batch
                        .Select(item => pairs.GetValueOrDefault(item.Key))
                        .OfType<ClassPair>()
                        .Distinct()
                        .Where(HasSameSource)
                        .SelectMany(p => p.Modified)
                        .ToList();
                    if (same.Count > 0)
                    {
                        ((IProgress<IReadOnlyList<EarDiffEntry>>)sameText).Report(same);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // Selecting one of these rows decompiles it on its own, which reports the actual error.
                    Interlocked.Add(ref failed, batch.Length);
                }

                ((IProgress<int>)progress).Report(Interlocked.Add(ref done, batch.Length));
            });

            BackgroundStatus = failed == 0 ? string.Empty : $"{failed:N0} classes could not be decompiled in the background";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a new comparison, refresh or decompiler setup.
        }
    }

    private static string PathInContainer(string container, string path) =>
        container.Length == 0 ? path : path[(container.Length + ArchivePath.NestedSeparator.Length)..];

    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task ExportAsync()
    {
        if (Comparison is null)
        {
            return;
        }

        var file = _dialogs.PickSaveFile("Export differences", "CSV (*.csv)|*.csv", "ear-diff.csv");
        if (file is null)
        {
            return;
        }

        var sb = new StringBuilder("status,path,left_bytes,right_bytes,identical_text\r\n");
        foreach (var entry in Comparison.Entries.Where(e => !e.IsArchive && e.Status != DiffStatus.Unchanged))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{entry.Status},\"{entry.Path.Replace("\"", "\"\"", StringComparison.Ordinal)}\",{entry.Left?.Length},{entry.Right?.Length},{(IsSameText(entry) ? "yes" : "no")}\r\n");
        }

        await File.WriteAllTextAsync(file, sb.ToString());
        StatusText = $"Exported to {file}";
    }

    [RelayCommand]
    private void CopyPath(EarDiffRow? row)
    {
        if (row is not null)
        {
            _shell.CopyToClipboard(row.Path);
        }
    }

    /// <summary>Opens a row (the previewed one by default) in a tab of the diff window, or switches to its tab.</summary>
    [RelayCommand]
    private void OpenInWindow(EarDiffRow? row)
    {
        var entry = row?.Entry ?? Preview.Entry;
        if (entry is null || entry.IsArchive)
        {
            return;
        }

        var document = OpenDocuments.FirstOrDefault(d => d.Entry?.Path == entry.Path);
        if (document is null)
        {
            document = new DiffDocumentViewModel(LoadDiffAsync, _zoom);
            OpenDocuments.Add(document);
        }

        if (document.Entry != entry)
        {
            // The preview usually has it already: reuse its diff instead of reading and decompiling again.
            if (entry == Preview.Entry && !Preview.IsLoading && Preview.Detail is { } detail)
            {
                document.Show(entry, detail);
            }
            else
            {
                _ = document.LoadAsync(entry);
            }
        }

        ActiveDocument = document;
        _dialogs.ShowDiffWindow(this);
    }

    [RelayCommand]
    private void CloseDocument(DiffDocumentViewModel? document)
    {
        document ??= ActiveDocument;
        var index = document is null ? -1 : OpenDocuments.IndexOf(document);
        if (index < 0)
        {
            return;
        }

        OpenDocuments.RemoveAt(index);
        document!.Dispose();
        if (ActiveDocument is null || ActiveDocument == document)
        {
            ActiveDocument = OpenDocuments.Count == 0 ? null : OpenDocuments[Math.Min(index, OpenDocuments.Count - 1)];
        }
    }

    /// <summary>Closes every tab, e.g. when the diff window closes or a new comparison starts.</summary>
    public void CloseAllDocuments()
    {
        foreach (var document in OpenDocuments)
        {
            document.Dispose();
        }

        OpenDocuments.Clear();
        ActiveDocument = null;
    }

    private void ApplyRowFilter() => RebuildRows(Preview.Entry);

    /// <summary>Fills the list from the filters and sort order, keeping <paramref name="keep"/> selected when it is still listed.</summary>
    private void RebuildRows(EarDiffEntry? keep)
    {
        if (Comparison is null)
        {
            return;
        }

        var search = SearchText.Trim();
        var rows = new List<EarDiffRow>();
        var sameTextCount = 0;
        EarDiffRow? kept = null;
        foreach (var e in Comparison.Entries)
        {
            var isSameText = IsSameText(e);
            if (isSameText && !e.IsArchive)
            {
                sameTextCount++;
            }

            var isKept = keep is not null && e == keep;
            if (!ShowsStatus(e.Status)
                || (!ShowArchives && e.IsArchive)
                || (search.Length > 0 && !e.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
                || (isSameText && !ShowSameText && !isKept)) // the open file stays listed, so it never vanishes from under the user
            {
                continue;
            }

            var row = new EarDiffRow(e, isSameText);
            rows.Add(row);
            if (isKept)
            {
                kept = row;
            }
        }

        rows.Sort(CompareRows);
        _rowsByPath = rows.ToDictionary(r => r.Path, StringComparer.Ordinal);

        var restoring = _restoringSelection;
        _restoringSelection = true;
        try
        {
            Rows = new ObservableCollection<EarDiffRow>(rows);
            SelectedRow = kept;
            SameTextCount = sameTextCount;
        }
        finally
        {
            _restoringSelection = restoring;
        }

        ShowPreview(kept?.Entry);
    }

    private bool ShowsStatus(DiffStatus status) => status switch
    {
        DiffStatus.Modified => ShowModified,
        DiffStatus.Added => ShowAdded,
        DiffStatus.Removed => ShowRemoved,
        _ => ShowUnchanged,
    };

    private int CompareRows(EarDiffRow a, EarDiffRow b)
    {
        var primary = SortKey switch
        {
            EarDiffSortKey.Status => StatusRank(a.Status).CompareTo(StatusRank(b.Status)),
            EarDiffSortKey.Left => Nullable.Compare(a.Left?.Length, b.Left?.Length),
            EarDiffSortKey.Right => Nullable.Compare(a.Right?.Length, b.Right?.Length),
            _ => string.CompareOrdinal(a.Path, b.Path),
        };
        if (SortDescending)
        {
            primary = -primary;
        }

        return primary != 0 ? primary : string.CompareOrdinal(a.Path, b.Path);
    }

    // Changes first, in the order of the status chips.
    private static int StatusRank(DiffStatus status) => status switch
    {
        DiffStatus.Modified => 0,
        DiffStatus.Added => 1,
        DiffStatus.Removed => 2,
        _ => 3,
    };

    private bool IsSameText(EarDiffEntry entry) =>
        entry.Status == DiffStatus.Modified && _sameText.Contains(SameTextKey(entry));

    /// <summary>
    /// Records files found to have identical text and tags their rows. Rows are not removed here, so the list never
    /// rearranges itself while being read; the next filter, sort or refresh hides them.
    /// </summary>
    private void MarkSameText(EarComparison comparison, IEnumerable<EarDiffEntry> entries)
    {
        if (!ReferenceEquals(comparison, Comparison))
        {
            return; // a stale result from before a new comparison or refresh
        }

        foreach (var entry in entries)
        {
            if (entry.Status != DiffStatus.Modified || !_sameText.Add(SameTextKey(entry)))
            {
                continue;
            }

            if (!entry.IsArchive)
            {
                SameTextCount++;
            }

            if (_rowsByPath.TryGetValue(entry.Path, out var row) && row.Entry == entry)
            {
                row.IsSameText = true;
            }
        }
    }

    private static string SameTextKey(EarDiffEntry entry) => string.Create(
        CultureInfo.InvariantCulture, $"{entry.Path}|{entry.Left?.Length}:{entry.Left?.Crc32}|{entry.Right?.Length}:{entry.Right?.Crc32}");

    private bool HasSameSource(ClassPair pair) =>
        _decompiled.TryGetValue(pair.LeftKey, out var left) && left.IsCompletedSuccessfully
        && _decompiled.TryGetValue(pair.RightKey, out var right) && right.IsCompletedSuccessfully
        && string.Equals(left.Result, right.Result, StringComparison.Ordinal);

    /// <summary>com/acme/Foo$Bar$1.class → com/acme/Foo.class.</summary>
    private static string OuterClassPath(string path)
    {
        var folder = FolderOf(path);
        var name = path[folder.Length..];
        var dollar = name.IndexOf('$', StringComparison.Ordinal);
        return dollar <= 0 ? path : string.Concat(folder, name.AsSpan(0, dollar), ".class");
    }

    /// <summary>Builds the diff of one entry of the current comparison, for the preview and the diff window's tabs.</summary>
    private async Task<ContentDiffResult> LoadDiffAsync(EarDiffEntry entry, Action<string> reportStatus, CancellationToken ct)
    {
        if (Comparison is not { } comparison)
        {
            return new ContentDiffResult(false, [], "Run a comparison first.");
        }

        var leftTask = entry.Left is null ? Task.FromResult<byte[]?>(null) : ReadAsync(comparison.LeftRoot, entry.Path, ct);
        var rightTask = entry.Right is null ? Task.FromResult<byte[]?>(null) : ReadAsync(comparison.RightRoot, entry.Path, ct);
        var contents = await Task.WhenAll(leftTask, rightTask);

        ContentDiffResult detail;
        if (IsClassFile(entry.Path) && _decompilerSetup is { } setup)
        {
            reportStatus("Decompiling…");
            detail = await DecompileAndDiffAsync(setup, comparison, entry, contents[0], contents[1], ct);
        }
        else
        {
            detail = await Task.Run(() => ContentDiff.Build(entry.Path, contents[0], contents[1]), ct);
        }

        // Covers what the background passes skip, e.g. classes when background decompiling is off.
        if (entry.Status == DiffStatus.Modified && detail.IsText && !detail.Stats.HasChanges)
        {
            MarkSameText(comparison, [entry]);
        }

        return detail;
    }

    private async Task<byte[]?> ReadAsync(string root, string path, CancellationToken ct) =>
        await _scanner.ReadEntryAsync(root, path, ct);

    private static bool IsClassFile(string path) => path.EndsWith(".class", StringComparison.OrdinalIgnoreCase);

    private async Task<ContentDiffResult> DecompileAndDiffAsync(
        DecompilerSetup setup, EarComparison comparison, EarDiffEntry entry, byte[]? left, byte[]? right, CancellationToken ct)
    {
        try
        {
            var leftSource = left is null ? Task.FromResult<string?>(null) : DecompileSideAsync(setup, comparison, isLeft: true, entry, ct);
            var rightSource = right is null ? Task.FromResult<string?>(null) : DecompileSideAsync(setup, comparison, isLeft: false, entry, ct);
            var sources = await Task.WhenAll(leftSource, rightSource);

            var prefix = string.Join(" · ", new[] { "Decompiled with Vineflower", ContentDiff.DescribeClassVersionChange(left, right) }.OfType<string>());
            var result = await Task.Run(() => ContentDiff.BuildText(sources[0], sources[1], prefix), ct);

            // A class recompiled from equivalent source (only comments moved, another compiler build) differs in
            // bytes the decompiler does not show, such as line numbers; the row says Modified, so say why.
            return !result.Stats.HasChanges && left is not null && right is not null && !left.AsSpan().SequenceEqual(right)
                ? result with { Summary = $"{result.Summary} · the class files differ only in compiler output the decompiled source does not show (e.g. line numbers or debug info)" }
                : result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            // Fall back to the binary summary so the user still sees sizes and Java versions.
            var fallback = ContentDiff.Build(entry.Path, left, right);
            return fallback with { Summary = $"Decompiler failed: {ex.Message}{Environment.NewLine}{fallback.Summary}" };
        }
    }

    private async Task<string?> DecompileSideAsync(DecompilerSetup setup, EarComparison comparison, bool isLeft, EarDiffEntry entry, CancellationToken ct)
    {
        var group = FindClassGroup(comparison, isLeft, entry);
        var task = _decompiled.GetOrAdd(group.Key, _ => DecompileCoreAsync(setup, group));
        try
        {
            return await task.WaitAsync(ct);
        }
        catch (Exception) when (task.IsFaulted)
        {
            // Failures are not cached: the user may fix the setup and select the row again.
            _decompiled.TryRemove(new KeyValuePair<string, Task<string>>(group.Key, task));
            throw;
        }
    }

    private async Task<string> DecompileCoreAsync(DecompilerSetup setup, ClassGroupWork group)
    {
        // Not cancellable on purpose: the result is cached and reused when the row is selected again.
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in group.RelatedPaths)
        {
            files[path[(path.LastIndexOf('/') + 1)..]] = await _scanner.ReadEntryAsync(group.Root, path, CancellationToken.None);
        }

        return await _decompiler.DecompileAsync(setup, group.Entry.Name, files, CancellationToken.None);
    }

    /// <summary>A class on one side plus its inner classes (Foo$Bar.class, Foo$1.class) from the same folder, so nested types decompile too.</summary>
    private ClassGroupWork FindClassGroup(EarComparison comparison, bool isLeft, EarDiffEntry entry)
    {
        var index = _classIndex;
        if (index is null || !ReferenceEquals(index.Comparison, comparison))
        {
            index = new ClassIndex(comparison, comparison.Entries
                .Where(e => !e.IsArchive && IsClassFile(e.Path))
                .ToLookup(e => FolderOf(e.Path), StringComparer.Ordinal));
            _classIndex = index;
        }

        var root = isLeft ? comparison.LeftRoot : comparison.RightRoot;
        var directory = FolderOf(entry.Path);
        var outerName = entry.Name[..^".class".Length];
        var related = index.ByFolder[directory]
            .Where(e => (isLeft ? e.Left : e.Right) is not null && IsSameClassOrInner(e.Path, directory, outerName))
            .ToList();

        var key = new StringBuilder(root).Append('|').Append(entry.Path);
        foreach (var e in related)
        {
            var info = (isLeft ? e.Left : e.Right)!;
            key.Append(CultureInfo.InvariantCulture, $"|{e.Path}:{info.Length}:{info.Crc32}");
        }

        return new ClassGroupWork(root, entry, related.ConvertAll(e => e.Path), key.ToString());
    }

    private static string FolderOf(string path) => path[..(path.LastIndexOf('/') + 1)];

    private static bool IsSameClassOrInner(string path, string directory, string outerName)
    {
        if (!path.StartsWith(directory, StringComparison.Ordinal) || !IsClassFile(path))
        {
            return false;
        }

        var name = path.AsSpan(directory.Length);
        return !name.Contains('/')
            && name.StartsWith(outerName, StringComparison.Ordinal)
            && (name.Length == outerName.Length + ".class".Length || name[outerName.Length] == '$');
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        StopBackgroundWork();
        _prefetchCts?.Dispose();
        _refreshCts?.Dispose();
        Preview.Dispose();
        CloseAllDocuments();
    }

    /// <summary>Class files by folder for one comparison, to find inner classes without scanning every entry.</summary>
    private sealed record ClassIndex(EarComparison Comparison, ILookup<string, EarDiffEntry> ByFolder);

    /// <param name="Root">The side's root.</param>
    /// <param name="Entry">The outer class row.</param>
    /// <param name="RelatedPaths">Virtual paths of the class and its inner classes on that side.</param>
    /// <param name="Key">Identifies the content, for <see cref="_decompiled"/>.</param>
    private sealed record ClassGroupWork(string Root, EarDiffEntry Entry, IReadOnlyList<string> RelatedPaths, string Key);

    /// <param name="LeftKey">The left class group, for <see cref="_decompiled"/>.</param>
    /// <param name="RightKey">The right class group.</param>
    /// <param name="Modified">Modified rows of the outer class and its inner classes, tagged together when both sides decompile the same.</param>
    private sealed record ClassPair(string LeftKey, string RightKey, IReadOnlyList<EarDiffEntry> Modified);
}
