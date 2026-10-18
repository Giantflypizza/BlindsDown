using System.Collections.Concurrent;
using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.Media;
using BlindsDown.Usage;
using CameraUse = BlindsDown.Usage.CameraActivityMonitor.CameraUse;

namespace BlindsDown.Tests.Camera;

/// <summary>
/// How camera activity reports become usage signals: merging, filtering Windows' own processes, naming the camera and
/// the app, and raising Changed on the engine thread once per real change. Fake reports and fake lookups only; no
/// camera, Media Foundation or Windows lookup is touched.
/// </summary>
public sealed class CameraActivityLogicTests : IDisposable
{
    private const string Webcam = @"\\?\USB#VID_046D&PID_085C&MI_00#7&1a2b3c4d&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL";
    private const string WebcamOtherInterface = @"\\?\USB#VID_046D&PID_085C&MI_00#7&1a2b3c4d&0&0000#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\GLOBAL";
    private const string WebcamInstance = @"USB\VID_046D&PID_085C&MI_00\7&1A2B3C4D&0&0000";
    private const string IrCamera = @"\\?\USB#VID_04F2&PID_B6BE&MI_02#6&2c3d4e5f&0&0002#{24e552d7-6523-47f7-a647-d3465bf1f5ca}\GLOBAL";
    private const string IrInstance = @"USB\VID_04F2&PID_B6BE&MI_02\6&2C3D4E5F&0&0002";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly EngineThread _engine;

    public CameraActivityLogicTests()
    {
        _engine = new EngineThread("BlindsDown camera test engine", _errors.Enqueue);
    }

    public void Dispose() => _engine.Dispose();

    [Fact]
    public void OnlyStreamingProcessesCount()
    {
        IReadOnlyList<CameraUse> uses = CameraActivityMonitor.Summarize(
        [
            new(Webcam, "Logitech BRIO", 1200, Streaming: true),
            new(Webcam, "Logitech BRIO", 1300, Streaming: false), // has it open, but no frames
        ]);

        CameraUse use = Assert.Single(uses);
        Assert.Equal(new CameraUse(Webcam, "Logitech BRIO", 1200), use);
    }

