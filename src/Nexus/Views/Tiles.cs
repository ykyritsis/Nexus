using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace Nexus.Views;

/// <summary>
/// Fills a quick-settings tile: a slim pill with a round icon on the left and the name and state
/// beside it, like Windows 11's quick settings. Each setting has its own colour: off, the icon is
/// tinted with it; on, the whole tile fills with it, like Control Center.
/// </summary>
public static class Tiles
{
    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    // One colour per setting, by its title.
    static readonly Dictionary<string, Color> Colours = new()
    {
        ["Microphone"] = Color.FromArgb(255, 0xFF, 0x5A, 0x5F),
        ["Bluetooth"] = Color.FromArgb(255, 0x3B, 0x82, 0xF6),
        ["Airplane mode"] = Color.FromArgb(255, 0xF5, 0x9E, 0x0B),
        ["Xbox mode"] = Color.FromArgb(255, 0x2B, 0xB6, 0x73),
        ["Screenshot"] = Color.FromArgb(255, 0xA8, 0x55, 0xF7),
        ["Network"] = Color.FromArgb(255, 0x14, 0xB8, 0xA6),
        ["Keyboard"] = Color.FromArgb(255, 0x63, 0x66, 0xF1),
        ["Gamepad cursor"] = Color.FromArgb(255, 0xEC, 0x48, 0x99),
        ["Compact"] = Color.FromArgb(255, 0xEA, 0xB3, 0x08),
        ["Game Bar"] = Color.FromArgb(255, 0x84, 0xCC, 0x16),
    };
    static readonly Color Fallback = Color.FromArgb(255, 0x5A, 0xA9, 0xFF);

    public static Color ColourFor(string title) => Colours.TryGetValue(title, out var c) ? c : Fallback;

    public static void Set(Button tile, string glyph, string title, string? status, bool active = false)
    {
        var colour = ColourFor(title);
        tile.Tag = active; // IsActive reads this
        tile.Background = active ? new SolidColorBrush(Color.FromArgb(0xE6, colour.R, colour.G, colour.B)) : Res("GlassFill");
        tile.Foreground = new SolidColorBrush(Colors.White);
        tile.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        tile.VerticalContentAlignment = VerticalAlignment.Center;
        tile.Padding = new Thickness(10, 6, 10, 6);

        if (tile.Content is not Grid grid || grid.Name != "tile")
        {
            grid = new Grid { Name = "tile", ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var circle = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17), VerticalAlignment = VerticalAlignment.Center,
                Child = new FontIcon { FontSize = 15 },
            };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            text.Children.Add(new TextBlock { FontSize = 11, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            Grid.SetColumn(text, 1);
            grid.Children.Add(circle);
            grid.Children.Add(text);
            tile.Content = grid;
        }

        var c = (Border)grid.Children[0];
        c.Background = active
            ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x38, colour.R, colour.G, colour.B));
        var icon = (FontIcon)c.Child;
        icon.Glyph = glyph;
        icon.Foreground = new SolidColorBrush(active ? Colors.White : Lighter(colour));
        var t = (StackPanel)grid.Children[1];
        ((TextBlock)t.Children[0]).Text = title;
        var s = (TextBlock)t.Children[1];
        s.Text = status ?? "";
        s.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, status is null ? title : $"{title}, {status}");
    }

    public static bool IsActive(Button tile) => tile.Tag is true;

    /// <summary>A lighter version of a colour, readable on dark glass.</summary>
    static Color Lighter(Color c) => Color.FromArgb(255, (byte)(c.R + (255 - c.R) / 3), (byte)(c.G + (255 - c.G) / 3), (byte)(c.B + (255 - c.B) / 3));
}
