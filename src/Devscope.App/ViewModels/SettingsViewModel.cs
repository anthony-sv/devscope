using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;
using Devscope.Core.Ear;

namespace Devscope.App.ViewModels;

/// <summary>Edits a copy of <see cref="UserSettings"/>; nothing is applied until Save.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IUserSettingsStore _store;
    private readonly DecompiledSourceCache _cache;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;

    public SettingsViewModel(IUserSettingsStore store, DecompiledSourceCache cache, IDialogService dialogs, IShellService shell)
    {
        _store = store;
        _cache = cache;
        _dialogs = dialogs;
        _shell = shell;

        var current = store.Current;
        _javaPath = current.JavaPath;
        _decompilerJarPath = current.DecompilerJarPath;
        _prefetchDecompile = current.PrefetchDecompile;
        _maxDecompilerProcesses = current.MaxDecompilerProcesses;
        _diskCacheEnabled = current.DiskCacheEnabled;
        _diskCacheMegabytes = current.DiskCacheMegabytes;
        _archiveCacheMegabytes = current.ArchiveCacheMegabytes;
        _initialTailMegabytes = current.InitialTailMegabytes;
        _maxFilesPerSource = current.MaxFilesPerSource;
        _dbSampleRows = current.DbSampleRows;
        _dbMaxInMemoryRows = current.DbMaxInMemoryRows;
        CheckDecompiler();
        _ = RefreshCacheUsageAsync();
    }

    public string SettingsFilePath => _store.FilePath;

    public string CacheFolder => _cache.Folder;

    /// <summary>Raised when the dialog should close; true means saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty]
    private string _javaPath;

    [ObservableProperty]
    private string _decompilerJarPath;

    [ObservableProperty]
    private string _decompilerStatus = string.Empty;

    [ObservableProperty]
    private bool _prefetchDecompile;

    [ObservableProperty]
    private int _maxDecompilerProcesses;

    [ObservableProperty]
    private bool _diskCacheEnabled;

    [ObservableProperty]
    private int _diskCacheMegabytes;

    [ObservableProperty]
    private int _archiveCacheMegabytes;

    [ObservableProperty]
    private int _initialTailMegabytes;

    [ObservableProperty]
    private int _maxFilesPerSource;

    [ObservableProperty]
    private int _dbSampleRows;

    [ObservableProperty]
    private int _dbMaxInMemoryRows;

    [ObservableProperty]
    private string _cacheUsage = "Measuring…";

    partial void OnJavaPathChanged(string value) => CheckDecompiler();

    partial void OnDecompilerJarPathChanged(string value) => CheckDecompiler();

    private void CheckDecompiler()
    {
        DecompilerSettings.Locate(JavaPath.Trim(), DecompilerJarPath.Trim(), out var message);
        DecompilerStatus = message;
    }

    [RelayCommand]
    private void BrowseJava() =>
        JavaPath = _dialogs.PickFile("java.exe (Java 17 or newer)", "java.exe|java.exe|Programs (*.exe)|*.exe") ?? JavaPath;

    [RelayCommand]
    private void BrowseDecompilerJar() =>
        DecompilerJarPath = _dialogs.PickFile("Vineflower jar", "Vineflower (vineflower*.jar)|vineflower*.jar|Jar files (*.jar)|*.jar") ?? DecompilerJarPath;

    [RelayCommand]
    private async Task ClearDiskCacheAsync()
    {
        CacheUsage = "Clearing…";
        await Task.Run(_cache.Clear);
        await RefreshCacheUsageAsync();
    }

    [RelayCommand]
    private void RevealSettingsFile() => _shell.RevealInExplorer(_store.FilePath);

    [RelayCommand]
    private void Save()
    {
        _store.Update(s =>
        {
            s.JavaPath = JavaPath.Trim();
            s.DecompilerJarPath = DecompilerJarPath.Trim();
            s.PrefetchDecompile = PrefetchDecompile;
            s.MaxDecompilerProcesses = Math.Clamp(MaxDecompilerProcesses, 1, 8);
            s.DiskCacheEnabled = DiskCacheEnabled;
            s.DiskCacheMegabytes = Math.Clamp(DiskCacheMegabytes, 10, 100_000);
            s.ArchiveCacheMegabytes = Math.Clamp(ArchiveCacheMegabytes, 0, 16_384);
            s.InitialTailMegabytes = Math.Clamp(InitialTailMegabytes, 1, 1024);
            s.MaxFilesPerSource = Math.Clamp(MaxFilesPerSource, 1, 10_000);
            s.DbSampleRows = Math.Clamp(DbSampleRows, 10, 100_000);
            s.DbMaxInMemoryRows = Math.Clamp(DbMaxInMemoryRows, 10_000, 100_000_000);
        });
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private async Task RefreshCacheUsageAsync()
    {
        var bytes = await Task.Run(_cache.GetSizeBytes);
        CacheUsage = $"{bytes / (1024.0 * 1024.0):N1} MB used";
    }
}
