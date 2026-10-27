using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.Services;
using Windows.UI;

namespace Nexus.Views;

/// <summary>
/// First-run setup, also reachable from More settings: each item shows its real state (re-checked
/// every two seconds, so it updates when you come back from Windows or Steam settings) and has one
/// press that fixes it. Done when the Guide button belongs to Nexus.
/// </summary>
public sealed class OnboardingView : UserControl, INexusView
{
    sealed class Step
    {
        public required string Glyph, Title;
        public required Func<(bool ok, string detail)> Check;
        public required string Action;
        public required Func<Task> Fix;
        public TextBlock Detail = null!;
        public Border Badge = null!;
        public Button Button = null!;
    }

    readonly App _app;
    readonly List<Step> _steps;
    readonly DispatcherQueueTimer _poll;
    readonly TextBlock _summary;
    bool _busy;

    public string Title => "Set up Nexus";

    static ResourceDictionary Res => Application.Current.Resources;
    static readonly Brush Good = new SolidColorBrush(Color.FromArgb(255, 120, 220, 140));
    static readonly Brush Todo = new SolidColorBrush(Color.FromArgb(255, 255, 196, 90));

    public OnboardingView(App app)
    {
        _app = app;
        _steps = new()
        {
            new Step
            {
                Glyph = "", Title = "Windows Game Bar leaves Guide alone",
                Check = () => SetupChecks.GameBarUsesGuide ? (false, "Turn off \"Allow your controller to open Game Bar\"") : (true, "Guide opens Nexus"),
                Action = "Open settings",
                Fix = () => { Leave("ms-settings:gaming-gamebar"); return Task.CompletedTask; },
            },
            new Step
            {
                Glyph = "", Title = "Steam leaves Guide alone",
                Check = () => SetupChecks.SteamUsesGuide() switch
                {
                    true => (false, "Turn off \"Guide button focuses Steam\" in Steam > Settings > Controller"),
                    false => (true, "Steam won't open on Guide"),
                    null => (true, "Steam not found, nothing to do"),
                },
                Action = "Steam settings",
                Fix = () => { Leave("steam://open/settings"); return Task.CompletedTask; },
            },
            new Step
            {
                Glyph = "", Title = "Games ignore the controller in the guide",
                Check = () => _app.InputBridge.Ready
                    ? (true, _app.Settings.LockGameInput ? "On (BO3 and others pause and wait)" : "Installed, switched off on the Controllers page")
                    : (false, "Installs two small signed drivers (one admin prompt)"),
                Action = "Set up",
                Fix = async () => { _app.Shell.Hide(restoreFocus: false); await _app.InputBridge.SetUpAsync(); },
            },
            new Step
            {
                Glyph = "", Title = "Start with Windows",
                Check = () => SetupChecks.StartWithWindows ? (true, "Ready the moment you press Guide") : (false, "Otherwise Guide does nothing until you open Nexus"),
                Action = "Turn on",
                Fix = () => { SetupChecks.StartWithWindows = true; return Task.CompletedTask; },
            },
            new Step
            {
                Glyph = "", Title = "Pause the game when the guide opens",
                Check = () => _app.Settings.PauseOnGuide ? (true, "Taps Start as you press Guide (needs the lock above)") : (false, "Optional: the game keeps running behind the guide"),
                Action = "Turn on",
                Fix = () => { _app.Settings.PauseOnGuide = true; _app.Settings.Save(); return Task.CompletedTask; },
            },
        };

        // Two columns: five rows stacked don't fit the panel's height.
        var list = new Grid { ColumnSpacing = 10, RowSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        list.ColumnDefinitions.Add(new ColumnDefinition());
        list.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < _steps.Count; i++)
        {
            if (i % 2 == 0) list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = Row(_steps[i]);
            Grid.SetRow((FrameworkElement)row, i / 2);
            Grid.SetColumn((FrameworkElement)row, i % 2);
            list.Children.Add(row);
        }

        _summary = new TextBlock { Style = (Style)Res["CaptionTextBlockStyle"], Foreground = (Brush)Res["SubtleTextBrush"], VerticalAlignment = VerticalAlignment.Center };
        var done = Pill("", "Done", () => { Finish(); return Task.CompletedTask; });
        var footer = new Grid { Margin = new Thickness(4, 4, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(done, 1);
        footer.Children.Add(_summary);
        footer.Children.Add(done);

        var root = new Grid { RowSpacing = 6 };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(footer, 1);
        root.Children.Add(list);
        root.Children.Add(footer);
        Content = root;

        _poll = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(2);
        _poll.Tick += (_, _) => Refresh();
    }

    UIElement Row(Step s)
    {
        s.Badge = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = (Brush)Res["IconCircleBrush"],
            Child = new FontIcon { Glyph = s.Glyph, FontSize = 14 }, VerticalAlignment = VerticalAlignment.Center,
        };
        s.Detail = new TextBlock { Style = (Style)Res["CaptionTextBlockStyle"], Foreground = (Brush)Res["SubtleTextBrush"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = s.Title, Style = (Style)Res["BodyStrongTextBlockStyle"], FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis }, s.Detail },
        };
        s.Button = Pill("", s.Action, async () =>
        {
            if (_busy) return;
            _busy = true;
            try { await s.Fix(); }
            catch (Exception ex) { Log.Error($"setup step {s.Title}", ex); }
            finally { _busy = false; }
            Refresh();
        });
        var row = new Grid
        {
            ColumnSpacing = 12, Padding = new Thickness(10, 5, 6, 5), CornerRadius = new CornerRadius(16),
            Background = (Brush)Res["GlassFill"], BorderBrush = (Brush)Res["GlassRim"], BorderThickness = new Thickness(1),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(s.Button, 2);
        row.Children.Add(s.Badge);
        row.Children.Add(text);
        row.Children.Add(s.Button);
        return row;
    }

    void Refresh()
    {
        int left = 0;
        foreach (var s in _steps)
        {
            var (ok, detail) = s.Check();
            s.Detail.Text = detail;
            s.Badge.Background = ok ? Good : (Brush)Res["IconCircleBrush"];
            ((FontIcon)s.Badge.Child).Glyph = ok ? "" : s.Glyph;
            ((FontIcon)s.Badge.Child).Foreground = ok ? new SolidColorBrush(Color.FromArgb(255, 16, 40, 20)) : Todo;
            s.Button.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (!ok) left++;
        }
        _summary.Text = left == 0 ? "All set. Press Guide any time." : $"{left} left. You can come back from More settings.";
    }

    void Leave(string target)
    {
        _app.Shell.Hide(restoreFocus: false);
        Launch.Open(target);
    }

    void Finish()
    {
        _app.Settings.OnboardingDone = true;
        _app.Settings.Save();
        _app.Shell.Back();
    }

    static Button Pill(string glyph, string text, Func<Task> click)
    {
        var b = new Button
        {
            Style = (Style)Res["GlassButtonStyle"], MinHeight = 34, Padding = new Thickness(12, 0, 14, 0),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = glyph, FontSize = 13 }, new TextBlock { Text = text, FontSize = 13 } } },
        };
        b.Click += async (_, _) => await click();
        return b;
    }

    public void OnShown() { Refresh(); _poll.Start(); }
    public void OnHidden() => _poll.Stop();
}
