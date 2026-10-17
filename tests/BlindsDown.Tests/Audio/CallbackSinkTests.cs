using System.Runtime.InteropServices;
using BlindsDown.Audio;
using BlindsDown.Core.Devices;
using BlindsDown.Infrastructure;
using BlindsDown.Interop.CoreAudio;

namespace BlindsDown.Tests.Audio;

/// <summary>
/// Calls BlindsDown's own notification objects the way Windows does: through the native vtable of the COM
/// wrapper the runtime builds for them, from a thread that is not the engine thread, with buffers that are
/// overwritten as soon as the call returns. No devices are touched, so these always run.
/// </summary>
public sealed class CallbackSinkTests : IDisposable
{
    private const string EndpointId = "{0.0.1.00000000}.{6b0c3a1e-2f4d-4e8a-9c71-0d5e3f2a1b9c}";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly EngineThread _engine = new("BlindsDown callback test engine", _ => { });
    private readonly ManualResetEventSlim _engineGate = new();

    public void Dispose()
    {
        _engineGate.Set();
        _engine.Dispose();
        _engineGate.Dispose();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public unsafe void MuteSinkCopiesTheNotificationAndHandsItToTheEngineThread(bool muted, bool ours)
    {
        var received = new List<(string Id, VolumeNotification Notification, bool OnEngineThread)>();
        var sink = new MuteNotificationSink(_engine, EndpointId, (id, n) => received.Add((id, n, _engine.IsCurrentThread)));
        Guid context = ours ? CoreAudioMicBackend.EventContext : new Guid("0d4c8f3e-55aa-4b7e-8e1a-3c2b9d7f6e10");

        // AUDIO_VOLUME_NOTIFICATION_DATA with one channel: GUID, BOOL bMuted, float, UINT nChannels, float[1].
        nint data = Marshal.AllocHGlobal(32);
        nint callback = Marshal.GetComInterfaceForObject(sink, typeof(IAudioEndpointVolumeCallback));
        int hr;
        try
        {
            *(Guid*)data = context;
            *(int*)(data + 16) = muted ? 1 : 0;
            *(float*)(data + 20) = 0.5f;
            *(uint*)(data + 24) = 1;
            *(float*)(data + 28) = 0.5f;

            HoldEngine();
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(callback, 3))(callback, data);

            // Windows reuses the buffer after OnNotify returns: the sink must have copied what it needs.
            new Span<byte>((void*)data, 32).Fill(0xA5);
        }
        finally
        {
            ReleaseEngine();
            Marshal.FreeHGlobal(data);
            _ = Marshal.Release(callback);
        }

        Assert.Equal(CoreAudioErrors.SOk, hr);
        var result = Assert.Single(DrainEngine(() => received.ToList()));
        Assert.Equal(EndpointId, result.Id);
        Assert.Equal(muted, result.Notification.Muted);
        Assert.Equal(context, result.Notification.EventContext);
        Assert.True(result.OnEngineThread);
    }

    [Fact]
    public unsafe void EndpointClientForwardsCopiedIdsAndOnlyCaptureDefaultChanges()
    {
        var endpoints = new List<string>();
        int captureDefaultChanges = 0;
        bool allOnEngineThread = true;
        var client = new EndpointNotificationClient(
            _engine,
            id =>
            {
                endpoints.Add(id);
                allOnEngineThread &= _engine.IsCurrentThread;
            },
            () =>
            {
                captureDefaultChanges++;
                allOnEngineThread &= _engine.IsCurrentThread;
            });

        nint id = Marshal.StringToCoTaskMemUni(EndpointId);
        nint callback = Marshal.GetComInterfaceForObject(client, typeof(IMMNotificationClient));
        var results = new List<int>();
        try
        {
            HoldEngine();
            nint* vtable = *(nint**)callback;
            var onStateChanged = (delegate* unmanaged[Stdcall]<nint, nint, uint, int>)vtable[3];
            var onAdded = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[4];
            var onRemoved = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[5];
            var onDefaultChanged = (delegate* unmanaged[Stdcall]<nint, int, int, nint, int>)vtable[6];
            var onPropertyChanged = (delegate* unmanaged[Stdcall]<nint, nint, PropertyKey, int>)vtable[7];

            results.Add(onStateChanged(callback, id, (uint)EndpointState.Unplugged));
            results.Add(onAdded(callback, id));
            results.Add(onRemoved(callback, id));
            results.Add(onStateChanged(callback, 0, (uint)EndpointState.Active));
            results.Add(onDefaultChanged(callback, (int)DataFlow.Render, (int)DeviceRole.Console, id));
            results.Add(onDefaultChanged(callback, (int)DataFlow.Capture, (int)DeviceRole.Communications, id));
            results.Add(onDefaultChanged(callback, (int)DataFlow.Capture, (int)DeviceRole.Console, 0));

            // PROPERTYKEY is passed by value (20 bytes): this checks the call frame, and that it is ignored.
            results.Add(onPropertyChanged(callback, id, EndpointPropertyKeys.FriendlyName));

            // The ID string belongs to Windows and is gone after the call: forwarded IDs must be copies.
            new Span<char>((void*)id, EndpointId.Length).Fill('X');
        }
        finally
        {
            ReleaseEngine();
            Marshal.FreeCoTaskMem(id);
            _ = Marshal.Release(callback);
        }

        Assert.All(results, hr => Assert.Equal(CoreAudioErrors.SOk, hr));
        (List<string> forwarded, int defaults, bool onEngine) = DrainEngine(() => (endpoints.ToList(), captureDefaultChanges, allOnEngineThread));
        Assert.Equal([EndpointId, EndpointId, EndpointId], forwarded);
        Assert.Equal(2, defaults);
        Assert.True(onEngine);
    }

