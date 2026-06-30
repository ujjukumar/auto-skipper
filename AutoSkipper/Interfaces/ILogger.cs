namespace AutoSkipper.Interfaces;

public interface ILogger
{
    void Log(string message);
    void LogWarning(string message);
    void LogError(string message);
    void LogSuccess(string message);
    void LogDebug(Func<string> message);

    bool IsVerbose { get; }
}
