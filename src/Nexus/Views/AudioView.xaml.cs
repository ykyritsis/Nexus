using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;
using Nexus.Providers;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>
/// Audio tab: output and input on the left (device, mute, level), and every app
/// playing sound as a vertical glass channel strip on the right. Y favourites an app (pinned first).
/// </summary>
public sealed partial class AudioView : UserControl, INexusView
{
    sealed class Strip
    {
        public Meter Meter = null!;
        public required MixerApp App;
        public required Border Root;
        public required Button MuteButton;
        public required Slider Slider;
        public required TextBlock Value;
        public required Button Star;
    }

    readonly App _app;
    readonly AudioService _audio = new();
    readonly DispatcherQueueTimer _poll, _meters;
    readonly Meter? _outMeter;
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
        _meters = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _meters.Interval = TimeSpan.FromMilliseconds(50); // 20 fps, only while this tab is open
        _meters.Tick += (_, _) => UpdateMeters();
        // Output meter under the volume bar.
        _outMeter = MakeMeter(vertical: false, 300);
        _outMeter.Track.Margin = new Thickness(0, -2, 0, 4);
        _outMeter.Track.HorizontalAlignment = HorizontalAlignment.Center;
        var pill = (Border)((Grid)OutSlider.Parent).Parent;
        var stack = (StackPanel)pill.Parent;
        stack.Children.Insert(stack.Children.IndexOf(pill) + 1, _outMeter.Track);
    }

    public void OnShown()
    {
        Refresh(devices: true);
        _poll.Start();
        _meters.Start();
    }

    public void OnHidden() { _poll.Stop(); _meters.Stop(); }

    // ---------------- level meters ----------------

    /// <summary>A meter: a dim track with a fill that grows with the level (green, amber, red).</summary>
    public sealed class Meter
    {
        public required Border Track;
        public required Border Fill;
        public required bool Vertical;
        public required double Length;
        public double Shown;
    }

    static readonly Brush MeterBrush = MakeMeterBrush();

    static Brush MakeMeterBrush()
    {
        var b = new LinearGradientBrush { StartPoint = new(0, 1), EndPoint = new(0, 0) };
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x3D, 0xDC, 0x84), Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0xF5, 0xC2, 0x42), Offset = 0.72 });
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0xFF, 0x5A, 0x5F), Offset = 1 });
        return b;
    }

    static Meter MakeMeter(bool vertical, double length)
    {
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3), Background = vertical ? MeterBrush : HorizontalMeterBrush,
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            VerticalAlignment = vertical ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
        };
        if (vertical) fill.Height = 0; else fill.Width = 0;
        var track = new Border
        {
            CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            Width = vertical ? 6 : length, Height = vertical ? length : 4, Child = fill, VerticalAlignment = VerticalAlignment.Center,
        };
        return new Meter { Track = track, Fill = fill, Vertical = vertical, Length = length };
    }

    static readonly Brush HorizontalMeterBrush = MakeHorizontalMeterBrush();

    static Brush MakeHorizontalMeterBrush()
    {
        var b = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x3D, 0xDC, 0x84), Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0xF5, 0xC2, 0x42), Offset = 0.72 });
        b.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0xFF, 0x5A, 0x5F), Offset = 1 });
        return b;
    }

    /// <summary>Rises instantly, falls smoothly, like a mixing desk.</summary>
    static void Show(Meter m, float peak)
    {
        m.Shown = Math.Max(peak, m.Shown * 0.82);
        if (m.Shown < 0.004) m.Shown = 0;
        double size = Math.Round(m.Shown * m.Length);
        if (m.Vertical) m.Fill.Height = size; else m.Fill.Width = size;
    }

    void UpdateMeters()
    {
        try
        {
            if (_outMeter != null) Show(_outMeter, _audio.OutputPeak());
            foreach (var strip in _strips.Values) Show(strip.Meter, AudioService.Peak(strip.App));
        }
        catch { }
    }

    // ---------------- master ----------------

    void Refresh(bool devices)
    {
        _updating = true;
        try
        {
            if (_audio.Endpoint(DataFlow.Render) is { } spk)
            {
                SpeakerGlyph.Glyph = spk.muted ? "" : "";
                if (OutSlider.FocusState == FocusState.Unfocused) OutSlider.Value = Math.Round(spk.volume * 100);
                OutValue.Text = $"{Math.Round(spk.volume * 100)}";
            }

            if (_audio.Endpoint(DataFlow.Capture) is { } mic)
            {
                MicGlyph.Glyph = mic.muted ? "" : "";
                if (MicSlider.FocusState == FocusState.Unfocused) MicSlider.Value = Math.Round(mic.volume * 100);
                MicValue.Text = $"{Math.Round(mic.volume * 100)}";
            }

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

    bool IsFavourite(string key) => _app.Settings.FavoriteApps.Contains(key, StringComparer.OrdinalIgnoreCase);

    void RefreshMixer()
    {
        var apps = _audio.Mixer()
            .OrderByDescending(a => IsFavourite(a.Key))
            .ThenByDescending(a => a.IsSystem)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        NoAppsText.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var key = string.Join("|", apps.Select(a => a.Key + (IsFavourite(a.Key) ? "*" : "")));
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
        var mute = new Button { Style = (Style)res["IconButtonStyle"], Width = 42, Height = 42, Background = (Brush)res["IconCircleBrush"] };
        if (app.IsSystem) mute.Content = new FontIcon { Glyph = "", FontSize = 20 };
        else _ = SetIconAsync(mute, app.ExePath);

        var slider = new Slider
        {
            Style = (Style)res["NexusSliderStyle"], Orientation = Orientation.Vertical,
            Minimum = 0, Maximum = 100, SmallChange = 2, Height = 104,
            HorizontalAlignment = HorizontalAlignment.Center, Value = Math.Round(app.Volume * 100),
        };
        var value = new TextBlock { Style = (Style)res["BodyStrongTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center };
        var name = new TextBlock
        {
            Text = app.Name, Style = (Style)res["CaptionTextBlockStyle"], TextWrapping = TextWrapping.Wrap, MaxLines = 2,
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Height = 32,
            Foreground = (Brush)res["SubtleTextBrush"],
        };
        var star = new Button { Style = (Style)res["IconButtonStyle"], Width = 32, Height = 32 };

        // A live level meter beside the slider.
        var meter = MakeMeter(vertical: true, 104);
        var fader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Children = { slider, meter.Track } };
        var root = new Border
        {
            Style = (Style)res["GlassSurface"], Width = 104, CornerRadius = new CornerRadius(22), Padding = new Thickness(8, 10, 8, 6),
            Child = new StackPanel
            {
                Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center,
                Children = { mute, fader, value, name, star },
            },
        };
        var strip = new Strip { App = app, Root = root, MuteButton = mute, Slider = slider, Value = value, Star = star, Meter = meter };

        slider.ValueChanged += (_, e) =>
        {
            if (_updating) return;
            AudioService.SetAppVolume(strip.App, (float)(e.NewValue / 100));
            UpdateStrip(strip);
        };
        mute.Click += (_, _) => { AudioService.SetAppMute(strip.App, !strip.App.Muted); UpdateStrip(strip); };
        star.Click += (_, _) => ToggleFavourite(strip.App.Key);
        UpdateStrip(strip);
        return strip;
    }

    void UpdateStrip(Strip s)
    {
        bool fav = IsFavourite(s.App.Key);
        s.Value.Text = s.App.Muted ? "Muted" : $"{Math.Round(s.App.Volume * 100)}";
        s.MuteButton.Opacity = s.App.Muted ? 0.45 : 1;
        s.Star.Content = new FontIcon
        {
            Glyph = fav ? "" : "", FontSize = 16,
            Foreground = fav ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 210, 74)) : null,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(s.Slider, $"{s.App.Name} volume");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(s.MuteButton, $"Mute {s.App.Name}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(s.Star, fav ? $"Unfavourite {s.App.Name}" : $"Favourite {s.App.Name}");
    }

    static async Task SetIconAsync(Button b, string? exe)
    {
        if (await Icons.LoadAsync(exe, 96) is { } img)
            b.Content = new Image { Source = img, Width = 26, Height = 26 };
        else
            b.Content = new FontIcon { Glyph = "", FontSize = 20 };
    }

    void ToggleFavourite(string key)
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
            if (strip != null) { ToggleFavourite(strip.App.Key); return true; }
        }
        return false;
    }
}
