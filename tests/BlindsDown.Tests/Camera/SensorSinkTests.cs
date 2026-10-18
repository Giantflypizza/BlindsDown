using System.Runtime.InteropServices;
using BlindsDown.Interop.CoreAudio;
using BlindsDown.Interop.Media;

namespace BlindsDown.Tests.Camera;

/// <summary>
/// Calls BlindsDown's sensor activity callback the way Media Foundation does: through the native vtable of the COM
/// wrapper the runtime builds for it, from a test thread, with a report that is itself a COM object (a managed fake
/// behind its own COM wrapper). Checks that everything is copied, references are balanced, odd strings are handled,
/// and nothing ever escapes into native code. No camera or Media Foundation is involved, so these always run.
/// </summary>
public sealed class SensorSinkTests
{
    private const int EFail = unchecked((int)0x80004005);
    private const int InsufficientBuffer = unchecked((int)0x8007007A);
    private const string Webcam = @"\\?\USB#VID_046D&PID_085C&MI_00#7&1a2b3c4d&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL";
    private const string IrCamera = @"\\?\USB#VID_04F2&PID_B6BE&MI_02#6&2c3d4e5f&0&0002#{24e552d7-6523-47f7-a647-d3465bf1f5ca}\GLOBAL";

    [Fact]
    public void EveryCameraAndProcessIsCopied()
    {
        var report = new FakeActivitiesReport(
            new FakeCamera(Webcam, "Logitech BRIO", new FakeProcess(1200, true), new FakeProcess(1300, false)),
            new FakeCamera(IrCamera, null, new FakeProcess(900, true)));

        IReadOnlyList<CameraActivityEntry>? entries = Deliver(report, out int hr);

        Assert.Equal(0, hr);
        Assert.Equal(
            [
                new CameraActivityEntry(Webcam, "Logitech BRIO", 1200, true),
                new CameraActivityEntry(Webcam, "Logitech BRIO", 1300, false),
                new CameraActivityEntry(IrCamera, null, 900, true),
            ],
            entries!);
    }

    [Fact]
    public void AnEmptyReportMeansNoCameraIsInUse()
    {
        IReadOnlyList<CameraActivityEntry>? entries = Deliver(new FakeActivitiesReport(), out int hr);

        Assert.Equal(0, hr);
        Assert.NotNull(entries);
        Assert.Empty(entries);
    }

    [Fact]
    public void AReportWhoseCountFailsIsUnreadable()
    {
        IReadOnlyList<CameraActivityEntry>? entries = Deliver(new FakeActivitiesReport { CountResult = EFail }, out int hr);

        Assert.Equal(0, hr);
        Assert.Null(entries);
    }

    [Fact]
    public void UnreadableCamerasAndProcessesAreSkipped()
    {
        var report = new FakeActivitiesReport(
            new FakeCamera(Webcam, "BRIO", new FakeProcess(1200, true) { Unreadable = true }, new FakeProcess(1300, true)),
            new FakeCamera(IrCamera, "IR") { Unreadable = true },
            new FakeCamera(null, "No link", new FakeProcess(1400, true)),
            new FakeCamera(IrCamera, "IR", new FakeProcess(1500, true)) { ProcessCountResult = EFail });

        IReadOnlyList<CameraActivityEntry>? entries = Deliver(report, out _);

        Assert.Equal([new CameraActivityEntry(Webcam, "BRIO", 1300, true)], entries!);
    }

    [Fact]
    public void ALinkLongerThanTheFirstBufferIsReadOnTheSecondTry()
    {
        string longLink = @"\\?\SWD#VCAMDEVAPI#" + new string('a', SensorActivityReader.TextCapacity + 200) + @"#{e5323777-f976-4f5b-9b55-b94699c46e44}";

        IReadOnlyList<CameraActivityEntry>? entries = Deliver(new FakeActivitiesReport(new FakeCamera(longLink, "Virtual", new FakeProcess(42, true))), out _);

        Assert.Equal(longLink, Assert.Single(entries!).SymbolicLink);
    }

