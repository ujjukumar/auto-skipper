using AutoSkipper.Input;
using AutoSkipper.Models;

namespace AutoSkipper.UI;

internal class LogForm : Form
{
    // --- Core state ---
    private readonly GlobalHooks _hooks;
    private readonly ScreenConfig _config;

    // Macro edits are made on this draft copy and only applied to the live
    // hooks and config.json when the user clicks Save.
    private List<Macro> _draft = new();
    private Macro? _selectedMacro;
    private bool _dirty = false;
    private bool _loadingUI = false;
    private bool _allowClose = false;

    // --- Theme ---
    private readonly Color _bgColor = Color.FromArgb(30, 30, 30);
    private readonly Color _panelColor = Color.FromArgb(37, 37, 38);
    private readonly Color _fgColor = Color.FromArgb(204, 204, 204);
    private readonly Color _accentColor = Color.FromArgb(0, 122, 204);
    private readonly Color _btnColor = Color.FromArgb(60, 60, 60);
    private readonly Color _borderColor = Color.FromArgb(50, 50, 50);
    private readonly Color _fieldBg = Color.FromArgb(45, 45, 45);

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
    private Button _btnSave = null!;
    private Button _btnRevert = null!;
    private Label _unsavedLabel = null!;

    // --- Action composer ---
    private ComboBox _buttonCombo = null!;
    private RadioButton _radioPress = null!;
    private RadioButton _radioHold = null!;
    private NumericUpDown _durationNud = null!;
    private NumericUpDown _delayNud = null!;

