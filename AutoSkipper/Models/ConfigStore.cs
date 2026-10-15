using System.Text.Json;
using AutoSkipper.Services;

namespace AutoSkipper.Models;

// Loads and saves config.json in a data directory (normally AppPaths.DataDirectory).
internal class ConfigStore
{
    public ConfigData Config { get; }

    private const string ConfigFileName = "config.json";
    private readonly string _directory;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private string ConfigPath => Path.Combine(_directory, ConfigFileName);

    private ConfigStore(string directory, ConfigData config)
    {
        _directory = directory;
        Config = config;
    }

    public static ConfigStore Load(string directory)
    {
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, ConfigFileName);
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
                string? backup = BackupBrokenConfig(directory, configPath);
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

        var store = new ConfigStore(directory, config);
        if (save)
        {
            try
            {
                store.WriteAtomic(JsonSerializer.Serialize(config, JsonContext.Default.ConfigData));
            }
            catch (Exception ex)
            {
                Logger.LogErrorStatic($"Failed to write config.json: {ex.Message}");
            }
        }
        return store;
    }

    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            string json = JsonSerializer.Serialize(Config, JsonContext.Default.ConfigData);
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
    private void WriteAtomic(string json)
    {
        string tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    private static string? BackupBrokenConfig(string directory, string configPath)
    {
        try
        {
            string backup = Path.Combine(directory, $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
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
