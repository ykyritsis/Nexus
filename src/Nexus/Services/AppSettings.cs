using System.Text.Json;

namespace Nexus.Services;

/// <summary>User settings, saved as JSON in %LOCALAPPDATA%\Nexus\settings.json.</summary>
public sealed class AppSettings
{
    public bool CompactMode { get; set; }
    public bool GamepadCursor { get; set; }

    /// <summary>"auto" (OmniDash if installed, else the Xbox app), "xbox", "steam" or a path/URI.</summary>
    public string HomeTarget { get; set; } = "auto";
    public string LibraryTarget { get; set; } = "xbox";

    /// <summary>Volume mixer apps pinned to the top, by process name.</summary>
    public List<string> FavoriteApps { get; set; } = new();

    public int RecentGameCount { get; set; } = 4;

    static string FilePath => Path.Combine(Log.Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("settings unreadable, using defaults", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("could not save settings", ex);
        }
    }
}
