using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Nexus.Providers;
using Nexus.Services;
using Windows.UI;

namespace Nexus.Views;

/// <summary>
/// Home tab: your last four games as big square tiles (the most recent one larger), the focused
/// game's name, Home/Library and the launchers.
///
/// Each tile is built clean: a scenic picture with no text on it, cropped square, with the game's
/// transparent logo centred on top. Pictures come from Steam's hero art, or the Xbox/PlayStation
/// hero art (<see cref="ConsoleArt"/>). Games without a logo use console square key art, which has
/// the title built in, and finally the top of the Steam cover.
/// </summary>
public sealed partial class HomeView : UserControl, INexusView
{
    const int GameCount = 4;
    const double HeroSize = 222, TileSize = 184;

    readonly App _app;
    readonly ShellWindow _shell;
    readonly Dictionary<string, Color> _moods = new();
    bool _dockBuilt;
    string _recentKey = "";
    NowPlayingPanel? _now;
    string _nowKey = "";
    DispatcherTimer? _clock;

    public string Title => "Home";
    bool NowShowing => _now != null && NowHost.Visibility == Visibility.Visible;
    public string? XHint => NowShowing ? "Screenshot" : null;
    public string? YHint => NowShowing && _now!.CanForceQuit ? "Force quit" : null;

    public bool HandleX(DependencyObject? focused)
    {
        if (!NowShowing) return false;
        _now!.Screenshot();
        return true;
    }

    public bool HandleY(DependencyObject? focused)
    {
        if (!NowShowing) return false;
        _now!.ForceQuit();
        return true;
    }

    public HomeView(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(NowHost, true);
        ElementCompositionPreview.SetIsTranslationEnabled(RecentGrid, true);
    }

    public void OnShown()
    {
        _ = LoadNowPlayingAsync(_shell.PreviousForeground);
        _ = LoadRecentAsync();
        if (!_dockBuilt) { _dockBuilt = true; _ = LoadDockAsync(); }
    }

    public void OnHidden() => _clock?.Stop();

    // ---------------- now playing ----------------

    async Task LoadNowPlayingAsync(IntPtr previous)
    {
        var found = await Task.Run(() =>
        {
            var p = NowPlaying.Detect(previous);
            if (p is null) return default((PlayingGame, List<Achievement>, int)?);
            var list = p.AppId is null ? new List<Achievement>() : SteamAchievements.Load(p.AppId);
            int minutes = p.AppId is null ? 0 : SteamAccount.PlaytimeMinutes(p.AppId) ?? 0;
            return (p, list, minutes);
        });
        if (found is not { } f) { ShowNow(false); _now = null; _nowKey = ""; return; }

        var (playing, achievements, playtime) = f;
        var key = $"{playing.Game.Key}|{playing.ProcessId}|{achievements.Count(a => a.Unlocked)}";
        if (key != _nowKey || _now is null)
        {
            _nowKey = key;
            _now = new NowPlayingPanel(_shell, playing, achievements, playtime, () => ShowNow(false));
            NowHost.Content = _now;
        }
        _now.ResetQuit();
        _now.UpdateSession();
        ShowNow(true);
        _clock ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick -= Clock_Tick;
        _clock.Tick += Clock_Tick;
        _clock.Start();
    }

    void Clock_Tick(object? sender, object e) => _now?.UpdateSession();

    void ShowNow(bool now)
    {
        var incoming = now ? (UIElement)NowHost : RecentGrid;
        bool changed = incoming.Visibility != Visibility.Visible;
        NowHost.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
        RecentGrid.Visibility = now ? Visibility.Collapsed : Visibility.Visible;
        if (changed) Reveal(incoming, fromRight: !now);
        _shell.RefreshHints();
        if (now) _now?.FocusFirst();
        else if (Tiles.Children.FirstOrDefault() is Control first) first.Focus(FocusState.Keyboard);
    }

