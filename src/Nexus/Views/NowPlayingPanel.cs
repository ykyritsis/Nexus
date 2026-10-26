using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nexus.Providers;
using Nexus.Services;
using Windows.UI;

namespace Nexus.Views;

/// <summary>
/// The game you're in, like pressing Guide on a console: its art, how long you've been playing,
/// Steam achievement progress (read locally) and quick actions. Shown by Home while a game runs.
/// </summary>
public sealed class NowPlayingPanel : UserControl
{
    readonly ShellWindow _shell;
    readonly PlayingGame _playing;
    readonly Action _showRecent;
    readonly List<Achievement> _achievements;
    readonly int _playtime;
    readonly TextBlock _session;
    Button? _quit;
    bool _quitArmed;

    static ResourceDictionary Res => Application.Current.Resources;

    /// <param name="achievements">Steam achievements, loaded off the UI thread by the caller.</param>
    /// <param name="playtimeMinutes">Total Steam playtime, or 0 when unknown.</param>
    public NowPlayingPanel(ShellWindow shell, PlayingGame playing, List<Achievement> achievements, int playtimeMinutes, Action showRecent)
    {
        _shell = shell;
        _playing = playing;
        _achievements = achievements;
        _playtime = playtimeMinutes;
        _showRecent = showRecent;
        _session = new TextBlock { Style = (Style)Res["BodyTextBlockStyle"], Foreground = (Brush)Res["SubtleTextBrush"] };

        var top = new Grid { ColumnSpacing = 22 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        var hero = HeroCard();
        var details = Details();
        Grid.SetColumn(details, 1);
        top.Children.Add(hero);
        top.Children.Add(details);

        var root = new Grid { RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var actions = Actions();
        Grid.SetRow(actions, 1);
        root.Children.Add(top);
        root.Children.Add(actions);
        Content = root;
        UpdateSession();
    }

    Button? _resume;

    public void FocusFirst() => _resume?.Focus(FocusState.Keyboard);

    /// <summary>Quitting needs two presses in the same visit; reopening the overlay starts over.</summary>
    public void ResetQuit()
    {
        _forceArmed = false;
        if (!_quitArmed || _quit is null) return;
        _quitArmed = false;
        SetQuitLabel("Quit game", warn: false);
    }

    bool _forceArmed;

    public bool CanForceQuit => _playing.ProcessId != 0;

    /// <summary>X: screenshot of the game.</summary>
    public void Screenshot() => ForGame(Captures.Screenshot);

    /// <summary>Y: end a frozen game's process. The first press asks, the second ends it.</summary>
    public void ForceQuit()
    {
        if (_playing.ProcessId == 0) return;
        if (!_forceArmed)
        {
            _forceArmed = true;
            _quitArmed = false;
            SetQuitLabel("Press Y again to force quit", warn: true);
            return;
        }
        Log.Info($"force-quitting {_playing.Game.Name}");
        OpenWindows.ForceClose(_playing.ProcessId);
        _shell.Hide(restoreFocus: false);
    }

    void SetQuitLabel(string text, bool warn)
    {
        if (_quit is null) return;
        ((TextBlock)((StackPanel)_quit.Content).Children[1]).Text = text;
        if (warn) _quit.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 140, 120));
        else _quit.ClearValue(Control.ForegroundProperty);
    }

    public void UpdateSession()
    {
        var parts = new List<string>();
        if (_playing.StartedAt is { } started) parts.Add($"{NowPlaying.Duration(DateTime.Now - started)} this session");
        if (_playtime >= 60) parts.Add($"{_playtime / 60} h played");
        _session.Text = parts.Count > 0 ? string.Join(" · ", parts) : "Now playing";
    }

    // ---------------- hero ----------------

    Border HeroCard()
    {
        var layers = new Grid();
        var card = new Border
        {
            CornerRadius = new CornerRadius(28), Height = 222, Background = new SolidColorBrush(Color.FromArgb(255, 34, 38, 50)),
            BorderBrush = (Brush)Res["GlassRim"], BorderThickness = new Thickness(1), Child = layers,
        };
        _ = FillHeroAsync(card, layers);
        return card;
    }

