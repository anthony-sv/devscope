using System.Collections.Concurrent;
using System.Threading.Channels;
using Devscope.Core.Workspaces;
using Microsoft.Extensions.Logging;

namespace Devscope.Core.Logs;

/// <summary>Tuning knobs for a <see cref="LogSession"/>.</summary>
public sealed record LogSessionOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often folders are re-listed to pick up new or rotated files.</summary>
    public TimeSpan RediscoverInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>When a session starts, only this many trailing bytes of each existing file are loaded.</summary>
    public long InitialTailBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>Newest N files per source; protects against folders holding years of history.</summary>
    public int MaxFilesPerSource { get; init; } = 50;

    /// <summary>Parallel reads; network shares benefit from a few concurrent requests.</summary>
    public int MaxConcurrentReads { get; init; } = 8;
}

/// <summary>An environment chosen for viewing, with its sources.</summary>
public sealed record EnvironmentSelection(string Name, IReadOnlyList<WorkspaceSource> Sources, int ColorIndex);

/// <summary>Reachability of one configured source.</summary>
public sealed record SourceStatus(string Environment, string Path, int FileCount, string? Error);

/// <summary>New entries (sorted by time) and, after each discovery, the current source statuses.</summary>
public sealed record LogBatch(IReadOnlyList<LogEntry> Entries, bool IsInitial, IReadOnlyList<SourceStatus>? Statuses);

/// <summary>Creates log sessions; allows the UI to be tested without touching the file system.</summary>
public interface ILogSessionFactory
{
    LogSession Create(IReadOnlyList<EnvironmentSelection> environments, LogSessionOptions options);
}

/// <inheritdoc />
public sealed class LogSessionFactory(ILoggerFactory loggerFactory) : ILogSessionFactory
{
    /// <inheritdoc />
    public LogSession Create(IReadOnlyList<EnvironmentSelection> environments, LogSessionOptions options) =>
        new(environments, options, loggerFactory.CreateLogger<LogSession>());
}

/// <summary>
/// Follows every log file of the selected environments and streams new entries through <see cref="Batches"/>.
/// All access is read-only, directly against the local or UNC paths: no copies, uploads or listening ports.
/// </summary>
public sealed partial class LogSession : IAsyncDisposable
{
    private readonly IReadOnlyList<EnvironmentSelection> _environments;
    private readonly LogSessionOptions _options;
    private readonly ILogger<LogSession> _logger;
    private readonly Dictionary<string, LogFileTailer> _tailers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<LogBatch> _channel = Channel.CreateUnbounded<LogBatch>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private long _nextId;

    public LogSession(IReadOnlyList<EnvironmentSelection> environments, LogSessionOptions options, ILogger<LogSession> logger)
    {
        ArgumentNullException.ThrowIfNull(environments);
        ArgumentNullException.ThrowIfNull(options);
        _environments = environments;
        _options = options;
        _logger = logger;
    }

    /// <summary>Stream of batches; the first one has <see cref="LogBatch.IsInitial"/> set.</summary>
    public ChannelReader<LogBatch> Batches => _channel.Reader;

    /// <summary>Starts reading in the background.</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var statuses = Discover(_options.InitialTailBytes);
            // A second, immediate poll flushes each file's last buffered event, so the initial view is complete and sorted.
            var initial = await PollAllAsync(cancellationToken).ConfigureAwait(false);
            initial.AddRange(await PollAllAsync(cancellationToken).ConfigureAwait(false));
            initial.Sort(LogEntry.CompareByTime);
            await _channel.Writer.WriteAsync(new LogBatch(initial, IsInitial: true, statuses), cancellationToken).ConfigureAwait(false);

            var lastDiscovery = DateTime.UtcNow;
            using var timer = new PeriodicTimer(_options.PollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyList<SourceStatus>? changed = null;
                if (DateTime.UtcNow - lastDiscovery >= _options.RediscoverInterval)
                {
                    // Files appearing after start are new: read them from the beginning.
                    changed = Discover(initialTailBytes: 0);
                    lastDiscovery = DateTime.UtcNow;
                }

                var entries = await PollAllAsync(cancellationToken).ConfigureAwait(false);
                if (entries.Count > 0 || changed is not null)
                {
                    await _channel.Writer.WriteAsync(new LogBatch(entries, IsInitial: false, changed), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Session closed.
        }
        catch (Exception ex)
        {
            LogSessionFailed(ex);
            _channel.Writer.TryComplete(ex);
            return;
        }

        _channel.Writer.TryComplete();
    }

    private List<SourceStatus> Discover(long initialTailBytes)
    {
        var statuses = new List<SourceStatus>();
        foreach (var environment in _environments)
        {
            foreach (var source in environment.Sources)
            {
                try
                {
                    var files = LogSourceResolver.Resolve(source, _options.MaxFilesPerSource);
                    foreach (var file in files.Where(f => !_tailers.ContainsKey(f)))
                    {
                        _tailers[file] = new LogFileTailer(
                            new LogFileSource(environment.Name, file, environment.ColorIndex),
                            initialTailBytes,
                            () => Interlocked.Increment(ref _nextId));
                    }

                    statuses.Add(new SourceStatus(environment.Name, source.Path, files.Count, files.Count == 0 ? "No matching files" : null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    statuses.Add(new SourceStatus(environment.Name, source.Path, 0, ex.Message));
                }
            }
        }

        return statuses;
    }

    private async Task<List<LogEntry>> PollAllAsync(CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<IReadOnlyList<LogEntry>>();
        await Parallel.ForEachAsync(
            _tailers.Values,
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrentReads, CancellationToken = cancellationToken },
            async (tailer, ct) => results.Add(await tailer.PollAsync(ct).ConfigureAwait(false))).ConfigureAwait(false);

        var entries = results.SelectMany(r => r).ToList();
        entries.Sort(LogEntry.CompareByTime);
        return entries;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Log session stopped unexpectedly")]
    private partial void LogSessionFailed(Exception exception);
}
