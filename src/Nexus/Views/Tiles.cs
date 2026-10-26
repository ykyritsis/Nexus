using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Nexus.Views;

/// <summary>
/// Fills a Control-Center glass tile: a round icon in the corner, title and status at the bottom.
/// "On" tiles turn bright frosted white with dark text, like an iOS toggle that's on.
/// </summary>
public static class Tiles
{
    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static void Set(Button tile, string glyph, string title, string? status, bool active = false)
    {
        tile.Background = active ? Res("ActiveFill") : Res("GlassFill");
        tile.Foreground = active ? Res("ActiveForeground") : Res("IdleForeground");

        if (tile.Content is not Grid grid || grid.Tag as string != "tile")
        {
            grid = new Grid { Tag = "tile" };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var circle = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(22),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = new FontIcon { FontSize = 19 },
            };
            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetRow(text, 1);
            grid.Children.Add(circle);
            grid.Children.Add(text);
            tile.Content = grid;
        }

        var c = (Border)grid.Children[0];
        c.Background = active
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(28, 0, 0, 0))
            : Res("IconCircleBrush");
        ((FontIcon)c.Child).Glyph = glyph;
        var t = (StackPanel)grid.Children[1];
        ((TextBlock)t.Children[0]).Text = title;
        var s = (TextBlock)t.Children[1];
        s.Text = status ?? "";
        s.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, status is null ? title : $"{title}, {status}");
    }

    public static bool IsActive(Button tile) => ReferenceEquals(tile.Background, Res("ActiveFill"));
}
