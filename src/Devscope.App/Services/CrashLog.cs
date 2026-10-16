using System.Diagnostics;

namespace Devscope.App.Services;

/// <summary>Last-resort crash log, usable before (or after) the host and its loggers exist.</summary>
internal static class CrashLog
{
    public static string FilePath { get; } = Path.Combine(App.LocalDataFolder, "crash.log");

    public static void Write(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(App.LocalDataFolder);
            File.AppendAllText(FilePath, $"{Environment.NewLine}[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not write crash log: {ex.Message}");
        }
    }
}
