using System.Runtime.InteropServices;
using System.Text;
using Nexus.Interop;
using Nexus.Services;

namespace Nexus.Providers;

public sealed record OpenWindow(IntPtr Handle, string Title, uint ProcessId, string? ExePath);

/// <summary>
/// The app windows Alt+Tab would show, plus live DWM thumbnails of them and switch/close actions.
/// </summary>
public static class OpenWindows
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);

    const uint GW_OWNER = 4, WM_CLOSE = 0x0010;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    static readonly HashSet<string> ShellClasses = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow" };

    public static List<OpenWindow> List()
    {
        var list = new List<OpenWindow>();
        uint me = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            long ex = GetLongPtr(h, -20).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_NOACTIVATE) != 0) return true;
            if (DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            int len = GetWindowTextLength(h);
            if (len == 0 || ShellClasses.Contains(Native.ClassOf(h))) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == me) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            list.Add(new OpenWindow(h, sb.ToString(), pid, Native.ProcessPath(pid)));
            return true;
        }, IntPtr.Zero);
        return list; // already in Z-order: most recently used first
    }

    public static void SwitchTo(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9 /*SW_RESTORE*/);
        Native.SetForegroundWindow(h);
    }

    /// <summary>Asks the app to close, as clicking its X would.</summary>
    public static void Close(IntPtr h) => PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Ends the app's process without asking it (for frozen games).</summary>
    public static bool ForceClose(uint pid)
    {
        IntPtr p = Native.OpenProcess(0x0001 /*PROCESS_TERMINATE*/, false, pid);
        if (p == IntPtr.Zero) return false;
        try { return TerminateProcess(p, 1); }
        finally { Native.CloseHandle(p); Log.Info($"force-closed process {pid}"); }
    }

    // ---------------- live thumbnails ----------------

    [StructLayout(LayoutKind.Sequential)]
    struct ThumbProps
    {
        public uint dwFlags;
        public Native.RECT rcDestination, rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible, fSourceClientAreaOnly;
    }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbProps props);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);

    const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    public static IntPtr Register(IntPtr dest, IntPtr src) =>
        DwmRegisterThumbnail(dest, src, out var t) == 0 ? t : IntPtr.Zero;

    public static void Unregister(IntPtr thumb)
    {
        if (thumb != IntPtr.Zero) DwmUnregisterThumbnail(thumb);
    }

    /// <summary>Places a thumbnail inside a box (physical pixels), keeping the window's shape.</summary>
    public static void Place(IntPtr thumb, int left, int top, int width, int height, bool visible)
    {
        if (thumb == IntPtr.Zero) return;
        var props = new ThumbProps
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
            opacity = 255, fVisible = visible, fSourceClientAreaOnly = true,
        };
        if (visible && DwmQueryThumbnailSourceSize(thumb, out var src) == 0 && src.cx > 0 && src.cy > 0)
        {
            double s = Math.Min(width / (double)src.cx, height / (double)src.cy);
            int w = (int)(src.cx * s), h = (int)(src.cy * s);
            int x = left + (width - w) / 2, y = top + (height - h) / 2;
            props.rcDestination = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        }
        DwmUpdateThumbnailProperties(thumb, ref props);
    }
}
