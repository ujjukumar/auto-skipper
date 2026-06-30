namespace AutoSkipper.Interfaces;

public interface IInputSender
{
    void TapT();
    void TapF();
    void PressKey(ushort vkCode);
    void KeyDown(ushort vkCode);
    void KeyUp(ushort vkCode);
    void LeftClick();
    void RightClick();
}
