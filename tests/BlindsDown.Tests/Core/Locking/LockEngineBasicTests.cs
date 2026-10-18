using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>Lock and unlock with a static set of devices: scope, exact restore and status.</summary>
public sealed class LockEngineBasicTests
{
    [Fact]
    public void LockMutesEveryUsableMicAndUnlockRestoresExactly()
    {
        var world = new LockWorld();
        world.Mics.Add("arctis");
        world.Mics.Add("sonar", hardware: @"SWD\MMDEVAPI\{0.0.1.00000000}.{SONAR}");
        world.Mics.Add("jack", state: EndpointState.Unplugged);
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();

        Assert.Equal(LockState.Locked, status.State);
        Assert.True(world.Engine.HasLock);
        Assert.All(world.Mics.All, m => Assert.True(m.Muted, m.Id));
        Assert.Empty(status.Problems);

        RestoreReport? report = world.Engine.Unlock();

        Assert.NotNull(report);
        Assert.True(report.AllExact, report.Summary);
        Assert.Equal(3, report.DeviceCount);
        Assert.All(world.Mics.All, m => Assert.False(m.Muted, m.Id));
        Assert.False(world.Engine.HasLock);
        Assert.Null(world.PersistedJournal);
        Assert.Equal(LockState.Unlocked, world.Engine.Status.State);
    }

