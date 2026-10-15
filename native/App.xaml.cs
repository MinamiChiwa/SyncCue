using System.Windows;
using SyncCue.Services;

namespace SyncCue;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        // Belt-and-suspenders: closing the Job Object kills any child we assigned
        // (node room-server, cloudflared) even if MainWindow cleanup was skipped.
        try { ChildProcessJob.Shared.Dispose(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
