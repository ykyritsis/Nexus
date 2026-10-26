using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Nexus.Interop;
using Windows.Graphics;
using WinRT.Interop;

namespace Nexus.Views;

/// <summary>
/// A small glass notification at the top of the screen (controller connected, battery low), like a
/// console's. It never takes focus from the game and hides itself after a few seconds. The window is
/// made on first use and then reused.
/// </summary>
public sealed class ToastWindow : Window
{
    const int WidthDip = 320, HeightDip = 64, TopDip = 24;

    readonly IntPtr _hwnd;
    readonly FontIcon _glyph = new() { FontSize = 18 };
    readonly TextBlock _title, _detail;
    readonly Grid _card;
    readonly DispatcherQueueTimer _hide;

    public ToastWindow()
    {
        var res = Application.Current.Resources;
        _title = new TextBlock { Style = (Style)res["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        _detail = new TextBlock { Style = (Style)res["CaptionTextBlockStyle"], Foreground = (Brush)res["SubtleTextBrush"], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        var badge = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = (Brush)res["IconCircleBrush"],
            BorderBrush = (Brush)res["GlassRim"], BorderThickness = new Thickness(1), Child = _glyph,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Children = { _title, _detail } };
        _card = new Grid { ColumnSpacing = 12, Padding = new Thickness(13, 0, 16, 0), Background = (Brush)res["GlassFill"] };
        _card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _card.ColumnDefinitions.Add(new ColumnDefinition());
        badge.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        _card.Children.Add(badge);
        _card.Children.Add(text);
        Content = _card;

        _hwnd = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        // Stays frosted although it's never the active window; darker than the overlay so it reads over a game.
        SystemBackdrop = new GlassBackdrop(tint: 0.45f, luminosity: 0.6f);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
        AppWindow.IsShownInSwitchers = false;
        // Never activated (the game keeps focus) and not a taskbar/Alt+Tab window.
        Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        AppWindow.Closing += (_, e) => { e.Cancel = true; AppWindow.Hide(); };

        _hide = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hide.IsRepeating = false;
        _hide.Tick += (_, _) => FadeOut();
    }

    /// <summary>Shows a notification on the screen you're playing on.</summary>
    public void Show(string glyph, string title, string detail, int seconds = 4)
    {
        _glyph.Glyph = glyph;
        _title.Text = title;
        _detail.Text = detail;

        var (b, scale) = Native.MonitorOf(Native.GetForegroundWindow());
        int w = (int)(WidthDip * scale), h = (int)(HeightDip * scale);
        int x = b.Left + (b.Right - b.Left - w) / 2, y = b.Top + (int)(TopDip * scale);
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        AppWindow.Show(false); // false: don't activate
        AppWindow.MoveInZOrderAtTop();

        var visual = ElementCompositionPreview.GetElementVisual(_card);
        var c = visual.Compositor;
        var spring = c.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.56f), new Vector2(0.64f, 1f));
        var drop = c.CreateVector3KeyFrameAnimation();
        drop.InsertKeyFrame(0f, new Vector3(0, -18, 0));
        drop.InsertKeyFrame(1f, Vector3.Zero, spring);
        drop.Duration = TimeSpan.FromMilliseconds(420);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(200);
        ElementCompositionPreview.SetIsTranslationEnabled(_card, true);
        visual.StartAnimation("Translation", drop);
        visual.StartAnimation("Opacity", fade);

        _hide.Stop();
        _hide.Interval = TimeSpan.FromSeconds(seconds);
        _hide.Start();
    }

    void FadeOut()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_card);
        var c = visual.Compositor;
        var batch = c.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_hide.IsRunning) return; // another toast arrived meanwhile
            AppWindow.Hide();
            Hidden?.Invoke();
        });
    }

    /// <summary>Raised once the toast has gone (the app trims memory then).</summary>
    public event Action? Hidden;
}
