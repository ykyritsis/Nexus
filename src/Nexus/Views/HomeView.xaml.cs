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

    public string Title => "Home";

    public HomeView(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        InitializeComponent();
    }

    public void OnShown()
    {
        _ = LoadRecentAsync();
        if (!_dockBuilt) { _dockBuilt = true; _ = LoadDockAsync(); }
    }

    public void OnHidden() { }

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
        if (games.Count > 0) Focused(games[0], first: true);

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
        visual.CenterPoint = new Vector3((float)tile.ActualWidth / 2, (float)tile.ActualHeight / 2, 0);
        var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
        var spring = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.56f), new Vector2(0.64f, 1f));
        anim.InsertKeyFrame(1f, up ? new Vector3(1.07f, 1.07f, 1) : Vector3.One, spring);
        anim.Duration = TimeSpan.FromMilliseconds(up ? 380 : 220);
        visual.StartAnimation("Scale", anim);
    }

    async Task<Grid> TileArtAsync(GameEntry game, double size, ConsoleArt.Found? console)
    {
        var layers = new Grid();
        var picture = new Border { CornerRadius = new CornerRadius(25), Background = new SolidColorBrush(Color.FromArgb(255, 34, 38, 50)) };
        layers.Children.Add(picture);

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
            layers.Children.Add(new Border { CornerRadius = new CornerRadius(25), Background = shade });
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
            if (Tiles.Children.Count > 0 && FocusTitle.Text == game.Name) _shell.SetAmbient(mood);
        }
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
