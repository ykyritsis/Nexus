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
    DesktopAcrylicController? _controller;
    SystemBackdropConfiguration? _config;

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
            TintOpacity = 0.08f,
            LuminosityOpacity = 0.18f,
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
