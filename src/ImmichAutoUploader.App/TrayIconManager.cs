using WinForms = System.Windows.Forms;

namespace ImmichAutoUploader.App;

/// <summary>
/// Manages the Windows notification area (system tray) presence, context menu, and desktop balloon notifications.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayIconManager"/> class and configures context menu items.
    /// </summary>
    /// <param name="onOpen">Action invoked to display the settings window (triggered via double-click or menu item).</param>
    /// <param name="onExit">Action invoked to cleanly terminate the application.</param>
    /// <param name="onUploadNow">Optional action invoked when the "Upload now" menu item is clicked.</param>
    public TrayIconManager(Action onOpen, Action onExit, Action? onUploadNow = null)
    {
        _icon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Immich Auto Uploader",
            Visible = true,
        };

        _icon.DoubleClick += (_, _) => onOpen();

        _menu = new WinForms.ContextMenuStrip();
        _menu.Items.Add("Open Settings", image: null, (_, _) => onOpen());
        if (onUploadNow is not null)
        {
            var uploadNow = onUploadNow; // local copy: null-state is preserved inside the lambda
            _menu.Items.Add("Upload now", image: null, (_, _) => uploadNow());
        }
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add("Exit", image: null, (_, _) => onExit());
        _icon.ContextMenuStrip = _menu;

        _icon.BalloonTipTitle = "Immich Auto Uploader";
    }

    /// <summary>
    /// Displays a system tray balloon notification tip to the user.
    /// </summary>
    /// <param name="message">The notification text to display.</param>
    /// <param name="icon">The severity icon (defaults to <see cref="WinForms.ToolTipIcon.Info"/>).</param>
    public void Notify(string message, WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.Info)
    {
        _icon.ShowBalloonTip(timeout: 5000, "Immich Auto Uploader", message, icon);
    }

    /// <summary>
    /// Hides and disposes the notification icon and context menu.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _menu.Dispose();
        _icon.Dispose();
    }
}
