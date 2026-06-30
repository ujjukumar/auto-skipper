namespace AutoSkipper.Models;

public enum MacroActionType
{
    KeyPress,
    KeyDown,
    KeyUp,
    LeftClick,
    RightClick,
    Delay
}

public class MacroAction
{
    public MacroActionType Type { get; set; }
    public ushort KeyCode { get; set; } // For KeyPress, KeyDown, KeyUp
    public int DelayMs { get; set; } // For Delay

    public string GetDescription() => Type switch
    {
        MacroActionType.KeyPress => $"KeyPress {GetKeyName(KeyCode)}",
        MacroActionType.KeyDown => $"KeyDown {GetKeyName(KeyCode)}",
        MacroActionType.KeyUp => $"KeyUp {GetKeyName(KeyCode)}",
        MacroActionType.LeftClick => "Left Click",
        MacroActionType.RightClick => "Right Click",
        MacroActionType.Delay => $"Delay {DelayMs}ms",
        _ => Type.ToString()
    };

    public static string GetKeyName(ushort vkCode) => vkCode switch
    {
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x10 => "Shift",
        0x11 => "Ctrl", 0x12 => "Alt", 0x13 => "Pause", 0x14 => "CapsLock",
        0x1B => "Esc", 0x20 => "Space", 0x21 => "PgUp", 0x22 => "PgDn",
        0x23 => "End", 0x24 => "Home", 0x25 => "Left", 0x26 => "Up",
        0x27 => "Right", 0x28 => "Down", 0x2D => "Insert", 0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vkCode).ToString(), // 0-9
        >= 0x41 and <= 0x5A => ((char)vkCode).ToString(), // A-Z
        >= 0x60 and <= 0x69 => $"Num{vkCode - 0x60}",   // Numpad 0-9
        0x6A => "Num*", 0x6B => "Num+", 0x6D => "Num-", 0x6E => "Num.", 0x6F => "Num/",
        >= 0x70 and <= 0x7B => $"F{vkCode - 0x6F}",       // F1-F12
        0xA0 => "LShift", 0xA1 => "RShift", 0xA2 => "LCtrl", 0xA3 => "RCtrl",
        0xA4 => "LAlt", 0xA5 => "RAlt",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/",
        0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        0x01 => "LClick", 0x02 => "RClick", 0x04 => "MClick",
        0x05 => "XButton1", 0x06 => "XButton2",
        _ => $"0x{vkCode:X2}"
    };
}
