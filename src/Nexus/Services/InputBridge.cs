using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nexus.Input;
using Nexus.Interop;
using Nexus.Providers;

namespace Nexus.Services;

/// <summary>
/// Keeps games from reacting to the controller while the guide is open, for every game and without
/// touching them: while a game runs, the real controller is hidden from everything but Nexus
/// (HidHide) and Nexus mirrors it to a virtual Xbox controller (ViGEmBus) about 1000 times a second.
/// While the guide is open the virtual controller simply reports nothing pressed. Games only ever see
/// one controller that never disconnects; outside games nothing is hidden or mirrored.
///
/// HidHide only takes changes from an elevated process, so a one-time setup (one admin prompt)
/// registers Nexus, marks the controller to hide and creates two scheduled tasks that switch hiding
/// on and off; Nexus runs those tasks unelevated. A watchdog switches hiding off if Nexus ever exits
/// without doing so, so the controller is never left hidden.
/// </summary>
public sealed class InputBridge : IDisposable
{
    const string OnTask = @"Nexus\HidHide on", OffTask = @"Nexus\HidHide off";
    const ushort GuideButton = 0x0400;

    static readonly string[] CliPaths =
    {
        @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe",
        @"C:\Program Files\Nefarius Software Solutions\HidHide\HidHideCLI.exe",
    };

