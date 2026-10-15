using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

using System.IO;

namespace SyncCue.Services;

public enum TunnelStatus
{
    Off,
    Starting,
    Ready,
    Stopping,
    Error
}

public sealed class TunnelState
{
    public TunnelStatus Status { get; init; } = TunnelStatus.Off;
    public string? Url { get; init; }
    public int? Pid { get; init; }
    public string Message { get; init; } = "隧道未开，局域网仍可用。";
}

/// <summary>
/// Cloudflare Quick Tunnel (cloudflared) for public wss://*.trycloudflare.com.
/// Hard rule: stop on close-room / app exit.
/// </summary>
public sealed class TunnelService : IAsyncDisposable
{
    private static readonly Regex CfUrl = new(
        @"https://[a-z0-9-]+\.trycloudflare\.com",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly object _gate = new();
    private Process? _process;
    private CancellationTokenSource? _cts;
    private int _generation;
    private TunnelState _state = new();

    public TunnelState State
    {
        get { lock (_gate) return _state; }
    }

    public event Action<TunnelState>? Changed;

    public async Task<TunnelState> StartAsync(int localPort = 3131, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        var generation = Interlocked.Increment(ref _generation);
        Update(new TunnelState
        {
            Status = TunnelStatus.Starting,
            Message = "正在启动 Cloudflare 临时隧道…"
        });

        var exe = ResolveCloudflared();
        if (exe is null)
        {
            var err = new TunnelState
            {
                Status = TunnelStatus.Error,
                Message = "找不到 cloudflared.exe。请放到程序目录，或保留 sync-cue-v1\\.runtime\\tunnel-bin\\cloudflared.exe。"
            };
            Update(err);
            return err;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"tunnel --url http://127.0.0.1:{localPort} --no-autoupdate --protocol http2",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 cloudflared。");
        }
        catch (Exception ex)
        {
            var err = new TunnelState { Status = TunnelStatus.Error, Message = "启动隧道失败：" + ex.Message };
            Update(err);
            return err;
        }

        lock (_gate) _process = proc;
        Update(new TunnelState
        {
            Status = TunnelStatus.Starting,
            Pid = proc.Id,
            Message = "已拉起 cloudflared，等待公网地址…"
        });

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linked = _cts.Token;
        var output = new StringBuilder();
        string? publicWs = null;

        void OnData(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (output)
            {
                if (output.Length < 24000) output.AppendLine(line);
            }
            var m = CfUrl.Matches(line);
            if (m.Count > 0)
            {
                try
                {
                    var https = m[^1].Value;
                    var host = new Uri(https).Host;
                    publicWs = "wss://" + host;
                }
                catch { /* ignore */ }
            }
        }

        proc.OutputDataReceived += (_, e) => OnData(e.Data);
        proc.ErrorDataReceived += (_, e) => OnData(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (publicWs is null && DateTime.UtcNow < deadline)
        {
            if (generation != _generation || linked.IsCancellationRequested)
            {
                await StopAsync().ConfigureAwait(false);
                throw new OperationCanceledException("隧道已取消。");
            }
            if (proc.HasExited)
            {
                var tail = output.ToString();
                if (tail.Length > 800) tail = tail[^800..];
                var err = new TunnelState
                {
                    Status = TunnelStatus.Error,
                    Message = "cloudflared 提前退出。" + (string.IsNullOrWhiteSpace(tail) ? "" : " " + tail.Trim())
                };
                Update(err);
                await CleanupProcessAsync().ConfigureAwait(false);
                return err;
            }
            await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
        }

        if (publicWs is null)
        {
            await StopAsync().ConfigureAwait(false);
            var err = new TunnelState { Status = TunnelStatus.Error, Message = "等待公网地址超时，请重试。" };
            Update(err);
            return err;
        }

        Update(new TunnelState
        {
            Status = TunnelStatus.Starting,
            Pid = proc.Id,
            Url = publicWs,
            Message = "已拿到地址，正在探活…"
        });

        var probed = await ProbeAsync(publicWs, TimeSpan.FromSeconds(60), () => generation != _generation || proc.HasExited)
            .ConfigureAwait(false);
        if (generation != _generation)
        {
            await StopAsync().ConfigureAwait(false);
            throw new OperationCanceledException("隧道已取消。");
        }
        if (!probed)
        {
            await StopAsync().ConfigureAwait(false);
            var err = new TunnelState
            {
                Status = TunnelStatus.Error,
                Message = "公网地址探活失败（/health）。请确认本机房间服务已在 3131 监听。"
            };
            Update(err);
            return err;
        }

        var ready = new TunnelState
        {
            Status = TunnelStatus.Ready,
            Pid = proc.Id,
            Url = publicWs,
            Message = "公网隧道已就绪，可复制地址给远端。"
        };
        Update(ready);
        return ready;
    }

    public async Task StopAsync()
    {
        Interlocked.Increment(ref _generation);
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;

        Update(new TunnelState { Status = TunnelStatus.Stopping, Message = "正在关闭隧道…" });
        await CleanupProcessAsync().ConfigureAwait(false);
        Update(new TunnelState { Status = TunnelStatus.Off, Message = "隧道已关闭，局域网仍可用。" });
    }

    private async Task CleanupProcessAsync()
    {
        Process? proc;
        lock (_gate)
        {
            proc = _process;
            _process = null;
        }
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
        finally
        {
            try { proc.Dispose(); } catch { /* ignore */ }
        }
    }

    private void Update(TunnelState state)
    {
        lock (_gate) _state = state;
        Changed?.Invoke(state);
    }

    private static async Task<bool> ProbeAsync(string wssUrl, TimeSpan timeout, Func<bool> abort)
    {
        var https = wssUrl.Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase) + "/health";
        var deadline = DateTime.UtcNow + timeout;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        while (DateTime.UtcNow < deadline)
        {
            if (abort()) return false;
            try
            {
                using var resp = await http.GetAsync(https).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (body.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase) ||
                        body.Contains("\"ok\": true", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* retry */ }
            await Task.Delay(1000).ConfigureAwait(false);
        }
        return false;
    }

    public static string? ResolveCloudflared()
    {
        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "cloudflared.exe"));
        candidates.Add(Path.Combine(baseDir, "tools", "cloudflared.exe"));
        candidates.Add(Path.Combine(baseDir, "bin", "cloudflared.exe"));

        // Dev layouts next to the SyncCue tree
        var dig = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dig is not null; i++, dig = dig.Parent)
        {
            candidates.Add(Path.Combine(dig.FullName, "cloudflared.exe"));
            candidates.Add(Path.Combine(dig.FullName, "bin", "cloudflared.exe"));
            candidates.Add(Path.Combine(dig.FullName, ".runtime", "tunnel-bin", "cloudflared.exe"));
            candidates.Add(Path.Combine(dig.FullName, "sync-cue-v1", ".runtime", "tunnel-bin", "cloudflared.exe"));
            candidates.Add(Path.Combine(dig.FullName, "sync-cue-v1", "bin", "cloudflared.exe"));
        }

        candidates.Add(@"D:\Working\SyncCue\sync-cue-v1\.runtime\tunnel-bin\cloudflared.exe");
        candidates.Add(@"D:\Working\SyncCue\sync-cue-v1\bin\cloudflared.exe");

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    return path;
            }
            catch { /* ignore */ }
        }
        return null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
