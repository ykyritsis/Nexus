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
    }

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
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28), HorizontalAlignment = HorizontalAlignment.Center,
            BorderBrush = new SolidColorBrush(dot), BorderThickness = new Thickness(2.5),
            Background = f.AvatarUrl is null ? (Brush)Res["IconCircleBrush"]
                : new ImageBrush { ImageSource = new BitmapImage(new Uri(f.AvatarUrl)) { DecodePixelWidth = 112 }, Stretch = Stretch.UniformToFill },
            Opacity = f.Presence == Presence.Offline ? 0.55 : 1,
        };
        var name = new TextBlock
        {
            Text = f.Name, Style = (Style)Res["BodyStrongTextBlockStyle"], FontSize = 13, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 104,
        };
        var status = new TextBlock
        {
            Text = f.Status, Style = (Style)Res["CaptionTextBlockStyle"], FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 104,
            Foreground = f.Presence == Presence.InGame ? new SolidColorBrush(dot) : (Brush)Res["SubtleTextBrush"],
        };
        var card = new Button
        {
            Style = (Style)Res["TileStyle"], Width = 122, Height = 140, CornerRadius = new CornerRadius(22), Padding = new Thickness(8),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children = { avatar, name, status } },
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
