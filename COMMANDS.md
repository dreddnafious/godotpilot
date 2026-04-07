# GodotPilot Command Reference

All commands are invoked from a game project as `tools/gdpilot <subcommand> [args]`. The CLI returns JSON on stdout and human-readable messages on stderr. Pass `--pretty` for indented JSON.

The commands below are **built-in** and always available when the autoload is loaded. For game-specific commands (added via `RegisterCommand`), see [§ Registering game commands](#registering-game-commands).

---

## Lifecycle

| Command | Description |
|---|---|
| `setup [--path PATH]` | Find a Godot binary and write `.godot_bin` config in the project. Scans `GODOT_BIN`, PATH, common install locations (Linux/macOS/Windows). |
| `run [--scene SCENE] [--timeout SECONDS] [--verbose]` | Launch the game and block until the autoload is listening. Re-imports assets first. |
| `stop` | Send `quit` to the running game. |
| `list` | List all registered commands, separating built-in from game-registered. |

## Screenshot and viewport

| Command | Description |
|---|---|
| `screenshot [--max-width PX]` | Save a downscaled PNG of the viewport. Default max width 1280. Returns path + dimensions. |
| `viewport_info` | Window/viewport size, content scale factors. Useful when translating screenshot pixel coordinates to viewport coordinates. |
| `perf` | FPS, frame time, static memory, object/node counts, draw calls, time scale. |

## Input simulation

| Command | Description |
|---|---|
| `click X Y [--button N] [--from-screenshot WIDTH]` | Click at viewport coordinates. Hit-tests UI buttons first (calls `EmitSignal(Pressed)` directly for reliability), falls back to `PushInput` for 3D / non-button regions. With `--from-screenshot`, scales screenshot pixel coords to viewport coords. |
| `click_node --text TEXT \| --path PATH [--button N]` | Click a UI element by its text or scene path. More reliable than coordinate-based clicking when you know the target. |
| `hover --text TEXT \| --path PATH \| --x X --y Y` | Move the cursor — useful for hover-state testing. |
| `drag X1 Y1 X2 Y2 [--steps N] [--button N]` | Inject a mouse drag with interpolated motion events. |
| `type "TEXT" [--submit]` | Type characters as `InputEventKey` events. `--submit` presses Enter after. |
| `action NAME [--duration MS]` | Trigger an `InputMap` action and release it after duration. |
| `sequence ACTIONS... [--json JSON]` | Schedule overlapping input actions. Positional format: `name:start_ms:duration_ms`. Useful for combined moves like simultaneous pan + zoom. |

## Scene tree introspection

| Command | Description |
|---|---|
| `tree [--path PATH] [--depth N]` | Dump scene tree as nested JSON. Default root `/root`, depth 3. Includes node name, type, position (for `Node3D`). |
| `find PATTERN [--type TYPE]` | Search nodes by name pattern (case-insensitive substring), optionally filtered by class. Returns name, type, full path, position. |
| `props NODE_PATH` | Dump every editor/storage property on a node, plus `global_position` and (for `Node3D`/`Control`) size/rotation. Vector and Color types are unpacked into JSON-friendly dicts. |
| `get NODE_PATH PROPERTY` | Read a single property value from a node. |
| `set NODE_PATH PROPERTY VALUE` | Write a property. The value is type-converted based on the property's existing `Variant.Type` (so `set ... visible true` works as expected). Vector/Color values can be passed as JSON objects: `'{"x":1,"y":2}'`. |

## Logging

| Command | Description |
|---|---|
| `log [--clear]` | Read the in-game log buffer (last 500 lines of `GD.Print` output captured by the autoload). `--clear` empties the buffer after reading. |

## Wait

| Command | Description |
|---|---|
| `wait MS` | Sleep for the specified milliseconds before responding. Used for sequencing in scripts. |

---

## Coordinate system

All UI interaction uses **viewport coordinates** (the game's design resolution, e.g. `1920×1080`).

### Screenshot → click pipeline

Screenshots are captured from the viewport texture and downscaled to `--max-width` (default 1280). The scale is uniform:

```
screenshot (1280x720) × 1.5 = viewport (1920x1080)
```

Use `--from-screenshot WIDTH` to auto-translate screenshot pixel coordinates back to viewport coordinates:

```bash
# Take a screenshot, identify a button at pixel (1118, 590) in the screenshot,
# click it in the running game:
tools/gdpilot click --from-screenshot 1280 1118 590
```

### How `click` works

`click X Y` uses viewport coordinates and runs in two stages:

1. Walk the scene tree (including `CanvasLayer` children) for the deepest visible `BaseButton` whose `GetGlobalRect()` contains the point. If found, emit `Pressed` directly — reliable for all UI buttons regardless of `CanvasLayer` nesting.
2. If no button is hit, fall back to `PushInput` with `InputEventMouseButton` press + release. This handles 3D raycasts and non-button controls.

### `click_node` is preferred when you know the target

When you have the button's text or scene path, `click_node --text "Start Wave"` is more reliable than coordinate-based clicking — it finds the control directly and clicks it without translating coordinates.

### Viewport vs window

Games using content scaling (`canvas_items` mode) have a viewport size that differs from the actual window size. All `gdpilot` commands work in viewport coordinates; the autoload handles content-scale translation internally before calling `PushInput`. Use `viewport_info` to inspect the current resolution and scale factors.

---

## Registering game commands

Game-specific commands (anything beyond the built-ins) are registered by the game itself, typically from a `DevConsole.cs` autoload that gets the `GodotPilot` reference and calls `RegisterCommand`:

```csharp
namespace YourGame.Core;

using System.Collections.Generic;
using System.Text.Json;
using Godot;

public partial class DevConsole : Node
{
    private GodotPilot.GodotPilot? _pilot;

    public override void _Ready()
    {
        _pilot = GetNodeOrNull<GodotPilot.GodotPilot>("/root/GodotPilot");
        if (_pilot == null)
        {
            GD.PrintErr("[DevConsole] GodotPilot not found");
            return;
        }

        _pilot.RegisterCommand("get_game_state", args =>
        {
            return new Godot.Collections.Dictionary
            {
                ["state"] = GameManager.Instance.CurrentState.ToString(),
                ["score"] = GameManager.Instance.Score,
            };
        });

        _pilot.RegisterCommand("quick_start", args =>
        {
            GameManager.Instance.StartNewGame();
            return new Godot.Collections.Dictionary { ["ok"] = true };
        });
    }
}
```

Wire `DevConsole` as an autoload in `project.godot` after `GodotPilot`:

```ini
[autoload]

GodotPilot="*res://scripts/core/GodotPilot.cs"
DevConsole="*res://scripts/core/DevConsole.cs"
```

Game commands are then invoked via `cmd`:

```bash
tools/gdpilot cmd get_game_state
tools/gdpilot cmd quick_start
tools/gdpilot cmd add_score amount=100
```

Arguments are passed as `key=value` pairs and auto-parsed as `int`, `float`, `bool`, or string fallback. They arrive in the handler as a `Godot.Collections.Dictionary`.

`tools/gdpilot list` will show all registered commands, separating built-in from game-registered, so the agent can discover what's available without reading source.

---

## Direct game command invocation

If you want to bypass the `cmd` wrapper (some agents prefer flat command names):

```bash
echo '{"cmd":"get_game_state"}' | nc 127.0.0.1 6550
```

This is equivalent to `tools/gdpilot cmd get_game_state` and is useful for tooling that wants to talk to the autoload without going through the Python wrapper. See [PROTOCOL.md](PROTOCOL.md) for the wire format.
