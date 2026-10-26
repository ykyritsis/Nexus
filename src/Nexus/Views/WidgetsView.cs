using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.Providers;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>
/// Widgets tab: a row of live glass cards. Performance (CPU, GPU, RAM, VRAM), Capture, Display
/// (borderless sizes, Lossless Scaling, HDR), Gallery (latest captures) and Resources (apps using
/// the most memory). Everything samples only while this tab is on screen.
/// </summary>
public sealed class WidgetsView : UserControl, INexusView
{
    readonly App _app;
    readonly ShellWindow _shell;
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 16 };
    readonly DispatcherQueueTimer _perfTimer, _appsTimer;
    PerfMonitor? _perf;
    readonly TopApps _top = new();

    // live bits
    readonly (TextBlock value, ProgressBar bar)[] _meters = new (TextBlock, ProgressBar)[4];
    readonly StackPanel _gallery = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    readonly StackPanel _apps = new() { Spacing = 6 };
    Button? _placementButton, _frameGenButton;

    public string Title => "Widgets";

    static ResourceDictionary Res => Application.Current.Resources;

    public WidgetsView(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        _row.Children.Add(Card("Performance", "", 240, PerformanceBody()));
        _row.Children.Add(Card("Capture", "", 220, CaptureBody()));
        _row.Children.Add(Card("Display", "", 300, DisplayBody()));
        _row.Children.Add(Card("Gallery", "", 286, GalleryBody()));
        _row.Children.Add(Card("Resources", "", 268, _apps));
        Content = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(4), Content = _row,
        };

        var q = DispatcherQueue.GetForCurrentThread();
        _perfTimer = q.CreateTimer();
        _perfTimer.Interval = TimeSpan.FromSeconds(1);
        _perfTimer.Tick += (_, _) => UpdatePerf();
        _appsTimer = q.CreateTimer();
        _appsTimer.Interval = TimeSpan.FromSeconds(2.5);
        _appsTimer.Tick += (_, _) => UpdateApps();
    }

    public void OnShown()
    {
        UpdatePerf();
        UpdateApps();
        _ = UpdateGalleryAsync();
        UpdateDisplayLabels();
        UpdateCapture();
        _perfTimer.Start();
        _appsTimer.Start();
    }

    public void OnHidden()
    {
        _perfTimer.Stop();
        _appsTimer.Stop();
    }

    // ---------------- building blocks ----------------

    static Border Card(string title, string glyph, double width, UIElement body)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 12),
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 16 },
                new TextBlock { Text = title, Style = (Style)Res["BodyStrongTextBlockStyle"] },
            },
        };
        return new Border
        {
            Style = (Style)Res["GlassSurface"], CornerRadius = new CornerRadius(26), Padding = new Thickness(18, 14, 18, 14),
            Width = width, Child = new StackPanel { Children = { header, body } },
        };
    }

    static Button Pill(string glyph, string text, Action click, bool stretch = true)
    {
        var b = new Button
        {
            Style = (Style)Res["GlassButtonStyle"], MinHeight = 40, Padding = new Thickness(14, 0, 14, 0),
            HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { new FontIcon { Glyph = glyph, FontSize = 15 }, new TextBlock { Text = text } },
            },
        };
        b.Click += (_, _) => click();
        return b;
    }

    static void SetPillText(Button b, string text) => ((TextBlock)((StackPanel)b.Content).Children[1]).Text = text;

    /// <summary>Gets out of the game's way, then runs an action aimed at it.</summary>
    async void ForGame(Action action)
    {
        _shell.Hide();
        await Task.Delay(450);
        action();
    }

    // ---------------- Performance ----------------

    UIElement PerformanceBody()
    {
        var panel = new StackPanel { Spacing = 12 };
        string[] labels = { "CPU", "GPU", "RAM", "VRAM" };
        for (int i = 0; i < 4; i++)
        {
            var value = new TextBlock { Style = (Style)Res["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Right };
            var bar = new ProgressBar { Maximum = 100, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 4, 0, 0) };
            var top = new Grid { Children = { new TextBlock { Text = labels[i], Style = (Style)Res["BodyTextBlockStyle"] }, value } };
            panel.Children.Add(new StackPanel { Children = { top, bar } });
            _meters[i] = (value, bar);
        }
        return panel;
    }

    bool _sampling, _listing;

    /// <summary>Counters are read on a worker thread (opening them and collecting can take a while).</summary>
    async void UpdatePerf()
    {
        if (_sampling) return;
        _sampling = true;
        PerfSample s;
        try
        {
            _perf ??= await Task.Run(() => new PerfMonitor());
            s = await Task.Run(_perf.Sample);
        }
        finally { _sampling = false; }
        Set(0, $"{s.Cpu:0}%", s.Cpu);
        Set(1, $"{s.Gpu:0}%", s.Gpu);
        Set(2, $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0} GB", s.RamUsedGb / Math.Max(1, s.RamTotalGb) * 100);
        Set(3, s.VramTotalGb > 0 ? $"{s.VramUsedGb:0.0} / {s.VramTotalGb:0} GB" : $"{s.VramUsedGb:0.0} GB",
            s.VramTotalGb > 0 ? s.VramUsedGb / s.VramTotalGb * 100 : 0);

        void Set(int i, string text, double pct)
        {
            _meters[i].value.Text = text;
            _meters[i].bar.Value = Math.Clamp(pct, 0, 100);
        }
    }

    // ---------------- Capture ----------------

    UIElement CaptureBody()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Pill("", "Screenshot", () => ForGame(Captures.Screenshot)));
        var clip = Pill("", "Record last 30 s", () => ForGame(Captures.RecordThat));
        panel.Children.Add(clip);
        panel.Children.Add(Pill("", "Record / stop", () => ForGame(Captures.ToggleRecording)));
        _recordHint = new HyperlinkButton { Content = "Turn on background recording", Padding = new Thickness(4, 2, 4, 2), Foreground = (Brush)Res["SubtleTextBrush"], FontSize = 12 };
        _recordHint.Click += (_, _) => { _shell.Hide(restoreFocus: false); Launch.Open("ms-settings:gaming-gamedvr"); };
        panel.Children.Add(_recordHint);
        _clipButton = clip;
        UpdateCapture();
        return panel;
    }

    Button? _clipButton;
    HyperlinkButton? _recordHint;

    /// <summary>"Record last 30 s" needs background recording; re-checked each time, as it can be turned on in Settings.</summary>
    void UpdateCapture()
    {
        bool on = Captures.BackgroundRecording;
        if (_clipButton != null) _clipButton.IsEnabled = on;
        if (_recordHint != null) _recordHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------- Display ----------------

    UIElement DisplayBody()
    {
        var panel = new StackPanel { Spacing = 8 };
        if (DisplayControls.HasBorderless)
        {
            panel.Children.Add(new TextBlock { Text = "Borderless", Style = (Style)Res["CaptionTextBlockStyle"], Foreground = (Brush)Res["SubtleTextBrush"] });
            var sizes = new Grid { ColumnSpacing = 6 };
            for (int i = 0; i < DisplayControls.Sizes.Length; i++)
            {
                var (w, h) = DisplayControls.Sizes[i];
                sizes.ColumnDefinitions.Add(new ColumnDefinition());
                var b = new Button
                {
                    Style = (Style)Res["GlassButtonStyle"], MinHeight = 38, Padding = new Thickness(4, 0, 4, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch, Content = h.ToString(),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"Borderless {w} by {h}");
                b.Click += (_, _) => ForGame(() => DisplayControls.SetBorderless(w, h));
                Grid.SetColumn(b, i);
                sizes.Children.Add(b);
            }
            panel.Children.Add(sizes);
            _placementButton = Pill("", "Placement: Centre", () =>
            {
                DisplayControls.CyclePlacement();
                DispatcherQueue.GetForCurrentThread().TryEnqueue(async () => { await Task.Delay(1200); UpdateDisplayLabels(); });
            });
            panel.Children.Add(_placementButton);
        }
        if (DisplayControls.HasLossless)
        {
            var ls = new Grid { ColumnSpacing = 6 };
            ls.ColumnDefinitions.Add(new ColumnDefinition());
            ls.ColumnDefinitions.Add(new ColumnDefinition());
            var scale = Pill("", "Lossless", () => ForGame(DisplayControls.ToggleLossless));
            ls.Children.Add(scale);
            if (DisplayControls.HasLsMode)
            {
                _frameGenButton = Pill("", "2×", () =>
                {
                    DisplayControls.CycleFrameGen();
                    DispatcherQueue.GetForCurrentThread().TryEnqueue(async () => { await Task.Delay(1800); UpdateDisplayLabels(); });
                });
                Grid.SetColumn(_frameGenButton, 1);
                ls.Children.Add(_frameGenButton);
            }
            panel.Children.Add(ls);
        }
        panel.Children.Add(Pill("", "HDR on / off", () => ForGame(DisplayControls.ToggleHdr)));
        return panel;
    }

    void UpdateDisplayLabels()
    {
        if (_placementButton != null) SetPillText(_placementButton, $"Placement: {DisplayControls.Placement}");
        if (_frameGenButton != null) SetPillText(_frameGenButton, $"FG {DisplayControls.FrameGenMode}");
    }

    // ---------------- Gallery ----------------

    UIElement GalleryBody()
    {
        var open = Pill("", "Open captures", () => { _shell.Hide(restoreFocus: false); Launch.Open(Captures.Folder); });
        return new StackPanel { Spacing = 10, Children = { _gallery, open } };
    }

    async Task UpdateGalleryAsync()
    {
        var files = await Task.Run(() => Captures.Latest(3));
        _gallery.Children.Clear();
        if (files.Count == 0)
        {
            _gallery.Children.Add(new TextBlock { Text = "Screenshots and clips you capture show up here.", Style = (Style)Res["CaptionTextBlockStyle"], Foreground = (Brush)Res["SubtleTextBrush"], TextWrapping = TextWrapping.Wrap, Width = 240 });
            return;
        }
        foreach (var f in files)
        {
            var thumb = new Border { Width = 76, Height = 76, CornerRadius = new CornerRadius(14), Background = (Brush)Res["IconCircleBrush"] };
            if (await Icons.LoadAsync(f.FullName, 160) is { } img)
                thumb.Background = new ImageBrush { ImageSource = img, Stretch = Stretch.UniformToFill };
            if (f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                thumb.Child = new FontIcon { Glyph = "", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6) };
            var b = new Button { Style = (Style)Res["IconButtonStyle"], Width = 76, Height = 76, CornerRadius = new CornerRadius(14), Content = thumb };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, f.Name);
            var path = f.FullName;
            b.Click += (_, _) => { _shell.Hide(restoreFocus: false); Launch.Open(path); };
            _gallery.Children.Add(b);
        }
    }

    // ---------------- Resources ----------------

    /// <summary>Walking every process takes tens of milliseconds, so it runs on a worker thread.</summary>
    async void UpdateApps()
    {
        if (_listing) return;
        _listing = true;
        List<AppUsage> apps;
        try { apps = await Task.Run(() => _top.Sample(5)); }
        finally { _listing = false; }
        while (_apps.Children.Count < apps.Count) _apps.Children.Add(AppRow());
        while (_apps.Children.Count > apps.Count) _apps.Children.RemoveAt(_apps.Children.Count - 1);
        for (int i = 0; i < apps.Count; i++)
        {
            var row = (Grid)_apps.Children[i];
            var a = apps[i];
            var icon = (Image)row.Children[0];
            if (icon.Tag as string != a.ExePath) { icon.Tag = a.ExePath; icon.Source = null; _ = SetIconAsync(icon, a.ExePath); }
            ((TextBlock)row.Children[1]).Text = a.Name;
            ((TextBlock)row.Children[2]).Text = $"{a.RamGb:0.0} GB · {a.Cpu:0}%";
        }
    }

    static Grid AppRow()
    {
        var g = new Grid { ColumnSpacing = 10, Height = 30 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Style = (Style)Res["BodyTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var stats = new TextBlock { Style = (Style)Res["CaptionTextBlockStyle"], Foreground = (Brush)Res["SubtleTextBrush"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(stats, 2);
        g.Children.Add(icon);
        g.Children.Add(name);
        g.Children.Add(stats);
        return g;
    }

    static async Task SetIconAsync(Image img, string? exe)
    {
        if (await Icons.LoadAsync(exe, 48) is { } src) img.Source = src;
    }
}
