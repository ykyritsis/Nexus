using System.Text.RegularExpressions;

namespace Nexus.Providers;

/// <summary>
/// Decides whether a store result is the same game (ported from OmniDash's title-match.cjs).
/// A close spelling isn't enough: sequel numbers, roman numerals, years and words like
/// "Remastered" decide which game it is, so they must agree.
/// </summary>
public static class TitleMatch
{
    static readonly HashSet<string> EditionWords = new()
    {
        "edition", "standard", "deluxe", "ultimate", "gold", "premium", "complete", "goty", "game", "of", "the", "year",
        "digital", "pc", "windows", "for", "and", "bundle", "pack", "collectors", "collector", "s", "launch", "special",
        "steam", "version", "microsoft", "store", "xbox", "series", "x", "one", "enhanced",
    };
    static readonly HashSet<string> DistinctWords = new()
    {
        "remastered", "remaster", "remake", "reloaded", "redux", "reforged", "definitive", "anniversary", "origins", "legends",
        "returns", "reborn", "zero", "infinite", "vanguard", "beta", "demo", "playtest", "vr", "online", "multiplayer",
        "campaign", "dlc", "season", "soundtrack",
    };
    static readonly Regex Roman = new("^(?:i{1,3}|iv|v|vi{1,3}|ix|x|xi{1,3}|xiv|xv)$");
    static readonly Regex Year = new("^(19|20)\\d{2}$");

    public static string Normalize(string? value) =>
        Regex.Replace((value ?? "").ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();

    /// <summary>"Call of Duty: Black Ops - Multiplayer" and friends share the base game's art.</summary>
    public static string BaseTitle(string title) =>
        Regex.Replace(Regex.Replace(title, "[​-‍﻿™®©]", ""),
            "\\s*[-–:]\\s*(multiplayer|zombies|campaign|single ?player|mp|sp|beta|dedicated server|server|demo)\\s*$", "",
            RegexOptions.IgnoreCase).Trim();

    static (string[] words, string[] years, string[] numbers, string[] distinct) Tokens(string value)
    {
        var words = Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var years = words.Where(w => Year.IsMatch(w)).ToArray();
        var numbers = words.Select((w, i) => (w, i))
            .Where(x => !Year.IsMatch(x.w) && (x.w.All(char.IsDigit) || (x.i > 0 && Roman.IsMatch(x.w) && words[x.i - 1] != "series")))
            .Select(x => x.w).Where(w => w != "1" && w != "i").ToArray();
        var distinct = words.Where(DistinctWords.Contains).ToArray();
        return (words, years, numbers, distinct);
    }

    static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) => new HashSet<string>(a).SetEquals(b);

    /// <summary>True when two titles name different games of the same series.</summary>
    static bool Conflict(string query, string candidate)
    {
        var a = Tokens(query);
        var b = Tokens(candidate);
        if (!SameSet(a.numbers, b.numbers)) return true;
        if (a.years.Length > 0 && b.years.Length > 0 && !a.years.Intersect(b.years).Any()) return true;
        return !SameSet(a.distinct, b.distinct);
    }

    static double EditSimilarity(string wanted, string actual)
    {
        var rows = Enumerable.Range(0, wanted.Length + 1).ToArray();
        for (int col = 1; col <= actual.Length; col++)
        {
            int diagonal = rows[0];
            rows[0] = col;
            for (int row = 1; row <= wanted.Length; row++)
            {
                int previous = rows[row];
                rows[row] = Math.Min(Math.Min(rows[row] + 1, rows[row - 1] + 1), diagonal + (wanted[row - 1] == actual[col - 1] ? 0 : 1));
                diagonal = previous;
            }
        }
        return 1 - rows[wanted.Length] / (double)Math.Max(wanted.Length, actual.Length);
    }

    /// <summary>0-100. 82+ is treated as the same game for artwork.</summary>
    public static int Score(string query, string? candidate)
    {
        string wanted = Normalize(query), actual = Normalize(candidate);
        if (wanted == "" || actual == "") return 0;
        if (wanted == actual) return 100;
        if (Conflict(query, candidate!)) return 30;
        string Strip(string v) => string.Join(' ', Tokens(v).words.Where(w => !EditionWords.Contains(w) && !Year.IsMatch(w)));
        string wantedCore = Strip(query), actualCore = Strip(candidate!);
        if (wantedCore != "" && wantedCore == actualCore) return 96;
        if (actual.StartsWith(wanted) || wanted.StartsWith(actual))
        {
            var longer = actual.Length > wanted.Length ? actual : wanted;
            var shorter = longer == actual ? wanted : actual;
            var extra = longer[shorter.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return extra.All(w => EditionWords.Contains(w) || Year.IsMatch(w)) ? 90 : 60;
        }
        if (actual.Contains(wanted) || wanted.Contains(actual)) return 62;
        var ws = new HashSet<string>(wanted.Split(' '));
        var acs = new HashSet<string>(actual.Split(' '));
        double overlap = ws.Count(acs.Contains) / (double)Math.Max(ws.Count, acs.Count);
        return (int)Math.Round(Math.Max(overlap * 68, EditSimilarity(wanted, actual) * 64));
    }
}
