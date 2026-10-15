using AutoSkipper.Models;

namespace AutoSkipper.UI;

// The app window, with a Logs tab and a Macros tab. Closing or minimising it
// hides it back to the tray.
internal sealed class MainForm : Form
{
    private readonly LogView _logView;
    private readonly MacroEditorView _macroEditor;
    private readonly Button _tabLogs;
    private readonly Button _tabMacros;
    private bool _allowClose = false;

    public MainForm(ConfigStore store, Action<IReadOnlyList<Macro>> applyMacros, Icon? icon = null)
    {
        Text = "AutoSkipper";
        Size = new Size(900, 580);
        MinimumSize = new Size(840, 520);
        Icon = icon ?? SystemIcons.Application;
        ShowInTaskbar = false;
        BackColor = Theme.BackgroundColor;
        ForeColor = Theme.ForegroundColor;
        Font = Theme.UiFont;

        var tabBar = new Panel
        {
            Dock = DockStyle.Top, Height = 36, BackColor = Theme.PanelColor,
            Padding = new Padding(8, 0, 0, 0)
        };
        _tabLogs = Theme.CreateTabButton("Logs", true);
        _tabMacros = Theme.CreateTabButton("Macros", false);
        _tabLogs.Location = new Point(8, 6);
        _tabMacros.Location = new Point(_tabLogs.Right + 4, 6);
        _tabLogs.Click += (s, e) => SwitchTab(showLogs: true);
        _tabMacros.Click += (s, e) => SwitchTab(showLogs: false);
        tabBar.Controls.Add(_tabLogs);
        tabBar.Controls.Add(_tabMacros);

        _logView = new LogView();
        _macroEditor = new MacroEditorView(store, applyMacros) { Visible = false };

        // Reverse dock order: the tab bar docks first, the active view fills the rest.
        Controls.Add(_logView);
        Controls.Add(_macroEditor);
        Controls.Add(tabBar);

        // Force handle creation so the window appears instantly from the tray.
        var _ = Handle;

        // Hide to tray on minimize instead of leaving a minimized window.
        Resize += (s, e) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
                WindowState = FormWindowState.Normal;
            }
        };
    }

    public bool HasUnsavedChanges => _macroEditor.HasUnsavedChanges;

    public Task<bool> SaveMacrosAsync() => _macroEditor.SaveMacrosAsync();

    private void SwitchTab(bool showLogs)
    {
        _logView.Visible = showLogs;
        _macroEditor.Visible = !showLogs;
        Theme.SetTabActive(_tabLogs, showLogs);
        Theme.SetTabActive(_tabMacros, !showLogs);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            base.OnFormClosing(e);
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }
}
