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
    public void LeftClick()
    {
        lock (_inputLock)
        {
            _inputPool[0].type = Native.INPUT_MOUSE;
            _inputPool[0].u.mi.dwFlags = Native.MOUSEEVENTF_LEFTDOWN;
            _inputPool[0].u.mi.dwExtraInfo = IntPtr.Zero;

            _inputPool[1].type = Native.INPUT_MOUSE;
            _inputPool[1].u.mi.dwFlags = Native.MOUSEEVENTF_LEFTUP;
            _inputPool[1].u.mi.dwExtraInfo = IntPtr.Zero;

            Native.SendInput(2, _inputPool, Marshal.SizeOf<Native.Input>());
        }
    }
}
