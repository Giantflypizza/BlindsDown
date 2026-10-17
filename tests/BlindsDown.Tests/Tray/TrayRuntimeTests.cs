using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Settings;
using BlindsDown.Core.Usage;
using BlindsDown.Ui;
using BlindsDown.Ui.Tray;

namespace BlindsDown.Tests.Tray;

/// <summary>Exercises the real windows. Everything is placed on a fake monitor far off-screen, so nothing becomes visible.</summary>
public partial class TrayRuntimeTests
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExLayered = 0x00080000;
    private const long WsExNoActivate = 0x08000000;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseActivate = 0x0021;
    private const nint HtTransparent = -1;
    private const nint MaNoActivate = 3;

    private static readonly MonitorArea OffScreen = new(
        new Rectangle(-32000, -32000, 1280, 720),
        new Rectangle(-32000, -32000, 1280, 680),
        1.25f);

    [Fact]
    public void LockIndicatorBuildsSwapsAndClearsItsWindows()
    {
        TrayTestEnvironment.RequireRealUi();
        TrayTestEnvironment.RunOnSta(() =>
        {
            using var manager = new LockOverlayManager { MonitorSource = () => [OffScreen] };
            var settings = new AppSettings { ShowOverlay = true, ShowScreenEdges = true, HideOverlayFromScreenCapture = true };

            manager.Update(LockState.Unlocked, settings);
            Assert.Empty(manager.Windows);

            manager.Update(LockState.Locked, settings);
            IReadOnlyList<OverlayWindow> locked = manager.Windows;
            Assert.Equal(5, locked.Count); // four edge lines and the pill
            Assert.All(locked, w =>
            {
                Assert.True(w.Visible);
                Assert.True(w.IsExcludedFromCapture);
                Assert.True(OffScreen.Bounds.IntersectsWith(w.Target));

                // WinForms must not have moved/resized the layered window behind UpdateLayeredWindow's back.
                Assert.NotEqual(0, GetWindowRect(w.Handle, out WindowRect rect));
                Assert.Equal(w.Target, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                AssertClickThroughToolWindow(w);
            });
            Assert.Null(Form.ActiveForm); // nothing was activated

            // Same state and settings: nothing is rebuilt.
            manager.Update(LockState.Locked, settings);
            Assert.Equal(locked, manager.Windows);

            // Colour change: a new set replaces the old one.
            manager.Update(LockState.PartiallyLocked, settings);
            Assert.Equal(5, manager.Windows.Count);
            Assert.All(locked, w => Assert.True(w.IsDisposed));

            settings.ShowScreenEdges = false;
            manager.Update(LockState.PartiallyLocked, settings);
            OverlayWindow pill = Assert.Single(manager.Windows);
            Assert.True(OffScreen.WorkingArea.Contains(Rectangle.Inflate(pill.Target, -8, -8)));

            manager.Update(LockState.Unlocked, settings);
            Assert.Empty(manager.Windows);
            Assert.True(pill.IsDisposed);
        });
    }

    [Fact]
    public void OsdFlashShowsWithoutActivatingAndFadesAway()
    {
        TrayTestEnvironment.RequireRealUi();
        TrayTestEnvironment.RunOnSta(() =>
        {
            using var osd = new OsdFlash { MonitorSource = () => OffScreen };
            osd.Show(LockState.Locked);
            Form window = osd.Window!;
            Assert.True(window.Visible);
            Assert.Null(Form.ActiveForm);
            Assert.NotEqual(GetForegroundWindow(), window.Handle);
            AssertClickThroughToolWindow(window);
            Assert.InRange(window.Opacity, 0.5, 0.99); // layered via Opacity only, never UpdateLayeredWindow

            osd.Show(LockState.Unlocked, "Restored exactly"); // replaces the current flash in place
            Assert.Same(window, osd.Window);

            var clock = Stopwatch.StartNew();
            while (window.Visible && clock.ElapsedMilliseconds < 5000)
            {
                Application.DoEvents(); // runs the fade timer
                Thread.Sleep(10);
            }

            Assert.False(window.Visible);
            Assert.InRange(clock.ElapsedMilliseconds, 1200, 3000);
        });
    }

    /// <summary>Never calls Show(), so no icon reaches the notification area.</summary>
    [Fact]
    public void TrayControllerAcceptsEveryStateAndHugeUsageLists()
    {
        TrayTestEnvironment.RunOnSta(() =>
        {
            using var tray = new TrayController();
            foreach (LockState state in Enum.GetValues<LockState>())
            {
                tray.SetStatus(new LockStatusInfo(state, [], [new string('p', 400)]), Hotkey.Default);
            }

            UsageSession[] active =
            [
                .. Enumerable.Range(0, 40).Select(i => new UsageSession
                {
                    Id = Guid.NewGuid(),
                    Kind = i % 2 == 0 ? CaptureKind.Microphone : CaptureKind.Camera,
                    App = new AppIdentity($"exe:{i}", $"A & B application with a long name {i}", null, null),
                    DeviceId = "dev",
                    DeviceName = "Device",
                    ViaApp = "SteelSeries Sonar",
                    Start = DateTimeOffset.UnixEpoch,
                    Sources = ["test"],
                }),
            ];
            tray.SetActiveUsage(active);
            tray.SetActiveUsage([]);
        });
    }

    /// <summary>
    /// The safety properties of every indicator window: topmost, not in Alt+Tab, never activated and
    /// click-through (WS_EX_TRANSPARENT only works on a layered window; the hit test is answered too).
    /// </summary>
    private static void AssertClickThroughToolWindow(Form window)
    {
        const long required = WsExTopmost | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
        long exStyle = GetWindowLongPtrW(window.Handle, GwlExStyle);
        Assert.Equal(required, exStyle & required);
        Assert.Equal(HtTransparent, SendMessageW(window.Handle, WmNcHitTest, 0, 0));
        Assert.Equal(MaNoActivate, SendMessageW(window.Handle, WmMouseActivate, 0, 0));
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRect(nint hWnd, out WindowRect rect);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
