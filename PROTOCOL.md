# GodotPilot Wire Protocol

The Python CLI (`gdpilot`) is a thin convenience wrapper. Anything that can open a TCP socket and read/write newline-delimited JSON can talk to a GodotPilot autoload directly. This document specifies the wire format so alternative clients (an MCP server, a Bash one-liner, a different language) can be built without going through the Python layer.

## Transport

- **TCP**, IPv4 only.
- **Address**: `127.0.0.1:6550`. Hardcoded in the autoload — listening is restricted to localhost.
- **Multiple concurrent clients** are supported. Each connection has its own buffer; commands from different clients are processed independently.
- **Newline-delimited JSON**, both directions. One JSON object per line. The autoload reads until `\n`, parses, dispatches, and writes one JSON line back.

## Request format

```json
{"cmd": "<command_name>", "args": {<arg_name>: <value>, ...}}
```

- `cmd` (string, required) — name of a built-in command (`screenshot`, `tree`, `click`, etc.) or a game-registered command (anything passed to `RegisterCommand`).
- `args` (object, optional) — key/value pairs passed to the command handler. Missing or empty `args` is equivalent to `{}`.

Values in `args` are typed by the JSON parser:

| JSON | C# `Variant.Type` |
|---|---|
| string | `String` |
| integer | `Int` (preferring `Int32` if it fits, else `Int64`) |
| float | `Float` (`double`) |
| `true` / `false` | `Bool` |
| `null` | `Nil` |
| object / array | passed through as raw text in the command handler unless the handler decodes it explicitly |

Vector and Color values are passed as nested objects: `{"x": 1.5, "y": 2.0}` for `Vector2`, `{"r": 1, "g": 0, "b": 0, "a": 1}` for `Color`. The `set` command (and any handler that calls `ConvertJsonToVariantTyped`) auto-converts based on the target property's existing type.

## Response format

Success:

```json
{"ok": true, "<key>": <value>, ...}
```

The `ok: true` field is added automatically if a handler returns a dictionary without it. Handlers can return additional keys describing the result.

Error:

```json
{"ok": false, "error": "<message>"}
```

Errors arise from:

- Missing `cmd` field in the request: `"Missing 'cmd' field"`
- Invalid JSON: `"Invalid JSON: <parser message>"`
- Unknown command: `"Unknown command: <name>"`
- Handler-raised exceptions: `"Error: <exception message>"`
- Handler-returned `{"ok": false, "error": "..."}`

The `error` field is always a human-readable string. There is no error code taxonomy — the agent inspects the message text.

## Examples

### Capture a screenshot

Request:
```json
{"cmd": "screenshot", "args": {"max_width": 1280}}
```

Response:
```json
{"ok": true, "path": "/tmp/godot_pilot/screenshot_1712515200.png", "width": 1280, "height": 720}
```

### Click a button by text

Request:
```json
{"cmd": "click_node", "args": {"text": "Start Wave"}}
```

Response:
```json
{"ok": true, "clicked": "StartWaveButton", "type": "Button", "screen_pos": {"x": 1118, "y": 590}, "path": "/root/Game/UI/HBox/StartWaveButton"}
```

### Read a node property

Request:
```json
{"cmd": "get", "args": {"node_path": "/root/Game/Player", "property": "health"}}
```

Response:
```json
{"ok": true, "node": "/root/Game/Player", "property": "health", "value": "85"}
```

(Note: `value` is currently always serialized as a string. Use `props` if you need typed values.)

### Dump scene tree

Request:
```json
{"cmd": "tree", "args": {"path": "/root/Game", "depth": 2}}
```

Response (abbreviated):
```json
{
  "ok": true,
  "tree": {
    "name": "Game",
    "type": "Node",
    "children": [
      {"name": "World", "type": "Node3D", "position": {"x": 0, "y": 0, "z": 0}, "child_count": 12},
      {"name": "UI", "type": "CanvasLayer", "children": [...]}
    ]
  }
}
```

