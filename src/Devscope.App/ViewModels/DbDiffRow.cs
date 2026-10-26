using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Devscope.Core.Database;
using Devscope.Core.Ear;

namespace Devscope.App.ViewModels;

/// <summary>
/// One object of the database diff list, plus the data comparison of a table. Kept across option changes (only its
/// <see cref="Entry"/> is replaced), so data results survive toggling an option.
/// </summary>
public sealed partial class DbDiffRow : ObservableObject
{
    public DbDiffRow(DbDiffEntry entry) => _entry = entry;

    /// <summary>Identifies the object across comparisons with other options: type plus schema-qualified name.</summary>
    public string Key => KeyOf(Entry);

    [ObservableProperty]
    private DbDiffEntry _entry;

    public DiffStatus Status => Entry.Status;

    public string StatusText => StatusLabel(Entry.Status);

    public DbObjectType Type => Entry.Type;

    public string Name => Entry.Name;

    public string FullName => Entry.FullName;

    public IReadOnlyList<string> Differences => Entry.Differences;

    /// <summary>One line under the name: what differs, or where the object lives.</summary>
    public string Summary => Entry.Status switch
    {
        DiffStatus.Removed => "Only in the left database",
        DiffStatus.Added => "Only in the right database",
        _ when Entry.Differences.Count == 1 => Entry.Differences[0],
        _ when Entry.Differences.Count > 1 => string.Create(CultureInfo.CurrentCulture, $"{Entry.Differences.Count} differences: {Entry.Differences[0]}, …"),
        _ => Entry.FullName,
    };

    /// <summary>The Differences tab when there is no list to show.</summary>
    public string Detail => Entry.Status switch
    {
        DiffStatus.Removed => $"{Entry.FullName} exists only in the left database.",
        DiffStatus.Added => $"{Entry.FullName} exists only in the right database.",
        DiffStatus.Unchanged when Entry.Differences.Count == 0 => "Identical in both databases.",
        _ => string.Empty,
    };

    public long? LeftRows => Entry.Left?.RowCount;

    public long? RightRows => Entry.Right?.RowCount;

    /// <summary>Row counts for the detail header, e.g. "Rows (from metadata, approximate): 1,000 left · 997 right".</summary>
    public string RowCountsText => Entry.Type != DbObjectType.Table ? string.Empty
        : string.Create(CultureInfo.CurrentCulture, $"Rows (from metadata, approximate): {Count(LeftRows)} left · {Count(RightRows)} right");

    /// <summary>Whether the metadata row counts differ: a quick hint that rows are missing on one side.</summary>
    public bool RowCountsDiffer => LeftRows is not null && RightRows is not null && LeftRows != RightRows;

    /// <summary>Rows can be compared for tables present on both sides.</summary>
    public bool CanCompareData => Entry is { Type: DbObjectType.Table, Left: not null, Right: not null };

    // ---- Data comparison ---------------------------------------------------------------------

    /// <summary>Key columns, comma-separated; empty means the primary key or a unique index.</summary>
    [ObservableProperty]
    private string _keyColumnsText = string.Empty;

    /// <summary>Columns to leave out, comma-separated.</summary>
    [ObservableProperty]
    private string _ignoredColumnsText = string.Empty;

