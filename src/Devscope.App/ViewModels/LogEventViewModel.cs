using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;
using Devscope.Core.Logs;

namespace Devscope.App.ViewModels;

/// <summary>
/// One event opened in its own window (double-click or Enter in the log list), with every line of its stack trace.
/// The list keeps it up to date when more lines of the event arrive.
/// </summary>
public sealed partial class LogEventViewModel(LogEntry entry, IShellService shell) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Header))]
    private LogEntry _entry = entry;

    [ObservableProperty]
    private bool _wrapLines;

    public string Title => $"{Entry.Source.Environment} · {Entry.Source.FileName} – Devscope";

    public string Header => Entry.LineCount > 1 ? $"{Entry.LineCount:N0} lines" : "1 line";

    [RelayCommand]
    private void Copy() => shell.CopyToClipboard(Entry.Text);

    [RelayCommand]
    private void Reveal() => shell.RevealInExplorer(Entry.Source.FilePath);
}
