using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Nexus.Views;

namespace Nexus.Input;

/// <summary>
/// Controller navigation for the overlay. WinUI desktop windows don't get gamepad focus navigation
/// on their own, so this maps the pad to focus moves and control actions:
///   d-pad / left stick  move focus (along a focused slider's axis: adjust it)
///   A                   press, toggle, or open a drop-down
///   B                   close a drop-down, leave a sub-page, or close the overlay
///   LB / RB             previous / next tab
///   Y                   page-specific (e.g. favourite in the volume mixer)
/// Runs on the UI thread.
/// </summary>
public sealed class PadNavigator
{
    readonly ShellWindow _shell;
    ComboBox? _openCombo;
    int _comboStartIndex;

    public PadNavigator(ShellWindow shell) => _shell = shell;

    public void Reset()
    {
        if (_openCombo != null) _openCombo.IsDropDownOpen = false;
        _openCombo = null;
    }

    public void Handle(Pad button)
    {
        if (!_shell.IsOpen) return;
        Services.Log.Info($"pad {button}");
        if (_openCombo != null && HandleCombo(button)) return;

        var focused = FocusManager.GetFocusedElement(_shell.NavRoot.XamlRoot) as DependencyObject;
        // A slider takes the pad axis it runs along; the other axis moves focus as usual.
        var slider = focused as Slider;
        bool vertical = slider?.Orientation == Orientation.Vertical;
        switch (button)
        {
            case Pad.Up:
                if (slider != null && vertical) Nudge(slider, +1); else Move(FocusNavigationDirection.Up);
                break;
            case Pad.Down:
                if (slider != null && vertical) Nudge(slider, -1); else Move(FocusNavigationDirection.Down);
                break;
            case Pad.Left:
                if (slider != null && !vertical) Nudge(slider, -1); else Move(FocusNavigationDirection.Left);
                break;
            case Pad.Right:
                if (slider != null && !vertical) Nudge(slider, +1); else Move(FocusNavigationDirection.Right);
                break;
            case Pad.A: Activate(focused); break;
            case Pad.B: _shell.Back(); break;
            case Pad.LB: _shell.SwitchTab(-1); break;
            case Pad.RB: _shell.SwitchTab(+1); break;
            case Pad.Y: _shell.CurrentView?.HandleY(focused); break;
        }
    }

    void Move(FocusNavigationDirection direction)
    {
        var options = new FindNextElementOptions
        {
            SearchRoot = _shell.NavRoot,
            XYFocusNavigationStrategyOverride = XYFocusNavigationStrategyOverride.Projection,
        };
        if (FocusManager.FindNextElement(direction, options) is Control next)
            next.Focus(FocusState.Keyboard); // keyboard state = visible focus ring
    }

    static void Nudge(Slider s, int dir)
    {
        double step = s.SmallChange > 0 ? s.SmallChange : 1;
        s.Value = Math.Clamp(s.Value + dir * step * 2, s.Minimum, s.Maximum);
    }

    void Activate(DependencyObject? focused)
    {
        switch (focused)
        {
            case ToggleSwitch ts:
                ts.IsOn = !ts.IsOn;
                break;
            case ComboBox cb when cb.Items.Count > 0:
                _openCombo = cb;
                _comboStartIndex = cb.SelectedIndex;
                cb.IsDropDownOpen = true;
                break;
            case ButtonBase b:
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(b);
                if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
                else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
                break;
        }
    }

    /// <summary>While a drop-down is open: up/down pick, A confirms, B cancels.</summary>
    bool HandleCombo(Pad button)
    {
        var cb = _openCombo!;
        if (!cb.IsDropDownOpen) { _openCombo = null; return false; }
        switch (button)
        {
            case Pad.Up:
                cb.SelectedIndex = Math.Max(0, cb.SelectedIndex - 1);
                return true;
            case Pad.Down:
                cb.SelectedIndex = Math.Min(cb.Items.Count - 1, cb.SelectedIndex + 1);
                return true;
            case Pad.A:
                cb.IsDropDownOpen = false;
                _openCombo = null;
                cb.Focus(FocusState.Keyboard);
                return true;
            case Pad.B:
                cb.SelectedIndex = _comboStartIndex;
                cb.IsDropDownOpen = false;
                _openCombo = null;
                cb.Focus(FocusState.Keyboard);
                return true;
            default:
                return true; // swallow everything else while the list is open
        }
    }
}
