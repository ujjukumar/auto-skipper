using System.ComponentModel;
using System.Runtime.InteropServices;
using AutoSkipper.Services;

namespace AutoSkipper.Input;

// Installs the low-level keyboard and mouse hooks and forwards events to the
// InputRouter. The hooks live on a dedicated high-priority thread with its own
// message loop: every key press and mouse move system-wide waits for these
// callbacks, so they must never queue behind UI work such as the log window.
internal sealed class GlobalHooks : IDisposable
{
    private readonly InputRouter _router;
    private readonly Native.HookProc _kbdProc;
    private readonly Native.HookProc _mouseProc;
    private IntPtr _hKbdHook = IntPtr.Zero;
    private IntPtr _hMouseHook = IntPtr.Zero;

    private readonly Thread _hookThread;
    private uint _hookThreadId;
    private Exception? _startError;

    public GlobalHooks(InputRouter router)
    {
        _router = router;
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

    private IntPtr HookCallbackKbd(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // An exception escaping a native callback would crash the process.
            try
            {
                var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                _router.OnKeyboardEvent((int)wParam, data.vkCode, data.flags);
            }
            catch (Exception ex)
            {
                Logger.LogErrorStatic($"Keyboard hook error: {ex.Message}");
            }
        }
        return Native.CallNextHookEx(_hKbdHook, nCode, wParam, lParam);
    }

    private IntPtr HookCallbackMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Only side-button presses matter; every other mouse event (including each
        // mouse move) passes straight through without being decoded.
        if (nCode >= 0 && (int)wParam == Native.WM_XBUTTONDOWN)
        {
            try
            {
                var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                _router.OnXButtonDown(data.mouseData, data.flags);
            }
            catch (Exception ex)
            {
                Logger.LogErrorStatic($"Mouse hook error: {ex.Message}");
            }
        }
        return Native.CallNextHookEx(_hMouseHook, nCode, wParam, lParam);
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
