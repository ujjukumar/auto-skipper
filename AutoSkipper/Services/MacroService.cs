using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoSkipper.Interfaces;
using AutoSkipper.Models;

namespace AutoSkipper.Services;

internal class MacroService
{
    // A Press holds the button briefly so the game registers it, and every
    // action is followed by a small gap so back-to-back presses aren't collapsed
    // into a single input frame.
    private const int PressHoldMs = 45;
    private const int InterActionGapMs = 45;

    private readonly IInputSender _inputSender;
    private readonly ILogger _logger;

    public MacroService(IInputSender inputSender, ILogger logger)
    {
        _inputSender = inputSender;
        _logger = logger;
    }

    public async Task ExecuteMacroAsync(Macro macro)
    {
        _logger.Log($"Executing Macro: {macro.Name}");
        foreach (var action in macro.Actions)
        {
            _logger.Log($"  \u2192 {action.GetDescription()}");
            switch (action.Type)
            {
                case MacroActionType.Press:
                    ButtonDown(action.KeyCode);
                    await Task.Delay(PressHoldMs);
                    ButtonUp(action.KeyCode);
                    await Task.Delay(InterActionGapMs);
                    break;
                case MacroActionType.Hold:
                    ButtonDown(action.KeyCode);
                    await Task.Delay(Math.Max(0, action.DurationMs));
                    ButtonUp(action.KeyCode);
                    await Task.Delay(InterActionGapMs);
                    break;
                case MacroActionType.Delay:
                    await Task.Delay(Math.Max(0, action.DelayMs));
                    break;
            }
        }
    }

    private static bool IsMouse(ushort vkCode) => vkCode is 0x01 or 0x02 or 0x04 or 0x05 or 0x06;

    private void ButtonDown(ushort vkCode)
    {
        if (IsMouse(vkCode)) _inputSender.MouseDown(vkCode);
        else _inputSender.KeyDown(vkCode);
    }

    private void ButtonUp(ushort vkCode)
    {
        if (IsMouse(vkCode)) _inputSender.MouseUp(vkCode);
        else _inputSender.KeyUp(vkCode);
    }
}