    [Fact]
    public unsafe void EndpointClientWithoutDefaultHandlerIgnoresDefaultChanges()
    {
        int endpointChanges = 0;
        var client = new EndpointNotificationClient(_engine, _ => endpointChanges++, onCaptureDefaultChanged: null);
        nint callback = Marshal.GetComInterfaceForObject(client, typeof(IMMNotificationClient));
        int hr;
        try
        {
            hr = ((delegate* unmanaged[Stdcall]<nint, int, int, nint, int>)Slot(callback, 6))(callback, (int)DataFlow.Capture, (int)DeviceRole.Console, 0);
        }
        finally
        {
            _ = Marshal.Release(callback);
        }

        Assert.Equal(CoreAudioErrors.SOk, hr);
        Assert.Equal(0, DrainEngine(() => endpointChanges));
    }

    [Fact]
    public unsafe void SessionSinkCoalescesBurstsAndIgnoresVolumeAndNameEvents()
    {
        int refreshes = 0;
        bool allOnEngineThread = true;
        var sink = new SessionChangeSink(_engine, () =>
        {
            refreshes++;
            allOnEngineThread &= _engine.IsCurrentThread;
        });

        nint events = Marshal.GetComInterfaceForObject(sink, typeof(IAudioSessionEvents));
        nint created = Marshal.GetComInterfaceForObject(sink, typeof(IAudioSessionNotification));
        try
        {
            nint* vtable = *(nint**)events;
            var onDisplayNameChanged = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)vtable[3];
            var onSimpleVolumeChanged = (delegate* unmanaged[Stdcall]<nint, float, int, nint, int>)vtable[5];
            var onStateChanged = (delegate* unmanaged[Stdcall]<nint, int, int>)vtable[8];
            var onDisconnected = (delegate* unmanaged[Stdcall]<nint, int, int>)vtable[9];
            var onSessionCreated = (delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(created, 3);

            // A call starting fires a burst; while the engine is busy they collapse into one refresh.
            HoldEngine();
            Assert.Equal(CoreAudioErrors.SOk, onSessionCreated(created, 0));
            Assert.Equal(CoreAudioErrors.SOk, onStateChanged(events, (int)AudioSessionState.Active));
            Assert.Equal(CoreAudioErrors.SOk, onStateChanged(events, (int)AudioSessionState.Inactive));
            Assert.Equal(CoreAudioErrors.SOk, onDisconnected(events, (int)AudioSessionDisconnectReason.DeviceRemoval));
            ReleaseEngine();
            Assert.Equal(1, DrainEngine(() => refreshes));

            // Volume and name changes say nothing about who is capturing.
            Assert.Equal(CoreAudioErrors.SOk, onSimpleVolumeChanged(events, 0.25f, 1, 0));
            Assert.Equal(CoreAudioErrors.SOk, onDisplayNameChanged(events, 0, 0));
            Assert.Equal(1, DrainEngine(() => refreshes));

            // After a refresh ran, the next event schedules a new one.
            Assert.Equal(CoreAudioErrors.SOk, onStateChanged(events, (int)AudioSessionState.Active));
            Assert.Equal(2, DrainEngine(() => refreshes));
        }
        finally
        {
            ReleaseEngine();
            _ = Marshal.Release(events);
            _ = Marshal.Release(created);
        }

        Assert.True(DrainEngine(() => allOnEngineThread));
    }

    private static unsafe nint Slot(nint comInterface, int slot) => (*(nint**)comInterface)[slot];

    /// <summary>Blocks the engine thread, so posted work only runs after <see cref="ReleaseEngine"/>.</summary>
    private void HoldEngine()
    {
        _engineGate.Reset();
        _engine.Post(() => _engineGate.Wait(Timeout));
    }

    private void ReleaseEngine() => _engineGate.Set();

    /// <summary>Runs <paramref name="read"/> on the engine thread after everything posted before it.</summary>
    private T DrainEngine<T>(Func<T> read) => _engine.Invoke(read, Timeout);
}
