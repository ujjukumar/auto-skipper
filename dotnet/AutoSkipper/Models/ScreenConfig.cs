using System.Text.Json;
using AutoSkipper.Services;

namespace AutoSkipper.Models;

internal class ScreenConfig
{
    public int WIDTH { get; }
    public int HEIGHT { get; }
    public (int x, int y) PLAYING_ICON { get; }
    public (int x, int ly, int hy) DIALOGUE_ICON { get; }
    public (int x, int y) LOADING_PIXEL { get; }
    public string WINDOW_TITLE { get; }
    
    public ConfigData Config => _configData;

    private readonly ConfigData _configData;

    // Hardcoded pixel coordinates (1920x1080 base)
    private const int BaseWidth = 1920;
    private const int BaseHeight = 1080;
    private const int PlayingIconX = 84;
    private const int PlayingIconY = 46;
    private const int DialogueIconX = 1301;
    private const int DialogueIconLowY = 808;
    private const int DialogueIconHighY = 790;
    private const int DialogueIconWsLowY = 810;
    private const int DialogueIconWsHighY = 792;
    private const int LoadingPixelX = 1200;
    private const int LoadingPixelY = 700;
    private const int PlayingIconWsX = 230;
    private const int DialogueIconWsX = 2770;
    private const float DialogueIconWsExtra = 0.02f;

    public static async Task<ScreenConfig> CreateAsync()
    {
        var configData = await LoadConfigAsync();
        
        int w = Native.GetSystemMetrics(0);
        int h = Native.GetSystemMetrics(1);
        
        return new ScreenConfig(w, h, configData);
    }
    
    private static string GetConfigPath()
    {
        string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoSkipper");
        Directory.CreateDirectory(appDataPath);
        return Path.Combine(appDataPath, "config.json");
    }
    
    private static async Task<ConfigData> LoadConfigAsync()
    {
        string configPath = GetConfigPath();
        ConfigData config;
        if (File.Exists(configPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(configPath);
                config = JsonSerializer.Deserialize(json, JsonContext.Default.ConfigData) ?? new ConfigData();
            }
            catch (Exception ex)
            {
                Services.Logger.LogErrorStatic($"Error loading config.json: {ex.Message}");
                Services.Logger.LogStatic("Using default configuration.");
                config = new ConfigData();
            }
        }
        else
        {
            Services.Logger.LogStatic("config.json not found. Creating with default values.");
            config = new ConfigData();
            var json = JsonSerializer.Serialize(config, JsonContext.Default.ConfigData);
            await File.WriteAllTextAsync(configPath, json);
        }
        config.Validate();
        return config;
    }

    private ScreenConfig(int w, int h, ConfigData configData)
    {
        _configData = configData;
        WIDTH = w; 
        HEIGHT = h;
        WINDOW_TITLE = _configData.WindowTitle;
        PLAYING_ICON = CalcPlayingIcon();
        DIALOGUE_ICON = CalcDialogueIcon();
        LOADING_PIXEL = (Wa(LoadingPixelX), Ha(LoadingPixelY));
    }

    private int Wa(int x) => (int)(x / (float)BaseWidth * WIDTH);
    private int Ha(int y) => (int)(y / (float)BaseHeight * HEIGHT);

    private int ScalePos(int hd, int doubleHd, float extra = 0.0f)
    {
        if (WIDTH <= 3840) extra = 0.0f;
        int diff = doubleHd - hd;
        return (int)(hd + (WIDTH - BaseWidth) * ((diff / (float)BaseWidth) + extra));
    }

    private (int, int) CalcPlayingIcon()
    {
        bool ws = WIDTH > BaseWidth && Math.Abs((double)HEIGHT/WIDTH - 0.5625) > 0.001;
        int x = ws ? Math.Min(ScalePos(PlayingIconX, PlayingIconWsX), PlayingIconWsX) : Wa(PlayingIconX);
        return (x, Ha(PlayingIconY));
    }

    private (int, int, int) CalcDialogueIcon()
    {
        bool ws = WIDTH > BaseWidth && Math.Abs((double)HEIGHT/WIDTH - 0.5625) > 0.001;
        int x = ws ? ScalePos(DialogueIconX, DialogueIconWsX, DialogueIconWsExtra) : Wa(DialogueIconX);
        int ly = ws ? Ha(DialogueIconWsLowY) : Ha(DialogueIconLowY);
        int hy = ws ? Ha(DialogueIconWsHighY) : Ha(DialogueIconHighY);
        return (x, ly, hy);
    }
}
