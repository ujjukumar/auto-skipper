using AutoSkipper.Models;
using AutoSkipper.UI;
using static AutoSkipper.Tests.TestHelpers;

namespace AutoSkipper.Tests;

public class MacroEditorViewTests
{
    // Hosts the editor in an off-screen form so Visible/handles behave like the app.
    private static void WithEditor(Action<MacroEditorView, ConfigStore, List<IReadOnlyList<Macro>>> test)
    {
        using var dir = new TempDirectory();
        RunSta(() =>
        {
            var store = ConfigStore.Load(dir.Path); // contains the default "Mouse4 -> T" macro
            var applied = new List<IReadOnlyList<Macro>>();
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(900, 580), ShowInTaskbar = false };
            var editor = new MacroEditorView(store, applied.Add);
            form.Controls.Add(editor);
            form.Show();
            test(editor, store, applied);
        });
    }

    private static ListBox MacroList(MacroEditorView e) => Field<ListBox>(e, "_macroList");

    [Fact]
    public void Edits_AreStaged_UntilSaved()
    {
        WithEditor((editor, store, applied) =>
        {
            MacroList(editor).SelectedIndex = 0;
            Assert.False(editor.HasUnsavedChanges);

            Field<TextBox>(editor, "_nameBox").Text = "Renamed";

            Assert.True(editor.HasUnsavedChanges);
            Assert.True(Field<Label>(editor, "_unsavedLabel").Visible);
            Assert.Equal("Mouse4 -> T", store.Config.Macros[0].Name);
            Assert.Empty(applied);
        });
    }

    [Fact]
    public void Revert_DiscardsEdits()
    {
        WithEditor((editor, store, _) =>
        {
            MacroList(editor).SelectedIndex = 0;
            Field<TextBox>(editor, "_nameBox").Text = "Renamed";
            Invoke(editor, "BtnAddMacro_Click", null, EventArgs.Empty);

            Field<Button>(editor, "_btnRevert").PerformClick();

            Assert.False(editor.HasUnsavedChanges);
            var remaining = MacroList(editor).Items.Cast<Macro>().ToList();
            Assert.Equal("Mouse4 -> T", Assert.Single(remaining).Name);
            Assert.False(Field<Label>(editor, "_unsavedLabel").Visible);
        });
    }

    [Fact]
    public void Save_WritesConfigAndAppliesMacros()
    {
        WithEditor((editor, store, applied) =>
        {
            Invoke(editor, "BtnAddMacro_Click", null, EventArgs.Empty);
            Field<ComboBox>(editor, "_buttonCombo").Text = "left click";
            Field<RadioButton>(editor, "_radioHold").Checked = true;
            Invoke(editor, "BtnAddButtonAction_Click", null, EventArgs.Empty);
            Field<CheckBox>(editor, "_chkCtrl").Checked = true;
            Field<ComboBox>(editor, "_triggerCombo").SelectedItem = "Q";

            var save = editor.SaveMacrosAsync();
            Assert.True(WaitUntil(() => save.IsCompleted));

            Assert.True(save.Result);
            Assert.False(editor.HasUnsavedChanges);
            var savedMacro = store.Config.Macros[1];
            Assert.Equal(new ushort[] { 0xA2, 0x51 }, savedMacro.TriggerKeys);
            Assert.Equal(MacroActionType.Hold, Assert.Single(savedMacro.Actions).Type);
            Assert.Equal(2, Assert.Single(applied).Count);

            // Editing after saving must not leak into what was saved/applied.
            Field<TextBox>(editor, "_nameBox").Text = "Edited later";
            Assert.NotEqual("Edited later", store.Config.Macros[1].Name);
            Assert.NotEqual("Edited later", applied[0][1].Name);
        });
    }

    [Fact]
    public void TriggerList_OffersSideButtonsButNotClicks()
    {
        WithEditor((editor, _, _) =>
        {
            var triggers = Field<ComboBox>(editor, "_triggerCombo").Items.Cast<string>().ToList();
            var buttons = Field<ComboBox>(editor, "_buttonCombo").Items.Cast<string>().ToList();

            Assert.Contains("Mouse4", triggers);
            Assert.Contains("Mouse5", triggers);
            Assert.DoesNotContain(triggers, t => t.Contains("Click"));
            Assert.Contains("Left Click", buttons);
        });
    }
}
