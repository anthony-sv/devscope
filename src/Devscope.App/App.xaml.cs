using System.Windows;
using System.Windows.Threading;
using Devscope.App.Services;
using Devscope.App.ViewModels;
using Devscope.App.Views;
using Devscope.Core.Database;
using Devscope.Core.Ear;
using Devscope.Core.Logs;
using Devscope.Core.Workspaces;
using Devscope.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Devscope.App;

/// <summary>Composition root: generic host, dependency injection, logging and crash handling.</summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>Folder for the app's own diagnostics (not the logs being viewed).</summary>
    public static string LocalDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Devscope");

    protected override async void OnStartup(StartupEventArgs e)
    {
        RegisterCrashHandlers();
        base.OnStartup(e);

        _host = BuildHost();
        await _host.StartAsync();
        ApplySettings(_host.Services);

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Synchronous dispose: stops log sessions and flushes the file logger.
        _host?.Dispose();
        base.OnExit(e);
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });

        // No console, no event log: a single rolling file under %LOCALAPPDATA%\Devscope\logs.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(LocalDataFolder, "logs")));

        var services = builder.Services;

        // Core engine
        services.AddSingleton<ArchiveScanner>();
        services.AddSingleton<IArchiveScanner>(sp => sp.GetRequiredService<ArchiveScanner>());
        services.AddSingleton<IEarComparer, EarComparer>();
        services.AddSingleton(new DecompiledSourceCache(Path.Combine(LocalDataFolder, "cache", "decompiled"), maxBytes: 0));
        services.AddSingleton<VineflowerDecompiler>();
        services.AddSingleton<IJavaDecompiler>(sp => new CachingJavaDecompiler(
            sp.GetRequiredService<VineflowerDecompiler>(), sp.GetRequiredService<DecompiledSourceCache>()));
        services.AddSingleton<ILogSessionFactory, LogSessionFactory>();
        services.AddSingleton<IWorkspaceStore>(sp =>
            new JsonWorkspaceStore(JsonWorkspaceStore.DefaultFilePath, sp.GetRequiredService<ILogger<JsonWorkspaceStore>>()));
        services.AddSingleton<IDatabaseProvider, SqlServerProvider>();
        services.AddSingleton<IConnectionStore>(sp =>
            new JsonConnectionStore(JsonConnectionStore.DefaultFilePath, sp.GetRequiredService<ILogger<JsonConnectionStore>>()));

        // App services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IUserSettingsStore, UserSettingsStore>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<ConnectionPasswords>();

        // View models and the window
        services.AddSingleton<EarDiffViewModel>();
        services.AddSingleton<DbDiffViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<MainWindow>();

        return builder.Build();
    }

    /// <summary>Pushes cache settings into the engine now and whenever the Settings window saves.</summary>
    private static void ApplySettings(IServiceProvider services)
    {
        var store = services.GetRequiredService<IUserSettingsStore>();
        var scanner = services.GetRequiredService<ArchiveScanner>();
        var cache = services.GetRequiredService<DecompiledSourceCache>();

        void Apply()
        {
            var settings = store.Current;
            scanner.CacheCapacityBytes = Math.Max(0, settings.ArchiveCacheMegabytes) * 1024L * 1024L;
            cache.Enabled = settings.DiskCacheEnabled;
            cache.MaxBytes = Math.Max(1, settings.DiskCacheMegabytes) * 1024L * 1024L;
            _ = Task.Run(cache.Trim);
        }

        Apply();
        store.Changed += (_, _) => Apply();
    }

    private void RegisterCrashHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => CrashLog.Write("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("UnobservedTask", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write("Dispatcher", e.Exception);
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to {CrashLog.FilePath}",
            "Devscope", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
