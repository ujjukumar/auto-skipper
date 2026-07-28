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
    private Icon _defaultIcon = SystemIcons.Application;
    private Icon _activeIcon = SystemIcons.Asterisk;
    private bool _cleanupDone = false;
    private readonly ILogger _logger;

    public AutoSkipperContext(ScreenConfig config)
    {
        _logger = new Services.Logger();
        LoadIcons();
        
        _notifyIcon = new NotifyIcon
        {
            Icon = _defaultIcon,
            Text = "Genshin AutoSkipper - Ready",
            Visible = true
        };

        var macroService = new Services.MacroService(new Input.InputSender(), _logger);
        
        _skipper = new Services.AutoSkipper(config, _logger, new Input.InputSender());
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

        _logForm = new LogForm(config.Config.Macros, _hooks, config, _defaultIcon);

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
        
        _logger.LogSuccess("AutoSkipper started minimized to tray.");
        _logger.Log("Use F8/F9 or Tray Icon to control.");
    }

    private void LoadIcons()
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
                _activeIcon = _defaultIcon;
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
                if (embedded != null)
                {
                    _defaultIcon = embedded;
                    _activeIcon = embedded;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to extract embedded icon: {ex.Message}");
        }
    }

    private void OnDialogueStateChanged(bool inDialogue)
    {
        _notifyIcon.Icon = inDialogue ? _activeIcon : _defaultIcon;
        UpdateTooltip();
    }

    private void OnRunningStateChanged(bool running)
    {
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        string status = _skipper.IsRunning ? "Running" : "Paused";
        _notifyIcon.Text = $"Genshin AutoSkipper - {status}";
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

    private void Exit(object? sender, EventArgs e)
    {
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
        }
        base.Dispose(disposing);
    }
}
