using System.IO;
using System.Threading;
using System.Windows;
using ImmichAutoUploader.Core.Models;
using ImmichAutoUploader.Core.Security;
using ImmichAutoUploader.Core.Services;

namespace ImmichAutoUploader.App;

/// <summary>
/// Application entry point and lifecycle orchestrator for the WPF desktop client.
/// <para/>
/// <b>Responsibilities:</b>
/// <list type="bullet">
///   <item><description>Enforces single-instance execution via a named Windows mutex (<c>Local\ImmichAutoUploader_SingleInstance</c>).</description></item>
///   <item><description>Initializes settings, DPAPI credential storage, and the persistent SQLite upload queue.</description></item>
///   <item><description>Executes crash recovery on startup (<see cref="UploadQueue.ResetStuckUploading"/>).</description></item>
///   <item><description>Bootstraps background services (<see cref="FileWatcherService"/>, <see cref="UploadEngine"/>, and <see cref="TrayIconManager"/>).</description></item>
///   <item><description>Orchestrates clean, orderly resource disposal on system shutdown or application exit.</description></item>
/// </list>
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private EventWaitHandle? _showSettingsSignal;
    private RegisteredWaitHandle? _showSettingsRegistration;
    private TrayIconManager? _tray;
    private FileWatcherService? _fileWatcher;

    /// <summary>
    /// Gets the shared settings persistence service.
    /// </summary>
    public static SettingsService SettingsService { get; } = new();

    /// <summary>
    /// Gets the Windows DPAPI credential store for encrypted secret management.
    /// </summary>
    public static ICredentialStore CredentialStore { get; } = new DpapiCredentialStore();

    /// <summary>
    /// Gets or sets the active in-memory application settings.
    /// </summary>
    public static AppSettings Settings { get; set; } = new();

    /// <summary>
    /// Gets the singleton SQLite upload queue instance.
    /// </summary>
    public static UploadQueue? Queue { get; private set; }

    /// <summary>
    /// Gets the singleton background upload engine instance.
    /// </summary>
    public static UploadEngine? Engine { get; private set; }

    /// <summary>
    /// Handles application startup, initializes single-instance mutex, and bootstraps services.
    /// </summary>
    /// <param name="e">Startup arguments, including optional <c>--tray</c> flag.</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one instance may run: a second watcher on the same folder would double-upload.
        _singleInstance = new Mutex(false, @"Local\ImmichAutoUploader_SingleInstance");
        try
        {
            _ownsMutex = _singleInstance.WaitOne(TimeSpan.Zero, false);
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed while holding the mutex; we now own it.
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(@"Local\ImmichAutoUploader_ShowSettingsSignal");
                signal.Set();
            }
            catch
            {
                IntPtr hWnd = FindWindow(null, "Immich Auto Uploader — Settings");
                if (hWnd != IntPtr.Zero)
                {
                    ShowWindow(hWnd, SW_RESTORE);
                    SetForegroundWindow(hWnd);
                }
                else
                {
                    System.Windows.MessageBox.Show("Immich Auto Uploader is already running (check the system tray).",
                        "Immich Auto Uploader", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            Shutdown();
            return;
        }

        try
        {
            _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ImmichAutoUploader_ShowSettingsSignal");
            _showSettingsRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showSettingsSignal,
                (state, timedOut) =>
                {
                    if (!timedOut)
                    {
                        try
                        {
                            if (!Dispatcher.HasShutdownStarted)
                                Dispatcher.Invoke(ShowSettings);
                        }
                        catch { /* shutdown race */ }
                    }
                },
                null,
                -1,
                false);
        }
        catch { /* best effort activation signal */ }

        AppLogger.PurgeOldLogs();

        Settings = SettingsService.Load();
        ImmichAutoUploader.App.MainWindow.ApplyStartWithWindows(Settings.StartWithWindows);
        ResolveImmichGoPath();

        // Persistent queue + crash recovery, then start watching the folder.
        Queue = new UploadQueue(UploadQueue.DefaultDbPath);
        int recovered = Queue.ResetStuckUploading();
        if (recovered > 0)
            AppLogger.Info($"Recovered {recovered} interrupted upload(s) back to Pending.");
        StartWatching();

        // Stable per-PC device id for Immich (--device-uuid), generated once.
        if (string.IsNullOrWhiteSpace(Settings.DeviceUuid))
        {
            Settings.DeviceUuid = Guid.NewGuid().ToString();
            SettingsService.Save(Settings);
        }

        var queue = Queue ?? throw new InvalidOperationException("Upload queue was not initialized.");
        Engine = new UploadEngine(
            queue,
            getSettings: () => Settings,
            getCredentials: () => new EngineCredentials(
                CredentialStore.Load(CredentialNames.ApiKey) ?? string.Empty,
                CredentialStore.Load(CredentialNames.AdminApiKey),
                CredentialStore.Load(CredentialNames.JellyfinApiKey)));
        _tray = new TrayIconManager(
            onOpen: () => Dispatcher.Invoke(ShowSettings),
            onExit: () => Dispatcher.Invoke(() =>
            {
                if (MainWindow is MainWindow w) w.IsExplicitExit = true;
                Shutdown();
            }),
            onUploadNow: () => { var e = Engine; if (e is not null) _ = e.TriggerNowAsync(); });
        Engine.NotifyUser = msg => _tray?.Notify(msg, System.Windows.Forms.ToolTipIcon.Warning);
        Engine.Start();

        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            ShowSettings();

        base.OnStartup(e);
    }

    /// <summary>
    /// Displays or restores the primary settings window.
    /// </summary>
    private void ShowSettings()
    {
        // Reuse the single settings window; closing it hides to tray instead of exiting.
        if (MainWindow is MainWindow w)
        {
            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;
            w.Show();
            w.Activate();
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    /// <summary>
    /// (Re)starts the folder watcher from current settings. Called at startup
    /// and after Save, so folder changes take effect without restarting the app.
    /// </summary>
    public void StartWatching()
    {
        if (Queue is null)
            return;

        if (string.IsNullOrWhiteSpace(Settings.WatchFolder) || !Directory.Exists(Settings.WatchFolder))
        {
            AppLogger.Info("File watcher not started: watch folder is not configured.");
            return;
        }

        _fileWatcher = new FileWatcherService(Settings.WatchFolder, Settings.DoneFolder, Queue);
        _fileWatcher.Start();
    }

    /// <summary>
    /// Disposes the current watcher and re-initializes it with updated paths from <see cref="Settings"/>.
    /// </summary>
    public void RestartWatcher()
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;
        StartWatching();
    }

    /// <summary>
    /// If the user hasn't picked an immich-go binary, default to the one bundled
    /// next to the app by build.ps1 (tools\immich-go\immich-go.exe).
    /// </summary>
    private static void ResolveImmichGoPath()
    {
        if (!string.IsNullOrWhiteSpace(Settings.ImmichGoPath) && File.Exists(Settings.ImmichGoPath))
            return;

        string candidate = Path.Combine(AppContext.BaseDirectory, "tools", "immich-go", "immich-go.exe");
        if (File.Exists(candidate))
        {
            Settings.ImmichGoPath = candidate;

            string versionFile = Path.Combine(AppContext.BaseDirectory, "tools", "immich-go", "pinned-version.txt");
            if (File.Exists(versionFile))
                Settings.ImmichGoVersion = File.ReadAllText(versionFile).Trim();
        }
    }

    /// <summary>
    /// Handles OS session ending (logoff or reboot) to ensure windows are marked for explicit exit.
    /// </summary>
    /// <param name="e">Session ending event arguments.</param>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow w) w.IsExplicitExit = true;
        base.OnSessionEnding(e);
    }

    /// <summary>
    /// Performs graceful shutdown and resource cleanup of background services and the single-instance mutex.
    /// </summary>
    /// <param name="e">Exit event arguments.</param>
    protected override void OnExit(ExitEventArgs e)
    {
        _showSettingsRegistration?.Unregister(null);
        _showSettingsSignal?.Dispose();
        Engine?.Dispose();
        _fileWatcher?.Dispose();
        Queue?.Dispose();
        _tray?.Dispose();
        if (_singleInstance is not null)
        {
            if (_ownsMutex)
            {
                try { _singleInstance.ReleaseMutex(); }
                catch { /* not owned or already released */ }
            }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;
}
