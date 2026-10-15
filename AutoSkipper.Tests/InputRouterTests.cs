using AutoSkipper.Input;
using AutoSkipper.Models;

namespace AutoSkipper.Tests;

public class InputRouterTests
{
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104;
    private const uint Injected = 0x10, MouseInjected = 0x01;
    private const uint VK_Q = 0x51, VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78;

    private bool _gameActive = true;
    private readonly HashSet<ushort> _heldKeys = new();
    private readonly List<string> _calls = new();
    private readonly InputRouter _router;

    public InputRouterTests()
    {
        _router = new InputRouter(
            isGameActive: () => _gameActive,
            runMacro: m => _calls.Add($"macro:{m.Name}"),
            toggleFileLogging: () => _calls.Add("F7"),
            setRunning: on => _calls.Add(on ? "run" : "pause"),
            mouse5Burst: () => _calls.Add("burst"),
            isKeyDown: vk => _heldKeys.Contains(vk));
    }

    private static Macro MacroOn(string name, params ushort[] keys) =>
        new() { Name = name, TriggerKeys = keys.ToList(), Actions = { new MacroAction { Type = MacroActionType.Press, KeyCode = 0x5A } } };

    private void Key(int message, uint vk, uint flags = 0) => _router.OnKeyboardEvent(message, vk, flags);
    private void Side(int button, uint flags = 0) => _router.OnXButtonDown((uint)button << 16, flags);

    [Fact]
    public void HeldTriggerKey_FiresMacroOnce_UntilReleased()
    {
        _router.SetMacros([MacroOn("Q", 0x51)]);

        Key(WM_KEYDOWN, VK_Q);
        Key(WM_KEYDOWN, VK_Q); // auto-repeat
        Key(WM_KEYDOWN, VK_Q);
        Key(WM_KEYUP, VK_Q);
        Key(WM_SYSKEYDOWN, VK_Q); // pressed again while Alt is held

        Assert.Equal(new[] { "macro:Q", "macro:Q" }, _calls);
    }

    [Fact]
    public void InjectedInput_IsIgnored()
    {
        _router.SetMacros([MacroOn("Q", 0x51)]);

        Key(WM_KEYDOWN, VK_Q, Injected);
        Side(2, MouseInjected);

        Assert.Empty(_calls);
    }

    [Fact]
    public void Macro_DoesNotRunOutsideTheGame()
    {
        _router.SetMacros([MacroOn("Q", 0x51)]);
        _gameActive = false;

        Key(WM_KEYDOWN, VK_Q);

        Assert.Empty(_calls);
    }

    [Fact]
    public void Combo_RequiresTheOtherKeysHeld()
    {
        _router.SetMacros([MacroOn("Ctrl+Q", 0xA2, 0x51)]);

        Key(WM_KEYDOWN, VK_Q);
        Key(WM_KEYUP, VK_Q);
        _heldKeys.Add(0xA2);
        Key(WM_KEYDOWN, VK_Q);

        Assert.Equal(new[] { "macro:Ctrl+Q" }, _calls);
    }

    [Fact]
    public void Hotkeys_WorkEverywhere_AndIgnoreRepeat()
    {
        _gameActive = false;

        Key(WM_KEYDOWN, VK_F8);
        Key(WM_KEYDOWN, VK_F9);
        Key(WM_KEYDOWN, VK_F7);
        Key(WM_KEYDOWN, VK_F7); // held: must not toggle file logging back off

        Assert.Equal(new[] { "run", "pause", "F7" }, _calls);
    }

    [Fact]
    public void Mouse4_RunsItsMacro()
    {
        _router.SetMacros([MacroOn("Mouse4 -> T", InputRouter.VK_XBUTTON1)]);

        Side(1);

        Assert.Equal(new[] { "macro:Mouse4 -> T" }, _calls);
    }

    [Fact]
    public void Mouse5_WithoutMacro_StartsBurst()
    {
        Side(2);
        Assert.Equal(new[] { "burst" }, _calls);
    }

    [Fact]
    public void Mouse5_Macro_ReplacesBurst_EvenOutsideTheGame()
    {
        _router.SetMacros([MacroOn("M5", InputRouter.VK_XBUTTON2)]);

        Side(2);
        _gameActive = false;
        Side(2);

        Assert.Equal(new[] { "macro:M5" }, _calls);
    }

    [Fact]
    public void SetMacros_KeepsAPrivateCopy()
    {
        var macros = new List<Macro> { MacroOn("Q", 0x51) };
        _router.SetMacros(macros);

        macros[0].TriggerKeys[0] = 0x45; // edit after applying
        Key(WM_KEYDOWN, VK_Q);

        Assert.Equal(new[] { "macro:Q" }, _calls);
    }
}
