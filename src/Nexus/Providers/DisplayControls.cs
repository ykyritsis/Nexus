using System.Diagnostics;
using System.Xml.Linq;
using Nexus.Interop;
using Nexus.Services;

namespace Nexus.Providers;

/// <summary>
/// Display actions for the game in front, reusing the StreamDock scripts when they're present:
/// Borderless.ps1 (fixed-size borderless window, placement) and LsMode.ps1 (Lossless Scaling frame
/// generation), plus the Lossless Scaling and Windows HDR shortcuts.
/// </summary>
public static class DisplayControls
{
    // Documents may be redirected to OneDrive while the scripts stay in the profile's own Documents.
    static readonly string ScriptDir = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "StreamDock Modes"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "StreamDock Modes"),
        }.FirstOrDefault(Directory.Exists) ?? "";
    static string Borderless => Path.Combine(ScriptDir, "Borderless.ps1");
    static string LsMode => Path.Combine(ScriptDir, "LsMode.ps1");
    static string LsSettings => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lossless Scaling", "Settings.xml");
    static string PlacementFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamDock Modes", "placement.txt");

    public static bool HasBorderless => File.Exists(Borderless);
    public static bool HasLossless => File.Exists(LsSettings);
    public static bool HasLsMode => File.Exists(LsMode) && HasLossless;

    public static readonly (int w, int h)[] Sizes = { (3840, 1600), (3840, 1440), (3840, 1400) };

    public static void SetBorderless(int w, int h) => RunScript(Borderless, $"-Width {w} -Height {h}");
    public static void CyclePlacement() => RunScript(Borderless, "-Mode Place -Align Next");
    public static void CycleFrameGen() => RunScript(LsMode, "-Mode Next");

    /// <summary>Lossless Scaling's scale shortcut (Ctrl+Alt+S by default).</summary>
    public static void ToggleLossless() => Native.PressChord(0x11 /*Ctrl*/, Native.VK_MENU, 0x53 /*S*/);

    /// <summary>Windows' HDR shortcut, Win+Alt+B.</summary>
    public static void ToggleHdr() => Native.PressChord(Native.VK_LWIN, Native.VK_MENU, 0x42 /*B*/);

    public static string Placement
    {
        get
        {
            try { var v = File.ReadAllText(PlacementFile).Trim(); return v is "Top" or "Bottom" ? v : "Centre"; }
            catch { return "Centre"; }
        }
    }

    /// <summary>"2×", "3×", "4×" or "Auto" from Lossless Scaling's settings.</summary>
    public static string FrameGenMode
    {
        get
        {
            try
            {
                var p = XDocument.Load(LsSettings).Root?.Element("GameProfiles")?.Element("Profile");
                if (p?.Element("LSFG3Mode1")?.Value == "ADAPTIVE") return "Auto";
                return (p?.Element("LSFG3Multiplier")?.Value ?? "2") + "×";
            }
            catch { return "?"; }
        }
    }

    static void RunScript(string script, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("conhost.exe",
                $"--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{script}\" {args}")
            { UseShellExecute = false, CreateNoWindow = true });
            Log.Info($"ran {Path.GetFileName(script)} {args}");
        }
        catch (Exception ex) { Log.Error($"running {script}", ex); }
    }
}
