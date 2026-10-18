using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BlindsDown.Audio;
using BlindsDown.Core.Devices;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Tests.Audio;

/// <summary>
/// READ-ONLY checks of the stronger-lock plumbing on this PC. They create the Sound settings policy object (a
/// QueryInterface, nothing else), read endpoints and defaults the way the switches verify them, and run the switches'
/// "nothing to do" paths through a port whose setters fail the test instead of reaching Windows. No microphone is ever
/// turned off, turned on or made the default. Skipped unless BLINDSDOWN_DEVICE_TESTS=1.
/// </summary>
public sealed class RealPolicyConfigTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void ThePolicyConfigClientExistsAndAnswersForIPolicyConfig()
    {
        SkipUnlessEnabled();
        using var engine = new TestEngine();

        bool answered = engine.Thread.Invoke(
            () =>
            {
                // CoCreateInstance + QueryInterface only. No method of the object is called.
                object instance = new PolicyConfigClientCoClass();
                try
                {
                    return instance is IPolicyConfig;
                }
                finally
                {
                    _ = Marshal.ReleaseComObject(instance);
                }
            },
            Timeout);

        Assert.True(answered);
        engine.AssertNoErrors();
    }

    [Fact]
    public void TheSwitchPortReadsEveryMicrophoneAndDefaultLikeTheBackend()
    {
        SkipUnlessEnabled();
        using var engine = new TestEngine();

        (List<(MicEndpoint Mic, EndpointSwitchStatus? Status)> read, DefaultMics defaults, DefaultMics viaPort) = engine.Thread.Invoke(
            () =>
            {
                using var backend = new CoreAudioMicBackend();
                using var enumerator = new EndpointEnumerator("Policy test");
                var port = new CoreAudioSwitchPort(enumerator, new EndpointPolicyClient());
                List<(MicEndpoint, EndpointSwitchStatus?)> statuses = [.. backend.EnumerateMics().Select(m => (m, port.ReadEndpoint(m.Id)))];
                return (statuses, backend.GetDefaultMics(), new DefaultMics(
                    port.ReadDefaultMic(DeviceRole.Console),
                    port.ReadDefaultMic(DeviceRole.Multimedia),
                    port.ReadDefaultMic(DeviceRole.Communications)));
            },
            Timeout);

        foreach ((MicEndpoint mic, EndpointSwitchStatus? status) in read)
        {
            output.WriteLine($"{status?.State,-10} {status?.Flow,-8} {mic.Name}");
        }

        Assert.NotEmpty(read);
        Assert.All(read, r =>
        {
            Assert.NotNull(r.Status);
            Assert.Equal(r.Mic.State, r.Status.Value.State);
            Assert.Equal(DataFlow.Capture, r.Status.Value.Flow);
        });
        Assert.Equal(defaults, viaPort);
        engine.AssertNoErrors();
    }

    [Fact]
    public void NothingToDoNeverReachesWindows()
    {
        SkipUnlessEnabled();
        using var engine = new TestEngine();

        (List<string> outcomes, List<string> attempts) = engine.Thread.Invoke(
            () =>
            {
                using var backend = new CoreAudioMicBackend();
                using var enumerator = new EndpointEnumerator("Policy test");
                var guarded = new ReadOnlyPort(new CoreAudioSwitchPort(enumerator, new EndpointPolicyClient()));
                var results = new List<string>();
                foreach (MicEndpoint mic in backend.EnumerateMics())
                {
                    // Already on stays on, already off stays off; a missing one is refused before any change.
                    bool enabled = mic.State != EndpointState.Disabled;
                    try
                    {
                        EndpointSwitch.SetEnabled(guarded, mic.Id, enabled);
                        results.Add($"{mic.State}: left {(enabled ? "on" : "off")}: {mic.Name}");
                    }
                    catch (DeviceOperationException ex)
                    {
                        Assert.Equal(EndpointState.NotPresent, mic.State);
                        results.Add($"{mic.State}: refused ({ex.Message}): {mic.Name}");
                    }
                }

                DefaultMics defaults = backend.GetDefaultMics();
                foreach ((DeviceRole role, string? id) in new[] { (DeviceRole.Console, defaults.Console), (DeviceRole.Multimedia, defaults.Multimedia), (DeviceRole.Communications, defaults.Communications) })
                {
                    if (id is not null)
                    {
                        EndpointSwitch.SetDefault(guarded, id, role);
                        results.Add($"{role} default left as is");
                    }
                }

                return (results, guarded.Attempts);
            },
            Timeout);

        foreach (string outcome in outcomes)
        {
            output.WriteLine(outcome);
        }

        Assert.Empty(attempts);
        engine.AssertNoErrors();
    }

    private static void SkipUnlessEnabled() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("BLINDSDOWN_DEVICE_TESTS") == "1",
            "Reads this PC's real microphones; set BLINDSDOWN_DEVICE_TESTS=1 to run.");

    /// <summary>Reads through the real port; the setters only record the attempt and report failure, never reaching Windows.</summary>
    private sealed class ReadOnlyPort(IEndpointSwitchPort reads) : IEndpointSwitchPort
    {
        public List<string> Attempts { get; } = [];

        public EndpointSwitchStatus? ReadEndpoint(string endpointId) => reads.ReadEndpoint(endpointId);

        public string? ReadDefaultMic(DeviceRole role) => reads.ReadDefaultMic(role);

        public int SetEndpointVisibility(string endpointId, bool visible)
        {
            Attempts.Add($"SetEndpointVisibility({endpointId}, {visible})");
            return CoreAudioErrors.AccessDenied;
        }

        public int SetDefaultEndpoint(string endpointId, DeviceRole role)
        {
            Attempts.Add($"SetDefaultEndpoint({endpointId}, {role})");
            return CoreAudioErrors.AccessDenied;
        }

        public void Pause(TimeSpan delay)
        {
        }
    }

    /// <summary>An engine thread like the app's, collecting anything that escapes to its error handler.</summary>
    private sealed class TestEngine : IDisposable
    {
        private readonly ConcurrentQueue<Exception> _errors = new();

        public TestEngine()
        {
            Thread = new EngineThread("BlindsDown policy test engine", _errors.Enqueue);
        }

        public EngineThread Thread { get; }

        public void AssertNoErrors() => Assert.Empty(_errors);

        public void Dispose() => Thread.Dispose();
    }
}
