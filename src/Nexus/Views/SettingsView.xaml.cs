using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NAudio.CoreAudioApi;
using Nexus.Providers;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>Settings tab: quick toggles and status tiles.</summary>
public sealed partial class SettingsView : UserControl, INexusView
{
    readonly App _app;
    readonly ShellWindow _shell;
    readonly AudioService _audio = new();
    readonly DispatcherQueueTimer _poll;
    bool _updating;

    public string Title => "Settings";

    public SettingsView(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        InitializeComponent();
        _poll = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(1.5);
        _poll.Tick += (_, _) => RefreshAudio();

        // Placeholders until the first refresh fills in the real state.
        Tiles.Set(MicTile, "", "Mic", "");
        Tiles.Set(KeyboardTile, "", "Keyboard", "Show");
        Tiles.Set(XboxTile, "", "Xbox mode", "");
        Tiles.Set(CompactTile, "", "Compact", "");
        Tiles.Set(NetworkTile, "", "Network", "");
        Tiles.Set(BluetoothTile, "", "Bluetooth", "");
        Tiles.Set(AirplaneTile, "", "Airplane mode", "");
        Tiles.Set(CursorTile, "", "Gamepad cursor", "");
        Tiles.Set(GameBarTile, "", "Game Bar", "Record & capture");
        Tiles.Set(MoreTile, "", "More settings", "Setup & app");
    }

    public void OnShown()
    {
        RefreshAudio();
        RefreshLocal();
        _ = RefreshRadiosAsync();
        _poll.Start();
    }

    public void OnHidden() => _poll.Stop();

    // ---------------- refresh ----------------

    void RefreshAudio()
    {
        _updating = true;
        try
        {
            if (_audio.Endpoint(DataFlow.Capture) is { } mic)
                Tiles.Set(MicTile, mic.muted ? "" : "", "Mic", mic.muted ? "Off" : "On", active: !mic.muted);
            else
                Tiles.Set(MicTile, "", "Mic", "No microphone");

            if (_audio.Endpoint(DataFlow.Render) is { } spk)
            {
                if (VolumeSlider.FocusState == FocusState.Unfocused) // don't fight the user mid-adjust
                    VolumeSlider.Value = Math.Round(spk.volume * 100);
                VolumeValue.Text = spk.muted ? "Muted" : $"{Math.Round(spk.volume * 100)}";
                SpeakerGlyph.Glyph = spk.muted ? "" : "";
            }
        }
        finally { _updating = false; }
    }

    void RefreshLocal()
    {
        bool xboxSupported = SystemStatus.XboxModeSupported();
        bool xboxOn = xboxSupported && SystemStatus.XboxModeActive();
        Tiles.Set(XboxTile, "", "Xbox mode", !xboxSupported ? "Not available" : xboxOn ? "On" : "Off", active: xboxOn);
        XboxTile.IsEnabled = xboxSupported;
        Tiles.Set(CompactTile, "", "Compact", _app.Settings.CompactMode ? "On" : "Off", active: _app.Settings.CompactMode);
        Tiles.Set(CursorTile, "", "Gamepad cursor", _app.Settings.GamepadCursor ? "On" : "Off", active: _app.Settings.GamepadCursor);
        var (connected, detail) = SystemStatus.Network();
        Tiles.Set(NetworkTile, connected ? "" : "", "Network", detail, active: connected);
    }

    async Task RefreshRadiosAsync()
    {
        var bt = await SystemStatus.BluetoothAsync();
        Tiles.Set(BluetoothTile, "", "Bluetooth", bt ?? "Not available", active: bt is not null and not "Off");
        BluetoothTile.IsEnabled = bt is not null;
        var airplane = await SystemStatus.AirplaneAsync();
        Tiles.Set(AirplaneTile, "", "Airplane mode", airplane switch { true => "On", false => "Off", null => "No wireless" }, active: airplane == true);
        AirplaneTile.IsEnabled = airplane is not null;
    }

    // ---------------- actions ----------------

    void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (_audio.Endpoint(DataFlow.Capture) is { } mic) _audio.SetMute(DataFlow.Capture, !mic.muted);
        RefreshAudio();
    }

    void SpeakerMute_Click(object sender, RoutedEventArgs e)
    {
        if (_audio.Endpoint(DataFlow.Render) is { } spk) _audio.SetMute(DataFlow.Render, !spk.muted);
        RefreshAudio();
    }

    void Volume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        _audio.SetVolume(DataFlow.Render, (float)(e.NewValue / 100));
        VolumeValue.Text = $"{Math.Round(e.NewValue)}";
    }

    void Keyboard_Click(object sender, RoutedEventArgs e)
    {
        _shell.Hide();
        SystemStatus.ToggleTouchKeyboard();
    }

    void Xbox_Click(object sender, RoutedEventArgs e)
    {
        _shell.Hide();
        SystemStatus.ToggleXboxMode();
    }

    void Compact_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.CompactMode = !_app.Settings.CompactMode;
        _app.Settings.Save();
        _shell.Relayout();
        RefreshLocal();
    }

    void Network_Click(object sender, RoutedEventArgs e)
    {
        _shell.Hide(restoreFocus: false);
        Launch.Open("ms-settings:network-status");
    }

    void Bluetooth_Click(object sender, RoutedEventArgs e)
    {
        _shell.Hide(restoreFocus: false);
        Launch.Open("ms-settings:bluetooth");
    }

    async void Airplane_Click(object sender, RoutedEventArgs e)
    {
        var on = await SystemStatus.AirplaneAsync();
        if (on is null) return;
        await SystemStatus.SetAirplaneAsync(!on.Value);
        await RefreshRadiosAsync();
    }

    void Cursor_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.GamepadCursor = !_app.Settings.GamepadCursor;
        _app.Settings.Save();
        _app.Gamepad.CursorMode = _app.Settings.GamepadCursor;
        RefreshLocal();
    }

    void GameBar_Click(object sender, RoutedEventArgs e)
    {
        _shell.Hide();
        Launch.Open("ms-gamebar://launchForeground");
    }

    void More_Click(object sender, RoutedEventArgs e) => _shell.OpenSubPage(new MoreSettingsView(_app));
}
