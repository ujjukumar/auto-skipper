using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using AutoSkipper.Interfaces;

namespace AutoSkipper.Services;

// A high-performance, asynchronous logger designed to minimize impact on the main application thread.
// It uses a background thread to process and write log messages from a concurrent queue.
public class Logger : ILogger
{
    public enum LogLevel { Info, Debug, Error, Success, Warning }
    public static event Action<DateTime, LogLevel, string>? OnLogMessage;

    private readonly struct LogEntry
    {
        public readonly DateTime Timestamp;
        public readonly LogLevel Level;
        public readonly string Message;

        public LogEntry(LogLevel level, string message)
        {
            Timestamp = DateTime.Now;
            Level = level;
            Message = message;
        }
    }

    private static readonly BlockingCollection<LogEntry> _logQueue = new(new ConcurrentQueue<LogEntry>());
    private static readonly Task _consumerTask;
    private static volatile bool _isShutdown = false;

    private static bool _verbose = false;
    private static volatile StreamWriter? _logWriter; // Volatile for thread-safe reads
    private static readonly object _fileLock = new(); // Lock for creating/disposing the writer
    private static readonly Regex _markupRegex = new(@"\[/?[^\]]*\]", RegexOptions.Compiled);

    static Logger()
    {
        _consumerTask = Task.Run(ProcessQueue);
    }

    private static void ProcessQueue()
    {
        foreach (var entry in _logQueue.GetConsumingEnumerable())
        {
            try
            {
                // Invoke event for GUI
                OnLogMessage?.Invoke(entry.Timestamp, entry.Level, entry.Message);

                string timestamp = $"[grey]{entry.Timestamp:HH:mm:ss.fff}[/]";
                string level = entry.Level switch
                {
                    LogLevel.Info => "[blue]INFO[/]",
                    LogLevel.Debug => "[grey]DEBUG[/]",
                    LogLevel.Error => "[red]ERROR[/]",
                    LogLevel.Success => "[green]SUCCESS[/]",
                    LogLevel.Warning => "[yellow]WARN[/]",
                    _ => "[white]LOG[/]"
                };
                
                string message = entry.Message;
                // Basic markup escaping if needed, though we generally trust internal messages
                // message = Markup.Escape(message); 

                AnsiConsole.MarkupLine($"{timestamp} | {level} | {message}");
                
                // File logging remains plain text (strip Spectre markup)
                var writer = _logWriter;
                if (writer != null)
                {
                    string plainMessage = _markupRegex.Replace(entry.Message, "");
                    writer.WriteLine($"{entry.Timestamp:HH:mm:ss.fff} | {entry.Level.ToString().ToUpper()} | {plainMessage}");
                }
            }
            catch (Exception ex)
            {
                // Fallback if Spectre fails
                Console.WriteLine($"[FATAL] Logger failed: {ex.Message}");
            }
        }
    }

    public void Log(string message)
    {
        if (_isShutdown) return;
        _logQueue.TryAdd(new LogEntry(LogLevel.Info, message));
    }
    
    // Add back static methods for compatibility
    public static void LogStatic(string message) => new Logger().Log(message);
    public static void LogSuccessStatic(string message) => new Logger().LogSuccess(message);
    public static void LogWarningStatic(string message) => new Logger().LogWarning(message);
    public static void LogErrorStatic(string message) => new Logger().LogError(message);
    public static void LogDebugStatic(Func<string> messageFactory) => new Logger().LogDebug(messageFactory);

    public void LogSuccess(string message)
    {
        if (_isShutdown) return;
        _logQueue.TryAdd(new LogEntry(LogLevel.Success, message));
    }
    
    public void LogWarning(string message)
    {
        if (_isShutdown) return;
        _logQueue.TryAdd(new LogEntry(LogLevel.Warning, message));
    }
    
    public void LogError(string message)
    {
        if (_isShutdown) return;
        _logQueue.TryAdd(new LogEntry(LogLevel.Error, message));
    }

    public void LogDebug(Func<string> messageFactory)
    {
        if (_isShutdown || !_verbose) return;
        _logQueue.TryAdd(new LogEntry(LogLevel.Debug, messageFactory()));
    }

    public static void SetVerbose(bool verbose)
    {
        _verbose = verbose;
        if (verbose) new Logger().Log("Verbose mode enabled");
    }

    public static void ToggleFileLogging()
    {
        lock (_fileLock)
        {
            if (_logWriter == null)
            {
                try
                {
                    string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoSkipper");
                    Directory.CreateDirectory(appDataPath);
                    string logPath = Path.Combine(appDataPath, "autoskip_dialogue.log");
                    var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    _logWriter = new StreamWriter(stream, Encoding.UTF8, 4096) { AutoFlush = false };
                    new Logger().LogSuccess("File logging enabled.");
                }
                catch (Exception ex)
                {
                    new Logger().LogError($"Failed to enable file logging: {ex.Message}");
                }
            }
            else
            {
                new Logger().Log("File logging disabled.");
                _logWriter.Flush();
                _logWriter.Dispose();
                _logWriter = null;
            }
        }
    }

    public static void Shutdown()
    {
        _logQueue.TryAdd(new LogEntry(LogLevel.Info, "Logger shutting down..."));
        _isShutdown = true;
        _logQueue.CompleteAdding();

        // Wait for the consumer to finish.
        _consumerTask.Wait(1500);

        // Final flush and close of the file writer.
        lock (_fileLock)
        {
            _logWriter?.Flush();
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }
}
