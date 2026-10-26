using System.Diagnostics;
using Nexus.Interop;

namespace Nexus.Providers;

public sealed record AppUsage(string Name, string? ExePath, double RamGb, double Cpu, int[] Pids);

/// <summary>
/// The apps using the most memory right now, with their CPU share since the last sample. Only
/// sampled while the Widgets tab is open; exe paths and names are cached per process.
/// </summary>
public sealed class TopApps
{
    readonly Dictionary<int, TimeSpan> _lastCpu = new();
    readonly Dictionary<int, string?> _paths = new();
    readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    DateTime _lastAt = DateTime.UtcNow;

    public List<AppUsage> Sample(int count)
    {
        var now = DateTime.UtcNow;
        double wall = Math.Max(0.001, (now - _lastAt).TotalMilliseconds);
        _lastAt = now;
        var seen = new HashSet<int>();
        var groups = new Dictionary<string, (double ram, double cpu, List<int> pids, string? path)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id <= 4 || p.SessionId == 0) continue; // skip system and services
                seen.Add(p.Id);
                double cpu = 0;
                try
                {
                    var t = p.TotalProcessorTime;
                    if (_lastCpu.TryGetValue(p.Id, out var before)) cpu = (t - before).TotalMilliseconds / wall / Environment.ProcessorCount * 100;
                    _lastCpu[p.Id] = t;
                }
                catch { } // admin processes don't share CPU time
                if (!_paths.TryGetValue(p.Id, out var path)) _paths[p.Id] = path = Native.ProcessPath((uint)p.Id);
                var name = p.ProcessName;
                groups.TryGetValue(name, out var g);
                g.pids ??= new List<int>();
                g.pids.Add(p.Id);
                g.ram += p.WorkingSet64 / (1024.0 * 1024 * 1024);
                g.cpu += cpu;
                g.path ??= path;
                groups[name] = g;
            }
            catch { }
            finally { p.Dispose(); }
        }
        // Forget processes that have exited.
        foreach (var gone in _lastCpu.Keys.Where(id => !seen.Contains(id)).ToList()) { _lastCpu.Remove(gone); _paths.Remove(gone); }

        return groups.Select(kv => new AppUsage(Friendly(kv.Value.path, kv.Key), kv.Value.path, kv.Value.ram, kv.Value.cpu, kv.Value.pids.ToArray()))
            .Where(a => !a.Name.Equals("Nexus", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.RamGb).Take(count).ToList();
    }

    string Friendly(string? path, string fallback)
    {
        if (_names.TryGetValue(fallback, out var cached)) return cached;
        string name = fallback;
        try
        {
            if (path != null && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d) name = d;
        }
        catch { }
        return _names[fallback] = name;
    }
}
