namespace AssetBeeDrone.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan ServiceAliveMaxAge = TimeSpan.FromSeconds(5);

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
        _syncNowItem = new ToolStripMenuItem("Sync Now", null, (_, _) => SyncNow());

        ContextMenuStrip menu = new();
        menu.Items.Add(_lastSyncItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_syncNowItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitTray()));
        menu.Opening += (_, _) => RefreshStatus();

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "AssetBee Drone",
            Visible = true,
            ContextMenuStrip = menu
        };

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1_000 };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();

        RefreshStatus();
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

    private void RefreshStatus()
    {
        try
        {
            TrayStatusResponse? status = TrayFileIpcClient.ReadStatus();
            if (status is null)
            {
                SetUnavailable("Waiting for service status file.");
                return;
            }

            if (!TrayFileIpcClient.IsServiceAlive(status, ServiceAliveMaxAge))
            {
                SetUnavailable("Service status is stale. Is AssetBeeDrone running?");
                return;
            }

            ApplyStatus(status);
        }
        catch (Exception exception)
        {
            SetUnavailable(exception.Message);
        }
    }

    private void SyncNow()
    {
        if (_syncInFlight)
        {
            return;
        }

        _syncInFlight = true;
        _syncNowItem.Enabled = false;
        try
        {
            TrayStatusResponse? before = TrayFileIpcClient.ReadStatus();
            if (before is null || !TrayFileIpcClient.IsServiceAlive(before, ServiceAliveMaxAge))
            {
                ShowTip("AssetBee Drone", "Service is not running.", ToolTipIcon.Warning);
                SetUnavailable("Service is not running.");
                return;
            }

            if (before.Busy || before.Running)
            {
                ShowTip("AssetBee Drone", before.Message ?? "Sync already in progress.", ToolTipIcon.Info);
                ApplyStatus(before);
                return;
            }

            TrayFileIpcClient.RequestSync();
            ShowTip("AssetBee Drone", "Sync requested.", ToolTipIcon.Info);
            RefreshStatus();
        }
        catch (Exception exception)
        {
            SetUnavailable(exception.Message);
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
        _syncNowItem.Enabled = !_syncInFlight && !status.Busy && !status.Running;
    }

    private void SetUnavailable(string detail)
    {
        _lastSyncItem.Text = string.IsNullOrWhiteSpace(detail)
            ? "Last sync: Unavailable"
            : TruncateTip($"Unavailable: {detail}");
        _notifyIcon.Text = TruncateTip("AssetBee Drone — unavailable");
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
