using System.Globalization;
using System.Text;

namespace Devscope.Core.Ear;

/// <summary>Timing for <see cref="DeploymentWatcher"/>.</summary>
public sealed record DeploymentWatcherOptions
{
    /// <summary>How long a deployment must stay unchanged before <see cref="DeploymentWatcher.Changed"/> fires.</summary>
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Safety-net poll for local paths, where file system events normally wake the watcher first.</summary>
    public TimeSpan LocalPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Poll for network shares, where file system events are unreliable.</summary>
    public TimeSpan NetworkPollInterval { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Watches deployments (archive files or exploded folders) and raises <see cref="Changed"/> once one has changed
/// and then stayed unchanged for a quiet period, so a half-copied EAR is never rescanned. File system events wake
/// it early; polling a cheap fingerprint (sizes and timestamps) catches what events miss on network shares.
/// </summary>
public sealed class DeploymentWatcher : IDisposable
{
    private const string Unavailable = "unavailable";

    private readonly IReadOnlyList<string> _roots;
    private readonly DeploymentWatcherOptions _options;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    public DeploymentWatcher(IReadOnlyList<string> roots, DeploymentWatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = roots;
        _options = options ?? new DeploymentWatcherOptions();
    }

    /// <summary>Raised on a thread-pool thread after a settled change.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Starts watching. Completes once the baseline is taken, off the caller's thread since listing an exploded
    /// folder on a share can take a while; changes after that are reported.
    /// </summary>
    public async Task StartAsync()
    {
        if (_loop is not null)
        {
            return;
        }

        var token = _cts.Token;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop = Task.Run(async () =>
        {
            foreach (var root in _roots)
            {
                TryWatch(root);
            }

            var baseline = Fingerprint();
            ready.SetResult();
            await RunAsync(baseline, token).ConfigureAwait(false);
        });
        await ready.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_watchers)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _cts.Cancel();
        _cts.Dispose();
        _wake.Dispose();
    }

    /// <summary>Whether <paramref name="path"/> is on a network share (UNC or mapped drive).</summary>
    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task RunAsync(string baseline, CancellationToken ct)
    {
        var interval = _roots.Any(IsNetworkPath) ? _options.NetworkPollInterval : _options.LocalPollInterval;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _wake.WaitAsync(interval, ct).ConfigureAwait(false);
                var current = Fingerprint();
                if (current == baseline)
                {
                    continue;
                }

                // Wait until the copy or redeploy has finished: nothing changed for a whole quiet period.
                while (true)
                {
                    await Task.Delay(_options.QuietPeriod, ct).ConfigureAwait(false);
                    var next = Fingerprint();
                    if (next == current)
                    {
                        break;
                    }

                    current = next;
                }

                // Events raised by the change itself are already accounted for.
                while (_wake.Wait(0, CancellationToken.None))
                {
                }

                baseline = current;

                // A deployment that is gone (mid-redeploy, share down) fires when it comes back instead.
                if (!current.Contains(Unavailable, StringComparison.Ordinal))
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Disposed.
        }
    }

    private void TryWatch(string root)
    {
        try
        {
            FileSystemWatcher watcher;
            if (Directory.Exists(root))
            {
                watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true };
            }
            else if (Path.GetDirectoryName(Path.GetFullPath(root)) is { } folder && Directory.Exists(folder))
            {
                watcher = new FileSystemWatcher(folder, Path.GetFileName(root));
            }
            else
            {
                return;
            }

            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.InternalBufferSize = 64 * 1024;
            watcher.Changed += (_, _) => Wake();
            watcher.Created += (_, _) => Wake();
            watcher.Deleted += (_, _) => Wake();
            watcher.Renamed += (_, _) => Wake();
            watcher.Error += (_, _) => Wake();
            lock (_watchers)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return;
                }

                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Polling still covers this root.
        }
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // An event raced with Dispose.
        }
    }

    private string Fingerprint()
    {
        var sb = new StringBuilder();
        foreach (var root in _roots)
        {
            sb.Append(Fingerprint(root)).Append('\n');
        }

        return sb.ToString();
    }

    private static string Fingerprint(string root)
    {
        try
        {
            if (File.Exists(root))
            {
                var file = new FileInfo(root);
                return string.Create(CultureInfo.InvariantCulture, $"{file.Length}|{file.LastWriteTimeUtc.Ticks}");
            }

            if (Directory.Exists(root))
            {
                long count = 0, bytes = 0, latest = 0;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
                {
                    count++;
                    bytes += file.Length;
                    latest = Math.Max(latest, file.LastWriteTimeUtc.Ticks);
                }

                return string.Create(CultureInfo.InvariantCulture, $"{count}|{bytes}|{latest}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treated as missing: the share may be down or the folder mid-redeploy.
        }

        return Unavailable;
    }
}
