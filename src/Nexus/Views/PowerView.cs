using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.Providers;
using Windows.UI;

namespace Nexus.Views;

/// <summary>
/// Power page, opened from the power button in the corner: a short row of round glass buttons, like
/// a console's power menu. Restart, Shut down and Sign out ask for a second press, so a stray A
/// can't end a session.
/// </summary>
public sealed class PowerView : UserControl, INexusView
{
    sealed record Item(string Glyph, string Title, string? Confirm, Action Act);

    sealed class Choice
    {
        public required Item Item;
        public required Button Button;
        public required Border Circle;
        public required TextBlock Label;
    }

    readonly ShellWindow _shell;
    readonly List<Choice> _choices = new();
    Choice? _armed;

    public string Title => "Power";

    static ResourceDictionary Res => Application.Current.Resources;
    static readonly Brush WarnFill = new SolidColorBrush(Color.FromArgb(80, 255, 90, 80));
    static readonly Brush WarnText = new SolidColorBrush(Color.FromArgb(255, 255, 160, 150));

    public PowerView(App app, ShellWindow shell)
    {
        _shell = shell;
        var items = new List<Item>
        {
            new("", "Sleep", null, () => { _shell.Hide(restoreFocus: false); Power.Sleep(); }),
            new("", "Restart", "Press again", Power.Restart),
            new("", "Shut down", "Press again", Power.ShutDown),
            new("", "Lock", null, () => { _shell.Hide(restoreFocus: false); Power.Lock(); }),
            new("", "Sign out", "Press again", Power.SignOut),
        };
        if (SystemStatus.XboxModeSupported())
            items.Add(new("", SystemStatus.XboxModeActive() ? "Leave Xbox mode" : "Xbox mode", null,
                () => { _shell.Hide(); SystemStatus.ToggleXboxMode(); }));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in items) row.Children.Add(Make(item));
        Content = row;
    }

    UIElement Make(Item item)
    {
        var circle = new Border
        {
            Width = 60, Height = 60, CornerRadius = new CornerRadius(30),
            Background = (Brush)Res["GlassFill"], BorderBrush = (Brush)Res["GlassRim"], BorderThickness = new Thickness(1),
            Child = new FontIcon { Glyph = item.Glyph, FontSize = 20 },
        };
        var label = new TextBlock
        {
            Text = item.Title, Style = (Style)Res["CaptionTextBlockStyle"], FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.NoWrap,
        };
        // The button is just the circle (so the focus ring hugs it); the label sits underneath.
        var button = new Button
        {
            Style = (Style)Res["IconButtonStyle"], Width = 60, Height = 60, CornerRadius = new CornerRadius(30),
            HorizontalAlignment = HorizontalAlignment.Center, Content = circle,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, item.Title);
        var choice = new Choice { Item = item, Button = button, Circle = circle, Label = label };
        button.Click += (_, _) => Press(choice);
        button.GotFocus += (_, _) => { if (_armed != null && _armed != choice) Disarm(); };
        _choices.Add(choice);
        return new StackPanel { Spacing = 10, Width = 108, Children = { button, label } };
    }

    void Press(Choice c)
    {
        if (c.Item.Confirm != null && _armed != c)
        {
            Disarm();
            _armed = c;
            c.Circle.Background = WarnFill;
            c.Label.Text = c.Item.Confirm;
            c.Label.Foreground = WarnText;
            return;
        }
        c.Item.Act();
    }

    void Disarm()
    {
        if (_armed is null) return;
        _armed.Circle.Background = (Brush)Res["GlassFill"];
        _armed.Label.Text = _armed.Item.Title;
        _armed.Label.ClearValue(TextBlock.ForegroundProperty);
        _armed = null;
    }

    public void OnShown() { }
    public void OnHidden() => Disarm();
}
