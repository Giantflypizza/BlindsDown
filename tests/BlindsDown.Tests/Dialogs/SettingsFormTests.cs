using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Ui;
using BlindsDown.Ui.Forms;
using static BlindsDown.Tests.Dialogs.DialogsQaHarness;

namespace BlindsDown.Tests.Dialogs;

/// <summary>
/// Builds the Settings and Welcome windows without ever showing them (no window handle, nothing on screen), and
/// checks what OK would save: every new option round-trips, exclusions work for cameras and microphone hardware,
/// and a new helper state or device list never loses what the user was editing.
/// </summary>
[Collection(DesktopWindowsCollection.Name)]
public sealed class SettingsFormTests
{
    [Fact]
    public void EveryHelperStateBuilds()
    {
        RunSta(() =>
        {
            foreach (HelperUiState state in Samples.HelperStates())
            {
                using var form = new SettingsForm(Samples.Settings(), Samples.Devices(locked: false), locked: false, startWithWindows: true, _ => null, helper: state);
                form.SetHelperState(state, Samples.Devices(locked: true));
                Assert.Equal(state, form.Camera.State);
            }

            using var noHelper = new SettingsForm(Samples.Settings(), [], locked: true, startWithWindows: true, _ => null);
            Assert.Equal(HelperAvailability.NotInstalled, noHelper.Camera.State.Availability);
        });
    }

    [Fact]
    public void NewOptionsRoundTrip()
    {
        RunSta(() =>
        {
            AppSettings settings = Samples.Settings();
            settings.AutoLockOnWindowsLock = true;
            settings.AutoLockOnSleep = true;
            settings.DisableMicsInWindows = true;
            settings.LockCameras = false;
            settings.IncludeInfraredCameras = true;
            settings.IncludeVirtualCameras = true;
            settings.DisableMicOnlyHardware = true;

            using var form = new SettingsForm(settings, Samples.Devices(locked: false), locked: false, startWithWindows: true, _ => null, helper: new HelperUiState(HelperAvailability.Working));
            form.Accept();

            Assert.Equal(DialogResult.OK, form.DialogResult);
            AppSettings result = form.Result;
            Assert.True(result.AutoLockOnWindowsLock);
            Assert.True(result.AutoLockOnSleep);
            Assert.True(result.DisableMicsInWindows);
            Assert.False(result.LockCameras);
            Assert.True(result.IncludeInfraredCameras);
            Assert.True(result.IncludeVirtualCameras);
            Assert.True(result.DisableMicOnlyHardware);
        });
    }

    [Fact]
    public void ExclusionsOfEveryKindAreKeptAsTheyWere()
    {
        RunSta(() =>
        {
            AppSettings settings = Samples.Settings();
            using var form = new SettingsForm(settings, Samples.Devices(locked: false), locked: false, startWithWindows: true, _ => null);
            form.Accept();

            // The camera exclusion keeps its instance id, the unplugged ones stay, nothing is added.
            Assert.Equal(
                settings.ExcludedDevices.Select(e => e.StableKey).Order(StringComparer.Ordinal),
                form.Result.ExcludedDevices.Select(e => e.StableKey).Order(StringComparer.Ordinal));
            DeviceExclusion camera = Assert.Single(form.Result.ExcludedDevices, e => e.StableKey.StartsWith("cam|USB\\VID_0FD9", StringComparison.Ordinal));
            Assert.Equal(@"USB\VID_0FD9&PID_0078&MI_00\8&3D&0&0000", camera.LastKnownId);
        });
    }

    [Fact]
    public void TickingACameraExcludesItByInstanceIdAndStableKey()
    {
        RunSta(() =>
        {
            using var form = new SettingsForm(new AppSettings(), Samples.Devices(locked: false), locked: false, startWithWindows: true, _ => null);
            ListViewItem integrated = Row(form, "Integrated Camera");
            ListViewItem hardware = Row(form, "Logitech BRIO (audio)");
            integrated.Checked = true;
            hardware.Checked = true;
            form.Accept();

            var device = (DeviceLockInfo)integrated.Tag!;
            var mic = (DeviceLockInfo)hardware.Tag!;
            Assert.Equal(
                [
                    new DeviceExclusion { StableKey = device.StableKey, LastKnownId = device.Id, DisplayName = device.Name },
                    new DeviceExclusion { StableKey = mic.StableKey, LastKnownId = mic.Id, DisplayName = mic.Name },
                ],
                form.Result.ExcludedDevices);
        });
    }

