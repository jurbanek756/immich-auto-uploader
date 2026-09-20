using WinForms = System.Windows.Forms;

namespace ImmichAutoUploader.App;

/// <summary>
/// System tray icon: the app lives here when the settings window is closed.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private bool _disposed;

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

    public void Notify(string message, WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.Info)
    {
        _icon.ShowBalloonTip(timeout: 5000, "Immich Auto Uploader", message, icon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _menu.Dispose();
        _icon.Dispose();
    }
}
