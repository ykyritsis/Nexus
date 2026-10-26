using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Nexus.Interop;
using Nexus.Services;

namespace Nexus.Providers;

public sealed record AudioDevice(string Id, string Name);

/// <summary>One app in the volume mixer (all of a process's audio sessions together).</summary>
public sealed class MixerApp
{
    public required string Key { get; init; }          // process name, or "system" for System Sounds
    public required string Name { get; init; }
    public string? ExePath { get; init; }
    public bool IsSystem { get; init; }
    public float Volume { get; set; }
    public bool Muted { get; set; }
    internal List<AudioSessionControl> Sessions { get; } = new();
}

/// <summary>
/// Default speaker/mic volume and mute, default device switching and the per-app mixer, through
/// Windows Core Audio (NAudio). Use from the UI thread only: the COM objects belong to it.
/// </summary>
public sealed class AudioService
{
    readonly MMDeviceEnumerator _enumerator = new();

    MMDevice? Default(DataFlow flow)
    {
        try { return _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
        catch { return null; } // no device of that kind
    }

    // ---------------- master volume ----------------
    public (float volume, bool muted)? Endpoint(DataFlow flow)
    {
        var d = Default(flow);
        if (d is null) return null;
        var v = d.AudioEndpointVolume;
        return (v.MasterVolumeLevelScalar, v.Mute);
    }

    public void SetVolume(DataFlow flow, float value)
    {
        var d = Default(flow);
        if (d != null) d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
    }

    public void SetMute(DataFlow flow, bool muted)
    {
        var d = Default(flow);
        if (d != null) d.AudioEndpointVolume.Mute = muted;
    }

    // ---------------- devices ----------------
    public List<AudioDevice> Devices(DataFlow flow) =>
        _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioDevice(d.ID, d.FriendlyName)).ToList();

    public string? DefaultId(DataFlow flow) => Default(flow)?.ID;

    /// <summary>Makes a device the default for all three roles, like the Sound settings page does.</summary>
    public void SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            for (int role = 0; role < 3; role++) Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
            Log.Info($"default audio device set to {id}");
        }
        catch (Exception ex) { Log.Error("could not switch default device", ex); }
    }

    // ---------------- mixer ----------------
    public List<MixerApp> Mixer()
    {
        var apps = new Dictionary<string, MixerApp>(StringComparer.OrdinalIgnoreCase);
        var device = Default(DataFlow.Render);
        if (device is null) return new();
        var mgr = device.AudioSessionManager;
        mgr.RefreshSessions();
        var sessions = mgr.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
            MixerApp app;
            if (s.IsSystemSoundsSession)
            {
                if (!apps.TryGetValue("system", out app!))
                    apps["system"] = app = new MixerApp { Key = "system", Name = "System Sounds", IsSystem = true };
            }
            else
            {
                uint pid = s.GetProcessID;
                var path = Native.ProcessPath(pid);
                var key = path is null ? $"pid{pid}" : Path.GetFileNameWithoutExtension(path);
                if (!apps.TryGetValue(key, out app!))
                    apps[key] = app = new MixerApp { Key = key, Name = FriendlyName(path, key, s.DisplayName), ExePath = path };
            }
            app.Sessions.Add(s);
        }
        foreach (var app in apps.Values)
        {
            app.Volume = app.Sessions.Max(x => x.SimpleAudioVolume.Volume);
            app.Muted = app.Sessions.All(x => x.SimpleAudioVolume.Mute);
        }
        return apps.Values.ToList();
    }

    static string FriendlyName(string? path, string key, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith('@')) return displayName;
        try
        {
            if (path != null && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } desc) return desc;
        }
        catch { }
        return key;
    }

    public static void SetAppVolume(MixerApp app, float value)
    {
        foreach (var s in app.Sessions) s.SimpleAudioVolume.Volume = Math.Clamp(value, 0f, 1f);
        app.Volume = value;
    }

    public static void SetAppMute(MixerApp app, bool muted)
    {
        foreach (var s in app.Sessions) s.SimpleAudioVolume.Mute = muted;
        app.Muted = muted;
    }

    // Undocumented but long-stable interface the Sound settings page uses to change the default device.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility();
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }
}
