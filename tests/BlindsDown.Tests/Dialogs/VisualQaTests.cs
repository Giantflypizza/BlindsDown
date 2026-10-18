using System.Drawing.Imaging;
using BlindsDown.Core.Locking;
using BlindsDown.Ui;
using BlindsDown.Ui.Forms;
using static BlindsDown.Tests.Dialogs.DialogsQaHarness;
using UiDialogs = BlindsDown.Ui.Dialogs;

namespace BlindsDown.Tests.Dialogs;

/// <summary>
/// Renders every BlindsDown window and popup to artifacts/qa/*.png at 100 % and 150 % so their layout can
/// be checked by eye (clipping, alignment, padding). Creates real (invisible, non-activating) windows,
/// so it only runs with BLINDSDOWN_DEVICE_TESTS=1. Pass criteria are visual; the test itself only checks
/// that every image was produced.
/// </summary>
[Collection(DesktopWindowsCollection.Name)]
public sealed class VisualQaTests
{
    private static readonly int[] Dpis = [96, 144];

    [Fact]
    public void RenderEveryWindow()
    {
        Assert.SkipUnless(WindowsAllowed, $"Creates real windows; set {DeviceTestsVariable}=1 to run.");
        string dir = OutputDirectory();
        var written = new List<string>();

        RunSta(() =>
        {
            Application.EnableVisualStyles();
            foreach (int dpi in Dpis)
            {
                string scale = (dpi * 100 / 96).ToString(System.Globalization.CultureInfo.InvariantCulture);
                written.AddRange(RenderPopups(dir, dpi, scale));
                written.AddRange(RenderForms(dir, dpi, scale));
            }

            // Windows draws task dialogs itself, at the system DPI and always light.
            written.AddRange(RenderConfirmations(dir));

            // Dark mode (what Program enables on Windows 11 when the user chose dark apps).
            Application.SetColorMode(SystemColorMode.Dark);
            try
            {
                written.AddRange(RenderPopups(dir, 96, "dark-100"));
                written.AddRange(RenderForms(dir, 96, "dark-100"));
            }
            finally
            {
                Application.SetColorMode(SystemColorMode.Classic);
            }
        });

        Assert.All(written, path => Assert.True(new FileInfo(path).Length > 0, path));
    }

    private static List<string> RenderPopups(string dir, int dpi, string scale)
    {
        var paths = new List<string>();
        IReadOnlyList<AlertModel> models = Samples.Popups();
        for (int i = 0; i < models.Count; i++)
        {
            string path = Path.Combine(dir, $"popup-{models[i].Severity.ToString().ToLowerInvariant()}-{scale}.png");
            using Bitmap bitmap = AlertPopupForm.Render(models[i], dpi, drawOutline: true);
            bitmap.Save(path, ImageFormat.Png);
            paths.Add(path);
        }

        paths.Add(RenderStack(dir, dpi, scale));
        return paths;
    }

    /// <summary>Composes the bottom-right stack (4 visible + pill) on a plain backdrop, using the real stacking rules.</summary>
    private static string RenderStack(string dir, int dpi, string scale)
    {
        IReadOnlyList<AlertModel> models = [.. Samples.Popups(), new AlertModel("Zoom is using your microphone", "Microphone (Arctis Nova Pro Wireless).", AlertSeverity.Info)];
        var bitmaps = models.Select(m => AlertPopupForm.Render(m, dpi, drawOutline: true)).ToList();
        using var pill = new MorePillForm();
        Size pillSize = pill.SetCount(1, dpi);
        var work = new Rectangle(0, 0, (int)(520 * dpi / 96f), (int)(1000 * dpi / 96f));
        List<Bitmap> newestFirst = Enumerable.Reverse(bitmaps).ToList();
        AlertStackPlacement placement = AlertStackLayout.Compute(work, newestFirst.ConvertAll(b => b.Size), pillSize, 12 * dpi / 96, 8 * dpi / 96);

        using var canvas = new Bitmap(work.Width, work.Height, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.FromArgb(58, 84, 120));
            for (int i = 0; i < newestFirst.Count; i++)
            {
                if (placement.Locations[i] is Point location)
                {
                    g.DrawImage(newestFirst[i], location);
                }
            }

            if (placement.PillLocation is Point pillLocation)
            {
                using var pillImage = new Bitmap(pill.Width, pill.Height, PixelFormat.Format24bppRgb);
                pill.DrawToBitmap(pillImage, new Rectangle(Point.Empty, pill.Size));
                g.DrawImage(pillImage, pillLocation);
            }
        }

