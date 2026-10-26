using Microsoft.Win32;
using Nexus.Services;

namespace Nexus.Providers;

public sealed record LauncherEntry(string Name, string Target, string IconSource);

/// <summary>Game launchers that are installed on this PC, in the Game Bar's order.</summary>
public static class Launchers
{
    public static List<LauncherEntry> Installed()
    {
        var list = new List<LauncherEntry>();
        var xboxLogo = XboxLogo();
        list.Add(new("Xbox", Launch.XboxApp, xboxLogo ?? ""));

        if (GameLibrary.SteamPath() is { } steam && File.Exists(Path.Combine(steam, "steam.exe")))
            list.Add(new("Steam", "steam://open/main", Path.Combine(steam, "steam.exe")));

        AddIfExists(list, "EA", @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe");
        AddIfExists(list, "Battle.net", @"C:\Program Files (x86)\Battle.net\Battle.net.exe");
        AddIfExists(list, "Ubisoft Connect", UbisoftPath());
        AddIfExists(list, "Epic Games", @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe");
        AddIfExists(list, "GOG Galaxy", @"C:\Program Files (x86)\GOG Galaxy\GalaxyClient.exe");
        AddIfExists(list, "Rockstar Games", @"C:\Program Files\Rockstar Games\Launcher\Launcher.exe");
        return list;
    }

    static void AddIfExists(List<LauncherEntry> list, string name, string? exe)
    {
        if (exe != null && File.Exists(exe)) list.Add(new(name, exe, exe));
    }

    static string? UbisoftPath()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher");
        if (k?.GetValue("InstallDir") is string dir)
            return Path.Combine(dir, "UbisoftConnect.exe");
        return @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\UbisoftConnect.exe";
    }

    /// <summary>The Xbox app's own logo from its package, if Windows will tell us where it is.</summary>
    static string? XboxLogo()
    {
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            var pkg = pm.FindPackagesForUser("", "Microsoft.GamingApp_8wekyb3d8bbwe").FirstOrDefault();
            var logo = pkg?.Logo?.LocalPath;
            if (logo == null) return null;
            // The manifest points at Logo.png, but the files on disk are scale variants.
            var dir = Path.GetDirectoryName(logo)!;
            var stem = Path.GetFileNameWithoutExtension(logo);
            if (File.Exists(logo)) return logo;
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, stem + ".scale-*.png").OrderByDescending(f => f).FirstOrDefault()
                : null;
        }
        catch (Exception ex)
        {
            Log.Error("Xbox app logo lookup failed", ex);
            return null;
        }
    }
}
