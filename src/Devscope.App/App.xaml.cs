using System.Windows;
using System.Windows.Threading;
using Devscope.App.Services;
using Devscope.App.ViewModels;
using Devscope.App.Views;
using Devscope.Core.Ear;
using Devscope.Core.Logs;
using Devscope.Core.Workspaces;
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
        services.AddSingleton<IArchiveScanner, ArchiveScanner>();
        services.AddSingleton<IEarComparer, EarComparer>();
        services.AddSingleton<IJavaDecompiler, VineflowerDecompiler>();
        services.AddSingleton<ILogSessionFactory, LogSessionFactory>();
        services.AddSingleton<IWorkspaceStore>(sp =>
            new JsonWorkspaceStore(JsonWorkspaceStore.DefaultFilePath, sp.GetRequiredService<ILogger<JsonWorkspaceStore>>()));

        // App services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IUserSettingsStore, UserSettingsStore>();
        services.AddSingleton<IShellService, ShellService>();

        // View models and the window
        services.AddSingleton<EarDiffViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return builder.Build();
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
