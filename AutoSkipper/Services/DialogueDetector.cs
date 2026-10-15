using AutoSkipper.Interfaces;
using AutoSkipper.Models;

namespace AutoSkipper.Services;

// Reads a few pixels of the game window to tell whether a dialogue is showing.
// Pixel positions are relative to the game's client area, which is tracked so
// windowed mode, other monitors and resolution changes work.
internal sealed class DialogueDetector
{
    // GetPixel returns CLR_INVALID on failure. It must never count as a colour
    // match, otherwise a failed read looks like a white dialogue-choice pixel.
    private const uint ClrInvalid = 0xFFFFFFFF;
    private const int MinGameAreaSize = 200;

    private readonly ConfigData _config;
    private readonly ILogger _logger;
    private readonly IntPtr _hdc;

    private int _areaX, _areaY, _areaWidth, _areaHeight;
    private PixelLayout _layout;

    // hdc is the device context pixels are read from: the screen in the app, or a
    // memory bitmap in tests. The caller owns it.
    public DialogueDetector(ConfigData config, ILogger logger, IntPtr hdc)
    {
        _config = config;
        _logger = logger;
        _hdc = hdc;
    }

    public (int X, int Y, int Width, int Height) GameArea => (_areaX, _areaY, _areaWidth, _areaHeight);
    public PixelLayout Layout => _layout;

    // Reads the game window's client area. Returns false if there is no usable
    // area (e.g. minimised).
    public bool UpdateGameArea(IntPtr hwnd)
    {
        if (!Native.GetClientRect(hwnd, out var rect)) return false;
        var origin = new Native.POINT();
        if (!Native.ClientToScreen(hwnd, ref origin)) return false;
        return SetGameArea(origin.x, origin.y, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    // Sets the client area in screen pixels, recalculating the detection pixels
    // when it moves or resizes.
    public bool SetGameArea(int x, int y, int width, int height)
    {
        if (width < MinGameAreaSize || height < MinGameAreaSize) return false;

        if (x != _areaX || y != _areaY || width != _areaWidth || height != _areaHeight)
        {
            (_areaX, _areaY, _areaWidth, _areaHeight) = (x, y, width, height);
            _layout = PixelLayout.For(width, height);
            _logger.Log($"Game area: {width}x{height} at ({x},{y})");
        }
        return true;
    }

    public bool ColorsMatch(uint pixel, int r, int g, int b)
    {
        if (pixel == ClrInvalid) return false;
        int pr = (int)(pixel & 0xFF);
        int pg = (int)((pixel >> 8) & 0xFF);
        int pb = (int)((pixel >> 16) & 0xFF);
        int tolerance = _config.ColorTolerance;
        return Math.Abs(pr - r) <= tolerance && Math.Abs(pg - g) <= tolerance && Math.Abs(pb - b) <= tolerance;
    }

    // Single raw sample of the current dialogue state (no debounce).
    public bool Detect()
    {
        int playingX = _areaX + _layout.PlayingIcon.x;
        int playingY = _areaY + _layout.PlayingIcon.y;
        uint pPlaying = GetPixelWithRetry(playingX, playingY);
        bool isPlaying = ColorsMatch(pPlaying, 236, 229, 216);

        if (_logger.IsVerbose)
            _logger.LogDebug(() => $"Playing pixel: {Rgb(pPlaying)} @ ({playingX},{playingY}) -> {isPlaying}");

        if (isPlaying) return true;

        uint pLoad = GetPixelWithRetry(_areaX + _layout.LoadingPixel.x, _areaY + _layout.LoadingPixel.y);
        if (ColorsMatch(pLoad, 255, 255, 255)) return false;

        int choiceX = _areaX + _layout.DialogueIcon.x;
        uint pLow = GetPixelWithRetry(choiceX, _areaY + _layout.DialogueIcon.ly);
        uint pHigh = GetPixelWithRetry(choiceX, _areaY + _layout.DialogueIcon.hy);
        bool isChoice = ColorsMatch(pLow, 255, 255, 255) || ColorsMatch(pHigh, 255, 255, 255);

        if (_logger.IsVerbose)
            _logger.LogDebug(() => $"Choice pixels: Low={Rgb(pLow)}, High={Rgb(pHigh)} -> {isChoice}");

        return isChoice;
    }

    private uint GetPixelWithRetry(int x, int y, int maxRetries = 3)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            uint pixel = Native.GetPixel(_hdc, x, y);
            if (pixel != ClrInvalid) return pixel;
            if (i < maxRetries - 1) Thread.Sleep(1);
        }
        return ClrInvalid;
    }

    private static string Rgb(uint pixel) => $"RGB({pixel & 0xFF},{(pixel >> 8) & 0xFF},{(pixel >> 16) & 0xFF})";
}
