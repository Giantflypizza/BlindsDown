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
    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

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

    /// <summary>
    /// Shows a real task dialog, made almost fully transparent before it first appears (Windows only paints a
    /// task dialog's content while it is on a screen), captures it with PrintWindow once it has painted, and
    /// closes it. Task dialogs are drawn by Windows itself, so this is the only way to see the real thing.
    /// <paramref name="expanded"/> opens the "More details" part first.
    /// </summary>
    public static void RenderTaskDialog(TaskDialogPage page, string path, bool expanded = false)
    {
        if (expanded && page.Expander is not null)
        {
            page.Expander.Expanded = true;
        }

        Exception? failure = null;
        page.Created += (_, _) =>
        {
            nint hwnd = page.BoundDialog!.Handle;
            _ = SetWindowLongPtrW(hwnd, GWL_EXSTYLE, GetWindowLongPtrW(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
            _ = SetLayeredWindowAttributes(hwnd, 0, 1, LWA_ALPHA);
            var timer = new System.Windows.Forms.Timer { Interval = 600 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                try
                {
                    CaptureWindow(hwnd, path);
                }
                catch (Exception ex) when (ex is ExternalException or ArgumentException)
                {
                    failure = ex;
                }

                page.BoundDialog?.Close();
            };
            timer.Start();
        };

        TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
        if (failure is not null)
        {
            throw new InvalidOperationException("Capturing the task dialog failed", failure);
        }
    }

    private static void CaptureWindow(nint hwnd, string path)
    {
        _ = GetWindowRect(hwnd, out WindowRect rect);
        using var bitmap = new Bitmap(Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top), PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            nint hdc = g.GetHdc();
            try
            {
                _ = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

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

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial int SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRect(nint hwnd, out WindowRect rect);

    [LibraryImport("user32.dll")]
    private static partial int PrintWindow(nint hwnd, nint hdc, uint flags);

    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static unsafe partial nint SendMessage(nint hwnd, int message, nint wParam, int* lParam);

    public static class Samples
    {
        public static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

        public static IReadOnlyList<DeviceLockInfo> Devices(bool locked) =>
        [
            new("{0.0.1.00000000}.{a1}", "Microphone (Arctis Nova Pro Wireless)", @"mic|USB\VID_1234&PID_5678&MI_00|MICROPHONE", DeviceKind.Microphone, false, true, locked ? DeviceProtection.HardwareMute : DeviceProtection.None, null),
            new("{0.0.1.00000000}.{b2}", "Mikrofon (Realtek(R) Audio)", @"mic|HDAUDIO\FUNC_01&VEN_10EC|MIKROFON", DeviceKind.Microphone, false, false, locked ? DeviceProtection.SoftwareMute : DeviceProtection.None, null),
            new("{0.0.1.00000000}.{c3}", "Headset (AirPods Pro Hands-Free AG Audio)", @"mic|BTHHFENUM\BTHHFPAUDIO|HEADSET", DeviceKind.Microphone, false, false, locked ? DeviceProtection.NotPresent : DeviceProtection.None, null),
            new("{0.0.1.00000000}.{d4}", "Microphone (SteelSeries Sonar - Microphone)", @"mic|SWD\SONAR|MICROPHONE", DeviceKind.Microphone, true, false, DeviceProtection.Excluded, null),
            new("{0.0.1.00000000}.{e5}", "Line In (Realtek(R) Audio)", @"mic|HDAUDIO\FUNC_01&VEN_10EC|LINE IN", DeviceKind.Microphone, false, false, DeviceProtection.DisabledByUser, null),
            new("{0.0.1.00000000}.{f6}", "Microphone (USB PnP Sound Device)", @"mic|USB\VID_0D8C&PID_0014|MICROPHONE", DeviceKind.Microphone, false, false, DeviceProtection.None, locked ? "Mute did not take effect" : null),
            .. Cameras(locked, helper: true),
        ];

        /// <summary>The camera helper's devices: what Settings lists with the helper installed (or, with
        /// <paramref name="helper"/> false, what it lists before: every camera needs the helper).</summary>
        public static IReadOnlyList<DeviceLockInfo> Cameras(bool locked, bool helper)
        {
            DeviceProtection off = !helper ? DeviceProtection.NeedsHelper : locked ? DeviceProtection.DeviceDisabled : DeviceProtection.None;
            DeviceProtection left = helper ? DeviceProtection.None : DeviceProtection.NeedsHelper;
            return
            [
                new(@"USB\VID_04F2&PID_B6DD&MI_00\6&2A8F1E3&0&0000", "Integrated Camera", @"cam|USB\VID_04F2&PID_B6DD&MI_00|INTEGRATED CAMERA", DeviceKind.Camera, false, false, off, null, CameraType.Color),
                new(@"USB\VID_04F2&PID_B6DD&MI_02\6&2A8F1E3&0&0002", "Integrated IR Camera", @"cam|USB\VID_04F2&PID_B6DD&MI_02|INTEGRATED IR CAMERA", DeviceKind.Camera, false, false, left, null, CameraType.Infrared),
                new(@"USB\VID_046D&PID_085E&MI_00\7&1C2B&0&0000", "Logitech BRIO", @"cam|USB\VID_046D&PID_085E&MI_00|LOGITECH BRIO", DeviceKind.Camera, false, false, helper ? DeviceProtection.None : DeviceProtection.NeedsHelper, locked && helper ? "In use by an app; BlindsDown keeps trying" : null, CameraType.ColorAndInfrared),
                new(@"SWD\VCAMDEVAPI\OBS", "OBS Virtual Camera", @"cam|SWD\VCAMDEVAPI|OBS VIRTUAL CAMERA", DeviceKind.Camera, true, false, left, null, CameraType.Virtual),
                new(@"USB\VID_0FD9&PID_0078&MI_00\8&3D&0&0000", "Elgato Facecam", @"cam|USB\VID_0FD9&PID_0078&MI_00|ELGATO FACECAM", DeviceKind.Camera, false, false, helper ? DeviceProtection.Excluded : DeviceProtection.NeedsHelper, null, CameraType.Color),
                new(@"USB\VID_046D&PID_085E&MI_02\7&1C2B&0&0002", "Logitech BRIO (audio)", @"hw|USB\VID_046D&PID_085E&MI_02|LOGITECH BRIO (AUDIO)", DeviceKind.MicrophoneHardware, false, false, helper && locked ? DeviceProtection.DeviceDisabled : left, null),
            ];
        }

        public static IReadOnlyList<HelperUiState> HelperStates() =>
        [
            HelperUiState.NotInstalled,
            new(HelperAvailability.NotInstalled, Detail: "Windows didn't allow the install, so cameras can't be locked yet."),
            new(HelperAvailability.Working, "0.2.0"),
            new(HelperAvailability.Working, "0.2.0", "The camera helper is installed. Cameras are locked from the next lock on."),
            new(HelperAvailability.NeedsUpdate, "0.0.9"),
            new(HelperAvailability.NeedsUpdate, "99.0.0"),
            new(HelperAvailability.NotResponding, "0.2.0"),
            new(HelperAvailability.Busy, "0.2.0", "Installing the camera helper… Windows asks for your permission."),
        ];

        /// <summary>A day of locks and unlocks around the sample history: every source, a partial lock, a lock still on.</summary>
        public static IReadOnlyList<LockEventRecord> LockEvents()
        {
            DateTimeOffset localMidnight = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
            return
            [
                Event(Now.AddMinutes(-20), LockEventAction.Lock, LockEventSource.Tray, "6 microphones, 2 cameras"),
                Event(localMidnight.AddHours(13).AddMinutes(30), LockEventAction.Unlock, LockEventSource.OtherProgram, "2 restored exactly, 1 needs attention.", problems: true),
                Event(localMidnight.AddHours(13).AddMinutes(10), LockEventAction.Lock, LockEventSource.Automatic, "6 microphones, 2 cameras"),
                Event(localMidnight.AddHours(9).AddMinutes(45), LockEventAction.Unlock, LockEventSource.Hotkey, "All 8 devices restored exactly as before."),
                Event(localMidnight.AddHours(8).AddMinutes(55), LockEventAction.Lock, LockEventSource.Hotkey, "6 microphones, 2 cameras"),
                Event(localMidnight.AddDays(-1).AddHours(23).AddMinutes(10), LockEventAction.Unlock, LockEventSource.Exit, "All 4 microphones restored exactly as before."),
                Event(localMidnight.AddDays(-1).AddHours(22), LockEventAction.Lock, LockEventSource.Hotkey, "4 microphones; 1 could not be locked", problems: true),
            ];
        }

        public static RestoreReport CameraReport() =>
            new(
            [
                new RestoreLine(RestoreOutcome.Exact, "Microphone (Arctis Nova Pro Wireless)", "Unmuted, as before the lock."),
                new RestoreLine(RestoreOutcome.ChangedByOthers, "Microphone (USB PnP Sound Device)", "Was unmuted before the lock but is muted now (changed by another app or by you)."),
                new RestoreLine(RestoreOutcome.Exact, "Integrated Camera", "Turned back on, as before the lock.", DeviceKind.Camera),
                new RestoreLine(RestoreOutcome.Failed, "Logitech BRIO", "Could not be turned back on: Windows needs a restart for this device. It comes back on when you restart the PC.", DeviceKind.Camera),
                new RestoreLine(RestoreOutcome.Exact, "Logitech BRIO (audio)", "Turned back on, as before the lock.", DeviceKind.MicrophoneHardware),
            ],
            5,
            CoversCameras: true);

        public static AppSettings Settings()
        {
            var settings = new AppSettings();
            settings.ExcludedDevices.Add(new DeviceExclusion { StableKey = @"mic|SWD\SONAR|MICROPHONE", DisplayName = "Microphone (SteelSeries Sonar - Microphone)" });
            settings.ExcludedDevices.Add(new DeviceExclusion { StableKey = @"mic|USB\VID_046D&PID_0825|MICROPHONE", DisplayName = "Microphone (HD Webcam C270)" });
            settings.ExcludedDevices.Add(new DeviceExclusion { StableKey = @"cam|USB\VID_0FD9&PID_0078&MI_00|ELGATO FACECAM", LastKnownId = @"USB\VID_0FD9&PID_0078&MI_00\8&3D&0&0000", DisplayName = "Elgato Facecam" });
            settings.ExcludedDevices.Add(new DeviceExclusion { StableKey = @"cam|USB\VID_1BCF&PID_2284&MI_00|USB CAMERA", DisplayName = "USB Camera" });
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

        private static LockEventRecord Event(DateTimeOffset at, LockEventAction action, LockEventSource source, string summary, bool problems = false) => new()
        {
            At = at,
            Action = action,
            Source = source,
            Summary = summary,
            HadProblems = problems,
        };

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
