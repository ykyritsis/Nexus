using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Nexus.Interop;
using Nexus.Providers;

namespace Nexus.Services;

/// <summary>
/// Quiet auto-update from the GitHub releases the Release workflow publishes. Checks a couple of
/// minutes after start and then every six hours (one small request), downloads a newer build in the
/// background, and installs it only while you're not in a game and the overlay is closed: Nexus exits,
/// a small script copies the new files over and starts it again in the background.
/// Builds run from the source tree (next to Nexus.csproj) never update themselves.
/// </summary>
public sealed class Updater
{
    const string LatestUrl = "https://api.github.com/repos/ykyritsis/Nexus/releases/latest";
    const string AssetName = "Nexus-win-x64.zip";

    // Declared before Http: static fields initialise in order, and the client's user agent needs it.
    public static Version Current { get; } = typeof(App).Assembly.GetName().Version ?? new Version(0, 0);
    public static bool IsDevBuild { get; } = FindProject();

    static readonly HttpClient Http = CreateClient();
    readonly App _app;
    readonly DispatcherQueueTimer _check, _install;
    string? _ready;      // folder with the extracted new version
    Version? _readyVersion;
    bool _busy;

    public string Status { get; private set; } = IsDevBuild ? "Development build, updates off" : "Not checked yet";
    public bool UpdateReady => _ready != null;
    public event Action? StatusChanged;

    static string Folder => Path.Combine(Log.Folder, "update");
    static string AttemptFile => Path.Combine(Folder, "attempted.txt");
    Version? _failed; // a version whose install didn't take; not tried again

    public Updater(App app, DispatcherQueue ui)
    {
        _app = app;
        // Started again after an install: if we're still older than what was installed, it failed
        // (say the folder wasn't writable). Remember that instead of restarting every few minutes.
        try
        {
            if (File.Exists(AttemptFile) && Version.TryParse(File.ReadAllText(AttemptFile).Trim(), out var tried))
            {
                if (Normal(Current) < Normal(tried)) { _failed = tried; Log.Warn($"update {tried} didn't install; not retrying it"); }
                else File.Delete(AttemptFile);
            }
        }
        catch { }
        _check = ui.CreateTimer();
        _check.Interval = TimeSpan.FromMinutes(2); // first check soon after start, then every 6 h
        _check.Tick += async (_, _) => { _check.Interval = TimeSpan.FromHours(6); await CheckAsync(); };
        _install = ui.CreateTimer();
        _install.Interval = TimeSpan.FromMinutes(10);
        _install.Tick += (_, _) => TryInstall(onlyWhenIdle: true);
        if (!IsDevBuild) _check.Start();
    }

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Nexus/{Current.ToString(3)}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public async Task CheckAsync()
    {
        if (IsDevBuild || _busy || _ready != null) return;
        _busy = true;
        try
        {
            SetStatus("Checking for updates…");
            using var response = await Http.GetAsync(LatestUrl);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                SetStatus("No releases available");
                return;
            }
            response.EnsureSuccessStatusCode();
            var release = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var tag = ((string?)release?["tag_name"] ?? "").TrimStart('v');
            if (!Version.TryParse(tag, out var latest) || Normal(latest) <= Normal(Current))
            {
                SetStatus($"Up to date · checked {DateTime.Now:h:mm tt}");
                return;
            }
            if (_failed != null && Normal(latest) <= Normal(_failed))
            {
                SetStatus($"Version {latest.ToString(3)} couldn't be installed here");
                return;
            }
            if (!CanWrite(AppContext.BaseDirectory))
            {
                SetStatus("Can't update: Nexus's folder isn't writable");
                return;
            }
            var url = release?["assets"]?.AsArray()
                .FirstOrDefault(a => (string?)a?["name"] == AssetName)?["browser_download_url"]?.GetValue<string>();
            if (url is null) { SetStatus($"Version {latest.ToString(3)} has no download yet"); return; }

            SetStatus($"Downloading version {latest.ToString(3)}…");
            var folder = Folder;
            var zip = Path.Combine(folder, $"Nexus-{latest}.zip");
            var target = Path.Combine(folder, latest.ToString());
            Directory.CreateDirectory(folder);
            await using (var file = File.Create(zip))
            await using (var stream = await Http.GetStreamAsync(url))
                await stream.CopyToAsync(file);
            await Task.Run(() =>
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                ZipFile.ExtractToDirectory(zip, target);
                File.Delete(zip);
            });
            if (!File.Exists(Path.Combine(target, "Nexus.exe"))) throw new InvalidDataException("download has no Nexus.exe");

            _ready = target;
            _readyVersion = latest;
            Log.Info($"update {latest} downloaded");
            SetStatus($"Version {latest.ToString(3)} ready · installs when you're not playing");
            _install.Start();
            TryInstall(onlyWhenIdle: true); // right away if you're between games, else every 10 min
        }
        catch (Exception ex)
        {
            Log.Warn($"update check failed: {ex.Message}");
            SetStatus("Couldn't check for updates");
        }
        finally { _busy = false; }
    }

    /// <summary>Installs a downloaded update. Automatically only between games with the overlay closed.</summary>
    public void TryInstall(bool onlyWhenIdle)
    {
        if (_ready is null) return;
        if (onlyWhenIdle && (_app.Shell.IsOpen || NowPlaying.Detect(Native.GetForegroundWindow()) != null)) return;
        try
        {
            var dest = AppContext.BaseDirectory.TrimEnd('\\');
            var script = Path.Combine(Folder, "install.cmd");
            File.WriteAllText(AttemptFile, _readyVersion!.ToString());
            int pid = Environment.ProcessId;
            // Wait for this copy to exit, copy the new files over it, start it again.
            File.WriteAllText(script, $"""
                @echo off
                :wait
                tasklist /FI "PID eq {pid}" | find "{pid}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
                robocopy "{_ready}" "{dest}" /E /IS /IT /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul
                start "" "{dest}\Nexus.exe" --background
                rmdir /s /q "{_ready}"
                """);
            using var _ = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true });
            Log.Info($"installing update {_readyVersion} into {dest}");
            _app.Quit();
        }
        catch (Exception ex) { Log.Error("installing update", ex); }
    }

    void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke();
    }

    /// <summary>Treats 0.1.5 and 0.1.5.0 as the same version.</summary>
    static Version Normal(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".nexus-write-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    static bool FindProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Nexus.csproj"))) return true;
        return false;
    }
}
