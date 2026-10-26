namespace Nexus.Services;

/// <summary>Plain text log in %LOCALAPPDATA%\Nexus\nexus.log, trimmed when it passes 512 KB.</summary>
public static class Log
{
    static readonly object Gate = new();
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus");
    static readonly string FilePath = Path.Combine(Folder, "nexus.log");

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
