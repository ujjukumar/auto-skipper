using AutoSkipper.Models;

namespace AutoSkipper.UI;

internal class Program
{
    // Session-wide names shared by every copy of the app.
    internal const string InstanceMutexName = @"Local\GenshinAutoSkipper.Instance";
    internal const string ShowWindowEventName = @"Local\GenshinAutoSkipper.ShowWindow";

    [STAThread]
    static void Main(string[] args)
    {
        // Only one copy may run: a second would install the hooks again and double every press.
        using var instanceMutex = new Mutex(initiallyOwned: false, InstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            ActivateRunningInstance();
            return;
        }

        bool verbose = args.Any(arg => arg is "-v" or "--verbose");
        Services.Logger.SetVerbose(verbose);
        
        ApplicationConfiguration.Initialize();
        InstallErrorHandlers();

        try
        {
            var store = ConfigStore.Load(AppPaths.DataDirectory);
            using var context = new TrayContext(store);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show($"Error: {ex.Message}", "AutoSkipper Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Asks the running copy to show its window (the user probably launched the
    // app again because they couldn't find the tray icon).
    internal static void ActivateRunningInstance()
    {
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
            // This process owns the foreground right now; let the running copy take it.
            Services.Native.AllowSetForegroundWindow(Services.Native.ASFW_ANY);
            showEvent.Set();
        }
        catch (Exception)
        {
            MessageBox.Show("AutoSkipper is already running. Look for its icon in the system tray.",
                "AutoSkipper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static void InstallErrorHandlers()
    {
        // UI-thread exceptions are logged and the app keeps running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
        {
            WriteCrashLog(e.Exception);
            Services.Logger.LogErrorStatic($"Unexpected error: {e.Exception.Message} (details in crash.log)");
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            WriteCrashLog(e.Exception);
            Services.Logger.LogErrorStatic($"Unexpected background error: {e.Exception.InnerException?.Message ?? e.Exception.Message}");
            e.SetObserved();
        };
        // Exceptions on other threads terminate the process; record why first.
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            WriteCrashLog(e.ExceptionObject as Exception);
            MessageBox.Show($"AutoSkipper crashed: {(e.ExceptionObject as Exception)?.Message}\n\nDetails were saved to {AppPaths.CrashLog}.",
                "AutoSkipper Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
    }

    // Written directly (not via the async logger) so it survives a dying process.
    internal static void WriteCrashLog(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var info = new FileInfo(AppPaths.CrashLog);
            if (info.Exists && info.Length > 1_000_000) info.Delete();
            File.AppendAllText(AppPaths.CrashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best effort only.
        }
    }
}
