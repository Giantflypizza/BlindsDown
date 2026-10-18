using BlindsDown.Ui;
using BlindsDown.Ui.Forms;
using BlindsDown.Ui.Native;
using static BlindsDown.Tests.Dialogs.DialogsQaHarness;

namespace BlindsDown.Tests.Dialogs;

/// <summary>
/// A modal window (Settings, About, Welcome) disables every other top-level window of the UI thread while it is
/// open, the popups included, so their Close and action buttons would stop working. The popups enable themselves
/// again. Checked on hidden windows (a handle, never shown), with the same EnableWindow call the modal loop makes.
/// </summary>
[Collection(DesktopWindowsCollection.Name)]
public sealed class PopupEnableTests
{
    [Fact]
    public void APopupDisabledByAModalLoopEnablesItselfAgain()
    {
        RunSta(() =>
        {
            using var popup = new AlertPopupForm(new AlertModel("BlindsDown test", "Never shown.", AlertSeverity.Info, Sticky: true));
            nint handle = popup.Handle;
            Assert.True(PopupNative.IsWindowEnabled(handle));

            PopupNative.EnableWindow(handle, false);
            Assert.False(PopupNative.IsWindowEnabled(handle)); // re-enabled from a posted message, not inside the call
            Pump();
            Assert.True(PopupNative.IsWindowEnabled(handle));
        });
    }

    [Fact]
    public void TheMorePillEnablesItselfAgainToo()
    {
        RunSta(() =>
        {
            using var pill = new MorePillForm();
            nint handle = pill.Handle;

            PopupNative.EnableWindow(handle, false);
            Pump();
            Assert.True(PopupNative.IsWindowEnabled(handle));
        });
    }
}
