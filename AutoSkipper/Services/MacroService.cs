using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoSkipper.Interfaces;
using AutoSkipper.Models;

namespace AutoSkipper.Services;

internal class MacroService
{
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
            switch (action.Type)
            {
                case MacroActionType.KeyPress:
                    _inputSender.PressKey(action.KeyCode);
                    break;
                case MacroActionType.KeyDown:
                    _inputSender.KeyDown(action.KeyCode);
                    break;
                case MacroActionType.KeyUp:
                    _inputSender.KeyUp(action.KeyCode);
                    break;
                case MacroActionType.LeftClick:
                    _inputSender.LeftClick();
                    break;
                case MacroActionType.RightClick:
                    _inputSender.RightClick();
                    break;
                case MacroActionType.Delay:
                    await Task.Delay(action.DelayMs);
                    break;
            }
        }
    }
}
