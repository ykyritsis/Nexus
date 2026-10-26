using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>
/// Setup checks for the Guide button (Windows Game Bar and Steam both want it) and app options.
/// Settings only change when the user presses the matching button.
/// </summary>
public sealed partial class MoreSettingsView : UserControl, INexusView
{
    readonly App _app;

    public string Title => "More settings";

    public MoreSettingsView(App app)
    {
        _app = app;
        InitializeComponent();
        VersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)}";
    }

    public void OnShown() => Refresh();
    public void OnHidden() { }

    void Refresh()
    {
        bool gameBar = SetupChecks.GameBarUsesGuide;
        GameBarStatus.Text = gameBar
            ? "Also opens when you press Guide. Turn off \"Allow your controller to open Game Bar\"."
            : "Off. The Guide button belongs to Nexus.";
        GameBarButton.Content = gameBar ? "Open Game Bar settings" : "Game Bar settings";

        SteamStatus.Text = SetupChecks.SteamUsesGuide() switch
        {
            true => "\"Guide button focuses Steam\" is on, so Steam opens too. Turn it off in Steam > Settings > Controller.",
            false => "Leaves the Guide button alone.",
            null => "Couldn't read Steam's setting. If Steam opens on Guide, turn off \"Guide button focuses Steam\".",
        };
        _refreshing = true;
        StartupSwitch.IsOn = SetupChecks.StartWithWindows;
        ToastSwitch.IsOn = _app.Settings.ControllerToasts;
        _refreshing = false;
    }

    bool _refreshing;

    // Writing UseNexusForGameBarEnabled alone doesn't reach a running Game Bar; the Settings toggle
    // ("Allow your controller to open Game Bar") also notifies it, so send the user there.
    void GameBar_Click(object sender, RoutedEventArgs e)
    {
        _app.Shell.Hide(restoreFocus: false);
        Launch.Open("ms-settings:gaming-gamebar");
    }

    void Steam_Click(object sender, RoutedEventArgs e)
    {
        _app.Shell.Hide(restoreFocus: false);
        Launch.Open("steam://open/settings");
    }

    void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        SetupChecks.StartWithWindows = StartupSwitch.IsOn;
    }

    void Toasts_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        _app.Settings.ControllerToasts = ToastSwitch.IsOn;
        _app.Settings.Save();
    }

    void Log_Click(object sender, RoutedEventArgs e)
    {
        _app.Shell.Hide(restoreFocus: false);
        Launch.Open(Log.Folder);
    }

    void Quit_Click(object sender, RoutedEventArgs e) => _app.Quit();
}
