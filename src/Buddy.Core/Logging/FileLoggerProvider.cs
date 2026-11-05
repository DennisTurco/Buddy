using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Buddy.Core.Logging;

/// <summary>
/// Writes log lines to one file per day (<c>buddy-AAAA-MM-GG.log</c>) in <paramref name="folder"/> and deletes
/// files older than <paramref name="keepDays"/>. Small and dependency-free: writes are serialized with a lock and
/// the file is opened in append mode for each line, so nothing is lost if the process crashes.
/// </summary>
public sealed class FileLoggerProvider(string folder, LogLevel minimumLevel = LogLevel.Information, int keepDays = 14) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Lock _gate = new();
    private bool _cleaned;

    public string Folder => folder;

    /// <summary>Today's log file.</summary>
    public string CurrentFile => Path.Combine(folder, $"buddy-{DateTime.Now:yyyy-MM-dd}.log");

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(this, ShortName(name)));

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (level < minimumLevel)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ')
            .Append(Level(level)).Append(' ')
            .Append(category).Append(": ")
            .Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                if (!_cleaned)
                {
                    _cleaned = true;
                    CleanOldFiles();
                }

                File.AppendAllText(CurrentFile, line.AppendLine().ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never break the app.
            }
        }
    }

    private void CleanOldFiles()
    {
        foreach (var file in Directory.EnumerateFiles(folder, "buddy-*.log"))
        {
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-keepDays))
            {
                File.Delete(file);
            }
        }
    }

    private static string ShortName(string category) => category[(category.LastIndexOf('.') + 1)..];

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "CRT",
    };

    public void Dispose()
    {
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider.Minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }

    internal LogLevel Minimum => minimumLevel;
}
