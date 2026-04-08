namespace GodotPilot;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;

/// <summary>
/// TCP server autoload for remote control of the running game.
/// Listens on localhost:6550, accepts newline-delimited JSON commands.
/// Register game-specific commands via RegisterCommand().
/// </summary>
public partial class GodotPilot : Node
{
    private const int Port = 6550;
    private const string ScreenshotDir = "/tmp/godot_pilot";

    private TcpServer _server = null!;
    private readonly List<ClientState> _clients = new();
    private readonly Dictionary<string, Func<Dictionary<string, JsonElement>, Godot.Collections.Dictionary>> _commands = new();
    private readonly HashSet<string> _builtinCommands = new();
    private readonly List<string> _logBuffer = new();
    private const int MaxLogLines = 500;

    /// <summary>
    /// JSON deserialization options used by <c>invoke</c> when an argument
    /// targets a complex C# type (record, class, list, dictionary, etc.).
    /// Primitive parameter types (string/int/long/double/bool, nullable
    /// thereof, and enums) bypass this and use direct coercion.
    ///
    /// <para>Default is case-insensitive property matching plus a string
    /// enum converter — sufficient for most C# domain models. Projects
    /// with snake_case JSON conventions, custom converters, or polymorphic
    /// type discriminators that need extra options should override this
    /// from their <c>DevConsole._Ready</c> by assigning a new
    /// <see cref="JsonSerializerOptions"/> instance.</para>
    ///
    /// <para>Polymorphic types declared with
    /// <c>[JsonPolymorphic]</c> + <c>[JsonDerivedType]</c> attributes
    /// work out of the box because the discriminator strings are read
    /// from the type metadata, independent of options.</para>
    /// </summary>
    public System.Text.Json.JsonSerializerOptions InvokeJsonOptions { get; set; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    // For capturing GD.Print output
    private bool _captureLog = true;

    private class ClientState
    {
        public StreamPeerTcp Peer;
        public StringBuilder Buffer = new();
        public bool PendingAsync;
        public Godot.Collections.Dictionary? AsyncResult;

        public ClientState(StreamPeerTcp peer) => Peer = peer;
    }

    public override void _Ready()
    {
        _server = new TcpServer();
        var err = _server.Listen(Port, "127.0.0.1");
        if (err != Error.Ok)
        {
            GD.PrintErr($"[GodotPilot] Failed to listen on port {Port}: {err}");
            return;
        }
        GD.Print($"[GodotPilot] Listening on 127.0.0.1:{Port}");

        // Ensure screenshot directory exists
        if (!DirAccess.DirExistsAbsolute(ScreenshotDir))
            DirAccess.MakeDirRecursiveAbsolute(ScreenshotDir);

        RegisterBuiltinCommands();
    }

    public override void _Process(double delta)
    {
        if (_server == null) return;

        // Accept new connections
        while (_server.IsConnectionAvailable())
        {
            var peer = _server.TakeConnection();
            peer.SetNoDelay(true);
            _clients.Add(new ClientState(peer));
        }

        // Process each client
        for (int i = _clients.Count - 1; i >= 0; i--)
        {
            var client = _clients[i];
            client.Peer.Poll();

            var status = client.Peer.GetStatus();
            if (status == StreamPeerTcp.Status.Error || status == StreamPeerTcp.Status.None)
            {
                _clients.RemoveAt(i);
                continue;
            }

            if (status != StreamPeerTcp.Status.Connected)
                continue;

            // Check for pending async result
            if (client.PendingAsync && client.AsyncResult != null)
            {
                SendResponse(client, client.AsyncResult);
                client.PendingAsync = false;
                client.AsyncResult = null;
                continue;
            }

            if (client.PendingAsync)
                continue;

            // Read available data
            int available = client.Peer.GetAvailableBytes();
            if (available <= 0) continue;

            var data = client.Peer.GetData(available);
            if ((int)(Error)data[0].AsInt64() != (int)Error.Ok) continue;

            var bytes = (byte[])data[1];
            client.Buffer.Append(Encoding.UTF8.GetString(bytes));

            // Process complete lines
            var bufStr = client.Buffer.ToString();
            int nlIdx;
            while ((nlIdx = bufStr.IndexOf('\n')) >= 0)
            {
                var line = bufStr[..nlIdx].Trim();
                bufStr = bufStr[(nlIdx + 1)..];
                client.Buffer.Clear();
                client.Buffer.Append(bufStr);

                if (!string.IsNullOrEmpty(line))
                    ProcessRequest(client, line);
            }
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest || what == NotificationExitTree)
        {
            _server?.Stop();
            foreach (var client in _clients)
                client.Peer.DisconnectFromHost();
            _clients.Clear();
        }
    }

    /// <summary>
    /// Register a game-specific command. Handler receives parsed args dict
    /// and returns a Godot Dictionary result.
    /// </summary>
    public void RegisterCommand(string name, Func<Godot.Collections.Dictionary, Godot.Collections.Dictionary> handler)
    {
        _commands[name] = args =>
        {
            // Convert JsonElement args to Godot Dictionary
            var gd = new Godot.Collections.Dictionary();
            foreach (var kv in args)
            {
                gd[kv.Key] = JsonElementToVariant(kv.Value);
            }
            return handler(gd);
        };
    }

    private void ProcessRequest(ClientState client, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("cmd", out var cmdProp))
            {
                SendError(client, "Missing 'cmd' field");
                return;
            }

            var cmd = cmdProp.GetString()!;
            var args = new Dictionary<string, JsonElement>();
            if (root.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in argsProp.EnumerateObject())
                    args[prop.Name] = prop.Value.Clone();
            }

