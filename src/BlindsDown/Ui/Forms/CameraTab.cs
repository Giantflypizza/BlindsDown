using BlindsDown.Core.Settings;
using BlindsDown.Ui.Native;

namespace BlindsDown.Ui.Forms;

/// <summary>
/// The content of the Settings "Camera" tab, driven by <see cref="HelperUiState"/>:
///  * Not installed: why turning cameras off needs the helper, and "Turn on camera lock…".
///  * Installed (working, another version, not responding): a status line ("Camera lock is on", or "off" while
///    "Lock cameras" is unticked; a helper of another version is called older or newer only when it is), the
///    camera options, and the helper's own buttons (Update helper / Replace helper / Repair, Remove helper…).
///  * Busy: a progress line over the last layout, with every helper button disabled.
/// Every part is built once and only shown or hidden, so a new state never loses what the user is editing in
/// the option checkboxes. The options stay editable in every state (also while locked): they are plain settings
/// that apply at the next lock. The buttons only raise events; the Settings window confirms and the app acts.
/// </summary>
internal sealed class CameraTab
{
    private readonly TableLayoutPanel _content = DialogLayout.Stack(Padding.Empty);
    private readonly List<Control> _introRows = [];
    private readonly List<Control> _installedRows = [];
    private readonly Button _enable = ElevatingButton(HelperText.EnableButton);
    private readonly Button _fix = ElevatingButton(HelperText.UpdateButton);
    private readonly Button _remove = DialogLayout.Button(HelperText.RemoveButton);
    private readonly TableLayoutPanel _statusRow;
    private readonly SeverityGlyphControl _statusGlyph = new(AlertSeverity.Success) { Margin = new Padding(0, 0, 8, 0) };
    private readonly Label _statusTitle;
    private readonly Label _statusText;
    private readonly TableLayoutPanel _detailRow;
    private readonly SeverityGlyphControl _detailGlyph = new(AlertSeverity.Warning) { Margin = new Padding(0, 0, 8, 0), Anchor = AnchorStyles.Top | AnchorStyles.Left };
    private readonly Label _detailText;
    private readonly TableLayoutPanel _progressRow;
    private readonly Label _progressText;

    private HelperUiState _state = HelperUiState.NotInstalled;

    /// <summary>The layout behind a Busy state: the last state that wasn't Busy.</summary>
    private HelperAvailability _layout = HelperAvailability.NotInstalled;

    /// <summary>This BlindsDown's version: a helper of another version is "older" or "newer" than it.</summary>
    private readonly string _appVersion;

    /// <summary>The helper version shown in the status line.</summary>
    private string? _version;

    public CameraTab(AppSettings settings, string? appVersion = null)
    {
        _appVersion = appVersion ?? AppVersion.Text;
        LockCameras = DialogLayout.Check("Lock cameras", settings.LockCameras);
        IncludeInfrared = Indented(DialogLayout.Check("Also lock infrared cameras", settings.IncludeInfraredCameras), 20);
        IncludeVirtual = Indented(DialogLayout.Check("Also lock virtual cameras", settings.IncludeVirtualCameras), 20);
        DisableMicHardware = DialogLayout.Check("Also turn off microphone-only hardware", settings.DisableMicOnlyHardware);

        _statusTitle = DialogLayout.Text(string.Empty, TextRole.Strong, new Padding(0, 1, 0, 2));
        _statusText = DialogLayout.Hint(string.Empty, Padding.Empty);
        _statusRow = StatusRow(_statusGlyph, _statusTitle, _statusText);

        _detailText = DialogLayout.Text(string.Empty, margin: Padding.Empty);
        _detailRow = NoticeRow(_detailGlyph, _detailText);

        _progressText = DialogLayout.Caption(string.Empty, new Padding(8, 0, 0, 0));
        var progress = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = 120, Height = 14, Margin = Padding.Empty };
        _progressRow = DialogLayout.Inline(new Padding(0, 0, 0, 10), progress, _progressText);

