namespace ImmichAutoUploader.Core.Services;

/// <summary>
/// Provides a lightweight, thread-safe file logging facility.
/// <para/>
/// Log files are partitioned daily (<c>app-yyyyMMdd.log</c>) under
/// <c>%AppData%\ImmichAutoUploader\logs\</c>. Write operations are serialized via
/// an internal synchronization lock. Logging operations are fully fault-tolerant
/// and are guaranteed never to throw unhandled exceptions or crash the host process.
/// </summary>
public static class AppLogger
{
    private static readonly object _lock = new();
    private static bool _dirEnsured;

    /// <summary>
    /// Gets the absolute directory path where log files are stored.
    /// </summary>
    private static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImmichAutoUploader", "logs");

    /// <summary>
    /// Writes an informational message to the current daily log file.
    /// </summary>
    /// <param name="message">The message text to record.</param>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>
    /// Writes a warning message to the current daily log file.
    /// </summary>
    /// <param name="message">The warning text to record.</param>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>
    /// Writes an error message and optional exception details to the current daily log file.
    /// </summary>
    /// <param name="message">The error description.</param>
    /// <param name="ex">Optional exception whose type, message, and stack trace will be appended.</param>
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex}");

    /// <summary>
    /// Deletes log files whose last modification timestamp exceeds the specified retention threshold.
    /// Failures during cleanup are suppressed silently.
    /// </summary>
    /// <param name="retainDays">The maximum age in days for retained log files. Defaults to 30 days.</param>
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
                catch
                {
                    // Suppress individual file deletion errors (e.g., file open in another reader).
                }
            }
        }
        catch
        {
            // Never allow log maintenance to crash the application.
        }
    }

    /// <summary>
    /// Formats and appends a single log entry under the synchronization lock.
    /// </summary>
    /// <param name="level">The severity level label (e.g., "INFO", "WARN", "ERROR").</param>
    /// <param name="message">The formatted log text.</param>
    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                if (!_dirEnsured)
                {
                    if (!Directory.Exists(LogDir))
                        Directory.CreateDirectory(LogDir);
                    _dirEnsured = true;
                }
                string path = Path.Combine(LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Reset _dirEnsured so that if the log directory was deleted externally, it will be recreated
            lock (_lock)
            {
                _dirEnsured = false;
            }
            // Never let logging failures take down the application.
        }
    }
}
