using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;
using Devscope.Core.Ear;

namespace Devscope.App.ViewModels;

/// <summary>Feature 1: compare two EARs (or exploded deployments) entry by entry, including nested WARs/JARs.</summary>
public sealed partial class EarDiffViewModel : ObservableObject, IPage, IDisposable
{
    private const string ArchiveFilter = "Java archives (*.ear;*.war;*.jar;*.zip)|*.ear;*.war;*.jar;*.rar;*.sar;*.zip|All files (*.*)|*.*";

    private readonly IEarComparer _comparer;
    private readonly IArchiveScanner _scanner;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IUserSettingsStore _settings;
    private readonly IJavaDecompiler _decompiler;

    // Decompiled source per (root, path); valid for the lifetime of one comparison.
    private readonly ConcurrentDictionary<(string Root, string Path), Task<string>> _decompiled = new();
    private CancellationTokenSource? _detailCts;
    private int[] _changeRows = [];
    private DecompilerSetup? _decompilerSetup;

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
        _javaPath = settings.Current.JavaPath;
        _decompilerJarPath = settings.Current.DecompilerJarPath;
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

    /// <summary>java.exe override; empty means auto-detect.</summary>
    [ObservableProperty]
    private string _javaPath;

    /// <summary>Vineflower jar override; empty means auto-detect.</summary>
    [ObservableProperty]
    private string _decompilerJarPath;

    [ObservableProperty]
    private string _decompilerStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecompilerHeader))]
    private bool _isDecompilerReady;

    public string DecompilerHeader => IsDecompilerReady
        ? "Java decompiler: ready"
        : "Java decompiler: not set up (.class files show a binary summary)";

    public bool HasResult => Comparison is not null;

    partial void OnSearchTextChanged(string value) => ApplyRowFilter();

    partial void OnShowUnchangedChanged(bool value) => ApplyRowFilter();

    partial void OnShowArchivesChanged(bool value) => ApplyRowFilter();

    partial void OnSelectedRowChanged(EarDiffEntry? value) => _ = LoadDetailAsync(value);

    partial void OnJavaPathChanged(string value) => LocateDecompiler();

    partial void OnDecompilerJarPathChanged(string value) => LocateDecompiler();

    [RelayCommand]
    private void BrowseJava() =>
        JavaPath = _dialogs.PickFile("java.exe (Java 17 or newer)", "java.exe|java.exe|Programs (*.exe)|*.exe") ?? JavaPath;

    [RelayCommand]
    private void BrowseDecompilerJar() =>
        DecompilerJarPath = _dialogs.PickFile("Vineflower jar", "Vineflower (vineflower*.jar)|vineflower*.jar|Jar files (*.jar)|*.jar") ?? DecompilerJarPath;

    private void LocateDecompiler()
    {
        _settings.Current.JavaPath = JavaPath.Trim();
        _settings.Current.DecompilerJarPath = DecompilerJarPath.Trim();
        _settings.Save();

        var exeFolder = AppContext.BaseDirectory;
        _decompilerSetup = DecompilerLocator.Locate(JavaPath, DecompilerJarPath, [exeFolder, Path.Combine(exeFolder, "tools")], out var message);
        _decompiled.Clear();
        DecompilerStatus = message;
        IsDecompilerReady = _decompilerSetup is not null;
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
            Comparison = result;
            ApplyRowFilter();
            StatusText = result.Added + result.Removed + result.Modified == 0
                ? $"Identical content ({result.Unchanged:N0} files) · {result.Elapsed.TotalSeconds:N1}s"
                : $"{result.Modified:N0} modified · {result.Added:N0} added · {result.Removed:N0} removed · {result.Unchanged:N0} unchanged · {result.Elapsed.TotalSeconds:N1}s";
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
        var root = isLeft ? comparison.LeftRoot : comparison.RightRoot;
        var task = _decompiled.GetOrAdd((root, entry.Path), _ => DecompileCoreAsync(setup, comparison, isLeft, entry));
        try
        {
            return await task.WaitAsync(ct);
        }
        catch (Exception) when (task.IsFaulted)
        {
            // Failures are not cached: the user may fix the setup and select the row again.
            _decompiled.TryRemove((root, entry.Path), out _);
            throw;
        }
    }

    private async Task<string> DecompileCoreAsync(DecompilerSetup setup, EarComparison comparison, bool isLeft, EarDiffEntry entry)
    {
        var root = isLeft ? comparison.LeftRoot : comparison.RightRoot;

        // Include inner classes (Foo$Bar.class, Foo$1.class) from the same folder so nested types decompile too.
        var directory = entry.Path[..(entry.Path.LastIndexOf('/') + 1)];
        var outerName = entry.Name[..^".class".Length];
        var related = comparison.Entries
            .Where(e => (isLeft ? e.Left : e.Right) is not null && IsSameClassOrInner(e.Path, directory, outerName))
            .Select(e => e.Path)
            .ToList();

        // Not cancellable on purpose: the result is cached and reused when the row is selected again.
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in related)
        {
            files[path[(path.LastIndexOf('/') + 1)..]] = await _scanner.ReadEntryAsync(root, path, CancellationToken.None);
        }

        return await _decompiler.DecompileAsync(setup, entry.Name, files, CancellationToken.None);
    }

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
}
