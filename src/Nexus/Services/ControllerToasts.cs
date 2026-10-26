using Microsoft.UI.Dispatching;
using Nexus.Input;
using Nexus.Interop;
using Nexus.Views;

namespace Nexus.Services;

/// <summary>
/// Controller notifications: connected (with player number and battery), disconnected, and battery
/// low or empty. Battery is checked once a minute, only while a controller is connected; each warning
/// shows once per level until the controller is charged or reconnected.
/// </summary>
public sealed class ControllerToasts
{
    const string PadGlyph = "", LowGlyph = "", EmptyGlyph = "";

    readonly App _app;
    readonly DispatcherQueue _ui;
    readonly DispatcherQueueTimer _battery;
    readonly int[] _warned = { 4, 4, 4, 4 }; // lowest level already warned about (4 = none)
    ToastWindow? _toast;

    public ControllerToasts(App app, DispatcherQueue ui)
    {
        _app = app;
        _ui = ui;
        _battery = ui.CreateTimer();
        _battery.Interval = TimeSpan.FromMinutes(1);
        _battery.Tick += (_, _) => CheckBatteries();
        _app.Gamepad.ConnectionChanged += (slot, connected) => _ui.TryEnqueue(() => OnConnection(slot, connected));
    }

    async void OnConnection(int slot, bool connected)
    {
        if (_app.Gamepad.AnyConnected) _battery.Start(); else _battery.Stop();
        _warned[slot] = 4;
        // Controllers already on when Nexus starts aren't news.
        if (_app.Gamepad.Now < 5000 || !_app.Settings.ControllerToasts) return;
        if (!connected)
        {
            Show(PadGlyph, "Controller disconnected", $"Player {slot + 1}");
            return;
        }
        await Task.Delay(800); // battery isn't reported the instant it connects
        string battery = _app.Gamepad.Battery(slot) is { } b
            ? b.wired ? "Wired" : b.level switch { 0 => "Battery empty", 1 => "Battery low", 2 => "Battery medium", _ => "Battery full" }
            : "Connected";
        Show(PadGlyph, "Controller connected", $"Player {slot + 1} · {battery}");
        CheckBatteries();
    }

    void CheckBatteries()
    {
        if (!_app.Settings.ControllerToasts) return;
        for (int slot = 0; slot < 4; slot++)
        {
            if (_app.Gamepad.Battery(slot) is not { wired: false } b) continue;
            if (b.level >= 2) { _warned[slot] = 4; continue; } // charged again
            if (b.level >= _warned[slot]) continue;
            _warned[slot] = b.level;
            if (b.level == 0) Show(EmptyGlyph, "Controller battery empty", $"Player {slot + 1} · charge it soon", seconds: 6);
            else Show(LowGlyph, "Controller battery low", $"Player {slot + 1}", seconds: 5);
        }
    }

    /// <summary>Preview (Nexus.exe --test-toast).</summary>
    public void Test() => Show(PadGlyph, "Controller connected", "Player 1 · Battery full");

    void Show(string glyph, string title, string detail, int seconds = 4)
    {
        if (_app.Shell.IsOpen) return; // the controller chip in the overlay already shows it
        Log.Info($"toast: {title} ({detail})");
        if (_toast is null)
        {
            _toast = new ToastWindow();
            _toast.Hidden += () => { if (!_app.Shell.IsOpen) Native.TrimWorkingSet(); };
        }
        _toast.Show(glyph, title, detail, seconds);
    }
}
