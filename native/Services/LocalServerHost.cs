using System.Diagnostics;
using System.Net.Sockets;

using System.IO;

namespace SyncCue.Services;

/// <summary>
/// Ensures the Node room-server is listening on 3131 when possible.
/// Looks for bundled server/ next to the exe, or the dev tree sync-cue-v1.
/// </summary>
public sealed class LocalServerHost : IAsyncDisposable
{
    private Process? _process;
    private readonly object _gate = new();

    public bool Owned { get; private set; }
    public string? LastMessage { get; private set; }

    public async Task<bool> EnsureListeningAsync(int port = 3131, CancellationToken ct = default)
    {
        if (await IsPortOpenAsync(port, ct).ConfigureAwait(false))
        {
            LastMessage = $"本机 {port} 已在监听。";
            return true;
        }

        var serverEntry = ResolveServerEntry();
        var node = ResolveNode();
        if (serverEntry is null || node is null)
        {
            LastMessage = node is null
                ? "未找到 node.exe，请先在 sync-cue-v1 目录运行 npm run server。"
                : "未找到房间服务脚本（server/index.js）。";
            return false;
        }

        var serverDir = Path.GetDirectoryName(serverEntry)!;
        var appRoot = Directory.GetParent(serverDir)?.FullName ?? serverDir;
        var psi = new ProcessStartInfo
        {
            FileName = node,
            Arguments = $"\"{serverEntry}\"",
            WorkingDirectory = appRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["PORT"] = port.ToString();
        psi.Environment["HOST"] = "0.0.0.0";

        try
        {
            var proc = Process.Start(psi);
            if (proc is null)
            {
                LastMessage = "无法启动房间服务进程。";
                return false;
            }
            lock (_gate)
            {
                _process = proc;
                Owned = true;
            }
        }
        catch (Exception ex)
        {
            LastMessage = "启动房间服务失败：" + ex.Message;
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsPortOpenAsync(port, ct).ConfigureAwait(false))
            {
                LastMessage = $"已自动拉起本机房间服务（{port}）。";
                return true;
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        LastMessage = "房间服务已启动但端口尚未就绪，请稍后再连。";
        return await IsPortOpenAsync(port, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Process? proc;
        lock (_gate)
        {
            proc = _process;
            _process = null;
            Owned = false;
        }
        if (proc is null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch { /* ignore */ }
        finally
        {
            try { proc.Dispose(); } catch { /* ignore */ }
        }
    }

    public static async Task<bool> IsPortOpenAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(800);
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static string? ResolveNode()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "node", "node.exe"),
            Path.Combine(AppContext.BaseDirectory, "node.exe"),
            @"C:\Program Files\nodejs\node.exe",
            @"C:\Program Files (x86)\nodejs\node.exe"
        };
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim('"'), "node.exe"));

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (File.Exists(c)) return c; } catch { /* ignore */ }
        }
        return null;
    }

    public static string? ResolveServerEntry()
    {
        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "server", "index.js"));
        candidates.Add(Path.Combine(baseDir, "app", "server", "index.js"));

        var dig = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dig is not null; i++, dig = dig.Parent)
        {
            candidates.Add(Path.Combine(dig.FullName, "server", "index.js"));
            candidates.Add(Path.Combine(dig.FullName, "sync-cue-v1", "server", "index.js"));
        }
        candidates.Add(@"D:\Working\SyncCue\sync-cue-v1\server\index.js");

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (File.Exists(c)) return c; } catch { /* ignore */ }
        }
        return null;
    }
}
