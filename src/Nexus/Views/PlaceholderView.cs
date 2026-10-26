using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Nexus.Views;

/// <summary>Stand-in for tabs that arrive in later updates.</summary>
public sealed class PlaceholderView : UserControl, INexusView
{
    public string Title { get; }

    public PlaceholderView(string title, string glyph, string headline, string detail)
    {
        Title = title;
        var res = Application.Current.Resources;
        Content = new StackPanel
        {
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 460,
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 48, Foreground = (Brush)res["AccentTextFillColorPrimaryBrush"], Margin = new Thickness(0, 0, 0, 8) },
                new TextBlock { Text = headline, Style = (Style)res["SubtitleTextBlockStyle"], TextAlignment = TextAlignment.Center },
                new TextBlock
                {
                    Text = detail, Style = (Style)res["BodyTextBlockStyle"], TextAlignment = TextAlignment.Center,
                    Foreground = (Brush)res["TextFillColorSecondaryBrush"],
                },
            },
        };
    }

    public void OnShown() { }
    public void OnHidden() { }
}
