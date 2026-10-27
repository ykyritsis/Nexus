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
/// A one-time setup (one admin prompt) installs the two drivers, registers Nexus with HidHide and
/// marks the controller to hide; Nexus then switches hiding on and off itself with HidHide's CLI,
/// which works unelevated. A watchdog switches hiding off if Nexus ever exits without doing so, so
/// the controller is never left hidden.
/// </summary>
public sealed class InputBridge : IDisposable
{

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

    // Pinned official releases (both MIT/BSD, signed by Nefarius Software Solutions; ViGEmBus is
    // archived but final). The setup script refuses anything not signed by Nefarius.
    const string HidHideSetupUrl = "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe";
    const string VigemSetupUrl = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";

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
    bool _setUp, _busy;

    public string Status { get; private set; } = "";

    long _quietUntil;
    /// <summary>True for a few seconds around switching on/off (controllers change slots then).</summary>
    public bool Switching => Environment.TickCount64 < _quietUntil;
    public bool Active => _active;
    public event Action? StatusChanged;

    public static string? Cli => CliPaths.FirstOrDefault(File.Exists);
    public static bool DriversInstalled => Cli != null && ServiceExists("ViGEmBus");
    public bool Ready => DriversInstalled && _setUp && _canRestart;
    bool _canRestart;

