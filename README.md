# Genshin AutoSkipper

A Windows tray app that skips Genshin Impact dialogue for you, plus a few input helpers: side-button actions and simple macros.

## Requirements

- Windows 10/11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Administrator rights. Genshin runs elevated, and Windows blocks normal apps from reading input for, or sending input to, elevated windows. The app asks for elevation when it starts.

## Usage

Run `Release/AutoSkipper.exe`. It starts paused, as an icon in the system tray.

| Input | Effect |
| :--- | :--- |
| `F8` | Start / resume skipping |
| `F9` | Pause |
| `F7` | Toggle writing the log to a file |
| `Mouse4` | Press `T` (a default macro you can edit or delete) |
| `Mouse5` | Press `F` rapidly for 4 seconds (unless a macro is bound to Mouse5) |
| Double-click tray icon | Start / pause |

- Skipping only happens while the game window is focused. If the game stays in the background for 2 minutes, skipping pauses itself.
- The tray icon shows a green dot while dialogue is being skipped.
- Right-click the tray icon → **Show Window** to see the logs and edit macros. Launching the exe again also opens the window; only one copy runs at a time.
- Start with `-v` (or `--verbose`) for detailed logs, including pixel readings.

## How it works

A few times per second the app reads some pixels of the game window: the auto-play icon, the dialogue option icons and a loading-screen pixel. Two matching readings in a row mean a dialogue is open. It then presses `F` (or occasionally left-clicks) with randomised, human-like timing: varied delays, occasional skipped or double presses, short bursts and breaks. Each press first re-checks the screen, so presses stop as soon as the dialogue ends.

Pixel positions are calculated from the game window's size and position, so different resolutions, ultrawide screens, windowed mode and secondary monitors work.

## Macros

In the **Macros** tab, create a macro, choose a trigger (optionally Ctrl/Shift/Alt plus a key, Mouse4 or Mouse5) and add actions:

- **Press**: tap a key or mouse button
- **Hold**: hold it down for a number of milliseconds
- **Delay**: wait a number of milliseconds

Changes take effect when you click **Save**; **Revert** discards them. Macros only run while the game is focused, holding the trigger key runs a macro once, and a macro won't start again until its current run finishes.

## Configuration

Settings live in `%AppData%\AutoSkipper\config.json`, created on first run:

| Setting | Default | Description |
| :--- | :--- | :--- |
| `WindowTitle` | `"Genshin Impact"` | Title of the game window. |
| `StandardDelayMin` / `StandardDelayMax` | `0.13` / `0.17` | Seconds between presses. |
| `ColorTolerance` | `10` | Allowed RGB difference when matching pixels (0–255). |
| `LeftClickChance` | `0.1` | Chance (0–1) of a left click instead of `F`. |
| `Macros` | Mouse4 → T | Easiest to edit in the Macros tab. |
| `ConfigVersion` | | Used to upgrade older files; leave as is. |

Other files in the same folder:

- `autoskip_dialogue.log`: the log file written while `F7` logging is on
- `crash.log`: details of unexpected errors
- `config.broken-<date>.json`: a config file that couldn't be read, kept before defaults replaced it
- `auto_marker.ico`: optional; replaces the tray icon

## Building

```
dotnet build AutoSkipper.slnx
dotnet test --solution AutoSkipper.slnx
dotnet publish AutoSkipper/AutoSkipper.csproj -p:PublishProfile=FolderProfile
```

The publish profile writes `Release/AutoSkipper.exe`.

| Folder | Contents |
| :--- | :--- |
| `AutoSkipper/Services` | `DialogueSkipper` (main loop), `DialogueDetector` (pixel checks), `MacroService`, `Logger`, `Native` (Win32 interop) |
| `AutoSkipper/Input` | `GlobalHooks` (low-level hooks on their own thread), `InputRouter` (hotkeys and macro triggers), `InputSender` (`SendInput`) |
| `AutoSkipper/Models` | Config, macros, pixel layout |
| `AutoSkipper/UI` | `TrayContext` (tray and app lifetime), `MainForm`, `LogView`, `MacroEditorView` |
| `AutoSkipper.Tests` | xUnit tests |
