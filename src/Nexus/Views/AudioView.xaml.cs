using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;
using Nexus.Providers;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>
/// Audio tab: speaker and mic on the left (mute tiles, levels, default devices), and every app
/// playing sound as a vertical channel strip on the right. Y favourites an app (pinned first).
/// </summary>
public sealed partial class AudioView : UserControl, INexusView
{
    sealed class Strip
    {
        public required MixerApp App;
        public required Border Root;
        public required Button MuteButton;
        public required Slider Slider;
        public required TextBlock Value;
        public required Button Star;
    }

    readonly App _app;
    readonly AudioService _audio = new();
    readonly DispatcherQueueTimer _poll;
    readonly Dictionary<string, Strip> _strips = new();
    List<AudioDevice> _outputs = new(), _inputs = new();
    string _stripKey = "";
    bool _updating;

    public string Title => "Audio";
    public string? YHint => "Favourite";

    public AudioView(App app)
    {
        _app = app;
        InitializeComponent();
        _poll = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(1);
        _poll.Tick += (_, _) => Refresh(devices: false);
    }

    public void OnShown()
    {
        Refresh(devices: true);
        _poll.Start();
    }

    public void OnHidden() => _poll.Stop();

    // ---------------- master ----------------

    void Refresh(bool devices)
    {
        _updating = true;
        try
        {
            if (_audio.Endpoint(DataFlow.Render) is { } spk)
            {
                Tiles.Set(SpeakerTile, spk.muted ? "" : "", "Speakers", spk.muted ? "Muted" : "On", active: !spk.muted);
                if (OutSlider.FocusState == FocusState.Unfocused) OutSlider.Value = Math.Round(spk.volume * 100);
                OutValue.Text = $"{Math.Round(spk.volume * 100)}";
            }
            else Tiles.Set(SpeakerTile, "", "Speakers", "None");

            if (_audio.Endpoint(DataFlow.Capture) is { } mic)
            {
                Tiles.Set(MicTile, mic.muted ? "" : "", "Mic", mic.muted ? "Off" : "On", active: !mic.muted);
                if (MicSlider.FocusState == FocusState.Unfocused) MicSlider.Value = Math.Round(mic.volume * 100);
                MicValue.Text = $"{Math.Round(mic.volume * 100)}";
            }
            else Tiles.Set(MicTile, "", "Mic", "None");

            if (devices) RefreshDevices();
            RefreshMixer();
        }
        catch (Exception ex) { Log.Error("audio refresh", ex); }
        finally { _updating = false; }
    }

    void RefreshDevices()
    {
        _outputs = _audio.Devices(DataFlow.Render);
        _inputs = _audio.Devices(DataFlow.Capture);
        Fill(OutputBox, _outputs, _audio.DefaultId(DataFlow.Render));
        Fill(InputBox, _inputs, _audio.DefaultId(DataFlow.Capture));
    }

    static void Fill(ComboBox box, List<AudioDevice> devices, string? selectedId)
    {
        box.Items.Clear();
        foreach (var d in devices) box.Items.Add(d.Name);
        box.SelectedIndex = devices.FindIndex(d => d.Id == selectedId);
    }

    void Speaker_Click(object sender, RoutedEventArgs e)
    {
        if (_audio.Endpoint(DataFlow.Render) is { } s) _audio.SetMute(DataFlow.Render, !s.muted);
        Refresh(devices: false);
    }

