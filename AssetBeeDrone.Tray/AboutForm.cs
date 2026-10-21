namespace AssetBeeDrone.Tray;

internal sealed class AboutForm : Form
{
    public AboutForm(string serviceVersion, string trayVersion, string? updateVersion)
    {
        Text = "About AssetBee Drone";
        TrayUi.ApplyFormChrome(this, new Size(400, updateVersion is null ? 280 : 310));

        Label eyebrow = new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "ASSETBEE",
            Font = TrayUi.UiFont(8f, FontStyle.Bold),
            ForeColor = TrayUi.Accent,
            TextAlign = ContentAlignment.BottomLeft
        };

        Label title = TrayUi.CreateTitle("Drone");
        Label subtitle = TrayUi.CreateMutedBody(
            "Companion tray for sync status, updates, encryption guidance, and remote installs.");

        TableLayoutPanel versions = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = updateVersion is null ? 2 : 3,
            Padding = new Padding(0)
        };
        versions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        versions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        versions.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        versions.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        if (updateVersion is not null)
        {
            versions.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        }

        AddVersionRow(versions, 0, "Service", serviceVersion);
        AddVersionRow(versions, 1, "Tray", trayVersion);
        if (updateVersion is not null)
        {
            AddVersionRow(versions, 2, "Update", updateVersion);
        }

        Panel versionCard = TrayUi.CreateCard(versions, new Padding(14, 10, 14, 10));

        Button close = TrayUi.CreatePrimaryButton("Close");
        close.Dock = DockStyle.Right;
        close.Width = 100;
        close.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        AcceptButton = close;
        CancelButton = close;

        Panel footer = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 12, 0, 0)
        };
        footer.Controls.Add(close);

        TableLayoutPanel content = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(4)
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        content.Controls.Add(eyebrow, 0, 0);
        content.Controls.Add(title, 0, 1);
        content.Controls.Add(subtitle, 0, 2);
        content.Controls.Add(versionCard, 0, 3);
        content.Controls.Add(footer, 0, 4);

        Panel shell = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            BackColor = TrayUi.Canvas
        };
        shell.Controls.Add(content);
        Controls.Add(shell);
    }

    private static void AddVersionRow(TableLayoutPanel table, int row, string label, string value)
    {
        table.Controls.Add(new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = label,
            Font = TrayUi.UiFont(9f),
            ForeColor = TrayUi.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        table.Controls.Add(new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = value,
            Font = TrayUi.UiFont(9.5f, FontStyle.Bold),
            ForeColor = TrayUi.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        }, 1, row);
    }
}
