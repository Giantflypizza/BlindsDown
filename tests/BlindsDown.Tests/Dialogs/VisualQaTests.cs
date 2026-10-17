using System.Drawing.Imaging;
using BlindsDown.Ui;
using BlindsDown.Ui.Forms;
using static BlindsDown.Tests.Dialogs.DialogsQaHarness;

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

        foreach (string tab in new[] { "General", "Indicator", "Devices", "Alerts", "History" })
        {
            using var settings = new SettingsForm(Samples.Settings(), Samples.Devices(locked: false), locked: false, startWithWindows: true, _ => null);
            Render(settings, dpi, P("settings-" + tab.ToLowerInvariant()), f => ((SettingsForm)f).SelectTab(tab));
            paths.Add(P("settings-" + tab.ToLowerInvariant()));
        }

        using (var locked = new SettingsForm(Samples.Settings(), Samples.Devices(locked: true), locked: true, startWithWindows: false, _ => null))
        {
            Render(locked, dpi, P("settings-devices-locked"), f => ((SettingsForm)f).SelectTab("Devices"));
            paths.Add(P("settings-devices-locked"));
        }

        using (var activity = new ActivityForm())
        {
            activity.SetData(Samples.Active(), Samples.History());
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
}
