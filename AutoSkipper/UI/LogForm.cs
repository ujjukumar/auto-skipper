using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics;
using AutoSkipper.Input;
using AutoSkipper.Models;

namespace AutoSkipper.UI;

internal class LogForm : Form
{
    // --- Core state ---
    private readonly List<Macro> _macros;
    private readonly GlobalHooks _hooks;
    private readonly ScreenConfig _config;
    private Macro? _selectedMacro;
    private readonly List<MacroAction> _editingActions = new();
    private bool _allowClose = false;

    // --- Theme ---
    private readonly Color _bgColor = Color.FromArgb(30, 30, 30);
    private readonly Color _panelColor = Color.FromArgb(37, 37, 38);
    private readonly Color _fgColor = Color.FromArgb(204, 204, 204);
    private readonly Color _accentColor = Color.FromArgb(0, 122, 204);
    private readonly Color _btnColor = Color.FromArgb(60, 60, 60);
    private readonly Color _borderColor = Color.FromArgb(50, 50, 50);
    private readonly Color _fieldBg = Color.FromArgb(45, 45, 45);

    private static readonly Regex _markupRegex = new(@"\[/?[^\]]*\]", RegexOptions.Compiled);

    // --- Logs tab ---
    private readonly RichTextBox _richTextBox;
    private readonly CheckBox _autoScrollCheck;
    private bool _autoScroll = true;

    // --- Tab bar ---
    private Button _tabLogs = null!;
    private Button _tabMacros = null!;
    private Panel _logContainer = null!;
    private Panel _macroContainer = null!;

    // --- Macro list (left) ---
    private ListBox _macroList = null!;
    private TextBox _nameBox = null!;
    private CheckBox _chkCtrl = null!;
    private CheckBox _chkShift = null!;
    private CheckBox _chkAlt = null!;
    private ComboBox _triggerCombo = null!;

    // --- Action editor (right) ---
    private ListView _actionListView = null!;
    private Label _actionsHeader = null!;
    private Button _btnRecordActions = null!;
    private Button _btnSave = null!;

    // --- Recording state ---
    private Stopwatch? _recordStopwatch;
    private long _lastActionTimeMs;

    public LogForm(List<Macro> macros, GlobalHooks hooks, ScreenConfig config)
    {
        _macros = macros;
        _hooks = hooks;
        _config = config;

        this.Text = "AutoSkipper";
        this.Size = new Size(820, 560);
        this.MinimumSize = new Size(720, 480);
        this.Icon = SystemIcons.Application;
        this.ShowInTaskbar = false;
        this.BackColor = _bgColor;
        this.ForeColor = _fgColor;
        this.Font = new Font("Segoe UI", 9);

        // === Toolbar ===
        var toolbar = new Panel
        {
            Dock = DockStyle.Top, Height = 40,
            BackColor = _panelColor, Padding = new Padding(10, 0, 10, 0)
        };
        var btnClear = CreateButton("Clear", (s, e) => _richTextBox!.Clear());
        btnClear.Location = new Point(10, 8);
        _autoScrollCheck = new CheckBox
        {
            Text = "Auto-Scroll", Checked = true, AutoSize = true,
            Location = new Point(btnClear.Right + 15, 11),
            ForeColor = _fgColor, Font = new Font("Segoe UI", 9)
        };
        _autoScrollCheck.CheckedChanged += (s, e) => _autoScroll = _autoScrollCheck.Checked;
        toolbar.Controls.Add(btnClear);
        toolbar.Controls.Add(_autoScrollCheck);

        // === Tab Bar ===
        var tabBar = new Panel
        {
            Dock = DockStyle.Top, Height = 36, BackColor = _panelColor,
            Padding = new Padding(8, 0, 0, 0)
        };
        _tabLogs = CreateTabButton("Logs", true);
        _tabMacros = CreateTabButton("Macros", false);
        _tabLogs.Location = new Point(8, 6);
        _tabMacros.Location = new Point(_tabLogs.Right + 4, 6);
        _tabLogs.Click += (s, e) => SwitchTab(isLogs: true);
        _tabMacros.Click += (s, e) => SwitchTab(isLogs: false);
        tabBar.Controls.Add(_tabLogs);
        tabBar.Controls.Add(_tabMacros);

        // === Log Container ===
        _logContainer = new Panel
        {
            Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = _bgColor
        };
        _richTextBox = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true,
            BackColor = _bgColor, ForeColor = _fgColor,
            Font = new Font("Cascadia Mono", 10),
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical
        };
        _logContainer.Controls.Add(_richTextBox);

