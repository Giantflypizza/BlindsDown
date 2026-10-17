using BlindsDown.Infrastructure;

namespace BlindsDown.Ui.Forms;

/// <summary>Text styles. Everything except <see cref="Body"/> derives its font from the window font (see <see cref="DerivedFont"/>).</summary>
internal enum TextRole
{
    Body,
    Strong,
    Heading,
    Title,
}

/// <summary>
/// Building blocks for the code-built windows, so they share one look and survive 100–200 % DPI and long
/// text. Everything is written in logical (96 DPI) units; <see cref="AutoScaleMode.Dpi"/> scales sizes,
/// margins and paddings, and labels wrap because they sit in a percent-width table column with
/// AutoSize + Dock.Fill (TableLayoutPanel then measures them at the column width). Child controls never
/// get a fixed font of their own: WinForms would not rescale it when the window changes monitor.
/// Layout: a white content area on top and a grey command bar with the buttons below, like Windows' own dialogs.
/// </summary>
internal static class DialogLayout
{
    /// <summary>Standard setup; call first in a form's constructor, between SuspendLayout and ResumeLayout.</summary>
    public static void Initialize(Form form, string title, Font font)
    {
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = font;
        form.Text = title;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.BackColor = SystemColors.Window;
        form.ForeColor = SystemColors.WindowText;
        Icon? icon = AppIcon.Value;
        if (icon is null)
        {
            form.ShowIcon = false;
        }
        else
        {
            form.Icon = icon;
        }
    }

