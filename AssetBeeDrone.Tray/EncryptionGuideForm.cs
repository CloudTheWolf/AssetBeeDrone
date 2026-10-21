using System.Diagnostics;

namespace AssetBeeDrone.Tray;

internal sealed class EncryptionGuideForm : Form
{
    private static readonly TimeSpan ServiceAliveMaxAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(45);
    private static EncryptionGuideForm? _openInstance;

    private readonly Label _statusLabel;
    private readonly Button _refreshButton;
    private CancellationTokenSource? _refreshCts;
    private bool _refreshInFlight;

    private EncryptionGuideForm()
    {
        Text = "AssetBee · Enable encryption";
        TrayUi.ApplyFormChrome(this, new Size(560, 620));

        Label eyebrow = new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "DISK ENCRYPTION",
            Font = TrayUi.UiFont(8f, FontStyle.Bold),
            ForeColor = TrayUi.Accent,
            TextAlign = ContentAlignment.BottomLeft
        };

        Label heading = TrayUi.CreateTitle("Turn on BitLocker");
        Label intro = TrayUi.CreateMutedBody(
            "AssetBee needs BitLocker with a recovery key so this device can be restored if Windows cannot unlock the drive.");

        Label statusHeading = new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "Current status",
            Font = TrayUi.UiFont(9f, FontStyle.Bold),
            ForeColor = TrayUi.TextPrimary,
            TextAlign = ContentAlignment.BottomLeft
        };

        _statusLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            Text = "Waiting for AssetBee service…",
            Font = TrayUi.UiFont(9.25f),
            ForeColor = TrayUi.TextPrimary,
            BackColor = TrayUi.AccentSoft,
            TextAlign = ContentAlignment.TopLeft,
            UseMnemonic = false
        };
        Panel statusCard = TrayUi.CreateCard(_statusLabel, new Padding(0));
        statusCard.BackColor = TrayUi.AccentSoft;

        Panel guide = TrayUi.CreateScrollableGuide(BuildGuideText());
        Panel guideCard = TrayUi.CreateCard(guide, new Padding(12));

        _refreshButton = TrayUi.CreateSecondaryButton("Refresh status");
        _refreshButton.AutoSize = true;
        _refreshButton.Width = 130;
        _refreshButton.Click += async (_, _) => await RefreshStatusAsync();

        Button openSettings = TrayUi.CreateSecondaryButton("Open BitLocker settings");
        openSettings.AutoSize = true;
        openSettings.Width = 180;
        openSettings.Click += (_, _) => OpenBitLockerSettings();

        Button close = TrayUi.CreatePrimaryButton("Close");
        close.Width = 100;
        close.Click += (_, _) => Close();
        AcceptButton = close;
        CancelButton = close;

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(openSettings);
        buttons.Controls.Add(_refreshButton);

        TableLayoutPanel content = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(4)
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.Controls.Add(eyebrow, 0, 0);
        content.Controls.Add(heading, 0, 1);
        content.Controls.Add(intro, 0, 2);
        content.Controls.Add(statusHeading, 0, 3);
        content.Controls.Add(statusCard, 0, 4);
        content.Controls.Add(guideCard, 0, 5);
        content.Controls.Add(buttons, 0, 6);

        Panel shell = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            BackColor = TrayUi.Canvas
        };
        shell.Controls.Add(content);
        Controls.Add(shell);

        Shown += async (_, _) => await RefreshStatusAsync();
        FormClosed += (_, _) =>
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;
        };
    }

    public static void ShowGuide()
    {
        if (_openInstance is { IsDisposed: false })
        {
            if (_openInstance.WindowState == FormWindowState.Minimized)
            {
                _openInstance.WindowState = FormWindowState.Normal;
            }

            _openInstance.Activate();
            _ = _openInstance.RefreshStatusAsync();
            return;
        }

        EncryptionGuideForm form = new();
        _openInstance = form;
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_openInstance, form))
            {
                _openInstance = null;
            }
        };
        form.Show();
    }

    private async Task RefreshStatusAsync()
    {
        if (IsDisposed || _refreshInFlight)
        {
            return;
        }

        _refreshInFlight = true;
        _refreshButton.Enabled = false;

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource(RefreshTimeout);
        CancellationToken cancellationToken = _refreshCts.Token;

        try
        {
            TrayStatusResponse? before = TrayFileIpcClient.ReadStatus();
            bool alive = before is not null &&
                         TrayFileIpcClient.IsServiceAlive(before, ServiceAliveMaxAge);
            ApplyStatus(before, alive);

            if (!alive)
            {
                return;
            }

            DateTimeOffset? priorUtc = before?.BitLockerStatusUtc;
            bool priorRefreshing = before?.BitLockerRefreshing == true;
            TrayFileIpcClient.RequestBitLockerStatus();
            _statusLabel.Text = "Reading BitLocker status from the AssetBee service…";

            TrayStatusResponse? after = await WaitForBitLockerUpdateAsync(priorUtc, priorRefreshing, cancellationToken);
            bool afterAlive = after is not null &&
                              TrayFileIpcClient.IsServiceAlive(after, ServiceAliveMaxAge);
            ApplyStatus(after ?? TrayFileIpcClient.ReadStatus(), afterAlive);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
            {
                TrayStatusResponse? status = TrayFileIpcClient.ReadStatus();
                bool alive = status is not null &&
                             TrayFileIpcClient.IsServiceAlive(status, ServiceAliveMaxAge);
                if (alive && status?.BitLockerVolumes is { Count: > 0 })
                {
                    ApplyStatus(status, alive);
                }
                else
                {
                    _statusLabel.Text = "Timed out waiting for BitLocker status from the AssetBee service.";
                }
            }
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                _statusLabel.Text = "Could not refresh BitLocker status." + Environment.NewLine + exception.Message;
            }
        }
        finally
        {
            _refreshInFlight = false;
            if (!IsDisposed)
            {
                _refreshButton.Enabled = true;
            }
        }
    }

    private void ApplyStatus(TrayStatusResponse? status, bool serviceAlive)
    {
        if (!IsDisposed)
        {
            _statusLabel.Text = BitLockerStatusDisplay.Format(status, serviceAlive);
        }
    }

    private static async Task<TrayStatusResponse?> WaitForBitLockerUpdateAsync(
        DateTimeOffset? priorUtc,
        bool priorRefreshing,
        CancellationToken cancellationToken)
    {
        bool sawRefreshing = priorRefreshing;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(250, cancellationToken);
            TrayStatusResponse? status = TrayFileIpcClient.ReadStatus();
            if (status is null || !TrayFileIpcClient.IsServiceAlive(status, ServiceAliveMaxAge))
            {
                continue;
            }

            if (status.BitLockerRefreshing)
            {
                sawRefreshing = true;
                continue;
            }

            if (status.BitLockerStatusUtc is null)
            {
                continue;
            }

            if (priorUtc is null || status.BitLockerStatusUtc > priorUtc || sawRefreshing)
            {
                return status;
            }
        }

        return TrayFileIpcClient.ReadStatus();
    }

    private static string BuildGuideText() =>
        """
        1. Select "Open BitLocker settings" below (or search Windows for "Manage BitLocker").

        2. Find your system drive (usually C:) and choose "Turn on BitLocker". You may need to sign in as an administrator.

        3. If Windows asks how to unlock the drive at startup, keep the TPM / "this PC" option. Do not skip creating a recovery key.

        4. When asked how to back up your recovery key, choose any option. AssetBee will also collect the numerical recovery key on the next sync after encryption is on.

        5. Choose "Encrypt entire drive" (full encryption), not only used space.

        6. If offered, prefer "New encryption mode" on modern Windows PCs, or "Compatible mode" if you move the drive between older systems.

        7. Confirm and start encryption. Keep the PC powered on until it finishes.

        8. After BitLocker shows the drive as encrypted, use AssetBee Drone → Sync Now so the recovery key is sent to AssetBee.

        If AssetBee later shows "TPM Only (No Key)", open BitLocker settings, add a recovery password protector, then Sync Now again.

        Device Encryption (Settings → Privacy & security → Device encryption) on some PCs is similar; turn it on, then confirm a recovery key exists under Manage BitLocker.

        Status above is read by the AssetBee Drone service (LocalSystem), not by this tray app.
        """.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

    private static void OpenBitLockerSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "control.exe",
                Arguments = "/name Microsoft.BitLockerDriveEncryption",
                UseShellExecute = true
            });
            return;
        }
        catch
        {
            // Fall through to Settings URI.
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:deviceencryption",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Could not open BitLocker settings automatically." +
                Environment.NewLine + Environment.NewLine +
                "Search Windows for \"Manage BitLocker\" or open" +
                Environment.NewLine +
                "Settings → Privacy & security → Device encryption." +
                Environment.NewLine + Environment.NewLine +
                exception.Message,
                "AssetBee Drone",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
