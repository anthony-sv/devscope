using CommunityToolkit.Mvvm.ComponentModel;
using Devscope.Core.Ear;

namespace Devscope.App.ViewModels;

/// <summary>
/// One row of the EAR diff list. Compares by reference: compare <see cref="Entry"/> (a record, compared by value)
/// to tell whether two rows show the same file and bytes.
/// </summary>
public sealed partial class EarDiffRow(EarDiffEntry entry, bool isSameText) : ObservableObject
{
    public EarDiffEntry Entry { get; } = entry;

    public string Path => Entry.Path;

    public string Name => Entry.Name;

    public DiffStatus Status => Entry.Status;

    public ArchiveEntryInfo? Left => Entry.Left;

    public ArchiveEntryInfo? Right => Entry.Right;

    /// <summary>Modified, but the text reads the same: only line endings, a byte order mark or compiler output differ.</summary>
    [ObservableProperty]
    private bool _isSameText = isSameText;
}
