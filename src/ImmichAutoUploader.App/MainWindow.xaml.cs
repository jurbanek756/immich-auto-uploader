using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using ImmichAutoUploader.Core.Models;
using ImmichAutoUploader.Core.Security;
using WinForms = System.Windows.Forms;

namespace ImmichAutoUploader.App;

/// <summary>
/// Primary desktop configuration and management window.
/// <para/>
/// <b>Features:</b>
/// <list type="bullet">
///   <item><description>Configures Immich, Jellyfin, and Tailscale connection settings.</description></item>
///   <item><description>Performs interactive connection testing against Immich (<c>/api/users/me</c>) and Jellyfin (<c>/System/Info</c>).</description></item>
///   <item><description>Displays real-time aggregate statistics from the SQLite upload queue.</description></item>
///   <item><description>Provides manual controls to trigger an immediate batch ("Upload now") or requeue failed files.</description></item>
///   <item><description>Configures automatic startup via the Windows CurrentUser Run registry key.</description></item>
/// </list>
/// </summary>
public partial class MainWindow : Window
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ImmichAutoUploader";

    private static readonly HttpClient SharedTestClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    }) { Timeout = TimeSpan.FromSeconds(15) };

    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        _ = RefreshQueueStatusAsync();

        _statusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _statusTimer.Tick += (_, _) =>
        {
            if (IsVisible && !IsExplicitExit)
                _ = RefreshQueueStatusAsync();
        };
        _statusTimer.Start();

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                _ = RefreshQueueStatusAsync();
                _statusTimer.Start();
            }
            else
            {
                _statusTimer.Stop();
            }
        };
    }

    /// <summary>
    /// Gets or sets a value indicating whether window close was explicitly requested
    /// via the system tray or application shutdown. If <c>false</c>, closing the window
    /// simply hides it to the system tray.
    /// </summary>
    public bool IsExplicitExit { get; set; }

    /// <summary>
    /// Intercepts the window closing event. Hides to the system tray unless <see cref="IsExplicitExit"/> is true.
    /// </summary>
    /// <param name="e">Event arguments allowing cancellation.</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!IsExplicitExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _statusTimer.Stop();
        base.OnClosing(e);
    }

    /// <summary>
    /// Populates UI controls with values from <see cref="App.Settings"/> and <see cref="App.CredentialStore"/>.
    /// </summary>
    private void LoadSettings()
    {
        AppSettings s = App.Settings;

        TxtUrl.Text = s.ImmichUrl;
        TxtApiKey.Password = App.CredentialStore.Load(CredentialNames.ApiKey) ?? string.Empty;
        TxtAdminApiKey.Password = App.CredentialStore.Load(CredentialNames.AdminApiKey) ?? string.Empty;
        TxtWatchFolder.Text = s.WatchFolder;
        TxtDoneFolder.Text = s.DoneFolder;
        TxtJellyfinUrl.Text = s.JellyfinUrl;
        TxtJellyfinApiKey.Password = App.CredentialStore.Load(CredentialNames.JellyfinApiKey) ?? string.Empty;
        TxtJellyfinLibrary.Text = s.JellyfinLibraryName;
        ChkUseTailscale.IsChecked = s.UseTailscale;
        TxtTailscaleUrl.Text = s.ImmichUrlViaTailscale;
        TxtGoPath.Text = s.ImmichGoPath;
        LblGoVersion.Text = string.IsNullOrWhiteSpace(s.ImmichGoVersion)
            ? "(not found — run build.ps1)"
            : s.ImmichGoVersion;
        TxtConcurrentTasks.Text = s.ConcurrentTasks.ToString();
        TxtBatchMinutes.Text = s.BatchIntervalMinutes.ToString();
        TxtBatchSize.Text = s.MaxFilesPerBatch.ToString();
        ChkPauseJobs.IsChecked = s.PauseImmichJobs;
        ChkStartWithWindows.IsChecked = s.StartWithWindows;

        foreach (System.Windows.Controls.ComboBoxItem item in CmbOnErrors.Items)
        {
            if (string.Equals(item.Content as string, s.OnErrors, StringComparison.OrdinalIgnoreCase))
            {
                CmbOnErrors.SelectedItem = item;
                break;
            }
        }
        CmbOnErrors.SelectedItem ??= CmbOnErrors.Items[0];
    }

    /// <summary>
    /// Validates inputs, saves non-secret settings to JSON, encrypts credentials via DPAPI,
    /// updates Windows startup registry configuration, and restarts the file watcher.
    /// </summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtConcurrentTasks.Text, out int concurrent) || concurrent is < 1 or > 20)
        {
            SetStatus("Concurrent tasks must be a number between 1 and 20.");
            return;
        }
        if (!int.TryParse(TxtBatchMinutes.Text, out int batchMinutes) || batchMinutes < 1)
        {
            SetStatus("Batch interval must be at least 1 minute.");
            return;
        }
        if (!int.TryParse(TxtBatchSize.Text, out int batchSize) || batchSize < 1)
        {
            SetStatus("Max files per batch must be at least 1.");
            return;
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(TxtWatchFolder.Text) && !Directory.Exists(TxtWatchFolder.Text))
            {
                SetStatus("Watch folder does not exist.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(TxtWatchFolder.Text) &&
                !string.IsNullOrWhiteSpace(TxtDoneFolder.Text))
            {
                string cleanWatch = Path.GetFullPath(TxtWatchFolder.Text).TrimEnd(Path.DirectorySeparatorChar);
                string cleanDone = Path.GetFullPath(TxtDoneFolder.Text).TrimEnd(Path.DirectorySeparatorChar);

                if (string.Equals(cleanWatch, cleanDone, StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("Done folder must be different from the watch folder.");
                    return;
                }

                if (cleanWatch.StartsWith(cleanDone + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("Done folder cannot be a parent directory of the watch folder.");
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetStatus("Watch or Done folder contains an invalid path format.");
            return;
        }

        if (string.IsNullOrWhiteSpace(TxtUrl.Text) ||
            !Uri.TryCreate(TxtUrl.Text.Trim(), UriKind.Absolute, out var immichUri) ||
            (immichUri.Scheme != Uri.UriSchemeHttp && immichUri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus("Immich server URL must be a valid http:// or https:// URL.");
            return;
        }

        if (string.IsNullOrWhiteSpace(TxtApiKey.Password))
        {
            SetStatus("Immich API key is required.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(TxtJellyfinUrl.Text) && string.IsNullOrWhiteSpace(TxtJellyfinApiKey.Password))
        {
            SetStatus("Jellyfin URL is set but the API key is empty.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(TxtJellyfinUrl.Text) &&
            (!Uri.TryCreate(TxtJellyfinUrl.Text.Trim(), UriKind.Absolute, out var jfUri) ||
             (jfUri.Scheme != Uri.UriSchemeHttp && jfUri.Scheme != Uri.UriSchemeHttps)))
        {
            SetStatus("Jellyfin server URL must be a valid http:// or https:// URL.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(TxtTailscaleUrl.Text) &&
            (!Uri.TryCreate(TxtTailscaleUrl.Text.Trim(), UriKind.Absolute, out var tsUri) ||
             (tsUri.Scheme != Uri.UriSchemeHttp && tsUri.Scheme != Uri.UriSchemeHttps)))
        {
            SetStatus("Immich URL via Tailscale must be a valid http:// or https:// URL.");
            return;
        }

        var s = new AppSettings
        {
            ImmichUrl = TxtUrl.Text.Trim().TrimEnd('/'),
            WatchFolder = TxtWatchFolder.Text.Trim(),
            DoneFolder = TxtDoneFolder.Text.Trim(),
            JellyfinUrl = TxtJellyfinUrl.Text.Trim().TrimEnd('/'),
            JellyfinLibraryName = TxtJellyfinLibrary.Text.Trim(),
            UseTailscale = ChkUseTailscale.IsChecked == true,
            ImmichUrlViaTailscale = TxtTailscaleUrl.Text.Trim().TrimEnd('/'),
            TailscalePath = App.Settings.TailscalePath,
            ImmichGoPath = TxtGoPath.Text.Trim(),
            ImmichGoVersion = App.Settings.ImmichGoVersion, // managed by build.ps1
            DeviceUuid = App.Settings.DeviceUuid,
            ConcurrentTasks = concurrent,
            OnErrors = (CmbOnErrors.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? "continue",
            PauseImmichJobs = ChkPauseJobs.IsChecked == true,
            BatchIntervalMinutes = batchMinutes,
            MaxFilesPerBatch = batchSize,
            StartWithWindows = ChkStartWithWindows.IsChecked == true,
        };

        App.SettingsService.Save(s);
        App.Settings = s;

        // Secrets -> DPAPI, never the JSON file. Empty admin/Jellyfin keys clear their slots.
        App.CredentialStore.Save(CredentialNames.ApiKey, TxtApiKey.Password);
        if (string.IsNullOrEmpty(TxtAdminApiKey.Password))
            App.CredentialStore.Delete(CredentialNames.AdminApiKey);
        else
            App.CredentialStore.Save(CredentialNames.AdminApiKey, TxtAdminApiKey.Password);
        if (string.IsNullOrEmpty(TxtJellyfinApiKey.Password))
            App.CredentialStore.Delete(CredentialNames.JellyfinApiKey);
        else
            App.CredentialStore.Save(CredentialNames.JellyfinApiKey, TxtJellyfinApiKey.Password);

        ApplyStartWithWindows(s.StartWithWindows);

        // Folder changes take effect immediately (watcher restarts).
        ((App)System.Windows.Application.Current).RestartWatcher();

        SetStatus($"Saved at {DateTime.Now:T}. Watcher restarted.");
    }

    private void BrowseWatch_Click(object sender, RoutedEventArgs e)
        => TxtWatchFolder.Text = PickFolder(TxtWatchFolder.Text) ?? TxtWatchFolder.Text;

    private void BrowseDone_Click(object sender, RoutedEventArgs e)
        => TxtDoneFolder.Text = PickFolder(TxtDoneFolder.Text) ?? TxtDoneFolder.Text;

    /// <summary>
    /// Opens a Windows Forms folder browser dialog.
    /// </summary>
    private static string? PickFolder(string current)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Select folder",
            SelectedPath = Directory.Exists(current) ? current : string.Empty,
            ShowNewFolderButton = true,
        };
        return dlg.ShowDialog() == WinForms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    /// <summary>
    /// Tests HTTP connectivity to the configured Immich instance and optional Jellyfin server.
    /// </summary>
    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        if (btn is not null) btn.IsEnabled = false;

        try
        {
            string url = TxtUrl.Text.Trim().TrimEnd('/');
            string apiKey = TxtApiKey.Password;

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
            {
                SetStatus("Enter a server URL and API key first.");
                return;
            }

            SetStatus("Testing connection…");
            string immichWho;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url + "/api/users/me");
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                using var resp = await SharedTestClient.SendAsync(req);

                if (!resp.IsSuccessStatusCode)
                {
                    SetStatus($"Immich connection failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Check URL/key.");
                    return;
                }

                immichWho = "ok";
                try
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("name", out var name))
                        immichWho = name.GetString() ?? immichWho;
                    else if (doc.RootElement.TryGetProperty("email", out var email))
                        immichWho = email.GetString() ?? immichWho;
                }
                catch { /* display name is best-effort */ }
            }
            catch (Exception ex)
            {
                SetStatus($"Immich connection failed: {ex.Message}");
                return;
            }

            // Optional Jellyfin check. NOTE: this Jellyfin instance requires the
            // "Authorization: MediaBrowser Token=..." header (?api_key= / X-Emby-Token 401 here).
            string jellyfinMsg = string.Empty;
            string jellyfinUrl = TxtJellyfinUrl.Text.Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(jellyfinUrl))
            {
                string jellyfinKey = TxtJellyfinApiKey.Password;
                if (string.IsNullOrWhiteSpace(jellyfinKey))
                {
                    SetStatus("Jellyfin URL is set but the API key is empty.");
                    return;
                }

                try
                {
                    using var jfReq = new HttpRequestMessage(HttpMethod.Get, jellyfinUrl + "/System/Info");
                    jfReq.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{jellyfinKey}\"");
                    using var jfResp = await SharedTestClient.SendAsync(jfReq);

                    if (!jfResp.IsSuccessStatusCode)
                    {
                        SetStatus($"Jellyfin check failed: HTTP {(int)jfResp.StatusCode}. Check URL/key.");
                        return;
                    }

                    string jfVersion = "?";
                    try
                    {
                        using var doc = JsonDocument.Parse(await jfResp.Content.ReadAsStringAsync());
                        if (doc.RootElement.TryGetProperty("Version", out var v))
                            jfVersion = v.GetString() ?? jfVersion;
                    }
                    catch { /* version is best-effort */ }

                    jellyfinMsg = $" Jellyfin OK (v{jfVersion}).";
                }
                catch (Exception ex)
                {
                    SetStatus($"Jellyfin check failed: {ex.Message}");
                    return;
                }
            }

            SetStatus($"Immich: connected as {immichWho}.{jellyfinMsg}");
        }
        finally
        {
            if (btn is not null) btn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Configures or removes the application from the Windows CurrentUser Run registry key.
    /// </summary>
    /// <param name="enable"><c>true</c> to register the application with <c>--tray</c>; <c>false</c> to remove it.</param>
    internal static void ApplyStartWithWindows(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        if (enable)
        {
            string exe = Environment.ProcessPath ?? string.Empty;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(RunValueName, $"\"{exe}\" --tray");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Updates the status bar text on the main window.
    /// </summary>
    private void SetStatus(string message) => StatusText.Text = message;

    private void RefreshQueue_Click(object sender, RoutedEventArgs e) => _ = RefreshQueueStatusAsync();

    /// <summary>
    /// Triggers an immediate upload batch on demand.
    /// </summary>
    private async void UploadNow_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        if (btn is not null) btn.IsEnabled = false;

        try
        {
            var engine = App.Engine;
            if (engine is null)
            {
                SetStatus("Upload engine is not running.");
                return;
            }
            SetStatus("Upload batch running…");
            bool started = await engine.TriggerNowAsync();
            SetStatus(started ? "Batch finished." : "A batch is already running; try again shortly.");
            await RefreshQueueStatusAsync();
        }
        finally
        {
            if (btn is not null) btn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Resets all failed items back to pending and refreshes the queue status display.
    /// </summary>
    private async void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as System.Windows.Controls.Button;
        if (btn is not null) btn.IsEnabled = false;

        try
        {
            if (App.Queue is null)
            {
                SetStatus("Upload queue is not initialized.");
                return;
            }
            int requeued = App.Queue.RequeueFailed();
            SetStatus($"Requeued {requeued} failed file(s) back to Pending.");
            await RefreshQueueStatusAsync();
        }
        finally
        {
            if (btn is not null) btn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Queries aggregate queue metrics from the database on a background thread and updates the UI.
    /// </summary>
    private async Task RefreshQueueStatusAsync()
    {
        // Queue DB access is synchronous; hop off the UI thread to be safe.
        await Task.Run(() =>
        {
            try
            {
                if (Dispatcher.HasShutdownStarted)
                    return;

                if (App.Queue is null)
                {
                    try { Dispatcher.Invoke(() => TxtQueueStatus.Text = "Queue not initialized."); } catch { }
                    return;
                }
                var (pending, uploading, uploaded, failed) = App.Queue.GetStats();
                if (!Dispatcher.HasShutdownStarted)
                {
                    try
                    {
                        Dispatcher.Invoke(() =>
                            TxtQueueStatus.Text =
                                $"Pending: {pending}    Uploading: {uploading}    Uploaded: {uploaded}    Failed: {failed}");
                    }
                    catch { /* shutdown race */ }
                }
            }
            catch (Exception ex) when (ex is not TaskCanceledException)
            {
                if (!Dispatcher.HasShutdownStarted)
                {
                    try
                    {
                        Dispatcher.Invoke(() => TxtQueueStatus.Text = "Error reading queue: " + ex.Message);
                    }
                    catch { /* shutdown race */ }
                }
            }
        });
    }
}