    [Fact]
    public void TextWithoutATerminatorIsNotTrusted()
    {
        var camera = new FakeCamera(Webcam, "BRIO", new FakeProcess(1200, true)) { OmitNameTerminator = true };

        IReadOnlyList<CameraActivityEntry>? entries = Deliver(new FakeActivitiesReport(camera), out _);

        CameraActivityEntry entry = Assert.Single(entries!);
        Assert.Equal(Webcam, entry.SymbolicLink);
        Assert.Null(entry.FriendlyName);
    }

    [Fact]
    public void AnInvalidProcessIdBecomesNegative()
    {
        IReadOnlyList<CameraActivityEntry>? entries = Deliver(new FakeActivitiesReport(new FakeCamera(Webcam, "BRIO", new FakeProcess(0xFFFF_FFF0, true))), out _);

        Assert.True(Assert.Single(entries!).ProcessId < 0);
    }

    [Fact]
    public void AFailingHandlerNeverReachesMediaFoundation()
    {
        Exception? failure = null;
        var sink = new SensorActivitiesSink(_ => throw new InvalidOperationException("handler broke"), ex => failure = ex);

        int hr = Invoke(sink, new FakeActivitiesReport(new FakeCamera(Webcam, "BRIO", new FakeProcess(1200, true))));

        Assert.Equal(0, hr);
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public void EvenAFailingFailureHandlerIsContained()
    {
        var sink = new SensorActivitiesSink(_ => throw new InvalidOperationException("handler broke"), _ => throw new InvalidOperationException("logger broke"));

        Assert.Equal(0, Invoke(sink, new FakeActivitiesReport()));
    }

    [Fact]
    public void ANullReportIsUnreadable()
    {
        IReadOnlyList<CameraActivityEntry>? received = [];
        var sink = new SensorActivitiesSink(entries => received = entries, _ => { });

        Assert.Equal(0, InvokeRaw(sink, 0));
        Assert.Null(received);
    }

    [Fact]
    public void TheBorrowedReportIsHandedBackWithItsReferencesBalanced()
    {
        var sink = new SensorActivitiesSink(_ => { }, _ => { });
        var report = new FakeActivitiesReport(new FakeCamera(Webcam, "BRIO", new FakeProcess(1200, true)));
        nint pointer = Marshal.GetComInterfaceForObject(report, typeof(IMFSensorActivitiesReport));
        _ = Marshal.AddRef(pointer);
        int before = Marshal.Release(pointer);

        Assert.Equal(0, InvokeRaw(sink, pointer));

        _ = Marshal.AddRef(pointer);
        int after = Marshal.Release(pointer);
        _ = Marshal.Release(pointer);
        Assert.Equal(before, after);
    }

    [Fact]
    public void AnObjectOfAnotherKindIsRefusedWithItsReferencesBalanced()
    {
        // A camera report handed over where a whole report is expected: refused, not thrown, and no reference kept.
        var camera = new FakeCamera(Webcam, "BRIO");
        nint pointer = Marshal.GetComInterfaceForObject(camera, typeof(IMFSensorActivityReport));
        _ = Marshal.AddRef(pointer);
        int before = Marshal.Release(pointer);

        _ = Marshal.AddRef(pointer); // The reference TryTake takes over.
        ComRef<IMFSensorActivitiesReport>? taken = ComRef.TryTake<IMFSensorActivitiesReport>(pointer);

        _ = Marshal.AddRef(pointer);
        int after = Marshal.Release(pointer);
        _ = Marshal.Release(pointer);
        Assert.Null(taken);
        Assert.Equal(before, after);
    }

    private static IReadOnlyList<CameraActivityEntry>? Deliver(FakeActivitiesReport report, out int hr)
    {
        IReadOnlyList<CameraActivityEntry>? received = null;
        Exception? failure = null;
        var sink = new SensorActivitiesSink(entries => received = entries, ex => failure = ex);

        hr = Invoke(sink, report);

        Assert.Null(failure);
        return received;
    }

    private static int Invoke(SensorActivitiesSink sink, FakeActivitiesReport report)
    {
        nint pointer = Marshal.GetComInterfaceForObject(report, typeof(IMFSensorActivitiesReport));
        try
        {
            return InvokeRaw(sink, pointer);
        }
        finally
        {
            _ = Marshal.Release(pointer);
        }
    }

    /// <summary>OnActivitiesReport is slot 3 of the callback's vtable (after IUnknown).</summary>
    private static unsafe int InvokeRaw(SensorActivitiesSink sink, nint report)
    {
        nint callback = Marshal.GetComInterfaceForObject(sink, typeof(IMFSensorActivitiesReportCallback));
        try
        {
            var onActivitiesReport = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)callback)[3];
            return onActivitiesReport(callback, report);
        }
        finally
        {
            _ = Marshal.Release(callback);
        }
    }

    private sealed class FakeActivitiesReport(params FakeCamera[] cameras) : IMFSensorActivitiesReport
    {
        public int CountResult { get; init; }

        public int GetCount(out uint count)
        {
            count = (uint)cameras.Length;
            return CountResult;
        }

        public int GetActivityReport(uint index, out nint report)
        {
            report = 0;
            if (index >= cameras.Length || cameras[index].Unreadable)
            {
                return EFail;
            }

            report = Marshal.GetComInterfaceForObject(cameras[index], typeof(IMFSensorActivityReport));
            return 0;
        }

        public int GetActivityReportByDeviceName(string symbolicName, out nint report)
        {
            report = 0;
            return EFail;
        }
    }

    private sealed class FakeCamera(string? link, string? name, params FakeProcess[] processes) : IMFSensorActivityReport
    {
        public bool Unreadable { get; init; }

        public bool OmitNameTerminator { get; init; }

        public int ProcessCountResult { get; init; }

        public int GetFriendlyName(nint buffer, uint capacity, out uint written) => Copy(name, buffer, capacity, OmitNameTerminator, out written);

        public int GetSymbolicLink(nint buffer, uint capacity, out uint written) => Copy(link, buffer, capacity, omitTerminator: false, out written);

        public int GetProcessCount(out uint count)
        {
            count = (uint)processes.Length;
            return ProcessCountResult;
        }

        public int GetProcessActivity(uint index, out nint activity)
        {
            activity = 0;
            if (index >= processes.Length)
            {
                return EFail;
            }

            activity = Marshal.GetComInterfaceForObject(processes[index], typeof(IMFSensorProcessActivity));
            return 0;
        }

        /// <summary>Like Windows: fails with the size needed when the buffer is too small. With
        /// <paramref name="omitTerminator"/> it misbehaves instead: fills the whole buffer without a terminator.</summary>
        private static unsafe int Copy(string? text, nint buffer, uint capacity, bool omitTerminator, out uint written)
        {
            written = 0;
            if (text is null)
            {
                return EFail;
            }

            if (omitTerminator)
            {
                // Fill the whole buffer with no terminator at all.
                new Span<char>((void*)buffer, (int)capacity).Fill('x');
                written = capacity;
                return 0;
            }

            if (text.Length + 1 > capacity)
            {
                written = (uint)text.Length + 1;
                return InsufficientBuffer;
            }

            text.AsSpan().CopyTo(new Span<char>((void*)buffer, (int)capacity));
            ((char*)buffer)[text.Length] = '\0';
            written = (uint)text.Length + 1;
            return 0;
        }
    }

    private sealed class FakeProcess(uint processId, bool streaming) : IMFSensorProcessActivity
    {
        public bool Unreadable { get; init; }

        public int GetProcessId(out uint id)
        {
            id = processId;
            return Unreadable ? EFail : 0;
        }

        public int GetStreamingState(out int isStreaming)
        {
            isStreaming = streaming ? 1 : 0;
            return 0;
        }

        public int GetStreamingMode(out SensorDeviceMode mode)
        {
            mode = SensorDeviceMode.Controller;
            return 0;
        }

        public int GetReportTime(out long fileTime)
        {
            fileTime = 0;
            return 0;
        }
    }
}
