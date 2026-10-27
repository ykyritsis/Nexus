using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Nexus.Providers;
using Nexus.Services;
using Windows.UI;

namespace Nexus.Views;

/// <summary>
/// Widgets tab: four live glass cards that fit the panel without scrolling, each with its own
/// colour. Performance (ring gauges for CPU, GPU, RAM and VRAM, and a minute of CPU/GPU history),
/// Capture (screenshot, last 30 s, record, and the latest captures), Display (borderless sizes,
/// placement, Lossless Scaling, HDR) and Resources (the apps using the most memory). Everything
/// samples only while this tab is on screen.
/// </summary>
public sealed class WidgetsView : UserControl, INexusView
{
    static readonly Color Cyan = Color.FromArgb(255, 0x22, 0xD3, 0xEE), Purple = Color.FromArgb(255, 0xA8, 0x55, 0xF7),
        Amber = Color.FromArgb(255, 0xF5, 0x9E, 0x0B), Green = Color.FromArgb(255, 0x3D, 0xDC, 0x84), Red = Color.FromArgb(255, 0xFF, 0x5A, 0x5F);

    readonly App _app;
    readonly ShellWindow _shell;
    readonly DispatcherQueueTimer _perfTimer, _appsTimer;
    PerfMonitor? _perf;
    readonly TopApps _top = new();

    // live bits
    readonly (ProgressRing ring, TextBlock value, TextBlock detail)[] _gauges = new (ProgressRing, TextBlock, TextBlock)[4];
    readonly Polyline _cpuLine = new() { Stroke = new SolidColorBrush(Cyan), StrokeThickness = 2 };
    readonly Polyline _gpuLine = new() { Stroke = new SolidColorBrush(Purple), StrokeThickness = 2 };
    readonly Queue<double> _cpuHistory = new(), _gpuHistory = new();
    const int HistoryLength = 60;
    const double SparkWidth = 256, SparkHeight = 34;
    readonly StackPanel _gallery = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    readonly StackPanel _apps = new() { Spacing = 7 };
    Button? _placementButton, _frameGenButton;

    public string Title => "Widgets";

    static ResourceDictionary Res => Application.Current.Resources;

