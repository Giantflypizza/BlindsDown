using BlindsDown.Core.Pnp;
using BlindsDown.Devices.Pnp;
using BlindsDown.Interop.Pnp;
using static BlindsDown.Interop.Pnp.CfgMgrNative;

namespace BlindsDown.Tests.Pnp;

/// <summary>
/// Disable and Enable against a scripted device tree: the flags, the verification, and what each Windows answer
/// becomes. Real disables need administrator rights and real hardware, and never run in tests.
/// </summary>
public sealed class CfgMgrPnpBackendChangeTests
{
    private const string Camera = @"USB\VID_1234&PID_5678&MI_00\7&1A2B3C4D&0&0000";
    private const string WebcamMic = @"USB\VID_1234&PID_5678&MI_02\7&1A2B3C4D&0&0002";

    private readonly FakeCfgMgr _cfgMgr = new();
    private readonly FakeEndpoints _endpoints = new();
    private readonly FakeCfgMgr.FakeNode _camera;
    private readonly FakeCfgMgr.FakeNode _mic;

    public CfgMgrPnpBackendChangeTests()
    {
        _camera = _cfgMgr.Add(Camera);
        _cfgMgr.AddInterface(PnpKeys.VideoCameraInterface, Camera);
        _mic = _cfgMgr.Add(WebcamMic);
        _endpoints.Capture(WebcamMic);
    }

