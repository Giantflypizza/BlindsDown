using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Infrastructure;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Ui;

/// <summary>
/// Settings window (tabs). It edits a COPY of the settings; the caller reads
/// <see cref="Result"/> after DialogResult.OK and applies it.
///  * General: hotkey (HotkeyBox; validated by calling <c>tryHotkey</c>, which returns null on success or
///    an error message), play sounds, start with Windows (<see cref="StartWithWindows"/>), alert options.
///  * Indicator: show overlay, corner, screen edges, hide from screen capture.
///  * Devices: every microphone from <c>devices</c> with its current protection ("Muted in hardware",
///    "Muted in software (exclusive-mode apps can bypass)", "Disabled in Windows – left alone"...), a
///    checkbox per device to exclude it (writes <see cref="AppSettings.ExcludedDevices"/> using
///    <c>DeviceLockInfo</c>'s id plus its stable key). Exclusions can't be changed while locked (shows why).
///  * Alerts: list of "always allowed" apps with Remove.
///  * History: enable, retention days, "Clear history" (raises <see cref="ClearHistoryRequested"/>).
/// <c>tryHotkey</c> is only called when the hotkey actually changed, because it registers the hotkey for real.
/// </summary>
internal sealed class SettingsForm : Form
{
    /// <summary>The device name column gives up width first when the window is narrow.</summary>
    private const int DeviceNameColumn = 1;

