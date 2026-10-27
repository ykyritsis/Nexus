using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Nexus.Input;
using Nexus.Interop;
using Nexus.Services;
using Nexus.Views;

namespace Nexus;

public partial class App : Application
{
    readonly string[] _args;
    ShellWindow? _shell;
    WindowHooks? _hooks;
    DispatcherQueue? _ui;
    ControllerToasts? _toasts;

    public AppSettings Settings { get; }
    public GamepadService Gamepad { get; }
    public ShellWindow Shell => _shell!;
    public Updater Updater { get; private set; } = null!;
    public InputBridge InputBridge { get; private set; } = null!;
    public ControllerToasts Toasts => _toasts!;

    public App(string[] args)
    {
        _args = args;
        InitializeComponent();
        Settings = AppSettings.Load();
        Gamepad = new GamepadService { CursorMode = Settings.GamepadCursor };
        UnhandledException += (_, e) =>
        {
            Log.Error("unhandled", e.Exception);
            Log.Warn(e.Exception.ToString()); // full detail (stack, inner exceptions) for diagnosis
            e.Handled = true; // one broken panel must not take the overlay down
        };
        Log.Info($"Nexus {typeof(App).Assembly.GetName().Version} starting ({string.Join(' ', args)})");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        // The Fluent navigation sounds Xbox uses (focus, select, back, open, close).
        ElementSoundPlayer.State = ElementSoundPlayerState.On;
        ElementSoundPlayer.SpatialAudioMode = ElementSpatialAudioMode.Off;
        _shell = new ShellWindow(this);

        Gamepad.GuidePressed += (slot, at) =>
        {
            Log.Info($"Guide pressed on controller {slot + 1}");
            _ui.TryEnqueue(() =>
            {
                try { _shell.Toggle(at); }
                catch (Exception ex) { Log.Error("Guide", ex); Log.Warn(ex.ToString()); }
            });
        };
        // Holding Guide belongs to Windows (Xbox mode's task view), so Nexus only reacts to a press.
        // Work queued straight onto the dispatcher bypasses Application.UnhandledException: an exception
        // there ends the process. Controller actions open pages and press buttons, so guard them.
        Gamepad.NavButton += b => _ui.TryEnqueue(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try { _shell.Navigator.Handle(b); }
            catch (Exception ex) { Log.Error($"controller {b}", ex); Log.Warn(ex.ToString()); }
            if (clock.ElapsedMilliseconds > 25) Log.Warn($"slow input: {b} took {clock.ElapsedMilliseconds} ms on {_shell.CurrentView?.Title}");
        });
        _toasts = new ControllerToasts(this, _ui);
        Updater = new Updater(this, _ui);
        if (Updater.JustUpdated)
            _ui.TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                await Task.Delay(4000);
                try { _toasts.Notify("\uE895", $"Nexus updated to {Updater.Current.ToString(3)}", "What's new is on GitHub"); } catch { }
            });
        InputBridge = new InputBridge(this, _ui);

        _hooks = new WindowHooks(_shell.Hwnd);
        _hooks.HotkeyPressed += () => _shell.Toggle();
        var tray = new TrayIcon(_shell.Hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "nexus.ico"),
            "Nexus: press the Guide button",
            ("Open Nexus", () => _shell.Show()),
            ("More settings", async () => { await _shell.ShowAsync("settings"); _shell.OpenSubPage(new MoreSettingsView(this)); }),
            ("Open log folder", () => Launch.Open(Log.Folder)),
            ("-", () => { }),
            ("Quit Nexus", Quit));
        tray.Clicked += () => _shell.Toggle();
        _hooks.AttachTray(tray);

        SingleInstance.Listen(
            () => { Log.Info("toggle requested by another launch"); _ui.TryEnqueue(() => _shell.Toggle()); },
            () => _ui.TryEnqueue(Quit),
            () => _ui.TryEnqueue(async () =>
            {
                try { await _shell.SnapshotTabsAsync(); } catch (Exception ex) { Log.Error("snapshot tabs", ex); Log.Warn(ex.ToString()); }
            }));

        _shell.Warm();
        if (_args.Contains("--test-toast", StringComparer.OrdinalIgnoreCase))
            _ui.TryEnqueue(DispatcherQueuePriority.Low, async () => { await Task.Delay(1500); _toasts.Test(); });
        // Started in the background (at login): trim straight away, the overlay isn't needed yet.
        if (_args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            _ui.TryEnqueue(DispatcherQueuePriority.Low, async () => { await Task.Delay(3000); Native.TrimWorkingSet(); });
        if (!_args.Contains("--background", StringComparer.OrdinalIgnoreCase) && !_args.Contains("--test-toast", StringComparer.OrdinalIgnoreCase))
            _ui.TryEnqueue(DispatcherQueuePriority.Low, () => _shell.Show());
    }

    public void Quit()
    {
        Log.Info("quitting");
        _shell?.PrepareToQuit();
        _hooks?.Dispose();
        InputBridge?.Dispose();
        Gamepad.Dispose();
        Exit();
    }
}
