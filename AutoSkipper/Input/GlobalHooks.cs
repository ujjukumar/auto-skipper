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
    private IntPtr _hKbdHook = IntPtr.Zero;
    private IntPtr _hMouseHook = IntPtr.Zero;
    private readonly Services.AutoSkipper _skipper;
    private readonly Services.MacroService _macroService;
    private volatile Macro[] _macros = [];

    // Physical key-down state. Low-level hooks get a fresh WM_KEYDOWN for every
    // auto-repeat with no repeat flag, so this is how repeats are told apart.
    private readonly bool[] _keysDown = new bool[256];

    // The hooks live on a dedicated high-priority thread with its own message
    // loop. Every key press and mouse move system-wide waits for our callbacks,
    // so they must never queue behind UI work such as the log window.
    private readonly Thread _hookThread;
    private uint _hookThreadId;
    private Exception? _startError;

    public GlobalHooks(Services.AutoSkipper skipper, Services.MacroService macroService, IEnumerable<Macro> macros)
    {
        _skipper = skipper;
        _macroService = macroService;
        SetMacros(macros);
        _kbdProc = HookCallbackKbd;
        _mouseProc = HookCallbackMouse;

        var started = new ManualResetEventSlim(false);
        _hookThread = new Thread(() => HookThreadMain(started))
        {
            IsBackground = true,
            Name = "AutoSkipper Input Hooks",
            Priority = ThreadPriority.Highest
        };
        _hookThread.Start();
        started.Wait();
        if (_startError != null) throw _startError;
    }

    private void HookThreadMain(ManualResetEventSlim started)
    {
        try
        {
            _hookThreadId = Native.GetCurrentThreadId();
            InstallHooks();
        }
        catch (Exception ex)
        {
            _startError = ex;
        }
        finally
        {
            started.Set();
        }
        if (_startError != null) return;

        try
        {
            // Hook callbacks are dispatched from inside GetMessage.
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(in msg);
                Native.DispatchMessage(in msg);
            }
        }
        catch (Exception ex)
        {
            Logger.LogErrorStatic($"Input hook thread stopped: {ex.Message}");
        }
        finally
        {
            Native.UnhookWindowsHookEx(_hKbdHook);
            Native.UnhookWindowsHookEx(_hMouseHook);
        }
    }

    private void InstallHooks()
    {
        IntPtr modHandle = Native.GetModuleHandle(null);

        _hKbdHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbdProc, modHandle, 0);
        if (_hKbdHook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set keyboard hook.");
        }
        
        _hMouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, modHandle, 0);
        if (_hMouseHook == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Native.UnhookWindowsHookEx(_hKbdHook);
            throw new Win32Exception(error, "Failed to set mouse hook.");
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
            // Opening/closing the log file is disk I/O; keep it off the hook thread.
            case VK_F7: ThreadPool.QueueUserWorkItem(_ => Logger.ToggleFileLogging()); break;
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
        if (btn == 1)
        {
            TryRunMacro(VK_XBUTTON1);
        }
        else if (btn == 2)
        {
            // A macro bound to Mouse5 replaces the built-in F burst.
            if (!TryRunMacro(VK_XBUTTON2)) _skipper.HandleMouse5();
        }
    }

    // Returns true if a macro matched, even when it didn't run because the game
    // isn't focused, so built-in actions on the same button stay suppressed.
    private bool TryRunMacro(ushort justPressed)
    {
        var macro = FindMatchingMacro(justPressed);
        if (macro == null) return false;
        // Macros only fire while the game is focused, so typing elsewhere can't trigger them.
        if (_skipper.IsGameActive())
            _macroService.Run(macro);
        return true;
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
        // Ending the message loop unhooks on the hook thread itself.
        if (_hookThreadId != 0 && _hookThread.IsAlive)
        {
            Native.PostThreadMessage(_hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread.Join(1000);
        }
    }
}
