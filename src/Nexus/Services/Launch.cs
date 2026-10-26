using System.Diagnostics;

namespace Nexus.Services;

/// <summary>Opens apps, files, folders and URIs (steam://, ms-settings:, shell:AppsFolder\...).</summary>
public static class Launch
{
    public const string XboxApp = @"shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    public static bool Open(string target, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (args != null) psi.Arguments = args;
            if (File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target)!;
            using var _ = Process.Start(psi);
            Log.Info($"opened {target}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"could not open {target}", ex);
            return false;
        }
    }

    /// <summary>OmniDash if it's installed, otherwise null.</summary>
    public static string? OmniDashPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Xbox Preview UI");
        foreach (var exe in new[] { "OmniDash.exe", "Xbox Preview UI.exe" })
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Resolves a Home/Library setting: "auto", "xbox", "steam", or a literal path/URI.</summary>
    public static string Resolve(string setting) => setting.ToLowerInvariant() switch
    {
        "auto" => OmniDashPath() ?? XboxApp,
        "xbox" => XboxApp,
        "steam" => "steam://open/games",
        _ => setting,
    };
}
