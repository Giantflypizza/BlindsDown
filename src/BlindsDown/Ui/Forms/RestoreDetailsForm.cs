using BlindsDown.Core.Locking;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// The full restore report after an unlock that was not exact: one line per device with its outcome,
/// and a shortcut to Windows Sound settings to fix anything by hand. Non-modal. When the unlock also covered
/// cameras or microphone hardware (the camera helper's part), the lines are grouped under "Microphones",
/// "Cameras" and "Microphone hardware", and the closing hint says that a device still turned off comes back
/// when the PC restarts (the helper's disable never survives a restart).
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

        bool grouped = report.Lines.Select(l => l.Kind).Distinct().Skip(1).Any();
        foreach (IGrouping<DeviceKind, RestoreLine> kind in report.Lines.GroupBy(l => l.Kind).OrderBy(g => g.Key))
        {
            if (grouped)
            {
                DialogLayout.AddRow(content, DialogLayout.Text(KindHeading(kind.Key), TextRole.Heading, new Padding(0, 0, 0, 8)));
            }

            DialogLayout.AddRow(content, LineTable(kind));
        }

        DialogLayout.AddRow(content, DialogLayout.Hint(ClosingHint(report), new Padding(0, 4, 0, 8)));

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

    /// <summary>The heading over one kind's lines (only shown when the report covers more than one kind).</summary>
    internal static string KindHeading(DeviceKind kind) => kind switch
    {
        DeviceKind.Camera => "Cameras",
        DeviceKind.MicrophoneHardware => "Microphone hardware",
        _ => "Microphones",
    };

    /// <summary>Where to fix what is left. Devices the helper turned off come back by themselves at the next restart.</summary>
    internal static string ClosingHint(RestoreReport report) =>
        report.Lines.Any(l => l.Kind != DeviceKind.Microphone)
            ? "Anything that is still not right can be changed in Windows Sound settings (Recording devices). A camera or other device that is still turned off comes back on when you restart the PC."
            : "Anything that is still not right can be changed in Windows Sound settings (Recording devices).";

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

    /// <summary>One row per line: the outcome badge, then the device name over what happened to it.</summary>
    private static TableLayoutPanel LineTable(IEnumerable<RestoreLine> reportLines)
    {
        var lines = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty,
            ColumnStyles = { new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.Percent, 100f) },
        };
        foreach (RestoreLine line in reportLines)
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

        return lines;
    }
}
