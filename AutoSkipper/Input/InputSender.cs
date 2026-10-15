using System.Runtime.InteropServices;
using AutoSkipper.Services;
using AutoSkipper.Interfaces;

namespace AutoSkipper.Input;

// Each call builds fresh, zero-initialised INPUT structs on the stack, so there is
// no shared buffer to lock and no stale fields left over in the keyboard/mouse union.
internal sealed class InputSender : IInputSender
{
    private const ushort VK_F = 0x46;
    private static readonly int InputSize = Marshal.SizeOf<Native.Input>();

    public void TapF() => Send([Key(VK_F, 0), Key(VK_F, Native.KEYEVENTF_KEYUP)]);

    public void KeyDown(ushort vkCode) => Send([Key(vkCode, 0)]);

    public void KeyUp(ushort vkCode) => Send([Key(vkCode, Native.KEYEVENTF_KEYUP)]);

    public void LeftClick() => Send([Mouse(Native.MOUSEEVENTF_LEFTDOWN), Mouse(Native.MOUSEEVENTF_LEFTUP)]);

    public void MouseDown(ushort vkCode)
    {
        var (flags, data) = MouseEvent(vkCode, down: true);
        Send([Mouse(flags, data)]);
    }

    public void MouseUp(ushort vkCode)
    {
        var (flags, data) = MouseEvent(vkCode, down: false);
        Send([Mouse(flags, data)]);
    }

    private static void Send(ReadOnlySpan<Native.Input> inputs) =>
        Native.SendInput((uint)inputs.Length, inputs, InputSize);

    private static Native.Input Key(ushort vkCode, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion { ki = new Native.KeyboardInput { wVk = vkCode, dwFlags = flags } }
    };

    // mouseData selects the X button for MOUSEEVENTF_XDOWN/XUP.
    private static Native.Input Mouse(uint flags, uint mouseData = 0) => new()
    {
        type = Native.INPUT_MOUSE,
        u = new Native.InputUnion { mi = new Native.MouseInput { dwFlags = flags, mouseData = mouseData } }
    };

    private static (uint flags, uint mouseData) MouseEvent(ushort vkCode, bool down) => vkCode switch
    {
        0x02 => (down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP, 0u),
        0x04 => (down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP, 0u),
        0x05 => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, Native.XBUTTON1),
        0x06 => (down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP, Native.XBUTTON2),
        _ => (down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP, 0u)
    };
}
