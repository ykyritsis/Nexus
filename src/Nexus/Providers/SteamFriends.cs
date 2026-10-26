using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nexus.Services;

namespace Nexus.Providers;

public enum Presence { Offline, Online, InGame }

public sealed record Friend(string SteamId64, string Name, string? AvatarUrl, Presence Presence, string Status, string? Game);

/// <summary>
/// Steam friends without an API key. The list (names, avatar hashes) comes from Steam's local
/// config; online and in-game status comes from each friend's public profile XML. Status is
/// cached for a minute and only fetched while the Social tab is open.
/// </summary>
public static class SteamFriends
{
    const long SteamId64Base = 76561197960265728;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    static readonly Dictionary<string, (DateTime at, Presence presence, string status, string? game)> StatusCache = new();
    static readonly SemaphoreSlim Throttle = new(6);

    /// <summary>Friends from the most recently used Steam account on this PC (not including you).</summary>
    public static List<(string id64, string name, string? avatar)> List()
    {
        var result = new List<(string, string, string?)>();
        try
        {
            var steam = GameLibrary.SteamPath();
            if (steam is null) return result;
            var userdata = Path.Combine(steam, "userdata");
            var me = Directory.GetDirectories(userdata)
                .Where(d => File.Exists(Path.Combine(d, "config", "localconfig.vdf")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "config", "localconfig.vdf")))
                .FirstOrDefault();
            if (me is null) return result;
            var myId = Path.GetFileName(me);
            var text = File.ReadAllText(Path.Combine(me, "config", "localconfig.vdf"));
            int start = text.IndexOf("\"friends\"", StringComparison.Ordinal);
            if (start < 0) return result;
            var block = Block(text, text.IndexOf('{', start));
            // Each friend: "accountid" { ... "name" "x" ... "avatar" "hash" ... }
            foreach (Match m in Regex.Matches(block, "\"(\\d{3,12})\"\\s*\\{"))
            {
                var id = m.Groups[1].Value;
                if (id == myId) continue;
                var body = Block(block, m.Index + m.Length - 1);
                var name = Regex.Match(body, "\"name\"\\s+\"([^\"]*)\"").Groups[1].Value;
                var hash = Regex.Match(body, "\"avatar\"\\s+\"([0-9a-f]{40})\"").Groups[1].Value;
                if (name == "") continue;
                var id64 = (SteamId64Base + long.Parse(id)).ToString();
                result.Add((id64, name, hash == "" ? null : $"https://avatars.steamstatic.com/{hash}_medium.jpg"));
            }
        }
        catch (Exception ex) { Log.Error("reading Steam friends", ex); }
        return result;
    }

    /// <summary>Text from an opening brace to its matching closing brace.</summary>
    static string Block(string text, int open)
    {
        if (open < 0) return "";
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text.Substring(open, i - open + 1);
        }
        return text[open..];
    }

    /// <summary>Friends with their current status, in-game first, then online, then offline.</summary>
    public static async Task<List<Friend>> WithStatusAsync(int max = 40)
    {
        var people = await Task.Run(List);
        var tasks = people.Take(max).Select(async p =>
        {
            var (presence, status, game) = await StatusAsync(p.id64);
            return new Friend(p.id64, p.name, p.avatar, presence, status, game);
        });
        var friends = (await Task.WhenAll(tasks)).ToList();
        return friends.OrderByDescending(f => f.Presence)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static async Task<(Presence, string, string?)> StatusAsync(string id64)
    {
        lock (StatusCache)
            if (StatusCache.TryGetValue(id64, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(60))
                return (c.presence, c.status, c.game);

        await Throttle.WaitAsync();
        try
        {
            var xml = XDocument.Parse(await Http.GetStringAsync($"https://steamcommunity.com/profiles/{id64}/?xml=1"));
            var root = xml.Root;
            var state = root?.Element("onlineState")?.Value ?? "offline";
            var message = Regex.Replace(root?.Element("stateMessage")?.Value ?? "", "<[^>]+>", " ").Trim();
            Presence presence = state switch { "in-game" => Presence.InGame, "online" => Presence.Online, _ => Presence.Offline };
            string? game = presence == Presence.InGame ? Regex.Replace(message, "^In-Game\\s*", "").Trim() : null;
            string status = presence switch
            {
                Presence.InGame => string.IsNullOrEmpty(game) ? "In game" : game!,
                Presence.Online => "Online",
                _ => message.StartsWith("Last Online") ? message.Replace("Last Online", "Last online") : "Offline",
            };
            lock (StatusCache) StatusCache[id64] = (DateTime.UtcNow, presence, status, game);
            return (presence, status, game);
        }
        catch
        {
            return (Presence.Offline, "Offline", null);
        }
        finally { Throttle.Release(); }
    }

    public static void Chat(string id64) => Launch.Open($"steam://friends/message/{id64}");
    public static void Profile(string id64) => Launch.Open($"steam://url/SteamIDPage/{id64}");
    public static void OpenFriends() => Launch.Open("steam://open/friends");
}
