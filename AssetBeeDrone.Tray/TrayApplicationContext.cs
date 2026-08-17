using System.Reflection;

namespace AssetBeeDrone.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan ServiceAliveMaxAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CheckUpdateTimeout = TimeSpan.FromSeconds(45);

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _lastSyncItem;
    private readonly ToolStripMenuItem _syncNowItem;
    private readonly ToolStripMenuItem _checkUpdatesItem;
    private readonly ToolStripMenuItem _installUpdateItem;
    private readonly ToolStripMenuItem _aboutItem;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _syncInFlight;
    private bool _checkInFlight;
    private bool _installInFlight;
    private string? _balloonAnnouncedVersion;
    private string? _lastServiceVersion;

    public TrayApplicationContext()
    {
        _lastSyncItem = new ToolStripMenuItem("Last sync: …")
        {
            Enabled = false
        };
        _syncNowItem = new ToolStripMenuItem("Sync Now", null, (_, _) => SyncNow());
        _checkUpdatesItem = new ToolStripMenuItem("Check for Updates", null, (_, _) => CheckForUpdates());
        _installUpdateItem = new ToolStripMenuItem("Install Update", null, (_, _) => InstallUpdate())
        {
            Visible = false,
            Enabled = false
        };
        _aboutItem = new ToolStripMenuItem("About", null, (_, _) => ShowAbout());

        ContextMenuStrip menu = new();
        menu.Items.Add(_lastSyncItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_syncNowItem);
        menu.Items.Add(_checkUpdatesItem);
        menu.Items.Add(_installUpdateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_aboutItem);
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

    private async void CheckForUpdates()
    {
        if (_checkInFlight)
        {
            return;
        }

        _checkInFlight = true;
        _checkUpdatesItem.Enabled = false;
        try
        {
            TrayStatusResponse? before = TrayFileIpcClient.ReadStatus();
            if (before is null || !TrayFileIpcClient.IsServiceAlive(before, ServiceAliveMaxAge))
            {
                ShowTip("AssetBee Drone", "Service is not running.", ToolTipIcon.Warning);
                SetUnavailable("Service is not running.");
                return;
            }

            if (string.Equals(before.UpdateState, "Downloading", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(before.UpdateState, "Installing", StringComparison.OrdinalIgnoreCase))
            {
                ShowTip("AssetBee Drone", "Update already in progress.", ToolTipIcon.Info);
                return;
            }

            DateTimeOffset? priorCheckUtc = before.LastUpdateCheckUtc;
            TrayFileIpcClient.RequestCheckUpdate();
            ShowTip("AssetBee Drone", "Checking for updates…", ToolTipIcon.Info);

            TrayStatusResponse? after = await WaitForUpdateCheckAsync(priorCheckUtc);
            RefreshStatus();

            string message = after?.LastUpdateCheckMessage
                ?? after?.Message
                ?? "Update check finished.";
            ToolTipIcon icon = after?.UpdateAvailable == true
                ? ToolTipIcon.Info
                : message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                  message.Contains("not configured", StringComparison.OrdinalIgnoreCase)
                    ? ToolTipIcon.Warning
                    : ToolTipIcon.Info;
            ShowTip("AssetBee Drone", message, icon);
        }
        catch (Exception exception)
        {
            ShowTip("AssetBee Drone", $"Update check failed: {exception.Message}", ToolTipIcon.Error);
        }
        finally
        {
            _checkInFlight = false;
            RefreshStatus();
        }
    }

    private static async Task<TrayStatusResponse?> WaitForUpdateCheckAsync(DateTimeOffset? priorCheckUtc)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CheckUpdateTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            TrayStatusResponse? status = TrayFileIpcClient.ReadStatus();
            if (status is null)
            {
                continue;
            }

            if (status.LastUpdateCheckUtc is not null &&
                (priorCheckUtc is null || status.LastUpdateCheckUtc > priorCheckUtc))
            {
                return status;
            }
        }

        return TrayFileIpcClient.ReadStatus();
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

    private void ShowAbout()
    {
        TrayStatusResponse? status = null;
        try
        {
            status = TrayFileIpcClient.ReadStatus();
            if (status is not null && TrayFileIpcClient.IsServiceAlive(status, ServiceAliveMaxAge))
            {
                _lastServiceVersion = status.ServiceVersion ?? _lastServiceVersion;
            }
        }
        catch
        {
            // About still works offline with the last known service version.
        }

        string trayVersion = GetTrayVersion();
        string serviceVersion = !string.IsNullOrWhiteSpace(_lastServiceVersion)
            ? _lastServiceVersion
            : status?.ServiceVersion is { Length: > 0 } version
                ? version
                : "Unavailable";

        string updateLine = status is not null &&
            TrayFileIpcClient.IsServiceAlive(status, ServiceAliveMaxAge) &&
            status.UpdateAvailable &&
            !string.IsNullOrWhiteSpace(status.UpdateVersion)
            ? $"{Environment.NewLine}Update available: {status.UpdateVersion}"
            : string.Empty;

        MessageBox.Show(
            $"AssetBee Drone{Environment.NewLine}{Environment.NewLine}" +
            $"Service version: {serviceVersion}{Environment.NewLine}" +
            $"Tray version: {trayVersion}{updateLine}",
            "About AssetBee Drone",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ApplyStatus(TrayStatusResponse status)
    {
        if (!string.IsNullOrWhiteSpace(status.ServiceVersion))
        {
            _lastServiceVersion = status.ServiceVersion;
        }

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
        _checkUpdatesItem.Enabled = !_checkInFlight && !updateBusy;
    }

    private void SetUnavailable(string detail)
    {
        _lastSyncItem.Text = string.IsNullOrWhiteSpace(detail)
            ? "Last sync: Unavailable"
            : TruncateTip($"Unavailable: {detail}");
        _notifyIcon.Text = TruncateTip("AssetBee Drone — unavailable");
        _syncNowItem.Enabled = !_syncInFlight;
        _checkUpdatesItem.Enabled = !_checkInFlight;
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

    private static string GetTrayVersion()
    {
        Assembly assembly = typeof(TrayApplicationContext).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            string core = informational;
            int plus = core.IndexOf('+');
            if (plus >= 0)
            {
                core = core[..plus];
            }

            return core;
        }

        Version? version = assembly.GetName().Version;
        return version?.ToString() ?? "Unknown";
    }

    private static string TruncateTip(string value) =>
        value.Length <= 63 ? value : value[..60] + "...";
}
