using System.Runtime.InteropServices;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// About: version, what BlindsDown does, the no-internet promise, the honest limits, the license, and the
/// repository address as selectable text. Never a clickable link: BlindsDown does not open browsers.
/// </summary>
internal sealed class AboutForm : Form
{
    public const string RepositoryUrl = "https://github.com/Giantflypizza/BlindsDown";
    public const string NoInternetSentence = "BlindsDown never connects to the internet.";

    private readonly Font _font;

    public AboutForm(string version)
    {
        _font = Theme.BodyFont;

        SuspendLayout();
        DialogLayout.Initialize(this, "About BlindsDown", _font);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(500, 400);

        TableLayoutPanel content = DialogLayout.Stack(new Padding(24, 20, 24, 12));
        DialogLayout.AddRow(content, DialogLayout.Text("BlindsDown", TextRole.Title, new Padding(0, 0, 0, 2)));
        DialogLayout.AddRow(content, DialogLayout.Hint("Version " + version, new Padding(0, 0, 0, 10)));
        DialogLayout.AddRow(content, DialogLayout.Text(
            "A privacy lock for your microphones and cameras: one hotkey mutes every microphone (and, with camera lock on, turns your cameras off), guards them while locked and restores them exactly as they were.",
            margin: new Padding(0, 0, 0, 12)));

        DialogLayout.AddRow(content, DialogLayout.Text(NoInternetSentence, TextRole.Strong, new Padding(0, 0, 0, 2)));
        DialogLayout.AddRow(content, DialogLayout.Text(
            "It contains no network code: the build fails if any is added, and the app watches for it while it runs."));

        DialogLayout.AddRow(content, DialogLayout.Heading("Honest limits"));
        DialogLayout.AddRow(content, DialogLayout.Bullets(
        [
            "Software can't stop malware that has administrator rights. Only a camera cover or a hardware mute switch is certain.",
            "Malware running as you can unlock too, but not unseen: every unlock flashes on screen and changes the tray icon.",
            "Apps using exclusive mode, ASIO or kernel streaming can get around a software mute.",
            "A camera that an app is using may refuse to turn off until the app lets go of it. BlindsDown says so and keeps trying.",
            "Some full-screen or elevated apps swallow hotkeys. Use the tray icon then; every change flashes on screen (and plays a sound if sounds are on).",
            "The lock applies to every Windows user on this PC.",
        ]));

        DialogLayout.AddRow(content, DialogLayout.Heading("License and source code"));
        DialogLayout.AddRow(content, DialogLayout.Text("Free and open source under the MIT License.", margin: new Padding(0, 0, 0, 6)));
        DialogLayout.AddRow(content, BuildUrlRow());

        Button ok = DialogLayout.Button("OK", DialogResult.OK);
        AcceptButton = ok;
        CancelButton = ok;
        DialogLayout.FitHeightToContent(this, content, DialogLayout.CommandBar([], [ok]));

        ResumeLayout(false);
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
        }

        base.Dispose(disposing);
    }

    private static TableLayoutPanel BuildUrlRow()
    {
        var url = new TextBox
        {
            Text = RepositoryUrl,
            ReadOnly = true,
            BackColor = SystemColors.Window,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 8, 0),
            AccessibleName = "Source code address",
        };
        Button copy = DialogLayout.Button("Copy");
        copy.Margin = Padding.Empty;
        copy.AccessibleName = "Copy the source code address";
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(RepositoryUrl);
                copy.Text = "Copied";
            }
            catch (ExternalException)
            {
                // Another app holds the clipboard; the text box is still selectable.
                url.Focus();
                url.SelectAll();
            }
        };

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8),
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 100f), new ColumnStyle(SizeType.AutoSize) },
            RowStyles = { new RowStyle(SizeType.AutoSize) },
        };
        url.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        url.Dock = DockStyle.None;
        copy.Anchor = AnchorStyles.Right;
        row.Controls.Add(url, 0, 0);
        row.Controls.Add(copy, 1, 0);
        return row;
    }
}
