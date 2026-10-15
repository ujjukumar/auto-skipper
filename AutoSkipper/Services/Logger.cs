using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AutoSkipper.Interfaces;

namespace AutoSkipper.Services;

// Asynchronous logger: callers only enqueue. A dedicated background thread keeps
// the in-memory history, notifies viewers (the log window) and writes the
// optional log file. The app is a WinExe, so there is no console to write to.
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

    private static readonly string[] _levelNames = Enum.GetValues<LogLevel>().Select(l => l.ToString().ToUpperInvariant()).ToArray();
    public static string LevelName(LogLevel level) => _levelNames[(int)level];

    private static readonly BlockingCollection<LogEntry> _logQueue = new(new ConcurrentQueue<LogEntry>());
    private static readonly Thread _consumerThread;
    private static volatile bool _isShutdown = false;

    // Bounded in-memory history so viewers (e.g. the log window) can replay past entries.
    private const int MaxHistory = 2000;
    private static readonly Queue<LogEntry> _history = new();
    private static readonly object _historyLock = new();

    private static bool _verbose = false;
    private static StreamWriter? _logWriter; // Only accessed under _fileLock
    private static readonly object _fileLock = new();

    static Logger()
    {
        // A dedicated thread rather than Task.Run: the consumer blocks for the
        // lifetime of the app and shouldn't permanently occupy a pool thread.
        _consumerThread = new Thread(ProcessQueue) { IsBackground = true, Name = "AutoSkipper Logger" };
        _consumerThread.Start();
    }

    private static void ProcessQueue()
    {
        foreach (var entry in _logQueue.GetConsumingEnumerable())
        {
            try
            {
                // Invoke event for GUI
                lock (_historyLock)
                {
                    _history.Enqueue(entry);
                    if (_history.Count > MaxHistory) _history.Dequeue();
                    OnLogMessage?.Invoke(entry.Timestamp, entry.Level, entry.Message);
                }

                // Written under the lock so F7 can't dispose the writer mid-write.
                // Flushing when the queue drains keeps the file current without
                // flushing on every line during bursts.
                lock (_fileLock)
                {
                    if (_logWriter != null)
                    {
                        _logWriter.WriteLine($"{entry.Timestamp:HH:mm:ss.fff} | {LevelName(entry.Level)} | {entry.Message}");
                        if (_logQueue.Count == 0) _logWriter.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FATAL] Logger failed: {ex.Message}");
            }
        }
    }

    public void Log(string message) => Enqueue(LogLevel.Info, message);
    public void LogSuccess(string message) => Enqueue(LogLevel.Success, message);
    public void LogWarning(string message) => Enqueue(LogLevel.Warning, message);
    public void LogError(string message) => Enqueue(LogLevel.Error, message);

    public void LogDebug(Func<string> messageFactory)
    {
        if (_verbose) Enqueue(LogLevel.Debug, messageFactory());
    }

    public bool IsVerbose => _verbose;

    // For code without an ILogger instance (static helpers, config loading).
    public static void LogStatic(string message) => Enqueue(LogLevel.Info, message);
    public static void LogSuccessStatic(string message) => Enqueue(LogLevel.Success, message);
    public static void LogWarningStatic(string message) => Enqueue(LogLevel.Warning, message);
    public static void LogErrorStatic(string message) => Enqueue(LogLevel.Error, message);

    private static void Enqueue(LogLevel level, string message)
    {
        if (_isShutdown) return;
        try
        {
            _logQueue.TryAdd(new LogEntry(level, message));
        }
        catch (InvalidOperationException)
        {
            // Shutdown completed between the check and the add.
        }
    }

    // Attaches a viewer, replaying all buffered history first so it sees past
    // entries, then receiving live updates. Done under the history lock so no
    // entries are missed or duplicated relative to the live stream.
    public static void RegisterViewer(Action<DateTime, LogLevel, string> handler)
    {
        lock (_historyLock)
        {
            foreach (var entry in _history)
                handler(entry.Timestamp, entry.Level, entry.Message);
            OnLogMessage += handler;
        }
    }

    public static void UnregisterViewer(Action<DateTime, LogLevel, string> handler)
    {
        lock (_historyLock)
        {
            OnLogMessage -= handler;
        }
    }

    public static void SetVerbose(bool verbose)
    {
        _verbose = verbose;
        if (verbose) LogStatic("Verbose mode enabled");
    }

    public static void ToggleFileLogging()
    {
        lock (_fileLock)
        {
            if (_logWriter == null)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.DataDirectory);
                    var stream = new FileStream(AppPaths.LogFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                    _logWriter = new StreamWriter(stream, Encoding.UTF8, 4096) { AutoFlush = false };
                    LogSuccessStatic("File logging enabled.");
                }
                catch (Exception ex)
                {
                    LogErrorStatic($"Failed to enable file logging: {ex.Message}");
                }
            }
            else
            {
                LogStatic("File logging disabled.");
                _logWriter.Flush();
                _logWriter.Dispose();
                _logWriter = null;
            }
        }
    }

    public static void Shutdown()
    {
        Enqueue(LogLevel.Info, "Logger shutting down...");
        _isShutdown = true;
        _logQueue.CompleteAdding();

        // Wait for the consumer to finish.
        _consumerThread.Join(1500);

        // Final flush and close of the file writer.
        lock (_fileLock)
        {
            _logWriter?.Flush();
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }
}
