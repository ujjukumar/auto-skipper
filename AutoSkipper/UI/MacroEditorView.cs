using AutoSkipper.Models;
using AutoSkipper.Services;

namespace AutoSkipper.UI;

// The Macros tab. Edits are made on a draft copy of the saved macros and only
// written to config.json and applied to the live input hooks on Save.
internal sealed class MacroEditorView : UserControl
{
    private readonly ConfigStore _store;
    private readonly Action<IReadOnlyList<Macro>> _applyMacros;

    private List<Macro> _draft = new();
    private Macro? _selectedMacro;
    private bool _dirty = false;
    private bool _loadingUI = false;

    private static readonly SolidBrush _pressBrush = new(Theme.KeyColor);
    private static readonly SolidBrush _holdBrush = new(Color.FromArgb(78, 201, 176));
    private static readonly SolidBrush _delayBrush = new(Color.FromArgb(128, 128, 128));

    // --- Macro list (left) ---
    private readonly ListBox _macroList;
    private readonly TextBox _nameBox;
    private readonly CheckBox _chkCtrl;
    private readonly CheckBox _chkShift;
    private readonly CheckBox _chkAlt;
    private readonly ComboBox _triggerCombo;

    // --- Action editor (right) ---
    private readonly ListView _actionListView;
    private readonly Label _actionsHeader;
    private readonly Button _btnSave;
    private readonly Button _btnRevert;
    private readonly Label _unsavedLabel;

    // --- Action composer ---
    private readonly ComboBox _buttonCombo;
    private readonly RadioButton _radioPress;
    private readonly RadioButton _radioHold;
    private readonly NumericUpDown _durationNud;
    private readonly NumericUpDown _delayNud;

    // applyMacros receives the saved macros so they take effect immediately.
    public MacroEditorView(ConfigStore store, Action<IReadOnlyList<Macro>> applyMacros)
    {
        _store = store;
        _applyMacros = applyMacros;
        Dock = DockStyle.Fill;
        BackColor = Theme.BackgroundColor;

        // --- Left: Macro List + Properties ---
        var leftPanel = new Panel
        {
            Dock = DockStyle.Left, Width = 270, BackColor = Theme.PanelColor,
            Padding = new Padding(10)
        };

        var leftLabel = new Label
        {
            Text = "MACROS", Dock = DockStyle.Top, Height = 24,
            ForeColor = Theme.MutedColor, Font = Theme.HeaderFont,
            Padding = new Padding(0, 4, 0, 0)
        };

        _macroList = new ListBox
        {
            Dock = DockStyle.Fill, BackColor = Theme.BackgroundColor, ForeColor = Theme.ForegroundColor,
            BorderStyle = BorderStyle.None, Font = Theme.ListFont,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28
        };
        _macroList.DrawItem += MacroList_DrawItem;
        _macroList.SelectedIndexChanged += (s, e) => LoadSelectedMacro();

        // Properties panel
        var propsPanel = new Panel
        {
            Dock = DockStyle.Bottom, Height = 140, BackColor = Theme.PanelColor,
            Padding = new Padding(0, 8, 0, 0)
        };

        var lblName = new Label { Text = "Name:", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(0, 6) };
        _nameBox = new TextBox
        {
            Location = new Point(60, 3), Width = 190, BackColor = Theme.FieldColor,
            ForeColor = Theme.ForegroundColor, BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.UiFont
        };
        _nameBox.TextChanged += NameBox_TextChanged;

        var lblTrigger = new Label { Text = "Trigger:", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(0, 38) };

        // Modifier checkboxes
        _chkCtrl = new CheckBox { Text = "Ctrl", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(60, 37), Font = Theme.SmallFont };
        _chkShift = new CheckBox { Text = "Shift", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(110, 37), Font = Theme.SmallFont };
        _chkAlt = new CheckBox { Text = "Alt", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(165, 37), Font = Theme.SmallFont };
        _chkCtrl.CheckedChanged += TriggerCombo_Changed;
        _chkShift.CheckedChanged += TriggerCombo_Changed;
        _chkAlt.CheckedChanged += TriggerCombo_Changed;

        // Primary key combo
        var lblKey = new Label { Text = "Key:", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(0, 65) };
        _triggerCombo = new ComboBox
        {
            Location = new Point(60, 62), Width = 190, BackColor = Theme.FieldColor,
            ForeColor = Theme.KeyColor, FlatStyle = FlatStyle.Flat,
            Font = Theme.KeyFont,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        foreach (var (name, _) in _triggerKeys)
            _triggerCombo.Items.Add(name);
        _triggerCombo.SelectedIndex = 0;
        _triggerCombo.SelectedIndexChanged += TriggerCombo_Changed;

        propsPanel.Controls.AddRange(new Control[] { lblName, _nameBox, lblTrigger, _chkCtrl, _chkShift, _chkAlt, lblKey, _triggerCombo });

        // Add/Remove buttons
        var btnBar = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Theme.PanelColor };
        var btnAdd = Theme.CreateButton("+ Add", BtnAddMacro_Click);
        var btnRemove = Theme.CreateButton("- Remove", BtnRemoveMacro_Click);
        btnRemove.ForeColor = Theme.DangerColor;
        btnAdd.Location = new Point(0, 6);
        btnRemove.Location = new Point(btnAdd.Right + 6, 6);
        btnBar.Controls.Add(btnAdd);
        btnBar.Controls.Add(btnRemove);

        leftPanel.Controls.Add(_macroList);
        leftPanel.Controls.Add(propsPanel);
        leftPanel.Controls.Add(btnBar);
        leftPanel.Controls.Add(leftLabel);

        // --- Splitter ---
        var splitter = new Panel { Dock = DockStyle.Left, Width = 2, BackColor = Theme.BorderColor };

        // --- Right: Action Editor ---
        var rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BackgroundColor, Padding = new Padding(10) };

