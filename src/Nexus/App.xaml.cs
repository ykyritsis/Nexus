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

    public AppSettings Settings { get; }
    public GamepadService Gamepad { get; }
    public ShellWindow Shell => _shell!;

    public App(string[] args)
    {
        _args = args;
        InitializeComponent();
        Settings = AppSettings.Load();
        Gamepad = new GamepadService { CursorMode = Settings.GamepadCursor };
        UnhandledException += (_, e) =>
        {
            Log.Error("unhandled", e.Exception);
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
            _ui.TryEnqueue(() => _shell.Toggle(at));
        };
        // Holding Guide belongs to Windows (Xbox mode's task view), so Nexus only reacts to a press.
        Gamepad.NavButton += b => _ui.TryEnqueue(() => _shell.Navigator.Handle(b));

        _hooks = new WindowHooks(_shell.Hwnd);
        _hooks.HotkeyPressed += () => _shell.Toggle();
        var tray = new TrayIcon(_shell.Hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "nexus.ico"),
            "Nexus: press the Guide button",
            ("Open Nexus", () => _shell.Show()),
            ("More settings", () => { _shell.Show("settings"); _shell.OpenSubPage(new MoreSettingsView(this)); }),
            ("Open log folder", () => Launch.Open(Log.Folder)),
            ("-", () => { }),
            ("Quit Nexus", Quit));
        tray.Clicked += () => _shell.Toggle();
        _hooks.AttachTray(tray);

        SingleInstance.Listen(
            () => { Log.Info("toggle requested by another launch"); _ui.TryEnqueue(() => _shell.Toggle()); },
            () => _ui.TryEnqueue(Quit));

        _shell.Warm();
        // Started in the background (at login): trim straight away, the overlay isn't needed yet.
        if (_args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            _ui.TryEnqueue(DispatcherQueuePriority.Low, async () => { await Task.Delay(3000); Native.TrimWorkingSet(); });
        if (!_args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            _ui.TryEnqueue(DispatcherQueuePriority.Low, () => _shell.Show());
    }

    public void Quit()
    {
        Log.Info("quitting");
        _hooks?.Dispose();
        Gamepad.Dispose();
        Exit();
    }
}
