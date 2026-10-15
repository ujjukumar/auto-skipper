using System.Diagnostics;
using AutoSkipper.Interfaces;
using AutoSkipper.Models;
using AutoSkipper.Input;
using AutoSkipper.Services;

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
    private readonly IntPtr _hdc;
    private CancellationTokenSource? _spamCancel;
    private readonly object _spamLock = new();
    private readonly ManualResetEventSlim _wakeEvent = new(false);

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
    private const int WaitEventTimeout = 500;
    private const double WindowCheckInterval = 0.4;
    private const double FastBurstChance = 1.0 / 40.0;
    private const double BreakChance = 0.015;
    private const double BreakDurationMin = 2.0;
    private const double BreakDurationMax = 8.0;
    private const int BurstModeDelayMs = 120;
    private const int InactivePauseSeconds = 120;

    // Dialogue debounce: how many consecutive raw pixel readings are required
    // before the authoritative dialogue state flips. Entering requires more
    // confirmations to suppress single-frame false positives; exiting stays
    // responsive so presses stop quickly when a dialogue actually ends.
    private const int DialogueEnterConfirm = 2;
    private const int DialogueExitConfirm = 2;

    private bool _inDialogue = false;
    private bool _lastRawDialogue = false;
    private int _dialoguePositiveStreak = 0;
    private int _dialogueNegativeStreak = 0;
    private bool _windowActive = false;
    private static ReadOnlySpan<char> GenshinWindowTitle => "Genshin Impact".AsSpan();

    // Counter State
    private int _pressCount = 0;
    private double _sessionStartTime = 0.0;
    private double _inactiveSince = 0.0;

    public AutoSkipper(ScreenConfig cfg, ILogger logger, IInputSender inputSender)
    {
        _cfg = cfg;
        _logger = logger;
        _inputSender = inputSender;
        _hdc = Native.GetDC(IntPtr.Zero);
        _spamCancel = new CancellationTokenSource();
    }

    public void Dispose()
    {
        _spamCancel?.Cancel();
        _spamCancel?.Dispose();
        Native.ReleaseDC(IntPtr.Zero, _hdc);
        GC.SuppressFinalize(this);
    }

    public bool IsGameActive()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        Span<char> buffer = stackalloc char[256];
        int length = Native.GetWindowText(hwnd, buffer, buffer.Length);
        var windowTitleSpan = buffer[..length];

        return windowTitleSpan.Equals(GenshinWindowTitle, StringComparison.OrdinalIgnoreCase);
    }

    public bool ColorsMatch(uint pixel, int r, int g, int b)
    {
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
            if (pixel != uint.MaxValue) return pixel;
            if (i < maxRetries - 1) Thread.Sleep(1);
        }
        return uint.MaxValue;
    }

    private static double GetTime() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // Single raw pixel sample of the current dialogue state (no debounce).
    private bool DetectDialogueRaw()
    {
        uint pPlaying = GetPixelWithRetry(_cfg.PLAYING_ICON.x, _cfg.PLAYING_ICON.y);
        bool isPlaying = ColorsMatch(pPlaying, 236, 229, 216);

        if (_logger.IsVerbose)
            _logger.LogDebug(() => $"Playing pixel: RGB({(pPlaying & 0xFF)},{((pPlaying >> 8) & 0xFF)},{((pPlaying >> 16) & 0xFF)}) @ ({_cfg.PLAYING_ICON.x},{_cfg.PLAYING_ICON.y}) -> {isPlaying}");

        if (isPlaying) return true;

        uint pLoad = GetPixelWithRetry(_cfg.LOADING_PIXEL.x, _cfg.LOADING_PIXEL.y);
        if (ColorsMatch(pLoad, 255, 255, 255)) return false;

        uint pLow = GetPixelWithRetry(_cfg.DIALOGUE_ICON.x, _cfg.DIALOGUE_ICON.ly);
        uint pHigh = GetPixelWithRetry(_cfg.DIALOGUE_ICON.x, _cfg.DIALOGUE_ICON.hy);
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
                    _wakeEvent.Wait(WaitEventTimeout);
                    _wakeEvent.Reset();
                    lastPressTime = GetTime();
                    continue;
                }

                bool isActive = IsGameActive();

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
                    string countLog = GetCountLog();
                    ResetCounter();
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
                        string countLog = GetCountLog();
                        ResetCounter();
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
                    bool rawDialogue = DetectDialogueRaw();
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
                        string countLog = GetCountLog();
                        ResetCounter();
                        _logger.Log($"Dialogue State: DETECTED{countLog}");
                    }
                    else if (_inDialogue && _dialogueNegativeStreak >= DialogueExitConfirm)
                    {
                        _inDialogue = false;
                        OnDialogueStateChanged?.Invoke(false);
                        string countLog = GetCountLog();
                        ResetCounter();
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
                        string countLog = GetCountLog();
                        ResetCounter();
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
            _pressCount++;
        }
        
        if (!useClick && _doubleNext)
        {
            _doubleNext = false;
            _inputSender.TapF();
            _pressCount++;
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
            _wakeEvent.Wait(ms);
            _wakeEvent.Reset();
        }
    }

    public void Wake() => _wakeEvent.Set();

    public void ToggleRun(bool on) 
    { 
        if (_running != on)
        {
            _running = on; 
            OnRunningStateChanged?.Invoke(on);
            string countLog = "";
            if (!on) 
            {
                countLog = GetCountLog();
                ResetCounter();
            }
            _logger.Log(on ? "RUN" : $"PAUSE{countLog}"); 
            Wake(); 
        }
    }
    
    public void HandleMouse4()
    {
        _logger.LogDebug(() => "Mouse4 detected");
        if (IsGameActive()) { _inputSender.TapT(); _logger.Log("Remap: T"); }
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
                lock (_spamLock)
                {
                    _pressCount = 0;
                    _sessionStartTime = GetTime();
                }
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, cts.Token);
                while (!linkedCts.IsCancellationRequested)
                {
                    if (IsGameActive()) 
                    {
                        _inputSender.TapF();
                        lock (_spamLock)
                        {
                            _pressCount++;
                        }
                    }
                    try
                    {
                        await Task.Delay(BurstModeDelayMs, linkedCts.Token);
                    }
                    catch (TaskCanceledException) { break; }
                }
                lock (_spamLock)
                {
                    _pressCount = 0;
                    _sessionStartTime = GetTime();
                }
                _logger.LogWarning("Burst End");
            }, token);
        }
    }

    private string GetCountLog()
    {
        if (_pressCount > 0)
        {
            double duration = GetTime() - _sessionStartTime;
            return $". Total F presses: {_pressCount} in {duration:F1}s";
        }
        return "";
    }

    private void ResetCounter()
    {
        _pressCount = 0;
        _sessionStartTime = GetTime();
    }
}
