using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
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
    /// <summary>The window that was in front before the overlay opened (usually the game).</summary>
    public IntPtr PreviousForeground => _previousForeground;
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
        // See-through window; the blur is a still frame we draw ourselves (live acrylic cost a full-screen
        // blur every frame the game drew, and dropped to a flat dark fill over games).
        SystemBackdrop = new TransparentBackdrop();
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
        // Windows also hands controller presses to the window as Gamepad* keys, and WinUI acts on them
        // (moving focus, pressing buttons) on top of Nexus's own controller handling: every press
        // counted twice, so left/right skipped an item. Nexus reads the pad itself; drop these.
        Root.PreviewKeyDown += (_, e) => SwallowGamepadKey(e);
        Root.PreviewKeyUp += (_, e) => SwallowGamepadKey(e);
        HideStory.Completed += (_, _) =>
        {
            if (_visible) return;
            // Cloak first so the window's last frame (the sharp game frame) is what disappears, never black.
            Cloak(true);
            AppWindow.Hide();
            Cloak(false);
        };
        // While you play, give memory the overlay isn't using back to Windows (it pages back in on open).
        _trim = DispatcherQueue.CreateTimer();
        _trim.Interval = TimeSpan.FromSeconds(20);
        _trim.IsRepeating = false;
        _trim.Tick += (_, _) =>
        {
            if (_visible) return;
            _hdr?.Release(); // the GPU frame is re-captured on the next open anyway
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
            Native.TrimWorkingSet();
        };
        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(15);
        _clock.Tick += (_, _) => UpdateHeader();

        SelectTab("home", focus: false);
        SetupFocusRing();
    }

    // ---------------- focus ring ----------------
    // Instead of Windows' focus outline (which jumps, and whose corners don't follow ours), one ring
    // glides between controls on the compositor thread, with an even gap and matching rounded corners.

    const float RingGap = 5, RingStroke = 2.5f;
    ShapeVisual? _ring;
    CompositionRoundedRectangleGeometry? _ringShape;
    ScrollViewer? _ringScroller;
    Control? _ringTarget;
    bool _ringShown;

    void SetupFocusRing()
    {
        var c = ElementCompositionPreview.GetElementVisual(FocusLayer).Compositor;
        // A clean white ring, like current consoles: no coloured glow.
        _ringShape = c.CreateRoundedRectangleGeometry();
        var line = c.CreateSpriteShape(_ringShape);
        line.StrokeBrush = c.CreateColorBrush(Windows.UI.Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        line.StrokeThickness = RingStroke;
        _ring = c.CreateShapeVisual();
        _ring.Size = new Vector2(4000, 2000); // bigger than the panel; shapes draw where they are
        _ring.Shapes.Add(line);
        _ring.Opacity = 0;
        ElementCompositionPreview.SetElementChildVisual(FocusLayer, _ring);
        // An exception in this static focus event isn't routed to Application.UnhandledException; it
        // takes the process down (STATUS_STOWED_EXCEPTION). The ring is cosmetic, so never let it throw.
        FocusManager.GotFocus += (_, e) =>
        {
            try { OnFocusMoved(e.NewFocusedElement as Control); }
            catch (Exception ex) { Log.Error("focus ring", ex); HideRingQuietly(); }
        };
    }

    void OnFocusMoved(Control? target)
    {
        if (_ring is null) return;
        if (!_visible || target is null || target.FocusState == FocusState.Pointer || !InPanel(target))
        {
            HideRing();
            return;
        }
        target.UseSystemFocusVisuals = false;
        if (ContentHost.Content is FrameworkElement page && IsInside(target, page))
            _lastFocus[page] = new WeakReference<Control>(target);
        AHintText.Text = ActionFor(target);
        CentreInList(target);
        _ringTarget = target;
        TrackScroller(target);
        if (target.ActualWidth <= 0) // just created: wait for its layout
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try { if (_ringTarget == target) PlaceRing(target, animate: _ringShown); }
                catch (Exception ex) { Log.Error("focus ring", ex); HideRingQuietly(); }
            });
        else PlaceRing(target, animate: _ringShown);
    }

    void PlaceRing(Control target, bool animate)
    {
        if (_ring is null || _ringShape is null || target.XamlRoot is null) return;
        Windows.Foundation.Rect r;
        try { r = target.TransformToVisual(FocusLayer).TransformBounds(new Windows.Foundation.Rect(0, 0, target.ActualWidth, target.ActualHeight)); }
        catch { return; }
        // Game tiles grow when focused; ring the size they grow to.
        double lift = target.Tag as string == "lift" ? 1.07 : 1;
        double w = r.Width * lift + RingGap * 2, h = r.Height * lift + RingGap * 2;
        double x = r.X - (r.Width * (lift - 1) / 2) - RingGap, y = r.Y - (r.Height * (lift - 1) / 2) - RingGap;
        double radius = Math.Min(target.CornerRadius.TopLeft * lift + RingGap, Math.Min(w, h) / 2);
        // Composition rejects NaN, infinite or negative sizes with E_INVALIDARG; skip such layouts.
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0) return;
        if (!double.IsFinite(radius) || radius < 0) radius = 0;
        var offset = new Vector2((float)x, (float)y);
        var size = new Vector2((float)w, (float)h);
        var corner = new Vector2((float)radius);

        if (!animate)
        {
            _ringShape.StopAnimation("Offset");
            _ringShape.StopAnimation("Size");
            _ringShape.StopAnimation("CornerRadius");
            _ringShape.Offset = offset;
            _ringShape.Size = size;
            _ringShape.CornerRadius = corner;
        }
        else
        {
            var c = _ring.Compositor;
            var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));
            void Glide(string property, Vector2 to)
            {
                var a = c.CreateVector2KeyFrameAnimation();
                a.InsertKeyFrame(1f, to, ease);
                a.Duration = TimeSpan.FromMilliseconds(170);
                _ringShape.StartAnimation(property, a);
            }
            Glide("Offset", offset);
            Glide("Size", size);
            Glide("CornerRadius", corner);
        }
        if (!_ringShown)
        {
            _ringShown = true;
            var fade = _ring.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(140);
            _ring.StartAnimation("Opacity", fade);
        }
    }

    void HideRing()
    {
        _ringTarget = null;
        TrackScroller(null);
        if (_ring is null || !_ringShown) return;
        _ringShown = false;
        _ring.StopAnimation("Opacity");
        _ring.Opacity = 0;
    }

    /// <summary>Keeps the ring on its control while a list scrolls it into view.</summary>
    void TrackScroller(Control? target)
    {
        ScrollViewer? scroller = null;
        for (DependencyObject? e = target; e != null && scroller is null; e = VisualTreeHelper.GetParent(e))
            scroller = e as ScrollViewer;
        if (ReferenceEquals(scroller, _ringScroller)) return;
        if (_ringScroller != null) _ringScroller.ViewChanged -= Scroller_ViewChanged;
        _ringScroller = scroller;
        if (scroller != null) scroller.ViewChanged += Scroller_ViewChanged;
    }

    void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        try { if (_ringTarget != null && _ringShown) PlaceRing(_ringTarget, animate: false); }
        catch (Exception ex) { Log.Error("focus ring scroll", ex); HideRingQuietly(); }
    }

    void HideRingQuietly()
    {
        try { HideRing(); } catch { }
    }

    // ---------------- show / hide ----------------

    /// <summary>Shows the window once off-screen without activating it, so the first real open is instant.</summary>
    public void Warm()
    {
        _warming = true;
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1600, 900));
        _placed = default;
        AppWindow.Show(false);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            AppWindow.Hide();
            _warming = false;
            PrepareBackdrop();
            Log.Info("overlay warmed up");
        });
    }

    public void Toggle(long pressedAt = -1)
    {
        if (_opening) return;
        if (_visible) Hide();
        else Show(pressedAt: pressedAt);
    }

    public void Show(string? tab = null, long pressedAt = -1) => _ = ShowAsync(tab, pressedAt);

    bool _opening;

    /// <summary>Captures the frame behind, then shows the overlay. A press while it's opening is ignored.</summary>
    public async Task ShowAsync(string? tab = null, long pressedAt = -1)
    {
        if (_visible) { if (tab != null) SelectTab(tab, focus: true); return; }
        if (_opening) return;
        _opening = true;
        try { await OpenAsync(tab, pressedAt); }
        finally { _opening = false; }
    }

    async Task OpenAsync(string? tab, long pressedAt)
    {
        _pressedAt = pressedAt;
        var fg = Native.GetForegroundWindow();
        if (fg != Hwnd) _previousForeground = fg;

        HideStory.Stop();
        Place(_previousForeground);
        // Reopened while still fading out: the window is still up, keep the frame we already have.
        if (!AppWindow.IsVisible) await UpdateBackdropAsync();
        // Like a console guide: reopen on the tab you left, but not deep inside one of its sub-pages.
        if (tab != null || _subPages.Count > 0) SelectTab(tab ?? _tab, focus: false);
        _visible = true;
        _shownAt = Environment.TickCount64;
        _app.Gamepad.Navigating = true;

        // Shown cloaked (invisible but drawing) until the first frame with the captured screen is ready,
        // so the very first thing you see is your own screen, then the blur fades in. No black frame.
        Cloak(true);
        AppWindow.Show(true);
        Activate();
        Native.ForceForeground(Hwnd);
        ShowStory.Begin();
        await NextFramesAsync(2);
        Cloak(false);
        ElementSoundPlayer.Play(ElementSoundKind.Show);
        UpdateHeader();
        _clock.Start();
        if (ContentHost.Content is FrameworkElement current) ShowPageSoon(current);
        MoveLens(animate: false);
        FocusContent();
        Log.Info($"shown (foreground ok: {Native.GetForegroundWindow() == Hwnd})");
    }

    public void Hide(bool restoreFocus = true, [System.Runtime.CompilerServices.CallerMemberName] string by = "")
    {
        if (!_visible) return;
        Log.Info($"hidden by {by} (restore focus: {restoreFocus})");
        _visible = false;
        HideRing();
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

    bool _quitting;

    /// <summary>Exiting: losing focus now must not start the hide animation on a window being torn down.</summary>
    public void PrepareToQuit() => _quitting = true;

    const int DWMWA_CLOAK = 13;

    void Cloak(bool on)
    {
        int value = on ? 1 : 0;
        Native.DwmSetWindowAttribute(Hwnd, DWMWA_CLOAK, ref value, sizeof(int));
    }

    /// <summary>Waits until XAML has drawn <paramref name="count"/> frames (at most ~100 ms).</summary>
    static Task NextFramesAsync(int count)
    {
        var done = new TaskCompletionSource();
        int seen = 0;
        void OnRendering(object? s, object e)
        {
            if (++seen < count) return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            done.TrySetResult();
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        return Task.WhenAny(done.Task, Task.Delay(100)).ContinueWith(_ => Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering, TaskScheduler.FromCurrentSynchronizationContext());
    }

    bool _loggedGamepadKeys;

    void SwallowGamepadKey(KeyRoutedEventArgs e)
    {
        int key = (int)e.Key;
        if (key < (int)Windows.System.VirtualKey.GamepadA || key > (int)Windows.System.VirtualKey.GamepadRightThumbstickLeft) return;
        e.Handled = true;
        if (!_loggedGamepadKeys) { _loggedGamepadKeys = true; Log.Info($"ignoring WinUI's own gamepad keys (first: {e.Key})"); }
    }

    void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_warming || _quitting) return;
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
    RectInt32 _placed;
    Native.RECT _monitor;
    WriteableBitmap? _backdrop, _sharpBitmap;

    HdrBackdrop? _hdr;
    int _hdrFailures;

    /// <summary>The SDR white level of the overlay's display when it's in HDR mode, else 0.</summary>
    double SdrWhiteNits()
    {
        try
        {
            var info = Microsoft.Graphics.Display.DisplayInformation.CreateForWindowId(AppWindow.Id).GetAdvancedColorInfo();
            return info.CurrentAdvancedColorKind == Microsoft.Graphics.Display.DisplayAdvancedColorKind.HighDynamicRange ? info.SdrWhiteLevelInNits : 0;
        }
        catch { return 0; }
    }

    /// <summary>Sets up the GPU device and effects ahead of time, so the first open doesn't pay for it.</summary>
    void PrepareBackdrop()
    {
        try { if (HdrBackdrop.Supported) _hdr ??= new HdrBackdrop(SharpFrame, Backdrop); }
        catch (Exception ex) { Log.Warn($"HDR backdrop unavailable: {ex.Message}"); _hdrFailures = 3; }
    }

    /// <summary>
    /// Grabs and blurs the screen behind the overlay, before the overlay is shown. HDR-aware capture
    /// first (keeps HDR brightness); GDI (8-bit SDR) if that isn't available or fails.
    /// </summary>
    async Task UpdateBackdropAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool hdr = false;
        if (_hdrFailures < 3 && HdrBackdrop.Supported)
        {
            try
            {
                _hdr ??= new HdrBackdrop(SharpFrame, Backdrop);
                hdr = await _hdr.CaptureAsync(Native.MonitorHandle(_previousForeground), SdrWhiteNits(), TimeSpan.FromMilliseconds(250));
            }
            catch (Exception ex) { Log.Warn($"HDR backdrop unavailable: {ex.Message}"); }
            _hdrFailures = hdr ? 0 : _hdrFailures + 1;
        }
        if (hdr)
        {
            BackdropImage.Visibility = Visibility.Collapsed;
            SharpImage.Visibility = Visibility.Collapsed;
        }
        else
        {
            _hdr?.Hide();
            var shot = ScreenBlur.Capture(_monitor);
            if (shot is { } s)
            {
                if (_backdrop is null || _backdrop.PixelWidth != s.width || _backdrop.PixelHeight != s.height)
                    _backdrop = new WriteableBitmap(s.width, s.height);
                using (var stream = _backdrop.PixelBuffer.AsStream()) stream.Write(s.pixels, 0, s.pixels.Length);
                _backdrop.Invalidate();
                BackdropImage.Source = _backdrop;
                if (_sharpBitmap is null || _sharpBitmap.PixelWidth != s.width || _sharpBitmap.PixelHeight != s.height)
                    _sharpBitmap = new WriteableBitmap(s.width, s.height);
                using (var stream = _sharpBitmap.PixelBuffer.AsStream()) stream.Write(s.sharp, 0, s.sharp.Length);
                _sharpBitmap.Invalidate();
                SharpImage.Source = _sharpBitmap;
            }
            else { BackdropImage.Source = null; SharpImage.Source = null; }
            BackdropImage.Visibility = Visibility.Visible;
            SharpImage.Visibility = Visibility.Visible;
        }
        if (clock.ElapsedMilliseconds > 40) Log.Info($"backdrop ({(hdr ? "HDR" : "GDI")}) took {clock.ElapsedMilliseconds} ms");
    }

    void Place(IntPtr near)
    {
        var (b, scale) = Native.MonitorOf(near);
        _monitor = b;
        int w = b.Right - b.Left, h = b.Bottom - b.Top;
        // One pixel short of the full monitor: Windows treats a borderless window that exactly covers
        // the screen as a full-screen app and turns off its acrylic, leaving a flat dark fill.
        var rect = new RectInt32(b.Left, b.Top, w, h - 1);
        if (!rect.Equals(_placed)) { AppWindow.MoveAndResize(rect); _placed = rect; }
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

    /// <summary>The next page slides in from the side you moved towards.</summary>
    void SlideFrom(int direction)
    {
        if (ContentHost.ContentTransitions is { Count: > 0 } t && t[0] is ContentThemeTransition slide)
            slide.HorizontalOffset = direction >= 0 ? 40 : -40;
    }

    public void SwitchTab(int delta)
    {
        int i = Array.IndexOf(TabOrder, _tab);
        ElementSoundPlayer.Play(delta > 0 ? ElementSoundKind.MoveNext : ElementSoundKind.MovePrevious);
        SelectTab(TabOrder[(i + delta + TabOrder.Length) % TabOrder.Length], focus: true);
    }

    public void SelectTab(string tab, bool focus)
    {
        SlideFrom(Array.IndexOf(TabOrder, tab) - Array.IndexOf(TabOrder, _tab));
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
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
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
        SlideFrom(+1);
        SetContent(page);
        FocusContent();
    }

    /// <summary>B button: leave a sub-page, or close the overlay.</summary>
    public void Back()
    {
        if (_subPages.Count > 0)
        {
            ElementSoundPlayer.Play(ElementSoundKind.GoBack);
            SlideFrom(-1);
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
        RefreshHints();
        if (_visible) ShowPageSoon(page);
    }

    /// <summary>Re-reads the current page's X/Y hints (for pages whose actions depend on what they show).</summary>
    public void RefreshHints()
    {
        var view = CurrentView;
        YHint.Visibility = view?.YHint is null ? Visibility.Collapsed : Visibility.Visible;
        YHintText.Text = view?.YHint ?? "";
        XHint.Visibility = view?.XHint is null ? Visibility.Collapsed : Visibility.Visible;
        XHintText.Text = view?.XHint ?? "";
    }

    public INexusView? CurrentView => ContentHost.Content as INexusView;

    /// <summary>
    /// A page refreshes its data (audio devices, mixer, lists) just after it appears, not before: the
    /// switch draws at once, and flicking past a tab with LB/RB skips its refresh entirely.
    /// </summary>
    void ShowPageSoon(FrameworkElement page)
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_visible && ReferenceEquals(ContentHost.Content, page)) (page as INexusView)?.OnShown();
        });
    }

    /// <summary>Puts keyboard focus (with its ring) on the first control of the page.</summary>
    public void FocusContent()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            // Like a console: back on a page (or reopening the guide) you land where you left off.
            if (ContentHost.Content is FrameworkElement page && _lastFocus.TryGetValue(page, out var weak)
                && weak.TryGetTarget(out var last) && last.IsEnabled && last.Visibility == Visibility.Visible
                && IsInside(last, page) && last.Focus(FocusState.Keyboard))
                return;
            if (ContentHost.Content is DependencyObject p &&
                FocusManager.FindFirstFocusableElement(p) is Control c)
                c.Focus(FocusState.Keyboard);
            else
                _tabButtons[_tab].Focus(FocusState.Keyboard);
        });
    }

    readonly Dictionary<FrameworkElement, WeakReference<Control>> _lastFocus = new();

    /// <summary>The A prompt says what A will do here.</summary>
    static string ActionFor(Control target) => target switch
    {
        _ when target.Tag as string == "lift" => "Play",
        ToggleSwitch or Microsoft.UI.Xaml.Controls.Primitives.ToggleButton => "Toggle",
        ComboBox => "Choose",
        Slider => "Adjust",
        _ => "Select",
    };

    /// <summary>In a sideways list the focused item glides to the middle, so you always see what's next.</summary>
    static void CentreInList(Control target)
    {
        for (DependencyObject? e = VisualTreeHelper.GetParent(target); e != null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is ScrollViewer sv)
            {
                if (sv.HorizontalScrollMode != ScrollMode.Disabled && sv.ScrollableWidth > 0)
                    target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, HorizontalAlignmentRatio = 0.5 });
                return;
            }
        }
    }

    /// <summary>
    /// After a page rebuilds its items (cards loaded a moment after opening), focus can be left on an
    /// element that is gone, which leaves the controller with nothing to move from. Put it back.
    /// </summary>
    public void EnsureFocus(Control? preferred = null)
    {
        if (!_visible) return;
        var focused = FocusManager.GetFocusedElement(Panel.XamlRoot) as DependencyObject;
        if (focused != null && IsInside(focused, Panel)) return;
        if (preferred != null && preferred.Focus(FocusState.Keyboard)) return;
        FocusContent();
    }

    public bool InPanel(DependencyObject element) => IsInside(element, Panel);

    static bool IsInside(DependencyObject element, DependencyObject root)
    {
        for (var e = element; e != null; e = VisualTreeHelper.GetParent(e))
            if (ReferenceEquals(e, root)) return true;
        return false;
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

    /// <summary>Tints the glow behind the panel, e.g. with the focused game's colour (a crossfade).</summary>
    public void SetAmbient(Windows.UI.Color color)
    {
        if (color == _ambient) return;
        _ambient = color;
        bool toB = AmbientA.Opacity >= 0.5;
        var (show, hide) = toB ? (AmbientB, AmbientA) : (AmbientA, AmbientB);
        var (inner, mid) = toB ? (AmbientBInner, AmbientBMid) : (AmbientAInner, AmbientAMid);
        inner.Color = Windows.UI.Color.FromArgb(0x1E, color.R, color.G, color.B);
        mid.Color = Windows.UI.Color.FromArgb(0x08, color.R, color.G, color.B);
        var sb = new Storyboard();
        void Fade(UIElement e, double to)
        {
            var anim = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(450) };
            Storyboard.SetTarget(anim, e);
            Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
        }
        Fade(show, 1);
        Fade(hide, 0);
        sb.Begin();
    }

    Windows.UI.Color _ambient;

    /// <summary>Re-applies size settings (compact mode) while open.</summary>
    public void Relayout()
    {
        if (!_visible) return;
        Place(_previousForeground);
    }
}
