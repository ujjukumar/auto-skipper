namespace AutoSkipper;

// Files the app keeps in %AppData%\AutoSkipper.
internal static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoSkipper");

    public static string LogFile => Path.Combine(DataDirectory, "autoskip_dialogue.log");
    public static string CrashLog => Path.Combine(DataDirectory, "crash.log");

    // Optional user-supplied tray icon that overrides the built-in one.
    public static string CustomIcon => Path.Combine(DataDirectory, "auto_marker.ico");
}
