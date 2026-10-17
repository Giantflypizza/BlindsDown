using System.Collections.Concurrent;
using System.Diagnostics;
using BlindsDown.Audio;
using BlindsDown.Core.Devices;
using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.CoreAudio;
using BlindsDown.Usage;

namespace BlindsDown.Tests.Audio;

/// <summary>
/// READ-ONLY tests against this PC's real microphones: they enumerate endpoints, read mute states and
/// sessions, and register/unregister notifications. They never mute, unmute, disable or open a stream.
/// Skipped unless BLINDSDOWN_DEVICE_TESTS=1, because CI machines have no audio devices.
/// </summary>
public sealed class RealMicrophoneTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void EnumeratesEveryMicrophoneWithConsistentDetails()
    {
        SkipUnlessEnabled();
        using var engine = new TestEngine();

        IReadOnlyList<MicEndpoint> mics = engine.Thread.Invoke(
            () =>
            {
                using var backend = new CoreAudioMicBackend();
                return backend.EnumerateMics();
            },
            Timeout);

        foreach (MicEndpoint mic in mics)
        {
            output.WriteLine($"{mic.State,-10} muted={Show(mic.Muted),-5} hwMute={mic.HasHardwareMute,-5} virtual={mic.IsVirtual,-5} {mic.Name} [{mic.EndpointDescription}] <{mic.HardwareInstanceId}> {mic.Id}");
        }

        Assert.NotEmpty(mics);
        Assert.Equal(mics.Count, mics.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(mics, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
        Assert.All(mics, m => Assert.False(string.IsNullOrWhiteSpace(m.HardwareInstanceId), $"{m.Name} has no hardware ID"));
        Assert.All(mics.Where(m => m.State == EndpointState.NotPresent), m => Assert.Null(m.Muted));
        Assert.All(mics.Where(m => m.State == EndpointState.Active), m => Assert.NotNull(m.Muted));

        // Facts about well-known devices, checked only where those devices exist.
        foreach (MicEndpoint sonar in mics.Where(m => m.Name.Contains("Sonar", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.True(sonar.IsVirtual, $"{sonar.Name} should be virtual (hardware {sonar.HardwareInstanceId})");
        }

        foreach (MicEndpoint arctis in mics.Where(m => m.Name.Contains("Arctis", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Equal("USB", DeviceIds.Enumerator(arctis.HardwareInstanceId));
            Assert.False(arctis.IsVirtual);
        }

        engine.AssertNoErrors();
    }

    [Fact]
    public void ReadsDefaultsAndMuteStatesWithoutThrowing()
    {
        SkipUnlessEnabled();
        using var engine = new TestEngine();

        (IReadOnlyList<MicEndpoint> mics, DefaultMics defaults, List<(MicEndpoint Mic, bool? Muted)> reread, bool? unknown) = engine.Thread.Invoke(
            () =>
            {
                using var backend = new CoreAudioMicBackend();
                IReadOnlyList<MicEndpoint> all = backend.EnumerateMics();
                return (all, backend.GetDefaultMics(), all.Select(m => (m, backend.GetMute(m.Id))).ToList(), backend.GetMute("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}"));
            },
            Timeout);

        output.WriteLine($"Default console:        {Describe(mics, defaults.Console)}");
        output.WriteLine($"Default multimedia:     {Describe(mics, defaults.Multimedia)}");
        output.WriteLine($"Default communications: {Describe(mics, defaults.Communications)}");

        foreach (string? id in new[] { defaults.Console, defaults.Multimedia, defaults.Communications })
        {
            if (id is not null)
            {
                Assert.Contains(mics, m => m.Id == id && m.State == EndpointState.Active);
            }
        }

        // GetMute by ID must agree with the enumeration (a user could flip a mute in between; very unlikely).
        Assert.All(reread, r => Assert.Equal(r.Mic.Muted, r.Muted));
        Assert.Null(unknown);
        engine.AssertNoErrors();
    }

    [Fact]
    public void WatcherSubscribesToEveryActivatableMicrophoneAndUnsubscribesOnDispose()
    {
        SkipUnlessEnabled();
        using var engine = new TestEngine();
        var watcher = new AudioDeviceWatcher(engine.Thread);
        var endpointEvents = new ConcurrentQueue<string>();
        var otherEvents = new ConcurrentQueue<string>();
        watcher.EndpointChanged += endpointEvents.Enqueue;
        watcher.MuteChanged += (id, muted, ours) => otherEvents.Enqueue($"mute {id} {muted} ours={ours}");
        watcher.DefaultDeviceChanged += () => otherEvents.Enqueue("default changed");

        try
        {
            (int expected, int watched, int watchedAfterCheck) = engine.Thread.Invoke(
                () =>
                {
                    using var backend = new CoreAudioMicBackend();
                    int activatable = backend.EnumerateMics().Count(m => (m.State & CoreAudioConstants.ActivatableStates) != 0);
                    watcher.Start();
                    int subscribed = watcher.WatchedEndpointCount;

                    // The periodic check on unchanged devices must keep every subscription and report nothing.
                    watcher.PeriodicCheck();
                    return (activatable, subscribed, watcher.WatchedEndpointCount);
                },
                Timeout);

            output.WriteLine($"Activatable microphones: {expected}, mute subscriptions: {watched}, after a periodic check: {watchedAfterCheck}");
            Assert.Equal(expected, watched);
            Assert.Equal(watched, watchedAfterCheck);
            Assert.Empty(endpointEvents);
        }
        finally
        {
            watcher.Dispose(); // From the test thread: the teardown must still run on the engine thread.
        }

        Assert.Equal(0, engine.Thread.Invoke(() => watcher.WatchedEndpointCount, Timeout));

        foreach (string e in otherEvents)
        {
            output.WriteLine("Event during the test: " + e);
        }

        engine.AssertNoErrors();
    }

    [Fact]
    public void SessionMonitorFindsActiveCaptureSessions()
    {
        SkipUnlessEnabled();
        using var engine = new TestEngine();
        var monitor = new MicSessionMonitor(engine.Thread, new ProcessNameResolver());

        try
        {
            (IReadOnlyList<UsageSignal> first, IReadOnlyList<UsageSignal> second, int tracked, IReadOnlyList<MicEndpoint> mics) = engine.Thread.Invoke(
                () =>
                {
                    using var backend = new CoreAudioMicBackend();
                    monitor.Start();
                    return (monitor.GetActiveSignals(), monitor.GetActiveSignals(), monitor.TrackedSessionCount, backend.EnumerateMics());
                },
                Timeout);

            output.WriteLine($"Sessions followed on active microphones (active or not, including Windows' own): {tracked}");
            Assert.True(tracked >= first.Count);
            foreach (UsageSignal signal in first)
            {
                output.WriteLine($"{signal.App.DisplayName} (PID {signal.ProcessId}) is capturing from \"{signal.DeviceName}\"{(signal.ViaApp is null ? string.Empty : $" via {signal.ViaApp}")}");
            }

            if (first.Count == 0)
            {
                output.WriteLine("No app is capturing from a microphone right now.");
            }

            Assert.All(first, s =>
            {
                Assert.Equal(CaptureKind.Microphone, s.Kind);
                Assert.True(s.ProcessId > 0);
                Assert.False(s.DeviceUnknown);
                Assert.False(string.IsNullOrWhiteSpace(s.DeviceName));

                // Sessions are only read on active microphones, and only virtual ones name the app that relays them.
                MicEndpoint mic = Assert.Single(mics, m => m.Id == s.DeviceId);
                Assert.Equal(EndpointState.Active, mic.State);
                if (!mic.IsVirtual)
                {
                    Assert.Null(s.ViaApp);
                }
            });
            Assert.Equal(first.Count, first.Select(s => (s.ProcessId, s.DeviceId)).Distinct().Count());
            Assert.Equal(
                first.Select(s => $"{s.ProcessId}|{s.DeviceId}").Order(StringComparer.Ordinal),
                second.Select(s => $"{s.ProcessId}|{s.DeviceId}").Order(StringComparer.Ordinal));
        }
        finally
        {
            monitor.Dispose(); // From the test thread: the teardown must still run on the engine thread.
        }

        Assert.Equal(0, engine.Thread.Invoke(() => monitor.TrackedSessionCount, Timeout));
        Assert.Empty(engine.Thread.Invoke(monitor.GetActiveSignals, Timeout));
        engine.AssertNoErrors();
    }

    private static void SkipUnlessEnabled() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("BLINDSDOWN_DEVICE_TESTS") == "1",
            "Reads this PC's real microphones; set BLINDSDOWN_DEVICE_TESTS=1 to run.");

    private static string Show(bool? value) => value?.ToString() ?? "?";

    private static string Describe(IReadOnlyList<MicEndpoint> mics, string? id) =>
        id is null ? "(none)" : mics.FirstOrDefault(m => m.Id == id)?.Name ?? id;

    /// <summary>An engine thread like the app's, collecting anything that escapes to its error handler.</summary>
    private sealed class TestEngine : IDisposable
    {
        private readonly ConcurrentQueue<Exception> _errors = new();

        public TestEngine()
        {
            Thread = new EngineThread("BlindsDown test engine", _errors.Enqueue);
        }

        public EngineThread Thread { get; }

        public void AssertNoErrors() => Assert.Empty(_errors);

        public void Dispose() => Thread.Dispose();
    }

    /// <summary>Stand-in for the real resolver: the process name is enough to see who is capturing.</summary>
    private sealed class ProcessNameResolver : IProcessIdentityResolver
    {
        public AppIdentity Resolve(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return new AppIdentity("pid:" + processId, process.ProcessName, null, null);
            }
            catch (ArgumentException)
            {
                return AppIdentity.Unknown;
            }
            catch (InvalidOperationException)
            {
                return AppIdentity.Unknown;
            }
        }

        public bool IsExeRunning(string exePath) => false;

        public bool IsPackageRunning(string packageFamilyName) => false;
    }
}