    public LogForm(ScreenConfig config, GlobalHooks hooks, Icon? icon = null)
    {
        _hooks = hooks;
        _config = config;

        this.Text = "AutoSkipper";
        this.Size = new Size(900, 580);
        this.MinimumSize = new Size(840, 520);
        this.Icon = icon ?? SystemIcons.Application;
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
        Services.Logger.RegisterViewer(Logger_OnLogMessage);

        // Hide to tray on minimize instead of leaving a minimized window.
        // This also avoids the RichTextBox losing its content when its handle
        // is recreated on restore.
        this.Resize += (s, e) =>
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.Hide();
                this.WindowState = FormWindowState.Normal;
            }
        };
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
        _macroList.SelectedIndexChanged += (s, e) => LoadSelectedMacro();

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
        _actionListView.Columns.Add("#", 34);
        _actionListView.Columns.Add("Action", 80);
        _actionListView.Columns.Add("Button", 150);
        _actionListView.Columns.Add("Time", 90);
        _actionListView.OwnerDraw = true;
        _actionListView.DrawColumnHeader += ActionListView_DrawColumnHeader;
        _actionListView.DrawItem += ActionListView_DrawItem;
        _actionListView.DrawSubItem += ActionListView_DrawSubItem;

        // --- Composer: build one action at a time ---
        var composer = new Panel { Dock = DockStyle.Bottom, Height = 120, BackColor = _bgColor };
        var composerTopBorder = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = _borderColor };

        // Row 1: Button + Press/Hold + duration
        var lblButton = new Label { Text = "Button", AutoSize = true, ForeColor = _fgColor, Location = new Point(2, 15) };
        _buttonCombo = new ComboBox
        {
            Location = new Point(56, 12), Width = 132, BackColor = _fieldBg,
            ForeColor = Color.FromArgb(86, 156, 214), FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            DropDownStyle = ComboBoxStyle.DropDown
        };
        PopulateButtonCombo();

        _radioPress = new RadioButton { Text = "Press", AutoSize = true, Checked = true, ForeColor = _fgColor, Location = new Point(200, 13) };
        _radioHold = new RadioButton { Text = "Hold", AutoSize = true, ForeColor = _fgColor, Location = new Point(266, 13) };
        _radioPress.CheckedChanged += (s, e) => _durationNud.Enabled = _radioHold.Checked;
        _radioHold.CheckedChanged += (s, e) => _durationNud.Enabled = _radioHold.Checked;

        var lblFor = new Label { Text = "for", AutoSize = true, ForeColor = _fgColor, Location = new Point(324, 15) };
        _durationNud = new NumericUpDown
        {
            Location = new Point(350, 12), Width = 66, Minimum = 10, Maximum = 60000, Increment = 50,
            Value = 500, Enabled = false, BackColor = _fieldBg, ForeColor = _fgColor,
            BorderStyle = BorderStyle.FixedSingle
        };
        var lblDurMs = new Label { Text = "ms", AutoSize = true, ForeColor = _fgColor, Location = new Point(420, 15) };
        var btnAddAction = CreateButton("+ Add", BtnAddButtonAction_Click);
        btnAddAction.Location = new Point(456, 11);

        // Row 2: Delay
        var lblDelay = new Label { Text = "Delay", AutoSize = true, ForeColor = _fgColor, Location = new Point(2, 55) };
        _delayNud = new NumericUpDown
        {
            Location = new Point(56, 52), Width = 66, Minimum = 0, Maximum = 60000, Increment = 50,
            Value = 200, BackColor = _fieldBg, ForeColor = _fgColor, BorderStyle = BorderStyle.FixedSingle
        };
        var lblDelayMs = new Label { Text = "ms", AutoSize = true, ForeColor = _fgColor, Location = new Point(126, 55) };
        var btnAddDelay = CreateButton("+ Add Delay", BtnAddDelay_Click);
        btnAddDelay.Location = new Point(162, 51);

        // Row 3: list management
        var btnRemoveAction = CreateButton("\u2716 Remove", BtnRemoveAction_Click);
        btnRemoveAction.ForeColor = Color.FromArgb(244, 71, 71);
        var btnMoveUp = CreateButton("\u25B2 Up", BtnMoveUp_Click);
        var btnMoveDown = CreateButton("\u25BC Down", BtnMoveDown_Click);
        btnRemoveAction.Location = new Point(2, 88);
        btnMoveUp.Location = new Point(btnRemoveAction.Right + 6, 88);
        btnMoveDown.Location = new Point(btnMoveUp.Right + 6, 88);

        composer.Controls.AddRange(new Control[]
        {
            lblButton, _buttonCombo, _radioPress, _radioHold, lblFor, _durationNud, lblDurMs, btnAddAction,
            lblDelay, _delayNud, lblDelayMs, btnAddDelay,
            btnRemoveAction, btnMoveUp, btnMoveDown, composerTopBorder
        });

        // Save bar: Save applies every pending macro change, Revert discards them.
        var saveBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 46, BackColor = _bgColor,
            Padding = new Padding(0, 8, 0, 0), FlowDirection = FlowDirection.LeftToRight, WrapContents = false
        };
        _btnSave = CreateButton("\u2714 Save", BtnSave_Click);
        _btnSave.BackColor = Color.FromArgb(30, 100, 30);
        _btnSave.ForeColor = Color.FromArgb(180, 255, 180);
        _btnSave.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _btnSave.Margin = new Padding(2, 0, 6, 0);
        _btnRevert = CreateButton("\u21BA Revert", BtnRevert_Click);
        _btnRevert.Margin = new Padding(0, 0, 12, 0);
        _unsavedLabel = new Label
        {
            Text = "\u25CF Unsaved changes", AutoSize = true, Visible = false,
            ForeColor = Color.FromArgb(206, 145, 120), Margin = new Padding(0, 7, 0, 0)
        };
        saveBar.Controls.AddRange(new Control[] { _btnSave, _btnRevert, _unsavedLabel });

        rightPanel.Controls.Add(_actionListView);
        rightPanel.Controls.Add(composer);
        rightPanel.Controls.Add(saveBar);
        rightPanel.Controls.Add(_actionsHeader);

        _macroContainer.Controls.Add(rightPanel);
        _macroContainer.Controls.Add(splitter);
        _macroContainer.Controls.Add(leftPanel);

        LoadDraft();
    }

    // ==================== Draft / Save ====================

    public bool HasUnsavedChanges => _dirty;

    // Rebuilds the draft from the saved macros, discarding unsaved edits.
    private void LoadDraft(int selectIndex = -1)
    {
        _draft = _config.Config.Macros.Select(m => m.Clone()).ToList();
        _macroList.BeginUpdate();
        _macroList.Items.Clear();
        foreach (var m in _draft)
            _macroList.Items.Add(m);
        _macroList.EndUpdate();

        if (selectIndex >= 0 && _draft.Count > 0)
            _macroList.SelectedIndex = Math.Min(selectIndex, _draft.Count - 1);
        LoadSelectedMacro();
        SetDirty(false);
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        _unsavedLabel.Visible = dirty;
        _btnRevert.Enabled = dirty;
    }

    // Applies the draft to the live hooks and writes config.json.
    public async Task<bool> SaveMacrosAsync()
    {
        var previous = _config.Config.Macros;
        var saved = _draft.Select(m => m.Clone()).ToList();
        _config.Config.Macros = saved;
        try
        {
            await _config.SaveConfigAsync();
        }
        catch (Exception ex)
        {
            _config.Config.Macros = previous;
            MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        _hooks.SetMacros(saved);
        SetDirty(false);
        Services.Logger.LogSuccessStatic($"Saved {saved.Count} macro(s).");
        return true;
    }

    private async void BtnSave_Click(object? sender, EventArgs e)
    {
        if (!await SaveMacrosAsync()) return;

        _btnSave.Text = "\u2714 Saved!";
        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            if (!this.IsDisposed && this.IsHandleCreated)
                this.BeginInvoke(() => _btnSave.Text = "\u2714 Save");
        });
    }

    private void BtnRevert_Click(object? sender, EventArgs e) => LoadDraft(_macroList.SelectedIndex);

    // ==================== Macro List ====================

    private void MacroList_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _macroList.Items.Count) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var bgBrush = new SolidBrush(selected ? _accentColor : _bgColor);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);
        using var fgBrush = new SolidBrush(_fgColor);
        var macro = (Macro)_macroList.Items[e.Index];
        e.Graphics.DrawString($"{macro.Name}  [{macro.GetTriggerDisplay()}]", e.Font!, fgBrush, e.Bounds.X + 8, e.Bounds.Y + 5);
    }

    // Shows the selected macro in the editor. _loadingUI stops the field
    // change handlers from treating this as a user edit.
    private void LoadSelectedMacro()
    {
        _selectedMacro = _macroList.SelectedItem as Macro;
        _loadingUI = true;
        try
        {
            _nameBox.Text = _selectedMacro?.Name ?? "";
            LoadTriggerToUI(_selectedMacro?.TriggerKeys ?? []);
            _actionsHeader.Text = _selectedMacro != null
                ? $"ACTIONS  —  {_selectedMacro.Name}"
                : "Select a macro to edit actions";
        }
        finally
        {
            _loadingUI = false;
        }
        RefreshActionList();
    }

    private void NameBox_TextChanged(object? sender, EventArgs e)
    {
        if (_loadingUI || _selectedMacro == null) return;
        _selectedMacro.Name = _nameBox.Text;
        _macroList.Invalidate();
        _actionsHeader.Text = $"ACTIONS  —  {_selectedMacro.Name}";
        SetDirty(true);
    }

    // ==================== Add/Remove Macro ====================

    private void BtnAddMacro_Click(object? sender, EventArgs e)
    {
        var macro = new Macro { Name = $"Macro {_draft.Count + 1}" };
        _draft.Add(macro);
        _macroList.Items.Add(macro);
        _macroList.SelectedItem = macro;
        SetDirty(true);
    }

    private void BtnRemoveMacro_Click(object? sender, EventArgs e)
    {
        if (_selectedMacro == null) return;
        int idx = _macroList.SelectedIndex;
        _draft.Remove(_selectedMacro);
        _macroList.Items.RemoveAt(idx);
        if (_draft.Count > 0)
            _macroList.SelectedIndex = Math.Min(idx, _draft.Count - 1);
        LoadSelectedMacro();
        SetDirty(true);
    }

    // ==================== Trigger Key Selection ====================

    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_LALT = 0xA4;

    // Buttons usable in actions: keyboard keys plus all mouse buttons.
    private static readonly (string Name, ushort VK)[] _buttonKeys = BuildButtonList();

    // Trigger keys: "None" plus every action button except Left/Right/Middle
    // click, which the mouse hook doesn't watch.
    private static readonly (string Name, ushort VK)[] _triggerKeys =
        [("None", 0), .. _buttonKeys.Where(k => k.VK is not (0x01 or 0x02 or 0x04))];

    private static (string Name, ushort VK)[] BuildButtonList()
    {
        var keys = new List<(string, ushort)>();

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
        foreach (var (name, _) in _triggerKeys)
            _triggerCombo.Items.Add(name);
        _triggerCombo.SelectedIndex = 0;
    }

    /// <summary>Load a TriggerKeys list into the modifier checkboxes + primary key combo.</summary>
    private void LoadTriggerToUI(List<ushort> keys)
    {
        _chkCtrl.Checked = keys.Contains(VK_LCONTROL);
        _chkShift.Checked = keys.Contains(VK_LSHIFT);
        _chkAlt.Checked = keys.Contains(VK_LALT);

        // Find the primary key (not a modifier)
        var primary = keys.FirstOrDefault(k => k is not (VK_LCONTROL or VK_LSHIFT or VK_LALT));
        int idx = Array.FindIndex(_triggerKeys, t => t.VK == primary);
        _triggerCombo.SelectedIndex = Math.Max(0, idx);
    }

    /// <summary>Build TriggerKeys list from the current UI state.</summary>
    private List<ushort> BuildTriggerKeysFromUI()
    {
        var keys = new List<ushort>();
        if (_chkCtrl.Checked) keys.Add(VK_LCONTROL);
        if (_chkShift.Checked) keys.Add(VK_LSHIFT);
        if (_chkAlt.Checked) keys.Add(VK_LALT);

        int idx = _triggerCombo.SelectedIndex;
        if (idx > 0 && idx < _triggerKeys.Length)
            keys.Add(_triggerKeys[idx].VK);

        return keys;
    }

    private void TriggerCombo_Changed(object? sender, EventArgs e)
    {
        if (_loadingUI || _selectedMacro == null) return;
        _selectedMacro.TriggerKeys = BuildTriggerKeysFromUI();
        _macroList.Invalidate();
        SetDirty(true);
    }

    // ==================== Action Composer ====================

    private void PopulateButtonCombo()
    {
        _buttonCombo.Items.Clear();
        foreach (var (name, _) in _buttonKeys)
            _buttonCombo.Items.Add(name);
        _buttonCombo.Text = "";
    }

    /// <summary>Resolve the combo's text (typed or selected) to a virtual key code.</summary>
    private static bool TryResolveButton(string text, out ushort vk)
    {
        vk = 0;
        text = text.Trim();
        if (text.Length == 0) return false;

        // Match a friendly name (case-insensitive)
        foreach (var (name, v) in _buttonKeys)
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase)) { vk = v; return true; }
        }

        // Single character: letter or digit
        if (text.Length == 1)
        {
            char c = char.ToUpperInvariant(text[0]);
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
        }
        return false;
    }

    private void BtnAddButtonAction_Click(object? sender, EventArgs e)
    {
        if (_selectedMacro == null)
        {
            MessageBox.Show("Select or create a macro first.", "No macro selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!TryResolveButton(_buttonCombo.Text, out ushort vk))
        {
            MessageBox.Show("Pick a button from the list or type a valid key (e.g. E, Space, F1, Left Click).",
                "Unknown button", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var action = _radioHold.Checked
            ? new MacroAction { Type = MacroActionType.Hold, KeyCode = vk, DurationMs = (int)_durationNud.Value }
            : new MacroAction { Type = MacroActionType.Press, KeyCode = vk };

        _selectedMacro.Actions.Add(action);
        RefreshActionList();
        SelectLastAction();
        SetDirty(true);
    }

    private void BtnAddDelay_Click(object? sender, EventArgs e)
    {
        if (_selectedMacro == null)
        {
            MessageBox.Show("Select or create a macro first.", "No macro selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _selectedMacro.Actions.Add(new MacroAction { Type = MacroActionType.Delay, DelayMs = (int)_delayNud.Value });
        RefreshActionList();
        SelectLastAction();
        SetDirty(true);
    }

    private void SelectLastAction()
    {
        if (_actionListView.Items.Count == 0) return;
        var item = _actionListView.Items[^1];
        item.Selected = true;
        item.EnsureVisible();
    }

    // ==================== Action List Management ====================

    private void BtnRemoveAction_Click(object? sender, EventArgs e)
    {
        if (_selectedMacro == null || _actionListView.SelectedIndices.Count == 0) return;
        var actions = _selectedMacro.Actions;
        int idx = _actionListView.SelectedIndices[0];
        actions.RemoveAt(idx);
        RefreshActionList();
        if (actions.Count > 0)
            _actionListView.Items[Math.Min(idx, actions.Count - 1)].Selected = true;
        SetDirty(true);
    }

    private void BtnMoveUp_Click(object? sender, EventArgs e)
    {
        if (_selectedMacro == null || _actionListView.SelectedIndices.Count == 0) return;
        var actions = _selectedMacro.Actions;
        int idx = _actionListView.SelectedIndices[0];
        if (idx <= 0) return;
        (actions[idx], actions[idx - 1]) = (actions[idx - 1], actions[idx]);
        RefreshActionList();
        _actionListView.Items[idx - 1].Selected = true;
        SetDirty(true);
    }

    private void BtnMoveDown_Click(object? sender, EventArgs e)
    {
        if (_selectedMacro == null || _actionListView.SelectedIndices.Count == 0) return;
        var actions = _selectedMacro.Actions;
        int idx = _actionListView.SelectedIndices[0];
        if (idx >= actions.Count - 1) return;
        (actions[idx], actions[idx + 1]) = (actions[idx + 1], actions[idx]);
        RefreshActionList();
        _actionListView.Items[idx + 1].Selected = true;
        SetDirty(true);
    }

    private void RefreshActionList()
    {
        _actionListView.BeginUpdate();
        _actionListView.Items.Clear();
        var actions = _selectedMacro?.Actions ?? [];
        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            string button = a.Type is MacroActionType.Press or MacroActionType.Hold
                ? MacroAction.GetKeyName(a.KeyCode) : "";
            string time = a.Type switch
            {
                MacroActionType.Hold => $"{a.DurationMs} ms",
                MacroActionType.Delay => $"{a.DelayMs} ms",
                _ => ""
            };
            var item = new ListViewItem(new[] { (i + 1).ToString(), a.Type.ToString(), button, time });
            _actionListView.Items.Add(item);
        }
        _actionListView.EndUpdate();
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
        if (e.ColumnIndex == 1) // Action column — color by type
        {
            var typeText = e.SubItem!.Text;
            textColor = typeText switch
            {
                "Press" => Color.FromArgb(86, 156, 214),
                "Hold" => Color.FromArgb(78, 201, 176),
                "Delay" => Color.FromArgb(128, 128, 128),
                _ => _fgColor
            };
        }

        using var fgBrush = new SolidBrush(textColor);
        e.Graphics.DrawString(e.SubItem!.Text, _actionListView.Font, fgBrush, e.Bounds.X + 4, e.Bounds.Y + 4);
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
        _richTextBox.SelectionColor = _fgColor;
        _richTextBox.AppendText($"{message}\n");

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
            Services.Logger.UnregisterViewer(Logger_OnLogMessage);
            base.OnFormClosing(e);
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        this.Close();
    }
}
