using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Infrastructure;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Ui;

/// <summary>
/// Settings window (tabs). It edits a COPY of the settings; the caller reads
/// <see cref="Result"/> after DialogResult.OK and applies it.
///  * General: hotkey (HotkeyBox; validated by calling <c>tryHotkey</c>, which returns null on success or
///    an error message), automatic lock (Win+L, sleep), play sounds, start with Windows
///    (<see cref="StartWithWindows"/>), alert options.
///  * Camera: the optional camera helper (<see cref="HelperUiState"/>, see <see cref="CameraTab"/>) and the
///    camera options. Its buttons ask first (<see cref="Dialogs.ConfirmInstallHelper"/>,
///    <see cref="Dialogs.ConfirmRemoveHelper"/>), then raise <see cref="EnableHelperRequested"/>,
///    <see cref="UpdateHelperRequested"/> or <see cref="RemoveHelperRequested"/>; the app does the work while
///    this window stays open and reports back through <see cref="SetHelperState"/>.
///  * Indicator: show overlay, corner, screen edges, hide from screen capture.
///  * Devices: every microphone, camera and piece of microphone hardware from <c>devices</c> with its kind,
///    type and protection ("Muted in hardware", "Turned off (device disabled)", "Disabled in Windows – left
///    alone"...; while unlocked, what the next lock would do), a checkbox per device to exclude it (writes
///    <see cref="AppSettings.ExcludedDevices"/> using <c>DeviceLockInfo</c>'s id plus its stable key, for every
///    kind). Exclusions can't be changed while locked (shows why). Below the list: the stronger microphone lock.
///  * Alerts: list of "always allowed" apps with Remove.
///  * History: enable, retention days, "Clear history" (raises <see cref="ClearHistoryRequested"/>).
/// <c>tryHotkey</c> is only called when the hotkey actually changed, because it registers the hotkey for real.
/// The global hotkey keeps working while this window is open; only a press while the hotkey box is taking keys
/// (<see cref="IsHotkeyBoxCapturing"/>) goes into the box instead (<see cref="OfferCapturedHotkey"/>).
/// <c>locked</c> is the state when the window opened (it decides whether exclusions can be edited); the lock can
/// still change while the window is open (tray click, automatic lock), so the "Remove helper" question asks
/// <c>isLockedNow</c> at the moment it is shown.
/// </summary>
internal sealed class SettingsForm : Form
{
    /// <summary>The General tab's note on popups while locked (cameras count once the camera lock is on).</summary>
    internal const string LockedPopupsHint =
        "While locked, every app that opens a microphone (or a camera, when cameras are locked) shows a popup, even with popups off or for apps you always allow.";

    /// <summary>The Alerts tab's explanation of the "always allow" list: it only quiets popups while unlocked.</summary>
    internal const string AllowedAppsText =
        "While unlocked, these apps don't show a popup when they use your microphone or camera. While locked they still do. Their use still shows in Activity.";

    /// <summary>Under the History tab's checkbox: turning history off stops app-use records, not the lock timeline.</summary>
    internal const string LockTimelineHint =
        "Locks and unlocks are always recorded in Activity's lock timeline, even with history off, so nothing can unlock without you seeing it. They are kept for the same number of days.";

    /// <summary>The device name column gives up width first when the window is narrow.</summary>
    private const int DeviceNameColumn = 1;
    private const int ProtectionColumn = 4;

