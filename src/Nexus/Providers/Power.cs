using System.Diagnostics;
using System.Runtime.InteropServices;
using Nexus.Services;

namespace Nexus.Providers;

/// <summary>Sleep, restart, shut down, lock and sign out.</summary>
public static class Power
{
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    [DllImport("user32.dll")] static extern bool LockWorkStation();

    public static void Sleep() { Log.Info("power: sleep"); SetSuspendState(false, false, false); }
    public static void Lock() { Log.Info("power: lock"); LockWorkStation(); }
    public static void Restart() => Shutdown("/r /t 0");
    public static void ShutDown() => Shutdown("/s /hybrid /t 0"); // hybrid = Fast Startup, like the Start menu
    public static void SignOut() => Shutdown("/l");

    static void Shutdown(string args)
    {
        Log.Info($"power: shutdown {args}");
        using var _ = Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
    }
}
