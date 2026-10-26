using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Nexus.Services;

/// <summary>
/// Things outside Nexus that also react to the Guide button, plus "start with Windows".
/// Nothing here changes a setting on its own: the More settings page calls the setters when the
/// user presses the matching button.
/// </summary>
public static class SetupChecks
{
    const string GameBarKey = @"Software\Microsoft\GameBar";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Nexus";

    /// <summary>
    /// Windows opens Game Bar on Guide: Settings > Gaming > Game Bar > "Allow your controller to open
    /// Game Bar". Missing value = on. Read-only here: changing the value directly doesn't notify a
    /// running Game Bar, so More settings opens that Settings page instead.
    /// </summary>
    public static bool GameBarUsesGuide
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(GameBarKey);
            return (k?.GetValue("UseNexusForGameBarEnabled") as int? ?? 1) != 0;
        }
    }

    /// <summary>
    /// Steam's "Guide button focuses Steam" setting, read from localconfig.vdf
    /// (Controller_CheckGuideButton). Null when Steam or the value can't be found.
    /// </summary>
    public static bool? SteamUsesGuide()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is not string steam) return null;
            var userdata = Path.Combine(steam.Replace('/', '\\'), "userdata");
            if (!Directory.Exists(userdata)) return null;
            foreach (var cfg in Directory.GetDirectories(userdata)
                         .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
                         .Where(File.Exists)
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var m = Regex.Match(File.ReadAllText(cfg), "\"Controller_CheckGuideButton\"\\s+\"(\\d)\"");
                if (m.Success) return m.Groups[1].Value != "0";
            }
        }
        catch (Exception ex) { Log.Error("Steam check failed", ex); }
        return null;
    }

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --background");
            else k.DeleteValue(RunName, throwOnMissingValue: false);
            Log.Info($"start with Windows {(value ? "on" : "off")}");
        }
    }
}
