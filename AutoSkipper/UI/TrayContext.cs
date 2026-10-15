using System.Drawing.Drawing2D;
using AutoSkipper.Input;
using AutoSkipper.Interfaces;
using AutoSkipper.Models;
using AutoSkipper.Services;

namespace AutoSkipper.UI;

// Owns the app's lifetime: tray icon and menu, the skipper's logic thread, the
// input hooks and the main window.
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly DialogueSkipper _skipper;
    private readonly GlobalHooks _hooks;
    private readonly Thread _logicThread;
    private readonly MainForm _window;
    private readonly SynchronizationContext _ui;
    private readonly EventWaitHandle _showWindowEvent;
    private readonly RegisteredWaitHandle _showWindowWait;
    private Icon _defaultIcon = SystemIcons.Application;
    private Icon _activeIcon = SystemIcons.Application;
    private bool _inDialogue = false;
    private bool _cleanupDone = false;
    private readonly ILogger _logger;

    public TrayContext(ConfigStore store)
    {
        _logger = new Logger();
        LoadIcons();
        
        _notifyIcon = new NotifyIcon
        {
            Icon = _defaultIcon,
            Text = "Genshin AutoSkipper",
            Visible = true
        };

        var inputSender = new InputSender();
        var macroService = new MacroService(inputSender, _logger);
        _skipper = new DialogueSkipper(store.Config, _logger, inputSender);

        var router = new InputRouter(
            isGameActive: _skipper.IsGameActive,
            runMacro: macroService.Run,
            // Opening/closing the log file is disk I/O; keep it off the hook thread.
            toggleFileLogging: () => ThreadPool.QueueUserWorkItem(_ => Logger.ToggleFileLogging()),
            setRunning: _skipper.ToggleRun,
            mouse5Burst: _skipper.HandleMouse5);
        router.SetMacros(store.Config.Macros);

        try
        {
            _hooks = new GlobalHooks(router);
        }
        catch (Exception ex)
        {
            _skipper.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            throw new InvalidOperationException($"Failed to initialize global hooks: {ex.Message}", ex);
        }

        _window = new MainForm(store, router.SetMacros, _defaultIcon);

        // Skipper events can fire on the logic thread; tray updates are posted to
        // the UI thread. Creating the form above installed the WinForms context.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        // Launching the app again signals this event (see Program.ActivateRunningInstance).
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowWindowEventName);
        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(_showWindowEvent,
            (_, _) => _ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(new ToolStripMenuItem("Start/Resume (F8)", null, (s, e) => _skipper.ToggleRun(true)));
        contextMenu.Items.Add(new ToolStripMenuItem("Pause (F9)", null, (s, e) => _skipper.ToggleRun(false)));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Show Window", null, (s, e) => ToggleWindow()));
        contextMenu.Items.Add(new ToolStripMenuItem("Toggle Log File (F7)", null, (s, e) => Logger.ToggleFileLogging()));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, Exit));
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => _skipper.ToggleRun(!_skipper.IsRunning);

        _skipper.OnDialogueStateChanged += OnDialogueStateChanged;
        _skipper.OnRunningStateChanged += OnRunningStateChanged;

        _logicThread = new Thread(_skipper.Run)
        {
            IsBackground = true,
            Name = "AutoSkipper Logic"
        };
        _logicThread.Start();

        UpdateTray();
        _logger.LogSuccess("AutoSkipper started minimized to tray.");
        _logger.Log("Use F8/F9 or Tray Icon to control.");
    }

    private void LoadIcons()
    {
        LoadDefaultIcon();
        try
        {
            _activeIcon = CreateActiveIcon(_defaultIcon);
        }
        catch (Exception ex)
        {
            _activeIcon = _defaultIcon;
            _logger.LogWarning($"Failed to create active icon: {ex.Message}");
        }
    }

    // A custom icon in the data folder wins; otherwise use the exe's own icon.
    private void LoadDefaultIcon()
    {
        if (File.Exists(AppPaths.CustomIcon))
        {
            try
            {
                using var stream = File.OpenRead(AppPaths.CustomIcon);
                _defaultIcon = new Icon(stream);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to load icon: {ex.Message}");
            }
        }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                Icon? embedded = Icon.ExtractAssociatedIcon(exePath);
                if (embedded != null) _defaultIcon = embedded;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to extract embedded icon: {ex.Message}");
        }
    }

    // The default icon with a green dot in the corner, shown while skipping dialogue.
    internal static Icon CreateActiveIcon(Icon baseIcon)
    {
        using var bmp = baseIcon.ToBitmap();
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Math.Max(6, bmp.Width * 9 / 20);
            var dot = new Rectangle(bmp.Width - d - 1, bmp.Height - d - 1, d, d);
            using var fill = new SolidBrush(Color.FromArgb(0, 200, 83));
            using var outline = new Pen(Color.FromArgb(20, 20, 20), Math.Max(1f, bmp.Width / 16f));
            g.FillEllipse(fill, dot);
            g.DrawEllipse(outline, dot);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(hIcon);
        }
    }

    private void OnDialogueStateChanged(bool inDialogue)
    {
        _ui.Post(_ =>
        {
            _inDialogue = inDialogue;
            UpdateTray();
        }, null);
    }

    private void OnRunningStateChanged(bool running)
    {
        _ui.Post(_ => UpdateTray(), null);
    }

    private void UpdateTray()
    {
        if (_cleanupDone) return;
        bool running = _skipper.IsRunning;
        _notifyIcon.Icon = running && _inDialogue ? _activeIcon : _defaultIcon;
        _notifyIcon.Text = !running ? "Genshin AutoSkipper - Paused (F8 to start)"
            : _inDialogue ? "Genshin AutoSkipper - Skipping dialogue"
            : "Genshin AutoSkipper - Running";
    }

    private void ToggleWindow()
    {
        if (_window.Visible) _window.Hide();
        else ShowWindow();
    }

    private void ShowWindow()
    {
        if (_cleanupDone) return;
        if (_window.WindowState == FormWindowState.Minimized)
            _window.WindowState = FormWindowState.Normal;
        _window.Show();
        _window.BringToFront();
        _window.Activate();
    }

    private async void Exit(object? sender, EventArgs e)
    {
        if (_window.HasUnsavedChanges)
        {
            var result = MessageBox.Show("You have unsaved macro changes. Save them before exiting?",
                "AutoSkipper", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (result == DialogResult.Cancel) return;
            if (result == DialogResult.Yes && !await _window.SaveMacrosAsync()) return;
        }

        Cleanup();
        Application.Exit();
    }

    private void Cleanup()
    {
        if (_cleanupDone) return;
        _cleanupDone = true;

        _showWindowWait.Unregister(null);
        _showWindowEvent.Dispose();
        _notifyIcon.Visible = false;
        _window.ForceClose();

        _skipper.ShouldExit = true;
        _skipper.Wake();
        if (_logicThread.IsAlive) _logicThread.Join(1000);

        _hooks.Dispose();
        _skipper.Dispose();
        Logger.Shutdown();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Cleanup();
            _notifyIcon.Dispose();
            if (!ReferenceEquals(_activeIcon, _defaultIcon)) _activeIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
