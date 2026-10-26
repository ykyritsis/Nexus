using Microsoft.Win32;
using Nexus.Interop;

namespace Nexus.Providers;

/// <summary>
/// Game Bar's capture engine (it records even with Game Bar's own overlay out of the way) and the
/// Captures folder it saves into.
/// </summary>
public static class Captures
{
    const byte VK_G = 0x47, VK_R = 0x52, VK_SNAPSHOT = 0x2C;

    public static void Screenshot() => Native.PressChord(Native.VK_LWIN, Native.VK_MENU, VK_SNAPSHOT);
    public static void RecordThat() => Native.PressChord(Native.VK_LWIN, Native.VK_MENU, VK_G);
    public static void ToggleRecording() => Native.PressChord(Native.VK_LWIN, Native.VK_MENU, VK_R);

    /// <summary>Whether "Record what happened" (background recording) is on in Settings > Gaming > Captures.</summary>
    public static bool BackgroundRecording
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
            return (k?.GetValue("HistoricalCaptureEnabled") as int? ?? 0) != 0;
        }
    }

    /// <summary>Where screenshots and clips go (Videos\Captures unless moved).</summary>
    public static string Folder
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (k?.GetValue("{EDC0FE71-98D8-4F4A-B920-C8DC133CB165}") is string p) return Environment.ExpandEnvironmentVariables(p);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");
        }
    }

    public static List<FileInfo> Latest(int count)
    {
        var dir = new DirectoryInfo(Folder);
        if (!dir.Exists) return new();
        return dir.EnumerateFiles()
            .Where(f => f.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jxr" or ".mp4")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(count).ToList();
    }

    /// <summary>
    /// Latest captures of one game. Game Bar names files after the window title ("Game Name
    /// 2026-10-27 21-04-11.png"), so match on the name; anything captured since the game started
    /// counts too, because the game was in front.
    /// </summary>
    public static List<FileInfo> LatestFor(IEnumerable<string?> names, DateTime? since, int count)
    {
        var keys = names.Select(Squash).Where(k => k.Length >= 3).Distinct().ToList();
        return Latest(200)
            .Where(f => (since is { } s && f.LastWriteTime >= s) || keys.Any(k => Squash(f.Name).StartsWith(k)))
            .Take(count).ToList();
    }

    /// <summary>Letters and digits only, lower case: "Call of Duty®: WWII" and "Call of Duty WWII" match.</summary>
    static string Squash(string? s) => s is null ? "" : string.Concat(s.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
