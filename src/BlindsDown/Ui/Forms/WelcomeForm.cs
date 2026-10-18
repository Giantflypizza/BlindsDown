namespace BlindsDown.Ui.Forms;

/// <summary>
/// First-run welcome: what BlindsDown does, the hotkey, the no-internet promise, how to keep the tray
/// icon visible on Windows 11 (which hides new tray icons), the "Start with Windows" choice and, when offered,
/// "Also lock my cameras" (unchecked: installing the camera helper needs an administrator prompt, so it is
/// never the default).
/// </summary>
internal sealed class WelcomeForm : Form
{
    private readonly Font _font;
    private readonly CheckBox _startWithWindows;
    private readonly CheckBox? _lockCameras;

    public WelcomeForm(string hotkeyText, bool startWithWindows, bool offerCameraLock = false)
    {
        _font = Theme.BodyFont;
        _startWithWindows = DialogLayout.Check("Start BlindsDown when I sign in to Windows", startWithWindows);
        _startWithWindows.Margin = new Padding(0, 6, 0, 4);
        if (offerCameraLock)
        {
            _lockCameras = DialogLayout.Check("Also lock my cameras", false);
        }

        SuspendLayout();
        DialogLayout.Initialize(this, "Welcome to BlindsDown", _font);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(500, 400);

        TableLayoutPanel content = DialogLayout.Stack(new Padding(24, 20, 24, 12));
        DialogLayout.AddRow(content, DialogLayout.Text("Welcome to BlindsDown", TextRole.Title, new Padding(0, 0, 0, 8)));
        DialogLayout.AddRow(content, DialogLayout.Text(
            offerCameraLock
                ? "BlindsDown is a privacy lock for your microphones and cameras. One hotkey mutes every microphone (and, if you like, turns your cameras off), keeps them that way while locked, and puts everything back exactly as it was when you unlock."
                : "BlindsDown is a privacy lock for your microphones. One hotkey mutes all of them, keeps them muted while locked, and puts everything back exactly as it was when you unlock.",
            margin: new Padding(0, 0, 0, 12)));

        DialogLayout.AddRow(content, DialogLayout.Heading("Your hotkey", first: true));
        DialogLayout.AddRow(content, new KeyComboControl(hotkeyText) { Margin = new Padding(0, 2, 0, 10) });
        DialogLayout.AddRow(content, DialogLayout.Hint(
            "Press it anywhere in Windows to lock or unlock, or click the BlindsDown icon in the notification area. Every change flashes on screen and plays a short sound, so you always know it worked. You can change the hotkey in Settings."));

        DialogLayout.AddRow(content, DialogLayout.Heading("Private by design"));
        DialogLayout.AddRow(content, DialogLayout.Text(AboutForm.NoInternetSentence, TextRole.Strong, new Padding(0, 0, 0, 2)));
        DialogLayout.AddRow(content, DialogLayout.Text("Your settings and activity history stay on this PC."));

        DialogLayout.AddRow(content, DialogLayout.Heading("Keep the icon in view"));
        DialogLayout.AddRow(content, DialogLayout.Text(
            "Windows 11 hides new notification-area icons. To keep BlindsDown visible, open taskbar settings, expand \"Other system tray icons\" and turn BlindsDown on."));
        Button taskbar = DialogLayout.Button("Open taskbar settings");
        taskbar.Margin = new Padding(0, 0, 0, 8);
        taskbar.Anchor = AnchorStyles.Left;
        taskbar.Click += (_, _) => SettingsLinks.OpenTaskbar();
        DialogLayout.AddRow(content, taskbar);
        DialogLayout.AddRow(content, _startWithWindows);
        if (_lockCameras is not null)
        {
            DialogLayout.AddRow(content, _lockCameras);
            DialogLayout.AddRow(content, DialogLayout.Hint(
                "Windows asks for administrator permission once to install a small helper. You can change this later in Settings > Camera.",
                new Padding(20, 0, 0, 4)));
        }

        Button start = DialogLayout.Button("Get started", DialogResult.OK);
        AcceptButton = start;
        CancelButton = start;
        DialogLayout.FitHeightToContent(this, content, DialogLayout.CommandBar([], [start]));

        ResumeLayout(false);
        PerformLayout();
    }

    public bool StartWithWindows => _startWithWindows.Checked;

    /// <summary>The "Also lock my cameras" checkbox (false when it wasn't offered).</summary>
    public bool LockCameras => _lockCameras?.Checked == true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // First run is started by the user, so Windows lets us come to the front.
        Activate();
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
