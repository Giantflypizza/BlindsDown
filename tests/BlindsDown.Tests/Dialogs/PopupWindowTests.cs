using System.Runtime.InteropServices;
using BlindsDown.Ui;
using BlindsDown.Ui.Forms;
using static BlindsDown.Tests.Dialogs.DialogsQaHarness;

namespace BlindsDown.Tests.Dialogs;

/// <summary>
/// Drives real popup windows (bottom-right of the primary screen, for well under a second). Only runs
/// with BLINDSDOWN_DEVICE_TESTS=1 because it shows real windows on the desktop.
/// </summary>
[Collection(DesktopWindowsCollection.Name)]
public sealed partial class PopupWindowTests
{
    private const string Message = "BlindsDown self-test popup; it closes by itself.";

    [Fact]
    public void PopupsStackInTheCornerWithoutTakingFocus()
    {
        Assert.SkipUnless(WindowsAllowed, $"Shows real popups; set {DeviceTestsVariable}=1 to run.");

        RunSta(() =>
        {
            nint foregroundBefore = GetForegroundWindow();
            using var manager = new AlertPopupManager();
            for (int i = 1; i <= 6; i++)
            {
                manager.Show(new AlertModel("BlindsDown test " + i, Message, AlertSeverity.Info));
            }

            // Same title and message again within 3 s: coalesced, no seventh popup.
            manager.Show(new AlertModel("BlindsDown test 6", Message, AlertSeverity.Info));
            Pump();

            List<AlertPopupForm> popups = Application.OpenForms.OfType<AlertPopupForm>().ToList();
            Assert.Equal(6, popups.Count);
            List<AlertPopupForm> visible = popups.Where(p => p.Visible).ToList();
            Assert.Equal(4, visible.Count);
            Assert.All(popups.Take(2), p => Assert.False(p.Visible));
            Assert.True(Application.OpenForms.OfType<MorePillForm>().Single().Visible);

            // Newest closest to the corner: each newer popup sits below the previous one, inside the work area.
            Rectangle work = SystemInformation.WorkingArea;
            for (int i = 1; i < visible.Count; i++)
            {
                Assert.True(visible[i].Top > visible[i - 1].Bottom, "newer popups go below older ones");
            }

            Assert.All(visible, p => Assert.True(work.Contains(p.Bounds), "popup inside the work area"));
            Assert.Equal(foregroundBefore, GetForegroundWindow());

            manager.DismissAll();
            Pump();
            Assert.Empty(Application.OpenForms.OfType<AlertPopupForm>());
            Assert.False(Application.OpenForms.OfType<MorePillForm>().Single().Visible);

            // The same alert again within 3 s, but its popup is already gone: it must show again
            // instead of being merged into nothing.
            manager.Show(new AlertModel("BlindsDown test 6", Message, AlertSeverity.Info));
            Pump();
            AlertPopupForm again = Assert.Single(Application.OpenForms.OfType<AlertPopupForm>());
            Assert.True(again.Visible);
            Assert.True(work.Contains(again.Bounds), "popup inside the work area");
            Assert.Equal(foregroundBefore, GetForegroundWindow());

            manager.DismissAll();
            Pump();
            Assert.Empty(Application.OpenForms.OfType<AlertPopupForm>());
        });
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}