    // Elevated task (created by Set up) that restarts the hidden controller, so apps that opened it
    // before hiding switched on (a running game, Steam) must reconnect and then only find the virtual pad.
    const string RestartTask = @"\Nexus\Restart controller";

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
        _setUp = DriversInstalled && await Task.Run(EnsureConfigured);
        _canRestart = _setUp && await Task.Run(() => Run("schtasks.exe", $"/query /tn \"{RestartTask}\""));
        // Hiding persists across restarts: if Nexus or the PC went down, start from visible.
        if (_setUp) await Task.Run(() => Cloak(false));
        UpdateStatus();
        _poll.Start();
        await CheckAsync();
    }

    /// <summary>
    /// Every 5 s: keep the bridge on (games then never get the real controller, from the moment they
    /// start), except while a game whose anti-cheat refuses virtual controllers is running.
    /// </summary>
    async Task CheckAsync()
    {
        if (_busy) return;
        // Without the drivers (or set up) there's nothing to do: just look again now and then.
        _poll.Interval = TimeSpan.FromSeconds(Ready ? 5 : 30);
        if (!Ready || !_app.Settings.LockGameInput)
        {
            if (_active) await StopAsync("turned off");
            return;
        }
        _busy = true;
        try
        {
            string? blocked = await Task.Run(BlockedRunning);
            if (blocked is null && !_active) await StartAsync("always on");
            else if (blocked != null && _active) await StopAsync($"{blocked} refuses virtual controllers");
        }
        catch (Exception ex) { Log.Error("input bridge", ex); }
        finally { _busy = false; }
    }

    static string? BlockedRunning()
    {
        foreach (var name in Blocked)
        {
            var found = Process.GetProcessesByName(name);
            bool any = found.Length > 0;
            foreach (var p in found) p.Dispose();
            if (any) return name;
        }
        return null;
    }

    async Task StartAsync(string game)
    {
        try
        {
            _quietUntil = Environment.TickCount64 + 6000;
            // Hide the real controller first, so the virtual one takes its player slot.
            await Task.Run(() => Cloak(true));
            StartWatchdog();
            // Apps that already had it open keep reading it until it restarts and they reconnect.
            await Task.Run(RestartController);

            // Which slots Nexus sees before the virtual pad exists: the real controller keeps its slot
            // here (Nexus is allowed to see it), so the virtual pad is whichever slot appears next.
            var before = ConnectedSlots();
            _client = new ViGEmClient();
            _pad = _client.CreateXbox360Controller();
            _pad.AutoSubmitReport = false;
            _pad.FeedbackReceived += OnFeedback;
            _pad.Connect();
            int slot = await Task.Run(() => VirtualSlot(before));
            _app.Gamepad.IgnoredSlot = slot;

            _active = true;
            _thread = new Thread(Forward) { IsBackground = true, Name = "Nexus input bridge", Priority = ThreadPriority.Highest };
            _thread.Start();
            Log.Info($"input bridge on ({game}; virtual pad in slot {slot + 1})");
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
        _quietUntil = Environment.TickCount64 + 6000;
        _active = false;
        _thread?.Join(500);
        _thread = null;
        try { _pad?.Disconnect(); } catch { }
        _pad = null;
        _client?.Dispose();
        _client = null;
        _app.Gamepad.IgnoredSlot = -1;
        await Task.Run(() => Cloak(false));
        StopWatchdog();
        Log.Info($"input bridge off ({why})");
        UpdateStatus();
    }

    /// <summary>The virtual pad's XInput slot (reported a moment after it connects).</summary>
    /// <summary>
    /// The virtual pad's slot as Nexus sees it: the slot that wasn't there before it connected.
    /// (ViGEm's own UserIndex counts only virtual-bus pads, so it can name the real controller's slot.)
    /// </summary>
    int VirtualSlot(bool[] before)
    {
        for (int i = 0; i < 40; i++)
        {
            for (int slot = 0; slot < 4; slot++)
                if (!before[slot] && GamepadService.TryRead(slot, out _)) return slot;
            Thread.Sleep(25);
        }
        try { if (_pad != null) { Log.Warn("input bridge: virtual pad slot not seen, using ViGEm's index"); return _pad.UserIndex; } } catch { }
        return -1;
    }

    static bool[] ConnectedSlots()
    {
        var seen = new bool[4];
        for (int slot = 0; slot < 4; slot++) seen[slot] = GamepadService.TryRead(slot, out _);
        return seen;
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
                    Thread.Sleep(100); // no controller on: check ten times a second, not a thousand
                    continue;
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
    /// One-time setup, one admin prompt, everything in it: download the two drivers (pinned official
    /// releases, refused unless signed by Nefarius), install them silently, register Nexus with
    /// HidHide and mark the Xbox controller(s) to hide.
    /// </summary>
    public async Task<bool> SetUpAsync()
    {
        var script = Path.Combine(Log.Folder, "input-setup.ps1");
        var result = Path.Combine(Log.Folder, "input-setup.txt");
        var downloads = Path.Combine(Log.Folder, "drivers");
        Directory.CreateDirectory(Log.Folder);
        File.Delete(result);
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            $log = @()
            function Install($name, $url, $file, $installed) {
                if (& $installed) { return "$name already installed" }
                New-Item -ItemType Directory -Force '{{downloads}}' | Out-Null
                $path = Join-Path '{{downloads}}' $file
                [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
                Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $path
                $sig = Get-AuthenticodeSignature $path
                if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Nefarius Software Solutions') {
                    Remove-Item $path -Force; throw "$name download isn't signed by Nefarius ($($sig.Status))"
                }
                $p = Start-Process $path -ArgumentList '/exenoui','/qn','/norestart' -Wait -PassThru
                Remove-Item $path -Force -ErrorAction SilentlyContinue
                "$name installed (exit $($p.ExitCode))"
            }
            try {
                $log += Install 'ViGEmBus' '{{VigemSetupUrl}}' 'ViGEmBus_setup.exe' { Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\ViGEmBus' }
                $cliPaths = @('{{CliPaths[0]}}', '{{CliPaths[1]}}')
                $log += Install 'HidHide' '{{HidHideSetupUrl}}' 'HidHide_setup.exe' { $cliPaths | Where-Object { Test-Path $_ } }
                $cli = $cliPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
                if (-not $cli) { throw 'HidHide CLI not found after install' }
                $ErrorActionPreference = 'Continue'
                & $cli --app-reg '{{Environment.ProcessPath}}'
                $json = (& $cli --dev-gaming) | Out-String
                $paths = [regex]::Matches($json, '"deviceInstancePath"\s*:\s*"([^"]+)"') |
                    ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' } |
                    Where-Object { $_ -match 'VID_045E' -and $_ -notmatch 'PID_028E' } | Select-Object -Unique
                foreach ($p in $paths) { & $cli --dev-hide $p }
                & $cli --cloak-off
                $log += "controllers marked: $(@($paths).Count)"
                # Restart task: inline command (no script file a normal process could edit).
                $restart = "& '$cli' --dev-list | ForEach-Object { if (`$_ -match '--dev-hide `"(.+)`"') { pnputil /restart-device `$matches[1] | Out-Null } }"
                $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($restart))
                $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -EncodedCommand $encoded"
                $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
                $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 1)
                Register-ScheduledTask -TaskPath '\Nexus\' -TaskName 'Restart controller' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
                $log += "restart task created"
            } catch { $log += "error: $($_.Exception.Message)" }
            Set-Content -Path '{{result}}' -Value ($log -join '; ')
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

        _setUp = DriversInstalled && await Task.Run(EnsureConfigured);
        _canRestart = _setUp && await Task.Run(() => Run("schtasks.exe", $"/query /tn \"{RestartTask}\""));
        Log.Info($"input bridge setup: {(Ready ? "done" : "failed")} ({(File.Exists(result) ? File.ReadAllText(result).Trim() : "no result")})");
        if (Ready) { _app.Settings.LockGameInput = true; _app.Settings.Save(); }
        UpdateStatus();
        await CheckAsync();
        return Ready;
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
                "{Cli}" --cloak-off >nul
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

    /// <summary>
    /// Runs the elevated restart task and waits (up to ~6 s) for the controller to come back, if one
    /// was connected. A controller that's off needs nothing: it connects fresh, already hidden.
    /// </summary>
    void RestartController()
    {
        bool hadController = Enumerable.Range(0, 4).Any(slot => slot != _app.Gamepad.IgnoredSlot && GamepadService.TryRead(slot, out _));
        if (!hadController) return;
        if (!Run("schtasks.exe", $"/run /tn \"{RestartTask}\"")) { Log.Warn("input bridge: controller restart task missing"); return; }
        // Bluetooth reconnects can take several seconds; the virtual pad must not be added before the
        // real controller is back, or the real one would look like the newly appeared (virtual) slot.
        var clock = Stopwatch.StartNew();
        bool gone = false, back = false;
        while (clock.ElapsedMilliseconds < 12000)
        {
            bool present = Enumerable.Range(0, 4).Any(slot => GamepadService.TryRead(slot, out _));
            if (!present) gone = true;
            else if (gone) { back = true; break; } // it went away and is back
            Thread.Sleep(50);
        }
        Log.Info($"input bridge: controller restart {(back ? "done" : gone ? "timed out (controller not back yet)" : "not seen")} after {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>Switches hiding on or off. HidHide accepts this from a normal (unelevated) process.</summary>
    static bool Cloak(bool on) => Cli is { } cli && Run(cli, on ? "--cloak-on" : "--cloak-off");

    /// <summary>
    /// Nexus allowed to see hidden devices and the controller on the hide list. Fixed up here when
    /// HidHide allows it without admin (e.g. Nexus moved to a new folder); otherwise Set up does it.
    /// </summary>
    static bool EnsureConfigured()
    {
        if (Cli is not { } cli) return false;
        var exe = Environment.ProcessPath ?? "";
        if (!Output(cli, "--app-list").Contains(exe, StringComparison.OrdinalIgnoreCase))
            Run(cli, $"--app-reg \"{exe}\"");
        bool registered = Output(cli, "--app-list").Contains(exe, StringComparison.OrdinalIgnoreCase);
        bool hidden = Output(cli, "--dev-list").Contains("--dev-hide", StringComparison.OrdinalIgnoreCase);
        if (!registered || !hidden) Log.Info($"input bridge: HidHide {(registered ? "" : "doesn't list Nexus")}{(registered || hidden ? "" : ", ")}{(hidden ? "" : "has no controller to hide")}");
        return registered && hidden;
    }

    static string Output(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p is null) return "";
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return text;
        }
        catch { return ""; }
    }

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
        Status = !Ready ? (_setUp ? "Press Set up once more to finish (one admin prompt)" : "Not set up: one press installs and sets it up (admin prompt)")
            : !_app.Settings.LockGameInput ? "Off"
            : _active ? "On: games ignore the controller while the guide is open"
            : "On: paused for a game whose anti-cheat refuses virtual controllers";
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
        _quietUntil = Environment.TickCount64 + 6000;
        _active = false;
        _thread?.Join(500);
        try { _pad?.Disconnect(); } catch { }
        _client?.Dispose();
        _app.Gamepad.IgnoredSlot = -1;
        Cloak(false);
        StopWatchdog();
        Log.Info("input bridge off (quitting)");
    }
}