    void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (_audio.Endpoint(DataFlow.Capture) is { } m) _audio.SetMute(DataFlow.Capture, !m.muted);
        Refresh(devices: false);
    }

    void Out_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        _audio.SetVolume(DataFlow.Render, (float)(e.NewValue / 100));
        OutValue.Text = $"{Math.Round(e.NewValue)}";
    }

    void Mic_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        _audio.SetVolume(DataFlow.Capture, (float)(e.NewValue / 100));
        MicValue.Text = $"{Math.Round(e.NewValue)}";
    }

    // Switch the default only once the list closes, not on every step through it.
    void Output_Closed(object sender, object e) => Switch(OutputBox, _outputs, DataFlow.Render);
    void Input_Closed(object sender, object e) => Switch(InputBox, _inputs, DataFlow.Capture);

    void Switch(ComboBox box, List<AudioDevice> list, DataFlow flow)
    {
        int i = box.SelectedIndex;
        if (i < 0 || i >= list.Count || list[i].Id == _audio.DefaultId(flow)) return;
        _audio.SetDefault(list[i].Id);
        Refresh(devices: false);
    }

    void Manage_Click(object sender, RoutedEventArgs e)
    {
        _app.Shell.Hide(restoreFocus: false);
        Launch.Open("ms-settings:sound");
    }

    // ---------------- mixer ----------------

    void RefreshMixer()
    {
        var favs = _app.Settings.FavoriteApps;
        var apps = _audio.Mixer()
            .OrderByDescending(a => favs.Contains(a.Key, StringComparer.OrdinalIgnoreCase))
            .ThenByDescending(a => a.IsSystem)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        NoAppsText.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var key = string.Join("|", apps.Select(a => a.Key + (favs.Contains(a.Key, StringComparer.OrdinalIgnoreCase) ? "*" : "")));
        if (key != _stripKey)
        {
            Rebuild(apps);
            _stripKey = key;
            return;
        }
        foreach (var a in apps)
        {
            if (!_strips.TryGetValue(a.Key, out var s)) continue;
            s.App = a;
            if (s.Slider.FocusState == FocusState.Unfocused) s.Slider.Value = Math.Round(a.Volume * 100);
            UpdateStrip(s);
        }
    }

    void Rebuild(List<MixerApp> apps)
    {
        // Keep focus on the same app across the rebuild.
        var focusedKey = _strips.Values.FirstOrDefault(s => s.Slider.FocusState != FocusState.Unfocused ||
                                                            s.MuteButton.FocusState != FocusState.Unfocused ||
                                                            s.Star.FocusState != FocusState.Unfocused)?.App.Key;
        Strips.Children.Clear();
        _strips.Clear();
        foreach (var a in apps)
        {
            var s = MakeStrip(a);
            _strips[a.Key] = s;
            Strips.Children.Add(s.Root);
        }
        if (focusedKey != null && _strips.TryGetValue(focusedKey, out var again))
            again.Slider.Focus(FocusState.Keyboard);
    }

    Strip MakeStrip(MixerApp app)
    {
        var res = Application.Current.Resources;
        var mute = new Button { Style = (Style)res["IconButtonStyle"], Width = 60, Height = 60, Background = (Brush)res["IconCircleBrush"] };
        if (app.IsSystem) mute.Content = new FontIcon { Glyph = "", FontSize = 24 };
        else _ = SetIconAsync(mute, app.ExePath);

        var slider = new Slider
        {
            Style = (Style)res["NexusSliderStyle"], Orientation = Orientation.Vertical,
            Minimum = 0, Maximum = 100, SmallChange = 2, Height = 190,
            HorizontalAlignment = HorizontalAlignment.Center, Value = Math.Round(app.Volume * 100),
        };
        var value = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var name = new TextBlock
        {
            Text = app.Name, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Height = 36, Foreground = (Brush)res["SubtleTextBrush"],
        };
        var star = new Button { Style = (Style)res["IconButtonStyle"], Width = 40, Height = 40 };

        var root = new Border
        {
            Style = (Style)res["GlassSurface"], Width = 124, CornerRadius = new CornerRadius(30), Padding = new Thickness(10, 14, 10, 10),
            Child = new StackPanel
            {
                Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
                Children = { mute, slider, value, name, star },
            },
        };
        var strip = new Strip { App = app, Root = root, MuteButton = mute, Slider = slider, Value = value, Star = star };

        slider.ValueChanged += (_, e) =>
        {
            if (_updating) return;
            AudioService.SetAppVolume(strip.App, (float)(e.NewValue / 100));
            UpdateStrip(strip);
        };
        mute.Click += (_, _) => { AudioService.SetAppMute(strip.App, !strip.App.Muted); UpdateStrip(strip); };
        star.Click += (_, _) => ToggleFavorite(strip.App.Key);
        UpdateStrip(strip);
        return strip;
    }

    void UpdateStrip(Strip s)
    {
        bool fav = _app.Settings.FavoriteApps.Contains(s.App.Key, StringComparer.OrdinalIgnoreCase);
        s.Value.Text = s.App.Muted ? "Muted" : $"{Math.Round(s.App.Volume * 100)}";
        s.MuteButton.Opacity = s.App.Muted ? 0.45 : 1;
        s.Star.Content = new FontIcon { Glyph = fav ? "" : "", FontSize = 18, Foreground = fav ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 204, 64)) : null };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(s.Slider, $"{s.App.Name} volume");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(s.MuteButton, $"Mute {s.App.Name}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(s.Star, fav ? $"Unfavourite {s.App.Name}" : $"Favourite {s.App.Name}");
    }

    static async Task SetIconAsync(Button b, string? exe)
    {
        if (await Icons.LoadAsync(exe, 96) is { } img)
            b.Content = new Image { Source = img, Width = 38, Height = 38 };
        else
            b.Content = new FontIcon { Glyph = "", FontSize = 24 };
    }

    void ToggleFavorite(string key)
    {
        var favs = _app.Settings.FavoriteApps;
        var existing = favs.FirstOrDefault(f => f.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing != null) favs.Remove(existing); else favs.Add(key);
        _app.Settings.Save();
        RefreshMixer();
    }

    /// <summary>Y on any part of a strip favourites that app.</summary>
    public bool HandleY(DependencyObject? focused)
    {
        for (var d = focused; d != null; d = VisualTreeHelper.GetParent(d))
        {
            var strip = _strips.Values.FirstOrDefault(s => ReferenceEquals(s.Root, d));
            if (strip != null) { ToggleFavorite(strip.App.Key); return true; }
        }
        return false;
    }
}
