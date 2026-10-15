using System.ComponentModel;
using System.Runtime.InteropServices;
using AutoSkipper.Models;
using AutoSkipper.Services;

namespace AutoSkipper.Input;

internal class GlobalHooks : IDisposable
{
    private const int VK_F7 = 0x76;
    private const int VK_F8 = 0x77;
    private const int VK_F9 = 0x78;
    private const ushort VK_XBUTTON1 = 0x05;
    private const ushort VK_XBUTTON2 = 0x06;

    private readonly Native.HookProc _kbdProc;
    private readonly Native.HookProc _mouseProc;
    private readonly IntPtr _hKbdHook = IntPtr.Zero;
    private readonly IntPtr _hMouseHook = IntPtr.Zero;
    private readonly Services.AutoSkipper _skipper;
    private readonly Services.MacroService _macroService;
    private volatile Macro[] _macros = [];

    // Physical key-down state. Low-level hooks get a fresh WM_KEYDOWN for every
    // auto-repeat with no repeat flag, so this is how repeats are told apart.
    private readonly bool[] _keysDown = new bool[256];

    public GlobalHooks(Services.AutoSkipper skipper, Services.MacroService macroService, IEnumerable<Macro> macros)
    {
        _skipper = skipper;
        _macroService = macroService;
        SetMacros(macros);
        _kbdProc = HookCallbackKbd;
        _mouseProc = HookCallbackMouse;

        IntPtr modHandle = Native.GetModuleHandle(null);

        _hKbdHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbdProc, modHandle, 0);
        if (_hKbdHook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set keyboard hook.");
        }
        
        _hMouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, modHandle, 0);
        if (_hMouseHook == IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hKbdHook);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set mouse hook.");
        }
    }

    // Replaces the active macros with a private copy, so edits in the macro
    // editor only take effect once they are saved.
    public void SetMacros(IEnumerable<Macro> macros) => _macros = macros.Select(m => m.Clone()).ToArray();

    private IntPtr HookCallbackKbd(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // An exception escaping a native callback would crash the process.
            try { HandleKeyboard((int)wParam, lParam); }
            catch (Exception ex) { Logger.LogErrorStatic($"Keyboard hook error: {ex.Message}"); }
        }
        return Native.CallNextHookEx(_hKbdHook, nCode, wParam, lParam);
    }

    private void HandleKeyboard(int msg, IntPtr lParam)
    {
        var hookStruct = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);

        // Ignore our own injected input to avoid macro re-trigger loops.
        if ((hookStruct.flags & Native.LLKHF_INJECTED) != 0) return;

        int vkCode = (int)hookStruct.vkCode;
        if ((uint)vkCode >= _keysDown.Length) return;

        if (msg is Native.WM_KEYUP or Native.WM_SYSKEYUP)
        {
            _keysDown[vkCode] = false;
            return;
        }

        // Keys pressed while Alt is held arrive as WM_SYSKEYDOWN, not WM_KEYDOWN.
        if (msg is not (Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)) return;
        if (_keysDown[vkCode]) return; // auto-repeat while held
        _keysDown[vkCode] = true;

        switch (vkCode)
        {
            case VK_F7: Logger.ToggleFileLogging(); break;
            case VK_F8: _skipper.ToggleRun(true); break;
            case VK_F9: _skipper.ToggleRun(false); break;
            default: TryRunMacro((ushort)vkCode); break;
        }
    }

    private IntPtr HookCallbackMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try { HandleMouse((int)wParam, lParam); }
            catch (Exception ex) { Logger.LogErrorStatic($"Mouse hook error: {ex.Message}"); }
        }
        return Native.CallNextHookEx(_hMouseHook, nCode, wParam, lParam);
    }

    private void HandleMouse(int msg, IntPtr lParam)
    {
        if (msg != Native.WM_XBUTTONDOWN) return;

        var hookStruct = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
        // Ignore our own injected input so macro-sent clicks don't trigger
        // the Mouse4/Mouse5 remaps or re-trigger macros.
        if ((hookStruct.flags & Native.LLMHF_INJECTED) != 0) return;

        int btn = (int)(hookStruct.mouseData >> 16);
        if (btn == 1) _skipper.HandleMouse4();
        else if (btn == 2) _skipper.HandleMouse5();
        else return;

        // Check macro combos that include mouse buttons
        TryRunMacro(btn == 1 ? VK_XBUTTON1 : VK_XBUTTON2);
    }

    private void TryRunMacro(ushort justPressed)
    {
        var macro = FindMatchingMacro(justPressed);
        // Macros only fire while the game is focused, so typing elsewhere can't trigger them.
        if (macro != null && _skipper.IsGameActive())
            _macroService.Run(macro);
    }

    /// <summary>
    /// Checks if any macro's full key combo is currently satisfied.
    /// <paramref name="justPressed"/> is the key that was just pressed (already down).
    /// All other keys in the combo are checked via GetAsyncKeyState.
    /// </summary>
    private Macro? FindMatchingMacro(ushort justPressed)
    {
        foreach (var macro in _macros)
        {
            var keys = macro.TriggerKeys;
            if (keys.Count == 0 || !keys.Contains(justPressed)) continue;

            bool allPressed = true;
            foreach (var vk in keys)
            {
                if (vk == justPressed) continue; // this one is already down
                // GetAsyncKeyState: high bit set means key is currently down
                if ((Native.GetAsyncKeyState(vk) & 0x8000) == 0)
                {
                    allPressed = false;
                    break;
                }
            }
            if (allPressed) return macro;
        }
        return null;
    }

    public void Dispose()
    {
        if (_hKbdHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hKbdHook);
        if (_hMouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hMouseHook);
    }
}