    [Fact]
    public void AlreadyMutedMicStaysMutedAfterUnlock()
    {
        var world = new LockWorld();
        world.Mics.Add("open");
        world.Mics.Add("muted", muted: true);
        world.Engine.Initialize();

        world.Engine.Lock();
        LockAction mutedAction = world.Action("muted");
        RestoreReport report = world.Engine.Unlock()!;

        Assert.True(mutedAction.WasAlreadyLocked);
        Assert.False(world.Mics["open"].Muted);
        Assert.True(world.Mics["muted"].Muted);
        Assert.Equal(0, world.Mics.SetMuteCallsFor("muted"));
        Assert.True(report.AllExact);
        Assert.Contains(report.Lines, l => l.DeviceName == world.Mics["muted"].Name && l.Message.StartsWith("Muted", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledAndNotPresentDevicesAreNeverTouched()
    {
        var world = new LockWorld();
        world.Mics.Add("active");
        world.Mics.Add("disabled", state: EndpointState.Disabled);
        world.Mics.Add("gone", state: EndpointState.NotPresent);
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();

        Assert.Equal(LockState.Locked, status.State);
        Assert.Single(world.PersistedJournal!.Actions);
        Assert.Equal(DeviceProtection.DisabledByUser, world.DeviceStatus("disabled").Protection);
        Assert.DoesNotContain(status.Devices, d => d.Id == "gone");

        // They come and go during the lock, and the user re-enables nothing: still untouched.
        world.Mics.SetState("gone", EndpointState.Disabled);
        world.Pump();
        world.Engine.Reconcile();
        world.Engine.Unlock();

        Assert.Equal(0, world.Mics.SetMuteCallsFor("disabled"));
        Assert.Equal(0, world.Mics.SetMuteCallsFor("gone"));
        Assert.False(world.Mics["disabled"].Muted);
        Assert.False(world.Mics["gone"].Muted);
    }

    [Fact]
    public void DefaultMicsAreMutedLastAndUnmutedFirst()
    {
        var world = new LockWorld();
        world.Mics.Add("default");
        world.Mics.Add("comms");
        world.Mics.Add("other");
        world.Mics.Defaults = new DefaultMics("default", "default", "comms");
        world.Engine.Initialize();

        world.Engine.Lock();
        world.Engine.Unlock();

        List<string> mutes = world.Mics.SetMuteLog.Where(e => e.Muted).Select(e => e.Id).ToList();
        List<string> unmutes = world.Mics.SetMuteLog.Where(e => !e.Muted).Select(e => e.Id).ToList();
        Assert.Equal("other", mutes[0]);
        Assert.Equal(["default", "comms"], mutes[1..]);
        Assert.Equal(["comms", "default", "other"], unmutes);
    }

    [Fact]
    public void ExcludedDeviceIsNeverTouched()
    {
        var world = new LockWorld();
        world.Mics.Add("arctis");
        world.Mics.Add("sonar", hardware: @"SWD\MMDEVAPI\SONAR\0001");
        world.Exclude("sonar");
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();

        Assert.Equal(LockState.Locked, status.State);
        Assert.False(world.Mics["sonar"].Muted);
        Assert.Equal(DeviceProtection.Excluded, world.DeviceStatus("sonar").Protection);
        Assert.True(world.DeviceStatus("sonar").IsVirtual);

        // Another app unmuting/muting an excluded mic is none of BlindsDown's business.
        world.Mics.OtherAppSetsMute("sonar", true);
        world.Mics.OtherAppSetsMute("sonar", false);
        world.Pump();
        world.Engine.Reconcile();
        world.Engine.Unlock();

        Assert.Equal(0, world.Mics.SetMuteCallsFor("sonar"));
        Assert.Empty(world.Alerts);
    }

    [Fact]
    public void ExclusionByStableKeySurvivesAPortChange()
    {
        var world = new LockWorld();
        world.Mics.Add("usb-port1", hardware: @"USB\VID_1234&PID_5678&MI_00\9&1A2B3C4D&0&0000", description: "Microphone");
        world.Mics.Add("other");
        world.Exclude("usb-port1", byStableKeyOnly: true);
        world.Engine.Initialize();
        world.Engine.Lock();

        // Same headset, another USB port: new endpoint ID and instance suffix, same stable key.
        FakeMic moved = world.Mics.MoveToAnotherPort("usb-port1", "usb-port2");
        world.Pump();
        world.Engine.Reconcile();

        Assert.Equal(world.Mics["usb-port1"].ToEndpoint().StableKey, moved.ToEndpoint().StableKey);
        Assert.False(moved.Muted);
        Assert.Equal(0, world.Mics.SetMuteCallsFor("usb-port2"));
        Assert.Empty(world.AlertsOf(EngineAlertKind.NewDeviceLocked));
        Assert.Equal(DeviceProtection.Excluded, world.DeviceStatus("usb-port2").Protection);
    }

    [Fact]
    public void ExclusionByLastKnownIdMatchesEvenWhenTheStableKeyDiffers()
    {
        var world = new LockWorld();
        world.Mics.Add("mic-a", description: "Headset");
        world.Settings.ExcludedDevices.Add(new()
        {
            StableKey = "mic|SOMETHING-ELSE|HEADSET",
            LastKnownId = "MIC-A",
            DisplayName = "Old name",
        });
        world.Engine.Initialize();

        world.Engine.Lock();

        Assert.False(world.Mics["mic-a"].Muted);
        Assert.Empty(world.PersistedJournal!.Actions);
    }

    [Fact]
    public void LockAndUnlockAreIdempotent()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();

        Assert.Null(world.Engine.Unlock());
        world.Engine.Lock();
        int saves = world.MemoryJournal.SaveCount;
        LockStatusInfo again = world.Engine.Lock();

        Assert.Equal(LockState.Locked, again.State);
        Assert.Equal(saves, world.MemoryJournal.SaveCount);
        Assert.Single(world.PersistedJournal!.Actions);
        Assert.NotNull(world.Engine.Unlock());
        Assert.Null(world.Engine.Unlock());
    }

    [Fact]
    public void LockWithNoMicrophonesStillLocksAndCatchesTheFirstOne()
    {
        var world = new LockWorld();
        world.Engine.Initialize();

        LockStatusInfo status = world.Engine.Lock();
        Assert.Equal(LockState.Locked, status.State);

        world.Mics.Add("late", notify: true);
        world.Pump();

        Assert.True(world.Mics["late"].Muted);
        RestoreReport report = world.Engine.Unlock()!;
        Assert.False(world.Mics["late"].Muted);
        Assert.Equal("1 microphone restored exactly as before.", report.Summary);
    }

    [Fact]
    public void StatusMovesThroughEveryStateAndRaisesEventsOnlyOnChange()
    {
        var world = new LockWorld();
        world.Mics.Add("a", hardwareMute: true);
        world.Mics.Add("b");
        world.Engine.Initialize();
        Assert.Equal(LockState.Unlocked, Assert.Single(world.Statuses).State);

        world.Engine.Reconcile();
        world.Engine.OnEndpointChanged("a");
        Assert.Single(world.Statuses);

        world.Engine.RefreshStatus();
        Assert.Equal(2, world.Statuses.Count);

        world.Statuses.Clear();
        world.Engine.Lock();
        Assert.Equal([LockState.Locking, LockState.Locked], world.Statuses.Select(s => s.State));
        Assert.Equal(DeviceProtection.HardwareMute, world.DeviceStatus("a").Protection);
        Assert.Equal(DeviceProtection.SoftwareMute, world.DeviceStatus("b").Protection);
        Assert.True(world.Engine.Status.IsLockedOrPartly);

        world.Statuses.Clear();
        world.Engine.Unlock();
        Assert.Equal([LockState.Unlocking, LockState.Unlocked], world.Statuses.Select(s => s.State));
        Assert.All(world.Engine.Status.Devices, d => Assert.Equal(DeviceProtection.None, d.Protection));
        Assert.False(world.Engine.Status.IsLockedOrPartly);
    }

    [Fact]
    public void EnumerationFailureLocksNothingYetAndReconcileCatchesUp()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();
        world.Mics.EnumerateThrows = true;

        LockStatusInfo status = world.Engine.Lock();

        // Nothing could be checked, so the lock must not claim to be complete.
        Assert.Equal(LockState.PartiallyLocked, status.State);
        Assert.False(world.Mics["a"].Muted);

        world.Mics.EnumerateThrows = false;
        world.Engine.Reconcile();

        Assert.True(world.Mics["a"].Muted);
        Assert.Equal(LockState.Locked, world.Engine.Status.State);
        world.Engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
    }

    [Fact]
    public void DefaultDeviceFailureDoesNotStopTheLock()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Mics.DefaultsThrow = true;
        world.Engine.Initialize();

        Assert.Equal(LockState.Locked, world.Engine.Lock().State);
        Assert.True(world.Mics["a"].Muted);
        Assert.Equal(DefaultMics.None, world.PersistedJournal!.Before!.Defaults);
        Assert.True(world.Engine.Unlock()!.AllExact);
    }

