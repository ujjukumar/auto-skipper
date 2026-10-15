using System.Diagnostics;
using AutoSkipper.Interfaces;
using AutoSkipper.Models;

namespace AutoSkipper.Services;

internal class AutoSkipper : IDisposable
{
    public event Action<bool>? OnDialogueStateChanged;
    public event Action<bool>? OnRunningStateChanged;
    
    private readonly ScreenConfig _cfg;
    private readonly ILogger _logger;
    private readonly IInputSender _inputSender;
    private volatile bool _running = false;
    public bool IsRunning => _running;
    private volatile bool _shouldExit = false;
    public bool ShouldExit
    {
        get => _shouldExit;
        set => _shouldExit = value;
    }
    // Screen DC, acquired on the logic thread in Run().
    private IntPtr _hdc;
    private CancellationTokenSource? _spamCancel;
    private readonly object _spamLock = new();
    private readonly object _toggleLock = new();
    // Auto-reset so a Wake() that arrives while the loop isn't waiting is kept
    // for the next wait instead of being lost.
    private readonly AutoResetEvent _wakeEvent = new(false);

    // State
    private int _burstPool = 0;
    private bool _skipNext = false;
    private bool _doubleNext = false;
    private bool _burstMode = false;
    private int _burstRemaining = 0;
    private double _postBurstPauseUntil = 0.0;
    private double _breakUntil = 0.0;
    private double _lastBreakCheck = 0.0;
    private const double BreakInterval = 30.0;
    private const double FastBurstMultiplier = 0.5;
    private const double StateCheckInterval = 0.15;
    private const double ActiveDialogueCheckInterval = 0.25;
    private const double IdleSleepInterval = 0.25;
    private const double MaxSleepInterval = 0.35;
    private const double PostBurstPauseBase = 0.4;
    private const double PostBurstPauseRandom = 0.6;
    private const double SkipNextChance = 1.0 / 40.0;
    private const double DoubleNextChance = 1.0 / 35.0;
    private const double BurstModeChance = 1.0 / 60.0;
    private const int BurstPoolMin = 2;
    private const int BurstPoolMax = 6;
    private const int BurstRemainingMin = 3;
    private const int BurstRemainingMax = 6;
    private const double WindowCheckInterval = 0.4;
    private const double FastBurstChance = 1.0 / 40.0;
    private const double BreakChance = 0.015;
    private const double BreakDurationMin = 2.0;
    private const double BreakDurationMax = 8.0;
    private const int BurstModeDelayMs = 120;
    private static readonly TimeSpan MouseBurstDuration = TimeSpan.FromSeconds(4);
    private const int InactivePauseSeconds = 120;

    // GetPixel returns CLR_INVALID on failure. It must never count as a colour
    // match, otherwise a failed read looks like a white dialogue-choice pixel.
    private const uint ClrInvalid = 0xFFFFFFFF;

    // Dialogue debounce: how many consecutive raw pixel readings are required
    // before the authoritative dialogue state flips. This suppresses single-frame
    // false positives. Presses still stop on the first negative reading, because
    // every press also requires a fresh positive raw reading.
    private const int DialogueEnterConfirm = 2;
    private const int DialogueExitConfirm = 2;

    private bool _inDialogue = false;
    private bool _lastRawDialogue = false;
    private int _dialoguePositiveStreak = 0;
    private int _dialogueNegativeStreak = 0;
    private bool _windowActive = false;

    // The game's client area in screen pixels, and the detection pixels for its size.
    // Tracking the window (not the primary screen) supports windowed mode, other
    // monitors and resolution changes.
    private const int MinGameAreaSize = 200;
    private int _areaX, _areaY, _areaWidth, _areaHeight;
    private PixelLayout _layout;

    // Counter State (shared between the logic thread and ToggleRun callers)
    private readonly object _counterLock = new();
    private int _pressCount = 0;
    private double _sessionStartTime = 0.0;
    private double _inactiveSince = 0.0;

    public AutoSkipper(ScreenConfig cfg, ILogger logger, IInputSender inputSender)
    {
        _cfg = cfg;
        _logger = logger;
        _inputSender = inputSender;
        _spamCancel = new CancellationTokenSource();
    }

    public void Dispose()
    {
        _spamCancel?.Cancel();
        _spamCancel?.Dispose();
        GC.SuppressFinalize(this);
    }

    public bool IsGameActive() => TryGetGameWindow(out _);

