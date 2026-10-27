using System.Text.Json;

namespace Nexus.Services;

/// <summary>User settings, saved as JSON in %LOCALAPPDATA%\Nexus\settings.json.</summary>
public sealed class AppSettings
{
    public bool CompactMode { get; set; }
    /// <summary>Overlay width as a share of the screen, 0.40 to 0.70 (Compact makes it a little smaller).</summary>
    public double OverlaySize { get; set; } = 0.5;
    public bool GamepadCursor { get; set; }
    /// <summary>Small pop-ups when a controller connects, disconnects or runs low.</summary>
    public bool ControllerToasts { get; set; } = true;
    /// <summary>While a game runs, games ignore the controller while the guide is open (InputBridge).</summary>
    public bool LockGameInput { get; set; } = true;
    /// <summary>Opening the guide over a game taps Start so it pauses (InputBridge).</summary>
    public bool PauseOnGuide { get; set; } = true;

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
