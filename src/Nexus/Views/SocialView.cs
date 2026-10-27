using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nexus.Providers;
using Windows.UI;

namespace Nexus.Views;

/// <summary>
/// Social tab: Steam friends as glass cards, in-game first with the game they're playing.
/// A opens a chat, Y opens their profile. Status refreshes at most once a minute.
/// </summary>
public sealed class SocialView : UserControl, INexusView
{
    readonly ShellWindow _shell;
    readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _summary, _empty;
    readonly Dictionary<Button, Friend> _byCard = new();
    DateTime _loadedAt;
    bool _loading;

    public string Title => "Social";
    public string? YHint => "Profile";

    static ResourceDictionary Res => Application.Current.Resources;

    public SocialView(ShellWindow shell)
    {
        _shell = shell;
        _summary = new TextBlock { Text = "Steam friends", Style = (Style)Res["SectionHeaderStyle"], VerticalAlignment = VerticalAlignment.Center };
        var open = new Button
        {
            Style = (Style)Res["GlassButtonStyle"], MinHeight = 38, Padding = new Thickness(14, 0, 14, 0), HorizontalAlignment = HorizontalAlignment.Right,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "", FontSize = 15 }, new TextBlock { Text = "Open Steam friends" } } },
        };
        open.Click += (_, _) => { _shell.Hide(restoreFocus: false); SteamFriends.OpenFriends(); };
        var header = new Grid { Children = { _summary, open } };

        _empty = new TextBlock
        {
            Text = "Loading friends…", Style = (Style)Res["BodyTextBlockStyle"], Foreground = (Brush)Res["SubtleTextBrush"],
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var scroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(6), Content = _cards,
        };
        var body = new Grid { Children = { scroller, _empty } };
        var root = new Grid { RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(body, 1);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;
    }

    public async void OnShown()
    {
        if (_loading || DateTime.UtcNow - _loadedAt < TimeSpan.FromSeconds(60)) return;
        _loading = true;
        try
        {
            var friends = await SteamFriends.WithStatusAsync();
            _loadedAt = DateTime.UtcNow;
            Build(friends);
        }
        finally { _loading = false; }
    }

    public void OnHidden() { }

    void Build(List<Friend> friends)
    {
        _cards.Children.Clear();
        _byCard.Clear();
        int online = friends.Count(f => f.Presence != Presence.Offline);
        _summary.Text = friends.Count == 0 ? "Steam friends" : $"Steam friends · {online} online";
        _empty.Text = friends.Count == 0 ? "No Steam friends found on this PC." : "";
        _empty.Visibility = friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var f in friends) _cards.Children.Add(Card(f));
        _shell.EnsureFocus(_cards.Children.FirstOrDefault() as Control);
    }

    /// <summary>
    /// A friend: avatar beside name and status. In a game, the card is that game's art (darkened so
    /// text reads); otherwise a strip shows their most-played game of the last two weeks.
    /// </summary>
    Button Card(Friend f)
    {
        var dot = f.Presence switch
        {
            Presence.InGame => Color.FromArgb(255, 144, 220, 90),
            Presence.Online => Color.FromArgb(255, 90, 190, 255),
            _ => Color.FromArgb(255, 120, 124, 132),
        };
        var avatar = new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(23), VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(dot), BorderThickness = new Thickness(2.5),
            Background = f.AvatarUrl is null ? (Brush)Res["IconCircleBrush"]
                : new ImageBrush { ImageSource = new BitmapImage(new Uri(f.AvatarUrl)) { DecodePixelWidth = 92 }, Stretch = Stretch.UniformToFill },
            Opacity = f.Presence == Presence.Offline ? 0.6 : 1,
        };
        var name = new TextBlock { Text = f.Name, Style = (Style)Res["BodyStrongTextBlockStyle"], FontSize = 14, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock
        {
            Text = f.Status, Style = (Style)Res["CaptionTextBlockStyle"], FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            Foreground = f.Presence == Presence.InGame ? new SolidColorBrush(dot) : (Brush)Res["SubtleTextBrush"],
        };
        var top = new Grid { ColumnSpacing = 10 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, status } };
        Grid.SetColumn(who, 1);
        top.Children.Add(avatar);
        top.Children.Add(who);

        var body = new Grid { RowSpacing = 8 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(top);

        var layers = new Grid();
        if (f.Presence == Presence.InGame && f.GameAppId is { } playing)
        {
            // In a game: its art fills the card.
            layers.Children.Add(new Border { CornerRadius = new CornerRadius(20), Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(f.GameArt ?? Friend.HeaderArt(playing))) { DecodePixelWidth = 460 }, Stretch = Stretch.UniformToFill } });
            var shade = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
            shade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xD9, 0x0C, 0x0E, 0x12), Offset = 0 });
            shade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x59, 0x0C, 0x0E, 0x12), Offset = 1 });
            layers.Children.Add(new Border { CornerRadius = new CornerRadius(20), Background = shade });
            status.Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 245, 150));
        }
        else if (f.RecentGame is { } recent && f.RecentAppId is { } recentId)
        {
            // Not in a game: what they've been playing lately.
            var art = new Border
            {
                Width = 64, Height = 30, CornerRadius = new CornerRadius(6),
                Background = new ImageBrush { ImageSource = new BitmapImage(new Uri(f.RecentArt ?? Friend.HeaderArt(recentId))) { DecodePixelWidth = 128 }, Stretch = Stretch.UniformToFill },
            };
            var line = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = recent, Style = (Style)Res["CaptionTextBlockStyle"], FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap },
                    new TextBlock { Text = f.RecentHours is { } h ? $"{h} h past 2 weeks" : "Recently played", Style = (Style)Res["CaptionTextBlockStyle"], FontSize = 11, Foreground = (Brush)Res["SubtleTextBrush"], TextWrapping = TextWrapping.NoWrap },
                },
            };
            var strip = new Grid { ColumnSpacing = 8 };
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            strip.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(line, 1);
            strip.Children.Add(art);
            strip.Children.Add(line);
            Grid.SetRow(strip, 1);
            body.Children.Add(strip);
        }
        body.Margin = new Thickness(12, 10, 12, 10);
        body.VerticalAlignment = VerticalAlignment.Center;
        layers.Children.Add(body);

        var card = new Button
        {
            Style = (Style)Res["TileStyle"], Width = 220, Height = 112, CornerRadius = new CornerRadius(20), Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, Content = layers,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"Chat with {f.Name}, {f.Status}");
        card.Click += (_, _) => { _shell.Hide(restoreFocus: false); SteamFriends.Chat(f.SteamId64); };
        _byCard[card] = f;
        return card;
    }

    /// <summary>Y on a friend opens their Steam profile.</summary>
    public bool HandleY(DependencyObject? focused)
    {
        if (focused is Button b && _byCard.TryGetValue(b, out var f))
        {
            _shell.Hide(restoreFocus: false);
            SteamFriends.Profile(f.SteamId64);
            return true;
        }
        return false;
    }
}
