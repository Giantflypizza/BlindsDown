using BlindsDown.Audio;
using BlindsDown.Core.Devices;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Tests.Audio;

/// <summary>
/// The stronger microphone lock's switches (<see cref="EndpointSwitch"/>) against a fake Windows: check first, change,
/// verify, and classify failures. The real switches change devices for every user of the PC, so they are never
/// called in tests.
/// </summary>
public class EndpointSwitchTests
{
    private const string Mic = "{0.0.1.00000000}.{6b0c3a1e-2f4d-4e8a-9c71-0d5e3f2a1b9c}";
    private const string OtherMic = "{0.0.1.00000000}.{1d2c3b4a-5f6e-4d7c-8b9a-0a1b2c3d4e5f}";
    private const string Speakers = "{0.0.0.00000000}.{9a8b7c6d-5e4f-4a3b-2c1d-0e9f8a7b6c5d}";

    [Theory]
    [InlineData(EndpointState.Active)]
    [InlineData(EndpointState.Unplugged)]
    public void TurnsAMicrophoneOffAndVerifies(EndpointState before)
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, before);

        EndpointSwitch.SetEnabled(port, Mic, enabled: false);

        Assert.Equal([(Mic, false)], port.VisibilityCalls);
        Assert.Equal(EndpointState.Disabled, port.Endpoints[Mic].State);
        Assert.Empty(port.Pauses);
    }

    [Theory]
    [InlineData(EndpointState.Active)]
    [InlineData(EndpointState.Unplugged)]
    public void TurnsAMicrophoneBackOnAndAcceptsActiveOrUnplugged(EndpointState restoredTo)
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, EndpointState.Active);
        port.DisabledEarlierFrom(Mic, restoredTo);

        EndpointSwitch.SetEnabled(port, Mic, enabled: true);

        Assert.Equal([(Mic, true)], port.VisibilityCalls);
        Assert.Equal(restoredTo, port.Endpoints[Mic].State);
    }

    [Theory]
    [InlineData(EndpointState.Disabled, false)]
    [InlineData(EndpointState.Active, true)]
    [InlineData(EndpointState.Unplugged, true)]
    public void AMicrophoneAlreadyAsRequestedIsLeftAlone(EndpointState state, bool enabled)
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, state);

        EndpointSwitch.SetEnabled(port, Mic, enabled);

        Assert.Empty(port.VisibilityCalls);
        Assert.Equal(state, port.Endpoints[Mic].State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMicrophoneThatIsNotConnectedCantBeSwitched(bool enabled)
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, EndpointState.NotPresent);

        var ex = Assert.Throws<DeviceOperationException>(() => EndpointSwitch.SetEnabled(port, Mic, enabled));

        Assert.Equal(DeviceErrorKind.Failed, ex.ErrorKind);
        Assert.Contains("isn't connected", ex.Message, StringComparison.Ordinal);
        Assert.Empty(port.VisibilityCalls);
    }

    [Fact]
    public void AnUnknownEndpointIsRefused()
    {
        var port = new FakeSwitchPort();

        var off = Assert.Throws<DeviceOperationException>(() => EndpointSwitch.SetEnabled(port, Mic, enabled: false));
        var standard = Assert.Throws<DeviceOperationException>(() => EndpointSwitch.SetDefault(port, Mic, DeviceRole.Console));

        Assert.Equal(DeviceErrorKind.Failed, off.ErrorKind);
        Assert.Contains("no longer lists", off.Message, StringComparison.Ordinal);
        Assert.Equal(DeviceErrorKind.Failed, standard.ErrorKind);
        Assert.Empty(port.VisibilityCalls);
        Assert.Empty(port.DefaultCalls);
    }

    [Theory]
    [InlineData((int)DataFlow.Render)]
    [InlineData(null)]
    public void OnlyMicrophonesAreEverSwitched(int? flow)
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Speakers, EndpointState.Active, (DataFlow?)flow);

        Assert.Throws<DeviceOperationException>(() => EndpointSwitch.SetEnabled(port, Speakers, enabled: false));
        Assert.Throws<DeviceOperationException>(() => EndpointSwitch.SetDefault(port, Speakers, DeviceRole.Multimedia));

        Assert.Empty(port.VisibilityCalls);
        Assert.Empty(port.DefaultCalls);
        Assert.Equal(EndpointState.Active, port.Endpoints[Speakers].State);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), DeviceErrorKind.AccessDenied)] // E_ACCESSDENIED
    [InlineData(unchecked((int)0x80070522), DeviceErrorKind.AccessDenied)] // ERROR_PRIVILEGE_NOT_HELD
    [InlineData(unchecked((int)0x80004001), DeviceErrorKind.NotSupported)] // E_NOTIMPL
    [InlineData(unchecked((int)0x80040154), DeviceErrorKind.NotSupported)] // REGDB_E_CLASSNOTREG
    [InlineData(unchecked((int)0x80004002), DeviceErrorKind.NotSupported)] // E_NOINTERFACE
    [InlineData(unchecked((int)0x80004005), DeviceErrorKind.Failed)] // E_FAIL
    [InlineData(unchecked((int)0x80070490), DeviceErrorKind.Failed)] // ERROR_NOT_FOUND
    [InlineData(unchecked((int)0x88890004), DeviceErrorKind.Failed)] // AUDCLNT_E_DEVICE_INVALIDATED
    public void FailuresAreClassified(int hr, DeviceErrorKind expected)
    {
        Assert.Equal(expected, EndpointSwitch.ErrorKindFor(hr));

        FakeSwitchPort port = new FakeSwitchPort().With(Mic, EndpointState.Active).With(OtherMic, EndpointState.Active);
        port.VisibilityResult = hr;
        port.DefaultResult = hr;

        var off = Assert.Throws<DeviceOperationException>(() => EndpointSwitch.SetEnabled(port, Mic, enabled: false));
        var standard = Assert.Throws<DeviceOperationException>(() => EndpointSwitch.SetDefault(port, OtherMic, DeviceRole.Communications));

        Assert.Equal(expected, off.ErrorKind);
        Assert.Equal(expected, standard.ErrorKind);
        Assert.Equal(EndpointSwitch.Explain(hr), off.Message);
        Assert.Equal(EndpointState.Active, port.Endpoints[Mic].State);
        Assert.Empty(port.Pauses); // A refused change is not waited for.
    }

    [Fact]
    public void ExplanationsSayWhatTheUserCanDo()
    {
        Assert.Equal("Windows requires administrator rights for this (0x80070005)", EndpointSwitch.Explain(unchecked((int)0x80070005)));
        Assert.Equal("this version of Windows doesn't support it (0x80040154)", EndpointSwitch.Explain(unchecked((int)0x80040154)));
        Assert.Equal("Windows reported error 0x80004005", EndpointSwitch.Explain(unchecked((int)0x80004005)));
    }

    [Fact]
    public void AnAudioServiceFailureIsLeftForTheReconnectAndRetry()
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, EndpointState.Active).With(OtherMic, EndpointState.Active);
        port.VisibilityResult = CoreAudioErrors.ServiceNotRunning;
        port.DefaultResult = CoreAudioErrors.ServiceNotRunning;

        var off = Assert.Throws<CoreAudioException>(() => EndpointSwitch.SetEnabled(port, Mic, enabled: false));
        var standard = Assert.Throws<CoreAudioException>(() => EndpointSwitch.SetDefault(port, OtherMic, DeviceRole.Console));

        Assert.Equal(CoreAudioErrors.ServiceNotRunning, off.HResult);
        Assert.Equal(CoreAudioErrors.ServiceNotRunning, standard.HResult);
    }

    [Fact]
    public void ARetryAfterTheChangeAlreadyHappenedDoesNothing()
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, EndpointState.Active);

        EndpointSwitch.SetEnabled(port, Mic, enabled: false);
        EndpointSwitch.SetEnabled(port, Mic, enabled: false);

        Assert.Single(port.VisibilityCalls);
    }

    [Fact]
    public void AChangeThatShowsAfterAMomentIsWaitedFor()
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, EndpointState.Active);
        port.PausesBeforeChangeShows = 3;

        EndpointSwitch.SetEnabled(port, Mic, enabled: false);

        Assert.Equal(EndpointState.Disabled, port.Endpoints[Mic].State);
        Assert.Equal(3, port.Pauses.Count);
        Assert.All(port.Pauses, p => Assert.Equal(EndpointSwitch.PollInterval, p));
    }

    [Theory]
    [InlineData(false, "still turned on")]
    [InlineData(true, "still turned off")]
    public void AChangeThatNeverShowsFailsAfterAboutTwoSeconds(bool enabled, string expected)
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, enabled ? EndpointState.Disabled : EndpointState.Active);
        port.IgnoreChanges = true;

        var ex = Assert.Throws<DeviceOperationException>(() => EndpointSwitch.SetEnabled(port, Mic, enabled));

        Assert.Equal(DeviceErrorKind.Failed, ex.ErrorKind);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(EndpointSwitch.MaxPolls, port.Pauses.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), EndpointSwitch.PollInterval * EndpointSwitch.MaxPolls);
    }

    [Fact]
    public void AMicrophoneThatVanishesWhileChangingIsReported()
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, EndpointState.Active);
        port.VanishOnChange = true;

        var ex = Assert.Throws<DeviceOperationException>(() => EndpointSwitch.SetEnabled(port, Mic, enabled: false));

        Assert.Contains("disappeared", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MicRole.Console)]
    [InlineData(MicRole.Multimedia)]
    [InlineData(MicRole.Communications)]
    public void PutsADefaultMicrophoneBackForOneRoleAndVerifies(MicRole micRole)
    {
        var role = (DeviceRole)micRole;
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, EndpointState.Active).With(OtherMic, EndpointState.Active);
        foreach (DeviceRole each in Enum.GetValues<DeviceRole>())
        {
            port.Defaults[each] = OtherMic;
        }

        EndpointSwitch.SetDefault(port, Mic, role);

        Assert.Equal([(Mic, role)], port.DefaultCalls);
        Assert.Equal(Mic, port.Defaults[role]);
        Assert.All(Enum.GetValues<DeviceRole>().Where(r => r != role), r => Assert.Equal(OtherMic, port.Defaults[r]));
    }

    [Fact]
    public void TheCurrentDefaultIsLeftAloneWhateverTheCase()
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, EndpointState.Active);
        port.Defaults[DeviceRole.Console] = Mic.ToUpperInvariant();

        EndpointSwitch.SetDefault(port, Mic, DeviceRole.Console);

        Assert.Empty(port.DefaultCalls);
    }

    [Theory]
    [InlineData(EndpointState.Disabled)]
    [InlineData(EndpointState.Unplugged)]
    [InlineData(EndpointState.NotPresent)]
    public void OnlyAnActiveMicrophoneCanBecomeTheDefault(EndpointState state)
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, state);

        var ex = Assert.Throws<DeviceOperationException>(() => EndpointSwitch.SetDefault(port, Mic, DeviceRole.Console));

        Assert.Equal(DeviceErrorKind.Failed, ex.ErrorKind);
        Assert.Contains("isn't available", ex.Message, StringComparison.Ordinal);
        Assert.Empty(port.DefaultCalls);
    }

    [Fact]
    public void ADefaultThatNeverChangesIsReported()
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, EndpointState.Active);
        port.Defaults[DeviceRole.Communications] = OtherMic;
        port.IgnoreChanges = true;

        var ex = Assert.Throws<DeviceOperationException>(() => EndpointSwitch.SetDefault(port, Mic, DeviceRole.Communications));

        Assert.Contains("another microphone is still the default", ex.Message, StringComparison.Ordinal);
        Assert.Equal(EndpointSwitch.MaxPolls, port.Pauses.Count);
    }

    [Fact]
    public void ADefaultThatShowsAfterAMomentIsWaitedFor()
    {
        FakeSwitchPort port = new FakeSwitchPort().With(Mic, EndpointState.Active);
        port.PausesBeforeChangeShows = 2;

        EndpointSwitch.SetDefault(port, Mic, DeviceRole.Multimedia);

        Assert.Equal(Mic, port.Defaults[DeviceRole.Multimedia]);
        Assert.Equal(2, port.Pauses.Count);
    }

    [Fact]
    public void TheBackendRejectsBadArgumentsBeforeTouchingWindows()
    {
        // Creating the backend creates no Windows objects; these calls fail before the first one would be made.
        using var backend = new CoreAudioMicBackend();

        Assert.Throws<ArgumentOutOfRangeException>(() => backend.SetDefaultMic(Mic, (MicRole)7));
        Assert.Throws<ArgumentException>(() => backend.SetEndpointEnabled(" ", enabled: false));
        Assert.Throws<ArgumentException>(() => backend.SetDefaultMic(string.Empty, MicRole.Console));
    }

    [Fact]
    public void MicRolesAreTheWindowsRoles()
    {
        Assert.Equal((int)DeviceRole.Console, (int)MicRole.Console);
        Assert.Equal((int)DeviceRole.Multimedia, (int)MicRole.Multimedia);
        Assert.Equal((int)DeviceRole.Communications, (int)MicRole.Communications);
    }
}
