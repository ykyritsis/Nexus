using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Nexus.Input;
using Nexus.Interop;
using Nexus.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Nexus.Views;

/// <summary>
/// The overlay. One borderless, always-on-top window covering the monitor the game is on: its
/// backdrop blurs the game and a compact liquid-glass panel floats in the middle. It is created once
/// at startup and kept hidden, so pressing Guide only has to show it.
/// </summary>
public sealed partial class ShellWindow : Window
{
    public static readonly string[] TabOrder = { "home", "settings", "audio", "social", "multitask", "widgets" };

    readonly App _app;
    readonly Dictionary<string, Button> _tabButtons;
    readonly Dictionary<string, FrameworkElement> _views = new();
    readonly Stack<FrameworkElement> _subPages = new();
    readonly DispatcherQueueTimer _clock, _trim;

    string _tab = "home";
    bool _visible, _warming;
    long _pressedAt = -1, _shownAt;
    IntPtr _previousForeground;

    public IntPtr Hwnd { get; }
    public PadNavigator Navigator { get; }
    public bool IsOpen => _visible;
    public UIElement NavRoot => Panel;

    public ShellWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        Navigator = new PadNavigator(this);
        _tabButtons = new()
        {
            ["home"] = TabHome, ["settings"] = TabSettings, ["audio"] = TabAudio,
            ["social"] = TabSocial, ["multitask"] = TabMulti, ["widgets"] = TabWidgets,
        };

        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new GlassBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
        AppWindow.IsShownInSwitchers = false;
        // No Windows 11 window border or corner rounding: the glass pieces draw their own edges.
        int none = unchecked((int)0xFFFFFFFE), square = 1;
        Native.DwmSetWindowAttribute(Hwnd, 34 /*DWMWA_BORDER_COLOR*/, ref none, sizeof(int));
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));
        AppWindow.Closing += (_, e) => { e.Cancel = true; Hide(); }; // Alt+F4 hides, Quit exits

        Activated += OnActivated;
        Root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { Back(); e.Handled = true; }
        };
        HideStory.Completed += (_, _) => { if (!_visible) AppWindow.Hide(); };
        // While you play, give memory the overlay isn't using back to Windows (it pages back in on open).
        _trim = DispatcherQueue.CreateTimer();
        _trim.Interval = TimeSpan.FromSeconds(20);
        _trim.IsRepeating = false;
        _trim.Tick += (_, _) =>
        {
            if (_visible) return;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
            Native.TrimWorkingSet();
        };
        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(15);
        _clock.Tick += (_, _) => UpdateHeader();

        SelectTab("home", focus: false);
    }

    // ---------------- show / hide ----------------

    /// <summary>Shows the window once off-screen without activating it, so the first real open is instant.</summary>
    public void Warm()
    {
        _warming = true;
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1600, 900));
        AppWindow.Show(false);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            AppWindow.Hide();
            _warming = false;
            Log.Info("overlay warmed up");
        });
    }

    public void Toggle(long pressedAt = -1)
    {
        if (_visible) Hide();
        else Show(pressedAt: pressedAt);
    }

    public void Show(string? tab = null, long pressedAt = -1)
    {
        _pressedAt = pressedAt;
        var fg = Native.GetForegroundWindow();
        if (fg != Hwnd) _previousForeground = fg;

        HideStory.Stop();
        Place(_previousForeground);
        if (tab != null) SelectTab(tab, focus: false);
        _visible = true;
        _shownAt = Environment.TickCount64;
        _app.Gamepad.Navigating = true;

        AppWindow.Show(true);
        Activate();
        Native.ForceForeground(Hwnd);
        ShowStory.Begin();
        ElementSoundPlayer.Play(ElementSoundKind.Show);
        UpdateHeader();
        _clock.Start();
        (ContentHost.Content as INexusView)?.OnShown();
        MoveLens(animate: false);
        FocusContent();
        Log.Info($"shown (foreground ok: {Native.GetForegroundWindow() == Hwnd})");
    }

    public void Hide(bool restoreFocus = true, [System.Runtime.CompilerServices.CallerMemberName] string by = "")
    {
        if (!_visible) return;
        Log.Info($"hidden by {by} (restore focus: {restoreFocus})");
        _visible = false;
        _app.Gamepad.Navigating = false;
        Navigator.Reset();
        _clock.Stop();
        (ContentHost.Content as INexusView)?.OnHidden();
        ElementSoundPlayer.Play(ElementSoundKind.Hide);
        ShowStory.Stop();
        HideStory.Begin(); // the window itself hides when this finishes
        _trim.Stop();
        _trim.Start();
        // Hand focus straight back to the game.
        if (restoreFocus && _previousForeground != IntPtr.Zero && Native.IsWindow(_previousForeground))
            Native.SetForegroundWindow(_previousForeground);
    }

    void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_warming) return;
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Another app took focus: get out of the way (ignoring the flicker right after opening).
            if (_visible && Environment.TickCount64 - _shownAt > 400) Hide(restoreFocus: false);
            return;
        }
        if (_pressedAt >= 0)
        {
            Log.Info($"open latency {_app.Gamepad.Now - _pressedAt} ms (Guide press to active window)");
            _pressedAt = -1;
        }
    }

    void Scrim_PointerPressed(object sender, PointerRoutedEventArgs e) => Hide();

    /// <summary>
    /// Covers the game's monitor (the backdrop blurs all of it) and sizes the panel to about half
    /// the screen width, centred.
    /// </summary>
    void Place(IntPtr near)
    {
        var (b, scale) = Native.MonitorOf(near);
        int w = b.Right - b.Left, h = b.Bottom - b.Top;
        AppWindow.MoveAndResize(new RectInt32(b.Left, b.Top, w, h));
        double dipW = w / scale, dipH = h / scale;
        Panel.Measure(new Windows.Foundation.Size(Panel.Width, double.PositiveInfinity));
        double designH = Panel.DesiredSize.Height;
        double share = _app.Settings.CompactMode ? 0.42 : 0.5;
        PanelBox.Width = Math.Min(dipW * share, dipH * 0.45 * Panel.Width / designH);
    }

    // ---------------- tabs and pages ----------------

    void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tab) SelectTab(tab, focus: false);
    }

    public void SwitchTab(int delta)
    {
        int i = Array.IndexOf(TabOrder, _tab);
        ElementSoundPlayer.Play(delta > 0 ? ElementSoundKind.MoveNext : ElementSoundKind.MovePrevious);
        SelectTab(TabOrder[(i + delta + TabOrder.Length) % TabOrder.Length], focus: true);
    }

    public void SelectTab(string tab, bool focus)
    {
        _subPages.Clear();
        _tab = tab;
        foreach (var (name, button) in _tabButtons)
            button.Opacity = name == tab ? 1.0 : 0.62;
        MoveLens(animate: _visible);
        SetContent(ViewFor(tab));
        if (focus) FocusContent();
    }

    /// <summary>Slides the glass lens under the selected tab.</summary>
    void MoveLens(bool animate)
    {
        var button = _tabButtons[_tab];
        double x;
        try { x = button.TransformToVisual(TabStrip).TransformPoint(new Windows.Foundation.Point(0, 0)).X; }
        catch { x = Array.IndexOf(TabOrder, _tab) * (button.Width + TabStrip.Spacing); }
        if (!animate) { LensSlide.X = x; return; }
        var anim = new DoubleAnimation
        {
            To = x,
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
        };
        Storyboard.SetTarget(anim, LensSlide);
        Storyboard.SetTargetProperty(anim, "X");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    FrameworkElement ViewFor(string tab)
    {
        if (_views.TryGetValue(tab, out var v)) return v;
        v = tab switch
        {
            "home" => new HomeView(_app, this),
            "settings" => new SettingsView(_app, this),
            "audio" => new AudioView(_app),
            "social" => new SocialView(this),
            "multitask" => new AppsView(_app, this),
            _ => new WidgetsView(_app, this),
        };
        _views[tab] = v;
        return v;
    }

    public void OpenSubPage(FrameworkElement page)
    {
        if (ContentHost.Content is FrameworkElement current) _subPages.Push(current);
        SetContent(page);
        FocusContent();
    }

    /// <summary>B button: leave a sub-page, or close the overlay.</summary>
    public void Back()
    {
        if (_subPages.Count > 0)
        {
            ElementSoundPlayer.Play(ElementSoundKind.GoBack);
            SetContent(_subPages.Pop());
            FocusContent();
        }
        else Hide();
    }

    void Back_Click(object sender, RoutedEventArgs e) => Back();

    void Power_Click(object sender, RoutedEventArgs e) => OpenSubPage(new PowerView(_app, this));
    void Controllers_Click(object sender, RoutedEventArgs e) => OpenSubPage(new ControllersView(_app));

    void SetContent(FrameworkElement page)
    {
        (ContentHost.Content as INexusView)?.OnHidden();
        ContentHost.Content = page;
        var view = page as INexusView;
        TitleText.Text = view?.Title ?? "";
        SubHeader.Visibility = _subPages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        YHint.Visibility = view?.YHint is null ? Visibility.Collapsed : Visibility.Visible;
        YHintText.Text = view?.YHint ?? "";
        XHint.Visibility = view?.XHint is null ? Visibility.Collapsed : Visibility.Visible;
        XHintText.Text = view?.XHint ?? "";
        if (_visible) view?.OnShown();
    }

    public INexusView? CurrentView => ContentHost.Content as INexusView;

    /// <summary>Puts keyboard focus (with its ring) on the first control of the page.</summary>
    public void FocusContent()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (ContentHost.Content is DependencyObject page &&
                FocusManager.FindFirstFocusableElement(page) is Control c)
                c.Focus(FocusState.Keyboard);
            else
                _tabButtons[_tab].Focus(FocusState.Keyboard);
        });
    }

    void UpdateHeader()
    {
        ClockText.Text = DateTime.Now.ToString("h:mm");
        var pad = _app.Gamepad;
        var slot = pad.LastActiveSlot >= 0 ? pad.LastActiveSlot : pad.FirstConnectedSlot;
        BatteryChip.Visibility = pad.AnyConnected ? Visibility.Visible : Visibility.Collapsed;
        if (pad.Battery(slot) is { } b)
        {
            BatteryGlyph.Glyph = b.wired ? "" : b.level switch { 0 => "", 1 => "", 2 => "", _ => "" };
            BatteryText.Text = b.wired ? "Wired" : b.level switch { 0 => "Empty", 1 => "Low", 2 => "Medium", _ => "Full" };
        }
        else
        {
            BatteryGlyph.Glyph = "";
            BatteryText.Text = "Controller";
        }
    }

    /// <summary>Tints the glow behind the panel, e.g. with the focused game's colour.</summary>
    public void SetAmbient(Windows.UI.Color color)
    {
        var sb = new Storyboard();
        void Fade(GradientStop stop, byte alpha)
        {
            var anim = new ColorAnimation
            {
                To = Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B),
                Duration = TimeSpan.FromMilliseconds(600),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(anim, stop);
            Storyboard.SetTargetProperty(anim, "Color");
            sb.Children.Add(anim);
        }
        Fade(AmbientInner, 0x80);
        Fade(AmbientMid, 0x30);
        sb.Begin();
    }

    /// <summary>Re-applies size settings (compact mode) while open.</summary>
    public void Relayout()
    {
        if (!_visible) return;
        Place(_previousForeground);
    }
}
