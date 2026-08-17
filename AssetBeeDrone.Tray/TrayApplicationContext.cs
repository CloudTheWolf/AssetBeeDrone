namespace AssetBeeDrone.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan ServiceAliveMaxAge = TimeSpan.FromSeconds(5);

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _lastSyncItem;
    private readonly ToolStripMenuItem _syncNowItem;
    private readonly ToolStripMenuItem _installUpdateItem;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _syncInFlight;
    private bool _installInFlight;
    private string? _balloonAnnouncedVersion;

    public TrayApplicationContext()
    {
        _lastSyncItem = new ToolStripMenuItem("Last sync: …")
        {
            Enabled = false
        };
        _syncNowItem = new ToolStripMenuItem("Sync Now", null, (_, _) => SyncNow());
        _installUpdateItem = new ToolStripMenuItem("Install Update", null, (_, _) => InstallUpdate())
        {
            Visible = false,
            Enabled = false
        };

        ContextMenuStrip menu = new();
        menu.Items.Add(_lastSyncItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_syncNowItem);
        menu.Items.Add(_installUpdateItem);
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
        _notifyIcon.BalloonTipClicked += (_, _) => InstallUpdate();

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

            if (status.QuitTray ||
                string.Equals(status.UpdateState, "Installing", StringComparison.OrdinalIgnoreCase))
            {
                ExitTray();
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

    private void InstallUpdate()
    {
        if (_installInFlight)
        {
            return;
        }

        try
        {
            TrayStatusResponse? before = TrayFileIpcClient.ReadStatus();
            if (before is null || !TrayFileIpcClient.IsServiceAlive(before, ServiceAliveMaxAge))
            {
                ShowTip("AssetBee Drone", "Service is not running.", ToolTipIcon.Warning);
                return;
            }

            if (!before.UpdateAvailable)
            {
                return;
            }

            if (string.Equals(before.UpdateState, "Downloading", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(before.UpdateState, "Installing", StringComparison.OrdinalIgnoreCase))
            {
                ShowTip("AssetBee Drone", "Update already in progress.", ToolTipIcon.Info);
                return;
            }

            _installInFlight = true;
            _installUpdateItem.Enabled = false;
            TrayFileIpcClient.RequestInstall();
            ShowTip(
                "AssetBee Drone",
                string.IsNullOrWhiteSpace(before.UpdateVersion)
                    ? "Downloading update…"
                    : $"Downloading update {before.UpdateVersion}…",
                ToolTipIcon.Info);
            RefreshStatus();
        }
        catch (Exception exception)
        {
            ShowTip("AssetBee Drone", $"Update failed: {exception.Message}", ToolTipIcon.Error);
            _installInFlight = false;
            _installUpdateItem.Enabled = true;
        }
    }

    private void ApplyStatus(TrayStatusResponse status)
    {
        _lastSyncItem.Text = status.LastSuccessUtc is null
            ? "Last sync: Never"
            : $"Last sync: {status.LastSuccessUtc.Value.ToLocalTime():g}";

        bool updateBusy =
            string.Equals(status.UpdateState, "Downloading", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.UpdateState, "Installing", StringComparison.OrdinalIgnoreCase);
        bool canInstall = status.UpdateAvailable && !updateBusy && !_installInFlight;

        _installUpdateItem.Visible = status.UpdateAvailable || updateBusy;
        _installUpdateItem.Enabled = canInstall;
        _installUpdateItem.Text = updateBusy
            ? (string.Equals(status.UpdateState, "Installing", StringComparison.OrdinalIgnoreCase)
                ? "Installing update…"
                : "Downloading update…")
            : string.IsNullOrWhiteSpace(status.UpdateVersion)
                ? "Install Update"
                : $"Install Update ({status.UpdateVersion})";

        if (status.UpdateAvailable &&
            !updateBusy &&
            !string.IsNullOrWhiteSpace(status.UpdateVersion) &&
            !string.Equals(_balloonAnnouncedVersion, status.UpdateVersion, StringComparison.Ordinal))
        {
            _balloonAnnouncedVersion = status.UpdateVersion;
            ShowTip(
                "AssetBee Drone",
                $"Update {status.UpdateVersion} available — click to install.",
                ToolTipIcon.Info);
        }

        if (!string.IsNullOrWhiteSpace(status.UpdateError) &&
            string.Equals(status.UpdateState, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            _installInFlight = false;
        }

        string tooltip = updateBusy
            ? "AssetBee Drone — updating"
            : status.Busy || status.Running
                ? "AssetBee Drone — syncing"
                : status.UpdateAvailable
                    ? TruncateTip($"AssetBee Drone — update {status.UpdateVersion} available")
                    : string.IsNullOrWhiteSpace(status.LastError)
                        ? "AssetBee Drone — running"
                        : "AssetBee Drone — last sync had errors";
        _notifyIcon.Text = TruncateTip(tooltip);
        _syncNowItem.Enabled = !_syncInFlight && !status.Busy && !status.Running && !updateBusy;
    }

    private void SetUnavailable(string detail)
    {
        _lastSyncItem.Text = string.IsNullOrWhiteSpace(detail)
            ? "Last sync: Unavailable"
            : TruncateTip($"Unavailable: {detail}");
        _notifyIcon.Text = TruncateTip("AssetBee Drone — unavailable");
        _syncNowItem.Enabled = !_syncInFlight;
        _installUpdateItem.Visible = false;
        _installUpdateItem.Enabled = false;
        _installInFlight = false;
    }

    private void ShowTip(string title, string text, ToolTipIcon icon)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(8000);
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