    /// <summary>Hub and tiles slide in from the side they "live" on, with the same spring as the tiles.</summary>
    static void Reveal(UIElement element, bool fromRight)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        var spring = c.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, spring);
        fade.Duration = TimeSpan.FromMilliseconds(260);
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(fromRight ? 36 : -36, 0, 0));
        slide.InsertKeyFrame(1f, Vector3.Zero, spring);
        slide.Duration = TimeSpan.FromMilliseconds(340);
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
    }

    void Home_Click(object sender, RoutedEventArgs e) => OpenAndHide(Launch.Resolve(_app.Settings.HomeTarget));
    void Library_Click(object sender, RoutedEventArgs e) => OpenAndHide(Launch.Resolve(_app.Settings.LibraryTarget));

    void OpenAndHide(string target)
    {
        _shell.Hide(restoreFocus: false);
        Launch.Open(target);
    }

    // ---------------- tiles ----------------

    async Task LoadRecentAsync()
    {
        var games = await Task.Run(() => GameLibrary.Recent(GameCount));
        NoRecentText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var key = string.Join("|", games.Select(g => g.Key));
        if (key == _recentKey) return;
        _recentKey = key;

        Tiles.Children.Clear();
        var built = new List<(GameEntry game, Button tile, double size)>();
        for (int i = 0; i < games.Count; i++)
        {
            double size = i == 0 ? HeroSize : TileSize;
            var tile = MakeTile(games[i], size, first: i == 0);
            tile.Content = await TileArtAsync(games[i], size, ConsoleArt.Cached(games[i].Key));
            Tiles.Children.Add(tile);
            built.Add((games[i], tile, size));
        }
        if (games.Count > 0 && RecentGrid.Visibility == Visibility.Visible) Focused(games[0], first: true);
        // Tiles arrive just after the page opens: start on the first one rather than wherever focus fell.
        if (RecentGrid.Visibility == Visibility.Visible && Tiles.Children.FirstOrDefault() is Control firstTile)
        {
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
            if (focused is not DependencyObject d || ReferenceEquals(d, HomeButton) || !_shell.InPanel(d))
                firstTile.Focus(FocusState.Keyboard);
        }

        // Fill in missing art in the background: Steam's official logo and scene first (for the clean
        // centred-logo look), then console art for anything still without a logo.
        foreach (var (original, tile, size) in built)
        {
            var game = original;
            if ((game.Art.Logo == null || game.Art.Hero == null) && game.Key.StartsWith("steam:"))
            {
                var (hero, logo) = await ConsoleArt.SteamOfficialAsync(game.Key[6..]);
                if (hero != null || logo != null)
                {
                    game = game with { Art = game.Art with { Hero = game.Art.Hero ?? hero, Logo = game.Art.Logo ?? logo } };
                    if (tile.Parent != null) tile.Content = await TileArtAsync(game, size, ConsoleArt.Cached(game.Key));
                }
            }
            if (game.Art.Logo != null && game.Art.Hero != null) continue;
            if (ConsoleArt.Cached(game.Key) != null) continue;
            var found = await ConsoleArt.ResolveAsync(game.Key, game.Name);
            if (found != null && tile.Parent != null) tile.Content = await TileArtAsync(game, size, found);
        }
    }

    Button MakeTile(GameEntry game, double size, bool first)
    {
        var tile = new Button
        {
            Style = (Style)Application.Current.Resources["GameTileStyle"],
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
            Tag = "lift", // the focus ring sizes itself to the lifted tile
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, game.Name);
        tile.Click += (_, _) => OpenAndHide(game.LaunchTarget);
        tile.GotFocus += (_, _) => { Lift(tile, true); Focused(game, first); };
        tile.LostFocus += (_, _) => Lift(tile, false);
        tile.PointerEntered += (_, _) => { Lift(tile, true); Focused(game, first); };
        tile.PointerExited += (_, _) => { if (tile.FocusState == FocusState.Unfocused) Lift(tile, false); };
        return tile;
    }

    void Focused(GameEntry game, bool first)
    {
        FocusTitle.Text = game.Name;
        FocusDetail.Text = (first ? "Continue · " : "") + $"{game.Source} · {GameLibrary.Ago(game.LastPlayed)}";
        if (_moods.TryGetValue(game.Key, out var mood)) _shell.SetAmbient(mood);
    }

    /// <summary>A springy "lift": focused tiles grow from their centre with a little overshoot.</summary>
    static void Lift(Button tile, bool up)
    {
        var visual = ElementCompositionPreview.GetElementVisual(tile);
        // Width/Height are set when the tile is made; ActualWidth is still 0 if it is focused before its
        // first layout (the first tile, as the overlay opens), which made it grow from its top-left corner.
        double w = double.IsNaN(tile.Width) ? tile.ActualWidth : tile.Width, h = double.IsNaN(tile.Height) ? tile.ActualHeight : tile.Height;
        visual.CenterPoint = new Vector3((float)w / 2, (float)h / 2, 0);
        var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
        var spring = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));
        anim.InsertKeyFrame(1f, up ? new Vector3(1.07f, 1.07f, 1) : Vector3.One, spring);
        anim.Duration = TimeSpan.FromMilliseconds(up ? 200 : 160);
        visual.StartAnimation("Scale", anim);
    }

    async Task<Grid> TileArtAsync(GameEntry game, double size, ConsoleArt.Found? console)
    {
        var layers = new Grid();
        var picture = new Border { CornerRadius = new CornerRadius(26), Background = new SolidColorBrush(Color.FromArgb(255, 34, 38, 50)) };
        layers.Children.Add(picture);
        // A faint inner hairline on top keeps the tile's shape on very light art without framing it.
        var edge = new Border { CornerRadius = new CornerRadius(26), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0, 0, 0)), IsHitTestVisible = false };

        string? scene = game.Art.Hero ?? console?.Hero;
        string? logo = game.Art.Logo;
        string? used;

        if (scene != null && logo != null && await Icons.LoadAsync(scene, (uint)(size * 3)) is { } sceneImg)
        {
            // Clean composite: scene cropped square, a soft centre shade for contrast, logo centred.
            used = scene;
            picture.Background = new ImageBrush { ImageSource = sceneImg, Stretch = Stretch.UniformToFill };
            var shade = new RadialGradientBrush { Center = new(0.5, 0.5), GradientOrigin = new(0.5, 0.5), RadiusX = 0.6, RadiusY = 0.6 };
            shade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(70, 0, 0, 0), Offset = 0 });
            shade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 1 });
            layers.Children.Add(new Border { CornerRadius = new CornerRadius(26), Background = shade });
            if (await Icons.LoadAsync(logo, 800) is { } logoImg)
                layers.Children.Add(new Image
                {
                    Source = logoImg, MaxWidth = size * 0.78, MaxHeight = size * 0.42, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
        }
        else if (console != null && await Icons.LoadAsync(console.Square, (uint)(size * 2)) is { } sq)
        {
            // Console key art already carries the title.
            used = console.Square;
            picture.Background = new ImageBrush { ImageSource = sq, Stretch = Stretch.UniformToFill };
        }
        else
        {
            used = game.Art.Portrait ?? game.Art.Hero ?? game.Art.Icon;
            if (await Icons.LoadAsync(used, (uint)(size * 3)) is { } img)
                picture.Background = new ImageBrush { ImageSource = img, Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
            else
                layers.Children.Add(new TextBlock
                {
                    Text = game.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14),
                });
        }

        if (await ArtColor.FromFileAsync(used) is { } mood)
        {
            _moods[game.Key] = mood;
            if (Tiles.Children.Count > 0 && FocusTitle.Text == game.Name && RecentGrid.Visibility == Visibility.Visible) _shell.SetAmbient(mood);
        }
        layers.Children.Add(edge);
        return layers;
    }

    // ---------------- launchers ----------------

    async Task LoadDockAsync()
    {
        foreach (var l in await Task.Run(Launchers.Installed))
        {
            var b = new Button { Style = (Style)Application.Current.Resources["IconButtonStyle"], Width = 40, Height = 40 };
            ToolTipService.SetToolTip(b, l.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, l.Name);
            if (await Icons.LoadAsync(l.IconSource, 96) is { } img)
                b.Content = new Image { Source = img, Width = 28, Height = 28 };
            else
                b.Content = new TextBlock { Text = l.Name[..1], FontSize = 16, FontWeight = FontWeights.SemiBold };
            b.Click += (_, _) => OpenAndHide(l.Target);
            Dock.Children.Add(b);
        }
    }
}
