using System.Runtime.InteropServices;
using System.Text;

namespace SyncCue.Services;

/// <summary>
/// Fail-fast Win32 clipboard write. Unlike WPF Clipboard.SetDataObject,
/// OpenClipboard returns immediately when another app holds the lock
/// (no multi-second UI freeze).
/// </summary>
internal static class NativeClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>One attempt. Returns false immediately if clipboard is locked.</summary>
    public static bool TrySetText(string text, out int win32Error)
    {
        win32Error = 0;
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)(uint)bytes.Length);
        if (hGlobal == IntPtr.Zero)
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        var locked = GlobalLock(hGlobal);
        if (locked == IntPtr.Zero)
        {
            win32Error = Marshal.GetLastWin32Error();
            GlobalFree(hGlobal);
            return false;
        }

        try
        {
            Marshal.Copy(bytes, 0, locked, bytes.Length);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            win32Error = Marshal.GetLastWin32Error();
            GlobalFree(hGlobal);
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                win32Error = Marshal.GetLastWin32Error();
                GlobalFree(hGlobal);
                return false;
            }

            if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
            {
                win32Error = Marshal.GetLastWin32Error();
                GlobalFree(hGlobal);
                return false;
            }

            // System owns hGlobal now.
            hGlobal = IntPtr.Zero;
            return true;
        }
        finally
        {
            CloseClipboard();
            if (hGlobal != IntPtr.Zero)
                GlobalFree(hGlobal);
        }
    }
}
