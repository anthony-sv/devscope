using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;
using Devscope.Core.Ear;

namespace Devscope.App.ViewModels;

/// <summary>
/// Feature 1: compare two EARs (or exploded deployments) entry by entry, including nested WARs/JARs.
/// Changed classes are decompiled in the background, and the comparison can refresh itself when either side
/// is redeployed.
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

    // Decompiled source keyed by root, path, and the size and CRC of the class and its inner classes,
    // so entries survive an auto-refresh unless their bytes changed.
    private readonly ConcurrentDictionary<string, Task<string>> _decompiled = new(StringComparer.Ordinal);
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _prefetchCts;
    private CancellationTokenSource? _refreshCts;
    private DeploymentWatcher? _watcher;
    private ClassIndex? _classIndex;
    private int[] _changeRows = [];
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
        _settings.Changed += OnSettingsChanged;
        LocateDecompiler();
    }

    public string Title => "EAR diff";

    public string Glyph => "";

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
    private EarComparison? _comparison;

    [ObservableProperty]
    private ObservableCollection<EarDiffEntry> _rows = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showUnchanged;

    [ObservableProperty]
    private bool _showArchives;

    [ObservableProperty]
    private EarDiffEntry? _selectedRow;

    [ObservableProperty]
    private bool _isLoadingDetail;

    [ObservableProperty]
    private ContentDiffResult? _detail;

    [ObservableProperty]
    private SideBySideLine? _selectedLine;

    [ObservableProperty]
    private string _loadingText = string.Empty;

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

    partial void OnShowUnchangedChanged(bool value) => ApplyRowFilter();

    partial void OnShowArchivesChanged(bool value) => ApplyRowFilter();

    partial void OnSelectedRowChanged(EarDiffEntry? value)
    {
        if (!_restoringSelection)
        {
            _ = LoadDetailAsync(value);
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
        Comparison = null;
        _decompiled.Clear();
        Rows = [];
        Detail = null;
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
        : $"{result.Modified:N0} modified · {result.Added:N0} added · {result.Removed:N0} removed · {result.Unchanged:N0} unchanged · {result.Elapsed.TotalSeconds:N1}s";

    /// <summary>Shows a comparison, keeping the selected row (and its open diff, if that file did not change).</summary>
    private void ShowComparison(EarComparison result, EarDiffEntry? previousRow)
    {
        EarDiffEntry? match;
        _restoringSelection = true;
        try
        {
            Comparison = result;
            ApplyRowFilter();
            match = previousRow is null ? null : Rows.FirstOrDefault(r => r.Path == previousRow.Path);
            SelectedRow = match;
        }
        finally
        {
            _restoringSelection = false;
        }

        if (match != previousRow)
        {
            _ = LoadDetailAsync(match);
        }
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
                ct.ThrowIfCancellationRequested();

                ShowComparison(result, SelectedRow);
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

    /// <summary>Decompiles every changed class, batched per archive so each Java process handles many classes.</summary>
    private async Task PrefetchAsync(EarComparison comparison, DecompilerSetup setup, CancellationToken ct)
    {
        // Outer classes only: inner classes are decompiled together with them.
        var work = new List<ClassGroupWork>();
        foreach (var entry in comparison.Entries.Where(e => !e.IsArchive && e.Status != DiffStatus.Unchanged && IsClassFile(e.Path) && !e.Name.Contains('$')))
        {
            foreach (var isLeft in (bool[])[true, false])
            {
                if ((isLeft ? entry.Left : entry.Right) is not null && FindClassGroup(comparison, isLeft, entry) is var group && !_decompiled.ContainsKey(group.Key))
                {
                    work.Add(group);
                }
            }
        }

        if (work.Count == 0)
        {
            return;
        }

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

        var sb = new StringBuilder("status,path,left_bytes,right_bytes\r\n");
        foreach (var entry in Comparison.Entries.Where(e => !e.IsArchive && e.Status != DiffStatus.Unchanged))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{entry.Status},\"{entry.Path.Replace("\"", "\"\"", StringComparison.Ordinal)}\",{entry.Left?.Length},{entry.Right?.Length}\r\n");
        }

        await File.WriteAllTextAsync(file, sb.ToString());
        StatusText = $"Exported to {file}";
    }

    [RelayCommand]
    private void CopyPath(EarDiffEntry? entry)
    {
        if (entry is not null)
        {
            _shell.CopyToClipboard(entry.Path);
        }
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

    private void ApplyRowFilter()
    {
        if (Comparison is null)
        {
            return;
        }

        var search = SearchText.Trim();
        var rows = Comparison.Entries.Where(e =>
            (ShowUnchanged || e.Status != DiffStatus.Unchanged)
            && (ShowArchives || !e.IsArchive)
            && (search.Length == 0 || e.Path.Contains(search, StringComparison.OrdinalIgnoreCase)));
        Rows = new ObservableCollection<EarDiffEntry>(rows);
    }

    private async Task LoadDetailAsync(EarDiffEntry? entry)
    {
        if (_detailCts is not null)
        {
            await _detailCts.CancelAsync();
            _detailCts.Dispose();
        }

        Detail = null;
        _changeRows = [];
        if (entry is null || entry.IsArchive || Comparison is null)
        {
            return;
        }

        _detailCts = new CancellationTokenSource();
        var ct = _detailCts.Token;
        var comparison = Comparison;
        IsLoadingDetail = true;
        LoadingText = "Reading…";
        try
        {
            var leftTask = entry.Left is null ? Task.FromResult<byte[]?>(null) : ReadAsync(comparison.LeftRoot, entry.Path, ct);
            var rightTask = entry.Right is null ? Task.FromResult<byte[]?>(null) : ReadAsync(comparison.RightRoot, entry.Path, ct);
            var contents = await Task.WhenAll(leftTask, rightTask);

            ContentDiffResult detail;
            if (IsClassFile(entry.Path) && _decompilerSetup is { } setup)
            {
                LoadingText = "Decompiling…";
                detail = await DecompileAndDiffAsync(setup, comparison, entry, contents[0], contents[1], ct);
            }
            else
            {
                detail = await Task.Run(() => ContentDiff.Build(entry.Path, contents[0], contents[1]), ct);
            }

            _changeRows = detail.Lines.Select((line, index) => (line, index)).Where(x => x.line.IsChange).Select(x => x.index).ToArray();
            Detail = detail;
            SelectedLine = _changeRows.Length > 0 ? detail.Lines[_changeRows[0]] : null;
        }
        catch (OperationCanceledException)
        {
            // Another row was selected.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Detail = new ContentDiffResult(false, [], $"Could not read entry: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingDetail = false;
                LoadingText = string.Empty;
            }
        }
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
            return await Task.Run(() => ContentDiff.BuildText(sources[0], sources[1], prefix), ct);
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
        _detailCts?.Cancel();
        _detailCts?.Dispose();
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

    /// <summary>Class files by folder for one comparison, to find inner classes without scanning every entry.</summary>
    private sealed record ClassIndex(EarComparison Comparison, ILookup<string, EarDiffEntry> ByFolder);

    /// <param name="Root">The side's root.</param>
    /// <param name="Entry">The outer class row.</param>
    /// <param name="RelatedPaths">Virtual paths of the class and its inner classes on that side.</param>
    /// <param name="Key">Identifies the content, for <see cref="_decompiled"/>.</param>
    private sealed record ClassGroupWork(string Root, EarDiffEntry Entry, IReadOnlyList<string> RelatedPaths, string Key);
}