### Game-registered command

Request:
```json
{"cmd": "get_game_state"}
```

Response (entirely up to the game's handler):
```json
{"ok": true, "state": "InGame", "score": 1230, "wave": 7}
```

### Inspect signal subscription graph

Request:
```json
{"cmd": "signals", "args": {"name": "CombatStarted"}}
```

Response:
```json
{
  "ok": true,
  "count": 1,
  "signals": [
    {
      "node": "/root/EventBus",
      "node_type": "EventBus",
      "signal": "CombatStarted",
      "args": [{"name": "encounterJson", "type": "String"}],
      "connection_count": 2,
      "connections": [
        {"target": "/root/MainGame/CombatManager", "method": "OnCombatStarted"},
        {"target": "/root/MainGame/CombatUI", "method": "_on_combat_started"}
      ]
    }
  ]
}
```

Optional args: `node` (limit to one node by path), `name` (case-insensitive substring filter on signal name), `include_unconnected` (boolean, defaults false — set true to also report signals with zero subscribers).

### Describe an autoload's C# state via reflection

Request:
```json
{"cmd": "describe", "args": {"node_path": "/root/PartyManager"}}
```

Response:
```json
{
  "ok": true,
  "node": "/root/PartyManager",
  "type": "OdeToTheBard.Party.PartyManager",
  "godot_type": "Node",
  "properties": {
    "Gold": 500,
    "Peril": 23,
    "TickCount": 142,
    "PartyCount": 6,
    "ActiveSongId": "warcry"
  }
}
```

Optional args: `include_godot` (boolean, defaults false — set true to also include Godot framework properties like `name`, `position`, `process_priority`), `depth` (integer 0-3, default 1 — controls recursion into nested non-primitive members; lists truncate at 50 items).

## Connection lifecycle

- **Connect** any time after the autoload's `_Ready` runs. The autoload prints `[GodotPilot] Listening on 127.0.0.1:6550` when ready. Connection refused before that point.
- **Send** a complete JSON line (terminated by `\n`).
- **Wait** for one JSON line back. Most commands respond synchronously within the same `_Process` frame. Some (e.g. async sequences) may defer; the wire format is unchanged either way.
- **Pipeline** if you want — multiple newline-delimited requests can be queued; responses come back in order.
- **Disconnect** any time. The autoload tracks per-client state but holds no resources beyond the socket.

The autoload also closes all connections cleanly on `NotificationWMCloseRequest` and `NotificationExitTree` (i.e. when the game window is closed or the autoload is freed).

## What the protocol does NOT include

- **Authentication.** Listening on `127.0.0.1` is the only access control. Anyone with local shell access can drive the game. This is intentional — it's a development tool, not a production service.
- **Encryption.** Plaintext over loopback.
- **Versioning.** There's no protocol version field. If the wire format ever changes incompatibly, clients will need to be updated.
- **Streaming responses.** One request, one response. Long-running operations either return synchronously (blocking the caller until done) or return immediately and require the caller to poll a status command.
- **Error codes.** Errors are stringly-typed messages, not enumerated codes. Agents read the message text.

## Building an alternative client

A minimal client in any language:

1. Open TCP to `127.0.0.1:6550`.
2. Write a JSON object followed by `\n`.
3. Read until `\n`.
4. Parse the response as JSON.
5. Check `ok` field.

Example in Bash + `nc`:

```bash
echo '{"cmd":"perf"}' | nc -q 1 127.0.0.1 6550
```

Example in Python (from `gdpilot` itself):

```python
import json, socket
with socket.create_connection(("127.0.0.1", 6550), timeout=10) as s:
    s.sendall((json.dumps({"cmd": "screenshot"}) + "\n").encode())
    buf = b""
    while b"\n" not in buf:
        buf += s.recv(4096)
print(json.loads(buf.decode().strip()))
```

That's the entire protocol. If something more is needed, the right place to add it is the autoload's `RegisterBuiltinCommands` — not a new transport.
