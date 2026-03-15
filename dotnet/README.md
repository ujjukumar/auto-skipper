# Genshin AutoSkipper

Genshin AutoSkipper is a .NET 10 application designed to automate the dialogue skipping process in Genshin Impact. It detects dialogue sequences via pixel monitoring and simulates keyboard and mouse inputs.

## Features

*   **Automatic Skipping**: Simulates 'F' key presses and Left Mouse clicks.
*   **Intelligent Detection**: Uses pixel color analysis for dialogue detection, adaptable for various resolutions.
*   **Human-like Inputs**: Randomized delays, burst modes, and randomized action types ('F' or Click).
*   **Global Hotkeys**:
    *   `F7`: Toggle file logging.
    *   `F8`: Start auto-skipping.
    *   `F9`: Pause auto-skipping.
*   **Mouse Remapping**:
    *   `Mouse 4`: Remapped to 'T'.
    *   `Mouse 5`: Initiates a rapid 'F' burst.

## Configuration

Settings can be adjusted in `%AppData%\AutoSkipper\config.json`:

| Setting | Default | Description |
| :--- | :--- | :--- |
| `WindowTitle` | "Genshin Impact" | Window title for detection. |
| `StandardDelayMin` | 0.13 | Min delay (sec) between inputs. |
| `StandardDelayMax` | 0.17 | Max delay (sec) between inputs. |
| `ColorTolerance` | 10 | RGB color matching tolerance (0-255). |
| `LeftClickChance` | 0.1 | Prob. (0-1) of Left Click instead of 'F'. |

---