    private bool TryGetGameWindow(out IntPtr hwnd)
    {
        hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        Span<char> buffer = stackalloc char[256];
        int length = Native.GetWindowText(hwnd, buffer, buffer.Length);
        ReadOnlySpan<char> windowTitle = buffer[..length];

        return windowTitle.Equals(_cfg.Config.WindowTitle.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    // Refreshes the game's client area and recalculates the detection pixels when
    // it moves or resizes. Returns false if there is no usable area (e.g. minimised).
    private bool UpdateGameArea(IntPtr hwnd)
    {
        if (!Native.GetClientRect(hwnd, out var rect)) return false;
        var origin = new Native.POINT();
        if (!Native.ClientToScreen(hwnd, ref origin)) return false;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width < MinGameAreaSize || height < MinGameAreaSize) return false;

        if (origin.x != _areaX || origin.y != _areaY || width != _areaWidth || height != _areaHeight)
        {
            (_areaX, _areaY, _areaWidth, _areaHeight) = (origin.x, origin.y, width, height);
            _layout = PixelLayout.For(width, height);
            _logger.Log($"Game area: {width}x{height} at ({origin.x},{origin.y})");
        }
        return true;
    }

    public bool ColorsMatch(uint pixel, int r, int g, int b)
    {
        if (pixel == ClrInvalid) return false;
        int pr = (int)(pixel & 0xFF);
        int pg = (int)((pixel >> 8) & 0xFF);
        int pb = (int)((pixel >> 16) & 0xFF);
        int tolerance = _cfg.Config.ColorTolerance;
        return Math.Abs(pr - r) <= tolerance && Math.Abs(pg - g) <= tolerance && Math.Abs(pb - b) <= tolerance;
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

    private static double GetTime() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // Single raw pixel sample of the current dialogue state (no debounce).
    // Pixel positions are relative to the game's client area.
    private bool DetectDialogueRaw()
    {
        int playingX = _areaX + _layout.PlayingIcon.x;
        int playingY = _areaY + _layout.PlayingIcon.y;
        uint pPlaying = GetPixelWithRetry(playingX, playingY);
        bool isPlaying = ColorsMatch(pPlaying, 236, 229, 216);

        if (_logger.IsVerbose)
            _logger.LogDebug(() => $"Playing pixel: RGB({(pPlaying & 0xFF)},{((pPlaying >> 8) & 0xFF)},{((pPlaying >> 16) & 0xFF)}) @ ({playingX},{playingY}) -> {isPlaying}");

        if (isPlaying) return true;

        uint pLoad = GetPixelWithRetry(_areaX + _layout.LoadingPixel.x, _areaY + _layout.LoadingPixel.y);
        if (ColorsMatch(pLoad, 255, 255, 255)) return false;

        int choiceX = _areaX + _layout.DialogueIcon.x;
        uint pLow = GetPixelWithRetry(choiceX, _areaY + _layout.DialogueIcon.ly);
        uint pHigh = GetPixelWithRetry(choiceX, _areaY + _layout.DialogueIcon.hy);
        bool isChoice = ColorsMatch(pLow, 255, 255, 255) || ColorsMatch(pHigh, 255, 255, 255);

        if (_logger.IsVerbose)
            _logger.LogDebug(() => $"Choice pixels: Low=RGB({(pLow & 0xFF)},{((pLow >> 8) & 0xFF)},{((pLow >> 16) & 0xFF)}), High=RGB({(pHigh & 0xFF)},{((pHigh >> 8) & 0xFF)},{((pHigh >> 16) & 0xFF)}) -> {isChoice}");

        return isChoice;
    }

    private double NextKeyInterval()
    {
        if (_burstPool > 0)
        {
            _burstPool--;
            return FastBurstInterval();
        }
        
        // Chance to enter fast burst mode
        if (Random.Shared.NextDouble() < FastBurstChance)
        {
            _burstPool = Random.Shared.Next(BurstPoolMin, BurstPoolMax);
            return FastBurstInterval();
        }

        // Standard speed
        return _cfg.Config.StandardDelayMin + Random.Shared.NextDouble() * (_cfg.Config.StandardDelayMax - _cfg.Config.StandardDelayMin);
    }

    // Fast burst: 50% of standard speed
    private double FastBurstInterval()
    {
        double min = _cfg.Config.StandardDelayMin * FastBurstMultiplier;
        double max = _cfg.Config.StandardDelayMax * FastBurstMultiplier;
        return min + Random.Shared.NextDouble() * (max - min);
    }

    public void Run()
    {
        // Per-monitor DPI awareness for this thread, so the game's window rect and
        // screen pixels are physical pixels on every monitor regardless of the UI's
        // DPI mode. The screen DC is acquired after, on this thread.
        Native.SetThreadDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _hdc = Native.GetDC(IntPtr.Zero);
        _logger.Log("Ready.");
        
        double lastPressTime = GetTime();
        double nextInterval = NextKeyInterval();
        double nextStateCheck = 0.0;
        _lastBreakCheck = GetTime();

        try
        {
            while (!ShouldExit)
            {
                double now = GetTime();

                if (!_running)
                {
                    // Paused: block until ToggleRun or shutdown wakes us.
                    _wakeEvent.WaitOne();
                    lastPressTime = GetTime();
                    continue;
                }

                bool isActive = TryGetGameWindow(out IntPtr gameWindow);

                if (isActive)
                {
                    _inactiveSince = 0.0;
                }
                else
                {
                    if (_inactiveSince == 0.0) _inactiveSince = now;
                    if (now - _inactiveSince > InactivePauseSeconds)
                    {
                        _logger.LogWarning($"Paused due to inactivity ({InactivePauseSeconds}s)");
                        ToggleRun(false);
                        _inactiveSince = 0.0;
                    }
                }

                if (isActive != _windowActive)
                {
                    _windowActive = isActive;
                    string countLog = TakeCountLog();
                    _logger.Log(isActive ? $"Window State: ACTIVE{countLog}" : $"Window State: INACTIVE{countLog}");

                    // Reset detection state when the game loses focus so stale
                    // dialogue state can't trigger a press when it returns.
                    if (!isActive)
                    {
                        _lastRawDialogue = false;
                        _dialoguePositiveStreak = 0;
                        _dialogueNegativeStreak = 0;
                        if (_inDialogue)
                        {
                            _inDialogue = false;
                            OnDialogueStateChanged?.Invoke(false);
                        }
                    }
                }

                if (!isActive)
                {
                    SleepUntil(now + WindowCheckInterval);
                    continue;
                }

                // Active break
                if (now < _breakUntil)
                {
                    SleepUntil(_breakUntil);
                    continue;
                }

                // Periodic break check
                if (now - _lastBreakCheck > BreakInterval)
                {
                    _lastBreakCheck = now;
                    if (Random.Shared.NextDouble() < BreakChance)
                    {
                        double dur = BreakDurationMin + Random.Shared.NextDouble() * (BreakDurationMax - BreakDurationMin);
                        string countLog = TakeCountLog();
                        _logger.LogWarning($"Break: {dur:F1}s{countLog}");
                        _breakUntil = now + dur;
                        nextInterval = NextKeyInterval();
                        continue;
                    }
                }

                // Compute whether a press is due so we can force a fresh
                // dialogue check in the same iteration, right before pressing.
                bool actionDue = (now - lastPressTime) >= nextInterval || _burstMode;

                // Dialogue detection
                if (now >= nextStateCheck || actionDue)
                {
                    bool rawDialogue = UpdateGameArea(gameWindow) && DetectDialogueRaw();
                    _lastRawDialogue = rawDialogue;

                    if (rawDialogue)
                    {
                        _dialoguePositiveStreak++;
                        _dialogueNegativeStreak = 0;
                    }
                    else
                    {
                        _dialogueNegativeStreak++;
                        _dialoguePositiveStreak = 0;
                    }

                    // Debounced transitions: only flip authoritative state after
                    // enough consecutive confirming readings.
                    if (!_inDialogue && _dialoguePositiveStreak >= DialogueEnterConfirm)
                    {
                        _inDialogue = true;
                        OnDialogueStateChanged?.Invoke(true);
                        string countLog = TakeCountLog();
                        _logger.Log($"Dialogue State: DETECTED{countLog}");
                    }
                    else if (_inDialogue && _dialogueNegativeStreak >= DialogueExitConfirm)
                    {
                        _inDialogue = false;
                        OnDialogueStateChanged?.Invoke(false);
                        string countLog = TakeCountLog();
                        _logger.Log($"Dialogue State: ENDED{countLog}");

                        // Drop any pending humanization/burst state when dialogue ends so
                        // it can't carry over and fire presses into the next dialogue.
                        _burstMode = false;
                        _burstRemaining = 0;
                        _skipNext = false;
                        _doubleNext = false;
                        _burstPool = 0;
                    }

                    nextStateCheck = now + (_inDialogue ? ActiveDialogueCheckInterval : StateCheckInterval);
                    if (!_inDialogue)
                    {
                        SleepUntil(now + IdleSleepInterval);
                        continue;
                    }
                }

                // Post-burst pause
                if (now < _postBurstPauseUntil)
                {
                    SleepUntil(_postBurstPauseUntil);
                    continue;
                }

                // Action. A press only fires when the debounced state says we are in
                // dialogue AND the most recent raw reading (taken this iteration when
                // actionDue) still confirms it, so presses cannot trail past the end
                // of a dialogue.
                if (actionDue && _inDialogue && _lastRawDialogue)
                {
                    double r1 = Random.Shared.NextDouble();
                    double r2 = Random.Shared.NextDouble();
                    double r3 = Random.Shared.NextDouble();

                    if (!_skipNext && r1 < SkipNextChance) _skipNext = true;
                    if (!_doubleNext && r2 < DoubleNextChance) _doubleNext = true;
                    if (!_burstMode && r3 < BurstModeChance)
                    {
                        _burstMode = true;
                        _burstRemaining = Random.Shared.Next(BurstRemainingMin, BurstRemainingMax);
                        string countLog = TakeCountLog();
                        _logger.LogWarning($"Burst mode: {_burstRemaining}{countLog}");
                    }

                    if (_skipNext)
                    {
                        _skipNext = false;
                        lastPressTime = now;
                        nextInterval = NextKeyInterval();
                    }
                    else
                    {
                        PerformPress(now);
                        lastPressTime = now;
                        nextInterval = NextKeyInterval();
                    }
                }

                double nextActionTime = lastPressTime + nextInterval;
                double wakeTarget = nextActionTime;
                if (_breakUntil > now && _breakUntil < wakeTarget) wakeTarget = _breakUntil;
                if (nextStateCheck < wakeTarget) wakeTarget = nextStateCheck;
                if (_postBurstPauseUntil > now && _postBurstPauseUntil < wakeTarget) wakeTarget = _postBurstPauseUntil;

                SleepUntil(Math.Min(wakeTarget, now + MaxSleepInterval));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"An unexpected error occurred in the main loop: {ex.Message}");
            _logger.Log(ex.ToString());
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, _hdc);
            _hdc = IntPtr.Zero;
            _logger.Log("Closing Logic Loop");
        }
    }
    
    private void PerformPress(double now)
    {
        bool useClick = Random.Shared.NextDouble() < (_burstMode ? 0.0 : _cfg.Config.LeftClickChance);
        if (useClick) _inputSender.LeftClick(); else _inputSender.TapF();
        if (_logger.IsVerbose)
            _logger.LogDebug(() => $"Press: {(useClick ? "LeftClick" : "F")}");
        
        if (!useClick)
        {
            CountPress();
        }
        
        if (!useClick && _doubleNext)
        {
            _doubleNext = false;
            _inputSender.TapF();
            CountPress();
            _postBurstPauseUntil = now + PostBurstPauseBase + Random.Shared.NextDouble() * PostBurstPauseRandom;

            _logger.LogDebug(() => "Press: F (double)");
        }

        if (_burstMode)
        {
            _burstRemaining--;
            if (_burstRemaining <= 0)
            {
                _burstMode = false;
                _postBurstPauseUntil = now + PostBurstPauseBase + Random.Shared.NextDouble() * PostBurstPauseRandom;
            }
        }
    }
    
    private void SleepUntil(double targetTime)
    {
        if (ShouldExit) return;
        double now = GetTime();
        double timeout = Math.Max(0.0, targetTime - now);
        int ms = (int)(timeout * 1000);
        if (ms > 0)
        {
            _wakeEvent.WaitOne(ms);
        }
    }

    public void Wake() => _wakeEvent.Set();

    public void ToggleRun(bool on) 
    { 
        lock (_toggleLock)
        {
            if (_running == on) return;
            _running = on; 
            OnRunningStateChanged?.Invoke(on);
            string countLog = on ? "" : TakeCountLog();
            _logger.Log(on ? "RUN" : $"PAUSE{countLog}"); 
            Wake(); 
        }
    }
    
    public void HandleMouse5()
    {
        _logger.LogDebug(() => "Mouse5 detected");
        if (!IsGameActive()) return;
        lock (_spamLock)
        {
            _spamCancel?.Cancel();
            _spamCancel?.Dispose();
            _spamCancel = new CancellationTokenSource();
            var token = _spamCancel.Token;
            
            Task.Run(async () =>
            {
                _logger.LogWarning("Burst Start");
                int presses = 0;
                double start = GetTime();
                using var timeoutCts = new CancellationTokenSource(MouseBurstDuration);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
                try
                {
                    while (!linkedCts.IsCancellationRequested)
                    {
                        if (IsGameActive())
                        {
                            _inputSender.TapF();
                            presses++;
                        }
                        await Task.Delay(BurstModeDelayMs, linkedCts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                _logger.LogWarning($"Burst End. Total F presses: {presses} in {GetTime() - start:F1}s");
            }, token);
        }
    }

    private void CountPress()
    {
        lock (_counterLock) _pressCount++;
    }

    // Returns the press-count suffix for a log line and starts a new count.
    private string TakeCountLog()
    {
        lock (_counterLock)
        {
            double now = GetTime();
            string log = _pressCount > 0 ? $". Total F presses: {_pressCount} in {now - _sessionStartTime:F1}s" : "";
            _pressCount = 0;
            _sessionStartTime = now;
            return log;
        }
    }
}
