namespace Nexus.Providers;

public sealed record Achievement(string Name, string Description, string IconUrl, bool Unlocked, DateTime? UnlockedAt, bool Hidden);

/// <summary>
/// Steam achievements read from Steam's own local cache, no login or API key:
/// appcache\stats\UserGameStatsSchema_{app}.bin has names, descriptions and icons;
/// UserGameStats_{account}_{app}.bin has a bitfield per stat block plus unlock times.
/// </summary>
public static class SteamAchievements
{
    public static List<Achievement> Load(string appId)
    {
        var list = new List<Achievement>();
        var steam = GameLibrary.SteamPath();
        var account = SteamAccount.CurrentId();
        if (steam is null || account is null) return list;
        var dir = Path.Combine(steam, "appcache", "stats");
        var schema = BinaryKv.Load(Path.Combine(dir, $"UserGameStatsSchema_{appId}.bin"))?[appId]?["stats"];
        if (schema is null) return list;
        var user = BinaryKv.Load(Path.Combine(dir, $"UserGameStats_{account}_{appId}.bin"))?["cache"];

        foreach (var (statId, stat) in schema.Children)
        {
            var bits = stat["bits"];
            if (bits is null) continue; // a plain stat, not an achievement block
            int data = user?[statId]?.Int("data") ?? 0;
            var times = user?[statId]?["AchievementTimes"];
            foreach (var (bitKey, bit) in bits.Children)
            {
                if (!int.TryParse(bitKey, out int b)) continue;
                var display = bit["display"];
                string name = display?["name"]?.Str("english") ?? bit.Str("name") ?? "Achievement";
                string desc = display?["desc"]?.Str("english") ?? "";
                bool unlocked = ((data >> b) & 1) == 1;
                string? icon = unlocked ? display?.Str("icon") : display?.Str("icon_gray") ?? display?.Str("icon");
                DateTime? at = times?.Int(bitKey) is int t && t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime : null;
                list.Add(new Achievement(name, desc,
                    icon is null ? "" : $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{icon}",
                    unlocked, at, display?.Int("hidden") == 1));
            }
        }
        return list;
    }
}

/// <summary>The Steam account used most recently on this PC (its userdata folder name).</summary>
public static class SteamAccount
{
    public static string? CurrentId()
    {
        var steam = GameLibrary.SteamPath();
        if (steam is null) return null;
        var userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata)) return null;
        return Directory.GetDirectories(userdata)
            .Where(d => File.Exists(Path.Combine(d, "config", "localconfig.vdf")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "config", "localconfig.vdf")))
            .Select(Path.GetFileName).FirstOrDefault();
    }

    /// <summary>Total playtime in minutes Steam has recorded for a game (localconfig.vdf).</summary>
    public static int? PlaytimeMinutes(string appId)
    {
        try
        {
            var steam = GameLibrary.SteamPath();
            var id = CurrentId();
            if (steam is null || id is null) return null;
            var text = File.ReadAllText(Path.Combine(steam, "userdata", id, "config", "localconfig.vdf"));
            int apps = text.IndexOf("\"apps\"", StringComparison.Ordinal);
            while (apps >= 0)
            {
                int key = text.IndexOf($"\"{appId}\"", apps, StringComparison.Ordinal);
                if (key < 0) return null;
                int open = text.IndexOf('{', key);
                int between = text.IndexOf('"', key + appId.Length + 2);
                if (open > 0 && (between < 0 || open < between)) // it's a block, not a value
                {
                    var block = Block(text, open);
                    var m = System.Text.RegularExpressions.Regex.Match(block, "\"Playtime\"\\s+\"(\\d+)\"");
                    if (m.Success) return int.Parse(m.Groups[1].Value);
                }
                apps = key + 1;
            }
        }
        catch { }
        return null;
    }

    static string Block(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text.Substring(open, i - open + 1);
        }
        return text[open..];
    }
}