    // Anti-cheats that refuse virtual controllers: never bridge while these run.
    static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "VALORANT-Win64-Shipping", "VALORANT", "League of Legends", "LeagueClient",
    };

    public const string HidHideUrl = "https://github.com/nefarius/HidHide/releases";
    public const string ViGEmBusUrl = "https://github.com/nefarius/ViGEmBus/releases";

    readonly App _app;
    readonly DispatcherQueue _ui;
    readonly DispatcherQueueTimer _poll;
    readonly object _gate = new();
    ViGEmClient? _client;
    IXbox360Controller? _pad;
    Thread? _thread;
    Process? _watchdog;
    volatile bool _active;
    volatile int _source = -1;
    int _misses;
    bool _setUp, _busy;

    public string Status { get; private set; } = "";
    public bool Active => _active;
    public event Action? StatusChanged;

    public static string? Cli => CliPaths.FirstOrDefault(File.Exists);
    public static bool DriversInstalled => Cli != null && ServiceExists("ViGEmBus");
    public bool Ready => DriversInstalled && _setUp;

    public InputBridge(App app, DispatcherQueue ui)
    {
        _app = app;
        _ui = ui;
        _poll = ui.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(2);
        _poll.Tick += async (_, _) => await CheckAsync();
        _ = InitAsync();
    }

    async Task InitAsync()
    {
        _setUp = DriversInstalled && await Task.Run(() => TaskExists(OnTask) && TaskExists(OffTask));
        // Hiding persists across restarts: if Nexus or the PC went down mid-game, start from visible.
        if (_setUp) await Task.Run(() => RunTask(OffTask));
        UpdateStatus();
        _poll.Start();
    }

    /// <summary>Every 2 s: bridge while a game runs, stop shortly after it ends.</summary>
    async Task CheckAsync()
    {
        if (_busy) return;
        // Without the drivers (or set up) there's nothing to do: just look again now and then.
        _poll.Interval = TimeSpan.FromSeconds(Ready ? 2 : 30);
        if (!Ready || !_app.Settings.LockGameInput)
        {
            if (_active) await StopAsync("turned off");
            return;
        }
        if (_app.Shell.IsOpen) return; // the guide is in front: keep whatever state the game had
        _busy = true;
        try
        {
            var game = await Task.Run(() => NowPlaying.Detect(Native.GetForegroundWindow()));
            bool blocked = game?.ExePath is { } exe && Blocked.Contains(Path.GetFileNameWithoutExtension(exe));
            if (game != null && !blocked)
            {
                _misses = 0;
                if (!_active) await StartAsync(game.Game.Name);
            }
            else if (_active && (blocked || ++_misses >= 3)) // ~6 s without a game (alt-tab blips don't count)
                await StopAsync(blocked ? "blocked game" : "game ended");
        }
        catch (Exception ex) { Log.Error("input bridge", ex); }
        finally { _busy = false; }
    }

    async Task StartAsync(string game)
    {
        try
        {
            // Hide the real controller first, so the virtual one takes its player slot.
            await Task.Run(() => RunTask(OnTask));
            StartWatchdog();
            await Task.Delay(400);

            _client = new ViGEmClient();
            _pad = _client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false;
            _pad.FeedbackReceived += OnFeedback;
            _pad.Connect();
            int slot = await Task.Run(VirtualSlot);
            _app.Gamepad.IgnoredSlot = slot;

            _active = true;
            _thread = new Thread(Forward) { IsBackground = true, Name = "Nexus input bridge", Priority = ThreadPriority.Highest };
            _thread.Start();
            Log.Info($"input bridge on for {game} (virtual pad in slot {slot + 1})");
        }
        catch (Exception ex)
        {
            Log.Error("input bridge start", ex);
            await StopAsync("failed to start");
        }
        UpdateStatus();
    }

    async Task StopAsync(string why)
    {
        _active = false;
        _thread?.Join(500);
        _thread = null;
        try { _pad?.Disconnect(); } catch { }
        _pad = null;
        _client?.Dispose();
        _client = null;
        _app.Gamepad.IgnoredSlot = -1;
        await Task.Run(() => RunTask(OffTask));
        StopWatchdog();
        Log.Info($"input bridge off ({why})");
        UpdateStatus();
    }

    /// <summary>The virtual pad's XInput slot (reported a moment after it connects).</summary>
    int VirtualSlot()
    {
        for (int i = 0; i < 20; i++)
        {
            try { if (_pad != null) return _pad.UserIndex; } catch { }
            Thread.Sleep(25);
        }
        return -1;
    }

    // ---------------- mirroring ----------------

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    /// <summary>Copies the real controller to the virtual one (~1 kHz); reports nothing while the guide is open.</summary>
    void Forward()
    {
        timeBeginPeriod(1);
        try
        {
            uint lastPacket = uint.MaxValue;
            bool lastLocked = false;
            long lastSend = 0;
            var clock = Stopwatch.StartNew();
            while (_active)
            {
                var pad = _pad;
                if (pad is null) break;
                bool locked = _app.Shell.IsOpen;
                if (!FindSource(out var s))
                {
                    if (lastPacket != uint.MaxValue) { Neutral(pad); lastPacket = uint.MaxValue; }
                }
                else if (s.Packet != lastPacket || locked != lastLocked || clock.ElapsedMilliseconds - lastSend > 100)
                {
                    if (locked) Neutral(pad);
                    else
                    {
                        pad.SetButtonsFull(s.Buttons); // Guide included, so Windows' own Guide features still work
                        pad.SetSliderValue(Xbox360Slider.LeftTrigger, s.LeftTrigger);
                        pad.SetSliderValue(Xbox360Slider.RightTrigger, s.RightTrigger);
                        pad.SetAxisValue(Xbox360Axis.LeftThumbX, s.LX);
                        pad.SetAxisValue(Xbox360Axis.LeftThumbY, s.LY);
                        pad.SetAxisValue(Xbox360Axis.RightThumbX, s.RX);
                        pad.SetAxisValue(Xbox360Axis.RightThumbY, s.RY);
                        pad.SubmitReport();
                    }
                    lastPacket = s.Packet;
                    lastLocked = locked;
                    lastSend = clock.ElapsedMilliseconds;
                }
                Thread.Sleep(1);
            }
        }
        catch (Exception ex) { Log.Error("input bridge mirroring", ex); }
        finally { timeEndPeriod(1); }
    }

    /// <summary>The real controller: the first connected slot that isn't the virtual pad.</summary>
    bool FindSource(out GamepadService.RawState state)
    {
        int src = _source;
        if (src >= 0 && src != _app.Gamepad.IgnoredSlot && GamepadService.TryRead(src, out state)) return true;
        for (int slot = 0; slot < 4; slot++)
        {
            if (slot == _app.Gamepad.IgnoredSlot) continue;
            if (GamepadService.TryRead(slot, out state)) { _source = slot; return true; }
        }
        state = default;
        _source = -1;
        return false;
    }

    static void Neutral(IXbox360Controller pad)
    {
        pad.ResetReport();
        pad.SubmitReport();
    }

    /// <summary>Rumble the game sends to the virtual pad goes to the real one.</summary>
    void OnFeedback(object? sender, Xbox360FeedbackReceivedEventArgs e)
    {
        if (_source >= 0) GamepadService.Vibrate(_source, (ushort)(e.LargeMotor * 257), (ushort)(e.SmallMotor * 257));
    }

    // ---------------- HidHide ----------------

    /// <summary>
    /// One-time setup (admin prompt): register Nexus with HidHide, mark the Xbox controller(s) to hide,
    /// and create the on/off tasks. Returns true when it worked.
    /// </summary>
    public async Task<bool> SetUpAsync()
    {
        var cli = Cli;
        if (cli is null || !DriversInstalled) return false;
        var script = Path.Combine(Log.Folder, "hidhide-setup.ps1");
        var result = Path.Combine(Log.Folder, "hidhide-setup.txt");
        Directory.CreateDirectory(Log.Folder);
        File.Delete(result);
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Continue'
            $cli = '{{cli}}'
            & $cli --app-reg '{{Environment.ProcessPath}}'
            $json = (& $cli --dev-gaming) | Out-String
            $paths = [regex]::Matches($json, '"deviceInstancePath"\s*:\s*"([^"]+)"') |
                ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' } |
                Where-Object { $_ -match 'VID_045E' -and $_ -notmatch 'PID_028E' } | Select-Object -Unique
            foreach ($p in $paths) { & $cli --dev-hide $p }
            & $cli --cloak-off
            schtasks /create /tn '{{OnTask}}' /tr "`"$cli`" --cloak-on" /sc once /st 00:00 /rl highest /f
            schtasks /create /tn '{{OffTask}}' /tr "`"$cli`" --cloak-off" /sc once /st 00:00 /rl highest /f
            Set-Content -Path '{{result}}' -Value ("devices=" + @($paths).Count)
            """);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p != null) await p.WaitForExitAsync();
        }
        catch (Exception ex) { Log.Warn($"input bridge setup cancelled: {ex.Message}"); return false; }

        _setUp = await Task.Run(() => TaskExists(OnTask) && TaskExists(OffTask));
        Log.Info($"input bridge setup: {(_setUp ? "done" : "failed")} ({(File.Exists(result) ? File.ReadAllText(result).Trim() : "no result")})");
        if (_setUp) { _app.Settings.LockGameInput = true; _app.Settings.Save(); }
        UpdateStatus();
        return _setUp;
    }

    /// <summary>
    /// A small script that waits for Nexus to exit and switches hiding off, in case Nexus crashes
    /// while a game is bridged (otherwise the controller would stay hidden from games).
    /// </summary>
    void StartWatchdog()
    {
        StopWatchdog();
        try
        {
            int pid = Environment.ProcessId;
            var script = Path.Combine(Log.Folder, "bridge-watchdog.cmd");
            File.WriteAllText(script, $"""
                @echo off
                :wait
                tasklist /FI "PID eq {pid}" | find "{pid}" >nul && (timeout /t 2 /nobreak >nul & goto wait)
                schtasks /run /tn "{OffTask}" >nul
                """);
            _watchdog = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex) { Log.Warn($"input bridge watchdog: {ex.Message}"); }
    }

    void StopWatchdog()
    {
        try { if (_watchdog is { HasExited: false }) _watchdog.Kill(entireProcessTree: true); } catch { }
        _watchdog?.Dispose();
        _watchdog = null;
    }

    static bool RunTask(string name) => Run("schtasks.exe", $"/run /tn \"{name}\"");
    static bool TaskExists(string name) => Run("schtasks.exe", $"/query /tn \"{name}\"");

    static bool Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    static bool ServiceExists(string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return k != null;
    }

    void UpdateStatus()
    {
        Status = !DriversInstalled ? "Needs two free drivers: HidHide and ViGEmBus"
            : !_setUp ? "Drivers found: one-time setup needed"
            : !_app.Settings.LockGameInput ? "Off"
            : _active ? "On: games ignore the controller while the guide is open"
            : "On: starts when a game is running";
        _ui.TryEnqueue(() => StatusChanged?.Invoke());
    }

    /// <summary>The Controllers page switch.</summary>
    public async Task SetEnabledAsync(bool on)
    {
        _app.Settings.LockGameInput = on;
        _app.Settings.Save();
        if (!on && _active) await StopAsync("turned off");
        UpdateStatus();
    }

    public void Dispose()
    {
        _poll.Stop();
        if (!_active) return;
        // Quitting: undo synchronously so the controller is visible again before Nexus is gone.
        _active = false;
        _thread?.Join(500);
        try { _pad?.Disconnect(); } catch { }
        _client?.Dispose();
        _app.Gamepad.IgnoredSlot = -1;
        RunTask(OffTask);
        StopWatchdog();
        Log.Info("input bridge off (quitting)");
    }
}