    /// <summary>WHERE condition used on both sides.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataState), nameof(HasData), nameof(DataTooltip), nameof(DataText), nameof(DataSummary))]
    private RowComparison? _data;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataState), nameof(DataTooltip), nameof(DataText))]
    private bool _isComparingData;

    [ObservableProperty]
    private string _dataProgress = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataState), nameof(DataTooltip), nameof(DataText))]
    private string? _dataError;

    [ObservableProperty]
    private IReadOnlyList<RowDifferenceItem> _dataSamples = [];

    [ObservableProperty]
    private IReadOnlyList<string> _dataColumns = [];

    public bool HasData => Data is not null;

    /// <summary>What the Data column shows.</summary>
    public DataState DataState => IsComparingData ? DataState.Comparing
        : DataError is not null ? DataState.Failed
        : Data is null ? DataState.None
        : Data.HasDifferences ? DataState.Different
        : DataState.Same;

    /// <summary>The list's Data column: −rows only in left, +rows only in right, ~changed rows.</summary>
    public string DataText => DataState switch
    {
        DataState.Comparing => "Comparing…",
        DataState.Failed => "⚠ Failed",
        DataState.Same => "✓ Same",
        DataState.Different => string.Join("  ", new[]
        {
            Data!.OnlyInLeft > 0 ? string.Create(CultureInfo.CurrentCulture, $"−{Data.OnlyInLeft:N0}") : null,
            Data.OnlyInRight > 0 ? string.Create(CultureInfo.CurrentCulture, $"+{Data.OnlyInRight:N0}") : null,
            Data.Different > 0 ? string.Create(CultureInfo.CurrentCulture, $"~{Data.Different:N0}") : null,
        }.OfType<string>()),
        _ => string.Empty,
    };

    /// <summary>How the rows were compared, e.g. "1,000 left · 998 right rows · primary key [PK_Orders] · merged in key order · 0.4s".</summary>
    public string DataSummary => Data is not { } data ? string.Empty : string.Create(CultureInfo.CurrentCulture,
        $"{data.LeftRows:N0} left · {data.RightRows:N0} right rows · key: {data.Plan.KeyDescription} · {ModeText(data.Mode)} · {data.Elapsed.TotalSeconds:N1}s");

    public string DataTooltip => DataState switch
    {
        DataState.Comparing => "Comparing rows…",
        DataState.Failed => $"Data comparison failed: {DataError}",
        DataState.None => CanCompareData ? "Rows not compared yet" : "Rows are compared for tables that exist on both sides",
        _ => string.Create(CultureInfo.CurrentCulture,
            $"{Data!.OnlyInLeft:N0} only in left (missing on the right), {Data.OnlyInRight:N0} only in right, {Data.Different:N0} changed, {Data.Identical:N0} identical"),
    };

    /// <summary>Shows a finished data comparison.</summary>
    public void SetData(RowComparison result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var columns = result.Plan.Columns;
        DataError = null;
        DataColumns = [.. columns.Select((c, i) => result.DifferentByColumn[i] > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{c.Name}: {result.DifferentByColumn[i]:N0} {(result.DifferentByColumn[i] == 1 ? "row" : "rows")}")
            : null).OfType<string>()];
        DataSamples = [.. result.Samples.Select(s => new RowDifferenceItem(s, result.Plan))];
        Data = result;
    }

    public void ClearData()
    {
        Data = null;
        DataError = null;
        DataSamples = [];
        DataColumns = [];
        DataProgress = string.Empty;
    }

    // Nearly everything shown derives from the entry.
    partial void OnEntryChanged(DbDiffEntry value) => OnPropertyChanged(string.Empty);

    /// <summary>Status in the words of a database comparison: removed means only in the left database.</summary>
    public static string StatusLabel(DiffStatus status) => status switch
    {
        DiffStatus.Modified => "Different",
        DiffStatus.Removed => "Only in left",
        DiffStatus.Added => "Only in right",
        _ => "Identical",
    };

    private static string ModeText(RowCompareMode mode) => mode switch
    {
        RowCompareMode.Merge => "streamed in key order",
        RowCompareMode.KeyedInMemory => "matched by key in memory",
        _ => "whole rows matched",
    };

    private static string Count(long? rows) => rows is { } n ? n.ToString("N0", CultureInfo.CurrentCulture) : "—";

    public static string KeyOf(DbDiffEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"{entry.Type}|{entry.Schema}|{entry.Name}".ToUpperInvariant();
    }
}

/// <summary>State of a table's data comparison, for the list's Data column.</summary>
public enum DataState
{
    None,
    Comparing,
    Failed,
    Same,
    Different,
}

/// <summary>One differing row, for display: its key and what differs.</summary>
public sealed class RowDifferenceItem
{
    private const int MaxValueLength = 80;

    public RowDifferenceItem(RowDifference difference, RowComparePlan plan)
    {
        ArgumentNullException.ThrowIfNull(difference);
        ArgumentNullException.ThrowIfNull(plan);

        Difference = difference;
        var row = (difference.Left ?? difference.Right)!;
        var columns = plan.Columns;
        var keys = plan.KeyColumns;
        (Status, Label) = difference.Kind switch
        {
            RowDiffKind.OnlyInLeft => (DiffStatus.Removed, "Only in left"),
            RowDiffKind.OnlyInRight => (DiffStatus.Added, "Only in right"),
            _ => (DiffStatus.Modified, "Changed"),
        };

        Key = keys.Count > 0
            ? string.Join(", ", keys.Select(i => $"{columns[i].Name} = {DbValues.Format(row[i], MaxValueLength)}"))
            : "(no key)";
        Details = difference.Kind == RowDiffKind.Different
            ? string.Join("   ", difference.ChangedColumns.Select(i =>
                $"{columns[i].Name}: {DbValues.Format(difference.Left![i], MaxValueLength)} → {DbValues.Format(difference.Right![i], MaxValueLength)}"))
            : string.Join(", ", Enumerable.Range(0, columns.Count).Where(i => !keys.Contains(i))
                .Select(i => $"{columns[i].Name} = {DbValues.Format(row[i], MaxValueLength)}"));
    }

    public RowDifference Difference { get; }

    /// <summary>Colour of the badge: removed for only in left, added for only in right, modified for changed.</summary>
    public DiffStatus Status { get; }

    public string Label { get; }

    public string Key { get; }

    public string Details { get; }
}
