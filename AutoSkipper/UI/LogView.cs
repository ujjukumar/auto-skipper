using System.Collections.Concurrent;
using System.Text;
using AutoSkipper.Services;
using LogLevel = AutoSkipper.Services.Logger.LogLevel;

namespace AutoSkipper.UI;

// The Logs tab. The logger thread only queues lines; a UI timer appends them in
// batches. Whenever this view isn't on screen (window hidden or the Macros tab
// selected) it is detached from the logger entirely, and coming back replays
// the logger's history.
internal sealed class LogView : UserControl
{
    private const int MaxLogLines = 2000;
    private const int LogTrimSlack = 200;
    private const int LogFlushIntervalMs = 100;

    private readonly record struct LogLine(DateTime Timestamp, LogLevel Level, string Message);
    private static readonly string[] _levelLabels = Enum.GetValues<LogLevel>().Select(l => Logger.LevelName(l).PadRight(7)).ToArray();

    private readonly ConcurrentQueue<LogLine> _pendingLogs = new();
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = LogFlushIntervalMs };
    private bool _viewerAttached = false;
    private long _clearedBeforeTicks = 0;
    private readonly RichTextBox _richTextBox;
    private readonly CheckBox _autoScrollCheck;
    private bool _autoScroll = true;

    // RTF colour table: 1 = timestamp, 2.. = one per log level, then message text.
    private const int RtfTimestampColor = 1;
    private const int RtfLevelColorBase = 2;
    private static readonly int RtfMessageColor = RtfLevelColorBase + Enum.GetValues<LogLevel>().Length;
    private string? _rtfHeader;

    public LogView()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.BackgroundColor;

        var toolbar = new Panel
        {
            Dock = DockStyle.Top, Height = 40,
            BackColor = Theme.PanelColor, Padding = new Padding(10, 0, 10, 0)
        };
        var btnClear = Theme.CreateButton("Clear", (s, e) => ClearLogs());
        btnClear.Location = new Point(10, 8);
        _autoScrollCheck = new CheckBox
        {
            Text = "Auto-Scroll", Checked = true, AutoSize = true,
            Location = new Point(btnClear.Right + 15, 11),
            ForeColor = Theme.ForegroundColor, Font = Theme.UiFont
        };
        _autoScrollCheck.CheckedChanged += (s, e) =>
        {
            _autoScroll = _autoScrollCheck.Checked;
            if (_autoScroll) ScrollLogToEnd();
        };
        toolbar.Controls.Add(btnClear);
        toolbar.Controls.Add(_autoScrollCheck);

        var container = new Panel
        {
            Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Theme.BackgroundColor
        };
        _richTextBox = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true,
            BackColor = Theme.BackgroundColor, ForeColor = Theme.ForegroundColor,
            Font = Theme.MonoFont,
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical
        };
        // Read-only log: no need for RichEdit to keep an undo history of every append.
        _richTextBox.HandleCreated += (s, e) => Native.SendMessage(_richTextBox.Handle, Native.EM_SETUNDOLIMIT, 0, 0);
        container.Controls.Add(_richTextBox);

        Controls.Add(container);
        Controls.Add(toolbar);

        _logTimer.Tick += (s, e) => FlushPendingLogs();
    }

    // Raised when this control or a parent becomes visible, or this control is
    // hidden directly (e.g. switching tabs).
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateViewerAttachment();
    }

    // WinForms doesn't raise VisibleChanged on children when their window is
    // hidden, so also follow the host window directly.
    private Form? _hostForm;

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        var form = FindForm();
        if (form == _hostForm) return;
        if (_hostForm != null) _hostForm.VisibleChanged -= HostVisibleChanged;
        _hostForm = form;
        if (_hostForm != null) _hostForm.VisibleChanged += HostVisibleChanged;
    }

    private void HostVisibleChanged(object? sender, EventArgs e) => UpdateViewerAttachment();

    private void UpdateViewerAttachment()
    {
        if (Visible) AttachLogViewer();
        else DetachLogViewer();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_hostForm != null) _hostForm.VisibleChanged -= HostVisibleChanged;
            DetachLogViewer();
            _logTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    // Called on the logger thread (and on the UI thread while replaying history).
    private void OnLogMessage(DateTime timestamp, LogLevel level, string message)
    {
        if (timestamp.Ticks < Interlocked.Read(ref _clearedBeforeTicks)) return;
        _pendingLogs.Enqueue(new LogLine(timestamp, level, message));
    }

    private void AttachLogViewer()
    {
        if (_viewerAttached) return;
        _viewerAttached = true;
        _pendingLogs.Clear();
        _richTextBox.Clear();
        Logger.RegisterViewer(OnLogMessage); // replays history, then streams live
        FlushPendingLogs();
        _logTimer.Start();
    }

    private void DetachLogViewer()
    {
        if (!_viewerAttached) return;
        _viewerAttached = false;
        _logTimer.Stop();
        Logger.UnregisterViewer(OnLogMessage);
        _pendingLogs.Clear();
    }

    private void ClearLogs()
    {
        // Also hides these entries from the history replay when the view comes back.
        Interlocked.Exchange(ref _clearedBeforeTicks, DateTime.Now.Ticks);
        _pendingLogs.Clear();
        _richTextBox.Clear();
    }

    private static Color LevelColor(LogLevel level) => level switch
    {
        LogLevel.Info => Color.FromArgb(86, 156, 214),
        LogLevel.Debug => Color.FromArgb(128, 128, 128),
        LogLevel.Error => Color.FromArgb(244, 71, 71),
        LogLevel.Success => Color.FromArgb(106, 153, 85),
        LogLevel.Warning => Color.FromArgb(206, 145, 120),
        _ => Theme.ForegroundColor
    };

    // Appends all queued lines as one RTF insert with redraw suspended, then
    // trims the oldest lines so the control never grows past MaxLogLines.
    // (One RTF insert is ~70x faster than colouring each run via selection.)
    private void FlushPendingLogs()
    {
        if (_pendingLogs.IsEmpty || _richTextBox.IsDisposed) return;

        // Lines beyond the cap would be trimmed right away; don't render them.
        for (int excess = _pendingLogs.Count - MaxLogLines; excess > 0; excess--)
            _pendingLogs.TryDequeue(out _);

        var rtf = new StringBuilder(4096);
        rtf.Append(_rtfHeader ??= BuildRtfHeader());
        while (_pendingLogs.TryDequeue(out var line))
        {
            rtf.Append(@"\cf").Append(RtfTimestampColor).Append(' ').Append(line.Timestamp.ToString("HH:mm:ss")).Append("  ");
            rtf.Append(@"\cf").Append(RtfLevelColorBase + (int)line.Level).Append(' ').Append(_levelLabels[(int)line.Level]);
            rtf.Append(@"\cf").Append(RtfMessageColor).Append("  ");
            AppendRtfEscaped(rtf, line.Message);
            rtf.Append(@"\par ");
        }
        rtf.Append('}');

        var rtb = _richTextBox;
        IntPtr hwnd = rtb.Handle;
        var scrollPos = new Native.POINT();
        if (!_autoScroll) Native.SendMessage(hwnd, Native.EM_GETSCROLLPOS, 0, ref scrollPos);
        Native.SendMessage(hwnd, Native.WM_SETREDRAW, 0, 0);
        try
        {
            rtb.Select(rtb.TextLength, 0);
            rtb.SelectedRtf = rtf.ToString();
            TrimLog();
            rtb.Select(rtb.TextLength, 0);
        }
        finally
        {
            Native.SendMessage(hwnd, Native.WM_SETREDRAW, 1, 0);
            if (_autoScroll) rtb.ScrollToCaret();
            else Native.SendMessage(hwnd, Native.EM_SETSCROLLPOS, 0, ref scrollPos);
            rtb.Invalidate();
        }
    }

    private string BuildRtfHeader()
    {
        var sb = new StringBuilder(@"{\rtf1\ansi\deff0{\fonttbl{\f0 ");
        sb.Append(_richTextBox.Font.Name).Append(@";}}{\colortbl;");
        AppendRtfColor(sb, Color.Gray);
        foreach (var level in Enum.GetValues<LogLevel>())
            AppendRtfColor(sb, LevelColor(level));
        AppendRtfColor(sb, Theme.ForegroundColor);
        sb.Append(@"}\f0\fs").Append((int)Math.Round(_richTextBox.Font.SizeInPoints * 2)).Append(' ');
        return sb.ToString();
    }

    private static void AppendRtfColor(StringBuilder sb, Color c) =>
        sb.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');

    private static void AppendRtfEscaped(StringBuilder sb, string text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\n': sb.Append(@"\line "); break;
                case '\t': sb.Append(@"\tab "); break;
                case < ' ': break; // drops \r and other control characters
                case < (char)0x80: sb.Append(c); break;
                default: sb.Append(@"\u").Append((short)c).Append('?'); break; // signed 16-bit, per RTF spec
            }
        }
    }

    private void TrimLog()
    {
        int lines = _richTextBox.GetLineFromCharIndex(_richTextBox.TextLength);
        if (lines <= MaxLogLines + LogTrimSlack) return;
        int cut = _richTextBox.GetFirstCharIndexFromLine(lines - MaxLogLines);
        if (cut <= 0) return;
        // RichEdit silently ignores deletions while read-only, so lift it briefly.
        _richTextBox.ReadOnly = false;
        _richTextBox.Select(0, cut);
        _richTextBox.SelectedText = "";
        _richTextBox.ReadOnly = true;
    }

    private void ScrollLogToEnd()
    {
        _richTextBox.Select(_richTextBox.TextLength, 0);
        _richTextBox.ScrollToCaret();
    }
}