    async Task FillHeroAsync(Border card, Grid layers)
    {
        var game = _playing.Game;
        var scene = game.Art.Hero;
        var logo = game.Art.Logo;
        if (_playing.AppId != null && (scene is null || logo is null))
        {
            var (h, l) = await ConsoleArt.SteamOfficialAsync(_playing.AppId);
            scene ??= h;
            logo ??= l;
        }
        var console = ConsoleArt.Cached(game.Key) ?? (scene is null ? await ConsoleArt.ResolveAsync(game.Key, game.Name) : null);
        scene ??= console?.Hero;

        var picture = new Border { CornerRadius = new CornerRadius(27) };
        if (await Icons.LoadAsync(scene ?? console?.Square, 1200) is { } img)
            picture.Background = new ImageBrush { ImageSource = img, Stretch = Stretch.UniformToFill };
        layers.Children.Add(picture);

        var fade = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        fade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(10, 0, 0, 0), Offset = 0 });
        fade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(150, 0, 0, 0), Offset = 1 });
        layers.Children.Add(new Border { CornerRadius = new CornerRadius(27), Background = fade });

        if (await Icons.LoadAsync(logo, 900) is { } logoImg)
            layers.Children.Add(new Image { Source = logoImg, MaxWidth = 300, MaxHeight = 110, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        else if (scene is null && console is null && await Icons.LoadAsync(_playing.ExePath, 96) is { } exeIcon)
            layers.Children.Add(new Image { Source = exeIcon, Width = 72, Height = 72 });

        // "Now playing" chip in the corner
        layers.Children.Add(new Border
        {
            Background = (Brush)Res["GlassChipFill"], BorderBrush = (Brush)Res["GlassRim"], BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Color.FromArgb(255, 120, 230, 120)), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Now playing", Style = (Style)Res["CaptionTextBlockStyle"] },
                },
            },
        });

        if (await ArtColor.FromFileAsync(scene ?? console?.Square) is { } mood) _shell.SetAmbient(mood);
        await AddCapturesAsync(layers);
    }

    /// <summary>This game's latest screenshots and clips, as small thumbnails in the card's corner.</summary>
    async Task AddCapturesAsync(Grid layers)
    {
        string? title = null;
        if (_playing.Window != IntPtr.Zero) title = OpenWindows.List().FirstOrDefault(w => w.Handle == _playing.Window)?.Title;
        var files = await Task.Run(() => Captures.LatestFor(new[] { _playing.Game.Name, title }, _playing.StartedAt, 3));
        if (files.Count == 0) return;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        };
        foreach (var f in files)
        {
            var thumb = new Border { Width = 64, Height = 36, CornerRadius = new CornerRadius(8), Background = (Brush)Res["GlassChipFill"] };
            if (await Icons.LoadAsync(f.FullName, 128) is { } img)
                thumb.Background = new ImageBrush { ImageSource = img, Stretch = Stretch.UniformToFill };
            if (f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                thumb.Child = new FontIcon { Glyph = "", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Button
            {
                Style = (Style)Res["IconButtonStyle"], Width = 66, Height = 38, Padding = new Thickness(1), CornerRadius = new CornerRadius(9),
                BorderBrush = (Brush)Res["GlassRim"], BorderThickness = new Thickness(1), Content = thumb,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, f.Name);
            var path = f.FullName;
            b.Click += (_, _) => { _shell.Hide(restoreFocus: false); Launch.Open(path); };
            row.Children.Add(b);
        }
        layers.Children.Add(row);
    }

    // ---------------- details ----------------

    StackPanel Details()
    {
        var panel = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = _playing.Game.Name, Style = (Style)Res["SubtitleTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        panel.Children.Add(_session);

        var list = _achievements;
        {
            if (list.Count > 0)
            {
                int got = list.Count(a => a.Unlocked);
                var header = new Grid { Margin = new Thickness(0, 12, 0, 0) };
                header.Children.Add(new TextBlock { Text = "Achievements", Style = (Style)Res["BodyStrongTextBlockStyle"] });
                header.Children.Add(new TextBlock { Text = $"{got} of {list.Count}", Style = (Style)Res["BodyTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Right });
                panel.Children.Add(header);
                panel.Children.Add(new ProgressBar { Maximum = list.Count, Value = got, Height = 6, CornerRadius = new CornerRadius(3) });

                // Next up: locked achievements (secret ones last), or the latest unlocks once all are done.
                var next = list.Where(a => !a.Unlocked).OrderBy(a => a.Hidden).Take(2).ToList();
                bool done = next.Count == 0;
                if (done) next = list.Where(a => a.UnlockedAt != null).OrderByDescending(a => a.UnlockedAt).Take(2).ToList();
                panel.Children.Add(new TextBlock
                {
                    Text = done ? "All unlocked · latest" : "Next up", Style = (Style)Res["CaptionTextBlockStyle"],
                    Foreground = (Brush)Res["SubtleTextBrush"], Margin = new Thickness(0, 6, 0, 0),
                });
                foreach (var a in next) panel.Children.Add(AchievementRow(a));
            }
        }
        return panel;
    }

    Grid AchievementRow(Achievement a)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = (Brush)Res["IconCircleBrush"],
        };
        if (!a.Hidden && a.IconUrl != "") _ = IconAsync(icon, a.IconUrl);
        else icon.Child = new FontIcon { Glyph = "", FontSize = 14 };
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = a.Hidden && !a.Unlocked ? "Secret achievement" : a.Name, Style = (Style)Res["BodyStrongTextBlockStyle"], FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap },
                new TextBlock { Text = a.Hidden && !a.Unlocked ? "Keep playing to find out" : a.Description, Style = (Style)Res["CaptionTextBlockStyle"], Foreground = (Brush)Res["SubtleTextBrush"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap },
            },
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Achievement icons are downloaded once and kept with the other artwork.</summary>
    async Task IconAsync(Border icon, string url)
    {
        if (await ConsoleArt.SmallImageAsync(url, $"ach_{_playing.AppId}_{Path.GetFileName(new Uri(url).LocalPath)}") is { } file
            && await Icons.LoadAsync(file, 68) is { } img)
            icon.Background = new ImageBrush { ImageSource = img, Stretch = Stretch.UniformToFill };
    }

    // ---------------- actions ----------------

    StackPanel Actions()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _resume = Pill("", "Resume", () => _shell.Hide());
        row.Children.Add(_resume);
        row.Children.Add(Pill("", "Screenshot", () => ForGame(Captures.Screenshot)));
        row.Children.Add(Pill("", "Record last 30 s", () => ForGame(Captures.RecordThat)));
        if (_playing.AppId != null)
            row.Children.Add(Pill("", "Steam page", () => { _shell.Hide(restoreFocus: false); Launch.Open($"steam://nav/games/details/{_playing.AppId}"); }));
        row.Children.Add(Pill("", "Recent games", _showRecent));
        _quit = Pill("", "Quit game", Quit);
        row.Children.Add(_quit);
        return row;
    }

    async void ForGame(Action action)
    {
        _shell.Hide();
        await Task.Delay(450);
        action();
    }

    /// <summary>First press asks, second closes the game (its window, or the process if it has none).</summary>
    void Quit()
    {
        if (!_quitArmed)
        {
            _quitArmed = true;
            _forceArmed = false;
            SetQuitLabel("Press again to quit", warn: true);
            return;
        }
        Log.Info($"quitting {_playing.Game.Name}");
        if (_playing.Window != IntPtr.Zero) OpenWindows.Close(_playing.Window);
        else if (_playing.ProcessId != 0) OpenWindows.ForceClose(_playing.ProcessId);
        _shell.Hide(restoreFocus: false);
    }

    static Button Pill(string glyph, string text, Action click)
    {
        var b = new Button
        {
            Style = (Style)Res["GlassButtonStyle"], MinHeight = 36, Padding = new Thickness(12, 0, 14, 0),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = glyph, FontSize = 14 }, new TextBlock { Text = text, FontSize = 13 } } },
        };
        b.Click += (_, _) => click();
        return b;
    }
}
