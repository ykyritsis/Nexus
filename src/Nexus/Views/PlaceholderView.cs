using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Nexus.Views;

/// <summary>Stand-in for tabs that arrive in later phases.</summary>
public sealed class PlaceholderView : UserControl, INexusView
{
    public string Title { get; }

    public PlaceholderView(string title, string glyph, string headline, string detail)
    {
        Title = title;
        var subtle = (Brush)Application.Current.Resources["SubtleTextBrush"];
        Content = new StackPanel
        {
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 520,
            Children =
            {
                new Border
                {
                    Width = 96, Height = 96, CornerRadius = new CornerRadius(48),
                    Background = (Brush)Application.Current.Resources["GlassFill"],
                    BorderBrush = (Brush)Application.Current.Resources["GlassRim"],
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new FontIcon { Glyph = glyph, FontSize = 40 },
                },
                new TextBlock
                {
                    Text = headline, FontSize = 19, FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                },
                new TextBlock
                {
                    Text = detail, FontSize = 15, Foreground = subtle,
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                },
            },
        };
    }

    public void OnShown() { }
    public void OnHidden() { }
}