    [Fact]
    public void ANewHelperStateAndDeviceListKeepUnsavedEdits()
    {
        RunSta(() =>
        {
            using var form = new SettingsForm(Samples.Settings(), Samples.Devices(locked: false), locked: false, startWithWindows: true, _ => null);
            form.SetHelperState(new HelperUiState(HelperAvailability.Busy, Detail: "Installing…"));

            // Edits while the install runs: exclude the colour camera, un-exclude the Elgato, change options.
            Row(form, "Integrated Camera").Checked = true;
            Row(form, "Elgato Facecam").Checked = false;
            form.Camera.IncludeInfrared.Checked = true;

            // The install finished: the helper reports its devices (one more camera appeared).
            DeviceLockInfo newCamera = new(@"USB\VID_9999&PID_0001&MI_00\1", "New Webcam", @"cam|USB\VID_9999&PID_0001&MI_00|NEW WEBCAM", DeviceKind.Camera, false, false, DeviceProtection.None, null, CameraType.Color);
            form.SetHelperState(new HelperUiState(HelperAvailability.Working, "0.2.0"), [.. Samples.Devices(locked: false), newCamera]);

            Assert.True(Row(form, "Integrated Camera").Checked);
            Assert.False(Row(form, "Elgato Facecam").Checked);
            Assert.False(Row(form, "New Webcam").Checked);
            Assert.True(form.Camera.IncludeInfrared.Checked);
            Assert.True(form.Camera.HelperAvailable);

            form.Accept();
            Assert.Contains(form.Result.ExcludedDevices, e => e.DisplayName == "Integrated Camera");
            Assert.DoesNotContain(form.Result.ExcludedDevices, e => e.DisplayName == "Elgato Facecam");
            Assert.True(form.Result.IncludeInfraredCameras);
        });
    }

    [Fact]
    public void ADeviceThatComesBackPicksUpItsSavedExclusion()
    {
        RunSta(() =>
        {
            // "USB Camera" is excluded but not connected: an orphan row until the helper lists it.
            using var form = new SettingsForm(Samples.Settings(), Samples.Devices(locked: false), locked: false, startWithWindows: true, _ => null);
            Assert.IsType<DeviceExclusion>(Row(form, "USB Camera").Tag);

            DeviceLockInfo back = new(@"USB\VID_1BCF&PID_2284&MI_00\5&1", "USB Camera", @"cam|USB\VID_1BCF&PID_2284&MI_00|USB CAMERA", DeviceKind.Camera, false, false, DeviceProtection.Excluded, null, CameraType.Color);
            form.SetHelperState(new HelperUiState(HelperAvailability.Working), [.. Samples.Devices(locked: false), back]);

            ListViewItem row = Row(form, "USB Camera");
            Assert.IsType<DeviceLockInfo>(row.Tag);
            Assert.True(row.Checked);
            Assert.Equal("Excluded", row.SubItems[4].Text);
        });
    }

    [Fact]
    public void WhileLockedExclusionsAreReadOnlyButTheOptionsAreNot()
    {
        RunSta(() =>
        {
            AppSettings settings = Samples.Settings();
            using var form = new SettingsForm(settings, Samples.Devices(locked: true), locked: true, startWithWindows: true, _ => null, helper: new HelperUiState(HelperAvailability.Working));
            Assert.False(form.DeviceList.CheckBoxes);
            Assert.Equal("Yes", Row(form, "Elgato Facecam").Text);
            Assert.Equal("Turned off (device disabled)", Row(form, "Integrated Camera").SubItems[4].Text);

            form.Camera.DisableMicHardware.Checked = true;
            form.Accept();

            Assert.Equal(settings.ExcludedDevices, form.Result.ExcludedDevices);
            Assert.True(form.Result.DisableMicOnlyHardware);
        });
    }

