using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devscope.App.Services;
using Devscope.Core.Logs;
using Devscope.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace Devscope.App.ViewModels;

/// <summary>
/// Feature 2: open a workspace (an app's logs across environments and cluster nodes) or an ad-hoc path,
/// then tail, filter and colour the merged stream.
/// </summary>
public sealed partial class LogsViewModel : ObservableObject, IPage, IDisposable
{
    /// <summary>Oldest entries are dropped beyond this many, to keep memory bounded on chatty logs.</summary>
    public const int MaxEntries = 750_000;

    private static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(200);

    private readonly ILogSessionFactory _sessionFactory;
    private readonly IWorkspaceStore _store;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IUserSettingsStore _settings;
    private readonly ILogger<LogsViewModel> _logger;

    private readonly List<LogEntry> _allEntries = [];
    private readonly List<LogBatch> _pausedBatches = [];
    private readonly Dictionary<LogSeverity, FilterChipViewModel<LogSeverity>> _severityChips;
    private Func<LogEntry, bool> _predicate = static _ => true;
    private LogSession? _session;
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _filterCts;
    private IReadOnlyList<HighlightRule> _workspaceHighlights = [];
    private int _generation;

    public LogsViewModel(
        ILogSessionFactory sessionFactory,
        IWorkspaceStore store,
        IDialogService dialogs,
        IShellService shell,
        IUserSettingsStore settings,
        ILogger<LogsViewModel> logger)
    {
        _sessionFactory = sessionFactory;
        _store = store;
        _dialogs = dialogs;
        _shell = shell;
        _settings = settings;
        _logger = logger;

        _severityChips = new[]
        {
            (LogSeverity.Fatal, "Fatal"), (LogSeverity.Error, "Error"), (LogSeverity.Warning, "Warn"),
            (LogSeverity.Info, "Info"), (LogSeverity.Debug, "Debug"), (LogSeverity.Trace, "Trace"), (LogSeverity.Unknown, "Other"),
        }.ToDictionary(x => x.Item1, x => new FilterChipViewModel<LogSeverity>(x.Item1, x.Item2, (int)x.Item1, ScheduleRefilter));
        SeverityChips = [.. _severityChips.Values];

        _ = LoadWorkspacesAsync();
    }

    public string Title => "Logs";

    public string Glyph => "";

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

    public IReadOnlyList<FilterChipViewModel<LogSeverity>> SeverityChips { get; }

    public ObservableCollection<FilterChipViewModel<string>> EnvironmentChips { get; } = [];

