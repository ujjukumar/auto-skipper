using System.Text.Json;
using AutoSkipper.Services;

namespace AutoSkipper.Models;

// Loads and saves %AppData%\AutoSkipper\config.json.
internal class ScreenConfig
{
    public ConfigData Config => _configData;

    private readonly ConfigData _configData;
    private static readonly SemaphoreSlim _saveLock = new(1, 1);

    private static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoSkipper");
    private static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    private ScreenConfig(ConfigData configData)
    {
        _configData = configData;
    }

    public static ScreenConfig Load()
    {
        Directory.CreateDirectory(ConfigDirectory);
        string configPath = ConfigPath;
        ConfigData? config = null;

        if (File.Exists(configPath))
        {
            try
            {
                config = JsonSerializer.Deserialize(File.ReadAllText(configPath), JsonContext.Default.ConfigData);
            }
            catch (Exception ex)
            {
                Logger.LogErrorStatic($"Error loading config.json: {ex.Message}");
                // Keep the unreadable file: the defaults written below would otherwise
                // replace it and lose the user's settings and macros.
                string? backup = BackupBrokenConfig(configPath);
                Logger.LogWarningStatic(backup != null
                    ? $"Using default configuration. The unreadable file was saved as {Path.GetFileName(backup)}."
                    : "Using default configuration.");
            }
        }
        else
        {
            Logger.LogStatic("config.json not found. Creating with default values.");
        }

        bool save = config == null;
        config ??= new ConfigData();
        config.Validate();
        save |= config.Migrate();

        var screenConfig = new ScreenConfig(config);
        if (save)
        {
            try
            {
                WriteAtomic(JsonSerializer.Serialize(config, JsonContext.Default.ConfigData));
            }
            catch (Exception ex)
            {
                Logger.LogErrorStatic($"Failed to write config.json: {ex.Message}");
            }
        }
        return screenConfig;
    }

    public async Task SaveConfigAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            string json = JsonSerializer.Serialize(_configData, JsonContext.Default.ConfigData);
            string tempPath = ConfigPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // Writes to a temp file and then replaces config.json, so a crash or full
    // disk mid-write can't leave a truncated config behind.
    private static void WriteAtomic(string json)
    {
        string tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    private static string? BackupBrokenConfig(string configPath)
    {
        try
        {
            string backup = Path.Combine(ConfigDirectory, $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(configPath, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex)
        {
            Logger.LogErrorStatic($"Failed to back up config.json: {ex.Message}");
            return null;
        }
    }
}