    [Fact]
    public void TheDevicesTabPreviewsWhatTheNextLockDoes()
    {
        RunSta(() =>
        {
            using var form = new SettingsForm(new AppSettings(), Samples.Devices(locked: false), locked: false, startWithWindows: true, _ => null);
            Assert.Equal(DeviceText.NeedsHelper, Row(form, "Integrated Camera").SubItems[4].Text);

            form.SetHelperState(new HelperUiState(HelperAvailability.Working));
            Assert.Equal("Turned off while locked", Row(form, "Integrated Camera").SubItems[4].Text);
            Assert.Equal("Left on: infrared cameras aren't locked", Row(form, "Integrated IR Camera").SubItems[4].Text);

            form.Camera.IncludeInfrared.Checked = true;
            Assert.Equal("Turned off while locked", Row(form, "Integrated IR Camera").SubItems[4].Text);
            Assert.Contains("Integrated IR Camera", Row(form, "Integrated IR Camera").ToolTipText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SetHelperStateAfterTheWindowClosedDoesNothing()
    {
        RunSta(() =>
        {
            var form = new SettingsForm(new AppSettings(), [], locked: false, startWithWindows: true, _ => null);
            form.Dispose();
            form.SetHelperState(new HelperUiState(HelperAvailability.Working), Samples.Devices(locked: false));
        });
    }

    [Fact]
    public void CameraTab_BusyKeepsTheLastLayoutAndDisablesTheButtons()
    {
        RunSta(() =>
        {
            var tab = new CameraTab(new AppSettings());
            Assert.False(tab.HelperAvailable);

            tab.SetState(new HelperUiState(HelperAvailability.NeedsUpdate, "0.1.9"));
            Assert.True(tab.HelperAvailable);

            tab.SetState(new HelperUiState(HelperAvailability.Busy));
            Assert.True(tab.HelperAvailable); // still the old helper until the update finishes
            Assert.All(Buttons(tab.Content), b => Assert.False(b.Enabled));

            tab.SetState(new HelperUiState(HelperAvailability.NotResponding));
            Assert.False(tab.HelperAvailable);
            Assert.Contains(Buttons(tab.Content), b => b.Text == "Repair" && b.Enabled);
            tab.Content.Dispose();
        });
    }

    [Fact]
    public void TheRemoveQuestionAsksForTheLockStateWhenItIsShown()
    {
        RunSta(() =>
        {
            bool lockedNow = false;
            using var form = new SettingsForm(new AppSettings(), [], locked: false, startWithWindows: true, _ => null, isLockedNow: () => lockedNow);
            Assert.False(form.LockedNow);

            // Locked from the tray (or automatically) while Settings is open: removing must say cameras come back on.
            lockedNow = true;
            Assert.True(form.LockedNow);

            using var withoutHost = new SettingsForm(new AppSettings(), [], locked: true, startWithWindows: true, _ => null);
            Assert.True(withoutHost.LockedNow);
        });
    }

    [Fact]
    public void CameraTab_StatusFollowsLockCamerasAndKeepsTheVersionWhileBusy()
    {
        RunSta(() =>
        {
            var tab = new CameraTab(new AppSettings { LockCameras = true });
            tab.SetState(new HelperUiState(HelperAvailability.Working, "0.2.0"));
            Assert.Contains(Labels(tab.Content), l => l.Text == "Camera lock is on");

            tab.LockCameras.Checked = false;
            Assert.Contains(Labels(tab.Content), l => l.Text == "Camera lock is off");
            Assert.DoesNotContain(Labels(tab.Content), l => l.Text == "Camera lock is on");

            tab.LockCameras.Checked = true;
            tab.SetState(new HelperUiState(HelperAvailability.Busy, Detail: "Updating the camera helper…"));
            Assert.Contains(Labels(tab.Content), l => l.Text == "Camera lock is on");
            Assert.Contains(Labels(tab.Content), l => l.Text == "Camera helper version 0.2.0.");
            tab.Content.Dispose();
        });
    }

    [Fact]
    public void CameraTab_InfraredAndVirtualOnlyMatterWhenCamerasAreLocked()
    {
        RunSta(() =>
        {
            var tab = new CameraTab(new AppSettings { LockCameras = true });
            int changes = 0;
            tab.OptionsChanged += () => changes++;

            tab.LockCameras.Checked = false;
            Assert.False(tab.IncludeInfrared.Enabled);
            Assert.False(tab.IncludeVirtual.Enabled);
            Assert.Equal(1, changes);

            var settings = new AppSettings();
            tab.ApplyTo(settings);
            Assert.False(settings.LockCameras);
            tab.Content.Dispose();
        });
    }

    [Fact]
    public void CameraTab_AHelperFromANewerBlindsDownIsReplacedNotUpdated()
    {
        RunSta(() =>
        {
            var tab = new CameraTab(new AppSettings(), appVersion: "1.0.0");
            tab.SetState(new HelperUiState(HelperAvailability.NeedsUpdate, "1.1.0"));
            Assert.Equal(HelperVersionAge.Newer, tab.VersionAge);
            Assert.Contains(Buttons(tab.Content), b => b.Text == "Replace helper");
            Assert.Contains(Labels(tab.Content), l => l.Text == "The camera helper is from a newer BlindsDown");
            Assert.DoesNotContain(Labels(tab.Content), l => l.Text.Contains("is older than", StringComparison.Ordinal));

            tab.SetState(new HelperUiState(HelperAvailability.NeedsUpdate, "0.9.0"));
            Assert.Equal(HelperVersionAge.Older, tab.VersionAge);
            Assert.Contains(Buttons(tab.Content), b => b.Text == "Update helper");
            Assert.Contains(Labels(tab.Content), l => l.Text.StartsWith("The installed helper (version 0.9.0) is older than this BlindsDown (version 1.0.0).", StringComparison.Ordinal));
            tab.Content.Dispose();
        });
    }

    [Fact]
    public void ACapturedHotkeyGoesIntoTheBoxAndClearsTheError()
    {
        RunSta(() =>
        {
            var taken = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'K');
            var free = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'L');
            var tried = new List<Hotkey>();
            using var form = new SettingsForm(new AppSettings(), [], locked: false, startWithWindows: true, hotkey =>
            {
                tried.Add(hotkey);
                return hotkey == taken ? "Another app already uses this combination." : null;
            });

            // Never shown, so the box has no focus: a press of the global hotkey would toggle the lock as usual.
            Assert.False(form.IsHotkeyBoxCapturing);

            form.OfferCapturedHotkey(taken);
            form.Accept();
            Assert.NotEqual(DialogResult.OK, form.DialogResult);
            Assert.Equal("Another app already uses this combination.", form.HotkeyError);

            form.OfferCapturedHotkey(free);
            Assert.Equal(string.Empty, form.HotkeyError);
            form.Accept();
            Assert.Equal(DialogResult.OK, form.DialogResult);
            Assert.Equal(free.ToString(), form.Result.Hotkey);
            Assert.Equal([taken, free], tried);
        });
    }

    [Fact]
    public void RetentionStaysEditableWithHistoryOff()
    {
        RunSta(() =>
        {
            // The lock timeline keeps recording with history off, and is pruned with the same retention.
            using var form = new SettingsForm(new AppSettings { HistoryEnabled = false, HistoryRetentionDays = 30 }, [], locked: false, startWithWindows: true, _ => null);
            Assert.True(form.Retention.Enabled);

            form.Retention.Value = 7;
            form.Accept();
            Assert.False(form.Result.HistoryEnabled);
            Assert.Equal(7, form.Result.HistoryRetentionDays);
        });
    }

    [Fact]
    public void AlwaysAllowIsOnlyPromisedWhileUnlocked()
    {
        Assert.StartsWith("While unlocked, these apps don't show a popup", SettingsForm.AllowedAppsText, StringComparison.Ordinal);
        Assert.Contains("While locked they still do.", SettingsForm.AllowedAppsText, StringComparison.Ordinal);
        Assert.DoesNotContain("never", SettingsForm.AllowedAppsText, StringComparison.Ordinal);
        Assert.Contains("or a camera, when cameras are locked", SettingsForm.LockedPopupsHint, StringComparison.Ordinal);
        Assert.Equal("Always allow (no popups while unlocked)", ActivityForm.AllowText);
        Assert.Contains("lock timeline", SettingsForm.LockTimelineHint, StringComparison.Ordinal);
    }

    [Fact]
    public void Welcome_OffersCamerasUncheckedAndOnlyWhenAsked()
    {
        RunSta(() =>
        {
            using (var plain = new WelcomeForm("Ctrl+Alt+Shift+M", startWithWindows: true))
            {
                Assert.False(plain.LockCameras);
                Assert.DoesNotContain(CheckBoxes(plain), c => c.Text == "Also lock my cameras");
            }

            using var offer = new WelcomeForm("Ctrl+Alt+Shift+M", startWithWindows: true, offerCameraLock: true);
            CheckBox cameras = Assert.Single(CheckBoxes(offer), c => c.Text == "Also lock my cameras");
            Assert.False(cameras.Checked);
            Assert.False(offer.LockCameras);
            cameras.Checked = true;
            Assert.True(offer.LockCameras);
        });
    }

    [Fact]
    public void Activity_TakesHundredsOfLockEvents()
    {
        RunSta(() =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<BlindsDown.Core.Usage.LockEventRecord> events = [];
            for (int i = 0; i < 600; i++)
            {
                events.Add(new() { At = now.AddMinutes(-(i * 30) - 10), Action = BlindsDown.Core.Usage.LockEventAction.Unlock, Source = BlindsDown.Core.Usage.LockEventSource.Tray });
                events.Add(new() { At = now.AddMinutes(-(i * 30) - 20), Action = BlindsDown.Core.Usage.LockEventAction.Lock, Source = BlindsDown.Core.Usage.LockEventSource.Hotkey });
            }

            using var activity = new ActivityForm();
            activity.SetData(Samples.Active(), Samples.History(), events);
            activity.SetData(Samples.Active(), Samples.History()); // keeps the timeline
            activity.SetData([], [], []);
        });
    }

    private static ListViewItem Row(SettingsForm form, string name) =>
        form.DeviceList.Items.Cast<ListViewItem>().Single(i => i.SubItems[1].Text == name);

    private static IEnumerable<Button> Buttons(Control root) => All(root).OfType<Button>();

    private static IEnumerable<CheckBox> CheckBoxes(Control root) => All(root).OfType<CheckBox>();

    private static IEnumerable<Label> Labels(Control root) => All(root).OfType<Label>();

    private static IEnumerable<Control> All(Control root) =>
        root.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));
}
