using Devscope.Core.Ear;

namespace Devscope.App.Services;

/// <summary>Resolves the decompiler from the user's settings and the folders shipped next to Devscope.exe.</summary>
public static class DecompilerSettings
{
    public static DecompilerSetup? Locate(string javaPath, string jarPath, out string message)
    {
        var exeFolder = AppContext.BaseDirectory;
        return DecompilerLocator.Locate(javaPath, jarPath, [exeFolder, Path.Combine(exeFolder, "tools")], out message);
    }

    public static DecompilerSetup? Locate(UserSettings settings, out string message)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Locate(settings.JavaPath, settings.DecompilerJarPath, out message);
    }
}
