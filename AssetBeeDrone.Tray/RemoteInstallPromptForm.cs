namespace AssetBeeDrone.Tray;

internal sealed class RemoteInstallPromptForm : Form
{
    private readonly ComboBox _delayCombo;

    public string Decision { get; private set; } = "dismiss";
    public DateTimeOffset? ScheduledFor { get; private set; }

    public RemoteInstallPromptForm(
        string packageName,
        DateTimeOffset? deadline,
        DateTimeOffset? scheduledFor,
        DateTimeOffset? autoInstallAt = null)
    {
        Text = "AssetBee · Remote install";
        TrayUi.ApplyFormChrome(this, new Size(420, 380));
        TopMost = true;

        Label eyebrow = new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "REMOTE INSTALL",
            Font = TrayUi.UiFont(8f, FontStyle.Bold),
            ForeColor = TrayUi.Accent,
            TextAlign = ContentAlignment.BottomLeft,
            UseMnemonic = false
        };

        Label title = TrayUi.CreateTitle(packageName);
        Label body = TrayUi.CreateMutedBody(
            "IT is installing this package via AssetBee Drone. Choose when to run it. " +
            "If you do not respond within 15 minutes, it starts automatically.");

        Panel calloutInner = new()
        {
            Dock = DockStyle.Fill,
            BackColor = TrayUi.WarningSoft,
            Padding = new Padding(12, 10, 12, 10)
        };
        calloutInner.Paint += (_, e) =>
        {
            using Pen pen = new(TrayUi.WarningBorder);
            Rectangle bounds = calloutInner.ClientRectangle;
            bounds.Width -= 1;
            bounds.Height -= 1;
            e.Graphics.DrawRectangle(pen, bounds);
        };
        Label callout = new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = BuildDeadlineText(deadline, scheduledFor, autoInstallAt),
            Font = TrayUi.UiFont(9f),
            ForeColor = TrayUi.WarningText,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };
        calloutInner.Controls.Add(callout);

        Label delayLabel = new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "When to install",
            Font = TrayUi.UiFont(9f, FontStyle.Bold),
            ForeColor = TrayUi.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _delayCombo = new ComboBox
        {
            Dock = DockStyle.Top,
            Height = 32,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = TrayUi.UiFont(10f),
            FlatStyle = FlatStyle.Flat,
            BackColor = TrayUi.Surface,
            ForeColor = TrayUi.TextPrimary
        };
        foreach (DelayOption option in BuildDelayOptions(deadline))
        {
            _delayCombo.Items.Add(option);
        }

        if (_delayCombo.Items.Count > 0)
        {
            _delayCombo.SelectedIndex = 0;
        }

        Button confirm = TrayUi.CreatePrimaryButton("Confirm");
        confirm.Width = 120;
        confirm.Height = 40;
        confirm.Margin = new Padding(0);
        confirm.Click += (_, _) => AcceptSelection();

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0)
        };
        actions.Controls.Add(confirm);

        TableLayoutPanel content = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(4)
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        content.Controls.Add(eyebrow, 0, 0);
        content.Controls.Add(title, 0, 1);
        content.Controls.Add(body, 0, 2);
        content.Controls.Add(calloutInner, 0, 3);
        content.Controls.Add(delayLabel, 0, 4);
        content.Controls.Add(_delayCombo, 0, 5);
        content.Controls.Add(actions, 0, 6);

        Panel card = TrayUi.CreateCard(content, new Padding(20));
        Panel shell = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            BackColor = TrayUi.Canvas
        };
        shell.Controls.Add(card);
        Controls.Add(shell);

        AcceptButton = confirm;
    }

    private static IReadOnlyList<DelayOption> BuildDelayOptions(DateTimeOffset? deadline)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        DelayOption[] candidates =
        [
            new("Install now", null),
            new("In 1 hour", TimeSpan.FromHours(1)),
            new("In 4 hours", TimeSpan.FromHours(4)),
            new("In 8 hours", TimeSpan.FromHours(8)),
            new("In 24 hours", TimeSpan.FromHours(24)),
        ];

        List<DelayOption> options = [];
        foreach (DelayOption candidate in candidates)
        {
            if (candidate.Delay is null)
            {
                options.Add(candidate);
                continue;
            }

            DateTimeOffset runAt = now.Add(candidate.Delay.Value);
            if (deadline is not null && runAt > deadline.Value.AddMinutes(1))
            {
                continue;
            }

            options.Add(candidate);
        }

        return options;
    }

    private static string BuildDeadlineText(
        DateTimeOffset? deadline,
        DateTimeOffset? scheduledFor,
        DateTimeOffset? autoInstallAt)
    {
        if (scheduledFor is not null)
        {
            return $"Currently scheduled for {scheduledFor.Value.ToLocalTime():g}";
        }

        if (autoInstallAt is not null)
        {
            return $"Auto-installs at {autoInstallAt.Value.ToLocalTime():g} if you do not respond";
        }

        if (deadline is not null)
        {
            return $"Must run by {deadline.Value.ToLocalTime():g}";
        }

        return "Choose when this install should run";
    }

    private void AcceptSelection()
    {
        if (_delayCombo.SelectedItem is not DelayOption option)
        {
            return;
        }

        if (option.Delay is null)
        {
            Decision = "install_now";
            ScheduledFor = null;
        }
        else
        {
            Decision = "postpone";
            ScheduledFor = DateTimeOffset.UtcNow.Add(option.Delay.Value);
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record DelayOption(string Label, TimeSpan? Delay)
    {
        public override string ToString() => Label;
    }
}