            // Check built-in commands first, then registered
            if (_commands.TryGetValue(cmd, out var handler))
            {
                var result = handler(args);
                if (result == null)
                    result = new Godot.Collections.Dictionary { ["ok"] = true };
                SendResponse(client, result);
            }
            else
            {
                SendError(client, $"Unknown command: {cmd}");
            }
        }
        catch (JsonException ex)
        {
            SendError(client, $"Invalid JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            SendError(client, $"Error: {ex.Message}");
        }
    }

    private void SendResponse(ClientState client, Godot.Collections.Dictionary result)
    {
        // Ensure ok field exists
        if (!result.ContainsKey("ok") && !result.ContainsKey("status"))
            result["ok"] = true;

        var json = Json.Stringify(result);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        client.Peer.PutData(bytes);
    }

    private void SendError(ClientState client, string message)
    {
        var result = new Godot.Collections.Dictionary
        {
            ["ok"] = false,
            ["error"] = message,
        };
        var json = Json.Stringify(result);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        client.Peer.PutData(bytes);
    }

    private static Variant JsonElementToVariant(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString()!,
            JsonValueKind.Number when el.TryGetInt32(out int i) => i,
            JsonValueKind.Number when el.TryGetInt64(out long l) => l,
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => default,
            _ => el.GetRawText(),
        };
    }

    // ── Built-in commands ────────────────────────────────────────────

    private void RegisterBuiltin(string name, Func<Dictionary<string, JsonElement>, Godot.Collections.Dictionary> handler)
    {
        _commands[name] = handler;
        _builtinCommands.Add(name);
    }

    private void RegisterBuiltinCommands()
    {
        RegisterBuiltin("screenshot", CmdScreenshot);
        RegisterBuiltin("click", CmdClick);
        RegisterBuiltin("drag", CmdDrag);
        RegisterBuiltin("type", CmdType);
        RegisterBuiltin("action", CmdAction);
        RegisterBuiltin("tree", CmdTree);
        RegisterBuiltin("find", CmdFind);
        RegisterBuiltin("get", CmdGet);
        RegisterBuiltin("props", CmdProps);
        RegisterBuiltin("set", CmdSet);
        RegisterBuiltin("sequence", CmdSequence);
        RegisterBuiltin("click_node", CmdClickNode);
        RegisterBuiltin("hover", CmdHover);
        RegisterBuiltin("viewport_info", CmdViewportInfo);
        RegisterBuiltin("perf", CmdPerf);
        RegisterBuiltin("log", CmdLog);
        RegisterBuiltin("wait", CmdWait);
        RegisterBuiltin("list_commands", CmdListCommands);
        RegisterBuiltin("signals", CmdSignals);
        RegisterBuiltin("describe", CmdDescribe);
        RegisterBuiltin("static", CmdStatic);
        RegisterBuiltin("invoke", CmdInvoke);
        RegisterBuiltin("quit", CmdQuit);
    }

    private Godot.Collections.Dictionary CmdScreenshot(Dictionary<string, JsonElement> args)
    {
        int maxWidth = 1280;
        if (args.TryGetValue("max_width", out var mw))
            maxWidth = mw.GetInt32();

        var viewport = GetViewport();
        var image = viewport.GetTexture().GetImage();

        // Downscale if needed
        if (image.GetWidth() > maxWidth)
        {
            float ratio = (float)maxWidth / image.GetWidth();
            int newH = (int)(image.GetHeight() * ratio);
            image.Resize(maxWidth, newH);
        }

        var timestamp = Time.GetUnixTimeFromSystem();
        var path = $"{ScreenshotDir}/screenshot_{timestamp:F0}.png";
        image.SavePng(path);

        return new Godot.Collections.Dictionary
        {
            ["path"] = path,
            ["width"] = image.GetWidth(),
            ["height"] = image.GetHeight(),
        };
    }

    /// <summary>Find the topmost visible BaseButton at a viewport position.</summary>
    private BaseButton? FindButtonAtPosition(Vector2 viewportPos)
    {
        BaseButton? best = null;
        int bestDepth = -1;

        // Search all scenes including CanvasLayer children
        var scene = GetTree().CurrentScene;
        if (scene != null)
            FindButtonRecursive(scene, viewportPos, 0, ref best, ref bestDepth);

        // Also search root children (autoloads, CanvasLayers)
        foreach (var child in GetTree().Root.GetChildren())
            FindButtonRecursive(child, viewportPos, 0, ref best, ref bestDepth);

        return best;
    }

    private static void FindButtonRecursive(Node node, Vector2 pos, int depth, ref BaseButton? best, ref int bestDepth)
    {
        if (node is BaseButton btn && btn.Visible && !btn.Disabled)
        {
            var rect = btn.GetGlobalRect();
            if (rect.HasPoint(pos) && depth > bestDepth)
            {
                best = btn;
                bestDepth = depth;
            }
        }
        foreach (var child in node.GetChildren())
            FindButtonRecursive(child, pos, depth + 1, ref best, ref bestDepth);
    }

    /// <summary>
    /// Transform viewport coordinates to window/input coordinates.
    /// When content_scale_mode is canvas_items, the viewport (design resolution)
    /// may differ from the window size. PushInput expects window coordinates.
    /// </summary>
    private Vector2 ViewportToWindow(Vector2 viewportPos)
    {
        var viewport = GetViewport();
        var vpSize = viewport.GetVisibleRect().Size; // design resolution (e.g. 1920x1080)
        var winSize = DisplayServer.WindowGetSize();  // actual window (e.g. 1920x1008)
        return new Vector2(
            viewportPos.X * winSize.X / vpSize.X,
            viewportPos.Y * winSize.Y / vpSize.Y
        );
    }

    private Godot.Collections.Dictionary CmdClick(Dictionary<string, JsonElement> args)
    {
        float x = args.TryGetValue("x", out var xv) ? (float)xv.GetDouble() : 0;
        float y = args.TryGetValue("y", out var yv) ? (float)yv.GetDouble() : 0;
        int button = args.TryGetValue("button", out var bv) ? bv.GetInt32() : 1;

        var pos = new Vector2(x, y);
        var viewport = GetViewport();

        // Try to find a button at this position and click it directly
        // PushInput doesn't reliably reach CanvasLayer buttons
        var hitButton = FindButtonAtPosition(pos);
        if (hitButton != null)
        {
            hitButton.EmitSignal(BaseButton.SignalName.Pressed);
            return new Godot.Collections.Dictionary
            {
                ["clicked"] = new Godot.Collections.Dictionary { ["x"] = x, ["y"] = y },
                ["button"] = button,
                ["hit_control"] = hitButton.Name,
            };
        }

        // Move cursor first (triggers hover states)
        var motion = new InputEventMouseMotion
        {
            Position = pos,
            GlobalPosition = pos,
        };
        viewport.PushInput(motion);

        // Press
        var press = new InputEventMouseButton
        {
            Position = pos,
            GlobalPosition = pos,
            ButtonIndex = (MouseButton)button,
            Pressed = true,
        };
        viewport.PushInput(press);

        // Release in same frame (buttons require press+release to register)
        var release = new InputEventMouseButton
        {
            Position = pos,
            GlobalPosition = pos,
            ButtonIndex = (MouseButton)button,
            Pressed = false,
        };
        viewport.PushInput(release);

        return new Godot.Collections.Dictionary
        {
            ["clicked"] = new Godot.Collections.Dictionary { ["x"] = x, ["y"] = y },
            ["button"] = button,
        };
    }

    private void DeferredInput(InputEvent ev)
    {
        GetViewport().PushInput(ev);
    }

    private Godot.Collections.Dictionary CmdDrag(Dictionary<string, JsonElement> args)
    {
        float x1 = args.TryGetValue("x1", out var x1v) ? (float)x1v.GetDouble() : 0;
        float y1 = args.TryGetValue("y1", out var y1v) ? (float)y1v.GetDouble() : 0;
        float x2 = args.TryGetValue("x2", out var x2v) ? (float)x2v.GetDouble() : 0;
        float y2 = args.TryGetValue("y2", out var y2v) ? (float)y2v.GetDouble() : 0;
        int steps = args.TryGetValue("steps", out var sv) ? sv.GetInt32() : 10;
        int button = args.TryGetValue("button", out var bv) ? bv.GetInt32() : 1;

        var viewport = GetViewport();
        var from = new Vector2(x1, y1);
        var to = new Vector2(x2, y2);

        // Press at start
        var press = new InputEventMouseButton
        {
            Position = from,
            GlobalPosition = from,
            ButtonIndex = (MouseButton)button,
            Pressed = true,
        };
        viewport.PushInput(press);

        // Create a tween for smooth drag
        var tween = CreateTween();
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            var pos = from.Lerp(to, t);
            tween.TweenCallback(Callable.From(() =>
            {
                var move = new InputEventMouseMotion
                {
                    Position = pos,
                    GlobalPosition = pos,
                    ButtonMask = (MouseButtonMask)(1 << ((int)(MouseButton)button - 1)),
                };
                viewport.PushInput(move);
            })).SetDelay(0.016f); // ~60fps
        }

        // Release at end
        tween.TweenCallback(Callable.From(() =>
        {
            var release = new InputEventMouseButton
            {
                Position = to,
                GlobalPosition = to,
                ButtonIndex = (MouseButton)button,
                Pressed = false,
            };
            viewport.PushInput(release);
        }));

        return new Godot.Collections.Dictionary
        {
            ["dragged"] = new Godot.Collections.Dictionary
            {
                ["from"] = new Godot.Collections.Dictionary { ["x"] = x1, ["y"] = y1 },
                ["to"] = new Godot.Collections.Dictionary { ["x"] = x2, ["y"] = y2 },
            },
            ["steps"] = steps,
        };
    }

    private Godot.Collections.Dictionary CmdType(Dictionary<string, JsonElement> args)
    {
        string text = args.TryGetValue("text", out var tv) ? tv.GetString()! : "";
        bool submit = args.TryGetValue("submit", out var sv) && sv.GetBoolean();

        var viewport = GetViewport();
        foreach (char c in text)
        {
            var press = new InputEventKey
            {
                Unicode = c,
                Keycode = (Key)c,
                Pressed = true,
            };
            viewport.PushInput(press);
            var release = new InputEventKey
            {
                Unicode = c,
                Keycode = (Key)c,
                Pressed = false,
            };
            viewport.PushInput(release);
        }

        if (submit)
        {
            var enter = new InputEventKey { Keycode = Key.Enter, Pressed = true };
            viewport.PushInput(enter);
            var enterUp = new InputEventKey { Keycode = Key.Enter, Pressed = false };
            viewport.PushInput(enterUp);
        }

        return new Godot.Collections.Dictionary
        {
            ["typed"] = text.Length,
            ["submitted"] = submit,
        };
    }

    private Godot.Collections.Dictionary CmdAction(Dictionary<string, JsonElement> args)
    {
        string name = args.TryGetValue("name", out var nv) ? nv.GetString()! : "";
        int durationMs = args.TryGetValue("duration_ms", out var dv) ? dv.GetInt32() : 100;

        if (!InputMap.HasAction(name))
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Unknown action: {name}",
            };
        }

        // Two parallel paths because Godot's input system has two:
        //
        //   1. Polled state — Input.ActionPress/ActionRelease.
        //      Reflects in Input.IsActionPressed("name") which polled
        //      handlers (typically _Process loops) read every frame.
        //
        //   2. Event pipeline — Input.ParseInputEvent with a synthesized
        //      InputEventAction. Propagates through _Input(InputEvent) on
        //      every Node, where handlers call event.IsActionPressed("name")
        //      to detect just-pressed events. This is the canonical Godot
        //      pattern for action-based UI input.
        //
        // The original implementation only set the polled state, which
        // meant `gdpilot action` could not drive _Input handlers — most
        // UI screens that override _Input never saw the event. Firing
        // both keeps polled consumers working AND wakes up event-based
        // _Input handlers.
        Input.ActionPress(name);
        Input.ParseInputEvent(new InputEventAction { Action = name, Pressed = true });

        // Release after duration — same dual path.
        var timer = GetTree().CreateTimer(durationMs / 1000.0);
        timer.Timeout += () =>
        {
            Input.ActionRelease(name);
            Input.ParseInputEvent(new InputEventAction { Action = name, Pressed = false });
        };

        return new Godot.Collections.Dictionary
        {
            ["action"] = name,
            ["duration_ms"] = durationMs,
        };
    }

    private Godot.Collections.Dictionary CmdTree(Dictionary<string, JsonElement> args)
    {
        string path = args.TryGetValue("path", out var pv) ? pv.GetString()! : "/root";
        int depth = args.TryGetValue("depth", out var dv) ? dv.GetInt32() : 3;

        var node = GetNodeOrNull(path);
        if (node == null)
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Node not found: {path}",
            };
        }

        var tree = BuildTree(node, depth, 0);
        return new Godot.Collections.Dictionary { ["tree"] = tree };
    }

    private Godot.Collections.Dictionary BuildTree(Node node, int maxDepth, int currentDepth)
    {
        var result = new Godot.Collections.Dictionary
        {
            ["name"] = node.Name.ToString(),
            ["type"] = node.GetClass(),
        };

        if (node is Node3D n3d)
        {
            result["position"] = new Godot.Collections.Dictionary
            {
                ["x"] = Math.Round(n3d.GlobalPosition.X, 2),
                ["y"] = Math.Round(n3d.GlobalPosition.Y, 2),
                ["z"] = Math.Round(n3d.GlobalPosition.Z, 2),
            };
        }

        if (currentDepth < maxDepth && node.GetChildCount() > 0)
        {
            var children = new Godot.Collections.Array();
            foreach (var child in node.GetChildren())
                children.Add(BuildTree(child, maxDepth, currentDepth + 1));
            result["children"] = children;
        }
        else if (node.GetChildCount() > 0)
        {
            result["child_count"] = node.GetChildCount();
        }

        return result;
    }

    private Godot.Collections.Dictionary CmdFind(Dictionary<string, JsonElement> args)
    {
        string pattern = args.TryGetValue("pattern", out var pv) ? pv.GetString()! : "";
        string? type = args.TryGetValue("type", out var tv) ? tv.GetString() : null;

        var results = new Godot.Collections.Array();
        FindNodes(GetTree().CurrentScene, pattern, type, results);

        return new Godot.Collections.Dictionary
        {
            ["count"] = results.Count,
            ["nodes"] = results,
        };
    }

    private void FindNodes(Node? node, string pattern, string? type, Godot.Collections.Array results)
    {
        if (node == null) return;

        bool nameMatch = node.Name.ToString().Contains(pattern, StringComparison.OrdinalIgnoreCase);
        bool typeMatch = type == null || node.GetClass().Contains(type, StringComparison.OrdinalIgnoreCase);

        if (nameMatch && typeMatch)
        {
            var entry = new Godot.Collections.Dictionary
            {
                ["name"] = node.Name.ToString(),
                ["type"] = node.GetClass(),
                ["path"] = node.GetPath().ToString(),
            };
            if (node is Node3D n3d)
            {
                entry["position"] = new Godot.Collections.Dictionary
                {
                    ["x"] = Math.Round(n3d.GlobalPosition.X, 2),
                    ["y"] = Math.Round(n3d.GlobalPosition.Y, 2),
                    ["z"] = Math.Round(n3d.GlobalPosition.Z, 2),
                };
            }
            results.Add(entry);
        }

        foreach (var child in node.GetChildren())
            FindNodes(child, pattern, type, results);
    }

    private Godot.Collections.Dictionary CmdGet(Dictionary<string, JsonElement> args)
    {
        string nodePath = args.TryGetValue("node_path", out var npv) ? npv.GetString()! : "";
        string property = args.TryGetValue("property", out var pv) ? pv.GetString()! : "";

        var node = GetNodeOrNull(nodePath);
        if (node == null)
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Node not found: {nodePath}",
            };
        }

        var val = node.Get(property);
        return new Godot.Collections.Dictionary
        {
            ["node"] = nodePath,
            ["property"] = property,
            ["value"] = val.ToString(),
        };
    }

    private Godot.Collections.Dictionary CmdClickNode(Dictionary<string, JsonElement> args)
    {
        string? text = args.TryGetValue("text", out var tv) ? tv.GetString() : null;
        string? path = args.TryGetValue("path", out var pv) ? pv.GetString() : null;
        int button = args.TryGetValue("button", out var bv) ? bv.GetInt32() : 1;

        Control? target = null;

        if (path != null)
        {
            var node = GetNodeOrNull(path);
            if (node is Control ctrl)
                target = ctrl;
            else
                return new Godot.Collections.Dictionary { ["ok"] = false, ["error"] = $"Node not found or not a Control: {path}" };
        }
        else if (text != null)
        {
            target = FindControlByText(GetTree().CurrentScene, text);
            if (target == null)
                return new Godot.Collections.Dictionary { ["ok"] = false, ["error"] = $"No Control with text '{text}' found" };
        }
        else
        {
            return new Godot.Collections.Dictionary { ["ok"] = false, ["error"] = "Provide 'text' or 'path' argument" };
        }

        // GetGlobalRect returns viewport coordinates; PushInput expects viewport coordinates
        var screenCenter = target.GetGlobalRect().GetCenter();

        var viewport = GetViewport();

        // Move cursor to the button (triggers hover states, mouse_entered, etc.)
        var motion = new InputEventMouseMotion
        {
            Position = screenCenter,
            GlobalPosition = screenCenter,
        };
        viewport.PushInput(motion);

        // Inject mouse press + release in same frame
        if (target is BaseButton baseButton)
        {
            var press = new InputEventMouseButton
            {
                Position = screenCenter,
                GlobalPosition = screenCenter,
                ButtonIndex = (MouseButton)button,
                Pressed = true,
            };
            viewport.PushInput(press);

            var release = new InputEventMouseButton
            {
                Position = screenCenter,
                GlobalPosition = screenCenter,
                ButtonIndex = (MouseButton)button,
                Pressed = false,
            };
            viewport.PushInput(release);

            // Also directly emit Pressed as a fallback
            if (!baseButton.Disabled)
                baseButton.EmitSignal(BaseButton.SignalName.Pressed);
        }
        else
        {
            // Non-button control: just inject click events
            var press = new InputEventMouseButton
            {
                Position = screenCenter,
                GlobalPosition = screenCenter,
                ButtonIndex = (MouseButton)button,
                Pressed = true,
            };
            viewport.PushInput(press);
            var release = new InputEventMouseButton
            {
                Position = screenCenter,
                GlobalPosition = screenCenter,
                ButtonIndex = (MouseButton)button,
                Pressed = false,
            };
            CallDeferred(MethodName.DeferredInput, release);
        }

        return new Godot.Collections.Dictionary
        {
            ["clicked"] = target.Name.ToString(),
            ["type"] = target.GetClass(),
            ["screen_pos"] = new Godot.Collections.Dictionary
            {
                ["x"] = Math.Round(screenCenter.X, 1),
                ["y"] = Math.Round(screenCenter.Y, 1),
            },
            ["path"] = target.GetPath().ToString(),
        };
    }

    private Godot.Collections.Dictionary CmdHover(Dictionary<string, JsonElement> args)
    {
        // Move cursor to a position or node — useful for testing hover states
        float x = 0, y = 0;

        string? text = args.TryGetValue("text", out var tv) ? tv.GetString() : null;
        string? path = args.TryGetValue("path", out var pv) ? pv.GetString() : null;

        if (text != null || path != null)
        {
            Control? target = null;
            if (path != null)
            {
                var node = GetNodeOrNull(path);
                if (node is Control ctrl) target = ctrl;
            }
            else if (text != null)
            {
                target = FindControlByText(GetTree().CurrentScene, text);
            }

            if (target == null)
                return new Godot.Collections.Dictionary { ["ok"] = false, ["error"] = "Target not found" };

            var center = target.GetGlobalRect().GetCenter();
            x = center.X;
            y = center.Y;
        }
        else
        {
            x = args.TryGetValue("x", out var xv) ? (float)xv.GetDouble() : 0;
            y = args.TryGetValue("y", out var yv) ? (float)yv.GetDouble() : 0;
        }

        var motion = new InputEventMouseMotion
        {
            Position = new Vector2(x, y),
            GlobalPosition = new Vector2(x, y),
        };
        GetViewport().PushInput(motion);

        return new Godot.Collections.Dictionary
        {
            ["position"] = new Godot.Collections.Dictionary { ["x"] = x, ["y"] = y },
        };
    }

    private Godot.Collections.Dictionary CmdViewportInfo(Dictionary<string, JsonElement> args)
    {
        var viewport = GetViewport();
        var windowSize = DisplayServer.WindowGetSize();
        var viewportSize = viewport.GetVisibleRect().Size;

        return new Godot.Collections.Dictionary
        {
            ["window_width"] = windowSize.X,
            ["window_height"] = windowSize.Y,
            ["viewport_width"] = viewportSize.X,
            ["viewport_height"] = viewportSize.Y,
            ["scale_x"] = Math.Round(windowSize.X / viewportSize.X, 4),
            ["scale_y"] = Math.Round(windowSize.Y / viewportSize.Y, 4),
            ["content_scale_mode"] = ProjectSettings.GetSetting("display/window/stretch/mode").ToString(),
        };
    }

    private static Control? FindControlByText(Node? root, string text)
    {
        if (root == null) return null;

        // Check common text-bearing controls
        if (root is Button btn && btn.Text.Contains(text, StringComparison.OrdinalIgnoreCase) && btn.Visible && !btn.Disabled)
            return btn;
        if (root is Label lbl && lbl.Text.Contains(text, StringComparison.OrdinalIgnoreCase) && lbl.Visible)
            return lbl;
        if (root is LinkButton lb && lb.Text.Contains(text, StringComparison.OrdinalIgnoreCase) && lb.Visible)
            return lb;

        // Also check disabled buttons (for diagnostic purposes — caller can check)
        if (root is Button disBtn && disBtn.Text.Contains(text, StringComparison.OrdinalIgnoreCase) && disBtn.Visible)
            return disBtn;

        foreach (var child in root.GetChildren())
        {
            var found = FindControlByText(child, text);
            if (found != null) return found;
        }

        return null;
    }

    private Godot.Collections.Dictionary CmdPerf(Dictionary<string, JsonElement> args)
    {
        return new Godot.Collections.Dictionary
        {
            ["fps"] = Engine.GetFramesPerSecond(),
            ["frame_time_ms"] = Math.Round(1000.0 / Math.Max(1, Engine.GetFramesPerSecond()), 2),
            ["static_memory_mb"] = Math.Round(OS.GetStaticMemoryUsage() / (1024.0 * 1024.0), 2),
            ["objects"] = Performance.GetMonitor(Performance.Monitor.ObjectCount),
            ["nodes"] = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
            ["draw_calls"] = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            ["time_scale"] = Engine.TimeScale,
        };
    }

    /// <summary>
    /// Read recent log lines. Two sources, selected via <c>source</c>:
    ///
    /// <para><b>source=buffer</b> (default — preserved from the original
    /// command shape) — returns the in-memory ring buffer populated by
    /// manual <c>CaptureLog</c> calls. The default behavior is unchanged
    /// from the pre-2026-04-08 version of this command, so existing
    /// callers see exactly the same response shape and contents.</para>
    ///
    /// <para><b>source=file</b> (opt-in) — reads the last <c>tail</c>
    /// lines of the running game's Godot log file. Path resolved via
    /// <c>ProjectSettings("debug/file_logging/log_path")</c>, default
    /// <c>user://logs/godot.log</c>. Returns everything Godot has logged
    /// (prints, warnings, errors, stack traces) without needing the
    /// in-memory capture mechanism to be wired.</para>
    ///
    /// Args: <c>source</c> ("buffer" default, or "file"),
    /// <c>tail</c> (int, default 50, file-mode only),
    /// <c>clear</c> (bool, buffer-mode only — clears the buffer after
    /// returning its current contents).
    /// </summary>
    private Godot.Collections.Dictionary CmdLog(Dictionary<string, JsonElement> args)
    {
        string source = args.TryGetValue("source", out var sv) ? (sv.GetString() ?? "buffer") : "buffer";

        if (source == "buffer")
        {
            // Existing behavior — response shape unchanged for backward compat.
            bool clear = args.TryGetValue("clear", out var cv) && cv.GetBoolean();
            var bufferLines = new Godot.Collections.Array();
            foreach (var line in _logBuffer)
                bufferLines.Add(line);
            if (clear)
                _logBuffer.Clear();
            return new Godot.Collections.Dictionary
            {
                ["count"] = bufferLines.Count,
                ["lines"] = bufferLines,
            };
        }

        // Opt-in: read from the Godot log file. Response shape is a
        // superset of the buffer-mode shape (adds source/path/total_lines)
        // so consumers reading count/lines still work, but the source
        // field tells them which mode they got.
        int tail = args.TryGetValue("tail", out var tv) ? Math.Max(1, tv.GetInt32()) : 50;

        var setting = ProjectSettings.GetSetting("debug/file_logging/log_path", "user://logs/godot.log");
        string logPath = setting.AsString();
        if (string.IsNullOrEmpty(logPath))
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = "Godot file logging path is empty (debug/file_logging/log_path)",
            };
        }
        string globalPath = ProjectSettings.GlobalizePath(logPath);
        if (!File.Exists(globalPath))
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Log file not found: {globalPath}",
                ["hint"] = "Ensure debug/file_logging/enable_file_logging is true in project settings.",
            };
        }

        string[] allLines;
        try
        {
            allLines = File.ReadAllLines(globalPath);
        }
        catch (Exception ex)
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Read failed: {ex.GetType().Name}: {ex.Message}",
            };
        }

        int start = Math.Max(0, allLines.Length - tail);
        var lines = new Godot.Collections.Array();
        for (int i = start; i < allLines.Length; i++)
            lines.Add(allLines[i]);

        return new Godot.Collections.Dictionary
        {
            ["source"] = "file",
            ["path"] = globalPath,
            ["count"] = lines.Count,
            ["total_lines"] = allLines.Length,
            ["lines"] = lines,
        };
    }

    private Godot.Collections.Dictionary CmdWait(Dictionary<string, JsonElement> args)
    {
        // Wait is handled synchronously for simplicity — the response is just immediate
        int ms = args.TryGetValue("ms", out var mv) ? mv.GetInt32() : 0;
        return new Godot.Collections.Dictionary
        {
            ["waited_ms"] = ms,
        };
    }

    private Godot.Collections.Dictionary CmdProps(Dictionary<string, JsonElement> args)
    {
        string nodePath = args.TryGetValue("node_path", out var npv) ? npv.GetString()! : "";

        var node = GetNodeOrNull(nodePath);
        if (node == null)
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Node not found: {nodePath}",
            };
        }

        var properties = new Godot.Collections.Dictionary();
        foreach (var prop in node.GetPropertyList())
        {
            var name = (string)prop["name"];
            // Skip internal/editor properties that create noise
            var usage = (PropertyUsageFlags)(int)(long)prop["usage"];
            if ((usage & PropertyUsageFlags.Editor) == 0 && (usage & PropertyUsageFlags.Storage) == 0)
                continue;

            var val = node.Get(name);
            properties[name] = VariantToSerializable(val);
        }

        var result = new Godot.Collections.Dictionary
        {
            ["node"] = nodePath,
            ["type"] = node.GetClass(),
            ["property_count"] = properties.Count,
            ["properties"] = properties,
        };

        if (node is Node3D n3d)
        {
            result["global_position"] = new Godot.Collections.Dictionary
            {
                ["x"] = Math.Round(n3d.GlobalPosition.X, 4),
                ["y"] = Math.Round(n3d.GlobalPosition.Y, 4),
                ["z"] = Math.Round(n3d.GlobalPosition.Z, 4),
            };
            result["global_rotation"] = new Godot.Collections.Dictionary
            {
                ["x"] = Math.Round(n3d.GlobalRotation.X, 4),
                ["y"] = Math.Round(n3d.GlobalRotation.Y, 4),
                ["z"] = Math.Round(n3d.GlobalRotation.Z, 4),
            };
        }
        else if (node is Control ctrl)
        {
            result["global_position"] = new Godot.Collections.Dictionary
            {
                ["x"] = Math.Round(ctrl.GlobalPosition.X, 4),
                ["y"] = Math.Round(ctrl.GlobalPosition.Y, 4),
            };
            result["size"] = new Godot.Collections.Dictionary
            {
                ["x"] = Math.Round(ctrl.Size.X, 4),
                ["y"] = Math.Round(ctrl.Size.Y, 4),
            };
        }

        return result;
    }

    private static Variant VariantToSerializable(Variant val)
    {
        // Convert Godot types to JSON-friendly representations
        return val.VariantType switch
        {
            Variant.Type.Vector2 => VecToDict((Vector2)val),
            Variant.Type.Vector3 => Vec3ToDict((Vector3)val),
            Variant.Type.Color => ColorToDict((Color)val),
            Variant.Type.Rect2 => val.ToString(),
            Variant.Type.Transform2D => val.ToString(),
            Variant.Type.Transform3D => val.ToString(),
            Variant.Type.Basis => val.ToString(),
            Variant.Type.Aabb => val.ToString(),
            Variant.Type.NodePath => val.AsNodePath().ToString(),
            Variant.Type.Object => val.AsGodotObject() is GodotObject obj ? (Variant)(obj.GetClass().ToString() ?? "Object") : default,
            Variant.Type.Nil => default,
            _ => val,
        };
    }

    private static Godot.Collections.Dictionary VecToDict(Vector2 v) =>
        new() { ["x"] = Math.Round(v.X, 4), ["y"] = Math.Round(v.Y, 4) };

    private static Godot.Collections.Dictionary Vec3ToDict(Vector3 v) =>
        new() { ["x"] = Math.Round(v.X, 4), ["y"] = Math.Round(v.Y, 4), ["z"] = Math.Round(v.Z, 4) };

    private static Godot.Collections.Dictionary ColorToDict(Color c) =>
        new() { ["r"] = Math.Round(c.R, 4), ["g"] = Math.Round(c.G, 4), ["b"] = Math.Round(c.B, 4), ["a"] = Math.Round(c.A, 4) };

    private Godot.Collections.Dictionary CmdSet(Dictionary<string, JsonElement> args)
    {
        string nodePath = args.TryGetValue("node_path", out var npv) ? npv.GetString()! : "";
        string property = args.TryGetValue("property", out var pv) ? pv.GetString()! : "";

        var node = GetNodeOrNull(nodePath);
        if (node == null)
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Node not found: {nodePath}",
            };
        }

        // Read current value to determine expected type
        var current = node.Get(property);
        if (current.VariantType == Variant.Type.Nil)
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Property not found: {property}",
            };
        }

        if (!args.TryGetValue("value", out var valueEl))
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = "Missing 'value' argument",
            };
        }

        // Convert JSON value to the correct Variant type based on current type
        Variant newVal = ConvertJsonToVariantTyped(valueEl, current.VariantType);
        node.Set(property, newVal);

        return new Godot.Collections.Dictionary
        {
            ["node"] = nodePath,
            ["property"] = property,
            ["value"] = node.Get(property).ToString(),
        };
    }

    private static Variant ConvertJsonToVariantTyped(JsonElement el, Variant.Type targetType)
    {
        return targetType switch
        {
            Variant.Type.Bool => el.ValueKind == JsonValueKind.True || (el.ValueKind == JsonValueKind.String && el.GetString() == "true"),
            Variant.Type.Int => el.TryGetInt64(out long l) ? l : long.Parse(el.GetString()!),
            Variant.Type.Float => el.ValueKind == JsonValueKind.Number ? el.GetDouble() : double.Parse(el.GetString()!),
            Variant.Type.String => el.ToString(),
            Variant.Type.Vector2 => ParseVector2(el),
            Variant.Type.Vector3 => ParseVector3(el),
            Variant.Type.Color => ParseColor(el),
            _ => JsonElementToVariant(el),
        };
    }

    private static Vector2 ParseVector2(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            float x = el.TryGetProperty("x", out var xv) ? (float)xv.GetDouble() : 0;
            float y = el.TryGetProperty("y", out var yv) ? (float)yv.GetDouble() : 0;
            return new Vector2(x, y);
        }
        return default;
    }

    private static Vector3 ParseVector3(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            float x = el.TryGetProperty("x", out var xv) ? (float)xv.GetDouble() : 0;
            float y = el.TryGetProperty("y", out var yv) ? (float)yv.GetDouble() : 0;
            float z = el.TryGetProperty("z", out var zv) ? (float)zv.GetDouble() : 0;
            return new Vector3(x, y, z);
        }
        return default;
    }

    private static Color ParseColor(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            float r = el.TryGetProperty("r", out var rv) ? (float)rv.GetDouble() : 0;
            float g = el.TryGetProperty("g", out var gv) ? (float)gv.GetDouble() : 0;
            float b = el.TryGetProperty("b", out var bv) ? (float)bv.GetDouble() : 0;
            float a = el.TryGetProperty("a", out var av) ? (float)av.GetDouble() : 1;
            return new Color(r, g, b, a);
        }
        if (el.ValueKind == JsonValueKind.String)
            return new Color(el.GetString()!);
        return Colors.White;
    }

    private Godot.Collections.Dictionary CmdSequence(Dictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("actions", out var actionsEl) || actionsEl.ValueKind != JsonValueKind.Array)
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = "Missing 'actions' array. Format: [{\"name\":\"action\",\"start_ms\":0,\"duration_ms\":500}, ...]",
            };
        }

        int count = 0;
        foreach (var actionEl in actionsEl.EnumerateArray())
        {
            string name = actionEl.TryGetProperty("name", out var nv) ? nv.GetString()! : "";
            int startMs = actionEl.TryGetProperty("start_ms", out var sv) ? sv.GetInt32() : 0;
            int durationMs = actionEl.TryGetProperty("duration_ms", out var dv) ? dv.GetInt32() : 100;

            if (!InputMap.HasAction(name))
                continue;

            // Schedule press after start_ms
            var pressTimer = GetTree().CreateTimer(startMs / 1000.0);
            pressTimer.Timeout += () => Input.ActionPress(name);

            // Schedule release after start_ms + duration_ms
            var releaseTimer = GetTree().CreateTimer((startMs + durationMs) / 1000.0);
            releaseTimer.Timeout += () => Input.ActionRelease(name);

            count++;
        }

        return new Godot.Collections.Dictionary
        {
            ["scheduled"] = count,
        };
    }

    private Godot.Collections.Dictionary CmdListCommands(Dictionary<string, JsonElement> args)
    {
        var builtin = new Godot.Collections.Array();
        var game = new Godot.Collections.Array();
        foreach (var name in _commands.Keys)
        {
            if (_builtinCommands.Contains(name))
                builtin.Add(name);
            else
                game.Add(name);
        }
        return new Godot.Collections.Dictionary
        {
            ["builtin"] = builtin,
            ["game"] = game,
        };
    }

    // ── Reflection: signal subscription graph ───────────────────────

    /// <summary>
    /// Enumerate signals across the scene tree along with their connections.
    /// Optional filters: --node PATH (limit to one node), --name PATTERN
    /// (case-insensitive substring on signal name). With no filters, walks
    /// every node under /root and reports every user-emitter signal that
    /// has at least one connection.
    /// </summary>
    private Godot.Collections.Dictionary CmdSignals(Dictionary<string, JsonElement> args)
    {
        string? nodePathArg = args.TryGetValue("node", out var npv) ? npv.GetString() : null;
        string? namePattern = args.TryGetValue("name", out var nv) ? nv.GetString() : null;
        bool includeUnconnected = args.TryGetValue("include_unconnected", out var iuv) && iuv.GetBoolean();

        var results = new Godot.Collections.Array();

        if (nodePathArg != null)
        {
            var node = GetNodeOrNull(nodePathArg);
            if (node == null)
            {
                return new Godot.Collections.Dictionary
                {
                    ["ok"] = false,
                    ["error"] = $"Node not found: {nodePathArg}",
                };
            }
            CollectSignalsForNode(node, namePattern, includeUnconnected, results);
        }
        else
        {
            // Walk the entire scene tree (autoloads + current scene + everything else under /root)
            CollectSignalsRecursive(GetTree().Root, namePattern, includeUnconnected, results);
        }

        return new Godot.Collections.Dictionary
        {
            ["count"] = results.Count,
            ["signals"] = results,
        };
    }

    private void CollectSignalsRecursive(Node node, string? namePattern, bool includeUnconnected, Godot.Collections.Array results)
    {
        CollectSignalsForNode(node, namePattern, includeUnconnected, results);
        foreach (var child in node.GetChildren())
            CollectSignalsRecursive(child, namePattern, includeUnconnected, results);
    }

    private static void CollectSignalsForNode(Node node, string? namePattern, bool includeUnconnected, Godot.Collections.Array results)
    {
        Godot.Collections.Array<Godot.Collections.Dictionary> signalList;
        try
        {
            signalList = node.GetSignalList();
        }
        catch (Exception)
        {
            return;
        }

        var nodeType = node.GetType();

        foreach (var sigDict in signalList)
        {
            var sigName = (string)sigDict["name"];

            if (namePattern != null && !sigName.Contains(namePattern, StringComparison.OrdinalIgnoreCase))
                continue;

            var connArr = new Godot.Collections.Array();

            // Source A: GDScript-style and editor-defined connections via Object.GetSignalConnectionList().
            // Filter out the source-generator's reflexive bridge: when a [Signal] declaration triggers
            // EmitSignal native dispatch, Godot internally connects the signal to the C# class's
            // RaiseGodotClassSignalCallbacks override, which appears here as a connection where the
            // target is the same node and the method name equals the signal name. That's plumbing,
            // not subscription — skip it.
            Godot.Collections.Array<Godot.Collections.Dictionary> connections;
            try
            {
                connections = node.GetSignalConnectionList(sigName);
            }
            catch (Exception)
            {
                connections = new Godot.Collections.Array<Godot.Collections.Dictionary>();
            }

            foreach (var connDict in connections)
            {
                if (!connDict.ContainsKey("callable")) continue;
                var callable = connDict["callable"].AsCallable();
                var callableTarget = callable.Target;
                var methodName = callable.Method.ToString();

                // Skip the source-generator self-bridge
                if (callableTarget == (GodotObject)node && methodName == sigName)
                    continue;

                var targetPath = callableTarget is Node tn ? tn.GetPath().ToString() : (callableTarget?.GetClass() ?? "?");
                connArr.Add(new Godot.Collections.Dictionary
                {
                    ["target"] = targetPath,
                    ["method"] = methodName,
                    ["kind"] = "connect",
                });
            }

            // Source B: C# event subscribers attached via the `+=` syntax. The Godot.NET source
            // generator emits a private field `backing_<SignalName>` of the EventHandler delegate
            // type, and the public event's add/remove operators write to it. GetSignalConnectionList
            // never sees these — they live in the delegate's invocation list, accessible only via
            // reflection on the backing field. This is the dominant subscription style in C#-first
            // Godot projects, so missing it makes the signal graph essentially invisible.
            var backingField = nodeType.GetField(
                "backing_" + sigName,
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (backingField != null && typeof(Delegate).IsAssignableFrom(backingField.FieldType))
            {
                Delegate? backingDelegate = null;
                try
                {
                    backingDelegate = backingField.GetValue(node) as Delegate;
                }
                catch (Exception)
                {
                    // ignore — leave the slot empty
                }

                if (backingDelegate != null)
                {
                    foreach (var sub in backingDelegate.GetInvocationList())
                    {
                        var subTarget = sub.Target;
                        var subTargetPath = subTarget is Node stn
                            ? stn.GetPath().ToString()
                            : (subTarget?.GetType().FullName ?? "?");
                        connArr.Add(new Godot.Collections.Dictionary
                        {
                            ["target"] = subTargetPath,
                            ["method"] = sub.Method.Name,
                            ["kind"] = "csharp_event",
                        });
                    }
                }
            }

            if (connArr.Count == 0 && !includeUnconnected)
                continue;

            // Pull arg names + types from the signal definition
            var argArr = new Godot.Collections.Array();
            if (sigDict.TryGetValue("args", out var argsVar))
            {
                foreach (var a in argsVar.AsGodotArray())
                {
                    var ad = a.AsGodotDictionary();
                    var aname = ad.ContainsKey("name") ? (string)ad["name"] : "";
                    var atype = ad.ContainsKey("type") ? ((Variant.Type)(int)ad["type"]).ToString() : "?";
                    argArr.Add(new Godot.Collections.Dictionary
                    {
                        ["name"] = aname,
                        ["type"] = atype,
                    });
                }
            }

            results.Add(new Godot.Collections.Dictionary
            {
                ["node"] = node.GetPath().ToString(),
                ["node_type"] = node.GetClass(),
                ["signal"] = sigName,
                ["args"] = argArr,
                ["connection_count"] = connArr.Count,
                ["connections"] = connArr,
            });
        }
    }

    // ── Reflection: describe a node's C# instance state ─────────────

    /// <summary>
    /// Walk public C# properties on a node and dump their values as JSON.
    /// Filters out properties declared on Godot.Node and its bases (so the
    /// agent sees game state, not framework noise). Pass --include-godot
    /// to include the Godot built-ins. Pass --depth N to recurse into
    /// non-primitive members (default 1, max 3).
    /// </summary>
    private Godot.Collections.Dictionary CmdDescribe(Dictionary<string, JsonElement> args)
    {
        string nodePath = args.TryGetValue("node_path", out var npv) ? npv.GetString()! : "";
        bool includeGodot = args.TryGetValue("include_godot", out var igv) && igv.GetBoolean();
        int depth = args.TryGetValue("depth", out var dv) ? Math.Clamp(dv.GetInt32(), 0, 3) : 1;

        var node = GetNodeOrNull(nodePath);
        if (node == null)
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Node not found: {nodePath}",
            };
        }

        var type = node.GetType();
        var properties = DescribeObject(node, type, includeGodot, depth);

        return new Godot.Collections.Dictionary
        {
            ["node"] = nodePath,
            ["type"] = type.FullName ?? type.Name,
            ["godot_type"] = node.GetClass(),
            ["properties"] = properties,
        };
    }

    private static Godot.Collections.Dictionary DescribeObject(object instance, Type type, bool includeGodot, int depth)
    {
        var result = new Godot.Collections.Dictionary();

        // Walk all public instance properties on this type and its bases,
        // stopping at Godot.Node unless includeGodot is set.
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in props)
        {
            // Skip indexers
            if (prop.GetIndexParameters().Length > 0) continue;

            var declaring = prop.DeclaringType;
            if (declaring == null) continue;

            // Filter Godot framework properties unless asked
            if (!includeGodot && IsGodotFrameworkType(declaring)) continue;

            // Skip set-only properties
            if (!prop.CanRead) continue;

            object? value;
            try
            {
                value = prop.GetValue(instance);
            }
            catch (Exception ex)
            {
                result[prop.Name] = $"<error: {ex.GetType().Name}>";
                continue;
            }

            result[prop.Name] = SerializeValue(value, depth);
        }

        // Also walk public instance fields (less common but valid)
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        foreach (var field in fields)
        {
            if (!includeGodot && field.DeclaringType != null && IsGodotFrameworkType(field.DeclaringType))
                continue;

            object? value;
            try
            {
                value = field.GetValue(instance);
            }
            catch (Exception ex)
            {
                result[field.Name] = $"<error: {ex.GetType().Name}>";
                continue;
            }

            result[field.Name] = SerializeValue(value, depth);
        }

        return result;
    }

    private static bool IsGodotFrameworkType(Type type)
    {
        // Anything in the Godot namespace counts as framework — Node, Object, etc.
        var ns = type.Namespace;
        return ns != null && (ns == "Godot" || ns.StartsWith("Godot."));
    }

    // ── Reflection: static type inspection ──────────────────────────

    /// <summary>
    /// Inspect static fields and properties on a C# type by name. The
    /// game-side equivalent of <see cref="CmdDescribe"/> for things that
    /// don't live on a Node — typically static data tables (DataLoader.SpellById,
    /// item catalogs, configuration constants).
    ///
    /// <para>Args: <c>type</c> (fully-qualified or short C# type name),
    /// optional <c>member</c> (single field/property to inspect — if
    /// omitted, all public statics are dumped), optional <c>depth</c>
    /// (recursion depth for nested objects, 0-3, default 1).</para>
    ///
    /// <para>Type lookup walks all loaded assemblies. Both fully qualified
    /// names (<c>OdeToTheBard.Data.DataLoader</c>) and short names
    /// (<c>DataLoader</c>) are accepted; the resolved <c>FullName</c> is
    /// returned in the result so the caller knows which type was matched.
    /// On a name conflict, the first match wins — disambiguate by passing
    /// the fully qualified name.</para>
    /// </summary>
    private Godot.Collections.Dictionary CmdStatic(Dictionary<string, JsonElement> args)
    {
        string typeName = args.TryGetValue("type", out var tv) ? (tv.GetString() ?? "") : "";
        string? memberName = args.TryGetValue("member", out var mv) ? mv.GetString() : null;
        int depth = args.TryGetValue("depth", out var dv) ? Math.Clamp(dv.GetInt32(), 0, 3) : 1;

        if (string.IsNullOrEmpty(typeName))
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = "type required",
            };
        }

        var type = ResolveTypeByName(typeName);
        if (type == null)
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Type not found: {typeName}",
            };
        }

        var result = new Godot.Collections.Dictionary
        {
            ["type"] = type.FullName ?? type.Name,
        };

        const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.Static;

        if (memberName != null)
        {
            // Single member: try property first, then field.
            var prop = type.GetProperty(memberName, staticFlags);
            FieldInfo? field = null;
            if (prop == null)
                field = type.GetField(memberName, staticFlags);

            if (prop == null && field == null)
            {
                var available = new Godot.Collections.Array();
                foreach (var p in type.GetProperties(staticFlags)) available.Add(p.Name);
                foreach (var f in type.GetFields(staticFlags)) available.Add(f.Name);
                return new Godot.Collections.Dictionary
                {
                    ["ok"] = false,
                    ["error"] = $"Member not found: {type.Name}.{memberName}",
                    ["available"] = available,
                };
            }

            try
            {
                object? value = prop != null ? prop.GetValue(null) : field!.GetValue(null);
                result["member"] = memberName;
                result["value"] = SerializeValue(value, depth);
            }
            catch (Exception ex)
            {
                return new Godot.Collections.Dictionary
                {
                    ["ok"] = false,
                    ["error"] = $"Get threw: {ex.GetType().Name}: {ex.Message}",
                };
            }
        }
        else
        {
            // All public static members
            var members = new Godot.Collections.Dictionary();
            foreach (var prop in type.GetProperties(staticFlags))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                if (!prop.CanRead) continue;
                try { members[prop.Name] = SerializeValue(prop.GetValue(null), depth); }
                catch (Exception ex) { members[prop.Name] = $"<error: {ex.GetType().Name}>"; }
            }
            foreach (var field in type.GetFields(staticFlags))
            {
                try { members[field.Name] = SerializeValue(field.GetValue(null), depth); }
                catch (Exception ex) { members[field.Name] = $"<error: {ex.GetType().Name}>"; }
            }
            result["members"] = members;
        }

        return result;
    }

    // ── Reflection: method invocation ───────────────────────────────

    /// <summary>
    /// Invoke an instance method on a Node (target = "/node/path") or a
    /// static method on a type (target = "Namespace.TypeName"). Argument
    /// values are passed as a JSON array; primitive types (string, int,
    /// long, double, bool, null) are coerced to the method's parameter
    /// types automatically. Enum parameters accept their string name.
    /// Complex parameter types are not supported in v1 — for those, write
    /// a project-specific dev command via <c>RegisterCommand</c>.
    ///
    /// <para>Args: <c>target</c> (string — node path or type name),
    /// <c>method</c> (string), optional <c>args</c> (array of values).</para>
    ///
    /// <para>Returns: <c>{ok, method, returned}</c> on success;
    /// <c>{ok: false, error, available?}</c> on failure (with the available
    /// method names listed when the method isn't found).</para>
    /// </summary>
    private Godot.Collections.Dictionary CmdInvoke(Dictionary<string, JsonElement> args)
    {
        string target = args.TryGetValue("target", out var tv) ? (tv.GetString() ?? "") : "";
        string methodName = args.TryGetValue("method", out var mv) ? (mv.GetString() ?? "") : "";

        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(methodName))
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = "target and method required",
            };
        }

        // Parse args array — accept JSON array or omit entirely. Each element
        // is kept as a JsonElement so CoerceArg can decide between primitive
        // coercion (for value/string/enum parameters) and JSON deserialization
        // (for records, classes, lists, dictionaries, etc).
        var methodArgsList = new List<JsonElement>();
        if (args.TryGetValue("args", out var av) && av.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in av.EnumerateArray())
                methodArgsList.Add(element);
        }

        // Resolve target: instance method (node path starts with /) or static method (type name).
        object? instance = null;
        Type? type;
        if (target.StartsWith("/"))
        {
            var node = GetNodeOrNull(target);
            if (node == null)
            {
                return new Godot.Collections.Dictionary
                {
                    ["ok"] = false,
                    ["error"] = $"Node not found: {target}",
                };
            }
            instance = node;
            type = node.GetType();
        }
        else
        {
            type = ResolveTypeByName(target);
            if (type == null)
            {
                return new Godot.Collections.Dictionary
                {
                    ["ok"] = false,
                    ["error"] = $"Type not found: {target}",
                };
            }
        }

        var bindingFlags = instance != null
            ? BindingFlags.Public | BindingFlags.Instance
            : BindingFlags.Public | BindingFlags.Static;

        var methods = type.GetMethods(bindingFlags).Where(m => m.Name == methodName).ToArray();
        if (methods.Length == 0)
        {
            var available = new Godot.Collections.Array();
            foreach (var m in type.GetMethods(bindingFlags).Take(50))
                available.Add(m.Name);
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Method not found: {type.Name}.{methodName}",
                ["available"] = available,
            };
        }

        // Pick the first overload that matches arg count and accepts coerced args.
        MethodInfo? chosen = null;
        object?[]? coerced = null;
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != methodArgsList.Count) continue;
            try
            {
                var argsArray = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                    argsArray[i] = CoerceArg(methodArgsList[i], parameters[i].ParameterType, InvokeJsonOptions);
                chosen = method;
                coerced = argsArray;
                break;
            }
            catch
            {
                // Try the next overload
            }
        }

        if (chosen == null)
        {
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"No overload of {type.Name}.{methodName} matches {methodArgsList.Count} args (primitive coercion or JSON deserialization both failed)",
            };
        }

        try
        {
            object? returned = chosen.Invoke(instance, coerced);
            return new Godot.Collections.Dictionary
            {
                ["ok"] = true,
                ["method"] = $"{type.Name}.{methodName}",
                ["returned"] = SerializeValue(returned, 1),
            };
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            return new Godot.Collections.Dictionary
            {
                ["ok"] = false,
                ["error"] = $"Invoke threw: {inner.GetType().Name}: {inner.Message}",
            };
        }
    }

    // ── Reflection helpers ──────────────────────────────────────────

    /// <summary>
    /// Resolve a C# type by name. Tries direct <see cref="Type.GetType(string)"/>
    /// first, then walks every loaded assembly looking for a fully qualified
    /// or short-name match. Used by <see cref="CmdStatic"/> and the
    /// static-method path of <see cref="CmdInvoke"/>.
    /// </summary>
    private static Type? ResolveTypeByName(string typeName)
    {
        var t = Type.GetType(typeName);
        if (t != null) return t;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                t = asm.GetType(typeName);
                if (t != null) return t;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(x => x != null).ToArray()!; }

                foreach (var candidate in types)
                {
                    if (candidate == null) continue;
                    if (candidate.FullName == typeName) return candidate;
                    if (candidate.Name == typeName) return candidate;
                }
            }
            catch (Exception)
            {
                // Skip assemblies that can't be reflected
            }
        }
        return null;
    }

    /// <summary>
    /// Coerce a JSON element to a target method parameter type. The strategy
    /// is two-tier: simple types (primitive, string, enum, nullable thereof)
    /// get fast-path primitive coercion via <see cref="Convert.ChangeType(object, Type)"/>;
    /// complex types (records, classes, lists, dictionaries, polymorphic
    /// hierarchies) are deserialized via
    /// <see cref="JsonElement.Deserialize(Type, JsonSerializerOptions)"/>
    /// using the autoload's <see cref="InvokeJsonOptions"/>. Throws on
    /// incompatible coercion so <see cref="CmdInvoke"/> can fall through
    /// to the next overload.
    /// </summary>
    private static object? CoerceArg(JsonElement value, Type targetType, JsonSerializerOptions jsonOptions)
    {
        // Null handling — first because the underlying type check below
        // doesn't apply to a null JSON value.
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                throw new InvalidCastException($"Cannot pass null to value type {targetType.Name}");
            return null;
        }

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // Simple types — primitive coercion path. Matches the original
        // behavior so existing invoke calls keep working unchanged.
        if (IsSimpleType(underlying))
        {
            object? primitive = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.TryGetInt64(out var l) ? (object)l : value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidCastException(
                    $"Cannot coerce JSON {value.ValueKind} to simple type {underlying.Name}"),
            };

            if (underlying.IsEnum)
            {
                if (primitive is string s) return Enum.Parse(underlying, s, ignoreCase: true);
                return Enum.ToObject(underlying, primitive!);
            }

            if (underlying.IsInstanceOfType(primitive)) return primitive;
            return Convert.ChangeType(primitive, underlying);
        }

        // Complex types — JSON deserialization path. Records, classes,
        // List<T>, Dictionary<K,V>, polymorphic hierarchies declared with
        // [JsonDerivedType], etc. The discriminator-based polymorphism
        // works automatically because the converter reads the type
        // metadata, not the JsonSerializerOptions.
        return value.Deserialize(underlying, jsonOptions);
    }

    /// <summary>
    /// True for parameter types that should use primitive coercion rather
    /// than JSON deserialization in <see cref="CoerceArg"/>. The criterion
    /// is "values that one might reasonably pass on a CLI": numbers,
    /// strings, bools, enum names. Everything else (records, classes,
    /// containers) goes through JSON deserialization.
    /// </summary>
    private static bool IsSimpleType(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);

    private static Variant SerializeValue(object? value, int depth)
    {
        // Real JSON null, not the string "null". Json.Stringify on a default
        // (Nil) Variant produces JSON `null`, which is what consumers expect.
        if (value == null) return default;

        // Primitives — pass through
        switch (value)
        {
            case string s: return s;
            case bool b: return b;
            case int i: return i;
            case long l: return l;
            case float f: return Math.Round(f, 4);
            case double d: return Math.Round(d, 4);
            case Vector2 v2: return new Godot.Collections.Dictionary
            {
                ["x"] = Math.Round(v2.X, 4), ["y"] = Math.Round(v2.Y, 4),
            };
            case Vector3 v3: return new Godot.Collections.Dictionary
            {
                ["x"] = Math.Round(v3.X, 4), ["y"] = Math.Round(v3.Y, 4), ["z"] = Math.Round(v3.Z, 4),
            };
            case Color c: return new Godot.Collections.Dictionary
            {
                ["r"] = Math.Round(c.R, 4), ["g"] = Math.Round(c.G, 4),
                ["b"] = Math.Round(c.B, 4), ["a"] = Math.Round(c.A, 4),
            };
        }

        // Enums — return as their string name
        var t = value.GetType();
        if (t.IsEnum) return value.ToString() ?? "?";

        // Godot Node references — show as path, don't recurse
        if (value is Node node)
        {
            return new Godot.Collections.Dictionary
            {
                ["_type"] = "Node",
                ["path"] = node.GetPath().ToString(),
                ["class"] = node.GetClass(),
            };
        }

        // Collections — serialize as array
        if (value is IEnumerable enumerable && value is not string)
        {
            var arr = new Godot.Collections.Array();
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count >= 50)
                {
                    arr.Add("<truncated>");
                    break;
                }
                arr.Add(SerializeValue(item, Math.Max(0, depth - 1)));
                count++;
            }
            return arr;
        }

        // Custom class — recurse if depth allows, otherwise toString
        if (depth > 0 && t.IsClass)
        {
            try
            {
                var nested = DescribeObject(value, t, includeGodot: false, depth - 1);
                nested["_type"] = t.Name;
                return nested;
            }
            catch (Exception)
            {
                return value.ToString() ?? "?";
            }
        }

        return value.ToString() ?? "?";
    }

    private Godot.Collections.Dictionary CmdQuit(Dictionary<string, JsonElement> args)
    {
        // Respond first, then quit on next frame
        CallDeferred(MethodName.DeferredQuit);
        return new Godot.Collections.Dictionary { ["quitting"] = true };
    }

    private void DeferredQuit()
    {
        GetTree().Quit();
    }

    /// <summary>
    /// Capture a log line (called from _Process if log capture is on).
    /// Can also be called manually.
    /// </summary>
    public void CaptureLog(string line)
    {
        _logBuffer.Add(line);
        if (_logBuffer.Count > MaxLogLines)
            _logBuffer.RemoveAt(0);
    }
}
