using System.Runtime.InteropServices;

namespace Nexus.Interop;

/// <summary>
/// Notification area icon with a small menu. Messages arrive on the overlay window, which
/// <see cref="Services.WindowHooks"/> subclasses and forwards here.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    public const uint CallbackMessage = Native.WM_APP + 1;
    const int IconId = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public int uID; public int uFlags; public int uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.POINT pt);

    const int NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    const uint MF_STRING = 0, MF_SEPARATOR = 0x800, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;

    readonly IntPtr _hWnd;
    readonly (string text, Action action)[] _items;
    readonly IntPtr _icon;
    readonly string _tip;
    bool _added;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    // Sent to every top-level window when Explorer (and so the taskbar) restarts: the icon must be added again.
    static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    public event Action? Clicked;

    public TrayIcon(IntPtr hWnd, string iconPath, string tip, params (string text, Action action)[] menu)
    {
        _hWnd = hWnd;
        _items = menu;
        _icon = LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, 32, 32, 0x10 /*LR_LOADFROMFILE*/);
        _tip = tip;
        Add();
    }

    void Add()
    {
        var data = Data();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = (int)CallbackMessage;
        data.hIcon = _icon;
        data.szTip = _tip;
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hWnd, uID = IconId,
        szTip = "", szInfo = "", szInfoTitle = ""
    };

    /// <summary>Handles the tray callback message; returns true if it was ours.</summary>
    public bool HandleMessage(uint msg, IntPtr lParam)
    {
        if (msg == TaskbarCreated && TaskbarCreated != 0)
        {
            Add();
            return false; // let others see it too
        }
        if (msg != CallbackMessage) return false;
        uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
        if (ev == WM_LBUTTONUP) Clicked?.Invoke();
        else if (ev == WM_RBUTTONUP) ShowMenu();
        return true;
    }

    void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        for (int i = 0; i < _items.Length; i++)
        {
            if (_items[i].text == "-") AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            else AppendMenu(menu, MF_STRING, (UIntPtr)(i + 1), _items[i].text);
        }
        GetCursorPos(out var pt);
        Native.SetForegroundWindow(_hWnd); // so the menu closes when clicking elsewhere
        int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hWnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd > 0) _items[cmd - 1].action();
    }

    public void Dispose()
    {
        if (!_added) return;
        var data = Data();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }
}
