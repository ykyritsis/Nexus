using System.Runtime.InteropServices;
using Microsoft.Win32;
using Nexus.Services;

namespace Nexus.Providers;

public readonly record struct PerfSample(double Cpu, double Gpu, double RamUsedGb, double RamTotalGb, double VramUsedGb, double VramTotalGb);

/// <summary>
/// System load without helper tools: CPU from GetSystemTimes, RAM from GlobalMemoryStatusEx, GPU
/// load and video memory from Windows' own GPU performance counters (the ones Task Manager uses),
/// which work for AMD, NVIDIA and Intel alike.
/// </summary>
public sealed class PerfMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);
    const uint PDH_FMT_DOUBLE = 0x200, PDH_MORE_DATA = 0x800007D2;

    readonly IntPtr _query, _gpuLoad, _vram;
    ulong _lastIdle, _lastTotal;
    readonly double _vramTotalGb = VideoMemoryGb();

    public PerfMonitor()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
        {
            PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuLoad);
            PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _vram);
            PdhCollectQueryData(_query);
        }
        Sample(); // prime the CPU delta
    }

    public PerfSample Sample()
    {
        double cpu = 0;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong total = kernel.Value + user.Value; // kernel time includes idle
            ulong dTotal = total - _lastTotal, dIdle = idle.Value - _lastIdle;
            if (_lastTotal != 0 && dTotal > 0) cpu = 100.0 * (dTotal - dIdle) / dTotal;
            _lastTotal = total;
            _lastIdle = idle.Value;
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        const double GB = 1024.0 * 1024 * 1024;

        double gpu = 0, vram = 0;
        if (_query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
        {
            gpu = Math.Min(100, Sum(_gpuLoad, max: false));
            vram = Sum(_vram, max: true) / GB;
        }
        return new PerfSample(Math.Clamp(cpu, 0, 100), gpu, (mem.ullTotalPhys - mem.ullAvailPhys) / GB, mem.ullTotalPhys / GB, vram, _vramTotalGb);
    }

    /// <summary>Adds up a wildcard counter (one instance per engine/process), or takes the largest (per adapter).</summary>
    static double Sum(IntPtr counter, bool max)
    {
        if (counter == IntPtr.Zero) return 0;
        uint size = 0;
        int r = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out uint count, IntPtr.Zero);
        if ((uint)r != PDH_MORE_DATA || size == 0) return 0;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, buffer) != 0) return 0;
            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then { CStatus (4) + padding (4), double }.
            int stride = IntPtr.Size + 16;
            double total = 0, best = 0;
            for (int i = 0; i < count; i++)
            {
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, i * stride + IntPtr.Size + 8));
                total += v;
                best = Math.Max(best, v);
            }
            return max ? best : total;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Dedicated video memory of the main graphics card, from its driver's registry entry.</summary>
    static double VideoMemoryGb()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            double best = 0;
            foreach (var sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try // some subkeys (e.g. Properties) are admin-only; skip them
                {
                    using var k = cls!.OpenSubKey(sub);
                    if (k?.GetValue("HardwareInformation.qwMemorySize") is long bytes) best = Math.Max(best, bytes / (1024.0 * 1024 * 1024));
                    else if (k?.GetValue("HardwareInformation.qwMemorySize") is byte[] raw && raw.Length >= 8) best = Math.Max(best, BitConverter.ToInt64(raw) / (1024.0 * 1024 * 1024));
                }
                catch { }
            }
            return best;
        }
        catch (Exception ex) { Log.Error("video memory size", ex); return 0; }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
    }
}