        Build();
        LockCameras.CheckedChanged += (_, _) => OnOptionChanged();
        IncludeInfrared.CheckedChanged += (_, _) => OnOptionChanged();
        IncludeVirtual.CheckedChanged += (_, _) => OnOptionChanged();
        DisableMicHardware.CheckedChanged += (_, _) => OnOptionChanged();
        _enable.Click += (_, _) => EnableClicked?.Invoke();
        _fix.Click += (_, _) => FixClicked?.Invoke();
        _remove.Click += (_, _) => RemoveClicked?.Invoke();
        SetState(HelperUiState.NotInstalled);
    }

    /// <summary>"Turn on camera lock…".</summary>
    public event Action? EnableClicked;

    /// <summary>"Update helper", "Replace helper" or "Repair" (all reinstall the helper with this version).</summary>
    public event Action? FixClicked;

    /// <summary>"Remove helper…".</summary>
    public event Action? RemoveClicked;

    /// <summary>An option checkbox changed (the Devices tab previews what the next lock does).</summary>
    public event Action? OptionsChanged;

    public Control Content => _content;

    public CheckBox LockCameras { get; }

    public CheckBox IncludeInfrared { get; }

    public CheckBox IncludeVirtual { get; }

    public CheckBox DisableMicHardware { get; }

    public HelperUiState State => _state;

    /// <summary>How the installed helper's version compares with this BlindsDown's (for a helper of another version).</summary>
    public HelperVersionAge VersionAge => HelperText.CompareVersions(_version, _appVersion);

    /// <summary>The installed helper's version, if known.</summary>
    public string? HelperVersion => _version;

    /// <summary>This BlindsDown's version.</summary>
    public string AppVersionText => _appVersion;

    /// <summary>Whether cameras can be locked with the helper as it is (an older helper still works).</summary>
    public bool HelperAvailable => _layout is HelperAvailability.Working or HelperAvailability.NeedsUpdate;

    public void SetState(HelperUiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        bool busy = state.Availability == HelperAvailability.Busy;
        if (!busy)
        {
            _layout = state.Availability;
        }

        // A Busy state usually carries no version: keep showing the one last reported.
        _version = busy ? state.HelperVersion ?? _version : state.HelperVersion;

        _content.SuspendLayout();
        try
        {
            bool installed = _layout != HelperAvailability.NotInstalled;
            _introRows.ForEach(c => c.Visible = !installed);
            _installedRows.ForEach(c => c.Visible = installed);
            ShowStatus();

            string? fix = HelperText.FixButton(_layout, VersionAge);
            _fix.Visible = fix is not null;
            _fix.Text = fix ?? HelperText.UpdateButton;

            _progressRow.Visible = busy;
            _progressText.Text = busy ? HelperText.Busy(state.Detail) : string.Empty;

            // A detail while busy is the progress text itself. Otherwise it is news about the last attempt:
            // a note when things work, a warning when they don't.
            bool showDetail = !busy && !string.IsNullOrWhiteSpace(state.Detail);
            _detailRow.Visible = showDetail;
            _detailText.Text = showDetail ? state.Detail : string.Empty;
            _detailGlyph.Severity = _layout == HelperAvailability.Working ? AlertSeverity.Info : AlertSeverity.Warning;
            _detailRow.BackColor = UiPalette.Blend(SystemColors.Window, UiPalette.SeverityColor(_detailGlyph.Severity), 0.14f);
            _detailRow.AccessibleName = _detailText.Text;

            _enable.Enabled = !busy;
            _fix.Enabled = !busy;
            _remove.Enabled = !busy;
            UpdateOptionStates();
        }
        finally
        {
            _content.ResumeLayout(true);
        }
    }

    /// <summary>Writes the camera options into <paramref name="settings"/>.</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.LockCameras = LockCameras.Checked;
        settings.IncludeInfraredCameras = IncludeInfrared.Checked;
        settings.IncludeVirtualCameras = IncludeVirtual.Checked;
        settings.DisableMicOnlyHardware = DisableMicHardware.Checked;
    }

    /// <summary>A button that starts a UAC prompt carries Windows' shield (drawn only by a native button).</summary>
    private static Button ElevatingButton(string text)
    {
        Button button = DialogLayout.Button(text);
        button.FlatStyle = FlatStyle.System;

        // Room for the shield, which WinForms doesn't know about when it sizes the button to its text.
        button.Padding = new Padding(button.Padding.Left + 20, 0, button.Padding.Right, 0);
        button.HandleCreated += (_, _) => PopupNative.ShowElevationShield(button.Handle);
        return button;
    }

    private static T Indented<T>(T control, int by)
        where T : Control
    {
        control.Margin = new Padding(control.Margin.Left + by, control.Margin.Top, control.Margin.Right, control.Margin.Bottom);
        return control;
    }

    /// <summary>The badge on the left, the title and its explanation stacked to its right (so the text lines up with the title).</summary>
    private static TableLayoutPanel StatusRow(SeverityGlyphControl glyph, Label title, Label text)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Padding = Padding.Empty,
            Margin = new Padding(0, 0, 0, 6),
            ColumnStyles = { new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.Percent, 100f) },
            RowStyles = { new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.AutoSize) },
        };
        glyph.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        row.Controls.Add(glyph, 0, 0);
        row.SetRowSpan(glyph, 2);
        row.Controls.Add(title, 1, 0);
        row.Controls.Add(text, 1, 1);
        return row;
    }

    /// <summary>A tinted notice like <see cref="DialogLayout.Banner"/>, but with a badge and text that change in place.</summary>
    private static TableLayoutPanel NoticeRow(SeverityGlyphControl glyph, Label text)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 10),
            ColumnStyles = { new ColumnStyle(SizeType.AutoSize), new ColumnStyle(SizeType.Percent, 100f) },
            RowStyles = { new RowStyle(SizeType.AutoSize) },
        };
        row.Controls.Add(glyph, 0, 0);
        row.Controls.Add(text, 1, 0);
        return row;
    }

    private void Build()
    {
        DialogLayout.AddRow(_content, _progressRow);
        DialogLayout.AddRow(_content, _detailRow);

        // Not installed: what camera lock is and why it needs the helper.
        _introRows.Add(DialogLayout.AddRow(_content, DialogLayout.Heading(HelperText.IntroHeading, first: true)));
        _introRows.Add(DialogLayout.AddRow(_content, DialogLayout.Text(HelperText.Intro)));
        _introRows.Add(DialogLayout.AddRow(_content, DialogLayout.Text(HelperText.WhyAdmin, margin: new Padding(0, 0, 0, 12))));
        _enable.Anchor = AnchorStyles.Left;
        _enable.Margin = new Padding(0, 0, 0, 8);
        _introRows.Add(DialogLayout.AddRow(_content, _enable));

        // Installed: status, options, the helper itself.
        _installedRows.Add(DialogLayout.AddRow(_content, _statusRow));
        _installedRows.Add(DialogLayout.AddRow(_content, DialogLayout.Heading("What to lock")));
        _installedRows.Add(DialogLayout.AddRow(_content, LockCameras));
        _installedRows.Add(DialogLayout.AddRow(_content, IncludeInfrared));
        _installedRows.Add(DialogLayout.AddRow(_content, Indented(DialogLayout.Hint("Windows Hello face sign-in won't work while locked.", new Padding(0, 0, 0, 4)), 40)));
        _installedRows.Add(DialogLayout.AddRow(_content, IncludeVirtual));
        _installedRows.Add(DialogLayout.AddRow(_content, Indented(DialogLayout.Hint(HelperText.VirtualCamerasHint, new Padding(0, 0, 0, 4)), 40)));
        _installedRows.Add(DialogLayout.AddRow(_content, DisableMicHardware));
        _installedRows.Add(DialogLayout.AddRow(_content, Indented(DialogLayout.Hint("Such as webcam and USB microphones. Strongest: apps lose those microphones until you unlock.", new Padding(0, 0, 0, 4)), 20)));
        _installedRows.Add(DialogLayout.AddRow(_content, DialogLayout.Hint(HelperText.OptionsApplyNextLock, new Padding(0, 6, 0, 8))));

        _installedRows.Add(DialogLayout.AddRow(_content, DialogLayout.Heading("Camera helper")));
        _installedRows.Add(DialogLayout.AddRow(_content, DialogLayout.Hint(HelperText.HelperAbout)));
        _fix.Margin = new Padding(0, 0, 8, 0);
        _remove.Margin = Padding.Empty;
        _installedRows.Add(DialogLayout.AddRow(_content, DialogLayout.Inline(new Padding(0, 0, 0, 8), _fix, _remove)));
    }

    private void OnOptionChanged()
    {
        UpdateOptionStates();
        ShowStatus();
        OptionsChanged?.Invoke();
    }

    /// <summary>The status line for the installed helper. It follows "Lock cameras", so it never says camera lock
    /// is on while that option is unticked.</summary>
    private void ShowStatus()
    {
        HelperStatusLine? status = HelperText.Status(new HelperUiState(_layout, _version), LockCameras.Checked, _appVersion);
        _statusRow.Visible = _layout != HelperAvailability.NotInstalled && status is not null;
        _statusText.Visible = status?.Text is not null;
        if (status is not null)
        {
            _statusGlyph.Severity = status.Severity;
            _statusGlyph.AccessibleName = status.Title;
            _statusTitle.Text = status.Title;
            _statusText.Text = status.Text ?? string.Empty;
        }
    }

    /// <summary>Infrared and virtual cameras are only choices when cameras are locked at all.</summary>
    private void UpdateOptionStates()
    {
        IncludeInfrared.Enabled = LockCameras.Checked;
        IncludeVirtual.Enabled = LockCameras.Checked;
    }
}
