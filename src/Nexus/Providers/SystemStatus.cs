using System.Diagnostics;
using System.Runtime.InteropServices;
using Nexus.Interop;
using Nexus.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace Nexus.Providers;

/// <summary>Network, Bluetooth, airplane mode, Xbox mode and the touch keyboard.</summary>
public static class SystemStatus
{
    // ---------------- network ----------------
    public static (bool connected, string detail) Network()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return (false, "Not connected");
            var level = profile.GetNetworkConnectivityLevel();
            string kind = profile.IsWlanConnectionProfile
                ? profile.WlanConnectionProfileDetails.GetConnectedSsid()
                : "Ethernet";
            return level switch
            {
                NetworkConnectivityLevel.InternetAccess => (true, $"Connected · {kind}"),
                NetworkConnectivityLevel.ConstrainedInternetAccess => (true, $"Limited · {kind}"),
                _ => (false, $"No internet · {kind}"),
            };
        }
        catch (Exception ex)
        {
            Log.Error("network status", ex);
            return (false, "Unknown");
        }
    }

    // ---------------- radios ----------------
    // Radio objects track their state live, so the list is fetched once (Settings asks several times per open).
    static IReadOnlyList<Radio>? _radios;

    static async Task<IReadOnlyList<Radio>> RadiosAsync()
    {
        if (_radios is { Count: > 0 }) return _radios;
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return Array.Empty<Radio>();
            return _radios = await Radio.GetRadiosAsync();
        }
        catch (Exception ex)
        {
            Log.Error("radio access", ex);
            return Array.Empty<Radio>();
        }
    }

    /// <summary>"On · 2 connected" / "Not connected" / "Off" / null if there's no Bluetooth.</summary>
    public static async Task<string?> BluetoothAsync()
    {
        var radio = (await RadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
        if (radio is null) return null;
        if (radio.State != RadioState.On) return "Off";
        try
        {
            var classic = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
            var le = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
            int n = classic.Count + le.Count;
            return n == 0 ? "Not connected" : $"{n} connected";
        }
        catch { return "On"; }
    }

    public static async Task SetBluetoothAsync(bool on)
    {
        var radio = (await RadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
        if (radio is null) return;
        try { await radio.SetStateAsync(on ? RadioState.On : RadioState.Off); Log.Info($"Bluetooth {(on ? "on" : "off")}"); }
        catch (Exception ex) { Log.Error("Bluetooth radio", ex); }
    }

    /// <summary>Airplane mode as Nexus sees it: every wireless radio off. Null if there are none.</summary>
    public static async Task<bool?> AirplaneAsync()
    {
        var radios = (await RadiosAsync()).Where(IsWireless).ToList();
        if (radios.Count == 0) return null;
        return radios.All(r => r.State != RadioState.On);
    }

    public static async Task SetAirplaneAsync(bool on)
    {
        foreach (var r in (await RadiosAsync()).Where(IsWireless))
        {
            try { await r.SetStateAsync(on ? RadioState.Off : RadioState.On); }
            catch (Exception ex) { Log.Error($"radio {r.Name}", ex); }
        }
        Log.Info($"airplane mode {(on ? "on" : "off")}");
    }

    static bool IsWireless(Radio r) => r.Kind is RadioKind.WiFi or RadioKind.Bluetooth or RadioKind.MobileBroadband;

    // ---------------- Xbox full screen experience ----------------
    [DllImport("gamemode.dll")] static extern bool IsGamingFullScreenExperienceActive();
    [DllImport("gamemode.dll")] static extern bool IsGamingFullScreenExperienceSupported();

    public static bool XboxModeSupported() { try { return IsGamingFullScreenExperienceSupported(); } catch { return false; } }
    public static bool XboxModeActive() { try { return IsGamingFullScreenExperienceActive(); } catch { return false; } }

    /// <summary>Win+F11 enters and leaves Xbox mode.</summary>
    public static void ToggleXboxMode() => Native.PressChord(Native.VK_LWIN, Native.VK_F11);

    // ---------------- touch keyboard ----------------
    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITipInvocation { void Toggle(IntPtr hwnd); }

    [ComImport, Guid("4ce576fa-83dc-4f88-951c-9d0782b4e376")]
    class UIHostNoLaunch { }

    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();

    public static async Task ToggleTouchKeyboardAsync()
    {
        try
        {
            var running = Process.GetProcessesByName("TabTip");
            foreach (var t in running) t.Dispose();
            if (running.Length == 0)
            {
                var tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                    "microsoft shared", "ink", "TabTip.exe");
                using var _ = Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true });
                await Task.Delay(350); // let it start before asking it to show
            }
            ((ITipInvocation)new UIHostNoLaunch()).Toggle(GetDesktopWindow());
        }
        catch (Exception ex) { Log.Error("touch keyboard", ex); }
    }
}
