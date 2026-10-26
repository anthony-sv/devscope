using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Devscope.App.Services;

/// <summary>Per-user preferences: last paths, workspace and connections, plus everything edited in the Settings window.</summary>
public sealed class UserSettings
{
    public string LeftEarPath { get; set; } = string.Empty;

    public string RightEarPath { get; set; } = string.Empty;

    public Guid? LastWorkspaceId { get; set; }

    public int InitialTailMegabytes { get; set; } = 4;

    public int MaxFilesPerSource { get; set; } = 50;

    /// <summary>java.exe used for decompiling; empty means JAVA_HOME / PATH.</summary>
    public string JavaPath { get; set; } = string.Empty;

    /// <summary>Vineflower jar; empty means next to Devscope.exe or in its tools folder.</summary>
    public string DecompilerJarPath { get; set; } = string.Empty;

    /// <summary>Decompile every changed class in the background after a comparison.</summary>
    public bool PrefetchDecompile { get; set; } = true;

    /// <summary>Java processes used for background decompiling.</summary>
    public int MaxDecompilerProcesses { get; set; } = 2;

    /// <summary>Keep decompiled sources on disk across comparisons and restarts.</summary>
    public bool DiskCacheEnabled { get; set; } = true;

    public int DiskCacheMegabytes { get; set; } = 500;

    /// <summary>Memory budget for unpacked nested WARs/JARs.</summary>
    public int ArchiveCacheMegabytes { get; set; } = 256;

    /// <summary>Rescan the compared EARs when they change on disk.</summary>
    public bool AutoRefresh { get; set; }

    /// <summary>Zoom of the diff text (1 = 100%), changed with Ctrl+mouse wheel.</summary>
    public double DiffZoom { get; set; } = 1;

    public Guid? LeftConnectionId { get; set; }

    public Guid? RightConnectionId { get; set; }

    public bool DbIgnoreSystemNames { get; set; } = true;

    public bool DbIgnoreWhitespace { get; set; } = true;

    public bool DbIgnoreColumnOrder { get; set; }

    public bool DbIgnoreCollation { get; set; }

    /// <summary>Differing rows kept as examples per kind (only in left, only in right, changed) and table.</summary>
    public int DbSampleRows { get; set; } = 1000;

    /// <summary>Rows a data comparison may hold in memory when a table has no key both servers sort the same way.</summary>
    public int DbMaxInMemoryRows { get; set; } = 2_000_000;
}

public interface IUserSettingsStore
{
    UserSettings Current { get; }

    /// <summary>Full path of the settings file.</summary>
    string FilePath { get; }

    /// <summary>Raised after <see cref="Update"/> so features can apply new settings immediately.</summary>
    event EventHandler? Changed;

    /// <summary>Persists incidental state (last paths) without notifying anyone.</summary>
    void Save();

    /// <summary>Applies <paramref name="change"/>, saves, and raises <see cref="Changed"/>.</summary>
    void Update(Action<UserSettings> change);
}

/// <summary>Persists <see cref="UserSettings"/> to <c>%APPDATA%\Devscope\settings.json</c>.</summary>
public sealed partial class UserSettingsStore : IUserSettingsStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Devscope", "settings.json");

    private readonly ILogger<UserSettingsStore> _logger;

    public UserSettingsStore(ILogger<UserSettingsStore> logger)
    {
        _logger = logger;
        Current = Load();
    }

    public UserSettings Current { get; }

    public string FilePath => _path;

    public event EventHandler? Changed;

    public void Update(Action<UserSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change(Current);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.UserSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(ex);
        }
    }

    private UserSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(_path), SettingsJsonContext.Default.UserSettings) ?? new UserSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogLoadFailed(ex);
        }

        return new UserSettings();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not load user settings")]
    private partial void LogLoadFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save user settings")]
    private partial void LogSaveFailed(Exception exception);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UserSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
