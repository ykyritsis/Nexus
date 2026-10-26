using System.Diagnostics;
using Microsoft.Win32;
using Nexus.Interop;

namespace Nexus.Providers;

/// <summary>The game being played right now.</summary>
public sealed record PlayingGame(GameEntry Game, string? AppId, uint ProcessId, IntPtr Window, DateTime? StartedAt, string? ExePath);

/// <summary>
/// Works out what you're playing. Steam says which of its games is running (RunningAppID in the
/// registry). For anything else, the window you were in counts as a game when it fills its screen
/// and isn't a desktop app like a browser or Explorer.
/// </summary>
public static class NowPlaying
{
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "code", "devenv", "Nexus",
        "steamwebhelper", "steam", "Discord", "Spotify", "vlc", "mpc-hc64", "PotPlayerMini64", "Photos", "claude",
        "ApplicationFrameHost", "SystemSettings", "XboxPcApp", "TextInputHost", "LockApp",
    };

    public static PlayingGame? Detect(IntPtr previousForeground)
    {
        return FromSteam(previousForeground) ?? FromWindow(previousForeground);
    }

    static PlayingGame? FromSteam(IntPtr previousForeground)
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (k?.GetValue("RunningAppID") is not int appId || appId <= 0) return null;
        var found = GameLibrary.FindSteam(appId.ToString());
        if (found is null) return null;
        var (game, installDir) = found.Value;

        // The game's own process: the earliest one running from its install folder. Remembered while
        // it runs, so reopening the overlay doesn't scan every process again.
        Process? best = null;
        if (_cached is { } c && c.appId == appId && IsAlive(c.pid)) best = Process.GetProcessById(c.pid);
        else
        {
            foreach (var p in Process.GetProcesses())
            {
                var path = Native.ProcessPath((uint)p.Id);
                if (path != null && path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase)
                    && (best is null || SafeStart(p) < SafeStart(best)))
                    best = p;
            }
            _cached = best is null ? null : (appId, best.Id);
        }
        uint pid = (uint)(best?.Id ?? 0);
        var window = WindowOf(pid, previousForeground);
        var started = best is null ? null : SafeStart(best) is var s && s != DateTime.MaxValue ? s : (DateTime?)null;
        return new PlayingGame(game, appId.ToString(), pid, window, started, best is null ? null : Native.ProcessPath(pid));
    }

    static PlayingGame? FromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return null;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        var path = Native.ProcessPath(pid);
        if (path is null || NotGames.Contains(Path.GetFileNameWithoutExtension(path))) return null;
        if (!FillsScreen(hwnd)) return null;

        string name;
        try { name = FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d ? d : Path.GetFileNameWithoutExtension(path); }
        catch { name = Path.GetFileNameWithoutExtension(path); }
        DateTime? started = null;
        try { started = Process.GetProcessById((int)pid).StartTime; } catch { }
        var game = new GameEntry($"exe:{Path.GetFileNameWithoutExtension(path).ToLowerInvariant()}", name, "PC", path,
            new GameArt(path, null, null, null), DateTime.Now);
        return new PlayingGame(game, null, pid, hwnd, started, path);
    }

    /// <summary>Fullscreen or borderless: the window covers its whole monitor.</summary>
    static bool FillsScreen(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var (m, _) = Native.MonitorOf(hwnd);
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>The game's main window: the one you were in if it belongs to the game, else its largest.</summary>
    static IntPtr WindowOf(uint pid, IntPtr previous)
    {
        if (pid == 0) return IntPtr.Zero;
        if (previous != IntPtr.Zero)
        {
            Native.GetWindowThreadProcessId(previous, out uint p);
            if (p == pid) return previous;
        }
        return OpenWindows.List().FirstOrDefault(w => w.ProcessId == pid)?.Handle ?? IntPtr.Zero;
    }

    static (int appId, int pid)? _cached;

    static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; } catch { return false; }
    }

    static DateTime SafeStart(Process p)
    {
        try { return p.StartTime; } catch { return DateTime.MaxValue; }
    }

    public static string Duration(TimeSpan t) =>
        t.TotalMinutes < 1 ? "just started" :
        t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min" :
        $"{(int)t.TotalHours} h {t.Minutes} min";
}
