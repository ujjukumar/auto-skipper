using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<Macro, byte> _running = new();

    public MacroService(IInputSender inputSender, ILogger logger)
    {
        _inputSender = inputSender;
        _logger = logger;
    }

    // Starts the macro on the thread pool, off the hook thread. A macro that is
    // already running is not started again, so re-pressing its trigger mid-run
    // can't stack overlapping copies.
    public void Run(Macro macro)
    {
        if (!_running.TryAdd(macro, 0))
        {
            _logger.LogDebug(() => $"Macro already running: {macro.Name}");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(macro);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Macro '{macro.Name}' failed: {ex.Message}");
            }
            finally
            {
                _running.TryRemove(macro, out _);
            }
        });
    }

    private async Task ExecuteAsync(Macro macro)
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

    private void ButtonDown(ushort vkCode)
    {
        if (MacroAction.IsMouseKey(vkCode)) _inputSender.MouseDown(vkCode);
        else _inputSender.KeyDown(vkCode);
    }

    private void ButtonUp(ushort vkCode)
    {
        if (MacroAction.IsMouseKey(vkCode)) _inputSender.MouseUp(vkCode);
        else _inputSender.KeyUp(vkCode);
    }
}
