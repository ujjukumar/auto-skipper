namespace AutoSkipper.Interfaces;

public interface IInputSender
{
    void TapF();
    void LeftClick();
    void KeyDown(ushort vkCode);
    void KeyUp(ushort vkCode);
    void MouseDown(ushort vkCode);
    void MouseUp(ushort vkCode);
}
