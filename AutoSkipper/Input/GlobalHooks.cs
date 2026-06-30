using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Linq;
using AutoSkipper.Models;
using AutoSkipper.Services;

namespace AutoSkipper.Input;

internal class GlobalHooks : IDisposable
{
    private const int VK_F7 = 0x76;
    private const int VK_F8 = 0x77;
    private const int VK_F9 = 0x78;
    private const int VK_ESCAPE = 0x1B;
    
    private readonly Native.HookProc _kbdProc;
    private readonly Native.HookProc _mouseProc;
    private readonly IntPtr _hKbdHook = IntPtr.Zero;
    private readonly IntPtr _hMouseHook = IntPtr.Zero;
    private readonly Services.AutoSkipper _skipper;
    private readonly Services.MacroService _macroService;
    private readonly List<Macro> _macros;

    // Recording state
    public bool IsRecordingTrigger { get; set; }
    public bool IsRecordingActions { get; set; }

    /// <summary>Fires when a key/mouse button is pressed during trigger recording. Param: virtual key code.</summary>
    public event Action<ushort>? OnTriggerKeyRecorded;

    /// <summary>Fires for each input during action recording. Params: actionType, vkCode (0 for clicks).</summary>
    public event Action<MacroActionType, ushort>? OnActionInputRecorded;

    /// <summary>Fires when Escape is pressed during action recording to signal stop.</summary>
    public event Action? OnRecordingStopped;

    public GlobalHooks(Services.AutoSkipper skipper, Services.MacroService macroService, List<Macro> macros)
    {
        _skipper = skipper;
        _macroService = macroService;
        _macros = macros;
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

    private IntPtr HookCallbackKbd(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int vkCode = (int)hookStruct.vkCode;
            bool isKeyDown = wParam == (IntPtr)Native.WM_KEYDOWN;
            bool isKeyUp = wParam == (IntPtr)Native.WM_KEYUP;

            // --- Trigger recording mode ---
            if (IsRecordingTrigger && isKeyDown)
            {
                IsRecordingTrigger = false;
                OnTriggerKeyRecorded?.Invoke((ushort)vkCode);
                return (IntPtr)1; // Suppress the key
            }

            // --- Action recording mode ---
            if (IsRecordingActions)
            {
                // Escape stops recording
                if (vkCode == VK_ESCAPE && isKeyDown)
                {
                    IsRecordingActions = false;
                    OnRecordingStopped?.Invoke();
                    return (IntPtr)1;
                }

                if (isKeyDown)
                    OnActionInputRecorded?.Invoke(MacroActionType.KeyDown, (ushort)vkCode);
                else if (isKeyUp)
                    OnActionInputRecorded?.Invoke(MacroActionType.KeyUp, (ushort)vkCode);

                return (IntPtr)1; // Suppress
            }

            // --- Normal mode ---
            if (isKeyDown)
            {
                switch (vkCode)
                {
                    case VK_F7: Logger.ToggleFileLogging(); break;
                    case VK_F8: _skipper.ToggleRun(true); break;
                    case VK_F9: _skipper.ToggleRun(false); break;
                    default:
                        var macro = FindMatchingMacro((ushort)vkCode);
                        if (macro != null)
                        {
                            _ = _macroService.ExecuteMacroAsync(macro);
                        }
                        break;
                }
            }
        }
        return Native.CallNextHookEx(_hKbdHook, nCode, wParam, lParam);
    }

    private IntPtr HookCallbackMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;

            // --- Trigger recording: capture mouse buttons too ---
            if (IsRecordingTrigger)
            {
                if (msg == Native.WM_XBUTTONDOWN)
                {
                    var hookStruct = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    int btn = (int)(hookStruct.mouseData >> 16);
                    ushort vk = btn == 1 ? (ushort)0x05 : (ushort)0x06; // VK_XBUTTON1 / VK_XBUTTON2
                    IsRecordingTrigger = false;
                    OnTriggerKeyRecorded?.Invoke(vk);
                    return (IntPtr)1;
                }
                // Don't capture left/right click as triggers — too easy to accidentally set
            }

            // --- Action recording: capture clicks ---
            if (IsRecordingActions)
            {
                if (msg == Native.WM_LBUTTONDOWN)
                {
                    OnActionInputRecorded?.Invoke(MacroActionType.LeftClick, 0);
                    return (IntPtr)1;
                }
                if (msg == (int)0x0204) // WM_RBUTTONDOWN
                {
                    OnActionInputRecorded?.Invoke(MacroActionType.RightClick, 0);
                    return (IntPtr)1;
                }
                return Native.CallNextHookEx(_hMouseHook, nCode, wParam, lParam);
            }

            // --- Normal mode ---
            if (msg == Native.WM_XBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                int btn = (int)(hookStruct.mouseData >> 16);
                if (btn == 1) _skipper.HandleMouse4();
                if (btn == 2) _skipper.HandleMouse5();

                // Check macro combos that include mouse buttons
                ushort mouseVk = btn == 1 ? (ushort)0x05 : (ushort)0x06;
                var macro = FindMatchingMacro(mouseVk);
                if (macro != null)
                    _ = _macroService.ExecuteMacroAsync(macro);
            }
        }
        return Native.CallNextHookEx(_hMouseHook, nCode, wParam, lParam);
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
