using System.Diagnostics;
using System.Windows;

namespace Devscope.App.Services;

/// <summary>Interactions with the Windows shell, kept behind an interface so view models stay testable.</summary>
public interface IShellService
{
    void CopyToClipboard(string text);

    /// <summary>Opens Explorer with the file selected.</summary>
    void RevealInExplorer(string path);
}

public sealed class ShellService : IShellService
{
    public void CopyToClipboard(string text) => Clipboard.SetText(text);

    public void RevealInExplorer(string path)
    {
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        startInfo.ArgumentList.Add($"/select,{path}");
        Process.Start(startInfo)?.Dispose();
    }
}
