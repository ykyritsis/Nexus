using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;
using Nexus.Interop;
using Nexus.Providers;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>
/// Settings tab in the shape of Windows 11 Quick Settings: action buttons that turn accent-coloured
/// when on, a label under each, and the speaker/mic levels with an output shortcut on the right.
/// </summary>
public sealed partial class SettingsView : UserControl, INexusView
{
    sealed class QuickAction
    {
        public required Button Button;
        public required TextBlock Label;
        public required FontIcon Icon;
    }

    readonly App _app;
    readonly ShellWindow _shell;
    readonly AudioService _audio = new();
    readonly DispatcherQueueTimer _poll;
    readonly Dictionary<string, QuickAction> _actions = new();
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

        // Grid order, left to right, top to bottom.
        Add("mic", 0, 0, "", "Microphone", Mic_Click);
        Add("bluetooth", 1, 0, "", "Bluetooth", Bluetooth_Click);
        Add("airplane", 2, 0, "", "Airplane mode", Airplane_Click);
        Add("xbox", 3, 0, "", "Xbox mode", Xbox_Click);
        Add("screenshot", 4, 0, "", "Screenshot", Screenshot_Click);
        Add("network", 0, 1, "", "Network", Network_Click);
        Add("keyboard", 1, 1, "", "Keyboard", Keyboard_Click);
        Add("cursor", 2, 1, "", "Gamepad cursor", Cursor_Click);
        Add("compact", 3, 1, "", "Compact", Compact_Click);
        Add("gamebar", 4, 1, "", "Game Bar", GameBar_Click);
    }

    void Add(string key, int col, int row, string glyph, string label, RoutedEventHandler click)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 18 };
        var button = new Button { Style = (Style)Application.Current.Resources["QuickActionStyle"], Content = icon };
        button.Click += click;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        var text = new TextBlock
        {
            Text = label, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var cell = new StackPanel { Spacing = 8, Children = { button, text } };
        Grid.SetColumn(cell, col);
        Grid.SetRow(cell, row);
        ActionGrid.Children.Add(cell);
        _actions[key] = new QuickAction { Button = button, Label = text, Icon = icon };
    }

    /// <summary>On = the built-in accent button style, exactly like a Quick Settings toggle.</summary>
    void Set(string key, bool on, string? label = null, string? glyph = null, bool enabled = true)
    {
        var a = _actions[key];
        var style = (Style)Application.Current.Resources[on ? "QuickActionOnStyle" : "QuickActionStyle"];
        if (a.Button.Style != style) a.Button.Style = style;
        if (label != null) a.Label.Text = label;
        if (glyph != null) a.Icon.Glyph = glyph;
        a.Button.IsEnabled = enabled;
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
            {
                Set("mic", !mic.muted, mic.muted ? "Mic off" : "Microphone", mic.muted ? "" : "");
                MicGlyph.Glyph = mic.muted ? "" : "";
                if (MicSlider.FocusState == FocusState.Unfocused) MicSlider.Value = Math.Round(mic.volume * 100);
                MicValue.Text = $"{Math.Round(mic.volume * 100)}";
            }
            else Set("mic", false, "No microphone", "", enabled: false);

            if (_audio.Endpoint(DataFlow.Render) is { } spk)
            {
                SpeakerGlyph.Glyph = spk.muted ? "" : "";
                if (VolumeSlider.FocusState == FocusState.Unfocused) VolumeSlider.Value = Math.Round(spk.volume * 100);
                VolumeValue.Text = $"{Math.Round(spk.volume * 100)}";
            }
            var id = _audio.DefaultId(DataFlow.Render);
            OutputName.Text = _audio.Devices(DataFlow.Render).FirstOrDefault(d => d.Id == id)?.Name ?? "No output device";
        }
        catch (Exception ex) { Log.Error("settings audio refresh", ex); }
        finally { _updating = false; }
    }

    void RefreshLocal()
    {
        bool xboxSupported = SystemStatus.XboxModeSupported();
        Set("xbox", xboxSupported && SystemStatus.XboxModeActive(), enabled: xboxSupported);
        Set("compact", _app.Settings.CompactMode);
        Set("cursor", _app.Settings.GamepadCursor);
        Set("keyboard", false);
        Set("screenshot", false);
        Set("gamebar", false);
        var (connected, detail) = SystemStatus.Network();
        var kind = detail.Contains('·') ? detail[(detail.IndexOf('·') + 1)..].Trim() : detail;
        Set("network", connected, kind, connected ? "" : "");
        NetGlyph.Glyph = connected ? "" : "";
        NetText.Text = detail;
    }

    async Task RefreshRadiosAsync()
    {
        var bt = await SystemStatus.BluetoothAsync();
        Set("bluetooth", bt is not null and not "Off", bt is null ? "No Bluetooth" : bt == "Off" ? "Bluetooth" : bt, enabled: bt is not null);
        var airplane = await SystemStatus.AirplaneAsync();
        Set("airplane", airplane == true, enabled: airplane is not null);
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
        await SystemStatus.SetBluetoothAsync(!(_actions["bluetooth"].Button.Style == (Style)Application.Current.Resources["QuickActionOnStyle"]));
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

    void Keyboard_Click(object sender, RoutedEventArgs e)
    {
        _shell.Hide();
        SystemStatus.ToggleTouchKeyboard();
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
