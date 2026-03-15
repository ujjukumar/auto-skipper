using System.Diagnostics;
using Spectre.Console;
using AutoSkipper.Interfaces;
using AutoSkipper.Models;

namespace AutoSkipper.Services;

internal enum BreakType
{
    None,
    Short,
    Long
}

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
    private const double BurstModeSpaceChance = 0.1;
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
    private const double SpaceKeyChance = 0.1;
    private const int BurstModeDelayMs = 120;
    private const int InactivePauseSeconds = 120;
    private bool _inDialogue = false;
    private bool _windowActive = false;
    private static ReadOnlySpan<char> GenshinWindowTitle => "Genshin Impact".AsSpan();

    // Counter State
    private int _pressCount = 0;
    private double _sessionStartTime = 0.0;
    private double _inactiveSince = 0.0;
    private StatusContext? _statusContext;

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

    private double NextKeyInterval()
    {
        if (_burstPool > 0)
        {
            _burstPool--;
            // Fast burst: 50% of standard speed
            double min = _cfg.Config.StandardDelayMin * FastBurstMultiplier;
            double max = _cfg.Config.StandardDelayMax * FastBurstMultiplier;
            return min + Random.Shared.NextDouble() * (max - min);
        }
        
        // Chance to enter fast burst mode
        if (Random.Shared.NextDouble() < FastBurstChance)
        {
            _burstPool = Random.Shared.Next(BurstPoolMin, BurstPoolMax);
            double min = _cfg.Config.StandardDelayMin * FastBurstMultiplier;
            double max = _cfg.Config.StandardDelayMax * FastBurstMultiplier;
            return min + Random.Shared.NextDouble() * (max - min);
        }

        // Standard speed
        return _cfg.Config.StandardDelayMin + Random.Shared.NextDouble() * (_cfg.Config.StandardDelayMax - _cfg.Config.StandardDelayMin);
    }

    private BreakType MaybeBreak()
    {
        // Simplified break logic: just one type of break, but we keep the enum for now
        if (Random.Shared.NextDouble() < BreakChance) return BreakType.Short;
        return BreakType.None;
    }

    private double BreakDuration(BreakType kind) => 
        BreakDurationMin + Random.Shared.NextDouble() * (BreakDurationMax - BreakDurationMin);

    public void Run()
    {
        AnsiConsole.Status()
            .Spinner(Spinner.Known.SimpleDots) // Fallback, but we will override style
            .SpinnerStyle(Style.Parse("black")) // Hide spinner by making it black (or match bg)
            .Start("Waiting for input...", ctx =>
            {
                ctx.Spinner(new NoSpinner()); // Use custom no-op spinner
                _statusContext = ctx;
                RunInternal();
            });
    }

    // Custom spinner that does nothing
    private class NoSpinner : Spinner
    {
        public override TimeSpan Interval => TimeSpan.FromMilliseconds(1000);
        public override bool IsUnicode => false;
        public override IReadOnlyList<string> Frames => new[] { " " };
    }

    private void RunInternal()
    {
        _logger.Log("Ready.");
        UpdateStatus("Ready");
        
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
                    UpdateStatus("Paused");
                    // Do not call ResetCounter here repeatedly
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
                        _logger.Log($"[yellow]Paused due to inactivity ({InactivePauseSeconds}s)[/]");
                        ToggleRun(false);
                        _inactiveSince = 0.0;
                    }
                }

                if (isActive != _windowActive)
                {
                    _windowActive = isActive;
                    string countLog = GetCountLog();
                    ResetCounter();
                    _logger.Log(isActive ? $"Window State: [green]ACTIVE[/]{countLog}" : $"Window State: [red]INACTIVE[/]{countLog}");
                }

                if (!isActive)
                {
                    UpdateStatus("Waiting for Window...");
                    SleepUntil(now + WindowCheckInterval);
                    continue;
                }

                // Active break
                if (now < _breakUntil)
                {
                    UpdateStatus($"Taking a break... ({_breakUntil - now:F1}s)");
                    SleepUntil(_breakUntil);
                    continue;
                }

                // Periodic break check
                if (now - _lastBreakCheck > BreakInterval)
                {
                    _lastBreakCheck = now;
                    BreakType br = MaybeBreak();
                    if (br != BreakType.None)
                    {
                        double dur = BreakDuration(br);
                        string countLog = GetCountLog();
                        ResetCounter();
                        _logger.Log($"Break: [yellow]{br}[/] {dur:F1}s{countLog}");
                        _breakUntil = now + dur;
                        nextInterval = NextKeyInterval();
                        continue;
                    }
                }

                // Dialogue detection
                if (now >= nextStateCheck)
                {
                    uint pPlaying = GetPixelWithRetry(_cfg.PLAYING_ICON.x, _cfg.PLAYING_ICON.y);
                    bool isPlaying = ColorsMatch(pPlaying, 236, 229, 216);

                    _logger.LogDebug(() => $"Playing pixel: RGB({(pPlaying & 0xFF)},{((pPlaying >> 8) & 0xFF)},{((pPlaying >> 16) & 0xFF)}) @ ({_cfg.PLAYING_ICON.x},{_cfg.PLAYING_ICON.y}) -> {isPlaying}");
                    
                    bool isChoice = false;
                    if (!isPlaying)
                    {
                        uint pLoad = GetPixelWithRetry(_cfg.LOADING_PIXEL.x, _cfg.LOADING_PIXEL.y);
                        if (!ColorsMatch(pLoad, 255, 255, 255))
                        {
                            uint pLow = GetPixelWithRetry(_cfg.DIALOGUE_ICON.x, _cfg.DIALOGUE_ICON.ly);
                            uint pHigh = GetPixelWithRetry(_cfg.DIALOGUE_ICON.x, _cfg.DIALOGUE_ICON.hy);
                            isChoice = ColorsMatch(pLow, 255, 255, 255) || ColorsMatch(pHigh, 255, 255, 255);

                            _logger.LogDebug(() => $"Choice pixels: Low=RGB({(pLow & 0xFF)},{((pLow >> 8) & 0xFF)},{((pLow >> 16) & 0xFF)}), High=RGB({(pHigh & 0xFF)},{((pHigh >> 8) & 0xFF)},{((pHigh >> 16) & 0xFF)}) -> {isChoice}");
                        }
                    }

                    bool isDialogue = isPlaying || isChoice;
                    if (isDialogue != _inDialogue)
                    {
                        _inDialogue = isDialogue;
                        OnDialogueStateChanged?.Invoke(isDialogue);
                        string countLog = GetCountLog();
                        ResetCounter();
                        _logger.Log(isDialogue ? $"Dialogue State: [green]DETECTED[/]{countLog}" : $"Dialogue State: [yellow]ENDED[/]{countLog}");
                    }

                    nextStateCheck = now + (isDialogue ? ActiveDialogueCheckInterval : StateCheckInterval);
                    if (!isDialogue)
                    {
                        UpdateStatus("Idle (No Dialogue)");
                        SleepUntil(now + IdleSleepInterval);
                        continue;
                    }
                }

                // Post-burst pause
                if (now < _postBurstPauseUntil)
                {
                    UpdateStatus("Pausing after burst...");
                    SleepUntil(_postBurstPauseUntil);
                    continue;
                }

                // Action
                bool actionDue = (now - lastPressTime) >= nextInterval || _burstMode;
                if (actionDue)
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
                        _logger.Log($"Burst mode: [red]{_burstRemaining}[/]{countLog}");
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
                else
                {
                    UpdateStatus($"Skipping... [green]{_pressCount}[/] presses");
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
        bool useSpace = Random.Shared.NextDouble() < (_burstMode ? BurstModeSpaceChance : SpaceKeyChance);
        string key = useSpace ? "Space" : "F";
        if (useSpace) _inputSender.TapSpace(); else _inputSender.TapF();
        _logger.LogDebug(() => $"Press: {key}");
        
        if (!useSpace)
        {
            _pressCount++;
            UpdateStatus($"Skipping... [green]{_pressCount}[/] presses");
        }
        
        if (!useSpace && _doubleNext)
        {
            _doubleNext = false;
            _inputSender.TapF();
            _pressCount++;
            UpdateStatus($"Skipping... [green]{_pressCount}[/] presses");
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
                UpdateStatus("Paused");
            }
            _logger.Log(on ? "[bold green]RUN[/]" : $"[bold yellow]PAUSE[/]{countLog}"); 
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
                UpdateStatus("Bursting...");
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
                        UpdateStatus($"Bursting...");
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
                UpdateStatus("Ready");
            }, token);
        }
    }

    private string GetCountLog()
    {
        if (_pressCount > 0)
        {
            double duration = GetTime() - _sessionStartTime;
            return $". Total F presses: [cyan]{_pressCount}[/] in [cyan]{duration:F1}s[/]";
        }
        return "";
    }

    private void ResetCounter()
    {
        _pressCount = 0;
        _sessionStartTime = GetTime();
        UpdateStatus("Ready");
    }

    private void UpdateStatus(string status)
    {
        if (_statusContext != null)
        {
            _statusContext.Status(status);
        }
    }
}
