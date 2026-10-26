using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;
using Devscope.Core.Database;
using Devscope.Core.Ear;
using Microsoft.Extensions.Logging;

namespace Devscope.App.ViewModels;

/// <summary>Columns the database diff list can be sorted by.</summary>
public enum DbDiffSortKey
{
    Type,
    Name,
    Status,
    LeftRows,
    RightRows,
    Data,
}

/// <summary>An entry of the object type filter.</summary>
public sealed record TypeFilterOption(string Label, DbObjectType? Type);

/// <summary>
/// Feature 3: compare two SQL Server databases from saved connections. Lists which objects exist on one side only
/// and how the others differ (columns, keys, indexes, code), shows each object's scripts side by side, and compares
/// table rows on demand: missing, extra and changed rows, with a script to fix them.
/// </summary>
public sealed partial class DbDiffViewModel : ObservableObject, IPage, IDisposable
{
    private readonly IDatabaseProvider _provider;
    private readonly IConnectionStore _store;
    private readonly ConnectionPasswords _passwords;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IUserSettingsStore _settings;
    private readonly ILogger<DbDiffViewModel> _logger;

    private List<DbDiffRow> _allRows = [];
    private Sources? _sources;
    private CancellationTokenSource? _recompareCts;
    private DbDiffEntry? _shownScript;
    private bool _loadingConnections;
    private bool _rebuildingRows;

    public DbDiffViewModel(
        IDatabaseProvider provider,
        IConnectionStore store,
        ConnectionPasswords passwords,
        IDialogService dialogs,
        IShellService shell,
        IUserSettingsStore settings,
        ILogger<DbDiffViewModel> logger)
    {
        _provider = provider;
        _store = store;
        _passwords = passwords;
        _dialogs = dialogs;
        _shell = shell;
        _settings = settings;
        _logger = logger;

        var current = settings.Current;
        _ignoreSystemNames = current.DbIgnoreSystemNames;
        _ignoreWhitespace = current.DbIgnoreWhitespace;
        _ignoreColumnOrder = current.DbIgnoreColumnOrder;
        _ignoreCollation = current.DbIgnoreCollation;
        _typeFilter = TypeFilters[0];
        ScriptDiff = new ScriptDiffViewModel(new DiffZoom(settings));
        _ = LoadConnectionsAsync();
    }

    public string Title => "DB diff";

    public string Glyph => "";

    // ---- Connections -------------------------------------------------------------------------

