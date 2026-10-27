using System.Text.Json.Nodes;

namespace Nexus.Providers;

/// <summary>
/// How many people are playing a Steam game right now, from Steam's public
/// GetNumberOfCurrentPlayers endpoint (no key, no sign-in). Cached for five minutes per game.
/// </summary>
public static class SteamPlayers
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    static readonly Dictionary<string, (DateTime at, int? count)> Cache = new();

    public static async Task<int?> CurrentAsync(string appId)
    {
        lock (Cache)
            if (Cache.TryGetValue(appId, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(5)) return c.count;
        int? count = null;
        try
        {
            var json = JsonNode.Parse(await Http.GetStringAsync($"https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid={appId}"));
            if ((int?)json?["response"]?["result"] == 1) count = (int?)json?["response"]?["player_count"];
        }
        catch { }
        lock (Cache) Cache[appId] = (DateTime.UtcNow, count);
        return count;
    }

    /// <summary>"12.3K playing now" style.</summary>
    public static string Format(int n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 10_000 ? $"{n / 1000.0:0}K" : n >= 1000 ? $"{n / 1000.0:0.#}K" : n.ToString();
}
