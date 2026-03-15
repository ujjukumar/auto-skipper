using System.Text.RegularExpressions;

namespace AutoSkipper.UI;

public class LogForm : Form
{
    private readonly RichTextBox _richTextBox;
    private readonly CheckBox _autoScrollCheck;
    private bool _autoScroll = true;
    private bool _allowClose = false;
    // Updated regex to catch [/] and [bold red] etc.
    private static readonly Regex _markupRegex = new Regex(@"\[/?[^\]]*\]", RegexOptions.Compiled);

    // VS Code-like Dark Theme Colors
    private readonly Color _bgColor = Color.FromArgb(30, 30, 30);      // #1E1E1E
    private readonly Color _panelColor = Color.FromArgb(37, 37, 38);   // #252526
    private readonly Color _fgColor = Color.FromArgb(204, 204, 204);   // #CCCCCC
    private readonly Color _accentColor = Color.FromArgb(0, 122, 204); // #007ACC

    public LogForm()
    {
        this.Text = "AutoSkipper Logs";
        this.Size = new Size(700, 500);
        this.Icon = SystemIcons.Application;
        this.ShowInTaskbar = false;
        this.BackColor = _bgColor;
        this.ForeColor = _fgColor;

        // --- Toolbar ---
        var toolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = _panelColor,
            Padding = new Padding(10, 0, 10, 0)
        };

        var btnClear = CreateDarkButton("Clear", (s, e) => _richTextBox!.Clear());
        btnClear.Location = new Point(10, 8);
        
        _autoScrollCheck = new CheckBox
        {
            Text = "Auto-Scroll",
            Checked = true,
            AutoSize = true,
            Location = new Point(btnClear.Right + 15, 11),
            ForeColor = _fgColor,
            Font = new Font("Segoe UI", 9)
        };
        _autoScrollCheck.CheckedChanged += (s, e) => _autoScroll = _autoScrollCheck.Checked;

        toolbar.Controls.Add(btnClear);
        toolbar.Controls.Add(_autoScrollCheck);

        // --- Log Area Container (for Padding) ---
        var logContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10), // Nice breathing room
            BackColor = _bgColor
        };

        _richTextBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = _bgColor,
            ForeColor = _fgColor,
            Font = new Font("Cascadia Mono", 10), // Verified available
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical
        };

        logContainer.Controls.Add(_richTextBox);

        this.Controls.Add(logContainer);
        this.Controls.Add(toolbar);

        // --- Setup ---
        // Force handle creation
        var _ = this.Handle;
        Services.Logger.OnLogMessage += Logger_OnLogMessage;
    }

    private Button CreateDarkButton(string text, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = _fgColor,
            BackColor = Color.FromArgb(60, 60, 60),
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = _accentColor;
        btn.Click += onClick;
        return btn;
    }

    private void Logger_OnLogMessage(DateTime timestamp, Services.Logger.LogLevel level, string message)
    {
        if (_richTextBox.IsDisposed || !_richTextBox.IsHandleCreated) return;

        if (_richTextBox.InvokeRequired)
        {
            _richTextBox.BeginInvoke(new Action(() => Logger_OnLogMessage(timestamp, level, message)));
            return;
        }

        Color color = level switch
        {
            Services.Logger.LogLevel.Info => Color.FromArgb(86, 156, 214),    // Blue
            Services.Logger.LogLevel.Debug => Color.FromArgb(128, 128, 128),  // Gray
            Services.Logger.LogLevel.Error => Color.FromArgb(244, 71, 71),    // Red
            Services.Logger.LogLevel.Success => Color.FromArgb(106, 153, 85), // Green
            Services.Logger.LogLevel.Warning => Color.FromArgb(206, 145, 120),// Orange/Peach
            _ => _fgColor
        };

        _richTextBox.SelectionStart = _richTextBox.TextLength;
        _richTextBox.SelectionLength = 0;

        _richTextBox.SelectionColor = Color.Gray;
        _richTextBox.AppendText($"{timestamp:HH:mm:ss} ");

        _richTextBox.SelectionColor = color;
        _richTextBox.AppendText($" {level.ToString().ToUpper().PadRight(7)} ");
        
        string cleanMessage = _markupRegex.Replace(message, "");
        _richTextBox.SelectionColor = _fgColor;
        _richTextBox.AppendText($"{cleanMessage}\n");

        if (_autoScroll)
        {
            _richTextBox.SelectionStart = _richTextBox.TextLength;
            _richTextBox.ScrollToCaret();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_allowClose)
        {
            e.Cancel = true;
            this.Hide();
        }
        else
        {
            Services.Logger.OnLogMessage -= Logger_OnLogMessage;
            base.OnFormClosing(e);
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        this.Close();
    }
}