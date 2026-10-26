using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Nexus.Views;

/// <summary>
/// Frosted glass across the whole window: Windows' acrylic blur of whatever is behind (the game),
/// lightly tinted. Unlike the stock DesktopAcrylicBackdrop it stays blurred even when the window loses
/// focus for a moment, so it never flashes to a flat colour.
/// </summary>
public sealed class GlassBackdrop : SystemBackdrop
{
    readonly float _tint, _luminosity;
    DesktopAcrylicController? _controller;
    SystemBackdropConfiguration? _config;

    /// <param name="tint">How much of the dark tint covers the blur (the overlay uses very little).</param>
    /// <param name="luminosity">How much the blur is evened out.</param>
    public GlassBackdrop(float tint = 0.08f, float luminosity = 0.18f)
    {
        _tint = tint;
        _luminosity = luminosity;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _config = GetDefaultSystemBackdropConfiguration(target, root);
        _config.IsInputActive = true; // keep the blur even when inactive
        _controller = new DesktopAcrylicController
        {
            // Thin acrylic with a light tint: the game stays recognisable, just softly frosted.
            Kind = DesktopAcrylicKind.Thin,
            TintColor = Color.FromArgb(255, 20, 24, 34),
            TintOpacity = _tint,
            LuminosityOpacity = _luminosity,
            FallbackColor = Color.FromArgb(200, 30, 34, 44),
        };
        _controller.AddSystemBackdropTarget(target);
        _controller.SetSystemBackdropConfiguration(_config);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }
}