    private readonly AppSettings _working;
    private readonly bool _locked;
    private readonly Func<bool>? _isLockedNow;
    private readonly bool _startupDisabledInTaskManager;
    private readonly Func<Hotkey, string?> _tryHotkey;
    private readonly Hotkey _originalHotkey;
    private readonly List<AllowedApp> _allowedApps;
    private readonly Font _font;

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Margin = new Padding(12, 12, 12, 12) };
    private readonly TabPage _generalTab = Page("General");
    private readonly HotkeyBox _hotkeyBox = new() { Width = 240, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 4) };
    private readonly Label _hotkeyError;
    private readonly CheckBox _autoLockOnWindowsLock;
    private readonly CheckBox _autoLockOnSleep;
    private readonly CheckBox _playSounds;
    private readonly CheckBox _startWithWindows;
    private readonly CheckBox _alertsEnabled;
    private readonly CheckBox _alertOnStop;
    private readonly CheckBox _showOverlay;
    private readonly ComboBox _corner = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Anchor = AnchorStyles.Left };
    private readonly CheckBox _showEdges;
    private readonly CheckBox _hideFromCapture;
    private readonly CameraTab _cameraTab;
    private readonly ListView _deviceList = NewList(checkBoxes: true, DeviceNameColumn);
    private readonly Label _noDevices;
    private readonly CheckBox _disableMicsInWindows;
    private readonly ListView _appList = NewList(checkBoxes: false, flexibleColumn: -1);
    private readonly Button _removeApp = DialogLayout.Button("Remove");
    private readonly CheckBox _historyEnabled;
    private readonly NumericUpDown _retention = new() { Minimum = 1, Maximum = 3650, Width = 72, Anchor = AnchorStyles.Left, TextAlign = HorizontalAlignment.Right };
    private readonly Label _historyStatus;

    /// <summary>True while the device list is being filled, when ItemChecked must not rewrite texts.</summary>
    private bool _fillingDevices;

    public SettingsForm(
        AppSettings working,
        IReadOnlyList<DeviceLockInfo> devices,
        bool locked,
        bool startWithWindows,
        Func<Hotkey, string?> tryHotkey,
        bool startupDisabledInTaskManager = false,
        HelperUiState? helper = null,
        Func<bool>? isLockedNow = null)
    {
        _startupDisabledInTaskManager = startupDisabledInTaskManager;
        _working = SettingsStore.Clone(working);
        _locked = locked;
        _isLockedNow = isLockedNow;
        _tryHotkey = tryHotkey;
        _originalHotkey = _working.GetHotkey();
        _allowedApps = [.. _working.AllowedApps];
        StartWithWindows = startWithWindows;
        Result = _working;

        _font = Theme.BodyFont;
        _hotkeyError = DialogLayout.Text(string.Empty, margin: new Padding(0, 0, 0, 6));
        _hotkeyError.ForeColor = UiPalette.ErrorText;
        _hotkeyError.Visible = false;
        _autoLockOnWindowsLock = DialogLayout.Check("Lock when I lock Windows (Win+L)", _working.AutoLockOnWindowsLock);
        _autoLockOnSleep = DialogLayout.Check("Lock when the PC goes to sleep", _working.AutoLockOnSleep);
        _playSounds = DialogLayout.Check("Play a sound when locking and unlocking", _working.PlaySounds);
        _startWithWindows = DialogLayout.Check("Start BlindsDown when I sign in to Windows", startWithWindows);
        _alertsEnabled = DialogLayout.Check("Show a popup when an app starts using a microphone or camera", _working.AlertsEnabled);
        _alertOnStop = Indented(DialogLayout.Check("Also show a popup when it stops", _working.AlertOnStop));
        _showOverlay = DialogLayout.Check("Show a lock indicator on every screen while locked", _working.ShowOverlay);
        _showEdges = DialogLayout.Check("Red lines along the screen edges while locked", _working.ShowScreenEdges);
        _hideFromCapture = DialogLayout.Check("Hide BlindsDown from screen sharing and recordings", _working.HideOverlayFromScreenCapture);
        _historyEnabled = DialogLayout.Check("Keep a history of which apps used your microphone or camera", _working.HistoryEnabled);
        _historyStatus = DialogLayout.Hint(string.Empty);
        _historyStatus.Visible = false;
        _noDevices = DialogLayout.Hint("No microphones or cameras found. Connect one and open Settings again.");
        _disableMicsInWindows = DialogLayout.Check("Turn microphones off in Windows while locked, not just mute them", _working.DisableMicsInWindows);
        _cameraTab = new CameraTab(_working);
        _cameraTab.SetState(helper ?? HelperUiState.NotInstalled);

        SuspendLayout();
        DialogLayout.Initialize(this, "BlindsDown Settings", _font);

        // Property-sheet look: grey around the tabs, so the tab pages stand out.
        BackColor = SystemColors.Control;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        // Wide enough for the device list's five columns, short enough for a 1280×720 screen at 100 %. At the
        // minimum size the device list still shows a handful of rows above the stronger-lock section.
        ClientSize = new Size(840, 600);
        MinimumSize = new Size(600, 580);

        BuildGeneralTab();
        BuildCameraTab();
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

    /// <summary>"Turn on camera lock" (install the helper), after the user confirmed
    /// <see cref="Dialogs.ConfirmInstallHelper"/>; "Lock cameras" is ticked too. The host shows the UAC prompt and
    /// then calls <see cref="SetHelperState"/>; the dialog stays open meanwhile.</summary>
    public event Action? EnableHelperRequested;

    /// <summary>"Update helper", or "Repair" for a helper that isn't responding (both reinstall it; no question
    /// first, the UAC prompt is the confirmation).</summary>
    public event Action? UpdateHelperRequested;

    /// <summary>"Remove helper", after the user confirmed <see cref="Dialogs.ConfirmRemoveHelper"/>.</summary>
    public event Action? RemoveHelperRequested;

    /// <summary>
    /// Refreshes the Camera tab after an install, update or removal (or to show <see cref="HelperAvailability.Busy"/>
    /// while one runs), and the Devices list when <paramref name="devices"/> (microphones, cameras and microphone
    /// hardware) is given. Nothing the user is editing is lost: the camera options keep their checkboxes, and the
    /// device list keeps the exclusions as currently ticked. Safe to call after the window closed (does nothing).
    /// UI thread only.
    /// </summary>
    public void SetHelperState(HelperUiState state, IReadOnlyList<DeviceLockInfo>? devices = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (IsDisposed)
        {
            return;
        }

        _cameraTab.SetState(state);
        if (devices is null)
        {
            RefreshProtectionTexts();
            return;
        }

        FillDeviceList(devices, CurrentExclusions());
        FitColumns();
    }

    /// <summary>The edited settings (valid after DialogResult.OK).</summary>
    public AppSettings Result { get; private set; }

    public bool StartWithWindows { get; private set; }

    /// <summary>The device list and the Camera tab, so tests can make edits without showing the window.</summary>
    internal ListView DeviceList => _deviceList;

    internal CameraTab Camera => _cameraTab;

    /// <summary>The History tab's retention box, so tests can check it without showing the window.</summary>
    internal NumericUpDown Retention => _retention;

    /// <summary>The hotkey error under the hotkey box ("" when there is none).</summary>
    internal string HotkeyError => _hotkeyError.Text;

    /// <summary>Whether BlindsDown is locked right now (the "Remove helper" question says whether locked cameras
    /// come back on first): asked of the host, since the lock can change while this window is open.</summary>
    internal bool LockedNow => _isLockedNow?.Invoke() ?? _locked;

    /// <summary>
    /// True while the hotkey box has the keyboard focus in this window and this window is the active one: the user
    /// is entering a combination. The global hotkey stays registered while Settings is open, and Windows hands a
    /// registered combination to its owner instead of the focused box, so the host offers such a press to the box
    /// (<see cref="OfferCapturedHotkey"/>) instead of toggling the lock. UI thread only.
    /// </summary>
    public bool IsHotkeyBoxCapturing => !IsDisposed && Visible && ActiveForm == this && _hotkeyBox.Focused;

    /// <summary>
    /// The current global hotkey was pressed while <see cref="IsHotkeyBoxCapturing"/>: puts that combination into the
    /// hotkey box, as if the box had received it, and clears any hotkey error. UI thread only.
    /// </summary>
    public void OfferCapturedHotkey(Hotkey hotkey)
    {
        if (IsDisposed)
        {
            return;
        }

        _hotkeyBox.TakeHotkey(hotkey);
        HideHotkeyError();
    }

    /// <summary>Shows a tab by its title ("General", "Camera", "Indicator", "Devices", "Alerts", "History").</summary>
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
            EnableHelperRequested = null;
            UpdateHelperRequested = null;
            RemoveHelperRequested = null;
            _font.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildGeneralTab()
    {
        TableLayoutPanel stack = DialogLayout.Stack(Padding.Empty);
        DialogLayout.AddRow(stack, DialogLayout.Heading("Hotkey", first: true));
        DialogLayout.AddRow(stack, DialogLayout.Text("Press it anywhere in Windows to lock or unlock."));
        _hotkeyBox.Value = _originalHotkey;
        _hotkeyBox.HotkeyChanged += _ => HideHotkeyError();
        DialogLayout.AddRow(stack, _hotkeyBox);
        DialogLayout.AddRow(stack, _hotkeyError);
        DialogLayout.AddRow(stack, DialogLayout.Hint("Click the box, then press the new combination. Esc cancels, Backspace clears. F13–F24 work well with macro keys."));

        DialogLayout.AddRow(stack, DialogLayout.Heading("Automatic lock"));
        DialogLayout.AddRow(stack, _autoLockOnWindowsLock);
        DialogLayout.AddRow(stack, _autoLockOnSleep);
        DialogLayout.AddRow(stack, DialogLayout.Hint("Unlocking is always up to you."));

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
        DialogLayout.AddRow(stack, DialogLayout.Hint(LockedPopupsHint));
        _alertsEnabled.CheckedChanged += (_, _) => _alertOnStop.Enabled = _alertsEnabled.Checked;
        _alertOnStop.Enabled = _alertsEnabled.Checked;

        _generalTab.Controls.Add(stack);
        _tabs.TabPages.Add(_generalTab);
    }

    private void BuildCameraTab()
    {
        TabPage page = Page("Camera");
        page.Controls.Add(_cameraTab.Content);
        _cameraTab.OptionsChanged += RefreshProtectionTexts;
        _cameraTab.EnableClicked += () =>
        {
            if (Dialogs.ConfirmInstallHelper(this))
            {
                // "Turn on camera lock" means cameras are locked from now on, even if "Lock cameras" was unticked
                // before an earlier removal.
                _cameraTab.LockCameras.Checked = true;
                EnableHelperRequested?.Invoke();
            }
        };
        _cameraTab.FixClicked += () =>
        {
            // Update and Repair need no question (the UAC prompt is the confirmation), but replacing a helper from a
            // newer BlindsDown with this older one does: an old copy of the app must not downgrade it unnoticed.
            if (_cameraTab.State.Availability != HelperAvailability.NeedsUpdate
                || _cameraTab.VersionAge != HelperVersionAge.Newer
                || Dialogs.ConfirmReplaceNewerHelper(_cameraTab.HelperVersion, _cameraTab.AppVersionText, this))
            {
                UpdateHelperRequested?.Invoke();
            }
        };
        _cameraTab.RemoveClicked += () =>
        {
            if (Dialogs.ConfirmRemoveHelper(LockedNow, this))
            {
                RemoveHelperRequested?.Invoke();
            }
        };
        _tabs.TabPages.Add(page);
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

        DialogLayout.AddRow(table, DialogLayout.Text("BlindsDown locks every working microphone and, with camera lock on, your cameras. Exclude a device to leave it alone while locked."));
        if (_locked)
        {
            DialogLayout.AddRow(table, DialogLayout.Banner(
                "Exclusions can't be changed while locked. Unlock first, then change them here.",
                AlertSeverity.Warning));
        }

        // While locked the list stays readable (its protection column matters most then) but read-only:
        // no checkboxes, the exclusion shown as text. A disabled ListView would grey out (and in dark
        // mode hide) exactly the information the user is looking for.
        _deviceList.CheckBoxes = !_locked;
        _deviceList.Columns.Add("Exclude");
        _deviceList.Columns.Add("Device");
        _deviceList.Columns.Add("Kind");
        _deviceList.Columns.Add("Type");
        _deviceList.Columns.Add("Protection");
        _deviceList.AccessibleName = _locked
            ? "Microphones, cameras and how each one is protected"
            : "Microphones and cameras. Check a device to exclude it from the lock.";
        _deviceList.ItemChecked += (_, e) => OnDeviceChecked(e.Item);

        DialogLayout.AddRow(table, _noDevices);
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        table.RowCount = table.RowStyles.Count;
        _deviceList.Dock = DockStyle.Fill;
        _deviceList.Margin = new Padding(0, 4, 0, 8);
        table.Controls.Add(_deviceList, 0, table.RowCount - 1);
        FillDeviceList(devices, _working.ExcludedDevices);

        DialogLayout.AddRow(table, DialogLayout.Hint("Virtual microphones (such as SteelSeries Sonar) are fed by your physical ones, so locking those silences them too."));

        DialogLayout.AddRow(table, DialogLayout.Heading("Stronger microphone lock"));
        DialogLayout.AddRow(table, _disableMicsInWindows);
        _disableMicsInWindows.CheckedChanged += (_, _) => RefreshProtectionTexts();
        string stronger = "Apps that have a microphone open lose it, like when it is unplugged; some need it picked again after unlocking. "
            + "Virtual microphones are only muted, and a default microphone stays on while another one isn't locked, so Windows can't switch apps to that one.";
        DialogLayout.AddRow(table, Indented(DialogLayout.Hint(_locked ? stronger + " Changes apply the next time you lock." : stronger, Padding.Empty)));

        page.Controls.Add(table);
        _tabs.TabPages.Add(page);
    }

    /// <summary>
    /// (Re)fills the device list: microphones first, then cameras, then microphone hardware; physical before
    /// virtual; then by name. <paramref name="exclusions"/> decides the checkboxes (the saved ones at first,
    /// the ones currently ticked when the list is refreshed), and exclusions without a connected device stay
    /// as their own rows so they can still be removed.
    /// </summary>
    private void FillDeviceList(IReadOnlyList<DeviceLockInfo> devices, IReadOnlyList<DeviceExclusion> exclusions)
    {
        LockPreview preview = Preview();
        _fillingDevices = true;
        _deviceList.BeginUpdate();
        try
        {
            _deviceList.Items.Clear();
            IEnumerable<DeviceLockInfo> ordered = devices
                .OrderBy(d => d.Kind)
                .ThenBy(d => d.IsVirtual)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (DeviceLockInfo device in ordered)
            {
                bool excluded = DeviceExclusions.IsExcluded(exclusions, device, devices);
                var item = new ListViewItem(_locked && excluded ? "Yes" : string.Empty)
                {
                    Tag = device,
                    Checked = excluded,
                };
                item.SubItems.Add(device.Name);
                item.SubItems.Add(DeviceText.Kind(device.Kind));
                item.SubItems.Add(DeviceText.Type(device));
                ListViewItem.ListViewSubItem protection = item.SubItems.Add(string.Empty);
                ShowProtection(item, device, preview);
                if (device.Problem is not null)
                {
                    // A device the lock could not protect must stand out.
                    item.UseItemStyleForSubItems = false;
                    protection.ForeColor = UiPalette.ErrorText;
                }

                _deviceList.Items.Add(item);
            }

            foreach (DeviceExclusion orphan in DeviceExclusions.Orphans(exclusions, devices))
            {
                var item = new ListViewItem(_locked ? "Yes" : string.Empty) { Tag = orphan, Checked = true, ToolTipText = orphan.DisplayName + "\nNot connected" };
                item.SubItems.Add(orphan.DisplayName);
                item.SubItems.Add(DeviceExclusions.KindOf(orphan) is DeviceKind kind ? DeviceText.Kind(kind) : DeviceText.Empty);
                item.SubItems.Add(DeviceText.Empty);
                item.SubItems.Add("Not connected");
                _deviceList.Items.Add(item);
            }
        }
        finally
        {
            _deviceList.EndUpdate();
            _fillingDevices = false;
        }

        bool any = _deviceList.Items.Count > 0;
        _deviceList.Visible = any;
        _noDevices.Visible = !any;
    }

    /// <summary>The exclusions as the Devices tab shows them now: what OK would save.</summary>
    private List<DeviceExclusion> CurrentExclusions()
    {
        if (_locked)
        {
            return _working.ExcludedDevices;
        }

        List<ListViewItem> checkedItems = _deviceList.Items.Cast<ListViewItem>().Where(i => i.Checked).ToList();
        return DeviceExclusions.Build(
            checkedItems.Select(i => i.Tag).OfType<DeviceLockInfo>(),
            checkedItems.Select(i => i.Tag).OfType<DeviceExclusion>());
    }

    /// <summary>What the next lock would do follows the options being edited (Camera tab, stronger microphone lock).</summary>
    private LockPreview Preview() =>
        new(
            _cameraTab.HelperAvailable,
            _cameraTab.LockCameras.Checked,
            _cameraTab.IncludeInfrared.Checked,
            _cameraTab.IncludeVirtual.Checked,
            _cameraTab.DisableMicHardware.Checked,
            _disableMicsInWindows.Checked);

    private void OnDeviceChecked(ListViewItem item)
    {
        if (_fillingDevices || _locked || item.Tag is not DeviceLockInfo device)
        {
            return;
        }

        ShowProtection(item, device, Preview());
    }

    /// <summary>The Protection cell, and the row's tooltip with the name and protection in full (both columns can
    /// be cut short in a narrow window).</summary>
    private void ShowProtection(ListViewItem item, DeviceLockInfo device, LockPreview preview)
    {
        string protection = DeviceText.Protection(device, _locked, preview, item.Checked);
        item.SubItems[ProtectionColumn].Text = protection;
        item.ToolTipText = device.Name + "\n" + protection;
    }

    private void RefreshProtectionTexts()
    {
        LockPreview preview = Preview();
        _deviceList.BeginUpdate();
        try
        {
            foreach (ListViewItem item in _deviceList.Items)
            {
                if (item.Tag is DeviceLockInfo device)
                {
                    ShowProtection(item, device, preview);
                }
            }
        }
        finally
        {
            _deviceList.EndUpdate();
        }

        ListViews.FitColumns(_deviceList, DeviceNameColumn);
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
        DialogLayout.AddRow(table, DialogLayout.Text(AllowedAppsText));

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
        DialogLayout.AddRow(table, DialogLayout.Hint($"To add an app, open Activity from the tray menu, right-click the app and choose \"{ActivityForm.AllowText}\".", new Padding(0)));

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
            new Padding(20, 2, 0, 4),
            DialogLayout.Caption("Keep entries for", new Padding(0, 0, 6, 0)),
            _retention,
            DialogLayout.Caption("days", new Padding(6, 0, 0, 0)));
        DialogLayout.AddRow(stack, retentionRow);

        // The retention stays editable with history off: the lock timeline is always recorded and pruned with it.
        DialogLayout.AddRow(stack, Indented(DialogLayout.Hint(LockTimelineHint, new Padding(0, 0, 0, 8))));

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

    /// <summary>OK: validates the hotkey, then collects every tab into <see cref="Result"/>. Internal so tests can
    /// press OK without showing the window.</summary>
    internal void Accept()
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
        _working.AutoLockOnWindowsLock = _autoLockOnWindowsLock.Checked;
        _working.AutoLockOnSleep = _autoLockOnSleep.Checked;
        _working.DisableMicsInWindows = _disableMicsInWindows.Checked;
        _cameraTab.ApplyTo(_working);
        _working.ExcludedDevices = CurrentExclusions();

        Result = _working;
        StartWithWindows = _startWithWindows.Checked;
        DialogResult = DialogResult.OK;
    }

    private void HideHotkeyError()
    {
        _hotkeyError.Visible = false;
        _hotkeyError.Text = string.Empty;
    }

    private void ConfirmClearHistory()
    {
        var clearButton = new TaskDialogButton("Clear history");
        var page = new TaskDialogPage
        {
            Caption = "BlindsDown",
            Heading = "Clear all activity history?",
            Text = "This deletes the record of which apps used your microphone or camera, and when BlindsDown was locked. It can't be undone.",
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
