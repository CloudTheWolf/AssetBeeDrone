using System.Diagnostics;

namespace AssetBeeDrone.Tray;

internal sealed class EncryptionGuideForm : Form
{
    private static EncryptionGuideForm? _openInstance;

    private EncryptionGuideForm()
    {
        Text = "Enable full disk encryption";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        ClientSize = new Size(520, 460);
        Font = SystemFonts.MessageBoxFont;
        Padding = new Padding(16);

        Label heading = new()
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 36,
            Text = "BitLocker step-by-step",
            Font = new Font(Font.FontFamily, Font.Size + 2f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };

        TextBox guide = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            Text = BuildGuideText(),
            TabStop = false
        };

        Button openSettings = new()
        {
            Text = "Open BitLocker settings",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        openSettings.Click += (_, _) => OpenBitLockerSettings();

        Button close = new()
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Anchor = AnchorStyles.Right
        };
        AcceptButton = close;
        CancelButton = close;

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(0, 12, 0, 0),
            WrapContents = false
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(openSettings);

        // Docking applies in reverse z-order: fill first, then edges.
        Controls.Add(guide);
        Controls.Add(buttons);
        Controls.Add(heading);
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

    private static string BuildGuideText() =>
        """
        AssetBee needs BitLocker turned on with a recovery key so your device
        can be restored if Windows cannot unlock the drive.

        1. Select "Open BitLocker settings" below (or search Windows for
           "Manage BitLocker").

        2. Find your system drive (usually C:) and choose "Turn on BitLocker".
           You may need to sign in as an administrator.

        3. If Windows asks how to unlock the drive at startup, keep the TPM /
           "this PC" option. Do not skip creating a recovery key.

        4. When asked how to back up your recovery key, choose any option
           (Microsoft account, USB, file, or print). AssetBee will also collect
           the numerical recovery key on the next sync after encryption is on.

        5. Choose "Encrypt entire drive" (full encryption), not only used space.
           This protects deleted files and free space as well.

        6. If offered, prefer "New encryption mode" on modern Windows PCs, or
           "Compatible mode" if you move the drive between older systems.

        7. Confirm and start encryption. Keep the PC powered on until it finishes.
           Encryption can take a while on large disks.

        8. After BitLocker shows the drive as encrypted, use AssetBee Drone →
           Sync Now so the recovery key is sent to AssetBee.

        If AssetBee later shows "TPM Only (No Key)", protection is on but no
        numerical recovery key was found. Open BitLocker settings, select
        "Back up your recovery key" / "Manage BitLocker", and add a recovery
        password protector, then Sync Now again.

        Device Encryption (Settings → Privacy & security → Device encryption)
        on some PCs is similar; turn it on, then still confirm a recovery key
        exists under Manage BitLocker.
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
