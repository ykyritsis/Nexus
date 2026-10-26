using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Services;

namespace Nexus.Providers;

/// <summary>
/// Square "console" key art, the kind Xbox and PlayStation dashboards show, looked up by title:
/// Xbox Store first, then PlayStation Store, then IGN. Steam's own art stays the fallback.
/// Lookups are ported from OmniDash. Each game is looked up once and its images are kept in
/// %LOCALAPPDATA%\Nexus\artwork, so art never re-downloads; a miss is retried after a week.
/// </summary>
public static class ConsoleArt
{
    public sealed record Found(string Square, string? Hero, string Source);

    sealed class Entry
    {
        public string? Square { get; set; }
        public string? Hero { get; set; }
        public string? Source { get; set; }
        public DateTime CheckedAt { get; set; }
    }

    static readonly HttpClient Http = CreateClient();
    static readonly string Folder = Path.Combine(Log.Folder, "artwork");
    static readonly string IndexPath = Path.Combine(Folder, "artwork.json");
    static readonly SemaphoreSlim Gate = new(1, 1);
    static Dictionary<string, Entry>? _index;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Nexus/0.1");
        return c;
    }

    /// <summary>Cached result without touching the network (null if never found).</summary>
    public static Found? Cached(string key)
    {
        LoadIndex();
        return _index!.TryGetValue(key, out var e) && e.Square is { } sq && File.Exists(sq)
            ? new Found(sq, e.Hero is { } h && File.Exists(h) ? h : null, e.Source ?? "")
            : null;
    }

    // Home's tiles and the Now Playing card can ask for the same game at once; they share one lookup
    // so two downloads never race on the same file (and a failed one can't record a false "miss").
    static readonly Dictionary<string, Task> InFlight = new();

    static Task<T> Once<T>(string key, Func<Task<T>> work)
    {
        lock (InFlight)
        {
            if (InFlight.TryGetValue(key, out var running)) return (Task<T>)running;
            var task = Run();
            InFlight[key] = task;
            return task;
        }
        async Task<T> Run()
        {
            try { return await work(); }
            finally { lock (InFlight) InFlight.Remove(key); }
        }
    }

    /// <summary>Looks the game up if it hasn't been checked recently. Returns the art, or null.</summary>
    public static Task<Found?> ResolveAsync(string key, string title) => Once($"resolve:{key}", () => ResolveCoreAsync(key, title));

    static async Task<Found?> ResolveCoreAsync(string key, string title)
    {
        await Gate.WaitAsync();
        try
        {
            LoadIndex();
            if (_index!.TryGetValue(key, out var e))
            {
                if (e.Square != null && File.Exists(e.Square)) return Cached(key);
                if (DateTime.UtcNow - e.CheckedAt < TimeSpan.FromDays(7)) return null; // known miss
            }
        }
        finally { Gate.Release(); }

        var name = TitleMatch.BaseTitle(title);
        var hit = await Xbox(name) ?? await PlayStation(name) ?? await Ign(name);
        var entry = new Entry { CheckedAt = DateTime.UtcNow };
        if (hit != null)
        {
            var safe = string.Concat(key.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
            entry.Square = await DownloadAsync(hit.Value.square, Path.Combine(Folder, $"{safe}-square.jpg"));
            if (hit.Value.hero != null)
                entry.Hero = await DownloadAsync(hit.Value.hero, Path.Combine(Folder, $"{safe}-hero.jpg"));
            entry.Source = hit.Value.source;
            Log.Info($"art for {title}: {hit.Value.source}");
        }
        else Log.Info($"art for {title}: no console art, using Steam");

        await Gate.WaitAsync();
        try
        {
            _index![key] = entry;
            SaveIndex();
        }
        finally { Gate.Release(); }
        return entry.Square is { } s ? new Found(s, entry.Hero, entry.Source ?? "") : null;
    }

    /// <summary>
    /// Steam's official logo and scenic hero art for a game whose local Steam cache lacks them,
    /// from Steam's image server. Downloaded once; a missing file is remembered for a week.
    /// </summary>
    public static Task<(string? hero, string? logo)> SteamOfficialAsync(string appId) => Once($"steam:{appId}", () => SteamOfficialCoreAsync(appId));

    static async Task<(string? hero, string? logo)> SteamOfficialCoreAsync(string appId)
    {
        if (!appId.All(char.IsDigit)) return (null, null);
        var key = $"steamcdn:{appId}";
        await Gate.WaitAsync();
        try
        {
            LoadIndex();
            if (_index!.TryGetValue(key, out var e) && (e.Square != null || e.Hero != null || DateTime.UtcNow - e.CheckedAt < TimeSpan.FromDays(7)))
                return (e.Hero is { } h && File.Exists(h) ? h : null, e.Square is { } l && File.Exists(l) ? l : null);
        }
        finally { Gate.Release(); }

        var cdn = $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}";
        var entry = new Entry
        {
            CheckedAt = DateTime.UtcNow, Source = "Steam",
            Square = await DownloadAsync($"{cdn}/logo.png", Path.Combine(Folder, $"steam_{appId}-logo.png")), // logo kept in the Square slot
            Hero = await DownloadAsync($"{cdn}/library_hero.jpg", Path.Combine(Folder, $"steam_{appId}-hero.jpg")),
        };
        await Gate.WaitAsync();
        try { _index![key] = entry; SaveIndex(); }
        finally { Gate.Release(); }
        return (entry.Hero, entry.Square);
    }

    // ---------------- sources ----------------

    /// <summary>Microsoft Store catalog: FeaturePromotionalSquareArt, or square box art.</summary>
    static async Task<(string square, string? hero, string source)?> Xbox(string title)
    {
        try
        {
            var url = "https://displaycatalog.mp.microsoft.com/v7.0/productFamilies/Games/products?query=" +
                      Uri.EscapeDataString(title) + "&market=AU&languages=en-AU&fieldsTemplate=details&platformdependencyname=windows.xbox";
            var body = JsonNode.Parse(await Http.GetStringAsync(url));
            var products = (body?["Products"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
            static string TitleOf(JsonObject p) => (string?)p["LocalizedProperties"]?[0]?["ProductTitle"] ?? "";
            var best = products.OrderByDescending(p => TitleMatch.Score(title, TitleOf(p))).FirstOrDefault();
            if (best is null || TitleMatch.Score(title, TitleOf(best)) < 82) return null;

            var images = (best["LocalizedProperties"]?[0]?["Images"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
            JsonObject? Pick(string? purpose, bool square) => images.FirstOrDefault(i =>
                (purpose == null || (string?)i["ImagePurpose"] == purpose) &&
                (!square || (int?)i["Width"] == (int?)i["Height"]));
            var sq = Pick("FeaturePromotionalSquareArt", true) ?? Pick("BoxArt", true) ?? Pick(null, true);
            var hero = Pick("SuperHeroArt", false) ?? Pick("TitledHeroArt", false);
            var squareUrl = StoreImage(sq, 720);
            return squareUrl is null ? null : (squareUrl, StoreImage(hero, 1920), "Xbox");
        }
        catch (Exception ex) { Log.Warn($"Xbox art lookup for {title}: {ex.Message}"); return null; }
    }

    static string? StoreImage(JsonObject? image, int width)
    {
        var uri = (string?)image?["Uri"];
        if (uri is null) return null;
        if (uri.StartsWith("//")) uri = "https:" + uri;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Host != "store-images.s-microsoft.com") return null;
        return $"{u.GetLeftPart(UriPartial.Path)}?w={width}";
    }

    /// <summary>PlayStation Store search (the store website's own GraphQL query): MASTER key art is square.</summary>
    static async Task<(string square, string? hero, string source)?> PlayStation(string title)
    {
        try
        {
            var variables = JsonSerializer.Serialize(new { countryCode = "US", languageCode = "en", nextCursor = "", pageOffset = 0, pageSize = 24, searchTerm = title });
            var extensions = "{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"4df6284f982e57bec70f23c77e2c219dc792eb19af7fb3d3a81767aa3f1958aa\"}}";
            var url = "https://web.np.playstation.com/api/graphql/v1//op?operationName=getSearchResults&variables=" +
                      Uri.EscapeDataString(variables) + "&extensions=" + Uri.EscapeDataString(extensions);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Origin", "https://store.playstation.com");
            req.Headers.Referrer = new Uri("https://store.playstation.com/");
            req.Headers.Add("apollographql-client-name", "@sie-ppr-web-store/app");
            req.Headers.Add("apollographql-client-version", "0.113.0");
            req.Headers.Add("X-PSN-Store-Locale-Override", "en-US");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            var results = (JsonNode.Parse(await res.Content.ReadAsStringAsync())?["data"]?["universalSearch"]?["results"] as JsonArray)
                ?.OfType<JsonObject>().ToList() ?? new();
            var skip = new[] { "ADD_ON", "ADD_ON_PACK", "DEMO", "VIRTUAL_CURRENCY" };
            var best = results
                .Where(r => !skip.Contains((string?)r["storeDisplayClassification"]) && TitleMatch.Score(title, (string?)r["name"]) >= 82)
                .OrderByDescending(r => TitleMatch.Score(title, (string?)r["name"])).FirstOrDefault();
            if (best is null) return null;
            var media = (best["media"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
            string? Image(params string[] roles) => roles
                .Select(role => (string?)media.FirstOrDefault(m => (string?)m["type"] == "IMAGE" && (string?)m["role"] == role)?["url"])
                .FirstOrDefault(u => u != null);
            var square = Image("MASTER", "GAMEHUB_COVER_ART", "EDITION_KEY_ART");
            if (square is null || !square.StartsWith("https://image.api.playstation.com/")) return null;
            var hero = Image("BACKGROUND", "SIXTEEN_BY_NINE_BANNER", "BACKGROUND_LAYER_ART");
            return (square + "?w=720", hero is null ? null : hero + "?w=1920", "PlayStation");
        }
        catch (Exception ex) { Log.Warn($"PlayStation art lookup for {title}: {ex.Message}"); return null; }
    }

    /// <summary>IGN's game objects have a square primary image.</summary>
    static async Task<(string square, string? hero, string source)?> Ign(string title)
    {
        try
        {
            var variables = JsonSerializer.Serialize(new { term = title, count = 20, objectType = "Game" });
            var extensions = "{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"e1c2e012a21b4a98aaa618ef1b43eb0cafe9136303274a34f5d9ea4f2446e884\"}}";
            var url = "https://mollusk.apis.ign.com/graphql?operationName=SearchObjectsByName&variables=" +
                      Uri.EscapeDataString(variables) + "&extensions=" + Uri.EscapeDataString(extensions);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://www.ign.com/reviews/games");
            req.Headers.Add("apollographql-client-name", "kraken");
            req.Headers.Add("apollographql-client-version", "v0.67.0");
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            var objects = (JsonNode.Parse(await res.Content.ReadAsStringAsync())?["data"]?["searchObjectsByName"]?["objects"] as JsonArray)
                ?.OfType<JsonObject>().ToList() ?? new();
            static string? NameOf(JsonObject o) => (string?)o["metadata"]?["names"]?["name"];
            var best = objects.Where(o => TitleMatch.Score(title, NameOf(o)) >= 85)
                .OrderByDescending(o => TitleMatch.Score(title, NameOf(o))).FirstOrDefault();
            var square = (string?)best?["primaryImage"]?["url"];
            if (square is null || !square.StartsWith("https://assets-prd.ignimgs.com/")) return null;
            return (square + "?width=720", null, "IGN");
        }
        catch (Exception ex) { Log.Warn($"IGN art lookup for {title}: {ex.Message}"); return null; }
    }

    // ---------------- cache ----------------

    /// <summary>A small picture (like an achievement icon) kept on disk after the first download.</summary>
    public static async Task<string?> SmallImageAsync(string url, string name)
    {
        var path = Path.Combine(Folder, "small", name);
        if (File.Exists(path)) return path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length < 64) return null;
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception ex) { Log.Warn($"download {url}: {ex.Message}"); return null; }
    }

    static async Task<string?> DownloadAsync(string url, string path)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length < 1024) return null;
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception ex) { Log.Warn($"download {url}: {ex.Message}"); return null; }
    }

    static void LoadIndex()
    {
        if (_index != null) return;
        try
        {
            _index = File.Exists(IndexPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexPath)) ?? new()
                : new();
        }
        catch { _index = new(); }
    }

    static void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(IndexPath + ".tmp", JsonSerializer.Serialize(_index, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(IndexPath + ".tmp", IndexPath, overwrite: true);
        }
        catch (Exception ex) { Log.Error("saving artwork index", ex); }
    }
}