    [Fact]
    public void StreamingWinsWhenAReportContradictsItself()
    {
        IReadOnlyList<CameraUse> uses = CameraActivityMonitor.Summarize(
        [
            new(Webcam, "Logitech BRIO", 1200, Streaming: false),
            new(Webcam.ToLowerInvariant(), null, 1200, Streaming: true),
            new(Webcam, "Logitech BRIO", 1200, Streaming: true),
        ]);

        CameraUse use = Assert.Single(uses);
        Assert.Equal(1200, use.ProcessId);
        Assert.Equal("Logitech BRIO", use.ReportedName); // the first name any entry gave
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-5)]
    public void WindowsItselfIsNotAnApp(int processId)
    {
        Assert.Empty(CameraActivityMonitor.Summarize([new(Webcam, "Cam", processId, Streaming: true)]));
        Assert.Empty(CameraActivityMonitor.BuildSignals([new CameraUse(Webcam, "Cam", processId)], Resolve, App, new HashSet<int>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EntriesWithoutACameraAreDropped(string link) =>
        Assert.Empty(CameraActivityMonitor.Summarize([new(link, "Cam", 1200, Streaming: true)]));

    [Fact]
    public void EqualReportsGiveEqualSnapshotsWhateverTheOrder()
    {
        IReadOnlyList<CameraUse> first = CameraActivityMonitor.Summarize(
            [new(Webcam, "A", 1200, true), new(IrCamera, "B", 900, true), new(Webcam, "A", 800, true)]);
        IReadOnlyList<CameraUse> second = CameraActivityMonitor.Summarize(
            [new(Webcam, "A", 800, true), new(Webcam, "A", 1200, true), new(IrCamera, "B", 900, true)]);

        Assert.Equal(first, second);
        Assert.Equal(3, first.Count);
    }

    [Fact]
    public void SignalsNameTheDeviceByInstanceIdAndTheApp()
    {
        IReadOnlyList<UsageSignal> signals = CameraActivityMonitor.BuildSignals(
            [new CameraUse(Webcam, "Logitech BRIO", 1200), new CameraUse(IrCamera, null, 900)],
            Resolve,
            App,
            new HashSet<int>());

        Assert.Equal(2, signals.Count);
        UsageSignal teams = signals[0];
        Assert.Equal(CaptureKind.Camera, teams.Kind);
        Assert.Equal(1200, teams.ProcessId);
        Assert.Equal(WebcamInstance, teams.DeviceId);
        Assert.Equal("Logitech BRIO", teams.DeviceName); // the report's name comes first
        Assert.Equal("app1200", teams.App.Key);
        Assert.Null(teams.ViaApp);
        Assert.False(teams.DeviceUnknown);

        UsageSignal hello = signals[1];
        Assert.Equal(IrInstance, hello.DeviceId);
        Assert.Equal("Integrated IR Camera", hello.DeviceName); // the report had none: the device's name
    }

    [Fact]
    public void AnUnknownCameraKeepsItsLinkAndAPlainName()
    {
        const string gone = @"\\?\USB#VID_1234&PID_5678#gone#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL";

        UsageSignal signal = Assert.Single(CameraActivityMonitor.BuildSignals([new CameraUse(gone, null, 1200)], Resolve, App, new HashSet<int>()));

        Assert.Equal(gone, signal.DeviceId);
        Assert.Equal(CameraActivityMonitor.FallbackCameraName, signal.DeviceName);
        Assert.False(signal.DeviceUnknown);
    }

    [Fact]
    public void TwoInterfacesOfOneCameraAreOneSignalPerApp()
    {
        IReadOnlyList<UsageSignal> signals = CameraActivityMonitor.BuildSignals(
            [new CameraUse(Webcam, "BRIO", 1200), new CameraUse(WebcamOtherInterface, "BRIO", 1200), new CameraUse(WebcamOtherInterface, "BRIO", 1300)],
            Resolve,
            App,
            new HashSet<int>());

        Assert.Equal([(1200, WebcamInstance), (1300, WebcamInstance)], signals.Select(s => (s.ProcessId!.Value, s.DeviceId)));
    }

    [Fact]
    public void TheFrameServerIsNotAnApp()
    {
        IReadOnlyList<UsageSignal> signals = CameraActivityMonitor.BuildSignals(
            [new CameraUse(Webcam, "BRIO", 2222), new CameraUse(Webcam, "BRIO", 1200)],
            Resolve,
            App,
            new HashSet<int> { 2222 });

        Assert.Equal(1200, Assert.Single(signals).ProcessId);
    }

    [Fact]
    public void ChangedFiresOnTheEngineThreadOncePerChange()
    {
        using var monitor = new CameraActivityMonitor(_engine, new FakeIdentities(), Resolve, () => new HashSet<int>());
        var onEngine = new ConcurrentQueue<bool>();
        monitor.Changed += () => onEngine.Enqueue(_engine.IsCurrentThread);

        monitor.OnReport([new(Webcam, "BRIO", 1200, Streaming: true)]);
        Drain();
        monitor.OnReport([new(Webcam, "BRIO", 1200, Streaming: true), new(Webcam, "BRIO", 1300, Streaming: false)]); // same picture
        Drain();

        Assert.Equal([true], onEngine);
        IReadOnlyList<UsageSignal> signals = _engine.Invoke(monitor.GetActiveSignals, Timeout);
        UsageSignal signal = Assert.Single(signals);
        Assert.Equal(WebcamInstance, signal.DeviceId);
        Assert.Equal("BRIO", signal.DeviceName);
        Assert.Equal("app1200", signal.App.Key);

        monitor.OnReport([]); // the app stopped: the camera is gone from the report
        Drain();

        Assert.Equal(2, onEngine.Count);
        Assert.Empty(_engine.Invoke(monitor.GetActiveSignals, Timeout));
        Assert.Empty(_errors);
    }

    [Fact]
    public void ABurstOfReportsIsOneChanged()
    {
        using var monitor = new CameraActivityMonitor(_engine, new FakeIdentities(), Resolve, () => new HashSet<int>());
        int changed = 0;
        monitor.Changed += () => Interlocked.Increment(ref changed);
        using var gate = new ManualResetEventSlim();
        _engine.Post(() => gate.Wait(Timeout)); // Hold the engine thread while the reports arrive.

        monitor.OnReport([new(Webcam, "BRIO", 1200, true)]);
        monitor.OnReport([new(Webcam, "BRIO", 1300, true)]);
        monitor.OnReport([new(Webcam, "BRIO", 1400, true)]);
        gate.Set();
        Drain();

        Assert.Equal(1, changed);
        Assert.Equal(1400, Assert.Single(_engine.Invoke(monitor.GetActiveSignals, Timeout)).ProcessId);
    }

    [Fact]
    public void AnUnreadableReportKeepsThePreviousPicture()
    {
        using var monitor = new CameraActivityMonitor(_engine, new FakeIdentities(), Resolve, () => new HashSet<int>());
        monitor.OnReport([new(Webcam, "BRIO", 1200, true)]);

        monitor.OnReport(null);
        Drain();

        Assert.Equal(1200, Assert.Single(_engine.Invoke(monitor.GetActiveSignals, Timeout)).ProcessId);
    }

    [Fact]
    public void DeviceLookupsAreCachedOnlyWhenTheyWork()
    {
        var lookups = new List<string>();
        using var monitor = new CameraActivityMonitor(
            _engine,
            new FakeIdentities(),
            link =>
            {
                lookups.Add(link);
                return Resolve(link);
            },
            () => new HashSet<int>());
        const string gone = @"\\?\USB#gone";
        monitor.OnReport([new(Webcam, "BRIO", 1200, true), new(gone, null, 1200, true)]);

        _ = _engine.Invoke(monitor.GetActiveSignals, Timeout);
        _ = _engine.Invoke(monitor.GetActiveSignals, Timeout);

        Assert.Equal(1, lookups.Count(l => l == Webcam));
        Assert.Equal(2, lookups.Count(l => l == gone));
    }

    [Fact]
    public void AFailingLookupGivesNoSignalsInsteadOfAnError()
    {
        using var monitor = new CameraActivityMonitor(_engine, new FakeIdentities(), _ => throw new InvalidOperationException("boom"), () => new HashSet<int>());
        monitor.OnReport([new(Webcam, "BRIO", 1200, true)]);

        Assert.Empty(_engine.Invoke(monitor.GetActiveSignals, Timeout));
        Assert.Empty(_errors);
    }

    [Fact]
    public void AfterDisposeReportsAreIgnored()
    {
        var monitor = new CameraActivityMonitor(_engine, new FakeIdentities(), Resolve, () => new HashSet<int>());
        int changed = 0;
        monitor.Changed += () => Interlocked.Increment(ref changed);
        monitor.OnReport([new(Webcam, "BRIO", 1200, true)]);
        Drain();

        monitor.Dispose(); // Never started: nothing to stop, and no Media Foundation call is made.
        monitor.OnReport([new(Webcam, "BRIO", 1300, true)]);
        Drain();

        Assert.Equal(1, changed);
        Assert.False(monitor.IsAvailable);
        Assert.Empty(_engine.Invoke(monitor.GetActiveSignals, Timeout));
    }

    [Fact]
    public void TheMonitorIsStoppedThenShutDown()
    {
        var calls = new List<string>();

        CameraActivityMonitor.StopAndShutDown(new FakeShutdownMonitor(calls));

        Assert.Equal(["Stop", "Shutdown"], calls);
    }

    [Fact]
    public void AMonitorWithoutShutdownIsOnlyStopped()
    {
        var calls = new List<string>();

        CameraActivityMonitor.StopAndShutDown(new FakeMonitor(calls));

        Assert.Equal(["Stop"], calls);
    }

    [Fact]
    public void AFailedStopStillShutsTheMonitorDown()
    {
        var calls = new List<string>();

        CameraActivityMonitor.StopAndShutDown(new FakeShutdownMonitor(calls, result: unchecked((int)0xC00D3E85))); // MF_E_SHUTDOWN

        Assert.Equal(["Stop", "Shutdown"], calls);
    }

    private static CameraDeviceInfo Resolve(string link) =>
        link.Equals(Webcam, StringComparison.OrdinalIgnoreCase) || link.Equals(WebcamOtherInterface, StringComparison.OrdinalIgnoreCase)
            ? new CameraDeviceInfo(WebcamInstance, "Logitech BRIO 4K")
            : link.Equals(IrCamera, StringComparison.OrdinalIgnoreCase)
                ? new CameraDeviceInfo(IrInstance, "Integrated IR Camera")
                : default;

    private static AppIdentity App(int processId) => new("app" + processId, "App " + processId, null, null);

    /// <summary>Waits until everything queued on the engine thread so far has run.</summary>
    private void Drain() => _ = _engine.Invoke(() => true, Timeout);

    /// <summary>A sensor activity monitor without IMFShutdown that records what is called on it.</summary>
    private sealed class FakeMonitor(List<string> calls) : IMFSensorActivityMonitor
    {
        public int Start()
        {
            calls.Add("Start");
            return 0;
        }

        public int Stop()
        {
            calls.Add("Stop");
            return 0;
        }
    }

    /// <summary>A sensor activity monitor with IMFShutdown (like Windows' own) that records what is called on it.</summary>
    private sealed class FakeShutdownMonitor(List<string> calls, int result = 0) : IMFSensorActivityMonitor, IMFShutdown
    {
        public int Start()
        {
            calls.Add("Start");
            return result;
        }

        public int Stop()
        {
            calls.Add("Stop");
            return result;
        }

        public int Shutdown()
        {
            calls.Add("Shutdown");
            return result;
        }

        public int GetShutdownStatus(nint status)
        {
            calls.Add("GetShutdownStatus");
            return result;
        }
    }

    private sealed class FakeIdentities : IProcessIdentityResolver
    {
        public AppIdentity Resolve(int processId) => App(processId);

        public bool IsExeRunning(string exePath) => false;

        public bool IsPackageRunning(string packageFamilyName) => false;
    }
}
