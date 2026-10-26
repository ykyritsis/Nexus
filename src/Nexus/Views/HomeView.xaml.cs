using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.Providers;
using Nexus.Services;
using Windows.UI;

namespace Nexus.Views;

/// <summary>
/// Home tab: a row of square game tiles (the last game played gets a larger one), Home/Library,
/// and a dock of installed launchers.
/// </summary>
public sealed partial class HomeView : UserControl, INexusView
{
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

    double CardHeight => _app.Settings.CompactMode ? 230 : 280;

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

    async Task LoadRecentAsync()
    {
        var games = await Task.Run(() => GameLibrary.Recent(Math.Max(_app.Settings.RecentGameCount, 6)));
        var key = string.Join("|", games.Select(g => g.LaunchTarget)) + CardHeight;
        NoRecentText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (key == _recentKey) return;
        _recentKey = key;
        Cards.Children.Clear();
        for (int i = 0; i < games.Count; i++)
            Cards.Children.Add(await CardAsync(games[i], hero: i == 0));
    }

    /// <summary>
    /// Square, console-style tile. Like Xbox and PS5 tiles, it's key art plus the game's logo:
    /// Steam's wide scenic hero image cropped to a square with the transparent logo on top. Games
    /// without hero art use the top of their cover, which already has the title on it.
    /// </summary>
    async Task<Button> CardAsync(GameEntry game, bool hero)
    {
        double size = hero ? CardHeight : CardHeight * 0.8;
        bool composed = game.Art.Hero != null;
        var art = game.Art.Hero ?? game.Art.Portrait ?? game.Art.Icon;

        var layers = new Grid();
        var picture = new Border { CornerRadius = new CornerRadius(25), Background = new SolidColorBrush(Color.FromArgb(255, 28, 32, 40)) };
        if (await Icons.LoadAsync(art, (uint)(size * 3)) is { } img)
            picture.Background = new ImageBrush
            {
                ImageSource = img, Stretch = Stretch.UniformToFill,
                AlignmentY = composed ? AlignmentY.Center : AlignmentY.Top,
            };
        else
            picture.Child = new FontIcon { Glyph = "", FontSize = 40, Opacity = 0.6 };
        layers.Children.Add(picture);

        // Soft fade at the bottom so the logo and caption read over any scene.
        var fade = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        fade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0.4 });
        fade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(200, 0, 0, 0), Offset = 1 });
        layers.Children.Add(new Border { CornerRadius = new CornerRadius(25), Background = fade });

        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 14), Spacing = 4 };
        var logo = composed ? await Icons.LoadAsync(game.Art.Logo, 600) : null;
        if (logo != null)
            caption.Children.Add(new Image
            {
                Source = logo, MaxHeight = size * 0.3, MaxWidth = size * 0.82,
                HorizontalAlignment = HorizontalAlignment.Center, Stretch = Stretch.Uniform,
            });
        else if (composed || game.Art.Portrait == null) // a cropped cover already shows its title
            caption.Children.Add(new TextBlock { Text = game.Name, FontSize = hero ? 20 : 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextAlignment = TextAlignment.Center });
        caption.Children.Add(new TextBlock
        {
            Text = hero ? $"Continue · {GameLibrary.Ago(game.LastPlayed)}" : GameLibrary.Ago(game.LastPlayed),
            FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
        });
        layers.Children.Add(caption);

        var card = new Button
        {
            Style = (Style)Application.Current.Resources["CardStyle"], Width = size, Height = size, Content = layers,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, game.Name);
        card.Click += (_, _) => OpenAndHide(game.LaunchTarget);
        return card;
    }

    async Task LoadDockAsync()
    {
        foreach (var l in await Task.Run(Launchers.Installed))
        {
            var b = new Button
            {
                Style = (Style)Application.Current.Resources["IconButtonStyle"],
                Width = 60, Height = 60,
            };
            ToolTipService.SetToolTip(b, l.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, l.Name);
            if (await Icons.LoadAsync(l.IconSource, 96) is { } img)
                b.Content = new Image { Source = img, Width = 44, Height = 44 };
            else
                b.Content = new TextBlock { Text = l.Name[..1], FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) };
            b.Click += (_, _) => OpenAndHide(l.Target);
            Dock.Children.Add(b);
        }
    }
}
