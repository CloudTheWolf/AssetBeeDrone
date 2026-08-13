namespace AssetBeeDrone.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _lastSyncItem;
    private readonly ToolStripMenuItem _syncNowItem;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _syncInFlight;

    public TrayApplicationContext()
    {
        _lastSyncItem = new ToolStripMenuItem("Last sync: …")
        {
            Enabled = false
        };
        _syncNowItem = new ToolStripMenuItem("Sync Now", null, async (_, _) => await SyncNowAsync());

        ContextMenuStrip menu = new();
        menu.Items.Add(_lastSyncItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_syncNowItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitTray()));
        menu.Opening += async (_, _) => await RefreshStatusAsync();

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "AssetBee Drone",
            Visible = true,
            ContextMenuStrip = menu
        };

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _refreshTimer.Start();

        _ = RefreshStatusAsync();
    }

    private static Icon LoadIcon()
    {
        using Stream? stream = typeof(TrayApplicationContext).Assembly
            .GetManifestResourceStream("AssetBeeDrone.Tray.logo.ico");
        if (stream is not null)
        {
            return new Icon(stream);
        }

        string path = Path.Combine(AppContext.BaseDirectory, "logo.ico");
        if (File.Exists(path))
        {
            return new Icon(path);
        }

        return SystemIcons.Application;
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            TrayStatusResponse? status = await TrayPipeClient.SendAsync("status", CancellationToken.None);
            if (status is null)
            {
                SetUnavailable("No response from service.");
                return;
            }

            ApplyStatus(status);
        }
        catch
        {
            SetUnavailable("Service unavailable");
        }
    }

    private async Task SyncNowAsync()
    {
        if (_syncInFlight)
        {
            return;
        }

        _syncInFlight = true;
        _syncNowItem.Enabled = false;
        try
        {
            TrayStatusResponse? status = await TrayPipeClient.SendAsync("sync", CancellationToken.None);
            if (status is null)
            {
                ShowTip("AssetBee Drone", "No response from service.", ToolTipIcon.Warning);
                SetUnavailable("No response from service.");
                return;
            }

            ApplyStatus(status);
            if (status.Busy)
            {
                ShowTip("AssetBee Drone", status.Message ?? "Sync already in progress.", ToolTipIcon.Info);
            }
            else if (!string.IsNullOrWhiteSpace(status.LastError))
            {
                ShowTip("AssetBee Drone", status.LastError, ToolTipIcon.Warning);
            }
            else
            {
                ShowTip("AssetBee Drone", status.Message ?? "Sync completed.", ToolTipIcon.Info);
            }
        }
        catch (Exception exception)
        {
            SetUnavailable("Service unavailable");
            ShowTip("AssetBee Drone", $"Sync failed: {exception.Message}", ToolTipIcon.Error);
        }
        finally
        {
            _syncInFlight = false;
            _syncNowItem.Enabled = true;
        }
    }

    private void ApplyStatus(TrayStatusResponse status)
    {
        _lastSyncItem.Text = status.LastSuccessUtc is null
            ? "Last sync: Never"
            : $"Last sync: {status.LastSuccessUtc.Value.ToLocalTime():g}";

        string tooltip = status.Busy || status.Running
            ? "AssetBee Drone — syncing"
            : string.IsNullOrWhiteSpace(status.LastError)
                ? "AssetBee Drone — running"
                : "AssetBee Drone — last sync had errors";
        _notifyIcon.Text = TruncateTip(tooltip);
        _syncNowItem.Enabled = !_syncInFlight && !status.Busy;
    }

    private void SetUnavailable(string detail)
    {
        _lastSyncItem.Text = "Last sync: Unavailable";
        _notifyIcon.Text = TruncateTip($"AssetBee Drone — unavailable ({detail})");
        _syncNowItem.Enabled = !_syncInFlight;
    }

    private void ShowTip(string title, string text, ToolTipIcon icon)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(4000);
    }

    private void ExitTray()
    {
        _refreshTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string TruncateTip(string value) =>
        value.Length <= 63 ? value : value[..60] + "...";
}
