namespace Nexus.Services;

/// <summary>
/// One running copy. Later launches signal it through named events:
///   Nexus.exe              -> toggle the overlay (or start Nexus if it isn't running)
///   Nexus.exe --toggle     -> same
///   Nexus.exe --background -> start hidden, do nothing if already running (used at login)
///   Nexus.exe --quit       -> close the running copy
/// </summary>
public static class SingleInstance
{
    const string MutexName = @"Local\Nexus.Primary";
    const string ToggleName = @"Local\Nexus.Toggle";
    const string QuitName = @"Local\Nexus.Quit";

    static Mutex? _mutex;
    static EventWaitHandle? _toggle, _quit;

    public static bool TryBecomePrimary(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        if (created)
        {
            _toggle = new EventWaitHandle(false, EventResetMode.AutoReset, ToggleName);
            _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
            return true;
        }

        bool quit = args.Contains("--quit", StringComparer.OrdinalIgnoreCase);
        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (!background && EventWaitHandle.TryOpenExisting(quit ? QuitName : ToggleName, out var ev))
            ev.Set();
        return false;
    }

    /// <summary>Calls back (on a worker thread) whenever another launch asks to toggle or quit.</summary>
    public static void Listen(Action onToggle, Action onQuit)
    {
        if (_toggle is null || _quit is null) return;
        var t = new Thread(() =>
        {
            var handles = new WaitHandle[] { _toggle, _quit };
            while (true)
            {
                int i = WaitHandle.WaitAny(handles);
                if (i == 0) onToggle();
                else { onQuit(); return; }
            }
        }) { IsBackground = true, Name = "Nexus single instance" };
        t.Start();
    }
}
