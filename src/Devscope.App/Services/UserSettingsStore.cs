using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Devscope.App.Services;

/// <summary>Small per-user UI preferences (last paths, last workspace, read limits).</summary>
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
}

public interface IUserSettingsStore
{
    UserSettings Current { get; }

    void Save();
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
