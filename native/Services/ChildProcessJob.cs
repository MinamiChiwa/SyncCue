using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SyncCue.Services;

/// <summary>
/// Windows Job Object that kills every assigned child when the job handle is closed
/// (app exit, crash, or explicit Dispose). Nested-job failures are ignored — callers
/// still Kill(entireProcessTree) on shutdown.
/// </summary>
public sealed class ChildProcessJob : IDisposable
{
    private static readonly Lazy<ChildProcessJob> SharedLazy = new(() => new ChildProcessJob());
    public static ChildProcessJob Shared => SharedLazy.Value;

    private IntPtr _handle;
    private readonly object _gate = new();
    private bool _disposed;

    private ChildProcessJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (_handle == IntPtr.Zero)
            return;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ok = SetInformationJobObject(
            _handle,
            JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
            ref info,
            (uint)len);
        if (!ok)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>Assign a child we started. Safe to call if job unavailable.</summary>
    public void Assign(Process? process)
    {
        if (process is null) return;
        IntPtr handle;
        lock (_gate)
        {
            if (_disposed || _handle == IntPtr.Zero) return;
            handle = _handle;
        }
        try
        {
            if (process.HasExited) return;
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                // Often: already in a non-nested job. Ignore — Kill on exit still applies.
            }
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>Close the job handle → OS kills all remaining assigned children.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
        GC.SuppressFinalize(this);
    }

    ~ChildProcessJob()
    {
        try { Dispose(); } catch { /* ignore */ }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    private enum JOBOBJECTINFOCLASS
    {
        JobObjectExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JOBOBJECTINFOCLASS infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