    public ObservableCollection<DbConnectionProfile> Connections { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    private DbConnectionProfile? _leftConnection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    private DbConnectionProfile? _rightConnection;

    partial void OnLeftConnectionChanged(DbConnectionProfile? value) => RememberConnections();

    partial void OnRightConnectionChanged(DbConnectionProfile? value) => RememberConnections();

    // ---- Options -----------------------------------------------------------------------------

    [ObservableProperty]
    private bool _ignoreSystemNames;

    [ObservableProperty]
    private bool _ignoreWhitespace;

    [ObservableProperty]
    private bool _ignoreColumnOrder;

    [ObservableProperty]
    private bool _ignoreCollation;

    partial void OnIgnoreSystemNamesChanged(bool value) => OptionsChanged();

    partial void OnIgnoreWhitespaceChanged(bool value) => OptionsChanged();

    partial void OnIgnoreColumnOrderChanged(bool value) => OptionsChanged();

    partial void OnIgnoreCollationChanged(bool value) => OptionsChanged();

    private DbCompareOptions Options => new()
    {
        IgnoreSystemNames = IgnoreSystemNames,
        IgnoreWhitespace = IgnoreWhitespace,
        IgnoreColumnOrder = IgnoreColumnOrder,
        IgnoreCollation = IgnoreCollation,
    };

    // ---- Result ------------------------------------------------------------------------------

    [ObservableProperty]
    private string _statusText = "Pick two saved connections and compare. Nothing is written to either database.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(CompareAllDataCommand))]
    private DbComparison? _comparison;

    /// <summary>"server/db ↔ server/db" of the shown comparison.</summary>
    [ObservableProperty]
    private string _comparedDatabases = string.Empty;

    /// <summary>Database-level differences (collation, compatibility level, version), joined.</summary>
    [ObservableProperty]
    private string _databaseDifferences = string.Empty;

    /// <summary>Progress of comparing the data of many tables; empty when idle.</summary>
    [ObservableProperty]
    private string _backgroundStatus = string.Empty;

    [ObservableProperty]
    private ObservableCollection<DbDiffRow> _rows = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareDataCommand))]
    private DbDiffRow? _selectedRow;

    /// <summary>Scripts of <see cref="SelectedRow"/>, side by side.</summary>
    public ScriptDiffViewModel ScriptDiff { get; }

    public bool HasResult => Comparison is not null;

    partial void OnSelectedRowChanged(DbDiffRow? value)
    {
        if (!_rebuildingRows)
        {
            ShowScript(value?.Entry);
        }
    }

    // ---- Filters and sorting -----------------------------------------------------------------

    public static IReadOnlyList<TypeFilterOption> TypeFilters { get; } =
        [new("All types", null), .. Enum.GetValues<DbObjectType>().Select(t => new TypeFilterOption(t + "s", t))];

    [ObservableProperty]
    private TypeFilterOption _typeFilter;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showDifferent = true;

    [ObservableProperty]
    private bool _showOnlyInLeft = true;

    [ObservableProperty]
    private bool _showOnlyInRight = true;

    [ObservableProperty]
    private bool _showIdentical;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeHeader), nameof(NameHeader), nameof(StatusHeader), nameof(LeftRowsHeader), nameof(RightRowsHeader), nameof(DataHeader))]
    private DbDiffSortKey _sortKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeHeader), nameof(NameHeader), nameof(StatusHeader), nameof(LeftRowsHeader), nameof(RightRowsHeader), nameof(DataHeader))]
    private bool _sortDescending;

    public string TypeHeader => Header("Type", DbDiffSortKey.Type);

    public string NameHeader => Header("Object", DbDiffSortKey.Name);

    public string StatusHeader => Header("Status", DbDiffSortKey.Status);

    public string LeftRowsHeader => Header("Rows left", DbDiffSortKey.LeftRows);

    public string RightRowsHeader => Header("Rows right", DbDiffSortKey.RightRows);

    public string DataHeader => Header("Data", DbDiffSortKey.Data);

    partial void OnTypeFilterChanged(TypeFilterOption value) => ApplyRowFilter();

    partial void OnSearchTextChanged(string value) => ApplyRowFilter();

    partial void OnShowDifferentChanged(bool value) => ApplyRowFilter();

    partial void OnShowOnlyInLeftChanged(bool value) => ApplyRowFilter();

    partial void OnShowOnlyInRightChanged(bool value) => ApplyRowFilter();

    partial void OnShowIdenticalChanged(bool value) => ApplyRowFilter();

    [RelayCommand]
    private void SortBy(DbDiffSortKey key)
    {
        if (SortKey == key)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortKey = key;
            SortDescending = false;
        }

        ApplyRowFilter();
    }

    private string Header(string label, DbDiffSortKey key) =>
        SortKey == key ? $"{label} {(SortDescending ? "▼" : "▲")}" : label;

    // ---- Commands: connections ---------------------------------------------------------------

    /// <summary>Opens the connection manager on the left ("left", the default) or right ("right") connection.</summary>
    [RelayCommand]
    private async Task ManageConnectionsAsync(string? side) =>
        await EditConnectionsAsync(side == "right" ? RightConnection?.Id : LeftConnection?.Id, message: null);

    [RelayCommand]
    private void Swap() => (LeftConnection, RightConnection) = (RightConnection, LeftConnection);

    private bool CanCompare() => LeftConnection is not null && RightConnection is not null;

    // ---- Commands: comparing -----------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanCompare), IncludeCancelCommand = true)]
    private async Task CompareAsync(CancellationToken cancellationToken)
    {
        if (!await EnsurePasswordsAsync() || LeftConnection is not { } leftProfile || RightConnection is not { } rightProfile)
        {
            return;
        }

        StopDataWork();
        _sources = null;
        Comparison = null;
        _allRows = [];
        Rows = [];
        ComparedDatabases = string.Empty;
        DatabaseDifferences = string.Empty;
        BackgroundStatus = string.Empty;
        ShowScript(null);
        StatusText = $"Reading the schemas of {leftProfile.Name} and {rightProfile.Name}…";

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var left = _provider.Open(leftProfile, _passwords.Get(leftProfile));
            var right = _provider.Open(rightProfile, _passwords.Get(rightProfile));
            var schemas = await Task.WhenAll(left.ReadSchemaAsync(cancellationToken), right.ReadSchemaAsync(cancellationToken));
            var options = Options;
            var comparison = await Task.Run(() => SchemaComparer.Compare(schemas[0], schemas[1], options), cancellationToken);

            _sources = new Sources(left, right);
            ShowComparison(comparison, keepData: false);
            StatusText = Summarize(comparison, stopwatch.Elapsed);
        }
        catch (Exception ex) when (IsCancellation(ex, cancellationToken))
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            LogCompareFailed(ex);
            StatusText = "Comparison failed.";
            _dialogs.ShowError(ex.Message, "Could not compare");
        }
    }

    private static string Summarize(DbComparison result, TimeSpan elapsed) =>
        result.Different + result.OnlyInLeft + result.OnlyInRight == 0
            ? string.Create(CultureInfo.CurrentCulture, $"Identical schemas ({result.Identical:N0} objects) · {elapsed.TotalSeconds:N1}s")
            : string.Create(CultureInfo.CurrentCulture, $"Compared {result.Entries.Count:N0} objects · {elapsed.TotalSeconds:N1}s");

    private void OptionsChanged()
    {
        _settings.Current.DbIgnoreSystemNames = IgnoreSystemNames;
        _settings.Current.DbIgnoreWhitespace = IgnoreWhitespace;
        _settings.Current.DbIgnoreColumnOrder = IgnoreColumnOrder;
        _settings.Current.DbIgnoreCollation = IgnoreCollation;
        _settings.Save();
        _ = RecompareAsync();
    }

    /// <summary>Applies changed options to the schemas already read: no need to read the databases again.</summary>
    private async Task RecompareAsync()
    {
        if (Comparison is not { } previous)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _recompareCts, cts);
        if (old is not null)
        {
            await old.CancelAsync();
            old.Dispose();
        }

        try
        {
            var options = Options;
            var comparison = await Task.Run(() => SchemaComparer.Compare(previous.Left, previous.Right, options), cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(Comparison?.Left, previous.Left))
            {
                ShowComparison(comparison, keepData: true);
            }
        }
        catch (OperationCanceledException)
        {
            // Another option changed meanwhile.
        }
    }

    /// <summary>Shows a comparison. With <paramref name="keepData"/>, rows keep their data results and settings.</summary>
    private void ShowComparison(DbComparison comparison, bool keepData)
    {
        var previous = keepData ? _allRows.ToDictionary(r => r.Key) : new Dictionary<string, DbDiffRow>();
        _allRows = comparison.Entries.Select(e =>
        {
            if (previous.TryGetValue(DbDiffRow.KeyOf(e), out var row))
            {
                row.Entry = e;
                return row;
            }

            return new DbDiffRow(e);
        }).ToList();

        Comparison = comparison;
        ComparedDatabases = $"{comparison.Left.Info.DisplayName}  ↔  {comparison.Right.Info.DisplayName}";
        DatabaseDifferences = string.Join("  ·  ", comparison.DatabaseDifferences);
        ApplyRowFilter();
    }

    private void ShowScript(DbDiffEntry? entry)
    {
        if (!ReferenceEquals(entry, _shownScript))
        {
            _shownScript = entry;
            _ = ScriptDiff.LoadAsync(entry);
        }
    }

    // ---- Commands: data ----------------------------------------------------------------------

    private bool CanCompareSelectedData() => SelectedRow is { CanCompareData: true };

    /// <summary>Compares the rows of the selected table.</summary>
    [RelayCommand(CanExecute = nameof(CanCompareSelectedData), IncludeCancelCommand = true)]
    private Task CompareDataAsync(CancellationToken cancellationToken) =>
        SelectedRow is { } row ? CompareRowDataAsync(row, cancellationToken) : Task.CompletedTask;

    /// <summary>Compares the rows of every listed table that exists on both sides, one table at a time.</summary>
    [RelayCommand(CanExecute = nameof(HasResult), IncludeCancelCommand = true)]
    private async Task CompareAllDataAsync(CancellationToken cancellationToken)
    {
        var tables = Rows.Where(r => r.CanCompareData).ToList();
        if (tables.Count == 0)
        {
            BackgroundStatus = "No listed table exists on both sides. Show identical objects, or pick the Tables type.";
            return;
        }

        // A new comparison cancels this one; its last word must not land on the new comparison.
        var comparison = Comparison;
        var done = 0;
        try
        {
            foreach (var row in tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BackgroundStatus = string.Create(CultureInfo.CurrentCulture, $"Comparing data… {done + 1:N0} / {tables.Count:N0} tables ({row.FullName})");
                await CompareRowDataAsync(row, cancellationToken);
                done++;
            }

            var differing = tables.Count(r => r.DataState == DataState.Different);
            var failed = tables.Count(r => r.DataState == DataState.Failed);
            BackgroundStatus = string.Create(CultureInfo.CurrentCulture,
                $"Compared the data of {done:N0} tables: {differing:N0} differ{(failed > 0 ? $", {failed:N0} could not be compared" : string.Empty)}.");
        }
        catch (OperationCanceledException) when (ReferenceEquals(comparison, Comparison))
        {
            BackgroundStatus = string.Create(CultureInfo.CurrentCulture, $"Stopped after {done:N0} of {tables.Count:N0} tables.");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a new comparison.
        }
    }

    private async Task CompareRowDataAsync(DbDiffRow row, CancellationToken cancellationToken)
    {
        if (row.IsComparingData || _sources is not { } sources || !row.CanCompareData)
        {
            return;
        }

        RowComparePlan plan;
        try
        {
            plan = RowComparePlanner.Plan(row.Entry.Left!, row.Entry.Right!, SettingsFor(row));
        }
        catch (ArgumentException ex)
        {
            row.DataError = ex.Message;
            return;
        }

        row.IsComparingData = true;
        row.DataError = null;
        row.DataProgress = "Starting…";
        var (leftEstimate, rightEstimate) = (row.LeftRows ?? 0, row.RightRows ?? 0);
        var progress = new Progress<RowCompareProgress>(p => row.DataProgress = string.Create(CultureInfo.CurrentCulture,
            $"Read {p.LeftRows:N0} of ~{leftEstimate:N0} left and {p.RightRows:N0} of ~{rightEstimate:N0} right rows"));
        try
        {
            var result = await Task.Run(() => RowComparer.CompareAsync(sources.Left, sources.Right, plan, progress, cancellationToken), cancellationToken);
            row.SetData(result);
        }
        catch (Exception ex) when (IsCancellation(ex, cancellationToken))
        {
            // Keeps the previous result, if any.
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            LogDataFailed(ex, row.FullName);
            row.DataError = ex.Message;
        }
        finally
        {
            row.IsComparingData = false;
            row.DataProgress = string.Empty;
        }
    }

    private RowCompareSettings SettingsFor(DbDiffRow row) => new()
    {
        KeyColumns = SplitNames(row.KeyColumnsText),
        IgnoredColumns = SplitNames(row.IgnoredColumnsText),
        Filter = row.FilterText,
        MaxSamples = Math.Clamp(_settings.Current.DbSampleRows, 10, 100_000),
        MaxInMemoryRows = Math.Clamp(_settings.Current.DbMaxInMemoryRows, 10_000, 100_000_000),
    };

    /// <summary>"Id, [Order Date]" → Id, Order Date.</summary>
    private static List<string> SplitNames(string text) =>
        [.. text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.StartsWith('[') && n.EndsWith(']') ? n[1..^1].Replace("]]", "]", StringComparison.Ordinal) : n)
            .Where(n => n.Length > 0)];

    private void StopDataWork()
    {
        if (CompareDataCommand.IsRunning)
        {
            CompareDataCancelCommand.Execute(null);
        }

        if (CompareAllDataCommand.IsRunning)
        {
            CompareAllDataCancelCommand.Execute(null);
        }
    }

    /// <summary>Copies INSERT/UPDATE/DELETE statements that make the right ("right") or left ("left") table match the other.</summary>
    [RelayCommand]
    private void CopySyncScript(string? side)
    {
        if (SelectedRow is not { Data: { } data } row)
        {
            return;
        }

        var toRight = side != "left";
        _shell.CopyToClipboard(SyncScripter.Script(data, toRight));
        StatusText = $"Copied a script that makes {row.FullName} on the {(toRight ? "right" : "left")} match the {(toRight ? "left" : "right")}. Review it before running it.";
    }

    // ---- Commands: copy and export -----------------------------------------------------------

    [RelayCommand]
    private void CopyName(DbDiffRow? row)
    {
        if ((row ?? SelectedRow) is { } target)
        {
            _shell.CopyToClipboard(target.FullName);
        }
    }

    /// <summary>Copies the left ("left") or right ("right") script of the selected object.</summary>
    [RelayCommand]
    private void CopyScript(string? side)
    {
        var script = side == "right" ? SelectedRow?.Entry.RightScript : SelectedRow?.Entry.LeftScript;
        if (script is not null)
        {
            _shell.CopyToClipboard(script);
        }
    }

    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task ExportAsync()
    {
        var file = _dialogs.PickSaveFile("Export the listed objects", "CSV (*.csv)|*.csv", "db-diff.csv");
        if (file is null)
        {
            return;
        }

        var sb = new StringBuilder("status,type,schema,name,left_rows,right_rows,differences,data_only_in_left,data_only_in_right,data_changed,data_identical\r\n");
        foreach (var row in Rows)
        {
            var data = row.Data;
            sb.Append(CultureInfo.InvariantCulture,
                $"{DbDiffRow.StatusLabel(row.Status)},{row.Type},{Csv(row.Entry.Schema)},{Csv(row.Name)},{row.LeftRows},{row.RightRows},{Csv(string.Join("; ", row.Differences))},")
                .Append(CultureInfo.InvariantCulture, $"{data?.OnlyInLeft},{data?.OnlyInRight},{data?.Different},{data?.Identical}\r\n");
        }

        try
        {
            await File.WriteAllTextAsync(file, sb.ToString());
            StatusText = $"Exported to {file}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(ex.Message, "Could not export");
        }
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // ---- List --------------------------------------------------------------------------------

    private void ApplyRowFilter()
    {
        if (Comparison is null)
        {
            return;
        }

        var search = SearchText.Trim();
        var type = TypeFilter.Type;
        var rows = _allRows
            .Where(r => ShowsStatus(r.Status)
                && (type is null || r.Type == type)
                && (search.Length == 0 || r.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        rows.Sort(CompareRows);

        var selected = SelectedRow is { } current && rows.Contains(current) ? current : null;
        _rebuildingRows = true;
        try
        {
            Rows = new ObservableCollection<DbDiffRow>(rows);
            SelectedRow = selected;
        }
        finally
        {
            _rebuildingRows = false;
        }

        ShowScript(selected?.Entry);
    }

    private bool ShowsStatus(DiffStatus status) => status switch
    {
        DiffStatus.Modified => ShowDifferent,
        DiffStatus.Removed => ShowOnlyInLeft,
        DiffStatus.Added => ShowOnlyInRight,
        _ => ShowIdentical,
    };

    private int CompareRows(DbDiffRow a, DbDiffRow b)
    {
        var primary = SortKey switch
        {
            DbDiffSortKey.Name => StringComparer.OrdinalIgnoreCase.Compare(a.FullName, b.FullName),
            DbDiffSortKey.Status => StatusRank(a.Status).CompareTo(StatusRank(b.Status)),
            DbDiffSortKey.LeftRows => Nullable.Compare(a.LeftRows, b.LeftRows),
            DbDiffSortKey.RightRows => Nullable.Compare(a.RightRows, b.RightRows),
            DbDiffSortKey.Data => Nullable.Compare(a.Data?.DifferenceCount, b.Data?.DifferenceCount),
            _ => a.Type.CompareTo(b.Type),
        };
        if (SortDescending)
        {
            primary = -primary;
        }

        return primary != 0 ? primary : StringComparer.OrdinalIgnoreCase.Compare(a.FullName, b.FullName);
    }

    // Differences first, in the order of the status chips.
    private static int StatusRank(DiffStatus status) => status switch
    {
        DiffStatus.Modified => 0,
        DiffStatus.Removed => 1,
        DiffStatus.Added => 2,
        _ => 3,
    };

    // ---- Connections -------------------------------------------------------------------------

    private async Task LoadConnectionsAsync()
    {
        try
        {
            var connections = await _store.LoadAsync(CancellationToken.None);
            SetConnections(connections, _settings.Current.LeftConnectionId, _settings.Current.RightConnectionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStoreError(ex);
        }
    }

    private void SetConnections(IReadOnlyList<DbConnectionProfile> connections, Guid? left, Guid? right)
    {
        _loadingConnections = true;
        try
        {
            Connections.Clear();
            foreach (var connection in connections)
            {
                Connections.Add(connection);
            }

            LeftConnection = Connections.FirstOrDefault(c => c.Id == left);
            RightConnection = Connections.FirstOrDefault(c => c.Id == right);
        }
        finally
        {
            _loadingConnections = false;
        }
    }

    private void RememberConnections()
    {
        if (_loadingConnections)
        {
            return;
        }

        _settings.Current.LeftConnectionId = LeftConnection?.Id;
        _settings.Current.RightConnectionId = RightConnection?.Id;
        _settings.Save();
    }

    /// <summary>Opens the connection manager; returns whether it was saved.</summary>
    private async Task<bool> EditConnectionsAsync(Guid? selectedId, string? message)
    {
        var edited = _dialogs.EditConnections([.. Connections], selectedId, message);
        if (edited is null)
        {
            return false;
        }

        // New connections fill an empty side: the first goes left, the next right.
        var left = LeftConnection?.Id ?? (edited.Count > 0 ? edited[0].Id : null);
        var right = RightConnection?.Id ?? edited.FirstOrDefault(c => c.Id != left)?.Id ?? left;
        SetConnections(edited, left, right);
        RememberConnections();
        try
        {
            await _store.SaveAsync(edited, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStoreError(ex);
            _dialogs.ShowError(ex.Message, "Could not save connections");
        }

        return true;
    }

    /// <summary>Asks for the password of a SQL login that has none saved; false when the user gives none.</summary>
    private async Task<bool> EnsurePasswordsAsync()
    {
        foreach (var profile in new[] { LeftConnection, RightConnection }.OfType<DbConnectionProfile>().Distinct().ToList())
        {
            if (!_passwords.IsMissing(profile))
            {
                continue;
            }

            await EditConnectionsAsync(profile.Id, $"Enter the password of {profile.UserName} for '{profile.Name}'.");
            if (Connections.FirstOrDefault(c => c.Id == profile.Id) is not { } current || _passwords.IsMissing(current))
            {
                StatusText = $"No password for '{profile.Name}'.";
                return false;
            }
        }

        return true;
    }

    /// <summary>A cancelled SQL command can surface as a SqlException rather than an OperationCanceledException.</summary>
    private static bool IsCancellation(Exception ex, CancellationToken cancellationToken) =>
        ex is OperationCanceledException || (cancellationToken.IsCancellationRequested && ex is DbException or InvalidOperationException);

    public void Dispose()
    {
        StopDataWork();
        _recompareCts?.Cancel();
        _recompareCts?.Dispose();
        ScriptDiff.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database comparison failed")]
    private partial void LogCompareFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Data comparison of {Table} failed")]
    private partial void LogDataFailed(Exception exception, string table);

    [LoggerMessage(Level = LogLevel.Error, Message = "Connection store failed")]
    private partial void LogStoreError(Exception exception);

    /// <summary>The two databases of the shown comparison, opened with the passwords of that moment.</summary>
    private sealed record Sources(IDatabaseSource Left, IDatabaseSource Right);
}
