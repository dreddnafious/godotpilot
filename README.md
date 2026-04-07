# GodotPilot

A bash + JSON remote control surface for a running Godot game. Built so an AI agent can drive, observe, and introspect a Godot project the same way it drives every other CLI tool: type a command, parse the response, decide what to do next.

## What it is

Two pieces:

- **`GodotPilot.cs`** — a C# autoload that runs inside the game and listens on `127.0.0.1:6550` for newline-delimited JSON commands. Provides screenshot capture, input simulation (click, drag, type, action), scene tree introspection (`tree`, `find`, `get`, `props`, `set`), perf monitoring, log capture, and a hook to register game-specific commands. ~1160 lines, no dependencies beyond Godot 4.x with C#.
- **`gdpilot`** — a Python CLI client (stdlib only, no `pip install`) that wraps the wire protocol behind a subcommand interface and emits JSON to stdout, human messages to stderr. Also handles `run` (launch the game) and `stop` (quit it) so the agent can manage the lifecycle.

From the agent's perspective the entire surface is `gdpilot <subcommand> [args]` from inside a game project. Same shape as `pablo_p`, `modelforge`, etc.

## Install into a game

```
git clone <godotpilot-repo> /home/you/dev/godotpilot
/home/you/dev/godotpilot/install /path/to/your/game
```

The install script copies two files into the target Godot project:

```
<game>/scripts/core/GodotPilot.cs   # the C# autoload
<game>/tools/gdpilot                 # the Python CLI
```

It refuses to run if the target doesn't contain a `project.godot`. It's idempotent — re-run it any time you update the canonical source and want to push the latest version into a game.

## Wire it as an autoload

After install, register the autoload in `<game>/project.godot`:

```ini
[autoload]

GodotPilot="*res://scripts/core/GodotPilot.cs"
```

That's it. Run the game; you should see:

```
[GodotPilot] Listening on 127.0.0.1:6550
```

## Quickstart

With the game running:

```
cd /path/to/your/game
tools/gdpilot screenshot                 # save a viewport PNG, return path + dimensions
tools/gdpilot tree --depth 2              # dump the scene tree
tools/gdpilot find Button                 # find all nodes with "Button" in the name
tools/gdpilot props /root/Game            # dump every public property on a node
tools/gdpilot click 640 360               # click at viewport coords
tools/gdpilot click_node --text "Start"   # click a button by its text
tools/gdpilot perf                        # FPS, memory, draw calls
tools/gdpilot log                         # capture buffered GD.Print output
tools/gdpilot list                        # list all registered commands
```

For game lifecycle:

```
tools/gdpilot setup                       # find and remember the Godot binary
tools/gdpilot run                         # launch the game; returns when GodotPilot is ready
tools/gdpilot stop                        # quit the game cleanly
```

See [COMMANDS.md](COMMANDS.md) for the full command reference and [PROTOCOL.md](PROTOCOL.md) for the wire protocol if you want to talk to the autoload directly without the Python CLI.

## Registering game-specific commands

The autoload exposes `RegisterCommand(name, handler)` so a game can add its own command surface — typically from a `DevConsole.cs` autoload that registers commands like `get_game_state`, `quick_start`, `force_encounter`, etc. These show up in `gdpilot list` alongside the built-ins and are invoked via `gdpilot cmd <name> [key=value ...]`.

See [COMMANDS.md § Registering game commands](COMMANDS.md#registering-game-commands) for the pattern.

## Canonical source discipline

**Edits to GodotPilot live in this repo, not in game-project copies.** The install script pushes source from here to a game; nothing pushes the other direction. If you fix a bug while working in a game project, port the fix back to the canonical repo before re-installing — otherwise the next install will overwrite your in-game change.

This is the price of the copy-based distribution model: per-project copies are robust against link rot and project moves, but the canonical source has to stay authoritative or you get drift. Re-running `install` against a game is the only sanctioned update path.

## Why bash + JSON

Every other tool in the ecosystem (`pablo_p`, `modelforge`, `elton`, `blendpipe`) speaks the same dialect: subcommands, JSON to stdout, human/log to stderr. The agent already knows how to invoke commands and parse JSON output — there's no SDK to learn, no client library to install, no protocol to discover. GodotPilot uses the same shape so the agent's interface to "drive a Godot game" is identical to its interface to "generate an image" or "convert a model." Uniformity at the bash layer is the affordance.

The fact that GodotPilot is internally a TCP server with a Python client wrapper is implementation detail — the agent never sees it.

## Status

Standalone repo extracted from rogue_defense (where it was co-developed with that game). Currently at the state it had reached as of extraction. Future work likely includes attribute-based command registration (`[PilotCommand]`), reflection-based event/signal graph queries, and a static lint pass for common Godot anti-patterns. None of that is here yet — this repo is the extraction baseline.
