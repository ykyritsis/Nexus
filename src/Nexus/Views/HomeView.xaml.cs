using System.Numerics;
using Microsoft.UI.Composition;
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
/// Home tab, laid out like a console dashboard: a row of square tiles (the most recent game larger),
/// the focused game's name underneath, Home/Library, and installed launchers.
///
/// Tile art, best first: square key art from the Xbox Store, PlayStation Store or IGN
/// (<see cref="ConsoleArt"/>), else Steam's hero image cropped square with the game's logo on top.
/// Steam art shows straight away and console art replaces it as each lookup finishes.
/// </summary>
public sealed partial class HomeView : UserControl, INexusView
{
    const double HeroSize = 200, TileSize = 164;

    readonly App _app;
    readonly ShellWindow _shell;
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
        var games = await Task.Run(() => GameLibrary.Recent(Math.Max(_app.Settings.RecentGameCount, 6)));
        NoRecentText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var key = string.Join("|", games.Select(g => g.Key));
        if (key == _recentKey) return;
        _recentKey = key;

        Tiles.Children.Clear();
        var tiles = new List<(GameEntry game, Button tile, double size)>();
        for (int i = 0; i < games.Count; i++)
        {
            double size = i == 0 ? HeroSize : TileSize;
            var tile = MakeTile(games[i], size, first: i == 0);
            tile.Content = await TileArtAsync(games[i], size, ConsoleArt.Cached(games[i].Key));
            Tiles.Children.Add(tile);
            tiles.Add((games[i], tile, size));
        }
        if (games.Count > 0) ShowFocusText(games[0], first: true);

        // Fetch console art for any game that doesn't have it yet, one at a time, in the background.
        foreach (var (game, tile, size) in tiles)
        {
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
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Bottom,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, game.Name);
        tile.Click += (_, _) => OpenAndHide(game.LaunchTarget);
        tile.GotFocus += (_, _) => { Lift(tile, true); ShowFocusText(game, first); };
        tile.LostFocus += (_, _) => Lift(tile, false);
        tile.PointerEntered += (_, _) => Lift(tile, true);
        tile.PointerExited += (_, _) => { if (tile.FocusState == FocusState.Unfocused) Lift(tile, false); };
        return tile;
    }

    void ShowFocusText(GameEntry game, bool first)
    {
        FocusTitle.Text = game.Name;
        FocusDetail.Text = (first ? "Continue · " : "") + $"{game.Source} · {GameLibrary.Ago(game.LastPlayed)}";
    }

    /// <summary>The Xbox "lift": focused tiles grow slightly from their centre.</summary>
    static void Lift(Button tile, bool up)
    {
        var visual = ElementCompositionPreview.GetElementVisual(tile);
        visual.CenterPoint = new Vector3((float)tile.ActualWidth / 2, (float)tile.ActualHeight / 2, 0);
        var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
        var ease = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        anim.InsertKeyFrame(1f, up ? new Vector3(1.05f, 1.05f, 1) : Vector3.One, ease);
        anim.Duration = TimeSpan.FromMilliseconds(up ? 250 : 167);
        visual.StartAnimation("Scale", anim);
    }

    async Task<Grid> TileArtAsync(GameEntry game, double size, ConsoleArt.Found? console)
    {
        var layers = new Grid();
        var picture = new Border { CornerRadius = new CornerRadius(25), Background = new SolidColorBrush(Color.FromArgb(255, 28, 32, 40)) };
        layers.Children.Add(picture);

        // 1. Console square art: already a finished tile with the title on it.
        if (console != null && await Icons.LoadAsync(console.Square, (uint)(size * 2)) is { } sq)
        {
            picture.Background = new ImageBrush { ImageSource = sq, Stretch = Stretch.UniformToFill };
            return layers;
        }

        // 2. Steam: hero scene cropped square with the transparent logo on top,
        //    or the top of the cover (which has the title on it), or the icon.
        bool composed = game.Art.Hero != null;
        var art = game.Art.Hero ?? game.Art.Portrait ?? game.Art.Icon;
        if (await Icons.LoadAsync(art, (uint)(size * 3)) is { } img)
            picture.Background = new ImageBrush { ImageSource = img, Stretch = Stretch.UniformToFill, AlignmentY = composed ? AlignmentY.Center : AlignmentY.Top };
        else
            picture.Child = new FontIcon { Glyph = "", FontSize = 40, Opacity = 0.6 };

        if (composed)
        {
            var fade = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
            fade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0.45 });
            fade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(170, 0, 0, 0), Offset = 1 });
            layers.Children.Add(new Border { CornerRadius = new CornerRadius(25), Background = fade });
            if (await Icons.LoadAsync(game.Art.Logo, 600) is { } logo)
                layers.Children.Add(new Image
                {
                    Source = logo, MaxHeight = size * 0.3, MaxWidth = size * 0.8, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(12, 0, 12, size * 0.08),
                });
            else
                layers.Children.Add(new TextBlock
                {
                    Text = game.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                    TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 14),
                });
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
