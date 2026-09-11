using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// A minimal, dependency-light <see cref="ILogger"/> that writes to the console and,
    /// when a path is configured, appends the same lines to a log file. Instances are
    /// thread-safe and can be registered as a singleton in the DI container.
    /// </summary>
    public sealed class SqlMigratorLogger : ILogger
    {
        private static readonly object Sync = new object();
        private readonly TextWriter? _file;
        private readonly bool _writeFile;
        private readonly string _name;

        public SqlMigratorLogger(string? logFilePath, string categoryName = "SqlMigrator")
        {
            _name = categoryName;
            _writeFile = !string.IsNullOrWhiteSpace(logFilePath);
            if (_writeFile)
            {
                var path = logFilePath!;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                _file = TextWriter.Synchronized(new StreamWriter(path, true, Encoding.UTF8) { AutoFlush = true });
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelTag(logLevel)}] {message}";
            if (exception != null)
                line += Environment.NewLine + "    " + exception.ToString().Replace(Environment.NewLine, Environment.NewLine + "    ");

            var stamp = $"[{_name}] {line}";

            lock (Sync)
            {
                ConsoleColor previous = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ColorFor(logLevel);
                    Console.WriteLine(line);
                }
                finally
                {
                    Console.ForegroundColor = previous;
                }

                if (_writeFile)
                    _file?.WriteLine(stamp);
            }
        }

        private static string LevelTag(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT ",
            _ => "LOG  "
        };

        private static ConsoleColor ColorFor(LogLevel level) => level switch
        {
            LogLevel.Debug => ConsoleColor.Blue,
            LogLevel.Information => ConsoleColor.Green,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Critical => ConsoleColor.DarkRed,
            _ => ConsoleColor.Gray
        };
    }
}