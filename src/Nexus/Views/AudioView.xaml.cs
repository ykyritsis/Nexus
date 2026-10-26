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
/// Audio tab: output and input cards (device, mute, level) like Settings > Sound, and a volume mixer
/// list like Windows' own, with favourites pinned to the top (Y or the star).
/// </summary>
public sealed partial class AudioView : UserControl, INexusView
{
    sealed class Row
    {
        public required MixerApp App;
        public required Grid Root;
        public required Button Mute;
        public required Slider Slider;
        public required TextBlock Value;
        public required Button Star;
    }

    readonly App _app;
    readonly AudioService _audio = new();
    readonly DispatcherQueueTimer _poll;
    readonly Dictionary<string, Row> _rows = new();
    List<AudioDevice> _outputs = new(), _inputs = new();
    string _rowKey = "";
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

    // ---------------- output / input ----------------

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
        if (key != _rowKey)
        {
            Rebuild(apps);
            _rowKey = key;
            return;
        }
        foreach (var a in apps)
        {
            if (!_rows.TryGetValue(a.Key, out var r)) continue;
            r.App = a;
            if (r.Slider.FocusState == FocusState.Unfocused) r.Slider.Value = Math.Round(a.Volume * 100);
            Update(r);
        }
    }

    void Rebuild(List<MixerApp> apps)
    {
        // Keep focus on the same app across the rebuild.
        var focusedKey = _rows.Values.FirstOrDefault(r => r.Slider.FocusState != FocusState.Unfocused ||
                                                          r.Mute.FocusState != FocusState.Unfocused ||
                                                          r.Star.FocusState != FocusState.Unfocused)?.App.Key;
        Rows.Children.Clear();
        _rows.Clear();
        foreach (var a in apps)
        {
            var r = MakeRow(a);
            _rows[a.Key] = r;
            Rows.Children.Add(r.Root);
        }
        if (focusedKey != null && _rows.TryGetValue(focusedKey, out var again))
            again.Slider.Focus(FocusState.Keyboard);
    }

    Row MakeRow(MixerApp app)
    {
        var res = Application.Current.Resources;
        var mute = new Button { Style = (Style)res["IconButtonStyle"], Width = 44, Height = 44 };
        if (app.IsSystem) mute.Content = new FontIcon { Glyph = "", FontSize = 20 };
        else _ = SetIconAsync(mute, app.ExePath);

        var name = new TextBlock { Text = app.Name, Style = (Style)res["BodyTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 2, VerticalAlignment = VerticalAlignment.Center, Value = Math.Round(app.Volume * 100) };
        var value = new TextBlock { Style = (Style)res["BodyTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        var star = new Button { Style = (Style)res["IconButtonStyle"] };

        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        foreach (var w in new[] { GridLength.Auto, new GridLength(170), new GridLength(1, GridUnitType.Star), new GridLength(44), GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        void Put(FrameworkElement el, int col) { Grid.SetColumn(el, col); grid.Children.Add(el); }
        Put(mute, 0); Put(name, 1); Put(slider, 2); Put(value, 3); Put(star, 4);

        var row = new Row { App = app, Root = grid, Mute = mute, Slider = slider, Value = value, Star = star };
        slider.ValueChanged += (_, e) =>
        {
            if (_updating) return;
            AudioService.SetAppVolume(row.App, (float)(e.NewValue / 100));
            Update(row);
        };
        mute.Click += (_, _) => { AudioService.SetAppMute(row.App, !row.App.Muted); Update(row); };
        star.Click += (_, _) => ToggleFavourite(row.App.Key);
        Update(row);
        return row;
    }

    void Update(Row r)
    {
        bool fav = IsFavourite(r.App.Key);
        r.Value.Text = r.App.Muted ? "Off" : $"{Math.Round(r.App.Volume * 100)}";
        r.Mute.Opacity = r.App.Muted ? 0.4 : 1;
        r.Star.Content = new FontIcon
        {
            Glyph = fav ? "" : "", FontSize = 16,
            Foreground = fav ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] : null,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(r.Slider, $"{r.App.Name} volume");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(r.Mute, $"Mute {r.App.Name}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(r.Star, fav ? $"Unfavourite {r.App.Name}" : $"Favourite {r.App.Name}");
    }

    static async Task SetIconAsync(Button b, string? exe)
    {
        if (await Icons.LoadAsync(exe, 64) is { } img)
            b.Content = new Image { Source = img, Width = 28, Height = 28 };
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

    /// <summary>Y anywhere on a mixer row favourites that app.</summary>
    public bool HandleY(DependencyObject? focused)
    {
        for (var d = focused; d != null; d = VisualTreeHelper.GetParent(d))
        {
            var row = _rows.Values.FirstOrDefault(r => ReferenceEquals(r.Root, d));
            if (row != null) { ToggleFavourite(row.App.Key); return true; }
        }
        return false;
    }
}
