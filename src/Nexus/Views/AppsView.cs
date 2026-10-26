using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.Providers;

namespace Nexus.Views;

/// <summary>
/// Apps tab: every open app as a glass card with a live thumbnail (the same DWM previews Alt+Tab
/// uses). A switches to the app, the X closes it, Y ends a frozen app (press Y twice).
/// </summary>
public sealed class AppsView : UserControl, INexusView
{
    sealed class Card
    {
        public required OpenWindow Window;
        public required Border Root;
        public required Button Main;
        public required Border Preview;
        public required TextBlock Hint;
        public IntPtr Thumb;
    }

    readonly App _app;
    readonly ShellWindow _shell;
    readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
    readonly ScrollViewer _scroller;
    readonly TextBlock _empty;
    readonly List<Card> _list = new();
    readonly DispatcherQueueTimer _place, _refresh;
    Card? _armed; // card waiting for a second Y to force-close
    string _key = "";

    public string Title => "Apps";
    public string? YHint => "End task";

    public AppsView(App app, ShellWindow shell)
    {
        _app = app;
        _shell = shell;
        _scroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(6), Content = _cards,
        };
        _empty = new TextBlock
        {
            Text = "No other apps are open.", Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["SubtleTextBrush"],
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        Content = new Grid { Children = { _scroller, _empty } };

        var queue = DispatcherQueue.GetForCurrentThread();
        _place = queue.CreateTimer();
        _place.Interval = TimeSpan.FromMilliseconds(120);
        _place.Tick += (_, _) => PlaceThumbs();
        _refresh = queue.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(2);
        _refresh.Tick += (_, _) => Refresh();
    }

    public void OnShown()
    {
        _key = "";
        Refresh();
        _place.Start();
        _refresh.Start();
    }

    public void OnHidden()
    {
        _place.Stop();
        _refresh.Stop();
        foreach (var c in _list) { Providers.OpenWindows.Unregister(c.Thumb); c.Thumb = IntPtr.Zero; }
    }

    void Refresh()
    {
        var windows = Providers.OpenWindows.List();
        var key = string.Join("|", windows.Select(w => w.Handle));
        _empty.Visibility = windows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (key == _key)
        {
            foreach (var c in _list)
                if (windows.FirstOrDefault(w => w.Handle == c.Window.Handle) is { } w && w.Title != c.Window.Title)
                    ((TextBlock)((StackPanel)c.Main.Tag!).Children[1]).Text = w.Title;
            return;
        }
        _key = key;
        foreach (var c in _list) Providers.OpenWindows.Unregister(c.Thumb);
        _list.Clear();
        _cards.Children.Clear();
        foreach (var w in windows) _cards.Children.Add(MakeCard(w).Root);
    }

    Card MakeCard(OpenWindow w)
    {
        var res = Application.Current.Resources;
        var preview = new Border { Height = 132, CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 0, 0)) };

        var icon = new Image { Width = 20, Height = 20 };
        _ = SetIconAsync(icon, w.ExePath);
        var title = new TextBlock { Text = w.Title, Style = (Style)res["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 196 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, title } };

        var main = new Button
        {
            Style = (Style)res["TileStyle"], Padding = new Thickness(8), CornerRadius = new CornerRadius(22),
            Content = new StackPanel { Spacing = 8, Children = { preview, header } }, Tag = header,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(main, $"Switch to {w.Title}");

        var hint = new TextBlock { Style = (Style)res["CaptionTextBlockStyle"], Foreground = (Brush)res["SubtleTextBrush"], VerticalAlignment = VerticalAlignment.Center, Text = "Switch" };
        var close = new Button { Style = (Style)res["IconButtonStyle"], Background = (Brush)res["IconCircleBrush"], Content = new FontIcon { Glyph = "", FontSize = 14 } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, $"Close {w.Title}");
        var footer = new Grid { Margin = new Thickness(10, 0, 4, 0) };
        footer.Children.Add(hint);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(close);

        var root = new Border
        {
            Width = 262, Child = new StackPanel { Spacing = 8, Children = { main, footer } },
        };
        var card = new Card { Window = w, Root = root, Main = main, Preview = preview, Hint = hint };
        main.Click += (_, _) =>
        {
            _shell.Hide(restoreFocus: false);
            Providers.OpenWindows.SwitchTo(w.Handle);
        };
        main.GotFocus += (_, _) => { if (_armed != card) Disarm(); };
        close.Click += (_, _) => { Providers.OpenWindows.Close(w.Handle); _key = ""; };
        _list.Add(card);
        return card;
    }

    static async Task SetIconAsync(Image img, string? exe)
    {
        if (await Icons.LoadAsync(exe, 64) is { } src) img.Source = src;
    }

    /// <summary>Live previews sit on top of the overlay window, lined up with each card's preview box.</summary>
    void PlaceThumbs()
    {
        if (XamlRoot is null || !_shell.IsOpen) return;
        double scale = XamlRoot.RasterizationScale;
        Windows.Foundation.Rect view;
        try { view = _scroller.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, _scroller.ActualWidth, _scroller.ActualHeight)); }
        catch { return; }
        foreach (var c in _list)
        {
            if (c.Thumb == IntPtr.Zero) c.Thumb = Providers.OpenWindows.Register(_shell.Hwnd, c.Window.Handle);
            Windows.Foundation.Rect r;
            try { r = c.Preview.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, c.Preview.ActualWidth, c.Preview.ActualHeight)); }
            catch { continue; }
            bool inside = r.Width > 0 && r.Left >= view.Left - 1 && r.Right <= view.Right + 1;
            const double inset = 6;
            Providers.OpenWindows.Place(c.Thumb, (int)((r.Left + inset) * scale), (int)((r.Top + inset) * scale),
                (int)((r.Width - inset * 2) * scale), (int)((r.Height - inset * 2) * scale), inside);
        }
    }

    /// <summary>Y on a card: the first press asks, the second ends the app's process.</summary>
    public bool HandleY(DependencyObject? focused)
    {
        var card = _list.FirstOrDefault(c => ReferenceEquals(c.Main, focused));
        if (card is null) return false;
        if (_armed == card)
        {
            Providers.OpenWindows.ForceClose(card.Window.ProcessId);
            _armed = null;
            _key = "";
            Refresh();
            return true;
        }
        Disarm();
        _armed = card;
        card.Hint.Text = "Press Y again to end task";
        card.Hint.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 140, 120));
        return true;
    }

    void Disarm()
    {
        if (_armed is null) return;
        _armed.Hint.Text = "Switch";
        _armed.Hint.Foreground = (Brush)Application.Current.Resources["SubtleTextBrush"];
        _armed = null;
    }
}
