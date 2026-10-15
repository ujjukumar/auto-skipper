using AutoSkipper.Models;
using AutoSkipper.Services;

namespace AutoSkipper.Input;

// Decides what each physical key or side-button press does: the F7/F8/F9
// hotkeys, macros, and the Mouse5 burst. GlobalHooks only installs the hooks and
// passes raw events here, which keeps this logic testable without system hooks.
internal sealed class InputRouter
{
    public const ushort VK_XBUTTON1 = 0x05;
    public const ushort VK_XBUTTON2 = 0x06;
    private const int VK_F7 = 0x76;
    private const int VK_F8 = 0x77;
    private const int VK_F9 = 0x78;

    private readonly Func<bool> _isGameActive;
    private readonly Action<Macro> _runMacro;
    private readonly Action _toggleFileLogging;
    private readonly Action<bool> _setRunning;
    private readonly Action _mouse5Burst;
    private readonly Func<ushort, bool> _isKeyDown;
    private volatile Macro[] _macros = [];

    // Physical key-down state. Low-level hooks get a fresh WM_KEYDOWN for every
    // auto-repeat with no repeat flag, so this is how repeats are told apart.
    // Only touched on the hook thread.
    private readonly bool[] _keysDown = new bool[256];

    public InputRouter(Func<bool> isGameActive, Action<Macro> runMacro, Action toggleFileLogging,
        Action<bool> setRunning, Action mouse5Burst, Func<ushort, bool>? isKeyDown = null)
    {
        _isGameActive = isGameActive;
        _runMacro = runMacro;
        _toggleFileLogging = toggleFileLogging;
        _setRunning = setRunning;
        _mouse5Burst = mouse5Burst;
        _isKeyDown = isKeyDown ?? (vk => (Native.GetAsyncKeyState(vk) & 0x8000) != 0);
    }

    // Replaces the active macros with a private copy, so edits in the macro
    // editor only take effect once they are saved.
    public void SetMacros(IEnumerable<Macro> macros) => _macros = macros.Select(m => m.Clone()).ToArray();

    // Raw WM_KEY*/WM_SYSKEY* event from the low-level keyboard hook.
    public void OnKeyboardEvent(int message, uint vkCode, uint flags)
    {
        // Ignore our own injected input to avoid macro re-trigger loops.
        if ((flags & Native.LLKHF_INJECTED) != 0) return;
        if (vkCode >= _keysDown.Length) return;

        if (message is Native.WM_KEYUP or Native.WM_SYSKEYUP)
        {
            _keysDown[vkCode] = false;
            return;
        }

        // Keys pressed while Alt is held arrive as WM_SYSKEYDOWN, not WM_KEYDOWN.
        if (message is not (Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)) return;
        if (_keysDown[vkCode]) return; // auto-repeat while held
        _keysDown[vkCode] = true;

        switch ((int)vkCode)
        {
            case VK_F7: _toggleFileLogging(); break;
            case VK_F8: _setRunning(true); break;
            case VK_F9: _setRunning(false); break;
            default: TryRunMacro((ushort)vkCode); break;
        }
    }

    // WM_XBUTTONDOWN from the low-level mouse hook.
    public void OnXButtonDown(uint mouseData, uint flags)
    {
        // Ignore our own injected input so macro-sent clicks can't re-trigger macros.
        if ((flags & Native.LLMHF_INJECTED) != 0) return;

        int button = (int)(mouseData >> 16);
        if (button == 1)
        {
            TryRunMacro(VK_XBUTTON1);
        }
        else if (button == 2)
        {
            // A macro bound to Mouse5 replaces the built-in F burst.
            if (!TryRunMacro(VK_XBUTTON2)) _mouse5Burst();
        }
    }

    // Returns true if a macro matched, even when it didn't run because the game
    // isn't focused, so built-in actions on the same button stay suppressed.
    private bool TryRunMacro(ushort justPressed)
    {
        var macro = FindMatchingMacro(justPressed);
        if (macro == null) return false;
        // Macros only fire while the game is focused, so typing elsewhere can't trigger them.
        if (_isGameActive()) _runMacro(macro);
        return true;
    }

    // The first macro whose whole combo is held: justPressed plus every other
    // trigger key currently down.
    private Macro? FindMatchingMacro(ushort justPressed)
    {
        foreach (var macro in _macros)
        {
            var keys = macro.TriggerKeys;
            if (keys.Count == 0 || !keys.Contains(justPressed)) continue;
            if (keys.All(vk => vk == justPressed || _isKeyDown(vk))) return macro;
        }
        return null;
    }
}
