using Microsoft.UI.Xaml;

namespace Nexus.Views;

/// <summary>A page inside the overlay (a tab or a sub-page).</summary>
public interface INexusView
{
    string Title { get; }

    /// <summary>The overlay opened, or this page became the current one: refresh live data.</summary>
    void OnShown();

    /// <summary>The overlay closed or another page took over: stop timers.</summary>
    void OnHidden();

    /// <summary>Y button on the focused element. Return true if handled.</summary>
    bool HandleY(DependencyObject? focused) => false;

    /// <summary>Label for the Y hint, or null to hide it.</summary>
    string? YHint => null;

    /// <summary>X button on the focused element. Return true if handled.</summary>
    bool HandleX(DependencyObject? focused) => false;

    /// <summary>Label for the X hint, or null to hide it.</summary>
    string? XHint => null;

    /// <summary>Start (Menu) button on the focused element: options. Return true if handled.</summary>
    bool HandleStart(DependencyObject? focused) => false;

    /// <summary>Label for the Start hint, or null to hide it.</summary>
    string? StartHint => null;
}
