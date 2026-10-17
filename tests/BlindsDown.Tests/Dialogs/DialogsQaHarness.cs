using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Core.Usage;
using BlindsDown.Ui;
using BlindsDown.Ui.Forms;

namespace BlindsDown.Tests.Dialogs;

/// <summary>Tests that create real windows share Application.OpenForms, so they never run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopWindowsCollection
{
    public const string Name = "Desktop windows";
}

/// <summary>
/// Helpers for tests that create real windows: an STA thread with a message loop, rendering a form to a
/// PNG at a chosen DPI, and sample data. Forms are shown fully transparent and without activation, so the
/// user's focus is never taken. Other DPIs are emulated with the same WM_DPICHANGED message Windows sends
/// when a window moves to a monitor with a different scale, which runs WinForms' real rescaling path.
/// </summary>
internal static partial class DialogsQaHarness
{
    public const string DeviceTestsVariable = "BLINDSDOWN_DEVICE_TESTS";

    private const int SW_SHOWNOACTIVATE = 4;
    private const int WM_DPICHANGED = 0x02E0;

    public static bool WindowsAllowed => Environment.GetEnvironmentVariable(DeviceTestsVariable) == "1";

    public static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("STA test body failed", failure);
        }
    }

    /// <summary>The repository's artifacts/qa folder (git-ignored).</summary>
    public static string OutputDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BlindsDown.slnx")))
        {
            dir = dir.Parent;
        }

        string path = Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "artifacts", "qa");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Shows <paramref name="form"/> invisibly (no activation), switches it to <paramref name="dpi"/>, runs <paramref name="prepare"/> and saves a PNG.</summary>
    public static void Render(Form form, int dpi, string path, Action<Form>? prepare = null)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(60, 60);
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        _ = ShowWindow(form.Handle, SW_SHOWNOACTIVATE);
        Pump();
        if (form.DeviceDpi != dpi)
        {
            ChangeDpi(form, dpi);
        }

        prepare?.Invoke(form);
        Pump();
        Save(form, path);
        form.Hide();
    }

    public static void Save(Control control, string path)
    {
        using var bitmap = new Bitmap(control.Width, control.Height, PixelFormat.Format24bppRgb);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        bitmap.Save(path, ImageFormat.Png);
    }

    public static void Pump()
    {
        for (int i = 0; i < 5; i++)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private static unsafe void ChangeDpi(Form form, int dpi)
    {
        float factor = dpi / (float)form.DeviceDpi;
        int* rect = stackalloc int[4];
        rect[0] = form.Left;
        rect[1] = form.Top;
        rect[2] = form.Left + (int)Math.Round(form.Width * factor);
        rect[3] = form.Top + (int)Math.Round(form.Height * factor);
        _ = SendMessage(form.Handle, WM_DPICHANGED, (dpi << 16) | dpi, rect);
        Pump();
    }

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static unsafe partial nint SendMessage(nint hwnd, int message, nint wParam, int* lParam);

    public static class Samples
    {
        public static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

        public static IReadOnlyList<DeviceLockInfo> Devices(bool locked) =>
        [
            new("{0.0.1.00000000}.{a1}", "Microphone (Arctis Nova Pro Wireless)", @"mic|USB\VID_1038&PID_12E0&MI_00|MICROPHONE", DeviceKind.Microphone, false, true, locked ? DeviceProtection.HardwareMute : DeviceProtection.None, null),
            new("{0.0.1.00000000}.{b2}", "Mikrofon (Realtek(R) Audio)", @"mic|HDAUDIO\FUNC_01&VEN_10EC|MIKROFON", DeviceKind.Microphone, false, false, locked ? DeviceProtection.SoftwareMute : DeviceProtection.None, null),
            new("{0.0.1.00000000}.{c3}", "Headset (AirPods Pro Hands-Free AG Audio)", @"mic|BTHHFENUM\BTHHFPAUDIO|HEADSET", DeviceKind.Microphone, false, false, locked ? DeviceProtection.NotPresent : DeviceProtection.None, null),
            new("{0.0.1.00000000}.{d4}", "Microphone (SteelSeries Sonar - Microphone)", @"mic|SWD\SONAR|MICROPHONE", DeviceKind.Microphone, true, false, DeviceProtection.Excluded, null),
            new("{0.0.1.00000000}.{e5}", "Line In (Realtek(R) Audio)", @"mic|HDAUDIO\FUNC_01&VEN_10EC|LINE IN", DeviceKind.Microphone, false, false, DeviceProtection.DisabledByUser, null),
            new("{0.0.1.00000000}.{f6}", "Microphone (USB PnP Sound Device)", @"mic|USB\VID_0D8C&PID_0014|MICROPHONE", DeviceKind.Microphone, false, false, DeviceProtection.None, locked ? "Mute did not take effect" : null),
        ];

        public static AppSettings Settings()
        {
            var settings = new AppSettings();
            settings.ExcludedDevices.Add(new DeviceExclusion { StableKey = @"mic|SWD\SONAR|MICROPHONE", DisplayName = "Microphone (SteelSeries Sonar - Microphone)" });
            settings.ExcludedDevices.Add(new DeviceExclusion { StableKey = @"mic|USB\VID_046D&PID_0825|MICROPHONE", DisplayName = "Microphone (HD Webcam C270)" });
            settings.AllowedApps.Add(new AllowedApp { AppKey = "exe:SONAR.EXE|STEELSERIES SONAR", DisplayName = "SteelSeries Sonar" });
            settings.AllowedApps.Add(new AllowedApp { AppKey = "pkg:MICROSOFT.WINDOWSSOUNDRECORDER_8WEKYB3D8BBWE", DisplayName = "Sound Recorder" });
            return settings;
        }

        public static IReadOnlyList<UsageSession> Active() =>
        [
            Session("Discord", @"C:\Users\me\AppData\Local\Discord\app-1.0.9262\Discord.exe", "Microphone (SteelSeries Sonar - Microphone)", "SteelSeries Sonar", Now.AddMinutes(-12.4)),
            Session("Sound Recorder", null, "Microphone (Arctis Nova Pro Wireless)", null, Now.AddSeconds(-47)),
        ];

        public static IReadOnlyList<UsageRecord> History()
        {
            DateTimeOffset localMidnight = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
            return
            [
                Record("Discord", "Microphone (SteelSeries Sonar - Microphone)", "SteelSeries Sonar", localMidnight.AddHours(9).AddMinutes(2), 45, approximate: false),
                Record("Zoom Workplace", "Microphone (Arctis Nova Pro Wireless)", null, localMidnight.AddHours(8).AddMinutes(30), 12, approximate: false),
                Record("Microsoft Teams", "Mikrofon (Realtek(R) Audio)", null, localMidnight.AddDays(-1).AddHours(16).AddMinutes(5), 95, approximate: false),
                Record("Google Chrome", "Microphone (Arctis Nova Pro Wireless)", null, localMidnight.AddDays(-1).AddHours(11), 0.3, approximate: false),
                Record("Voice Access", string.Empty, null, localMidnight.AddDays(-4).AddHours(20).AddMinutes(14), 7, approximate: true),
            ];
        }

        public static RestoreReport MixedReport() =>
            new(
            [
                new RestoreLine(RestoreOutcome.Exact, "Microphone (Arctis Nova Pro Wireless)", "Unmuted, as before the lock."),
                new RestoreLine(RestoreOutcome.Exact, "Mikrofon (Realtek(R) Audio)", "Muted, as before the lock."),
                new RestoreLine(RestoreOutcome.Pending, "Headset (AirPods Pro Hands-Free AG Audio)", "Not connected right now. It will be unmuted when it is plugged back in."),
                new RestoreLine(RestoreOutcome.ChangedByOthers, "Microphone (USB PnP Sound Device)", "Was unmuted before the lock but is muted now (changed by another app or by you)."),
                new RestoreLine(RestoreOutcome.DefaultChanged, "Microphone (Arctis Nova Pro Wireless)", "The default communications microphone changed from \"Mikrofon (Realtek(R) Audio)\" to \"Microphone (Arctis Nova Pro Wireless)\"."),
            ],
            4);

        public static IReadOnlyList<AlertModel> Popups() =>
        [
            new("Microphones locked", "All 4 microphones are muted. Press Ctrl+Alt+Shift+M to unlock.", AlertSeverity.Success),
            new("Discord is using your microphone", "Microphone (SteelSeries Sonar - Microphone) via SteelSeries Sonar.", AlertSeverity.Info, Actions: [new AlertAction("Always allow", () => { }), new AlertAction("Open Activity", () => { })]),
            new("Unlocked – please check your microphones", "2 restored exactly, 1 will be restored when reconnected, 2 need attention.", AlertSeverity.Warning, Sticky: true, Actions: [new AlertAction("Details", () => { })]),
            new("Something unmuted a microphone while locked", "Something unmuted \"Microphone (Arctis Nova Pro Wireless)\" while locked. BlindsDown muted it again. If this keeps happening, check which app is doing it: some headset utilities restore their own mute state when they reconnect.", AlertSeverity.Danger, Sticky: true),
        ];

        private static UsageSession Session(string name, string? exe, string device, string? via, DateTimeOffset start) => new()
        {
            Id = Guid.NewGuid(),
            Kind = CaptureKind.Microphone,
            App = new AppIdentity(exe?.ToLowerInvariant() ?? "pkg", name, exe, exe is null ? "Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe" : null),
            DeviceId = "{0.0.1.00000000}.{x}",
            DeviceName = device,
            ViaApp = via,
            Start = start,
            Sources = ["sessions"],
        };

        private static UsageRecord Record(string name, string device, string? via, DateTimeOffset start, double minutes, bool approximate) => new()
        {
            Kind = CaptureKind.Microphone,
            AppKey = "exe:" + name.ToUpperInvariant() + ".EXE|" + name.ToUpperInvariant(),
            AppName = name,
            ExePath = @"C:\Program Files\" + name + @"\" + name + ".exe",
            DeviceName = device,
            ViaApp = via,
            Start = start,
            End = start.AddMinutes(minutes),
            Approximate = approximate,
        };
    }
}