        _actionsHeader = new Label
        {
            Text = "Select a macro to edit actions", Dock = DockStyle.Top, Height = 28,
            ForeColor = Theme.MutedColor, Font = Theme.KeyFont,
            Padding = new Padding(0, 4, 0, 0)
        };

        _actionListView = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            BackColor = Theme.BackgroundColor, ForeColor = Theme.ForegroundColor, BorderStyle = BorderStyle.None,
            Font = Theme.ListFont, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            GridLines = false, MultiSelect = false
        };
        _actionListView.Columns.Add("#", 34);
        _actionListView.Columns.Add("Action", 80);
        _actionListView.Columns.Add("Button", 150);
        _actionListView.Columns.Add("Time", 90);
        _actionListView.OwnerDraw = true;
        _actionListView.DrawColumnHeader += ActionListView_DrawColumnHeader;
        _actionListView.DrawItem += (s, e) => { }; // drawn by DrawSubItem
        _actionListView.DrawSubItem += ActionListView_DrawSubItem;

        // --- Composer: build one action at a time ---
        var composer = new Panel { Dock = DockStyle.Bottom, Height = 120, BackColor = Theme.BackgroundColor };
        var composerTopBorder = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.BorderColor };

        // Row 1: Button + Press/Hold + duration
        var lblButton = new Label { Text = "Button", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(2, 15) };
        _buttonCombo = new ComboBox
        {
            Location = new Point(56, 12), Width = 132, BackColor = Theme.FieldColor,
            ForeColor = Theme.KeyColor, FlatStyle = FlatStyle.Flat,
            Font = Theme.KeyFont,
            DropDownStyle = ComboBoxStyle.DropDown
        };
        foreach (var (name, _) in _buttonKeys)
            _buttonCombo.Items.Add(name);

        _radioPress = new RadioButton { Text = "Press", AutoSize = true, Checked = true, ForeColor = Theme.ForegroundColor, Location = new Point(200, 13) };
        _radioHold = new RadioButton { Text = "Hold", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(266, 13) };

        var lblFor = new Label { Text = "for", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(324, 15) };
        _durationNud = new NumericUpDown
        {
            Location = new Point(350, 12), Width = 66, Minimum = 10, Maximum = 60000, Increment = 50,
            Value = 500, Enabled = false, BackColor = Theme.FieldColor, ForeColor = Theme.ForegroundColor,
            BorderStyle = BorderStyle.FixedSingle
        };
        _radioPress.CheckedChanged += (s, e) => _durationNud.Enabled = _radioHold.Checked;
        _radioHold.CheckedChanged += (s, e) => _durationNud.Enabled = _radioHold.Checked;
        var lblDurMs = new Label { Text = "ms", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(420, 15) };
        var btnAddAction = Theme.CreateButton("+ Add", BtnAddButtonAction_Click);
        btnAddAction.Location = new Point(456, 11);

        // Row 2: Delay
        var lblDelay = new Label { Text = "Delay", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(2, 55) };
        _delayNud = new NumericUpDown
        {
            Location = new Point(56, 52), Width = 66, Minimum = 0, Maximum = 60000, Increment = 50,
            Value = 200, BackColor = Theme.FieldColor, ForeColor = Theme.ForegroundColor, BorderStyle = BorderStyle.FixedSingle
        };
        var lblDelayMs = new Label { Text = "ms", AutoSize = true, ForeColor = Theme.ForegroundColor, Location = new Point(126, 55) };
        var btnAddDelay = Theme.CreateButton("+ Add Delay", BtnAddDelay_Click);
        btnAddDelay.Location = new Point(162, 51);

        // Row 3: list management
        var btnRemoveAction = Theme.CreateButton("\u2716 Remove", BtnRemoveAction_Click);
        btnRemoveAction.ForeColor = Theme.DangerColor;
        var btnMoveUp = Theme.CreateButton("\u25B2 Up", BtnMoveUp_Click);
        var btnMoveDown = Theme.CreateButton("\u25BC Down", BtnMoveDown_Click);
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
            Dock = DockStyle.Bottom, Height = 46, BackColor = Theme.BackgroundColor,
            Padding = new Padding(0, 8, 0, 0), FlowDirection = FlowDirection.LeftToRight, WrapContents = false
        };
        _btnSave = Theme.CreateButton("\u2714 Save", BtnSave_Click);
        _btnSave.BackColor = Color.FromArgb(30, 100, 30);
        _btnSave.ForeColor = Color.FromArgb(180, 255, 180);
        _btnSave.Font = Theme.TabFont;
        _btnSave.Margin = new Padding(2, 0, 6, 0);
        _btnRevert = Theme.CreateButton("\u21BA Revert", (s, e) => LoadDraft(_macroList.SelectedIndex));
        _btnRevert.Margin = new Padding(0, 0, 12, 0);
        _unsavedLabel = new Label
        {
            Text = "\u25CF Unsaved changes", AutoSize = true, Visible = false,
            ForeColor = Theme.WarningColor, Margin = new Padding(0, 7, 0, 0)
        };
        saveBar.Controls.AddRange(new Control[] { _btnSave, _btnRevert, _unsavedLabel });

        rightPanel.Controls.Add(_actionListView);
        rightPanel.Controls.Add(composer);
        rightPanel.Controls.Add(saveBar);
        rightPanel.Controls.Add(_actionsHeader);

        Controls.Add(rightPanel);
        Controls.Add(splitter);
        Controls.Add(leftPanel);

        LoadDraft();
    }

    // ==================== Draft / Save ====================

    public bool HasUnsavedChanges => _dirty;

    // Rebuilds the draft from the saved macros, discarding unsaved edits.
    private void LoadDraft(int selectIndex = -1)
    {
        _draft = _store.Config.Macros.Select(m => m.Clone()).ToList();
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

    // Writes the draft to config.json and applies it to the live hooks.
    public async Task<bool> SaveMacrosAsync()
    {
        var previous = _store.Config.Macros;
        var saved = _draft.Select(m => m.Clone()).ToList();
        _store.Config.Macros = saved;
        try
        {
            await _store.SaveAsync();
        }
        catch (Exception ex)
        {
            _store.Config.Macros = previous;
            MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        _applyMacros(saved);
        SetDirty(false);
        Logger.LogSuccessStatic($"Saved {saved.Count} macro(s).");
        return true;
    }

    private async void BtnSave_Click(object? sender, EventArgs e)
    {
        if (!await SaveMacrosAsync()) return;

        _btnSave.Text = "\u2714 Saved!";
        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(() => _btnSave.Text = "\u2714 Save");
        });
    }

    // ==================== Macro List ====================

    private void MacroList_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _macroList.Items.Count) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        e.Graphics.FillRectangle(selected ? Theme.AccentBrush : Theme.BackgroundBrush, e.Bounds);
        var macro = (Macro)_macroList.Items[e.Index];
        e.Graphics.DrawString($"{macro.Name}  [{macro.GetTriggerDisplay()}]", e.Font!, Theme.ForegroundBrush, e.Bounds.X + 8, e.Bounds.Y + 5);
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

        // Common keys and the side mouse buttons
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

    private bool RequireSelectedMacro()
    {
        if (_selectedMacro != null) return true;
        MessageBox.Show("Select or create a macro first.", "No macro selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void BtnAddButtonAction_Click(object? sender, EventArgs e)
    {
        if (!RequireSelectedMacro()) return;
        if (!TryResolveButton(_buttonCombo.Text, out ushort vk))
        {
            MessageBox.Show("Pick a button from the list or type a valid key (e.g. E, Space, F1, Left Click).",
                "Unknown button", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var action = _radioHold.Checked
            ? new MacroAction { Type = MacroActionType.Hold, KeyCode = vk, DurationMs = (int)_durationNud.Value }
            : new MacroAction { Type = MacroActionType.Press, KeyCode = vk };

        _selectedMacro!.Actions.Add(action);
        RefreshActionList();
        SelectLastAction();
        SetDirty(true);
    }

    private void BtnAddDelay_Click(object? sender, EventArgs e)
    {
        if (!RequireSelectedMacro()) return;
        _selectedMacro!.Actions.Add(new MacroAction { Type = MacroActionType.Delay, DelayMs = (int)_delayNud.Value });
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

    private void BtnMoveUp_Click(object? sender, EventArgs e) => MoveSelectedAction(-1);
    private void BtnMoveDown_Click(object? sender, EventArgs e) => MoveSelectedAction(+1);

    private void MoveSelectedAction(int offset)
    {
        if (_selectedMacro == null || _actionListView.SelectedIndices.Count == 0) return;
        var actions = _selectedMacro.Actions;
        int idx = _actionListView.SelectedIndices[0];
        int target = idx + offset;
        if (target < 0 || target >= actions.Count) return;
        (actions[idx], actions[target]) = (actions[target], actions[idx]);
        RefreshActionList();
        _actionListView.Items[target].Selected = true;
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
            _actionListView.Items.Add(new ListViewItem(new[] { (i + 1).ToString(), a.Type.ToString(), button, time }));
        }
        _actionListView.EndUpdate();
    }

    // ==================== Action ListView Custom Draw ====================

    private void ActionListView_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        e.Graphics.FillRectangle(Theme.PanelBrush, e.Bounds);
        e.Graphics.DrawString(e.Header!.Text, Theme.HeaderFont, Theme.MutedBrush, e.Bounds.X + 4, e.Bounds.Y + 4);
    }

    private void ActionListView_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        bool selected = e.Item!.Selected;
        e.Graphics.FillRectangle(selected ? Theme.AccentBrush : (e.ItemIndex % 2 == 0 ? Theme.BackgroundBrush : Theme.AltRowBrush), e.Bounds);

        Brush textBrush = Theme.ForegroundBrush;
        if (e.ColumnIndex == 1) // Action column — color by type
        {
            textBrush = e.SubItem!.Text switch
            {
                "Press" => _pressBrush,
                "Hold" => _holdBrush,
                "Delay" => _delayBrush,
                _ => Theme.ForegroundBrush
            };
        }

        e.Graphics.DrawString(e.SubItem!.Text, _actionListView.Font, textBrush, e.Bounds.X + 4, e.Bounds.Y + 4);
    }
}
