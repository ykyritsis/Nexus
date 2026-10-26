using Nexus.Interop;

namespace Nexus.Services;

/// <summary>
/// Listens on the overlay window for messages WinUI doesn't surface: the keyboard shortcut
/// (Win+Shift+G by default) and the tray icon.
/// </summary>
internal sealed class WindowHooks : IDisposable
{
    const int HotkeyId = 0x4E58; // "NX"
    readonly IntPtr _hWnd;
    readonly Native.SubclassProc _proc; // kept alive: the native side holds a pointer to it
    TrayIcon? _tray;

    public event Action? HotkeyPressed;

    public WindowHooks(IntPtr hWnd)
    {
        _hWnd = hWnd;
        _proc = WndProc;
        Native.SetWindowSubclass(hWnd, _proc, (UIntPtr)1, UIntPtr.Zero);
        if (!Native.RegisterHotKey(hWnd, HotkeyId, Native.MOD_WIN | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 0x47 /*G*/))
            Log.Warn("Win+Shift+G is taken by another app; the keyboard shortcut is off");
    }

    public void AttachTray(TrayIcon tray) => _tray = tray;

    IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            return IntPtr.Zero;
        }
        if (_tray?.HandleMessage(msg, lParam) == true)
            return IntPtr.Zero;
        return Native.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Native.UnregisterHotKey(_hWnd, HotkeyId);
        _tray?.Dispose();
    }
}
