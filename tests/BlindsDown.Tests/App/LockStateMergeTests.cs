using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;

namespace BlindsDown.Tests.App;

/// <summary>How the app combines the microphones' lock state with the camera helper's.</summary>
public sealed class LockStateMergeTests
{
    [Theory]
    [InlineData(LockState.Unlocked, LockState.Unlocked, LockState.Unlocked)]
    [InlineData(LockState.Locked, LockState.Locked, LockState.Locked)]
    [InlineData(LockState.Locked, LockState.Unlocked, LockState.Locked)]
    [InlineData(LockState.Locked, LockState.PartiallyLocked, LockState.PartiallyLocked)]
    [InlineData(LockState.PartiallyLocked, LockState.Locked, LockState.PartiallyLocked)]
    [InlineData(LockState.PartiallyLocked, LockState.Unlocked, LockState.PartiallyLocked)]
    [InlineData(LockState.Unlocked, LockState.Locked, LockState.PartiallyLocked)]
    [InlineData(LockState.Unlocked, LockState.PartiallyLocked, LockState.PartiallyLocked)]
    [InlineData(LockState.Locking, LockState.Unlocked, LockState.Locking)]
    [InlineData(LockState.Locked, LockState.Unlocking, LockState.Unlocking)]
    public void Merges_microphone_and_helper_states(LockState microphones, LockState helper, LockState expected) =>
        Assert.Equal(expected, AppHost.MergeState(microphones, helper));

    [Fact]
    public void Only_cameras_locked_is_never_shown_as_unlocked()
    {
        // Otherwise a click would try to lock again instead of unlocking the cameras.
        Assert.NotEqual(LockState.Unlocked, AppHost.MergeState(LockState.Unlocked, LockState.Locked));
    }

    [Fact]
    public void Helper_status_signature_ignores_new_objects_with_the_same_content()
    {
        DeviceLockStatus a = Status(DeviceProtection.DeviceDisabled);
        DeviceLockStatus b = Status(DeviceProtection.DeviceDisabled);

        Assert.NotSame(a, b);
        Assert.Equal(HelperLink.Signature(a), HelperLink.Signature(b));
    }

    [Fact]
    public void Helper_status_signature_changes_with_protection()
    {
        Assert.NotEqual(
            HelperLink.Signature(Status(DeviceProtection.DeviceDisabled)),
            HelperLink.Signature(Status(DeviceProtection.None)));
    }

    private static DeviceLockStatus Status(DeviceProtection protection) => new(
        LockState.Locked,
        [new DeviceLockInfo(@"USB\VID_046D&PID_0825&MI_00\7&1A2B3C4D&0&0000", "USB Webcam", "cam|X|Y", DeviceKind.Camera, false, false, protection, null, CameraType.Color)],
        []);
}
