using System.Runtime.InteropServices;
using AutoSkipper.Services;
using AutoSkipper.Interfaces;

namespace AutoSkipper.Input;

internal class InputSender : IInputSender
{
    private readonly Native.Input[] _inputPool = new Native.Input[2];
    private readonly object _inputLock = new();
    
    public InputSender()
    {
        _inputPool[0] = new Native.Input();
        _inputPool[1] = new Native.Input();
    }
    
    public void PressKey(ushort vkCode)
    {
        lock (_inputLock)
        {
            _inputPool[0].type = Native.INPUT_KEYBOARD;
            _inputPool[0].u.ki.wVk = vkCode;
            _inputPool[0].u.ki.dwFlags = 0; // Down

            _inputPool[1].type = Native.INPUT_KEYBOARD;
            _inputPool[1].u.ki.wVk = vkCode;
            _inputPool[1].u.ki.dwFlags = Native.KEYEVENTF_KEYUP; // Up

            Native.SendInput(2, _inputPool, Marshal.SizeOf<Native.Input>());
        }
    }
    
    public void TapT() => PressKey(0x54); // T
    public void TapF() => PressKey(0x46); // F

    public void KeyDown(ushort vkCode)
    {
        lock (_inputLock)
        {
            _inputPool[0].type = Native.INPUT_KEYBOARD;
            _inputPool[0].u.ki.wVk = vkCode;
            _inputPool[0].u.ki.dwFlags = 0;
            Native.SendInput(1, _inputPool, Marshal.SizeOf<Native.Input>());
        }
    }

    public void KeyUp(ushort vkCode)
    {
        lock (_inputLock)
        {
            _inputPool[0].type = Native.INPUT_KEYBOARD;
            _inputPool[0].u.ki.wVk = vkCode;
            _inputPool[0].u.ki.dwFlags = Native.KEYEVENTF_KEYUP;
            Native.SendInput(1, _inputPool, Marshal.SizeOf<Native.Input>());
        }
    }

    public void LeftClick()
    {
        lock (_inputLock)
        {
            SetMouse(0, Native.MOUSEEVENTF_LEFTDOWN);
            SetMouse(1, Native.MOUSEEVENTF_LEFTUP);
            Native.SendInput(2, _inputPool, Marshal.SizeOf<Native.Input>());
        }
    }

    public void RightClick()
    {
        lock (_inputLock)
        {
            SetMouse(0, Native.MOUSEEVENTF_RIGHTDOWN);
            SetMouse(1, Native.MOUSEEVENTF_RIGHTUP);
            Native.SendInput(2, _inputPool, Marshal.SizeOf<Native.Input>());
        }
    }

    public void MouseDown(ushort vkCode)
    {
        var (flags, data) = MouseEvent(vkCode, down: true);
        lock (_inputLock)
        {
            SetMouse(0, flags, data);
            Native.SendInput(1, _inputPool, Marshal.SizeOf<Native.Input>());
        }
    }

    public void MouseUp(ushort vkCode)
    {
        var (flags, data) = MouseEvent(vkCode, down: false);
        lock (_inputLock)
        {
            SetMouse(0, flags, data);
            Native.SendInput(1, _inputPool, Marshal.SizeOf<Native.Input>());
        }
    }

    // Fully initialises a mouse input slot. Zeroing dx/dy is required because the
    // pool is shared with keyboard input via a union and can carry stale values
    // that would otherwise move the cursor. mouseData selects the X button.
    private void SetMouse(int index, uint flags, uint mouseData = 0)
    {
        _inputPool[index].type = Native.INPUT_MOUSE;
        _inputPool[index].u.mi.dx = 0;
        _inputPool[index].u.mi.dy = 0;
        _inputPool[index].u.mi.mouseData = mouseData;
        _inputPool[index].u.mi.time = 0;
        _inputPool[index].u.mi.dwFlags = flags;
        _inputPool[index].u.mi.dwExtraInfo = IntPtr.Zero;
    }

    private static (uint flags, uint mouseData) MouseEvent(ushort vkCode, bool down) => vkCode switch
    {
        0x02 => (down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP, 0u),
        0x04 => (down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP, 0u),
        0x05 => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, Native.XBUTTON1),
        0x06 => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, Native.XBUTTON2),
        _ => (down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP, 0u)
    };
}