        // === Macro Container (hidden initially) ===
        _macroContainer = new Panel
        {
            Dock = DockStyle.Fill, BackColor = _bgColor, Visible = false
        };
        BuildMacroUI();

        // === Assembly order (reverse dock order) ===
        this.Controls.Add(_logContainer);
        this.Controls.Add(_macroContainer);
        this.Controls.Add(tabBar);
        this.Controls.Add(toolbar);

        // Force handle creation & hook logger
        var _ = this.Handle;
        Services.Logger.OnLogMessage += Logger_OnLogMessage;

        // Hook recording events
        _hooks.OnActionInputRecorded += OnActionInputRecorded;
        _hooks.OnRecordingStopped += OnRecordingStopped;
    }

    // ==================== Tab Switching ====================

    private void SwitchTab(bool isLogs)
    {
        _logContainer.Visible = isLogs;
        _macroContainer.Visible = !isLogs;
        StyleTabButton(_tabLogs, isLogs);
        StyleTabButton(_tabMacros, !isLogs);
    }

    private Button CreateTabButton(string text, bool active)
    {
        var btn = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
            ForeColor = _fgColor, BackColor = active ? _accentColor : _panelColor,
            Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Padding = new Padding(8, 2, 8, 2)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = _accentColor;
        return btn;
    }

    private void StyleTabButton(Button btn, bool active)
    {
        btn.BackColor = active ? _accentColor : _panelColor;
    }

    // ==================== Macro UI Build ====================

    private void BuildMacroUI()
    {
        // --- Left: Macro List + Properties ---
        var leftPanel = new Panel
        {
            Dock = DockStyle.Left, Width = 270, BackColor = _panelColor,
            Padding = new Padding(10)
        };

        var leftLabel = new Label
        {
            Text = "MACROS", Dock = DockStyle.Top, Height = 24,
            ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Padding = new Padding(0, 4, 0, 0)
        };

        _macroList = new ListBox
        {
            Dock = DockStyle.Fill, BackColor = _bgColor, ForeColor = _fgColor,
            BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5f),
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28
        };
        _macroList.DrawItem += MacroList_DrawItem;
        _macroList.SelectedIndexChanged += MacroList_SelectedIndexChanged;

        // Properties panel
        var propsPanel = new Panel
        {
            Dock = DockStyle.Bottom, Height = 140, BackColor = _panelColor,
            Padding = new Padding(0, 8, 0, 0)
        };

        var lblName = new Label { Text = "Name:", AutoSize = true, ForeColor = _fgColor, Location = new Point(0, 6) };
        _nameBox = new TextBox
        {
            Location = new Point(60, 3), Width = 190, BackColor = _fieldBg,
            ForeColor = _fgColor, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9)
        };
        _nameBox.TextChanged += NameBox_TextChanged;

        var lblTrigger = new Label { Text = "Trigger:", AutoSize = true, ForeColor = _fgColor, Location = new Point(0, 38) };

        // Modifier checkboxes
        _chkCtrl = new CheckBox { Text = "Ctrl", AutoSize = true, ForeColor = _fgColor, Location = new Point(60, 37), Font = new Font("Segoe UI", 8.5f) };
        _chkShift = new CheckBox { Text = "Shift", AutoSize = true, ForeColor = _fgColor, Location = new Point(110, 37), Font = new Font("Segoe UI", 8.5f) };
        _chkAlt = new CheckBox { Text = "Alt", AutoSize = true, ForeColor = _fgColor, Location = new Point(165, 37), Font = new Font("Segoe UI", 8.5f) };
        _chkCtrl.CheckedChanged += TriggerCombo_Changed;
        _chkShift.CheckedChanged += TriggerCombo_Changed;
        _chkAlt.CheckedChanged += TriggerCombo_Changed;

        // Primary key combo
        var lblKey = new Label { Text = "Key:", AutoSize = true, ForeColor = _fgColor, Location = new Point(0, 65) };
        _triggerCombo = new ComboBox
        {
            Location = new Point(60, 62), Width = 190, BackColor = _fieldBg,
            ForeColor = Color.FromArgb(86, 156, 214), FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        PopulateTriggerCombo();
        _triggerCombo.SelectedIndexChanged += TriggerCombo_Changed;

        propsPanel.Controls.AddRange(new Control[] { lblName, _nameBox, lblTrigger, _chkCtrl, _chkShift, _chkAlt, lblKey, _triggerCombo });

        // Add/Remove buttons
        var btnBar = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = _panelColor };
        var btnAdd = CreateButton("+ Add", BtnAddMacro_Click);
        var btnRemove = CreateButton("- Remove", BtnRemoveMacro_Click);
        btnRemove.ForeColor = Color.FromArgb(244, 71, 71);
        btnAdd.Location = new Point(0, 6);
        btnRemove.Location = new Point(btnAdd.Right + 6, 6);
        btnBar.Controls.Add(btnAdd);
        btnBar.Controls.Add(btnRemove);

        leftPanel.Controls.Add(_macroList);
        leftPanel.Controls.Add(propsPanel);
        leftPanel.Controls.Add(btnBar);
        leftPanel.Controls.Add(leftLabel);

        // --- Splitter ---
        var splitter = new Panel { Dock = DockStyle.Left, Width = 2, BackColor = _borderColor };

        // --- Right: Action Editor ---
        var rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = _bgColor, Padding = new Padding(10) };

        _actionsHeader = new Label
        {
            Text = "Select a macro to edit actions", Dock = DockStyle.Top, Height = 28,
            ForeColor = Color.FromArgb(150, 150, 150),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Padding = new Padding(0, 4, 0, 0)
        };

        _actionListView = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            BackColor = _bgColor, ForeColor = _fgColor, BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5f), HeaderStyle = ColumnHeaderStyle.Nonclickable,
            GridLines = false, MultiSelect = false
        };
        _actionListView.Columns.Add("#", 36);
        _actionListView.Columns.Add("Type", 100);
        _actionListView.Columns.Add("Detail", 180);
        _actionListView.OwnerDraw = true;
        _actionListView.DrawColumnHeader += ActionListView_DrawColumnHeader;
        _actionListView.DrawItem += ActionListView_DrawItem;
        _actionListView.DrawSubItem += ActionListView_DrawSubItem;

        // Action button bar
        var actionBtnBar = new Panel { Dock = DockStyle.Bottom, Height = 80, BackColor = _bgColor, Padding = new Padding(0, 6, 0, 0) };

        _btnRecordActions = CreateButton("\u25CF Record", BtnRecordActions_Click);
        _btnRecordActions.Width = 90;
        _btnRecordActions.BackColor = Color.FromArgb(50, 50, 50);
        var btnAddAction = CreateButton("+ Add Action", BtnAddAction_Click);
        btnAddAction.Width = 100;
        var btnRemoveAction = CreateButton("Remove", BtnRemoveAction_Click);
        var btnMoveUp = CreateButton("\u25B2 Up", BtnMoveUp_Click);
        var btnMoveDown = CreateButton("\u25BC Down", BtnMoveDown_Click);

        // Row 1: Record, Add Action
        _btnRecordActions.Location = new Point(0, 0);
        btnAddAction.Location = new Point(_btnRecordActions.Right + 6, 0);

        // Row 2: Remove, Up, Down
        btnRemoveAction.Location = new Point(0, 34);
        btnMoveUp.Location = new Point(btnRemoveAction.Right + 6, 34);
        btnMoveDown.Location = new Point(btnMoveUp.Right + 6, 34);

        actionBtnBar.Controls.AddRange(new Control[] { _btnRecordActions, btnAddAction, btnRemoveAction, btnMoveUp, btnMoveDown });

        // Save/Cancel bar
        var saveBar = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = _bgColor };
        _btnSave = CreateButton("\u2714 Save Macro", BtnSave_Click);
        _btnSave.Width = 110;
        _btnSave.BackColor = Color.FromArgb(30, 100, 30);
        _btnSave.ForeColor = Color.FromArgb(180, 255, 180);
        var btnCancel = CreateButton("Cancel", BtnCancel_Click);
        _btnSave.Location = new Point(0, 6);
        btnCancel.Location = new Point(_btnSave.Right + 8, 6);
        saveBar.Controls.Add(_btnSave);
        saveBar.Controls.Add(btnCancel);

        rightPanel.Controls.Add(_actionListView);
        rightPanel.Controls.Add(actionBtnBar);
        rightPanel.Controls.Add(saveBar);
        rightPanel.Controls.Add(_actionsHeader);

        _macroContainer.Controls.Add(rightPanel);
        _macroContainer.Controls.Add(splitter);
        _macroContainer.Controls.Add(leftPanel);

        RefreshMacroList();
    }

    // ==================== Macro List ====================

    private void RefreshMacroList()
    {
        int sel = _macroList.SelectedIndex;
        _macroList.Items.Clear();
        foreach (var m in _macros)
        {
            _macroList.Items.Add($"{m.Name}  [{m.GetTriggerDisplay()}]");
        }
        if (sel >= 0 && sel < _macroList.Items.Count)
            _macroList.SelectedIndex = sel;
    }

    private void MacroList_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var bgBrush = new SolidBrush(selected ? _accentColor : _bgColor);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);
        using var fgBrush = new SolidBrush(_fgColor);
        var text = _macroList.Items[e.Index]?.ToString() ?? "";
        e.Graphics.DrawString(text, e.Font!, fgBrush, e.Bounds.X + 8, e.Bounds.Y + 5);
    }

    private void MacroList_SelectedIndexChanged(object? sender, EventArgs e)
    {
        int idx = _macroList.SelectedIndex;
        if (idx < 0 || idx >= _macros.Count)
        {
            _selectedMacro = null;
            _nameBox.Text = "";
            _chkCtrl.Checked = false;
            _chkShift.Checked = false;
            _chkAlt.Checked = false;
            _triggerCombo.SelectedIndex = 0;
            _editingActions.Clear();
            _actionsHeader.Text = "Select a macro to edit actions";
            RefreshActionList();
            return;
        }

        _selectedMacro = _macros[idx];
        _nameBox.Text = _selectedMacro.Name;
        LoadTriggerToUI(_selectedMacro.TriggerKeys);

        // Deep-copy actions for editing
        _editingActions.Clear();
        foreach (var a in _selectedMacro.Actions)
            _editingActions.Add(new MacroAction { Type = a.Type, KeyCode = a.KeyCode, DelayMs = a.DelayMs });

        _actionsHeader.Text = $"ACTIONS  —  {_selectedMacro.Name}";
        RefreshActionList();
    }

    private void NameBox_TextChanged(object? sender, EventArgs e)
    {
        if (_selectedMacro == null) return;
        _selectedMacro.Name = _nameBox.Text;
        // Update list display without losing selection
        int idx = _macroList.SelectedIndex;
        if (idx >= 0)
        {
            _macroList.Items[idx] = $"{_selectedMacro.Name}  [{_selectedMacro.GetTriggerDisplay()}]";
        }
        _actionsHeader.Text = $"ACTIONS  —  {_selectedMacro.Name}";
    }

    // ==================== Add/Remove Macro ====================

    private void BtnAddMacro_Click(object? sender, EventArgs e)
    {
        var macro = new Macro { Name = $"Macro {_macros.Count + 1}" };
        _macros.Add(macro);
        RefreshMacroList();
        _macroList.SelectedIndex = _macros.Count - 1;
    }

    private void BtnRemoveMacro_Click(object? sender, EventArgs e)
    {
        if (_selectedMacro == null) return;
        int idx = _macroList.SelectedIndex;
        _macros.Remove(_selectedMacro);
        _selectedMacro = null;
        RefreshMacroList();
        if (_macros.Count > 0)
            _macroList.SelectedIndex = Math.Min(idx, _macros.Count - 1);
    }

    // ==================== Trigger Key Selection ====================

    private static readonly ushort VK_LCONTROL = 0xA2;
    private static readonly ushort VK_LSHIFT = 0xA0;
    private static readonly ushort VK_LALT = 0xA4;

    private static readonly (string Name, ushort VK)[] _primaryKeys = BuildPrimaryKeyList();

    private static (string Name, ushort VK)[] BuildPrimaryKeyList()
    {
        var keys = new List<(string, ushort)> { ("None", 0) };

        // Letters A-Z
        for (ushort vk = 0x41; vk <= 0x5A; vk++)
            keys.Add((MacroAction.GetKeyName(vk), vk));

        // Digits 0-9
        for (ushort vk = 0x30; vk <= 0x39; vk++)
            keys.Add((MacroAction.GetKeyName(vk), vk));

        // F1-F12
        for (ushort vk = 0x70; vk <= 0x7B; vk++)
            keys.Add((MacroAction.GetKeyName(vk), vk));

        // Numpad 0-9
        for (ushort vk = 0x60; vk <= 0x69; vk++)
            keys.Add((MacroAction.GetKeyName(vk), vk));

        // Common keys
        ushort[] extras = { 0x08, 0x09, 0x0D, 0x1B, 0x20, 0x21, 0x22, 0x23, 0x24,
                            0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E,
                            0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE,
                            0x05, 0x06, 0x13, 0x14,
                            0x6A, 0x6B, 0x6D, 0x6E, 0x6F };
        foreach (var vk in extras)
            keys.Add((MacroAction.GetKeyName(vk), vk));

        // Mouse buttons
        keys.Add(("Left Click", 0x01));
        keys.Add(("Right Click", 0x02));
        keys.Add(("Middle Click", 0x04));

        return keys.ToArray();
    }

    private void PopulateTriggerCombo()
    {
        _triggerCombo.Items.Clear();
        foreach (var (name, _) in _primaryKeys)
            _triggerCombo.Items.Add(name);
        _triggerCombo.SelectedIndex = 0;
    }

    /// <summary>Load a TriggerKeys list into the modifier checkboxes + primary key combo.</summary>
    private bool _suppressTriggerUpdate = false;

    private void LoadTriggerToUI(List<ushort> keys)
    {
        _suppressTriggerUpdate = true;
        _chkCtrl.Checked = keys.Contains(VK_LCONTROL);
        _chkShift.Checked = keys.Contains(VK_LSHIFT);
        _chkAlt.Checked = keys.Contains(VK_LALT);

        // Find the primary key (not a modifier)
        var primary = keys.FirstOrDefault(k => k != VK_LCONTROL && k != VK_LSHIFT && k != VK_LALT);
        int idx = 0;
        for (int i = 0; i < _primaryKeys.Length; i++)
        {
            if (_primaryKeys[i].VK == primary) { idx = i; break; }
        }
        _triggerCombo.SelectedIndex = idx;
        _suppressTriggerUpdate = false;
    }

    /// <summary>Build TriggerKeys list from the current UI state.</summary>
    private List<ushort> BuildTriggerKeysFromUI()
    {
        var keys = new List<ushort>();
        if (_chkCtrl.Checked) keys.Add(VK_LCONTROL);
        if (_chkShift.Checked) keys.Add(VK_LSHIFT);
        if (_chkAlt.Checked) keys.Add(VK_LALT);

        int idx = _triggerCombo.SelectedIndex;
        if (idx > 0 && idx < _primaryKeys.Length)
            keys.Add(_primaryKeys[idx].VK);

        return keys;
    }

    private void TriggerCombo_Changed(object? sender, EventArgs e)
    {
        if (_suppressTriggerUpdate || _selectedMacro == null) return;
        var newKeys = BuildTriggerKeysFromUI();
        _selectedMacro.TriggerKeys = newKeys;
        RefreshMacroList();
    }

    // ==================== Action Recording ====================

    private void BtnRecordActions_Click(object? sender, EventArgs e)
    {
        if (_selectedMacro == null) return;

        if (_hooks.IsRecordingActions)
        {
            StopActionRecording();
            return;
        }

        _editingActions.Clear();
        RefreshActionList();
        _btnRecordActions.Text = "\u25A0 Stop";
        _btnRecordActions.BackColor = Color.FromArgb(140, 40, 40);
        _recordStopwatch = Stopwatch.StartNew();
        _lastActionTimeMs = 0;
        _hooks.IsRecordingActions = true;
    }

    private void OnActionInputRecorded(MacroActionType type, ushort vkCode)
    {
        if (this.InvokeRequired)
        {
            this.BeginInvoke(() => OnActionInputRecorded(type, vkCode));
            return;
        }

        long nowMs = _recordStopwatch?.ElapsedMilliseconds ?? 0;
        long gap = nowMs - _lastActionTimeMs;

        // Insert delay if gap > 50ms and not the very first action
        if (_editingActions.Count > 0 && gap > 50)
        {
            _editingActions.Add(new MacroAction { Type = MacroActionType.Delay, DelayMs = (int)gap });
        }

        _editingActions.Add(new MacroAction { Type = type, KeyCode = vkCode });
        _lastActionTimeMs = nowMs;
        RefreshActionList();
    }

    private void OnRecordingStopped()
    {
        if (this.InvokeRequired)
        {
            this.BeginInvoke(OnRecordingStopped);
            return;
        }
        StopActionRecording();
    }

    private void StopActionRecording()
    {
        _hooks.IsRecordingActions = false;
        _recordStopwatch?.Stop();
        _btnRecordActions.Text = "\u25CF Record";
        _btnRecordActions.BackColor = Color.FromArgb(50, 50, 50);
    }

    // ==================== Manual Action Add ====================

    private void BtnAddAction_Click(object? sender, EventArgs e)
    {
        if (_selectedMacro == null) return;

        var menu = new ContextMenuStrip { BackColor = _panelColor, ForeColor = _fgColor };
        menu.Renderer = new DarkMenuRenderer(_panelColor, _accentColor, _fgColor);
        menu.Items.Add("KeyPress", null, (s, ev) => PromptKeyAction(MacroActionType.KeyPress));
        menu.Items.Add("KeyDown", null, (s, ev) => PromptKeyAction(MacroActionType.KeyDown));
        menu.Items.Add("KeyUp", null, (s, ev) => PromptKeyAction(MacroActionType.KeyUp));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Left Click", null, (s, ev) => { _editingActions.Add(new MacroAction { Type = MacroActionType.LeftClick }); RefreshActionList(); });
        menu.Items.Add("Right Click", null, (s, ev) => { _editingActions.Add(new MacroAction { Type = MacroActionType.RightClick }); RefreshActionList(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delay (ms)", null, (s, ev) => PromptDelay());

        var btn = (Button)sender!;
        menu.Show(btn, new Point(0, btn.Height));
    }

    private void PromptKeyAction(MacroActionType type)
    {
        using var dlg = new Form
        {
            Text = $"Add {type}", Size = new Size(280, 130), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            BackColor = _panelColor, ForeColor = _fgColor
        };
        var lbl = new Label { Text = "Press a key...", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12) };
        dlg.Controls.Add(lbl);
        dlg.KeyPreview = true;
        dlg.KeyDown += (s, e) =>
        {
            _editingActions.Add(new MacroAction { Type = type, KeyCode = (ushort)e.KeyCode });
            RefreshActionList();
            dlg.Close();
        };
        dlg.ShowDialog(this);
    }

    private void PromptDelay()
    {
        using var dlg = new Form
        {
            Text = "Add Delay", Size = new Size(280, 140), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            BackColor = _panelColor, ForeColor = _fgColor
        };
        var lbl = new Label { Text = "Delay (ms):", Location = new Point(20, 20), AutoSize = true };
        var nud = new NumericUpDown
        {
            Location = new Point(100, 17), Width = 120, Minimum = 10, Maximum = 30000, Value = 200,
            BackColor = _fieldBg, ForeColor = _fgColor
        };
        var btnOk = CreateButton("OK", (s, e) =>
        {
            _editingActions.Add(new MacroAction { Type = MacroActionType.Delay, DelayMs = (int)nud.Value });
            RefreshActionList();
            dlg.Close();
        });
        btnOk.Location = new Point(100, 55);
        dlg.Controls.AddRange(new Control[] { lbl, nud, btnOk });
        dlg.ShowDialog(this);
    }

    // ==================== Action List Management ====================

    private void BtnRemoveAction_Click(object? sender, EventArgs e)
    {
        if (_actionListView.SelectedIndices.Count == 0) return;
        int idx = _actionListView.SelectedIndices[0];
        _editingActions.RemoveAt(idx);
        RefreshActionList();
        if (_editingActions.Count > 0)
            _actionListView.Items[Math.Min(idx, _editingActions.Count - 1)].Selected = true;
    }

    private void BtnMoveUp_Click(object? sender, EventArgs e)
    {
        if (_actionListView.SelectedIndices.Count == 0) return;
        int idx = _actionListView.SelectedIndices[0];
        if (idx <= 0) return;
        (_editingActions[idx], _editingActions[idx - 1]) = (_editingActions[idx - 1], _editingActions[idx]);
        RefreshActionList();
        _actionListView.Items[idx - 1].Selected = true;
    }

    private void BtnMoveDown_Click(object? sender, EventArgs e)
    {
        if (_actionListView.SelectedIndices.Count == 0) return;
        int idx = _actionListView.SelectedIndices[0];
        if (idx >= _editingActions.Count - 1) return;
        (_editingActions[idx], _editingActions[idx + 1]) = (_editingActions[idx + 1], _editingActions[idx]);
        RefreshActionList();
        _actionListView.Items[idx + 1].Selected = true;
    }

    private void RefreshActionList()
    {
        _actionListView.Items.Clear();
        for (int i = 0; i < _editingActions.Count; i++)
        {
            var a = _editingActions[i];
            string detail = a.Type switch
            {
                MacroActionType.KeyPress or MacroActionType.KeyDown or MacroActionType.KeyUp => MacroAction.GetKeyName(a.KeyCode),
                MacroActionType.Delay => $"{a.DelayMs} ms",
                _ => ""
            };
            var item = new ListViewItem(new[] { (i + 1).ToString(), a.Type.ToString(), detail });
            _actionListView.Items.Add(item);
        }
    }

    // ==================== Save / Cancel ====================

    private async void BtnSave_Click(object? sender, EventArgs e)
    {
        if (_selectedMacro == null) return;
        _selectedMacro.Actions.Clear();
        foreach (var a in _editingActions)
            _selectedMacro.Actions.Add(new MacroAction { Type = a.Type, KeyCode = a.KeyCode, DelayMs = a.DelayMs });

        try
        {
            await _config.SaveConfigAsync();
            _btnSave.Text = "\u2714 Saved!";
            _ = Task.Delay(1500).ContinueWith(_ =>
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                    this.BeginInvoke(() => _btnSave.Text = "\u2714 Save Macro");
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BtnCancel_Click(object? sender, EventArgs e)
    {
        // Revert to saved state
        if (_selectedMacro != null)
            MacroList_SelectedIndexChanged(null, EventArgs.Empty);
    }

    // ==================== Action ListView Custom Draw ====================

    private void ActionListView_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bgBrush = new SolidBrush(_panelColor);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);
        using var fgBrush = new SolidBrush(Color.FromArgb(150, 150, 150));
        e.Graphics.DrawString(e.Header!.Text, new Font("Segoe UI", 8.5f, FontStyle.Bold), fgBrush, e.Bounds.X + 4, e.Bounds.Y + 4);
    }

    private void ActionListView_DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        // Handled by DrawSubItem
    }

    private void ActionListView_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        bool selected = e.Item!.Selected;
        using var bgBrush = new SolidBrush(selected ? _accentColor : (e.ItemIndex % 2 == 0 ? _bgColor : Color.FromArgb(35, 35, 35)));
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        Color textColor = _fgColor;
        if (e.ColumnIndex == 1) // Type column — color by type
        {
            var typeText = e.SubItem!.Text;
            textColor = typeText switch
            {
                "KeyPress" => Color.FromArgb(86, 156, 214),
                "KeyDown" => Color.FromArgb(78, 201, 176),
                "KeyUp" => Color.FromArgb(206, 145, 120),
                "LeftClick" or "RightClick" => Color.FromArgb(220, 220, 170),
                "Delay" => Color.FromArgb(128, 128, 128),
                _ => _fgColor
            };
        }

        using var fgBrush = new SolidBrush(textColor);
        e.Graphics.DrawString(e.SubItem!.Text, _actionListView.Font, fgBrush, e.Bounds.X + 4, e.Bounds.Y + 4);
    }

    // ==================== Dark Menu Renderer ====================

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly Color _bg, _highlight, _fg;
        public DarkMenuRenderer(Color bg, Color highlight, Color fg) : base(new DarkColorTable(bg, highlight))
        {
            _bg = bg; _highlight = highlight; _fg = fg;
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = _fg; base.OnRenderItemText(e); }
        private sealed class DarkColorTable : ProfessionalColorTable
        {
            private readonly Color _bg, _highlight;
            public DarkColorTable(Color bg, Color highlight) { _bg = bg; _highlight = highlight; }
            public override Color MenuItemSelected => _highlight;
            public override Color ToolStripDropDownBackground => _bg;
            public override Color ImageMarginGradientBegin => _bg;
            public override Color ImageMarginGradientMiddle => _bg;
            public override Color ImageMarginGradientEnd => _bg;
            public override Color SeparatorDark => Color.FromArgb(60, 60, 60);
            public override Color SeparatorLight => Color.FromArgb(60, 60, 60);
        }
    }

    // ==================== Shared Button Factory ====================

    private Button CreateButton(string text, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
            ForeColor = _fgColor, BackColor = _btnColor, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9), Padding = new Padding(4, 1, 4, 1)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = _accentColor;
        btn.Click += onClick;
        return btn;
    }

    // ==================== Logger ====================

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
            Services.Logger.LogLevel.Info => Color.FromArgb(86, 156, 214),
            Services.Logger.LogLevel.Debug => Color.FromArgb(128, 128, 128),
            Services.Logger.LogLevel.Error => Color.FromArgb(244, 71, 71),
            Services.Logger.LogLevel.Success => Color.FromArgb(106, 153, 85),
            Services.Logger.LogLevel.Warning => Color.FromArgb(206, 145, 120),
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

    // ==================== Close Handling ====================

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
            _hooks.OnActionInputRecorded -= OnActionInputRecorded;
            _hooks.OnRecordingStopped -= OnRecordingStopped;
            base.OnFormClosing(e);
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        this.Close();
    }
}