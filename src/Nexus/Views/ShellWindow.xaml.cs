using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nexus.Input;
using Nexus.Interop;
using Nexus.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Nexus.Views;

/// <summary>
/// The overlay. One borderless, always-on-top window covering the monitor the game is on: its
/// backdrop blurs the game and a Quick Settings style panel sits in the middle. It is created once
/// at startup and kept hidden, so pressing Guide only has to show it.
/// </summary>
public sealed partial class ShellWindow : Window
{
    public static readonly string[] TabOrder = { "home", "settings", "audio", "social", "multitask", "widgets" };

    readonly App _app;
    readonly Dictionary<string, SelectorBarItem> _tabItems;
    readonly Dictionary<string, FrameworkElement> _views = new();
    readonly Stack<FrameworkElement> _subPages = new();
    readonly DispatcherQueueTimer _clock;

    string _tab = "home";
    bool _visible, _warming, _selecting;
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
        _tabItems = new()
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
        AppWindow.Closing += (_, e) => { e.Cancel = true; Hide(); }; // Alt+F4 hides, Quit exits

        Activated += OnActivated;
        Root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { Back(); e.Handled = true; }
        };
        HideStory.Completed += (_, _) => { if (!_visible) AppWindow.Hide(); };
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

    /// <summary>Covers the game's monitor and scales the panel to a comfortable share of it.</summary>
    void Place(IntPtr near)
    {
        var (b, scale) = Native.MonitorOf(near);
        int w = b.Right - b.Left, h = b.Bottom - b.Top;
        AppWindow.MoveAndResize(new RectInt32(b.Left, b.Top, w, h));
        double dipW = w / scale, dipH = h / scale;
        double share = _app.Settings.CompactMode ? 0.66 : 0.8;
        PanelBox.Width = Math.Min(dipW * share, (dipH * 0.86) * Panel.Width / Panel.Height);
    }

    // ---------------- tabs and pages ----------------

    void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_selecting || sender.SelectedItem?.Tag is not string tab) return;
        SelectTab(tab, focus: false);
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
        _selecting = true;
        Tabs.SelectedItem = _tabItems[tab];
        _selecting = false;
        SetContent(ViewFor(tab));
        if (focus) FocusContent();
    }

    FrameworkElement ViewFor(string tab)
    {
        if (_views.TryGetValue(tab, out var v)) return v;
        v = tab switch
        {
            "home" => new HomeView(_app, this),
            "settings" => new SettingsView(_app, this),
            "audio" => new AudioView(_app),
            "social" => new PlaceholderView("Social", "",
                "Friends and parties are coming",
                "Steam friends first. Xbox friends need Microsoft sign-in that isn't open to other apps."),
            "multitask" => new PlaceholderView("Apps", "",
                "Switch between open apps",
                "Live thumbnails, switch, close and force-close arrive with the next update."),
            _ => new PlaceholderView("Widgets", "",
                "Capture, Performance and Display",
                "Screenshots and recording, an FPS and GPU monitor, and display controls arrive with the next update."),
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

    void SetContent(FrameworkElement page)
    {
        (ContentHost.Content as INexusView)?.OnHidden();
        ContentHost.Content = page;
        var view = page as INexusView;
        TitleText.Text = view?.Title ?? "";
        SubHeader.Visibility = _subPages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        YHint.Visibility = view?.YHint is null ? Visibility.Collapsed : Visibility.Visible;
        YHintText.Text = view?.YHint ?? "";
        if (_visible) view?.OnShown();
    }

    public INexusView? CurrentView => ContentHost.Content as INexusView;

    /// <summary>Puts keyboard focus (with the system focus visual) on the first control of the page.</summary>
    public void FocusContent()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (ContentHost.Content is DependencyObject page &&
                FocusManager.FindFirstFocusableElement(page) is Control c)
                c.Focus(FocusState.Keyboard);
            else
                _tabItems[_tab].Focus(FocusState.Keyboard);
        });
    }

    void UpdateHeader()
    {
        ClockText.Text = DateTime.Now.ToString("h:mm tt");
        var battery = _app.Gamepad.Battery(_app.Gamepad.LastActiveSlot);
        if (battery is { } b)
        {
            BatteryPanel.Visibility = Visibility.Visible;
            // Segoe Fluent battery glyphs: 0%, 30%, 60%, 100%; a plug when wired.
            BatteryGlyph.Glyph = b.wired ? "" : b.level switch { 0 => "", 1 => "", 2 => "", _ => "" };
            BatteryText.Text = b.wired ? "Wired" : b.level switch { 0 => "Empty", 1 => "Low", 2 => "Medium", _ => "Full" };
        }
        else BatteryPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Re-applies size settings (compact mode) while open.</summary>
    public void Relayout()
    {
        if (_visible) Place(_previousForeground);
    }
}
