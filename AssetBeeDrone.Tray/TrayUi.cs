using Microsoft.Win32;

namespace AssetBeeDrone.Tray;

/// <summary>
/// Shared visual language for tray dialogs and menus (WinForms).
/// Colors follow the Windows app light/dark preference.
/// </summary>
internal static class TrayUi
{
    public static bool IsDarkMode => ReadWindowsAppsDarkMode();

    public static Color Canvas => IsDarkMode
        ? Color.FromArgb(11, 15, 20)
        : Color.FromArgb(247, 248, 250);

    public static Color Surface => IsDarkMode
        ? Color.FromArgb(30, 36, 48)
        : Color.White;

    public static Color Border => IsDarkMode
        ? Color.FromArgb(51, 65, 85)
        : Color.FromArgb(226, 232, 240);

    public static Color TextPrimary => IsDarkMode
        ? Color.FromArgb(248, 250, 252)
        : Color.FromArgb(15, 23, 42);

    public static Color TextMuted => IsDarkMode
        ? Color.FromArgb(148, 163, 184)
        : Color.FromArgb(100, 116, 139);

    public static Color Accent => IsDarkMode
        ? Color.FromArgb(45, 212, 191)
        : Color.FromArgb(15, 118, 110);

    public static Color AccentHover => IsDarkMode
        ? Color.FromArgb(94, 234, 212)
        : Color.FromArgb(13, 148, 136);

    public static Color AccentSoft => IsDarkMode
        ? Color.FromArgb(19, 78, 74)
        : Color.FromArgb(240, 253, 250);

    public static Color WarningSoft => IsDarkMode
        ? Color.FromArgb(66, 32, 6)
        : Color.FromArgb(255, 251, 235);

    public static Color WarningBorder => IsDarkMode
        ? Color.FromArgb(133, 77, 14)
        : Color.FromArgb(253, 230, 138);

    public static Color WarningText => IsDarkMode
        ? Color.FromArgb(253, 230, 138)
        : Color.FromArgb(146, 64, 14);

    public static Color SecondaryFill => IsDarkMode
        ? Color.FromArgb(51, 65, 85)
        : Color.FromArgb(241, 245, 249);

    public static Color SecondaryHover => IsDarkMode
        ? Color.FromArgb(71, 85, 105)
        : Color.FromArgb(226, 232, 240);

    public static Color PrimaryButtonText => IsDarkMode
        ? Color.FromArgb(15, 23, 42)
        : Color.White;

    public static Font UiFont(float size = 9.75f, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style, GraphicsUnit.Point);

    public static void ApplyFormChrome(Form form, Size clientSize)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.BackColor = Canvas;
        form.Font = UiFont();
        form.ForeColor = TextPrimary;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.ShowInTaskbar = true;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.ClientSize = clientSize;
        form.Padding = new Padding(0);
    }

    public static Panel CreateCard(Control content, Padding? padding = null)
    {
        Panel card = new()
        {
            BackColor = Surface,
            Dock = DockStyle.Fill,
            Padding = padding ?? new Padding(16),
            Margin = new Padding(0)
        };
        card.Paint += (_, e) =>
        {
            using Pen pen = new(Border);
            Rectangle bounds = card.ClientRectangle;
            bounds.Width -= 1;
            bounds.Height -= 1;
            e.Graphics.DrawRectangle(pen, bounds);
        };
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        return card;
    }

    public static Button CreatePrimaryButton(string text)
    {
        Button button = CreateBaseButton(text);
        button.BackColor = Accent;
        button.ForeColor = PrimaryButtonText;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.FlatAppearance.MouseDownBackColor = Accent;
        return button;
    }

    public static Button CreateSecondaryButton(string text)
    {
        Button button = CreateBaseButton(text);
        button.BackColor = SecondaryFill;
        button.ForeColor = TextPrimary;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = SecondaryHover;
        button.FlatAppearance.MouseDownBackColor = SecondaryFill;
        return button;
    }

    public static Button CreateGhostButton(string text)
    {
        Button button = CreateBaseButton(text);
        button.BackColor = Canvas;
        button.ForeColor = TextMuted;
        button.FlatAppearance.MouseOverBackColor = SecondaryFill;
        button.FlatAppearance.MouseDownBackColor = SecondaryFill;
        return button;
    }

    public static Label CreateTitle(string text) =>
        new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = text,
            Font = UiFont(14f, FontStyle.Bold),
            ForeColor = TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false
        };

    public static Label CreateMutedBody(string text) =>
        new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = text,
            Font = UiFont(9.25f),
            ForeColor = TextMuted,
            TextAlign = ContentAlignment.TopLeft,
            UseMnemonic = false
        };

    /// <summary>
    /// Scrollable guide text that cannot be focus-selected (unlike a ReadOnly TextBox).
    /// </summary>
    public static Panel CreateScrollableGuide(string text)
    {
        Panel host = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Surface,
            Padding = new Padding(0)
        };

        Label guide = new()
        {
            AutoSize = true,
            BackColor = Surface,
            ForeColor = TextPrimary,
            Font = UiFont(9.25f),
            Text = text,
            UseMnemonic = false,
            MaximumSize = new Size(400, 0)
        };

        void SyncGuideWidth()
        {
            int gutter = SystemInformation.VerticalScrollBarWidth + 12;
            int width = Math.Max(120, host.ClientSize.Width - gutter);
            guide.MaximumSize = new Size(width, 0);
        }

        host.Controls.Add(guide);
        host.Resize += (_, _) => SyncGuideWidth();
        SyncGuideWidth();
        return host;
    }

    public static void StyleContextMenu(ContextMenuStrip menu)
    {
        menu.Font = UiFont(9.5f);
        menu.BackColor = Surface;
        menu.ForeColor = TextPrimary;
        menu.ShowImageMargin = false;
        menu.RenderMode = ToolStripRenderMode.Professional;
        menu.Renderer = new TrayMenuRenderer();
    }

    private static bool ReadWindowsAppsDarkMode()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            if (value is int appsUseLightTheme)
            {
                return appsUseLightTheme == 0;
            }
        }
        catch (Exception)
        {
            // Fall back to light when the preference cannot be read.
        }

        return false;
    }

    private static Button CreateBaseButton(string text) =>
        new()
        {
            Text = text,
            AutoSize = false,
            Height = 36,
            MinimumSize = new Size(112, 36),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Cursor = Cursors.Hand,
            Font = UiFont(9.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(14, 0, 14, 0),
            UseVisualStyleBackColor = false
        };

    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer()
            : base(new TrayColorTable())
        {
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled)
            {
                e.TextColor = TextMuted;
            }

            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using Pen pen = new(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }

    private sealed class TrayColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => Surface;
        public override Color MenuStripGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => AccentSoft;
        public override Color MenuItemSelected => AccentSoft;
        public override Color MenuItemSelectedGradientBegin => AccentSoft;
        public override Color MenuItemSelectedGradientEnd => AccentSoft;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
        public override Color ToolStripDropDownBackground => Surface;
    }
}