    private readonly AppSettings _working;
    private readonly bool _locked;
    private readonly bool _startupDisabledInTaskManager;
    private readonly Func<Hotkey, string?> _tryHotkey;
    private readonly Hotkey _originalHotkey;
    private readonly List<AllowedApp> _allowedApps;
    private readonly Font _font;

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Margin = new Padding(12, 12, 12, 12) };
    private readonly TabPage _generalTab = Page("General");
    private readonly HotkeyBox _hotkeyBox = new() { Width = 240, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 4) };
    private readonly Label _hotkeyError;
    private readonly CheckBox _playSounds;
    private readonly CheckBox _startWithWindows;
    private readonly CheckBox _alertsEnabled;
    private readonly CheckBox _alertOnStop;
    private readonly CheckBox _showOverlay;
    private readonly ComboBox _corner = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Anchor = AnchorStyles.Left };
    private readonly CheckBox _showEdges;
    private readonly CheckBox _hideFromCapture;
    private readonly ListView _deviceList = NewList(checkBoxes: true, DeviceNameColumn);
    private readonly ListView _appList = NewList(checkBoxes: false, flexibleColumn: -1);
    private readonly Button _removeApp = DialogLayout.Button("Remove");
    private readonly CheckBox _historyEnabled;
    private readonly NumericUpDown _retention = new() { Minimum = 1, Maximum = 3650, Width = 72, Anchor = AnchorStyles.Left, TextAlign = HorizontalAlignment.Right };
    private readonly Label _historyStatus;

    public SettingsForm(
        AppSettings working,
        IReadOnlyList<DeviceLockInfo> devices,
        bool locked,
        bool startWithWindows,
        Func<Hotkey, string?> tryHotkey,
        bool startupDisabledInTaskManager = false)
    {
        _startupDisabledInTaskManager = startupDisabledInTaskManager;
        _working = SettingsStore.Clone(working);
        _locked = locked;
        _tryHotkey = tryHotkey;
        _originalHotkey = _working.GetHotkey();
        _allowedApps = [.. _working.AllowedApps];
        StartWithWindows = startWithWindows;
        Result = _working;

        _font = Theme.BodyFont;
        _hotkeyError = DialogLayout.Text(string.Empty, margin: new Padding(0, 0, 0, 6));
        _hotkeyError.ForeColor = UiPalette.ErrorText;
        _hotkeyError.Visible = false;
        _playSounds = DialogLayout.Check("Play a sound when locking and unlocking", _working.PlaySounds);
        _startWithWindows = DialogLayout.Check("Start BlindsDown when I sign in to Windows", startWithWindows);
        _alertsEnabled = DialogLayout.Check("Show a popup when an app starts using a microphone", _working.AlertsEnabled);
        _alertOnStop = Indented(DialogLayout.Check("Also show a popup when it stops", _working.AlertOnStop));
        _showOverlay = DialogLayout.Check("Show a lock indicator on every screen while locked", _working.ShowOverlay);
        _showEdges = DialogLayout.Check("Red lines along the screen edges while locked", _working.ShowScreenEdges);
        _hideFromCapture = DialogLayout.Check("Hide BlindsDown from screen sharing and recordings", _working.HideOverlayFromScreenCapture);
        _historyEnabled = DialogLayout.Check("Keep a history of which apps used your microphone or camera", _working.HistoryEnabled);
        _historyStatus = DialogLayout.Hint(string.Empty);
        _historyStatus.Visible = false;

        SuspendLayout();
        DialogLayout.Initialize(this, "BlindsDown Settings", _font);

        // Property-sheet look: grey around the tabs, so the tab pages stand out.
        BackColor = SystemColors.Control;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(740, 560);
        MinimumSize = new Size(560, 480);

        BuildGeneralTab();
        BuildIndicatorTab();
        BuildDevicesTab(devices);
        BuildAlertsTab();
        BuildHistoryTab();

        Button ok = DialogLayout.Button("OK");
        Button cancel = DialogLayout.Button("Cancel", DialogResult.Cancel);
        ok.Click += (_, _) => Accept();
        AcceptButton = ok;
        CancelButton = cancel;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 100f) },
            RowStyles = { new RowStyle(SizeType.Percent, 100f), new RowStyle(SizeType.AutoSize) },
        };
        root.Controls.Add(_tabs, 0, 0);
        root.Controls.Add(DialogLayout.CommandBar([], [ok, cancel]), 0, 1);
        Controls.Add(root);

        ResumeLayout(false);
        PerformLayout();
    }

    public event Action? ClearHistoryRequested;

    /// <summary>The edited settings (valid after DialogResult.OK).</summary>
    public AppSettings Result { get; private set; }

    public bool StartWithWindows { get; private set; }

    /// <summary>Shows a tab by its title ("General", "Indicator", "Devices", "Alerts", "History").</summary>
    public void SelectTab(string title)
    {
        TabPage? page = _tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => string.Equals(p.Text, title, StringComparison.OrdinalIgnoreCase));
        if (page is not null)
        {
            _tabs.SelectedTab = page;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitColumns();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitColumns();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearHistoryRequested = null;
            _font.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildGeneralTab()
    {
        TableLayoutPanel stack = DialogLayout.Stack(Padding.Empty);
        DialogLayout.AddRow(stack, DialogLayout.Heading("Hotkey", first: true));
        DialogLayout.AddRow(stack, DialogLayout.Text("Press it anywhere in Windows to lock or unlock your microphones."));
        _hotkeyBox.Value = _originalHotkey;
        _hotkeyBox.HotkeyChanged += _ => _hotkeyError.Visible = false;
        DialogLayout.AddRow(stack, _hotkeyBox);
        DialogLayout.AddRow(stack, _hotkeyError);
        DialogLayout.AddRow(stack, DialogLayout.Hint("Click the box, then press the new combination. Esc cancels, Backspace clears. F13–F24 work well with macro keys."));

        DialogLayout.AddRow(stack, DialogLayout.Heading("Sounds and startup"));
        DialogLayout.AddRow(stack, _playSounds);
        DialogLayout.AddRow(stack, _startWithWindows);
        if (_startupDisabledInTaskManager)
        {
            // Task Manager's switch wins over the Run entry, and it belongs to the user: point there instead.
            DialogLayout.AddRow(stack, DialogLayout.Hint("Turned off in Task Manager > Startup apps, so Windows won't start BlindsDown. Turn it on there."));
            Button startupApps = DialogLayout.Button("Open Startup apps");
            startupApps.Click += (_, _) => SettingsLinks.Open(SafeShell.OpenStartupApps);
            DialogLayout.AddRow(stack, startupApps);
        }

        DialogLayout.AddRow(stack, DialogLayout.Heading("Popups"));
        DialogLayout.AddRow(stack, _alertsEnabled);
        DialogLayout.AddRow(stack, _alertOnStop);
        DialogLayout.AddRow(stack, DialogLayout.Hint("While your microphones are locked, every app that opens one shows a popup, even with popups off or for apps you always allow."));
        _alertsEnabled.CheckedChanged += (_, _) => _alertOnStop.Enabled = _alertsEnabled.Checked;
        _alertOnStop.Enabled = _alertsEnabled.Checked;

        _generalTab.Controls.Add(stack);
        _tabs.TabPages.Add(_generalTab);
    }

    private void BuildIndicatorTab()
    {
        TabPage page = Page("Indicator");
        TableLayoutPanel stack = DialogLayout.Stack(Padding.Empty);
        DialogLayout.AddRow(stack, DialogLayout.Heading("While locked", first: true));
        DialogLayout.AddRow(stack, _showOverlay);

        _corner.Items.AddRange([.. CornerChoice.All]);
        _corner.SelectedItem = CornerChoice.All.FirstOrDefault(c => c.Corner == _working.OverlayCorner) ?? CornerChoice.All[0];
        _corner.AccessibleName = "Indicator position";
        TableLayoutPanel cornerRow = DialogLayout.Inline(new Padding(20, 0, 0, 6), DialogLayout.Caption("Position:", new Padding(0, 0, 8, 0)), _corner);
        DialogLayout.AddRow(stack, cornerRow);
        _showOverlay.CheckedChanged += (_, _) => cornerRow.Enabled = _showOverlay.Checked;
        cornerRow.Enabled = _showOverlay.Checked;

        DialogLayout.AddRow(stack, _showEdges);
        DialogLayout.AddRow(stack, DialogLayout.Heading("Privacy of the indicator"));
        DialogLayout.AddRow(stack, _hideFromCapture);
        DialogLayout.AddRow(stack, Indented(DialogLayout.Hint("People in a video call or watching a recording won't see the indicator; you still do.")));

        page.Controls.Add(stack);
        _tabs.TabPages.Add(page);
    }

    private void BuildDevicesTab(IReadOnlyList<DeviceLockInfo> devices)
    {
        TabPage page = Page("Devices");
        page.AutoScroll = false;
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Margin = Padding.Empty,
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 100f) },
        };

        DialogLayout.AddRow(table, DialogLayout.Text("BlindsDown locks every working microphone. Exclude a microphone to leave it alone while locked."));
        if (_locked)
        {
            DialogLayout.AddRow(table, DialogLayout.Banner(
                "Exclusions can't be changed while your microphones are locked. Unlock first, then change them here.",
                AlertSeverity.Warning));
        }

        // While locked the list stays readable (its protection column matters most then) but read-only:
        // no checkboxes, the exclusion shown as text. A disabled ListView would grey out (and in dark
        // mode hide) exactly the information the user is looking for.
        _deviceList.CheckBoxes = !_locked;
        _deviceList.Columns.Add("Exclude");
        _deviceList.Columns.Add("Microphone");
        _deviceList.Columns.Add("Type");
        _deviceList.Columns.Add("Protection");
        foreach (DeviceLockInfo device in devices.Where(d => d.Kind == DeviceKind.Microphone).OrderBy(d => d.IsVirtual).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            bool excluded = DeviceExclusions.IsExcluded(_working.ExcludedDevices, device, devices);
            var item = new ListViewItem(_locked && excluded ? "Yes" : string.Empty)
            {
                Tag = device,
                Checked = excluded,
                ToolTipText = device.Name,
            };
            item.SubItems.Add(device.Name);
            item.SubItems.Add(DeviceText.Type(device.IsVirtual));
            ListViewItem.ListViewSubItem protection = item.SubItems.Add(DeviceText.Protection(device, _locked));
            if (device.Problem is not null)
            {
                // A device the lock could not protect must stand out.
                item.UseItemStyleForSubItems = false;
                protection.ForeColor = UiPalette.ErrorText;
            }

            _deviceList.Items.Add(item);
        }

        foreach (DeviceExclusion orphan in DeviceExclusions.Orphans(_working.ExcludedDevices, devices))
        {
            var item = new ListViewItem(_locked ? "Yes" : string.Empty) { Tag = orphan, Checked = true, ToolTipText = orphan.DisplayName };
            item.SubItems.Add(orphan.DisplayName);
            item.SubItems.Add(DeviceText.Empty);
            item.SubItems.Add("Not connected");
            _deviceList.Items.Add(item);
        }

        _deviceList.AccessibleName = _locked
            ? "Microphones and how each one is protected"
            : "Microphones. Check a microphone to exclude it from the lock.";
        if (_deviceList.Items.Count == 0)
        {
            DialogLayout.AddRow(table, DialogLayout.Hint("No microphones found. Connect one and open Settings again."));
        }
        else
        {
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            table.RowCount = table.RowStyles.Count;
            _deviceList.Dock = DockStyle.Fill;
            _deviceList.Margin = new Padding(0, 4, 0, 8);
            table.Controls.Add(_deviceList, 0, table.RowCount - 1);
        }

        DialogLayout.AddRow(table, DialogLayout.Hint("Virtual microphones (such as SteelSeries Sonar) are fed by your physical ones, so locking the physical ones silences them too.", new Padding(0)));
        page.Controls.Add(table);
        _tabs.TabPages.Add(page);
    }

    private void BuildAlertsTab()
    {
        TabPage page = Page("Alerts");
        page.AutoScroll = false;
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Margin = Padding.Empty,
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 100f) },
        };
        DialogLayout.AddRow(table, DialogLayout.Text("These apps never show a popup when they use your microphone. Their use is still recorded in Activity."));

        _appList.Columns.Add("App");
        _appList.HeaderStyle = ColumnHeaderStyle.None;
        _appList.MultiSelect = true;
        _appList.AccessibleName = "Always allowed apps";
        RefreshAppList();
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        table.RowCount = table.RowStyles.Count;
        _appList.Dock = DockStyle.Fill;
        _appList.Margin = new Padding(0, 4, 0, 8);
        table.Controls.Add(_appList, 0, table.RowCount - 1);

        _removeApp.Margin = new Padding(0, 0, 0, 8);
        _removeApp.Anchor = AnchorStyles.Left;
        _removeApp.Enabled = false;
        _removeApp.Click += (_, _) => RemoveSelectedApps();
        _appList.SelectedIndexChanged += (_, _) => _removeApp.Enabled = _appList.SelectedItems.Cast<ListViewItem>().Any(i => i.Tag is AllowedApp);
        DialogLayout.AddRow(table, _removeApp);
        DialogLayout.AddRow(table, DialogLayout.Hint("To add an app, open Activity from the tray menu, right-click the app and choose \"Always allow (no popups)\".", new Padding(0)));

        page.Controls.Add(table);
        _tabs.TabPages.Add(page);
    }

    private void BuildHistoryTab()
    {
        TabPage page = Page("History");
        TableLayoutPanel stack = DialogLayout.Stack(Padding.Empty);
        DialogLayout.AddRow(stack, DialogLayout.Heading("Activity history", first: true));
        DialogLayout.AddRow(stack, _historyEnabled);

        _retention.Value = Math.Clamp(_working.HistoryRetentionDays, (int)_retention.Minimum, (int)_retention.Maximum);
        _retention.AccessibleName = "Days to keep history";
        TableLayoutPanel retentionRow = DialogLayout.Inline(
            new Padding(20, 2, 0, 8),
            DialogLayout.Caption("Keep entries for", new Padding(0, 0, 6, 0)),
            _retention,
            DialogLayout.Caption("days", new Padding(6, 0, 0, 0)));
        DialogLayout.AddRow(stack, retentionRow);
        _historyEnabled.CheckedChanged += (_, _) => retentionRow.Enabled = _historyEnabled.Checked;
        retentionRow.Enabled = _historyEnabled.Checked;

        DialogLayout.AddRow(stack, DialogLayout.Hint(@"History is stored only on this PC (in %LocalAppData%\BlindsDown) and never leaves it."));

        Button clear = DialogLayout.Button("Clear history…");
        clear.Margin = new Padding(0, 4, 0, 6);
        clear.Anchor = AnchorStyles.Left;
        clear.Click += (_, _) => ConfirmClearHistory();
        DialogLayout.AddRow(stack, clear);
        DialogLayout.AddRow(stack, _historyStatus);

        page.Controls.Add(stack);
        _tabs.TabPages.Add(page);
    }

    private void Accept()
    {
        Hotkey hotkey = _hotkeyBox.Value;
        HotkeyProblem problem = hotkey.Validate();
        string? error = problem == HotkeyProblem.None ? null : HotkeyText.Describe(problem);
        if (error is null && hotkey != _originalHotkey)
        {
            error = _tryHotkey(hotkey);
        }

        if (error is not null)
        {
            _hotkeyError.Text = error;
            _hotkeyError.Visible = true;
            _tabs.SelectedTab = _generalTab;
            _hotkeyBox.Focus();
            return;
        }

        _working.Hotkey = hotkey.ToString();
        _working.PlaySounds = _playSounds.Checked;
        _working.AlertsEnabled = _alertsEnabled.Checked;
        _working.AlertOnStop = _alertOnStop.Checked;
        _working.ShowOverlay = _showOverlay.Checked;
        _working.OverlayCorner = (_corner.SelectedItem as CornerChoice)?.Corner ?? _working.OverlayCorner;
        _working.ShowScreenEdges = _showEdges.Checked;
        _working.HideOverlayFromScreenCapture = _hideFromCapture.Checked;
        _working.HistoryEnabled = _historyEnabled.Checked;
        _working.HistoryRetentionDays = (int)_retention.Value;
        _working.AllowedApps = [.. _allowedApps];
        if (!_locked)
        {
            IEnumerable<ListViewItem> checkedItems = _deviceList.Items.Cast<ListViewItem>().Where(i => i.Checked);
            _working.ExcludedDevices = DeviceExclusions.Build(
                checkedItems.Select(i => i.Tag).OfType<DeviceLockInfo>(),
                checkedItems.Select(i => i.Tag).OfType<DeviceExclusion>());
        }

        Result = _working;
        StartWithWindows = _startWithWindows.Checked;
        DialogResult = DialogResult.OK;
    }

    private void ConfirmClearHistory()
    {
        var clearButton = new TaskDialogButton("Clear history");
        var page = new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = "Clear all activity history?",
            Text = "This deletes the record of which apps used your microphone. It can't be undone.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { clearButton, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
        };

        if (TaskDialog.ShowDialog(this, page) != clearButton)
        {
            return;
        }

        ClearHistoryRequested?.Invoke();
        _historyStatus.Text = "History cleared.";
        _historyStatus.Visible = true;
    }

    private void RemoveSelectedApps()
    {
        foreach (ListViewItem item in _appList.SelectedItems.Cast<ListViewItem>().ToList())
        {
            if (item.Tag is AllowedApp app)
            {
                _allowedApps.Remove(app);
            }
        }

        RefreshAppList();
        FitColumns();
    }

    private void RefreshAppList()
    {
        _appList.BeginUpdate();
        _appList.Items.Clear();
        foreach (AllowedApp app in _allowedApps.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            _appList.Items.Add(new ListViewItem(app.DisplayName) { Tag = app, ToolTipText = app.AppKey });
        }

        if (_allowedApps.Count == 0)
        {
            _appList.Items.Add(new ListViewItem("No apps yet.") { ForeColor = SystemColors.GrayText });
        }

        _appList.EndUpdate();
        _removeApp.Enabled = false;
    }

    /// <summary>Sizes list columns to their content at the current DPI (WinForms does not scale column widths).</summary>
    private void FitColumns()
    {
        ListViews.FitColumns(_deviceList, DeviceNameColumn);
        ListViews.FitColumns(_appList);
    }

    private static TabPage Page(string title) =>
        new(title)
        {
            Padding = new Padding(14),
            AutoScroll = true,

            // Explicit, not the visual-style colour: the themed page body stays light in dark mode.
            UseVisualStyleBackColor = false,
            BackColor = SystemColors.Window,
        };

    private static ListView NewList(bool checkBoxes, int flexibleColumn)
    {
        var list = new ListView
        {
            View = View.Details,
            CheckBoxes = checkBoxes,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = true,
            ShowItemToolTips = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        ListViews.AttachAutoFit(list, flexibleColumn);
        return list;
    }

    private static T Indented<T>(T control)
        where T : Control
    {
        control.Margin = new Padding(control.Margin.Left + 20, control.Margin.Top, control.Margin.Right, control.Margin.Bottom);
        return control;
    }

    private sealed record CornerChoice(OverlayCorner Corner, string Label)
    {
        public static readonly CornerChoice[] All =
        [
            new(OverlayCorner.TopRight, "Top right"),
            new(OverlayCorner.TopLeft, "Top left"),
            new(OverlayCorner.BottomRight, "Bottom right"),
            new(OverlayCorner.BottomLeft, "Bottom left"),
        ];

        public override string ToString() => Label;
    }
}
