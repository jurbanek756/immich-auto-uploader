using System.IO;
using System.Threading;
using System.Windows;
using ImmichAutoUploader.Core.Models;
using ImmichAutoUploader.Core.Security;
using ImmichAutoUploader.Core.Services;

namespace ImmichAutoUploader.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private TrayIconManager? _tray;
    private FileWatcherService? _fileWatcher;

    public static SettingsService SettingsService { get; } = new();
    public static ICredentialStore CredentialStore { get; } = new DpapiCredentialStore();
    public static AppSettings Settings { get; set; } = new();
    public static UploadQueue? Queue { get; private set; }
    public static UploadEngine? Engine { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only one instance may run: a second watcher on the same folder would double-upload.
        _singleInstance = new Mutex(initiallyOwned: true, "ImmichAutoUploader_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("Immich Auto Uploader is already running (check the system tray).",
                "Immich Auto Uploader", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Settings = SettingsService.Load();
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
            onExit: () => Dispatcher.Invoke(() => Shutdown()),
            onUploadNow: () => { var e = Engine; if (e is not null) _ = e.TriggerNowAsync(); });
        Engine.NotifyUser = msg => _tray?.Notify(msg, System.Windows.Forms.ToolTipIcon.Warning);
        Engine.Start();

        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            ShowSettings();

        base.OnStartup(e);
    }

    private void ShowSettings()
    {
        // Reuse the single settings window; closing it hides to tray instead of exiting.
        if (MainWindow is MainWindow w)
        {
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

    protected override void OnExit(ExitEventArgs e)
    {
        Engine?.Dispose();
        _fileWatcher?.Dispose();
        Queue?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
