using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace FMBridge.Voice;

internal sealed class VoiceServer : IDisposable
{
    private const int MaxMessageBytes = 256 * 1024;

    private readonly ManualLogSource _log;
    private readonly ConcurrentDictionary<int, Conn> _clients = new ConcurrentDictionary<int, Conn>();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _port;
    private readonly bool _allowCommands;
    private HttpListener _listener;
    private Task _acceptLoop;
    private int _nextClientId;

    public MainThreadQueue Queue { get; }
    public SnapshotStore Snapshots { get; } = new SnapshotStore();

    public VoiceServer(ManualLogSource log, int port, bool allowCommands)
    {
        _log = log;
        _port = port;
        _allowCommands = allowCommands;
        Queue = new MainThreadQueue(log);
    }

    public double UptimeSec => _clock.Elapsed.TotalSeconds;

    public bool Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
        }
        catch (Exception e)
        {
            _log.LogError($"[Voice] listener failed on 127.0.0.1:{_port}: {e.Message}");
            return false;
        }
        _acceptLoop = Task.Run(AcceptLoop);
        _log.LogInfo($"[Voice] listening ws://127.0.0.1:{_port}/");
        return true;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception e)
            {
                if (_cts.IsCancellationRequested) return;
                _log.LogWarning($"[Voice] accept error: {e.Message}");
                await Task.Delay(500);
                continue;
            }
            _ = HandleHttpContext(ctx);
        }
    }

    private async Task HandleHttpContext(HttpListenerContext ctx)
    {
        if (!ctx.Request.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 426;
            ctx.Response.Close();
            return;
        }
        WebSocket ws;
        try
        {
            ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
        }
        catch (Exception e)
        {
            _log.LogWarning($"[Voice] handshake failed: {e.Message}");
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }
        var conn = new Conn(Interlocked.Increment(ref _nextClientId), ws);
        _clients[conn.Id] = conn;
        _log.LogInfo($"[Voice] client #{conn.Id} connected ({_clients.Count} active)");
        try
        {
            var hello = new JsonObject
            {
                ["event"] = "hello",
                ["data"] = Snapshots.Current?.ToJson() ?? new JsonObject(),
            };
            await conn.SendAsync(hello.ToJsonString());
        }
        catch { }
        await ReceiveLoop(conn);
        _clients.TryRemove(conn.Id, out _);
        _log.LogInfo($"[Voice] client #{conn.Id} gone ({_clients.Count} active)");
    }

    private async Task ReceiveLoop(Conn conn)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        while (!_cts.IsCancellationRequested && conn.S.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await conn.S.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
            }
            catch
            {
                return;
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await conn.S.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                _log.LogWarning($"[Voice] client #{conn.Id} message too large, dropping");
                return;
            }
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.ToArray());
            message.SetLength(0);
            await HandleMessage(conn, text);
        }
    }

    private async Task HandleMessage(Conn conn, string text)
    {
        long? id = null;
        string method = null;
        try
        {
            var root = JsonNode.Parse(text);
            if (root?["id"] is JsonValue jv && jv.TryGetValue<long>(out var parsed))
                id = parsed;
            method = (string)root?["method"];
        }
        catch (Exception e)
        {
            await ReplyError(conn, id, $"bad json: {e.Message}");
            return;
        }
        try
        {
            switch (method)
            {
                case "ping":
                    await ReplyOk(conn, id, new JsonObject
                    {
                        ["pong"] = true,
                        ["uptime_sec"] = Math.Round(UptimeSec, 1),
                        ["clients"] = _clients.Count,
                    });
                    return;

                case "game_status":
                    await ReplyOk(conn, id, Snapshots.Current?.ToJson() ?? new JsonObject());
                    return;


                case "inbox_list":
                    await ReplyOk(conn, id, await FMBridge.World.Inbox.ListAsync(Queue));
                    return;
                case "inbox_read":
                    await ReplyOk(conn, id, await FMBridge.World.Inbox.ReadAsync(Queue, text));
                    return;

                case "read_entity":
                    await ReplyOk(conn, id, await FMBridge.World.ReadEntity.Enqueue(Queue, text));
                    return;

                case "my_club":
                    await ReplyOk(conn, id, await FMBridge.World.MyClub.Enqueue(Queue, text));
                    return;

                case "squad_report":
                    await ReplyOk(conn, id, await FMBridge.World.SquadReport.Enqueue(Queue, text));
                    return;

                case "query_players":
                    await ReplyOk(conn, id, await FMBridge.World.QueryPlayers.Enqueue(Queue, text));
                    return;

                // In-game "Chat with DoF" UI (World/UiInject.cs): a menu row in
                // the Recruitment nav dropdown plus the chat overlay panel. Flat
                // "action" field: menu_add|menu_remove|menu_status|overlay_add|
                // overlay_remove|overlay_show|overlay_hide|overlay_status|
                // overlay_post|overlay_poll|overlay_clear|overlay_thinking.
                // UI-addition only — adds elements of its own, never reads or
                // drives the game's screens. overlay_post appends a chat bubble
                // ("from": "dof"|"user", "text" capped at 4000 chars);
                // overlay_poll drains text the player typed into the panel
                // since the last poll (plus a one-shot new_chat flag when the
                // player clicked "New chat"); overlay_thinking toggles the
                // dimmed wait indicator while the host computes a reply.
                case "ui_inject":
                    await ReplyOk(conn, id, await FMBridge.World.UiInject.Enqueue(Queue, text));
                    return;

                case "shortlist":
                    if (!_allowCommands)
                    {
                        await ReplyError(conn, id, "commands disabled (set Voice.AllowCommands=true)");
                        return;
                    }
                    await ReplyOk(conn, id, await FMBridge.World.Shortlist.Enqueue(Queue, text));
                    return;


                default:
                    await ReplyError(conn, id, $"unknown method '{method}'");
                    return;
            }
        }
        catch (Exception e)
        {
            await ReplyError(conn, id, $"{method} failed: {e.Message}");
        }
    }


    private Task ReplyOk(Conn conn, JsonNode id, JsonObject result)
    {
        return conn.SendAsync(new JsonObject
        {
            ["id"] = id,
            ["ok"] = true,
            ["result"] = result,
        }.ToJsonString());
    }

    private Task ReplyError(Conn conn, JsonNode id, string error)
    {
        return conn.SendAsync(new JsonObject
        {
            ["id"] = id,
            ["ok"] = false,
            ["error"] = error,
        }.ToJsonString());
    }

    public void Broadcast(string eventName, JsonObject data)
    {
        var envelope = new JsonObject
        {
            ["event"] = eventName,
            ["data"] = data ?? new JsonObject(),
        }.ToJsonString();
        foreach (var conn in _clients.Values)
        {
            _ = SendSafe(conn, envelope);
        }
    }

    private async Task SendSafe(Conn conn, string text)
    {
        try
        {
            await conn.SendAsync(text);
        }
        catch
        {
            _clients.TryRemove(conn.Id, out _);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Close(); } catch { }
        foreach (var conn in _clients.Values)
        {
            try { conn.S.Abort(); } catch { }
        }
        _clients.Clear();
    }

    private sealed class Conn
    {
        public readonly int Id;
        public readonly WebSocket S;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public Conn(int id, WebSocket s)
        {
            Id = id;
            S = s;
        }

        public async Task SendAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _lock.WaitAsync();
            try
            {
                await S.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