    [Fact]
    public void ADisableIsVerifiedAndNeverPersistent()
    {
        PnpChange change = Backend().Disable(Camera);

        Assert.Equal(PnpChange.Done, change);
        (string id, uint flags) = Assert.Single(_cfgMgr.DisableCalls);
        Assert.Equal(Camera, id);
        Assert.Equal(CmDisableUiNotOk, flags);
        Assert.Equal(0u, flags & CmDisablePersist);
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    [Fact]
    public void MicrophoneHardwareCanBeDisabledToo()
    {
        Assert.Equal(PnpChangeResult.Done, Backend().Disable(WebcamMic).Result);
        Assert.Equal(WebcamMic, Assert.Single(_cfgMgr.DisableCalls).InstanceId);
    }

    [Fact]
    public void IdsAreMatchedWithoutRegardToCase()
    {
        Assert.Equal(PnpChangeResult.Done, Backend().Disable(Camera.ToLowerInvariant()).Result);
    }

    [Fact]
    public void AnythingThatIsntACameraOrMicrophoneHardwareIsNeverDisabled()
    {
        _cfgMgr.Add(@"SCSI\DISK&VEN_NVME&PROD_SAMSUNG\5&1234&0&000000");
        _cfgMgr.Add(@"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086&REV_03\NETWORK");
        const string Headset = @"USB\VID_2345&PID_6789&MI_00\8&2B3C4D5E&0&0000";
        _cfgMgr.Add(Headset);
        _endpoints.Capture(Headset).Render(Headset);

        foreach (string id in new[] { @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG\5&1234&0&000000", @"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086&REV_03\NETWORK", Headset, @"HTREE\ROOT\0" })
        {
            PnpChange change = Backend().Disable(id);
            Assert.Equal(PnpChangeResult.Failed, change.Result);
            Assert.Equal(CfgMgrPnpBackend.NotLockableDetail, change.Detail);
        }

        Assert.Empty(_cfgMgr.DisableCalls);
    }

    [Fact]
    public void InvalidIdsAreRefusedBeforeReachingWindows()
    {
        // An empty ID would mean "the root of the device tree" to CM_Locate_DevNodeW; a null cuts an ID short.
        foreach (string id in new[] { string.Empty, " ", "NOSLASH", Camera + " ", "USB\\VID_1234\0\\x", @"USB\" + new string('A', 200) })
        {
            Assert.Equal(PnpChangeResult.Failed, Backend().Disable(id).Result);
            Assert.Equal(PnpChangeResult.Failed, Backend().Enable(id).Result);
        }

        Assert.Empty(_cfgMgr.DisableCalls);
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    [Fact]
    public void AnAlreadyDisabledDeviceIsLeftAlone()
    {
        _camera.MarkDisabled();

        PnpChange change = Backend().Disable(Camera);

        Assert.Equal(new PnpChange(PnpChangeResult.Done, CfgMgrPnpBackend.AlreadyDisabledDetail), change);
        Assert.Empty(_cfgMgr.DisableCalls);
    }

    [Fact]
    public void AVetoedDisableChangesNothing()
    {
        _camera.DisableResult = CrRemoveVetoed;
        _camera.OnDisable = _ => { };

        Assert.Equal(PnpChangeResult.Vetoed, Backend().Disable(Camera).Result);
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    [Fact]
    public void TheVerifiedStateWinsOverTheReturnCode()
    {
        // Windows says "vetoed", yet the device is off: it must count as BlindsDown's change, so unlock turns it back on.
        _camera.DisableResult = CrRemoveVetoed;
        _camera.OnDisable = node => node.MarkDisabled();

        Assert.Equal(PnpChange.Done, Backend().Disable(Camera));
    }

    [Fact]
    public void AccessDeniedIsReported()
    {
        _camera.DisableResult = CrAccessDenied;
        _camera.OnDisable = _ => { };

        Assert.Equal(PnpChangeResult.AccessDenied, Backend().Disable(Camera).Result);
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    [Fact]
    public void AnUnpluggedDeviceIsNotFound()
    {
        _camera.Present = false;

        Assert.Equal(PnpChangeResult.NotFound, Backend().Disable(Camera).Result);
        Assert.Equal(PnpChangeResult.NotFound, Backend().Enable(Camera).Result);
        Assert.Empty(_cfgMgr.DisableCalls);
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    [Fact]
    public void ADeviceUnpluggedDuringTheDisableIsNotFound()
    {
        _camera.DisableResult = CrNoSuchDevNode;
        _camera.OnDisable = node => node.Present = false;

        Assert.Equal(PnpChangeResult.NotFound, Backend().Disable(Camera).Result);
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    [Fact]
    public void ADisableThatWouldOnlyHappenAtARestartIsUndone()
    {
        _camera.OnDisable = node => node.Status |= DnNeedRestart;

        Assert.Equal(PnpChangeResult.NeedsRestart, Backend().Disable(Camera).Result);
        Assert.Equal(Camera, Assert.Single(_cfgMgr.EnableCalls));
    }

    [Fact]
    public void ADisabledButStillRunningDeviceIsUndone()
    {
        _camera.OnDisable = node =>
        {
            node.Status |= DnHasProblem;
            node.Problem = CmProbDisabled;
        };

        Assert.Equal(PnpChangeResult.NeedsRestart, Backend().Disable(Camera).Result);
        Assert.Equal(Camera, Assert.Single(_cfgMgr.EnableCalls));
    }

    [Fact]
    public void NeedRestartFromWindowsIsUndone()
    {
        _camera.DisableResult = CrNeedRestart;
        _camera.OnDisable = _ => { };

        Assert.Equal(PnpChangeResult.NeedsRestart, Backend().Disable(Camera).Result);
        Assert.Equal(Camera, Assert.Single(_cfgMgr.EnableCalls));
    }

    [Fact]
    public void ARestartFlagThatWasAlreadySetDoesntSpoilAVerifiedDisable()
    {
        _camera.Status |= DnNeedRestart; // E.g. a driver update waiting for a restart.
        _camera.OnDisable = node =>
        {
            node.Status = DnHasProblem | DnNeedRestart;
            node.Problem = CmProbDisabled;
        };

        Assert.Equal(PnpChange.Done, Backend().Disable(Camera));
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    [Fact]
    public void AnUndoThatFailsIsReportedAsAFailureNotAsLeftOn()
    {
        // Disabled but still running, and Windows refuses to take it back: the device may be left half done, so
        // the caller must not be told that it is on.
        _camera.OnDisable = node =>
        {
            node.Status |= DnHasProblem;
            node.Problem = CmProbDisabled;
        };
        _camera.EnableResult = CrFailure;
        _camera.OnEnable = _ => { };

        PnpChange change = Backend().Disable(Camera);

        Assert.Equal(PnpChangeResult.Failed, change.Result);
        Assert.Contains("could not take that back", change.Detail, StringComparison.Ordinal);
        Assert.Equal(Camera, Assert.Single(_cfgMgr.EnableCalls));
    }

    [Fact]
    public void ADisableThatWindowsMakesPersistentIsTakenBackAndNotTriedAgain()
    {
        // Never expected (only CM_DISABLE_PERSIST sets CONFIGFLAG_DISABLED), but a disable that outlives a restart
        // would break the promise that a restart brings every device back. Trying again would switch it off and on.
        _camera.OnDisable = node => node.MarkDisabled().ConfigFlags |= ConfigFlagDisabled;
        _camera.OnEnable = node => node.MarkEnabled().ConfigFlags &= ~ConfigFlagDisabled;
        CfgMgrPnpBackend backend = Backend();

        Assert.Equal(new PnpChange(PnpChangeResult.Failed, CfgMgrPnpBackend.PersistentDetail), backend.Disable(Camera));
        Assert.Equal(Camera, Assert.Single(_cfgMgr.EnableCalls));
        Assert.Equal(0u, _camera.ConfigFlags & ConfigFlagDisabled);

        Assert.Equal(PnpChangeResult.Failed, backend.Disable(Camera).Result);
        Assert.Single(_cfgMgr.DisableCalls);
    }

    [Fact]
    public void APersistentDisableThatCantBeTakenBackIsBlindsDownsToUndoAtUnlock()
    {
        _camera.OnDisable = node => node.MarkDisabled().ConfigFlags |= ConfigFlagDisabled;
        _camera.EnableResult = CrFailure;
        _camera.OnEnable = _ => { };
        CfgMgrPnpBackend backend = Backend();

        // It is off, by BlindsDown: Done, so the lock owns it and turns it back on at unlock. The caller is told it
        // is persistent, so it can record that.
        Assert.Equal(PnpChange.Done with { MadePersistent = true }, backend.Disable(Camera));

        _camera.EnableResult = CrSuccess;
        _camera.OnEnable = node => node.MarkEnabled().ConfigFlags &= ~ConfigFlagDisabled;
        Assert.Equal(PnpChange.Done, backend.Enable(Camera));
        Assert.Equal(0u, _camera.ConfigFlags);
        Assert.Equal(0u, _camera.Problem);
    }

    [Fact]
    public void APersistentDisableThatCantBeTakenBackIsStillBlindsDownsAfterTheHelperRestarts()
    {
        _camera.OnDisable = node => node.MarkDisabled().ConfigFlags |= ConfigFlagDisabled;
        _camera.EnableResult = CrFailure;
        _camera.OnEnable = _ => { };
        Assert.True(Backend().Disable(Camera).MadePersistent);
        _cfgMgr.EnableCalls.Clear();
        _camera.EnableResult = CrSuccess;
        _camera.OnEnable = node => node.MarkEnabled().ConfigFlags &= ~ConfigFlagDisabled;

        // A new helper (a new backend) no longer remembers it: without the caller's record it is kept off ...
        Assert.Equal(PnpChangeResult.KeptOff, Backend().Enable(Camera).Result);
        Assert.Empty(_cfgMgr.EnableCalls);

        // ... and with it, it is turned back on, and stays on after a restart.
        Assert.Equal(PnpChange.Done, Backend().Enable(Camera, clearPersistent: true));
        Assert.Equal(Camera, Assert.Single(_cfgMgr.EnableCalls));
        Assert.Equal(0u, _camera.ConfigFlags & ConfigFlagDisabled);
    }

    [Fact]
    public void ADevicePersistentlyDisabledBeforeTheLockIsStillLockedWhileRunning()
    {
        // Turned off in Device Manager, but Windows needs a restart to stop it: it is running, so it is locked. The
        // flag was already there, so it is not mistaken for a disable that Windows made persistent.
        _camera.ConfigFlags = ConfigFlagDisabled;
        CfgMgrPnpBackend backend = Backend();

        Assert.Equal(PnpChange.Done, backend.Disable(Camera));
        Assert.Empty(_cfgMgr.EnableCalls);

        // At unlock it stays off, as whoever turned it off asked (a restart would have done the same).
        Assert.Equal(new PnpChange(PnpChangeResult.KeptOff, CfgMgrPnpBackend.KeptOffDetail), backend.Enable(Camera));
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    [Fact]
    public void ADeviceSomeoneTurnedOffPersistentlyStaysOff()
    {
        // BlindsDown disabled the camera, then someone turned it off for good (Device Manager, Settings, an
        // administrator). Even when the caller believes the disable is BlindsDown's, it is not undone: KeptOff, its
        // own answer, so the caller neither reports a failure nor tries again.
        _camera.MarkDisabled().ConfigFlags = ConfigFlagDisabled;

        Assert.Equal(new PnpChange(PnpChangeResult.KeptOff, CfgMgrPnpBackend.KeptOffDetail), Backend().Enable(Camera));
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    [Fact]
    public void ASuccessThatDidntTakeEffectIsUndoneAndFailed()
    {
        _camera.OnDisable = _ => { };

        PnpChange change = Backend().Disable(Camera);

        Assert.Equal(PnpChangeResult.Failed, change.Result);
        Assert.Contains("still on", change.Detail, StringComparison.Ordinal);
        Assert.Equal(Camera, Assert.Single(_cfgMgr.EnableCalls));
    }

    [Fact]
    public void ASuccessThatCantBeCheckedIsUndoneAndNeverClaimedAsOn()
    {
        _camera.OnDisable = node => node.StatusError = CrFailure;

        PnpChange change = Backend().Disable(Camera);

        Assert.Equal(PnpChangeResult.Failed, change.Result);
        Assert.Contains("could not check", change.Detail, StringComparison.Ordinal);
        Assert.Equal(Camera, Assert.Single(_cfgMgr.EnableCalls));
    }

    [Fact]
    public void ADeviceWindowsWontDisableIsAFailure()
    {
        _camera.DisableResult = CrNotDisableable;
        _camera.OnDisable = _ => { };

        Assert.Equal(PnpChangeResult.Failed, Backend().Disable(Camera).Result);
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    [Fact]
    public void OtherErrorsAreFailuresWithTheirCode()
    {
        _camera.DisableResult = CrFailure;
        _camera.OnDisable = _ => { };

        PnpChange change = Backend().Disable(Camera);

        Assert.Equal(PnpChangeResult.Failed, change.Result);
        Assert.Contains("CR_FAILURE", change.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AStatusThatCantBeReadStopsADisable()
    {
        _camera.StatusError = CrFailure;

        Assert.Equal(PnpChangeResult.Failed, Backend().Disable(Camera).Result);
        Assert.Empty(_cfgMgr.DisableCalls);
    }

    [Fact]
    public void ADisableWhoseDeviceListCantBeReadIsAFailureNotACrash()
    {
        _cfgMgr.InterfaceListErrors[PnpKeys.VideoCameraInterface] = CrOutOfMemory;

        PnpChange change = Backend().Disable(Camera);

        Assert.Equal(PnpChangeResult.Failed, change.Result);
        Assert.Contains("CR_OUT_OF_MEMORY", change.Detail, StringComparison.Ordinal);
        Assert.Empty(_cfgMgr.DisableCalls);
    }

    [Fact]
    public void AnEnableIsVerified()
    {
        _camera.MarkDisabled();

        Assert.Equal(PnpChange.Done, Backend().Enable(Camera));
        Assert.Equal(Camera, Assert.Single(_cfgMgr.EnableCalls));
    }

    [Fact]
    public void ADeviceThatIsOnIsNotTouchedByEnable()
    {
        Assert.Equal(PnpChange.Done, Backend().Enable(Camera));
        Assert.Empty(_cfgMgr.EnableCalls);
    }

    [Fact]
    public void EnableWorksEvenWhenDevicesCantBeListed()
    {
        // Turning a device back on must never depend on anything but the device itself.
        _mic.MarkDisabled();
        _cfgMgr.InterfaceListErrors[PnpKeys.VideoCameraInterface] = CrOutOfMemory;
        _endpoints.Fail = true;

        Assert.Equal(PnpChange.Done, Backend().Enable(WebcamMic));
    }

    [Fact]
    public void AnEnableThatDidntTakeEffectIsAFailure()
    {
        _camera.MarkDisabled();
        _camera.OnEnable = _ => { };

        PnpChange change = Backend().Enable(Camera);

        Assert.Equal(PnpChangeResult.Failed, change.Result);
        Assert.Contains("still off", change.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableReportsAccessDeniedAndOtherErrors()
    {
        _camera.MarkDisabled();
        _camera.EnableResult = CrAccessDenied;
        _camera.OnEnable = _ => { };
        Assert.Equal(PnpChangeResult.AccessDenied, Backend().Enable(Camera).Result);

        _camera.EnableResult = CrFailure;
        PnpChange change = Backend().Enable(Camera);
        Assert.Equal(PnpChangeResult.Failed, change.Result);
        Assert.Contains("CR_FAILURE", change.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnableThatNeedsARestartSaysSo()
    {
        _camera.MarkDisabled();
        _camera.OnEnable = node =>
        {
            node.Status = DnHasProblem;
            node.Problem = CmProbNeedRestart;
        };

        Assert.Equal(PnpChangeResult.NeedsRestart, Backend().Enable(Camera).Result);
    }

    [Fact]
    public void ADeviceUnpluggedDuringTheEnableIsNotFound()
    {
        _camera.MarkDisabled();
        _camera.EnableResult = CrNoSuchDevNode;
        _camera.OnEnable = node => node.Present = false;

        Assert.Equal(PnpChangeResult.NotFound, Backend().Enable(Camera).Result);
    }

    private CfgMgrPnpBackend Backend() => new(_cfgMgr, _endpoints);
}
