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
    }

    public void OnShown()
    {
        Refresh();
        _app.Updater.StatusChanged += UpdateStatus;
        UpdateStatus();
    }

    public void OnHidden() => _app.Updater.StatusChanged -= UpdateStatus;

    void UpdateStatus()
    {
        VersionText.Text = $"Version {Updater.Current.ToString(3)} · {_app.Updater.Status}";
        UpdateButton.Content = _app.Updater.UpdateReady ? "Install update" : "Check for updates";
        UpdateButton.IsEnabled = !Updater.IsDevBuild;
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Updater.UpdateReady) { _app.Updater.TryInstall(onlyWhenIdle: false); return; }
        await _app.Updater.CheckAsync();
    }

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
        SizeSlider.Value = Math.Round(Math.Clamp(_app.Settings.OverlaySize, 0.4, 0.7) * 100);
        SizeValue.Text = $"{SizeSlider.Value:0}%";
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

    /// <summary>Resizes the overlay live; saved a moment after you stop moving the slider.</summary>
    void Size_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        SizeValue.Text = $"{e.NewValue:0}%";
        if (_refreshing) return;
        _app.Settings.OverlaySize = e.NewValue / 100;
        _app.Shell.Relayout();
        _saveSize ??= CreateSaveTimer();
        _saveSize.Stop();
        _saveSize.Start();
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveSize;

    Microsoft.UI.Dispatching.DispatcherQueueTimer CreateSaveTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(600);
        t.IsRepeating = false;
        t.Tick += (_, _) => _app.Settings.Save();
        return t;
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
