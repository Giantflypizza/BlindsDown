using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>Devices that appear, disappear or come back while locked or after unlock.</summary>
public sealed class LockEngineDeviceChangeTests
{
    [Fact]
    public void NewDeviceDuringLockIsMutedAlertedAndRestoredAtUnlock()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.Add("webcam-mic", notify: true);
        world.Pump();

        Assert.True(world.Mics["webcam-mic"].Muted);
        EngineAlert alert = Assert.Single(world.AlertsOf(EngineAlertKind.NewDeviceLocked));
        Assert.Equal(world.Mics["webcam-mic"].Name, alert.DeviceName);
        Assert.False(alert.IsUrgent);
        Assert.True(world.Action("webcam-mic").AddedWhileLocked);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        RestoreReport report = world.Engine.Unlock()!;

        Assert.False(world.Mics["webcam-mic"].Muted);
        Assert.True(report.AllExact);
        Assert.Equal(2, report.DeviceCount);
    }

    [Fact]
    public void NewDeviceThatArrivesMutedStaysMutedAfterUnlock()
    {
        var world = new LockWorld();
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.Add("muted-usb", muted: true, notify: true);
        world.Pump();
        world.Engine.Unlock();

        Assert.True(world.Mics["muted-usb"].Muted);
        Assert.Equal(0, world.Mics.SetMuteCallsFor("muted-usb"));
    }

    [Fact]
    public void NewDisabledOrExcludedDeviceDuringLockIsLeftAlone()
    {
        var world = new LockWorld();
        world.Settings.ExcludedDevices.Add(new() { StableKey = "x", LastKnownId = "excluded", DisplayName = "x" });
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.Add("disabled", state: EndpointState.Disabled, notify: true);
        world.Mics.Add("excluded", notify: true);
        world.Pump();
        world.Engine.Reconcile();

        Assert.False(world.Mics["disabled"].Muted);
        Assert.False(world.Mics["excluded"].Muted);
        Assert.Empty(world.PersistedJournal!.Actions);
        Assert.Empty(world.Alerts);
    }

    [Fact]
    public void DeviceUnpluggedDuringLockIsRestoredWhenItComesBackAfterUnlock()
    {
        var world = new LockWorld();
        world.Mics.Add("stays");
        world.Mics.Add("usb");
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.Unplug("usb");
        world.Pump();
        Assert.Equal(DeviceProtection.NotPresent, world.DeviceStatus("usb").Protection);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);

        RestoreReport report = world.Engine.Unlock()!;

        Assert.Equal(1, report.PendingCount);
        Assert.Contains(report.Lines, l => l.Outcome == RestoreOutcome.Pending && l.DeviceName == world.Mics["usb"].Name);
        Assert.Contains("will be restored when reconnected", report.Summary, StringComparison.Ordinal);
        LockAction pending = Assert.Single(world.Pending.Load());
        Assert.Equal(ActionStatus.RevertPending, pending.Status);
        Assert.True(world.Mics["usb"].Muted);

        world.Mics.Plug("usb");
        world.Pump();

        Assert.False(world.Mics["usb"].Muted);
        Assert.Empty(world.Pending.Load());
        Assert.Single(world.AlertsOf(EngineAlertKind.DeferredRestoreDone));
    }

    [Fact]
    public void PendingRestoreIsFinishedByReconcileWhenTheNotificationWasMissed()
    {
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("usb");
        world.Engine.Unlock();

        world.Mics.Plug("usb");
        world.Mics.DropNotifications();
        world.Engine.Reconcile();

        Assert.False(world.Mics["usb"].Muted);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void PendingRestoreIsFinishedAtStartup()
    {
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("usb");
        world.Engine.Unlock();
        world.Mics.Plug("usb");

        Assert.Null(world.Restart());

        Assert.False(world.Mics["usb"].Muted);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void PendingRestoreWaitsWhileTheDeviceIsDisabled()
    {
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Disable("usb");
        world.Pump();
        world.Engine.Unlock();
        Assert.Single(world.Pending.Load());

        world.Engine.Reconcile();
        Assert.True(world.Mics["usb"].Muted);
        Assert.Single(world.Pending.Load());

        world.Mics.Enable("usb");
        world.Pump();
        Assert.False(world.Mics["usb"].Muted);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void DeviceReturningDuringANewLockKeepsItsOriginalState()
    {
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("usb");
        world.Pump();
        world.Engine.Unlock();

        // Second lock while it is still away, then it comes back (still muted from the first lock).
        world.Engine.Lock();
        world.Mics.Plug("usb");
        world.Pump();

        Assert.True(world.Mics["usb"].Muted);
        Assert.Empty(world.Pending.Load());
        Assert.False(world.Action("usb").WasAlreadyLocked);

        world.Engine.Unlock();

        Assert.False(world.Mics["usb"].Muted);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void PendingDevicePresentAtTheNextLockIsRestoredByThatLocksUnlock()
    {
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("usb");
        world.Engine.Unlock();

        // It comes back but the notification is lost, and the user locks before the safety poll runs.
        world.Mics.Plug("usb");
        world.Mics.DropNotifications();
        world.Engine.Lock();

        Assert.Empty(world.Pending.Load());
        Assert.False(world.Action("usb").WasAlreadyLocked);

        world.Engine.Unlock();
        Assert.False(world.Mics["usb"].Muted);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void PendingDeviceAbsentAgainAtTheNextUnlockStaysPendingWithItsOriginalState()
    {
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Mics.Unplug("usb");
        world.Engine.Unlock();

        world.Engine.Lock();
        world.Mics.Plug("usb");
        world.Pump();
        world.Mics.Unplug("usb");
        world.Pump();
        world.Engine.Unlock();

        LockAction pending = Assert.Single(world.Pending.Load());
        Assert.False(pending.WasAlreadyLocked);
        world.Mics.Plug("usb");
        world.Pump();
        Assert.False(world.Mics["usb"].Muted);
    }

    [Fact]
    public void DeviceThatComesBackUnmutedDuringTheLockIsMutedAgainWithAnAlert()
    {
        var world = new LockWorld();
        world.Mics.Add("usb");
        world.Engine.Initialize();
        world.Engine.Lock();

        // Some devices reset their mute when replugged.
        world.Mics.Unplug("usb");
        world.Pump();
        world.Mics["usb"].Muted = false;
        world.Mics.Plug("usb");
        world.Pump();

        Assert.True(world.Mics["usb"].Muted);
        Assert.Single(world.AlertsOf(EngineAlertKind.DeviceRelocked));
        world.Engine.Unlock();
        Assert.False(world.Mics["usb"].Muted);
    }

    [Fact]
    public void UnreadableUnpluggedMicIsNotPresentNotAProblem()
    {
        var world = new LockWorld();
        world.Mics.Add("airpods-hfp", state: EndpointState.Unplugged, unreadableWhenUnplugged: true);
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();

        Assert.Equal(LockState.Locked, status.State);
        Assert.Equal(DeviceProtection.NotPresent, world.DeviceStatus("airpods-hfp").Protection);
        Assert.Equal(ActionStatus.Planned, world.Action("airpods-hfp").Status);
    }

    [Fact]
    public void UnreadableMicThatConnectsUnmutedIsMutedAndUnmutedAgainAtUnlock()
    {
        var world = new LockWorld();
        world.Mics.Add("airpods-hfp", state: EndpointState.Unplugged, unreadableWhenUnplugged: true);
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.Plug("airpods-hfp");
        world.Pump();
        Assert.True(world.Mics["airpods-hfp"].Muted);

        world.Engine.Unlock();
        Assert.False(world.Mics["airpods-hfp"].Muted);
    }

    [Fact]
    public void UnreadableMicThatWasMutedBeforeTheLockStaysMutedAfterUnlock()
    {
        // Its mute could not be read at lock time. When it connects it turns out to be muted, and
        // BlindsDown did not mute it, so unlock must leave it muted.
        var world = new LockWorld();
        world.Mics.Add("airpods-hfp", muted: true, state: EndpointState.Unplugged, unreadableWhenUnplugged: true);
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.Plug("airpods-hfp");
        world.Pump();
        world.Engine.Reconcile();
        RestoreReport report = world.Engine.Unlock()!;

        Assert.True(world.Mics["airpods-hfp"].Muted);
        Assert.Equal(0, world.Mics.SetMuteCallsFor("airpods-hfp"));
        Assert.True(report.AllExact, report.Summary);
    }

    [Fact]
    public void UnreadableMicThatNeverConnectedIsNotPendingAfterUnlock()
    {
        var world = new LockWorld();
        world.Mics.Add("airpods-hfp", state: EndpointState.Unplugged, unreadableWhenUnplugged: true);
        world.Engine.Initialize();
        world.Engine.Lock();

        RestoreReport report = world.Engine.Unlock()!;

        Assert.Empty(world.Pending.Load());
        Assert.Equal(0, report.PendingCount);
        Assert.True(report.AllExact, report.Summary);
    }

    [Fact]
    public void BluetoothMicThatDisconnectsDuringTheLockIsStillRestoredWhenItReturns()
    {
        // Muted by the lock, then its hands-free endpoint drops to "unplugged" and can't be opened.
        var world = new LockWorld();
        world.Mics.Add("airpods-hfp", unreadableWhenUnplugged: true);
        world.Engine.Initialize();
        world.Engine.Lock();
        Assert.True(world.Mics["airpods-hfp"].Muted);

        world.Mics.Unplug("airpods-hfp", EndpointState.Unplugged);
        world.Pump();
        world.Engine.Reconcile();

        Assert.Equal(DeviceProtection.NotPresent, world.DeviceStatus("airpods-hfp").Protection);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        Assert.Empty(world.AlertsOf(EngineAlertKind.DeviceRelocked));

        RestoreReport report = world.Engine.Unlock()!;
        Assert.Equal(1, report.PendingCount);
        Assert.Single(world.Pending.Load());

        // Its endpoint is "usable" (unplugged) but still can't be opened: the restore keeps waiting.
        world.Engine.Reconcile();
        Assert.Single(world.Pending.Load());

        world.Mics.Plug("airpods-hfp");
        world.Pump();
        Assert.False(world.Mics["airpods-hfp"].Muted);
        Assert.Empty(world.Pending.Load());
    }

    [Fact]
    public void BluetoothMicThatReconnectsDuringTheLockIsNotReportedAsRelocked()
    {
        var world = new LockWorld();
        world.Mics.Add("airpods-hfp", unreadableWhenUnplugged: true);
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.Unplug("airpods-hfp", EndpointState.Unplugged);
        world.Pump();
        world.Mics.Plug("airpods-hfp");
        world.Pump();

        Assert.True(world.Mics["airpods-hfp"].Muted);
        Assert.Empty(world.AlertsOf(EngineAlertKind.DeviceRelocked));
        Assert.Equal(ActionStatus.Applied, world.Action("airpods-hfp").Status);
        world.Engine.Unlock();
        Assert.False(world.Mics["airpods-hfp"].Muted);
    }

    [Fact]
    public void DeviceDisabledByTheUserDuringTheLockIsRestoredWhenReEnabled()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.Disable("a");
        world.Pump();
        Assert.Equal(DeviceProtection.DisabledByUser, world.DeviceStatus("a").Protection);
        world.Engine.Unlock();

        world.Mics.Enable("a");
        world.Pump();
        Assert.False(world.Mics["a"].Muted);
    }

    [Fact]
    public void DeviceEnabledByTheUserDuringTheLockIsLockedAndRestored()
    {
        var world = new LockWorld();
        world.Mics.Add("a", state: EndpointState.Disabled);
        world.Engine.Initialize();
        world.Engine.Lock();
        Assert.Empty(world.PersistedJournal!.Actions);

        world.Mics.Enable("a");
        world.Pump();
        Assert.True(world.Mics["a"].Muted);

        world.Engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
    }

    [Fact]
    public void NotificationsForUnknownEndpointsAreHarmless()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();

        world.Engine.OnEndpointChanged("{ghost}");
        world.Engine.OnMuteChanged("{ghost}", muted: false, causedByUs: false);
        world.Engine.Lock();
        world.Engine.OnEndpointChanged("{ghost}");
        world.Engine.OnMuteChanged("{ghost}", muted: false, causedByUs: false);

        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        Assert.Single(world.PersistedJournal!.Actions);
        Assert.Empty(world.Alerts);
    }

    [Fact]
    public void NewDeviceThatCannotBeOpenedYetIsLockedQuietlyWhenItConnects()
    {
        var world = new LockWorld();
        world.Engine.Initialize();
        world.Engine.Lock();

        world.Mics.Add("hfp", state: EndpointState.Unplugged, unreadableWhenUnplugged: true, notify: true);
        world.Pump();

        Assert.Equal(ActionStatus.Planned, world.Action("hfp").Status);
        Assert.Equal(DeviceProtection.NotPresent, world.DeviceStatus("hfp").Protection);
        Assert.Empty(world.Alerts);

        world.Mics.Plug("hfp");
        world.Pump();
        Assert.True(world.Mics["hfp"].Muted);
        Assert.Equal(DeviceProtection.SoftwareMute, world.DeviceStatus("hfp").Protection);

        world.Engine.Unlock();
        Assert.False(world.Mics["hfp"].Muted);
    }

    [Fact]
    public void SafetyPollDoesNotRewriteTheJournalForADeviceThatStaysAway()
    {
        var world = new LockWorld();
        world.Mics.Add("hfp", state: EndpointState.Unplugged, unreadableWhenUnplugged: true);
        world.Engine.Initialize();
        world.Engine.Lock();
        int saves = world.MemoryJournal.SaveCount;

        for (int i = 0; i < 10; i++)
        {
            world.Engine.Reconcile();
        }

        Assert.Equal(saves, world.MemoryJournal.SaveCount);
    }

    [Fact]
    public void EndpointNotificationsDuringLockOrUnlockAreLeftForTheFinalCheck()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();
        world.Engine.Lock();
        world.Restart();
        world.Journal.Save(WithPhase(world.PersistedJournal!, LockPhase.Unlocking));
        world.Restart();

        world.Mics.Add("b", notify: true);
        world.Pump();
        world.Engine.Reconcile();

        Assert.False(world.Mics["b"].Muted);
        Assert.Equal(LockState.Unlocking, world.Engine.Status.State);
    }

    private static LockJournal WithPhase(LockJournal journal, LockPhase phase)
    {
        journal.Phase = phase;
        return journal;
    }
}
