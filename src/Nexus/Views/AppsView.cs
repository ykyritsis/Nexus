using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.Providers;

namespace Nexus.Views;

/// <summary>
/// Apps tab: every open app as a compact glass card with a live thumbnail (the same DWM previews
/// Alt+Tab uses), like Xbox mode's task view. A switches to the app, X closes it, Y ends a frozen
/// app (press Y twice). Delete also closes, for mouse and keyboard.
/// </summary>
public sealed class AppsView : UserControl, INexusView
{
    sealed class Card
    {
        public required OpenWindow Window;
        public required Border Root;
        public required Button Main;
        public required Border Preview;
        public required TextBlock Title;
        public IntPtr Thumb;
        public (int x, int y, int w, int h, bool visible) Placed; // last position sent to DWM
    }

    readonly App _app;
    readonly ShellWindow _shell;
    readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly ScrollViewer _scroller;
    readonly TextBlock _empty;
    readonly List<Card> _list = new();
    readonly DispatcherQueueTimer _place, _refresh;
    Card? _armed; // card waiting for a second Y to force-close
    string _key = "";

    public string Title => "Apps";
    public string? YHint => "End task";
    public string? XHint => "Close";

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
        foreach (var c in _list) { Providers.OpenWindows.Unregister(c.Thumb); c.Thumb = IntPtr.Zero; c.Placed = default; }
    }

    void Refresh()
    {
        var windows = Providers.OpenWindows.List();
        var key = string.Join("|", windows.Select(w => w.Handle));
        _empty.Visibility = windows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (key == _key)
        {
            // An app may have been resized: let the next placement refit its thumbnail.
            foreach (var c in _list) c.Placed = default;
            foreach (var c in _list)
                if (windows.FirstOrDefault(w => w.Handle == c.Window.Handle) is { } w && w.Title != c.Window.Title)
                    c.Title.Text = w.Title;
            return;
        }
        _key = key;
        // Rebuilding drops focus; put it back on the same spot (the next app after one was closed).
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
        int at = _list.FindIndex(c => ReferenceEquals(c.Main, focused));
        foreach (var c in _list) Providers.OpenWindows.Unregister(c.Thumb);
        _list.Clear();
        _armed = null;
        _cards.Children.Clear();
        foreach (var w in windows) _cards.Children.Add(MakeCard(w).Root);
        if (at >= 0)
        {
            if (_list.Count > 0) _list[Math.Min(at, _list.Count - 1)].Main.Focus(FocusState.Keyboard);
            else _shell.FocusContent();
        }
    }

    Card MakeCard(OpenWindow w)
    {
        var res = Application.Current.Resources;
        var preview = new Border { Width = 196, Height = 112, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 0, 0)) };

        var icon = new Image { Width = 18, Height = 18 };
        _ = SetIconAsync(icon, w.ExePath);
        var title = new TextBlock { Text = w.Title, Style = (Style)res["CaptionTextBlockStyle"], FontSize = 13, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 168, VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(4, 0, 0, 0), Children = { icon, title } };

        var main = new Button
        {
            Style = (Style)res["TileStyle"], Padding = new Thickness(7), CornerRadius = new CornerRadius(20),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel { Spacing = 7, Children = { preview, header } },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(main, $"Switch to {w.Title}");

        var card = new Card { Window = w, Root = new Border { Child = main }, Main = main, Preview = preview, Title = title };
        main.Click += (_, _) =>
        {
            _shell.Hide(restoreFocus: false);
            Providers.OpenWindows.SwitchTo(w.Handle);
        };
        main.GotFocus += (_, _) => { if (_armed != card) Disarm(); };
        main.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Delete) { CloseCard(card); e.Handled = true; } };
        _list.Add(card);
        return card;
    }

    void CloseCard(Card card)
    {
        Providers.OpenWindows.Close(card.Window.Handle);
        DispatcherQueue.GetForCurrentThread().TryEnqueue(async () => { await Task.Delay(500); _key = ""; Refresh(); });
    }

    /// <summary>X on a card closes that app, like Xbox mode's task view.</summary>
    public bool HandleX(DependencyObject? focused)
    {
        var card = _list.FirstOrDefault(c => ReferenceEquals(c.Main, focused));
        if (card is null) return false;
        CloseCard(card);
        return true;
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
            const double inset = 2;
            var place = ((int)((r.Left + inset) * scale), (int)((r.Top + inset) * scale),
                (int)((r.Width - inset * 2) * scale), (int)((r.Height - inset * 2) * scale), inside);
            if (place == c.Placed) continue; // only talk to DWM when a card actually moved (scrolling)
            c.Placed = place;
            Providers.OpenWindows.Place(c.Thumb, place.Item1, place.Item2, place.Item3, place.Item4, inside);
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
        card.Title.Text = "Press Y again to end task";
        card.Title.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 140, 120));
        return true;
    }

    void Disarm()
    {
        if (_armed is null) return;
        _armed.Title.Text = _armed.Window.Title;
        _armed.Title.ClearValue(TextBlock.ForegroundProperty);
        _armed = null;
    }
}