        bitmaps.ForEach(b => b.Dispose());
        string path = Path.Combine(dir, $"popup-stack-{scale}.png");
        canvas.Save(path, ImageFormat.Png);
        return path;
    }

    private static List<string> RenderForms(string dir, int dpi, string scale)
    {
        var paths = new List<string>();
        string P(string name) => Path.Combine(dir, $"{name}-{scale}.png");

        var working = new HelperUiState(HelperAvailability.Working, "0.2.0");
        foreach (string tab in new[] { "General", "Camera", "Indicator", "Devices", "Alerts", "History" })
        {
            using var settings = new SettingsForm(Samples.Settings(), Samples.Devices(locked: false), locked: false, startWithWindows: true, _ => null, helper: working);
            Render(settings, dpi, P("settings-" + tab.ToLowerInvariant()), f => ((SettingsForm)f).SelectTab(tab));
            paths.Add(P("settings-" + tab.ToLowerInvariant()));
        }

        // Every helper state, switched in place on one window (as the app does while Settings stays open).
        using (var camera = new SettingsForm(Samples.Settings(), Samples.Devices(locked: false), locked: false, startWithWindows: true, _ => null))
        {
            IReadOnlyList<HelperUiState> states = Samples.HelperStates();
            for (int i = 0; i < states.Count; i++)
            {
                HelperUiState state = states[i];
                string name = $"settings-camera-{i}-{state.Availability.ToString().ToLowerInvariant()}";
                Render(camera, dpi, P(name), f =>
                {
                    var form = (SettingsForm)f;
                    form.SetHelperState(state);
                    form.SelectTab("Camera");
                });
                paths.Add(P(name));
            }

            // A working helper with "Lock cameras" unticked says camera lock is off.
            Render(camera, dpi, P("settings-camera-lockoff"), f =>
            {
                var form = (SettingsForm)f;
                form.SetHelperState(new HelperUiState(HelperAvailability.Working, "0.2.0"));
                form.Camera.LockCameras.Checked = false;
                form.SelectTab("Camera");
            });
            paths.Add(P("settings-camera-lockoff"));
            camera.Camera.LockCameras.Checked = true;

            // Without the helper, the Devices tab says which devices need it.
            Render(camera, dpi, P("settings-devices-nohelper"), f =>
            {
                var form = (SettingsForm)f;
                form.SetHelperState(HelperUiState.NotInstalled, [.. Samples.Devices(locked: false).Where(d => d.Kind == DeviceKind.Microphone), .. Samples.Cameras(locked: false, helper: false)]);
                form.SelectTab("Devices");
            });
            paths.Add(P("settings-devices-nohelper"));
        }

        using (var locked = new SettingsForm(Samples.Settings(), Samples.Devices(locked: true), locked: true, startWithWindows: false, _ => null, helper: working))
        {
            Render(locked, dpi, P("settings-devices-locked"), f => ((SettingsForm)f).SelectTab("Devices"));
            paths.Add(P("settings-devices-locked"));
            Render(locked, dpi, P("settings-camera-locked"), f => ((SettingsForm)f).SelectTab("Camera"));
            paths.Add(P("settings-camera-locked"));
        }

        // The smallest the window can be: everything must still wrap rather than clip.
        using (var small = new SettingsForm(Samples.Settings(), Samples.Devices(locked: false), locked: false, startWithWindows: true, _ => null, helper: working))
        {
            foreach (string tab in new[] { "General", "Camera", "Devices" })
            {
                string name = "settings-min-" + tab.ToLowerInvariant();
                Render(small, dpi, P(name), f =>
                {
                    f.Size = f.MinimumSize;
                    ((SettingsForm)f).SelectTab(tab);
                });
                paths.Add(P(name));
            }
        }

        using (var activity = new ActivityForm())
        {
            activity.SetData(Samples.Active(), Samples.History(), Samples.LockEvents());
            Render(activity, dpi, P("activity"));
            paths.Add(P("activity"));
        }

        using (var empty = new ActivityForm())
        {
            Render(empty, dpi, P("activity-empty"));
            paths.Add(P("activity-empty"));
        }

        using (var welcome = new WelcomeForm("Ctrl+Alt+Shift+M", startWithWindows: true))
        {
            Render(welcome, dpi, P("welcome"));
            paths.Add(P("welcome"));
        }

        using (var welcomeCameras = new WelcomeForm("Ctrl+Alt+Shift+M", startWithWindows: true, offerCameraLock: true))
        {
            Render(welcomeCameras, dpi, P("welcome-cameras"));
            paths.Add(P("welcome-cameras"));
        }

        using (var cameraReport = new RestoreDetailsForm(Samples.CameraReport()))
        {
            Render(cameraReport, dpi, P("restore-details-cameras"));
            paths.Add(P("restore-details-cameras"));
        }

        using (var tray = new TrayController())
        {
            var status = new LockStatusInfo(LockState.Locked, Samples.Devices(locked: true), []);
            tray.SetStatus(status, BlindsDown.Core.Settings.Hotkey.Default, cameraLockOn: true);
            paths.Add(RenderMenu(tray.Menu, P("tray-menu")));
        }

        using (var about = new AboutForm("0.1.0"))
        {
            Render(about, dpi, P("about"));
            paths.Add(P("about"));
        }

        using (var report = new RestoreDetailsForm(Samples.MixedReport()))
        {
            Render(report, dpi, P("restore-details"));
            paths.Add(P("restore-details"));
        }

        return paths;
    }

    /// <summary>The two confirmations before a UAC prompt, as Windows draws them (collapsed and with details).</summary>
    private static List<string> RenderConfirmations(string dir)
    {
        var paths = new List<string>();
        foreach ((string name, Func<TaskDialogPage> page, bool expanded) in new (string, Func<TaskDialogPage>, bool)[]
        {
            ("confirm-install", () => UiDialogs.ConfirmPage(DialogText.InstallHelper(), TaskDialogIcon.ShieldBlueBar), false),
            ("confirm-install-details", () => UiDialogs.ConfirmPage(DialogText.InstallHelper(), TaskDialogIcon.ShieldBlueBar), true),
            ("confirm-remove", () => UiDialogs.ConfirmPage(DialogText.RemoveHelper(locked: false), TaskDialogIcon.Information), false),
            ("confirm-remove-locked", () => UiDialogs.ConfirmPage(DialogText.RemoveHelper(locked: true), TaskDialogIcon.Information), true),
        })
        {
            string path = Path.Combine(dir, name + ".png");
            RenderTaskDialog(page(), path, expanded);
            paths.Add(path);
        }

        return paths;
    }

    /// <summary>Lays the tray menu out without opening it (opening would take the mouse) and draws it.</summary>
    private static string RenderMenu(ContextMenuStrip menu, string path)
    {
        menu.PerformLayout();
        menu.Size = menu.GetPreferredSize(Size.Empty);
        Save(menu, path);
        return path;
    }
}
