using System.Collections.Generic;

namespace AutoSkipper.Models;

public class Macro
{
    public string Name { get; set; } = "New Macro";
    public List<ushort> TriggerKeys { get; set; } = new(); // Virtual Key Codes (combo)
    public List<MacroAction> Actions { get; set; } = new();

    public string GetTriggerDisplay()
    {
        if (TriggerKeys.Count == 0) return "None";
        return string.Join(" + ", TriggerKeys.Select(k => MacroAction.GetKeyName(k)));
    }

    public Macro Clone() => new()
    {
        Name = Name,
        TriggerKeys = new List<ushort>(TriggerKeys),
        Actions = Actions.Select(a => a.Clone()).ToList()
    };

    public override string ToString() => Name;
}
