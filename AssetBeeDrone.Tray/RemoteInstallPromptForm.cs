using System.Globalization;

namespace AssetBeeDrone.Tray;

internal sealed class RemoteInstallPromptForm : Form
{
    private readonly DateTimeOffset? _deadline;
    private readonly DateTimePicker _schedulePicker;
    private readonly Label _deadlineLabel;

    public string Decision { get; private set; } = "dismiss";
    public DateTimeOffset? ScheduledFor { get; private set; }

    public RemoteInstallPromptForm(string packageName, DateTimeOffset? deadline, DateTimeOffset? scheduledFor)
    {
        _deadline = deadline;
        Text = "AssetBee remote install";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 260);
        ShowInTaskbar = true;

        Label message = new()
        {
            AutoSize = false,
            Location = new Point(16, 16),
            Size = new Size(428, 72),
            Text =
                $"IT is installing \"{packageName}\" on this PC via AssetBee Drone." +
                Environment.NewLine +
                "You can install now, postpone, or schedule a time (maximum 24 hours from first notice)."
        };

        _deadlineLabel = new Label
        {
            AutoSize = false,
            Location = new Point(16, 96),
            Size = new Size(428, 24),
            Text = deadline is null
                ? "Deadline: pending first notice"
                : $"Must run by {deadline.Value.ToLocalTime():g}"
        };

        Label scheduleLabel = new()
        {
            AutoSize = true,
            Location = new Point(16, 132),
            Text = "Run at:"
        };

        _schedulePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern + " HH:mm",
            ShowUpDown = true,
            Location = new Point(80, 128),
            Width = 220,
            MinDate = DateTime.Now.AddMinutes(-1),
            Value = scheduledFor?.ToLocalTime().DateTime
                    ?? DateTime.Now.AddHours(1)
        };
        if (deadline is not null)
        {
            DateTime max = deadline.Value.ToLocalTime().DateTime;
            if (max > _schedulePicker.MinDate)
            {
                _schedulePicker.MaxDate = max;
            }
        }

        Button installNow = new()
        {
            Text = "Install now",
            Location = new Point(16, 200),
            Size = new Size(120, 32)
        };
        installNow.Click += (_, _) =>
        {
            Decision = "install_now";
            DialogResult = DialogResult.OK;
            Close();
        };

        Button postpone = new()
        {
            Text = "Postpone",
            Location = new Point(148, 200),
            Size = new Size(120, 32)
        };
        postpone.Click += (_, _) => AcceptSchedule("postpone");

        Button schedule = new()
        {
            Text = "Schedule",
            Location = new Point(280, 200),
            Size = new Size(120, 32)
        };
        schedule.Click += (_, _) => AcceptSchedule("schedule");

        Controls.Add(message);
        Controls.Add(_deadlineLabel);
        Controls.Add(scheduleLabel);
        Controls.Add(_schedulePicker);
        Controls.Add(installNow);
        Controls.Add(postpone);
        Controls.Add(schedule);
    }

    private void AcceptSchedule(string action)
    {
        DateTimeOffset selected = new(_schedulePicker.Value);
        if (_deadline is not null && selected > _deadline)
        {
            MessageBox.Show(
                this,
                "That time is after the 24-hour postpone deadline.",
                "AssetBee",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        Decision = action;
        ScheduledFor = selected.ToUniversalTime();
        DialogResult = DialogResult.OK;
        Close();
    }
}