    public WidgetsView(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        var row = new Grid { ColumnSpacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        var cards = new[]
        {
            Card("Performance", "", Cyan, 300, PerformanceBody()),
            Card("Capture", "", Purple, 258, CaptureBody()),
            Card("Display", "", Amber, 282, DisplayBody()),
            Card("Resources", "", Green, 250, _apps),
        };
        for (int i = 0; i < cards.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(cards[i], i);
            row.Children.Add(cards[i]);
        }
        Content = row;

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

    static Border Card(string title, string glyph, Color colour, double width, UIElement body)
    {
        var badge = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(0x38, colour.R, colour.G, colour.B)),
            Child = new FontIcon { Glyph = glyph, FontSize = 13, Foreground = new SolidColorBrush(colour) },
        };
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10),
            Children = { badge, new TextBlock { Text = title, Style = (Style)Res["BodyStrongTextBlockStyle"], FontSize = 14, VerticalAlignment = VerticalAlignment.Center } },
        };
        return new Border
        {
            Style = (Style)Res["GlassSurface"], CornerRadius = new CornerRadius(24), Padding = new Thickness(16, 12, 16, 12),
            Width = width, VerticalAlignment = VerticalAlignment.Stretch, Child = new StackPanel { Children = { header, body } },
        };
    }

    static Button Pill(string glyph, string text, Action click, Color? tint = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 14 };
        if (tint is { } t) icon.Foreground = new SolidColorBrush(t);
        var b = new Button
        {
            Style = (Style)Res["GlassButtonStyle"], MinHeight = 34, Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, new TextBlock { Text = text, FontSize = 13 } } },
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
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        string[] labels = { "CPU", "GPU", "RAM", "VRAM" };
        Color[] colours = { Cyan, Purple, Green, Amber };
        for (int i = 0; i < 4; i++)
        {
            var ring = new ProgressRing
            {
                IsIndeterminate = false, Maximum = 100, Width = 56, Height = 56,
                Foreground = new SolidColorBrush(colours[i]), Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            };
            var value = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var detail = new TextBlock { FontSize = 10, Foreground = (Brush)Res["SubtleTextBrush"], HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
            var cell = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new Grid { Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center, Children = { ring, value } },
                    new TextBlock { Text = labels[i], FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    detail,
                },
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
            _gauges[i] = (ring, value, detail);
        }

        // A minute of CPU (cyan) and GPU (purple) history.
        var spark = new Canvas { Width = SparkWidth, Height = SparkHeight, Children = { _cpuLine, _gpuLine } };
        var history = new Border
        {
            Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(6), CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), Child = spark,
        };
        var legend = new TextBlock { Text = "Last minute · CPU · GPU", FontSize = 10, Foreground = (Brush)Res["SubtleTextBrush"], Margin = new Thickness(2, 4, 0, 0) };
        return new StackPanel { Children = { grid, history, legend } };
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
        Set(0, s.Cpu, $"{Environment.ProcessorCount} threads");
        Set(1, s.Gpu, "3D load");
        Set(2, s.RamUsedGb / Math.Max(1, s.RamTotalGb) * 100, $"{s.RamUsedGb:0.0}/{s.RamTotalGb:0} GB");
        Set(3, s.VramTotalGb > 0 ? s.VramUsedGb / s.VramTotalGb * 100 : 0, s.VramTotalGb > 0 ? $"{s.VramUsedGb:0.0}/{s.VramTotalGb:0} GB" : $"{s.VramUsedGb:0.0} GB");
        Push(_cpuHistory, s.Cpu, _cpuLine);
        Push(_gpuHistory, s.Gpu, _gpuLine);

        void Set(int i, double pct, string detail)
        {
            pct = Math.Clamp(pct, 0, 100);
            _gauges[i].ring.Value = pct;
            _gauges[i].value.Text = $"{pct:0}%";
            _gauges[i].detail.Text = detail;
        }
    }

    static void Push(Queue<double> history, double value, Polyline line)
    {
        history.Enqueue(Math.Clamp(value, 0, 100));
        while (history.Count > HistoryLength) history.Dequeue();
        var points = new PointCollection();
        int i = HistoryLength - history.Count;
        foreach (var v in history)
            points.Add(new Windows.Foundation.Point(i++ * SparkWidth / (HistoryLength - 1), SparkHeight - v / 100 * SparkHeight));
        line.Points = points;
    }

    // ---------------- Capture ----------------

    UIElement CaptureBody()
    {
        var buttons = new Grid { ColumnSpacing = 6 };
        for (int i = 0; i < 3; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var shot = RoundAction("", "Screenshot", Purple, () => ForGame(Captures.Screenshot));
        _clipButton = RoundAction("", "Last 30 s", Red, () => ForGame(Captures.RecordThat));
        var record = RoundAction("", "Record", Red, () => ForGame(Captures.ToggleRecording));
        Grid.SetColumn(_clipButton, 1);
        Grid.SetColumn(record, 2);
        buttons.Children.Add(shot);
        buttons.Children.Add(_clipButton);
        buttons.Children.Add(record);

        _recordHint = new HyperlinkButton { Content = "Turn on background recording", Padding = new Thickness(4, 2, 4, 2), Foreground = (Brush)Res["SubtleTextBrush"], FontSize = 11 };
        _recordHint.Click += (_, _) => { _shell.Hide(restoreFocus: false); Launch.Open("ms-settings:gaming-gamedvr"); };
        var recent = new TextBlock { Text = "Recent", FontSize = 11, Foreground = (Brush)Res["SubtleTextBrush"], Margin = new Thickness(2, 10, 0, 4) };
        var open = Pill("", "Open captures", () => { _shell.Hide(restoreFocus: false); Launch.Open(Captures.Folder); });
        open.Margin = new Thickness(0, 8, 0, 0);
        UpdateCapture();
        return new StackPanel { Children = { buttons, _recordHint, recent, _gallery, open } };
    }

    /// <summary>A big round glass button with a coloured icon and a label under it.</summary>
    static Button RoundAction(string glyph, string label, Color colour, Action click)
    {
        var b = new Button
        {
            Style = (Style)Res["IconButtonStyle"], Width = 72, Height = 74, CornerRadius = new CornerRadius(18), HorizontalAlignment = HorizontalAlignment.Center,
            Content = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new Border
                    {
                        Width = 44, Height = 44, CornerRadius = new CornerRadius(22), HorizontalAlignment = HorizontalAlignment.Center,
                        Background = new SolidColorBrush(Color.FromArgb(0x33, colour.R, colour.G, colour.B)),
                        BorderBrush = (Brush)Res["GlassRim"], BorderThickness = new Thickness(1),
                        Child = new FontIcon { Glyph = glyph, FontSize = 18, Foreground = new SolidColorBrush(colour) },
                    },
                    new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, label);
        b.Click += (_, _) => click();
        return b;
    }

    Button? _clipButton;
    HyperlinkButton? _recordHint;

    /// <summary>"Last 30 s" needs background recording; re-checked each time, as it can be turned on in Settings.</summary>
    void UpdateCapture()
    {
        bool on = Captures.BackgroundRecording;
        if (_clipButton != null) { _clipButton.IsEnabled = on; _clipButton.Opacity = on ? 1 : 0.45; }
        if (_recordHint != null) _recordHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    async Task UpdateGalleryAsync()
    {
        var files = await Task.Run(() => Captures.Latest(3));
        _gallery.Children.Clear();
        if (files.Count == 0)
        {
            _gallery.Children.Add(new TextBlock { Text = "Your captures show up here.", FontSize = 11, Foreground = (Brush)Res["SubtleTextBrush"] });
            return;
        }
        foreach (var f in files)
        {
            var thumb = new Border { Width = 70, Height = 40, CornerRadius = new CornerRadius(8), Background = (Brush)Res["IconCircleBrush"] };
            if (await Icons.LoadAsync(f.FullName, 160) is { } img)
                thumb.Background = new ImageBrush { ImageSource = img, Stretch = Stretch.UniformToFill };
            if (f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                thumb.Child = new FontIcon { Glyph = "", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Button { Style = (Style)Res["IconButtonStyle"], Width = 70, Height = 40, CornerRadius = new CornerRadius(8), Content = thumb };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, f.Name);
            var path = f.FullName;
            b.Click += (_, _) => { _shell.Hide(restoreFocus: false); Launch.Open(path); };
            _gallery.Children.Add(b);
        }
    }

    // ---------------- Display ----------------

    UIElement DisplayBody()
    {
        var panel = new StackPanel { Spacing = 6 };
        if (DisplayControls.HasBorderless)
        {
            panel.Children.Add(new TextBlock { Text = "Borderless", FontSize = 11, Foreground = (Brush)Res["SubtleTextBrush"], Margin = new Thickness(2, 0, 0, 0) });
            var sizes = new Grid { ColumnSpacing = 6 };
            for (int i = 0; i < DisplayControls.Sizes.Length; i++)
            {
                var (w, h) = DisplayControls.Sizes[i];
                sizes.ColumnDefinitions.Add(new ColumnDefinition());
                var b = new Button
                {
                    Style = (Style)Res["GlassButtonStyle"], MinHeight = 34, Padding = new Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new TextBlock { Text = h.ToString(), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Amber) },
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
            }, Amber);
            panel.Children.Add(_placementButton);
        }
        if (DisplayControls.HasLossless)
        {
            var ls = new Grid { ColumnSpacing = 6 };
            ls.ColumnDefinitions.Add(new ColumnDefinition());
            ls.ColumnDefinitions.Add(new ColumnDefinition());
            ls.Children.Add(Pill("", "Lossless", () => ForGame(DisplayControls.ToggleLossless), Amber));
            if (DisplayControls.HasLsMode)
            {
                _frameGenButton = Pill("", "FG 2×", () =>
                {
                    DisplayControls.CycleFrameGen();
                    DispatcherQueue.GetForCurrentThread().TryEnqueue(async () => { await Task.Delay(1800); UpdateDisplayLabels(); });
                }, Amber);
                Grid.SetColumn(_frameGenButton, 1);
                ls.Children.Add(_frameGenButton);
            }
            panel.Children.Add(ls);
        }
        panel.Children.Add(Pill("", "HDR on / off", () => ForGame(DisplayControls.ToggleHdr), Amber));
        return panel;
    }

    void UpdateDisplayLabels()
    {
        if (_placementButton != null) SetPillText(_placementButton, $"Placement: {DisplayControls.Placement}");
        if (_frameGenButton != null) SetPillText(_frameGenButton, $"FG {DisplayControls.FrameGenMode}");
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
        double most = apps.Count > 0 ? Math.Max(0.1, apps.Max(a => a.RamGb)) : 1;
        for (int i = 0; i < apps.Count; i++)
        {
            var row = (Grid)_apps.Children[i];
            var a = apps[i];
            var icon = (Image)row.Children[0];
            if (icon.Tag as string != a.ExePath) { icon.Tag = a.ExePath; icon.Source = null; _ = SetIconAsync(icon, a.ExePath); }
            var text = (StackPanel)row.Children[1];
            ((TextBlock)((Grid)text.Children[0]).Children[0]).Text = a.Name;
            ((TextBlock)((Grid)text.Children[0]).Children[1]).Text = $"{a.RamGb:0.0} GB · {a.Cpu:0}%";
            var bar = (Border)((Border)text.Children[1]).Child;
            bar.Width = Math.Max(4, 170 * a.RamGb / most);
        }
    }

    static Grid AppRow()
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        var line = new Grid();
        line.Children.Add(new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 0, 70, 0) });
        line.Children.Add(new TextBlock { FontSize = 11, Foreground = (Brush)Res["SubtleTextBrush"], HorizontalAlignment = HorizontalAlignment.Right });
        // How much memory, relative to the hungriest app.
        var track = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 3, 0, 0), Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            Child = new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Green), HorizontalAlignment = HorizontalAlignment.Left, Width = 4 },
        };
        var text = new StackPanel { Children = { line, track } };
        Grid.SetColumn(text, 1);
        g.Children.Add(icon);
        g.Children.Add(text);
        return g;
    }

    static async Task SetIconAsync(Image img, string? exe)
    {
        if (await Icons.LoadAsync(exe, 48) is { } src) img.Source = src;
    }
}
