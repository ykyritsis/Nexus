using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NAudio.CoreAudioApi;
using Nexus.Interop;
using Nexus.Providers;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>
/// Settings tab as a Control Center: glass tiles that turn frosted white when on, and the
/// speaker/mic levels with an output shortcut beside them.
/// </summary>
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
        Tiles.Set(MicTile, "", "Microphone", "");
        Tiles.Set(BluetoothTile, "", "Bluetooth", "");
        Tiles.Set(AirplaneTile, "", "Airplane mode", "");
        Tiles.Set(XboxTile, "", "Xbox mode", "");
        Tiles.Set(ScreenshotTile, "", "Screenshot", "Capture the game");
        Tiles.Set(NetworkTile, "", "Network", "");
        Tiles.Set(KeyboardTile, "", "Keyboard", "Show");
        Tiles.Set(CursorTile, "", "Gamepad cursor", "");
        Tiles.Set(CompactTile, "", "Compact", "");
        Tiles.Set(GameBarTile, "", "Game Bar", "Record & capture");
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

    string? _outputId = "";

    void RefreshAudio()
    {
        _updating = true;
        try
        {
            if (_audio.Endpoint(DataFlow.Capture) is { } mic)
            {
                Tiles.Set(MicTile, mic.muted ? "" : "", "Microphone", mic.muted ? "Off" : "On", active: !mic.muted);
                MicGlyph.Glyph = mic.muted ? "" : "";
                if (MicSlider.FocusState == FocusState.Unfocused) MicSlider.Value = Math.Round(mic.volume * 100);
                MicValue.Text = $"{Math.Round(mic.volume * 100)}";
            }
            else Tiles.Set(MicTile, "", "Microphone", "None");

            if (_audio.Endpoint(DataFlow.Render) is { } spk)
            {
                SpeakerGlyph.Glyph = spk.muted ? "" : "";
                if (VolumeSlider.FocusState == FocusState.Unfocused) VolumeSlider.Value = Math.Round(spk.volume * 100);
                VolumeValue.Text = $"{Math.Round(spk.volume * 100)}";
            }
            // The device list is only read again when the default output actually changes.
            var id = _audio.DefaultId(DataFlow.Render);
            if (id != _outputId)
            {
                _outputId = id;
                OutputName.Text = _audio.Devices(DataFlow.Render).FirstOrDefault(d => d.Id == id)?.Name ?? "No output device";
            }
        }
        catch (Exception ex) { Log.Error("settings audio refresh", ex); }
        finally { _updating = false; }
    }

    async void RefreshLocal()
    {
        bool xboxSupported = SystemStatus.XboxModeSupported();
        bool xboxOn = xboxSupported && SystemStatus.XboxModeActive();
        Tiles.Set(XboxTile, "", "Xbox mode", !xboxSupported ? "Not available" : xboxOn ? "On" : "Off", active: xboxOn);
        XboxTile.IsEnabled = xboxSupported;
        Tiles.Set(CompactTile, "", "Compact", _app.Settings.CompactMode ? "On" : "Off", active: _app.Settings.CompactMode);
        Tiles.Set(CursorTile, "", "Gamepad cursor", _app.Settings.GamepadCursor ? "On" : "Off", active: _app.Settings.GamepadCursor);
        var (connected, detail) = await Task.Run(SystemStatus.Network);
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

    void Mic_Click(object sender, RoutedEventArgs e) => MicMute_Click(sender, e);

    void MicMute_Click(object sender, RoutedEventArgs e)
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

    void Mic_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        _audio.SetVolume(DataFlow.Capture, (float)(e.NewValue / 100));
        MicValue.Text = $"{Math.Round(e.NewValue)}";
    }

    void Output_Click(object sender, RoutedEventArgs e) => _shell.SelectTab("audio", focus: true);

    async void Bluetooth_Click(object sender, RoutedEventArgs e)
    {
        await SystemStatus.SetBluetoothAsync(!Tiles.IsActive(BluetoothTile));
        await RefreshRadiosAsync();
    }

    async void Airplane_Click(object sender, RoutedEventArgs e)
    {
        var on = await SystemStatus.AirplaneAsync();
        if (on is null) return;
        await SystemStatus.SetAirplaneAsync(!on.Value);
        await RefreshRadiosAsync();
    }

    void Xbox_Click(object sender, RoutedEventArgs e)
    {
        _shell.Hide();
        SystemStatus.ToggleXboxMode();
    }

    /// <summary>Game Bar's game screenshot (Win+Alt+PrtScn), taken once Nexus is out of the way.</summary>
    async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        _shell.Hide();
        await Task.Delay(450);
        Native.PressChord(Native.VK_LWIN, Native.VK_MENU, 0x2C /*VK_SNAPSHOT*/);
    }

    void Network_Click(object sender, RoutedEventArgs e)
    {
        _shell.Hide(restoreFocus: false);
        Launch.Open("ms-settings:network-status");
    }

    async void Keyboard_Click(object sender, RoutedEventArgs e)
    {
        _shell.Hide();
        await SystemStatus.ToggleTouchKeyboardAsync();
    }

    void Cursor_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.GamepadCursor = !_app.Settings.GamepadCursor;
        _app.Settings.Save();
        _app.Gamepad.CursorMode = _app.Settings.GamepadCursor;
        RefreshLocal();
    }

    void Compact_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.CompactMode = !_app.Settings.CompactMode;
        _app.Settings.Save();
        _shell.Relayout();
        RefreshLocal();
    }

    void GameBar_Click(object sender, RoutedEventArgs e)
    {
        _shell.Hide();
        Launch.Open("ms-gamebar://launchForeground");
    }

    void More_Click(object sender, RoutedEventArgs e) => _shell.OpenSubPage(new MoreSettingsView(_app));
}