    /// <summary>A one-column table that stacks rows top to bottom and grows with its content.</summary>
    public static TableLayoutPanel Stack(Padding padding) =>
        new()
        {
            ColumnCount = 1,
            RowCount = 0,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = padding,
            Margin = Padding.Empty,
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 100f) },
        };

    /// <summary>Adds <paramref name="control"/> as a new auto-sized row.</summary>
    public static T AddRow<T>(TableLayoutPanel table, T control)
        where T : Control
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowCount = table.RowStyles.Count;
        table.Controls.Add(control, 0, table.RowCount - 1);
        return control;
    }

    /// <summary>A label that wraps to the width of its table column.</summary>
    public static Label Text(string text, TextRole role = TextRole.Body, Padding? margin = null)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Dock = DockStyle.Fill,
            UseMnemonic = false,
            Margin = margin ?? new Padding(0, 0, 0, 8),
        };
        return role switch
        {
            TextRole.Strong => DerivedFont.Follow(label, FontStyle.Bold, 1f),
            TextRole.Heading => DerivedFont.Follow(label, FontStyle.Bold, 1.08f),

            // Matches Theme.TitleFont (message font + 2.5 pt) for the usual 9 pt message font.
            TextRole.Title => DerivedFont.Follow(label, FontStyle.Bold, 1.28f),
            _ => label,
        };
    }

    /// <summary>Secondary text (explanations under an option).</summary>
    public static Label Hint(string text, Padding? margin = null)
    {
        Label label = Text(text, margin: margin ?? new Padding(0, 0, 0, 8));
        label.ForeColor = SystemColors.GrayText;
        return label;
    }

    /// <summary>A section heading inside a tab or dialog.</summary>
    public static Label Heading(string text, bool first = false) =>
        Text(text, TextRole.Heading, new Padding(0, first ? 0 : 12, 0, 6));

    /// <summary>
    /// Controls side by side on one line, vertically centred (a label in front of a box, for example).
    /// A table rather than a flow panel, because a table re-measures the row when a font grows with DPI.
    /// </summary>
    public static TableLayoutPanel Inline(Padding margin, params Control[] controls)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            ColumnCount = controls.Length,
            RowCount = 1,
            Margin = margin,
            Padding = Padding.Empty,
            RowStyles = { new RowStyle(SizeType.AutoSize) },
        };
        for (int i = 0; i < controls.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            controls[i].Anchor = AnchorStyles.Left;
            row.Controls.Add(controls[i], i, 0);
        }

        return row;
    }

    /// <summary>A plain one-line label for use inside <see cref="Inline"/>.</summary>
    public static Label Caption(string text, Padding margin) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, Margin = margin };

    public static CheckBox Check(string text, bool value) =>
        new()
        {
            Text = text,
            Checked = value,
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 2, 0, 4),
        };

    public static Button Button(string text, DialogResult result = DialogResult.None) =>
        new()
        {
            Text = text,
            DialogResult = result,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(88, 28),
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(8, 0, 0, 0),
            UseVisualStyleBackColor = true,
        };

    /// <summary>A bulleted list whose items wrap with a hanging indent.</summary>
    public static TableLayoutPanel Bullets(IEnumerable<string> items)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 8),
            ColumnStyles = { new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.Percent, 100f) },
        };
        foreach (string item in items)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowCount = table.RowStyles.Count;
            table.Controls.Add(new Label { Text = "•", AutoSize = true, Margin = new Padding(4, 0, 6, 4) }, 0, table.RowCount - 1);
            table.Controls.Add(Text(item, margin: new Padding(0, 0, 0, 4)), 1, table.RowCount - 1);
        }

        return table;
    }

    /// <summary>A tinted notice with a status badge, e.g. why a list is read-only right now.</summary>
    public static TableLayoutPanel Banner(string text, AlertSeverity severity)
    {
        var banner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiPalette.Blend(SystemColors.Window, UiPalette.SeverityColor(severity), 0.14f),
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
            ColumnStyles = { new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.Percent, 100f) },
            RowStyles = { new RowStyle(SizeType.AutoSize) },
        };
        banner.Controls.Add(new SeverityGlyphControl(severity) { Margin = new Padding(0, 0, 8, 0), Anchor = AnchorStyles.Top | AnchorStyles.Left }, 0, 0);
        banner.Controls.Add(Text(text, margin: Padding.Empty), 1, 0);
        banner.AccessibleName = text;
        return banner;
    }

    /// <summary>The grey strip with the dialog buttons. Pass the buttons in visual order (left to right).</summary>
    public static TableLayoutPanel CommandBar(IReadOnlyList<Control> left, IReadOnlyList<Button> right)
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = SystemColors.Control,
            Padding = new Padding(16, 12, 16, 12),
            Margin = Padding.Empty,
            ColumnCount = left.Count + right.Count + 1,
            RowCount = 1,
            RowStyles = { new RowStyle(SizeType.AutoSize) },
        };

        foreach (Control control in left)
        {
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            control.Margin = new Padding(0, 0, 8, 0);
            control.Anchor = AnchorStyles.Left;
            bar.Controls.Add(control);
        }

        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bar.Controls.Add(new Label { AutoSize = true, Margin = Padding.Empty }, left.Count, 0);
        for (int i = 0; i < right.Count; i++)
        {
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            right[i].Anchor = AnchorStyles.Right;
            bar.Controls.Add(right[i], left.Count + 1 + i, 0);
        }

        return bar;
    }

    /// <summary>
    /// The outer table of a fixed-width dialog: content on top, command bar below. The form's height
    /// follows the content (re-measured after DPI changes), so long text never clips.
    /// </summary>
    public static TableLayoutPanel FitHeightToContent(Form form, Control content, Control commandBar)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 100f) },
        };
        AddRow(root, content);
        AddRow(root, commandBar);
        form.Controls.Add(root);
        root.SizeChanged += (_, _) =>
        {
            if (root.Height <= 0)
            {
                return;
            }

            // Very long content (many devices in a restore report) scrolls instead of growing off-screen.
            int frame = form.Height - form.ClientSize.Height;
            int maxHeight = (Screen.FromControl(form).WorkingArea.Height * 9 / 10) - frame;
            int height = Math.Min(root.Height, maxHeight);
            form.AutoScroll = root.Height > height;
            if (form.ClientSize.Height != height)
            {
                form.ClientSize = new Size(form.ClientSize.Width, height);
            }
        };
        return root;
    }

    /// <summary>The app's own icon for title bars and the taskbar, read once from the running exe.</summary>
    private static class AppIcon
    {
        public static readonly Icon? Value = Load();

        private static Icon? Load()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                return exe is null ? null : Icon.ExtractAssociatedIcon(exe);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Log.Warn("Could not read the app icon: " + ex.Message);
                return null;
            }
        }
    }
}
