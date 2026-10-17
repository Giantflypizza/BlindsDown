using BlindsDown.Core.Locking;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// The full restore report after an unlock that was not exact: one line per device with its outcome,
/// and a shortcut to Windows Sound settings to fix anything by hand. Non-modal.
/// </summary>
internal sealed class RestoreDetailsForm : Form
{
    private readonly Font _font;

    public RestoreDetailsForm(RestoreReport report)
    {
        _font = Theme.BodyFont;

        SuspendLayout();
        DialogLayout.Initialize(this, "Restore report – BlindsDown", _font);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 300);

        TableLayoutPanel content = DialogLayout.Stack(new Padding(20, 16, 20, 8));
        DialogLayout.AddRow(content, DialogLayout.Text("Restore report", TextRole.Title, new Padding(0, 0, 0, 4)));
        DialogLayout.AddRow(content, DialogLayout.Text(report.Summary, margin: new Padding(0, 0, 0, 14)));

        var lines = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty,
            ColumnStyles = { new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.Percent, 100f) },
        };
        foreach (RestoreLine line in report.Lines)
        {
            string outcome = DialogText.Outcome(line.Outcome);
            lines.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lines.RowCount = lines.RowStyles.Count;
            var glyph = new SeverityGlyphControl(DialogText.OutcomeSeverity(line.Outcome))
            {
                Margin = new Padding(0, 1, 10, 0),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                AccessibleName = outcome,
            };
            TableLayoutPanel text = DialogLayout.Stack(Padding.Empty);
            text.Dock = DockStyle.Fill;
            text.Margin = new Padding(0, 0, 0, 12);
            DialogLayout.AddRow(text, DialogLayout.Text(line.DeviceName, TextRole.Strong, new Padding(0, 0, 0, 2)));
            DialogLayout.AddRow(text, DialogLayout.Text(outcome + ". " + line.Message, margin: Padding.Empty));
            lines.Controls.Add(glyph, 0, lines.RowCount - 1);
            lines.Controls.Add(text, 1, lines.RowCount - 1);
        }

        if (report.Lines.Count > 0)
        {
            DialogLayout.AddRow(content, lines);
        }
        else
        {
            lines.Dispose();
        }

        DialogLayout.AddRow(content, DialogLayout.Hint("Anything that is still not right can be changed in Windows Sound settings (Recording devices).", new Padding(0, 4, 0, 8)));

        Button sound = DialogLayout.Button("Open Sound settings");
        sound.Click += (_, _) => SettingsLinks.OpenSound();
        Button close = DialogLayout.Button("Close");
        close.Click += (_, _) => Close();
        AcceptButton = close;
        CancelButton = close;
        DialogLayout.FitHeightToContent(this, content, DialogLayout.CommandBar([sound], [close]));

        ResumeLayout(false);
        PerformLayout();
    }

    /// <summary>Opens a report window and brings it to the front (the user just asked for it).</summary>
    public static void ShowFor(RestoreReport report)
    {
        var form = new RestoreDetailsForm(report);
        form.Show();
        form.Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
        }

        base.Dispose(disposing);
    }
}