    [Fact]
    public void JournalSaysTheDeviceMayBeMutedBeforeItIsMuted()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        world.Engine.Initialize();
        ActionStatus? persistedAtMute = null;
        var probe = new ProbeBackend(world.Mics, () => persistedAtMute ??= world.MemoryJournal.Load()?.Actions.Single().Status);
        var engine = new LockEngine(probe, world.MemoryJournal, world.Pending, world.Clock, () => world.Settings);

        engine.Lock();

        // Write-ahead: had the process died during the mute, unlock would still have undone it.
        Assert.Equal(ActionStatus.Applying, persistedAtMute);
        LockJournal journal = world.PersistedJournal!;
        Assert.Equal(LockPhase.Locked, journal.Phase);
        Assert.NotNull(journal.Before);
        Assert.Equal(world.Clock.UtcNow, journal.StartedAt);
    }

    [Fact]
    public void FailedFirstJournalWriteLeavesEverythingUnlockedAndUntouched()
    {
        var world = new LockWorld();
        world.Mics.Add("a");
        var store = new FailingJournalStore();
        var engine = new LockEngine(world.Mics, store, world.Pending, world.Clock, () => world.Settings);
        engine.Initialize();

        Assert.Throws<IOException>(() => engine.Lock());

        Assert.False(world.Mics["a"].Muted);
        Assert.False(engine.HasLock);
        Assert.Null(engine.Unlock());

        // Disk has space again: a normal lock works.
        store.FailSaves = false;
        Assert.Equal(LockState.Locked, engine.Lock().State);
        Assert.True(world.Mics["a"].Muted);
        engine.Unlock();
        Assert.False(world.Mics["a"].Muted);
    }

    /// <summary>Calls a hook before the first SetMute so a test can look at what was persisted by then.</summary>
    private sealed class ProbeBackend(IMicBackend inner, Action beforeSetMute) : IMicBackend
    {
        public IReadOnlyList<MicEndpoint> EnumerateMics() => inner.EnumerateMics();

        public DefaultMics GetDefaultMics() => inner.GetDefaultMics();

        public bool? GetMute(string endpointId) => inner.GetMute(endpointId);

        public void SetMute(string endpointId, bool muted)
        {
            beforeSetMute();
            inner.SetMute(endpointId, muted);
        }

        public void SetEndpointEnabled(string endpointId, bool enabled) => inner.SetEndpointEnabled(endpointId, enabled);

        public void SetDefaultMic(string endpointId, MicRole role) => inner.SetDefaultMic(endpointId, role);
    }
}
