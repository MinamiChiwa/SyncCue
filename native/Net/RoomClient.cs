using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SyncCue.Models;

namespace SyncCue.Net;

public sealed class RoomClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _recvTask;
    private readonly Dictionary<int, double> _pendingSync = new();
    private int _syncId;
    private System.Threading.Timer? _syncTimer;

    public RoomClock Clock { get; } = new();
    public bool Connected { get; private set; }
    public SessionInfo? Session { get; private set; }
    public RoomState? Room { get; private set; }
    public string Endpoint { get; private set; } = "ws://127.0.0.1:3131";
    public string? LastError { get; private set; }

    public event Action? Changed;
    public event Action<string, bool>? Message;

    public async Task ConnectAsync(string endpoint, CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        Endpoint = NormalizeEndpoint(endpoint);
        Clock.Reset();
        _pendingSync.Clear();
        LastError = null;

        var ws = new ClientWebSocket();
        _cts = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        try
        {
            await ws.ConnectAsync(new Uri(Endpoint), linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ws.Dispose();
            LastError = "连接失败：" + ex.Message;
            Message?.Invoke(LastError, true);
            Changed?.Invoke();
            throw;
        }

        _ws = ws;
        Connected = true;
        Message?.Invoke(Session is null ? "已连接。可开房或加入。" : "已重连。", false);
        Changed?.Invoke();
        _recvTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        StartSync();
        if (Session is not null)
            await SendAsync(new { type = "resume", code = Session.Code, token = Session.Token }).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        StopSync();
        Connected = false;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* ignore */ }
            _ws.Dispose();
            _ws = null;
        }
        if (_recvTask is not null)
        {
            try { await _recvTask.ConfigureAwait(false); } catch { /* ignore */ }
            _recvTask = null;
        }
        _cts?.Dispose();
        _cts = null;
        Clock.Reset();
        Changed?.Invoke();
    }

    public Task CreateRoomAsync(string nickname, int durationMs) =>
        SendAsync(new { type = "create", nickname, durationMs });

    public Task JoinRoomAsync(string nickname, string code) =>
        SendAsync(new { type = "join", nickname, code });

    public Task StartAsync() => SendAsync(new { type = "start" });
    public Task CancelAsync() => SendAsync(new { type = "cancel" });
    public Task LeaveAsync() => SendAsync(new { type = "leave" });
    public Task CloseRoomAsync() => SendAsync(new { type = "close-room" });
    public Task SetDurationAsync(int durationMs) => SendAsync(new { type = "duration", durationMs });
    public Task SetReadyAsync(bool ready) => SendAsync(new { type = "ready", ready });
    public Task SetNicknameAsync(string nickname) => SendAsync(new { type = "nickname", nickname });

    public void ClearSession()
    {
        Session = null;
        Room = null;
        Changed?.Invoke();
    }

    private void StartSync()
    {
        StopSync();
        void Probe(object? _)
        {
            if (!Connected || _ws?.State != WebSocketState.Open) return;
            var id = Interlocked.Increment(ref _syncId);
            var now = Clock.NowMs();
            lock (_pendingSync)
            {
                foreach (var kv in _pendingSync.Where(kv => now - kv.Value > 3000).ToList())
                    _pendingSync.Remove(kv.Key);
                _pendingSync[id] = now;
            }
            _ = SendAsync(new { type = "sync", id });
        }

        Probe(null);
        for (var i = 1; i <= 5; i++)
        {
            var delay = i * 80;
            _ = Task.Delay(delay).ContinueWith(_ => Probe(null));
        }
        _syncTimer = new System.Threading.Timer(Probe, null, 1000, 1000);
    }

    private void StopSync()
    {
        _syncTimer?.Dispose();
        _syncTimer = null;
        lock (_pendingSync) _pendingSync.Clear();
    }

    private async Task SendAsync(object payload)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            Message?.Invoke("尚未连接。请先连上服务地址。", true);
            return;
        }
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var ws = _ws;
        if (ws is null) return;
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                HandleMessage(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Message?.Invoke("连接中断：" + ex.Message, true);
        }
        finally
        {
            Connected = false;
            StopSync();
            Changed?.Invoke();
        }
    }

    private void HandleMessage(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl)) return;
        var type = typeEl.GetString();
        switch (type)
        {
            case "sync":
            {
                if (!root.TryGetProperty("id", out var idEl) || !root.TryGetProperty("serverNowMs", out var serverEl))
                    break;
                var id = idEl.GetInt32();
                double? sent;
                lock (_pendingSync)
                {
                    if (_pendingSync.TryGetValue(id, out var s))
                    {
                        sent = s;
                        _pendingSync.Remove(id);
                    }
                    else sent = null;
                }
                if (sent is not null)
                    Clock.Sample(sent.Value, Clock.NowMs(), serverEl.GetDouble());
                Changed?.Invoke();
                break;
            }
            case "session":
                Session = new SessionInfo
                {
                    Code = root.GetProperty("code").GetString() ?? "",
                    MemberId = root.GetProperty("memberId").GetString() ?? "",
                    Token = root.GetProperty("token").GetString() ?? ""
                };
                Changed?.Invoke();
                break;
            case "state":
                if (root.TryGetProperty("room", out var roomEl))
                    Room = JsonSerializer.Deserialize<RoomState>(roomEl.GetRawText(), JsonOpts);
                Changed?.Invoke();
                break;
            case "left":
                Session = null;
                Room = null;
                Message?.Invoke("已离开房间。", false);
                Changed?.Invoke();
                break;
            case "room-closed":
                Session = null;
                Room = null;
                Message?.Invoke(root.TryGetProperty("message", out var m1) ? m1.GetString() ?? "房主已关房，你已离开。" : "房主已关房，你已离开。", false);
                Changed?.Invoke();
                break;
            case "kicked":
                Session = null;
                Room = null;
                Message?.Invoke(root.TryGetProperty("message", out var m2) ? m2.GetString() ?? "被踢出。" : "被踢出。", true);
                Changed?.Invoke();
                break;
            case "error":
            {
                var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "错误";
                if (code is "ROOM_NOT_FOUND" or "SESSION_EXPIRED")
                {
                    Session = null;
                    Room = null;
                }
                Message?.Invoke(msg ?? "错误", true);
                Changed?.Invoke();
                break;
            }
        }
    }

    private static string NormalizeEndpoint(string address)
    {
        var trimmed = address.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            trimmed = "ws://" + trimmed;
        return trimmed;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
