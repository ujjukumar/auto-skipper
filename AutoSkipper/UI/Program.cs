using System.Drawing.Drawing2D;
using AutoSkipper.Interfaces;
using AutoSkipper.Models;

namespace AutoSkipper.UI;

internal class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool verbose = false;
        foreach (var arg in args)
        {
            if (arg == "-v" || arg == "--verbose") verbose = true;
        }
        
        Services.Logger.SetVerbose(verbose);
        
        ApplicationConfiguration.Initialize();

        try
        {
            var config = ScreenConfig.CreateAsync().GetAwaiter().GetResult();
            using var context = new AutoSkipperContext(config);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "AutoSkipper Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal class AutoSkipperContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Services.AutoSkipper _skipper;
    private readonly Input.GlobalHooks? _hooks;
    private readonly Thread? _logicThread;
    private readonly LogForm _logForm;
    private readonly SynchronizationContext _ui;
    private Icon _defaultIcon = SystemIcons.Application;
    private Icon _activeIcon = SystemIcons.Application;
    private bool _inDialogue = false;
    private bool _cleanupDone = false;
    private readonly ILogger _logger;

    public AutoSkipperContext(ScreenConfig config)
    {
        _logger = new Services.Logger();
        LoadIcons();
        
        _notifyIcon = new NotifyIcon
        {
            Icon = _defaultIcon,
            Text = "Genshin AutoSkipper",
            Visible = true
        };

        var inputSender = new Input.InputSender();
        var macroService = new Services.MacroService(inputSender, _logger);
        
        _skipper = new Services.AutoSkipper(config, _logger, inputSender);
        try
        {
            _hooks = new Input.GlobalHooks(_skipper, macroService, config.Config.Macros);
        }
        catch (Exception ex)
        {
            _skipper.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            throw new InvalidOperationException($"Failed to initialize global hooks: {ex.Message}", ex);
        }

        _logForm = new LogForm(config, _hooks, _defaultIcon);

        // Skipper events can fire on the logic thread; tray updates are posted to
        // the UI thread. Creating the form above installed the WinForms context.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var contextMenu = new ContextMenuStrip();
        
        var startItem = new ToolStripMenuItem("Start/Resume (F8)", null, (s, e) => _skipper!.ToggleRun(true));
        var pauseItem = new ToolStripMenuItem("Pause (F9)", null, (s, e) => _skipper!.ToggleRun(false));
        var toggleLogItem = new ToolStripMenuItem("Toggle Log File (F7)", null, (s, e) => Services.Logger.ToggleFileLogging());
        var showLogsItem = new ToolStripMenuItem("Show Logs", null, (s, e) => ShowLogs());
        var exitItem = new ToolStripMenuItem("Exit", null, Exit);

        contextMenu.Items.Add(startItem);
        contextMenu.Items.Add(pauseItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(showLogsItem);
        contextMenu.Items.Add(toggleLogItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
        
        _notifyIcon.DoubleClick += (s, e) => _skipper!.ToggleRun(!_skipper!.IsRunning);

        _skipper.OnDialogueStateChanged += OnDialogueStateChanged;
        _skipper.OnRunningStateChanged += OnRunningStateChanged;

        _logicThread = new Thread(_skipper.Run)
        {
            IsBackground = true
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

    private void LoadDefaultIcon()
    {
        string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoSkipper");
        string iconPath = Path.Combine(appDataPath, "auto_marker.ico");
        if (!File.Exists(iconPath))
        {
            iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "auto_marker.ico");
        }
        if (File.Exists(iconPath))
        {
            try
            {
                using var stream = File.OpenRead(iconPath);
                _defaultIcon = new Icon(stream);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to load icon: {ex.Message}");
            }
        }

        // Fallback: use the icon embedded in the executable (ApplicationIcon).
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
    private static Icon CreateActiveIcon(Icon baseIcon)
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
            Services.Native.DestroyIcon(hIcon);
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

    private void ShowLogs()
    {
        if (_logForm.Visible)
        {
            _logForm.Hide();
        }
        else
        {
            if (_logForm.WindowState == FormWindowState.Minimized)
                _logForm.WindowState = FormWindowState.Normal;
            _logForm.Show();
            _logForm.BringToFront();
            _logForm.Activate();
        }
    }

    private async void Exit(object? sender, EventArgs e)
    {
        if (_logForm.HasUnsavedChanges)
        {
            var result = MessageBox.Show("You have unsaved macro changes. Save them before exiting?",
                "AutoSkipper", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (result == DialogResult.Cancel) return;
            if (result == DialogResult.Yes && !await _logForm.SaveMacrosAsync()) return;
        }

        Cleanup();
        Application.Exit();
    }

    private void Cleanup()
    {
        if (_cleanupDone) return;
        _cleanupDone = true;

        _notifyIcon.Visible = false;
        _logForm.ForceClose();

        _skipper.ShouldExit = true;
        _skipper.Wake();

        if (_logicThread?.IsAlive == true)
        {
            _logicThread.Join(1000);
        }

        _hooks?.Dispose();
        _skipper.Dispose();
        Services.Logger.Shutdown();
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