    public ObservableCollection<SourceStatus> SourceStatuses { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditWorkspaceCommand), nameof(DeleteWorkspaceCommand), nameof(OpenWorkspaceCommand))]
    private WorkspaceItemViewModel? _selectedWorkspace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuickOpenCommand))]
    private string _quickPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionOpen))]
    private string? _sessionTitle;

    [ObservableProperty]
    private ObservableCollection<LogEntry> _visibleEntries = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyEntryCommand), nameof(RevealEntryCommand))]
    private LogEntry? _selectedEntry;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _excludeText = string.Empty;

    [ObservableProperty]
    private bool _useRegex;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private string? _filterError;

    [ObservableProperty]
    private bool _isFollowing = true;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _countText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<HighlightSpec> _highlights = [];

    public bool IsSessionOpen => SessionTitle is not null;

    partial void OnSearchTextChanged(string value) => ScheduleRefilter();

    partial void OnExcludeTextChanged(string value) => ScheduleRefilter();

    partial void OnUseRegexChanged(bool value) => ScheduleRefilter();

    partial void OnMatchCaseChanged(bool value) => ScheduleRefilter();

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            foreach (var batch in _pausedBatches)
            {
                Apply(batch);
            }

            _pausedBatches.Clear();
        }
    }

    // ---- Workspaces -------------------------------------------------------------------------

    [RelayCommand]
    private async Task NewWorkspaceAsync()
    {
        var created = _dialogs.EditWorkspace(new Workspace
        {
            Name = "My app",
            Environments = [new WorkspaceEnvironment { Name = "local", Sources = [new WorkspaceSource()] }],
        });
        if (created is null)
        {
            return;
        }

        var item = new WorkspaceItemViewModel(created);
        Workspaces.Add(item);
        SelectedWorkspace = item;
        await SaveWorkspacesAsync();
    }

    private bool HasWorkspace() => SelectedWorkspace is not null;

    [RelayCommand(CanExecute = nameof(HasWorkspace))]
    private async Task EditWorkspaceAsync()
    {
        if (SelectedWorkspace is not { } item || _dialogs.EditWorkspace(item.Model) is not { } edited)
        {
            return;
        }

        item.Update(edited);
        await SaveWorkspacesAsync();
    }

    [RelayCommand(CanExecute = nameof(HasWorkspace))]
    private async Task DeleteWorkspaceAsync()
    {
        if (SelectedWorkspace is not { } item || !_dialogs.Confirm($"Delete workspace '{item.Name}'? Log files are not touched.", "Delete workspace"))
        {
            return;
        }

        Workspaces.Remove(item);
        SelectedWorkspace = Workspaces.FirstOrDefault();
        await SaveWorkspacesAsync();
    }

    [RelayCommand(CanExecute = nameof(HasWorkspace))]
    private async Task OpenWorkspaceAsync()
    {
        if (SelectedWorkspace is not { } item)
        {
            return;
        }

        var environments = item.Environments
            .Where(e => e.IsSelected)
            .Select(e => new EnvironmentSelection(e.Name, e.Model.Sources, e.ColorIndex))
            .ToList();
        if (environments.Count == 0)
        {
            _dialogs.ShowError("Tick at least one environment to open.", "Nothing selected");
            return;
        }

        _settings.Current.LastWorkspaceId = item.Model.Id;
        _settings.Save();
        await OpenSessionAsync(item.Name, environments, item.Model.Highlights);
    }

    // ---- Ad-hoc ------------------------------------------------------------------------------

    [RelayCommand]
    private void BrowseQuickFile() => QuickPath = _dialogs.PickFile("Open log file", "Log files (*.log;*.txt;*.out)|*.log;*.txt;*.out|All files (*.*)|*.*") ?? QuickPath;

    [RelayCommand]
    private void BrowseQuickFolder() => QuickPath = _dialogs.PickFolder("Open log folder") ?? QuickPath;

    private bool CanQuickOpen() => !string.IsNullOrWhiteSpace(QuickPath);

    [RelayCommand(CanExecute = nameof(CanQuickOpen))]
    private Task QuickOpenAsync()
    {
        var path = QuickPath.Trim().Trim('"');
        var source = new WorkspaceSource { Path = path, IncludePatterns = "*", Recursive = true };
        return OpenSessionAsync(path, [new EnvironmentSelection("ad-hoc", [source], 0)], []);
    }

    // ---- Session -----------------------------------------------------------------------------

    [RelayCommand]
    private async Task CloseSessionAsync()
    {
        await StopSessionAsync();
        _allEntries.Clear();
        VisibleEntries = [];
        EnvironmentChips.Clear();
        SourceStatuses.Clear();
        SessionTitle = null;
        UpdateCounts();
    }

    [RelayCommand]
    private void Clear()
    {
        _allEntries.Clear();
        _generation++;
        VisibleEntries = [];
        UpdateCounts();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedEntry))]
    private void CopyEntry() => _shell.CopyToClipboard(SelectedEntry!.Text);

    [RelayCommand(CanExecute = nameof(HasSelectedEntry))]
    private void RevealEntry() => _shell.RevealInExplorer(SelectedEntry!.Source.FilePath);

    private bool HasSelectedEntry() => SelectedEntry is not null;

    private async Task OpenSessionAsync(string title, IReadOnlyList<EnvironmentSelection> environments, IReadOnlyList<HighlightRule> highlights)
    {
        await CloseSessionAsync();

        SessionTitle = title;
        _workspaceHighlights = highlights;
        foreach (var environment in environments)
        {
            EnvironmentChips.Add(new FilterChipViewModel<string>(environment.Name, environment.Name, environment.ColorIndex, ScheduleRefilter));
        }

        var options = new LogSessionOptions
        {
            InitialTailBytes = Math.Max(1, _settings.Current.InitialTailMegabytes) * 1024L * 1024L,
            MaxFilesPerSource = Math.Max(1, _settings.Current.MaxFilesPerSource),
        };

        IsLoading = true;
        BuildFilter();
        _session = _sessionFactory.Create(environments, options);
        _sessionCts = new CancellationTokenSource();
        _session.Start();

        // Not awaited: the session lives until closed, and the open commands must stay usable.
        _ = ConsumeAsync(_session, _sessionCts.Token);
    }

    private async Task ConsumeAsync(LogSession session, CancellationToken cancellationToken)
    {
        try
        {
            // Continuations run on the UI thread, so batches are applied without extra marshalling.
            await foreach (var batch in session.Batches.ReadAllAsync(cancellationToken))
            {
                if (IsPaused && !batch.IsInitial)
                {
                    _pausedBatches.Add(batch);
                }
                else
                {
                    Apply(batch);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session closed.
        }
        catch (Exception ex)
        {
            LogSessionError(ex);
            _dialogs.ShowError(ex.Message, "Log reading stopped");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(LogBatch batch)
    {
        if (batch.Statuses is { } statuses)
        {
            SourceStatuses.Clear();
            foreach (var status in statuses)
            {
                SourceStatuses.Add(status);
            }
        }

        if (batch.IsInitial)
        {
            IsLoading = false;
        }

        if (batch.Entries.Count == 0)
        {
            return;
        }

        // Files are polled independently, so a batch can contain events older than what is already shown
        // (e.g. a slow share catching up). Keep the list in time order; that case is rare, so a full sort is fine.
        var outOfOrder = _allEntries.Count > 0 && LogEntry.CompareByTime(batch.Entries[0], _allEntries[^1]) < 0;
        _allEntries.AddRange(batch.Entries);
        if (outOfOrder)
        {
            _allEntries.Sort(LogEntry.CompareByTime);
        }

        if (_allEntries.Count > MaxEntries)
        {
            // Drop the oldest 10% in one go rather than one by one.
            _allEntries.RemoveRange(0, _allEntries.Count - (MaxEntries * 9 / 10));
            ScheduleRefilter(immediate: true);
        }
        else if (batch.IsInitial || outOfOrder)
        {
            ScheduleRefilter(immediate: true);
        }
        else
        {
            foreach (var entry in batch.Entries)
            {
                if (_predicate(entry))
                {
                    VisibleEntries.Add(entry);
                }
            }
        }

        UpdateCounts();
    }

    private async Task StopSessionAsync()
    {
        _pausedBatches.Clear();
        if (_sessionCts is not null)
        {
            await _sessionCts.CancelAsync();
            _sessionCts.Dispose();
            _sessionCts = null;
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }

    // ---- Filtering ---------------------------------------------------------------------------

    private void ScheduleRefilter() => ScheduleRefilter(immediate: false);

    private async void ScheduleRefilter(bool immediate)
    {
        // async void is intentional here: this is the entry point of a fire-and-forget UI refresh,
        // and every exception is handled below.
        try
        {
            if (_filterCts is not null)
            {
                await _filterCts.CancelAsync();
                _filterCts.Dispose();
            }

            _filterCts = new CancellationTokenSource();
            var ct = _filterCts.Token;
            if (!immediate)
            {
                await Task.Delay(FilterDebounce, ct);
            }

            if (!BuildFilter())
            {
                return;
            }

            var generation = ++_generation;
            var snapshot = _allEntries.ToArray();
            var predicate = _predicate;
            var filtered = await Task.Run(() => snapshot.Where(predicate).ToList(), ct);

            if (generation != _generation)
            {
                return; // the list was cleared or re-filtered meanwhile
            }

            // Entries that arrived while we were filtering in the background.
            filtered.AddRange(_allEntries.Skip(snapshot.Length).Where(predicate));

            var selected = SelectedEntry;
            VisibleEntries = new ObservableCollection<LogEntry>(filtered);
            SelectedEntry = selected is not null && predicate(selected) ? selected : null;
            UpdateCounts();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer filter.
        }
    }

    /// <summary>Compiles the current filter; returns false (and shows the error) for an invalid regex.</summary>
    private bool BuildFilter()
    {
        var severities = SeverityChips.Where(c => c.IsEnabled).Select(c => c.Value).ToHashSet();
        var environments = EnvironmentChips.Where(c => c.IsEnabled).Select(c => c.Value).ToHashSet();
        var filter = new LogFilter
        {
            Search = SearchText,
            Exclude = ExcludeText,
            UseRegex = UseRegex,
            MatchCase = MatchCase,
            Severities = severities,
            Environments = environments,
        };

        // Turning every chip off means "show nothing", not "no filter".
        if (severities.Count == 0 || (EnvironmentChips.Count > 0 && environments.Count == 0))
        {
            _predicate = static _ => false;
            FilterError = null;
            return true;
        }

        try
        {
            _predicate = filter.Compile();
            var highlights = new List<HighlightSpec>();
            if (filter.BuildHighlightRegex() is { } searchRegex)
            {
                highlights.Add(new HighlightSpec(searchRegex, "#FFD33D", IsSearch: true));
            }

            foreach (var rule in _workspaceHighlights.Where(r => !string.IsNullOrEmpty(r.Pattern)))
            {
                highlights.Add(new HighlightSpec(LogFilter.CreateRegex(rule.IsRegex ? rule.Pattern : System.Text.RegularExpressions.Regex.Escape(rule.Pattern), matchCase: false), rule.Color, IsSearch: false));
            }

            Highlights = highlights;
            FilterError = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            FilterError = ex.Message;
            return false;
        }
    }

    private void UpdateCounts()
    {
        foreach (var chip in SeverityChips)
        {
            chip.Count = 0;
        }

        var byEnvironment = EnvironmentChips.ToDictionary(c => c.Value, _ => 0);
        foreach (var entry in _allEntries)
        {
            _severityChips[entry.Severity].Count++;
            if (byEnvironment.TryGetValue(entry.Source.Environment, out var count))
            {
                byEnvironment[entry.Source.Environment] = count + 1;
            }
        }

        foreach (var chip in EnvironmentChips)
        {
            chip.Count = byEnvironment[chip.Value];
        }

        CountText = string.Create(CultureInfo.CurrentCulture, $"{VisibleEntries.Count:N0} of {_allEntries.Count:N0} events");
    }

    // ---- Persistence -------------------------------------------------------------------------

    private async Task LoadWorkspacesAsync()
    {
        try
        {
            foreach (var workspace in await _store.LoadAsync(CancellationToken.None))
            {
                Workspaces.Add(new WorkspaceItemViewModel(workspace));
            }

            SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Model.Id == _settings.Current.LastWorkspaceId) ?? Workspaces.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStoreError(ex);
        }
    }

    private async Task SaveWorkspacesAsync()
    {
        try
        {
            await _store.SaveAsync(Workspaces.Select(w => w.Model).ToList(), CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStoreError(ex);
            _dialogs.ShowError(ex.Message, "Could not save workspaces");
        }
    }

    public void Dispose()
    {
        _filterCts?.Cancel();
        _filterCts?.Dispose();
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();

        // The session's loop exits on cancellation; the process is shutting down, so don't block on it.
        _session?.DisposeAsync().AsTask().ContinueWith(t => LogSessionError(t.Exception!), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Log session failed")]
    private partial void LogSessionError(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Workspace store failed")]
    private partial void LogStoreError(Exception exception);
}
