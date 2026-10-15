using AutoSkipper.Models;
using AutoSkipper.Services;

namespace AutoSkipper.Tests;

public class MacroServiceTests
{
    private readonly FakeInputSender _input = new();
    private readonly MacroService _service;

    public MacroServiceTests() => _service = new MacroService(_input, new TestLogger());

    [Fact]
    public void PressAndHold_SendDownThenUp_ForKeysAndMouseButtons()
    {
        var macro = new Macro
        {
            Actions =
            {
                new MacroAction { Type = MacroActionType.Press, KeyCode = 0x5A },
                new MacroAction { Type = MacroActionType.Delay, DelayMs = 10 },
                new MacroAction { Type = MacroActionType.Hold, KeyCode = 0x06, DurationMs = 30 },
                new MacroAction { Type = MacroActionType.Press, KeyCode = 0x01 },
            }
        };

        _service.Run(macro);

        Assert.True(TestHelpers.WaitUntil(() => _input.Events == "KD5A KU5A MD06 MU06 MD01 MU01"), _input.Events);
    }

    [Fact]
    public void RunningMacro_IsNotStartedAgain_ButCanRunAfterFinishing()
    {
        var macro = new Macro { Actions = { new MacroAction { Type = MacroActionType.Press, KeyCode = 0x5A } } };

        _service.Run(macro);
        _service.Run(macro); // still running: ignored
        Assert.True(TestHelpers.WaitUntil(() => _input.Events == "KD5A KU5A"), _input.Events);
        Thread.Sleep(100); // let the first run finish its trailing gap

        _service.Run(macro);

        Assert.True(TestHelpers.WaitUntil(() => _input.Count("KD5A") == 2), _input.Events);
    }
}
