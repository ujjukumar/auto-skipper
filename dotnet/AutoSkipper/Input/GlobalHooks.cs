using System.ComponentModel;
using System.Runtime.InteropServices;
using AutoSkipper.Services;

namespace AutoSkipper.Input;

internal class GlobalHooks : IDisposable
{
    private const int VK_F7 = 0x76;
    private const int VK_F8 = 0x77;
    private const int VK_F9 = 0x78;
    
    private readonly Native.HookProc _kbdProc;
    private readonly Native.HookProc _mouseProc;
    private readonly IntPtr _hKbdHook = IntPtr.Zero;
    private readonly IntPtr _hMouseHook = IntPtr.Zero;
    private readonly Services.AutoSkipper _skipper;

    public GlobalHooks(Services.AutoSkipper skipper)
    {
        _skipper = skipper;
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
            // Clean up the successfully installed hook before throwing
            Native.UnhookWindowsHookEx(_hKbdHook);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set mouse hook.");
        }
    }

    private IntPtr HookCallbackKbd(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)Native.WM_KEYDOWN)
        {
            var hookStruct = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int vkCode = (int)hookStruct.vkCode;
            
            switch (vkCode)
            {
                case VK_F7: Logger.ToggleFileLogging(); break;
                case VK_F8: _skipper.ToggleRun(true); break;
                case VK_F9: _skipper.ToggleRun(false); break;
            }
        }
        return Native.CallNextHookEx(_hKbdHook, nCode, wParam, lParam);
    }

    private IntPtr HookCallbackMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)Native.WM_XBUTTONDOWN))
        {
            var hookStruct = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            int btn = (int)(hookStruct.mouseData >> 16);
            if (btn == 1) _skipper.HandleMouse4(); // XBUTTON1
            if (btn == 2) _skipper.HandleMouse5(); // XBUTTON2
        }
        return Native.CallNextHookEx(_hMouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hKbdHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hKbdHook);
        if (_hMouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hMouseHook);
    }
}

