namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Minimal thread-safe file logger. Phase 4 adds an in-app log viewer;
/// until then everything lands in %AppData%\ImmichAutoUploader\logs\.
/// Logging must never crash the app.
/// </summary>
public static class AppLogger
{
    private static readonly object _lock = new();

    private static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImmichAutoUploader", "logs");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    public static void PurgeOldLogs(int retainDays = 30)
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;
            var cutoff = DateTime.Now.AddDays(-retainDays);
            foreach (var file in Directory.GetFiles(LogDir, "app-*.log"))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTime < cutoff)
                        fi.Delete();
                }
                catch { }
            }
        }
        catch { }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                if (!Directory.Exists(LogDir))
                    Directory.CreateDirectory(LogDir);
                string path = Path.Combine(LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Never let logging take the app down.
        }
    }
}
