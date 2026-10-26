using System.Text.RegularExpressions;
using Microsoft.Win32;
using Nexus.Services;

namespace Nexus.Providers;

public sealed record GameArt(string? Icon, string? Portrait, string? Hero, string? Logo);

/// <param name="Key">Stable id for caches, e.g. "steam:1985820".</param>
public sealed record GameEntry(string Key, string Name, string Source, string LaunchTarget, GameArt Art, DateTime LastPlayed);

/// <summary>
/// Recently played games. Steam keeps a LastPlayed time in each appmanifest and its art in
/// appcache\librarycache, so it's the first source. Battle.net, EA and Ubisoft don't store a
/// last-played time anywhere readable, so they're only reachable from the launcher row for now.
/// </summary>
public static class GameLibrary
{
    // Tools that show up as "apps" in Steam but aren't games.
    static readonly HashSet<string> NotGames = new()
    {
        "228980", "1070560", "1391110", "1628350", "250820", "1493710", "1826330", "2180100",
    };

    public static string? SteamPath()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return (k?.GetValue("SteamPath") as string)?.Replace('/', '\\');
    }

    public static List<GameEntry> Recent(int count)
    {
        var games = new List<GameEntry>();
        try
        {
            var steam = SteamPath();
            if (steam is null) return games;
            foreach (var lib in SteamLibraries(steam))
            {
                var apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    var text = File.ReadAllText(acf);
                    string id = Field(text, "appid"), name = Field(text, "name");
                    if (id == "" || name == "" || NotGames.Contains(id)) continue;
                    if (!long.TryParse(Field(text, "LastPlayed"), out long played) || played <= 0) continue;
                    games.Add(new GameEntry($"steam:{id}", name, "Steam", $"steam://rungameid/{id}", SteamArt(steam, id),
                        DateTimeOffset.FromUnixTimeSeconds(played).LocalDateTime));
                }
            }
        }
        catch (Exception ex) { Log.Error("reading Steam library failed", ex); }
        return games.OrderByDescending(g => g.LastPlayed).Take(count).ToList();
    }

    /// <summary>One installed Steam game and its install folder, or null.</summary>
    public static (GameEntry game, string installDir)? FindSteam(string appId)
    {
        try
        {
            var steam = SteamPath();
            if (steam is null) return null;
            foreach (var lib in SteamLibraries(steam))
            {
                var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;
                var text = File.ReadAllText(acf);
                long.TryParse(Field(text, "LastPlayed"), out long played);
                var game = new GameEntry($"steam:{appId}", Field(text, "name"), "Steam", $"steam://rungameid/{appId}",
                    SteamArt(steam, appId), DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, played)).LocalDateTime);
                return (game, Path.Combine(lib, "steamapps", "common", Field(text, "installdir")));
            }
        }
        catch (Exception ex) { Log.Error($"finding Steam app {appId}", ex); }
        return null;
    }

    static IEnumerable<string> SteamLibraries(string steam)
    {
        var vdf = Path.Combine(steam, "config", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return new[] { steam };
        return Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))
            .Append(steam)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    static string Field(string acf, string key)
    {
        var m = Regex.Match(acf, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>
    /// Steam's local art cache. librarycache\{id}\ holds the community icon (a 40-hex-digit .jpg),
    /// the 2:3 cover, the wide hero and the transparent logo; older Steam builds used flat
    /// {id}_icon.jpg / {id}_library_600x900.jpg files.
    /// </summary>
    static GameArt SteamArt(string steam, string id)
    {
        var cache = Path.Combine(steam, "appcache", "librarycache");
        var dir = Path.Combine(cache, id);
        // Newer Steam builds put each picture in its own hashed subfolder: {id}\{hash}\library_600x900.jpg
        var folders = Directory.Exists(dir) ? Directory.GetDirectories(dir).Prepend(dir).ToArray() : Array.Empty<string>();
        string? Pick(params string[] names)
        {
            foreach (var n in names)
            {
                foreach (var f in folders)
                    if (File.Exists(Path.Combine(f, n))) return Path.Combine(f, n);
                if (File.Exists(Path.Combine(cache, $"{id}_{n}"))) return Path.Combine(cache, $"{id}_{n}");
            }
            return null;
        }
        string? icon = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.jpg").FirstOrDefault(f => Regex.IsMatch(Path.GetFileNameWithoutExtension(f), "^[0-9a-f]{40}$"))
            : null;
        icon ??= Pick("icon.jpg");
        return new GameArt(icon, Pick("library_600x900.jpg"), Pick("library_hero.jpg", "library_header.jpg", "header.jpg"), Pick("logo.png"));
    }

    public static string Ago(DateTime when)
    {
        var d = DateTime.Now - when;
        if (d.TotalMinutes < 60) return "played just now";
        if (d.TotalHours < 24) return $"played {(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "played yesterday";
        if (d.TotalDays < 30) return $"played {(int)d.TotalDays} days ago";
        return $"played {when:d MMM}";
    }
}
