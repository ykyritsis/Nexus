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
///   X / Y               page-specific (e.g. X closes an app, Y favourites in the mixer)
/// Runs on the UI thread.
/// </summary>
public sealed class PadNavigator
{
    readonly ShellWindow _shell;
    ComboBox? _openCombo;
    int _comboStartIndex;

    public PadNavigator(ShellWindow shell) => _shell = shell;

    Slider? _adjusting;

    void StopAdjusting()
    {
        _adjusting = null;
        _shell.SetAdjusting(false);
    }

    public void Reset()
    {
        if (_adjusting != null) StopAdjusting();
        if (_openCombo != null) _openCombo.IsDropDownOpen = false;
        _openCombo = null;
    }

    public void Handle(Pad button)
    {
        if (!_shell.IsOpen) return;
        if (_openCombo != null && HandleCombo(button)) return;

        var focused = FocusManager.GetFocusedElement(_shell.NavRoot.XamlRoot) as DependencyObject;
        // Sliders only move once A is pressed on them (then A or B is "Done"), so moving through a
        // page never changes a value by accident. While adjusting, the slider takes its own axis.
        if (_adjusting != null && !ReferenceEquals(focused, _adjusting)) StopAdjusting();
        if (_adjusting != null && button is Pad.A or Pad.B) { ElementSoundPlayer.Play(ElementSoundKind.Invoke); StopAdjusting(); return; }
        if (_adjusting != null && button is Pad.LB or Pad.RB) StopAdjusting();
        var slider = _adjusting;
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
            case Pad.X: _shell.CurrentView?.HandleX(focused); break;
            case Pad.Y: _shell.CurrentView?.HandleY(focused); break;
            case Pad.Start: if (!_shell.OptionsOpen) _shell.CurrentView?.HandleStart(focused); break;
            case Pad.LeftStick: case Pad.RightStick: StickClick(button); break;
        }
    }

    long _leftStickAt = -10000, _rightStickAt = -10000;

    /// <summary>Both sticks clicked within 0.4 s: screenshot (the controller's F12).</summary>
    void StickClick(Pad button)
    {
        long now = Environment.TickCount64;
        if (button == Pad.LeftStick) _leftStickAt = now; else _rightStickAt = now;
        if (Math.Abs(_leftStickAt - _rightStickAt) < 400)
        {
            _leftStickAt = _rightStickAt = -10000;
            _ = _shell.ScreenshotAsync();
        }
    }

    void Move(FocusNavigationDirection direction)
    {
        var options = new FindNextElementOptions
        {
            SearchRoot = _shell.NavScope,
            XYFocusNavigationStrategyOverride = XYFocusNavigationStrategyOverride.Projection,
        };
        if (FocusManager.FindNextElement(direction, options) is Control next)
        {
            next.Focus(FocusState.Keyboard); // keyboard state = the system focus visual
            ElementSoundPlayer.Play(ElementSoundKind.Focus);
        }
        else if (FocusManager.GetFocusedElement(_shell.NavRoot.XamlRoot) is not DependencyObject d || !_shell.InPanel(d))
        {
            // Focus was lost (its control went away mid-press): start again from the page instead of sticking.
            _shell.FocusContent();
        }
    }

    static void Nudge(Slider s, int dir)
    {
        double step = s.SmallChange > 0 ? s.SmallChange : 1;
        s.Value = Math.Clamp(s.Value + dir * step * 2, s.Minimum, s.Maximum);
    }

    /// <summary>A quick press-in on the selected item, so A feels physical.</summary>
    static void Pulse(UIElement element)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
        var size = element.ActualSize;
        visual.CenterPoint = new System.Numerics.Vector3(size.X / 2, size.Y / 2, 0);
        var now = visual.Scale;
        var press = visual.Compositor.CreateVector3KeyFrameAnimation();
        press.InsertKeyFrame(0.35f, now * 0.94f);
        press.InsertKeyFrame(1f, now);
        press.Duration = TimeSpan.FromMilliseconds(170);
        visual.StartAnimation("Scale", press);
    }

    void Activate(DependencyObject? focused)
    {
        if (focused is Control) ElementSoundPlayer.Play(ElementSoundKind.Invoke);
        if (focused is Control pressed && pressed is not Slider) Pulse(pressed);
        switch (focused)
        {
            case SelectorBarItem item when item.Tag is string tab:
                _shell.SelectTab(tab, focus: true);
                break;
            case Slider s:
                _adjusting = s;
                _shell.SetAdjusting(true);
                break;
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
