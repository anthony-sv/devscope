using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Devscope.App.Services;

/// <summary>Minimal thread-safe daily file logger for the app's own diagnostics.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetainedDays = 14;

    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Lock _gate = new();
    private readonly StreamWriter? _writer;

    public FileLoggerProvider(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            DeleteOldFiles(folder);
            var path = Path.Combine(folder, $"devscope-{DateTime.Now:yyyyMMdd}.log");
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never prevent the app from starting.
            _writer = null;
        }
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }

    private void Write(string line)
    {
        if (_writer is null)
        {
            return;
        }

        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    private static void DeleteOldFiles(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "devscope-*.log"))
        {
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-RetainedDays))
            {
                File.Delete(file);
            }
        }
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {category}: {formatter(state, exception)}");
            provider.Write(exception is null ? line : line + Environment.NewLine + exception);
        }
    }
}
