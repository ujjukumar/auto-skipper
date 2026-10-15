namespace AutoSkipper.Models;

using System.Collections.Generic;

internal class ConfigData
{
    private const int MinColorTolerance = 0;
    private const int MaxColorTolerance = 255;
    private const double MinDelay = 0.01;
    private const double MaxDelay = 5.0;
    private const string DefaultWindowTitle = "Genshin Impact";
    private const int CurrentConfigVersion = 1;
    private const ushort VK_XBUTTON1 = 0x05;
    private const ushort VK_T = 0x54;

    // Missing in files written before versioning, which reads as 0.
    public int ConfigVersion { get; set; }
    public string WindowTitle { get; set; } = DefaultWindowTitle;
    public double StandardDelayMin { get; set; } = 0.13;
    public double StandardDelayMax { get; set; } = 0.17;
    public int ColorTolerance { get; set; } = 10;
    public double LeftClickChance { get; set; } = 0.1;
    public List<Macro> Macros { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WindowTitle)) WindowTitle = DefaultWindowTitle;
        StandardDelayMin = Math.Clamp(StandardDelayMin, MinDelay, MaxDelay);
        StandardDelayMax = Math.Clamp(StandardDelayMax, MinDelay, MaxDelay);
        if (StandardDelayMin > StandardDelayMax) (StandardDelayMin, StandardDelayMax) = (StandardDelayMax, StandardDelayMin);
        ColorTolerance = Math.Clamp(ColorTolerance, MinColorTolerance, MaxColorTolerance);
        LeftClickChance = Math.Clamp(LeftClickChance, 0.0, 1.0);

        // Hand-edited JSON can contain nulls (e.g. "Actions": null); normalise them.
        Macros ??= new();
        Macros.RemoveAll(m => m == null);
        foreach (var macro in Macros)
        {
            macro.Name ??= "New Macro";
            macro.TriggerKeys ??= new();
            macro.Actions ??= new();
            macro.Actions.RemoveAll(a => a == null);
            foreach (var action in macro.Actions)
            {
                action.DurationMs = Math.Max(0, action.DurationMs);
                action.DelayMs = Math.Max(0, action.DelayMs);
            }
        }
    }

    // Upgrades configs written by older versions. Returns true if anything changed.
    public bool Migrate()
    {
        if (ConfigVersion >= CurrentConfigVersion) return false;

        if (ConfigVersion < 1)
        {
            // v1: the hardcoded Mouse4 -> T remap became a regular macro that can be
            // edited or removed. Skipped if Mouse4 is already bound to something.
            bool mouse4Bound = Macros.Any(m => m.TriggerKeys.Count == 1 && m.TriggerKeys[0] == VK_XBUTTON1);
            if (!mouse4Bound)
            {
                Macros.Insert(0, new Macro
                {
                    Name = "Mouse4 -> T",
                    TriggerKeys = { VK_XBUTTON1 },
                    Actions = { new MacroAction { Type = MacroActionType.Press, KeyCode = VK_T } }
                });
            }
        }

        ConfigVersion = CurrentConfigVersion;
        return true;
    }
}
