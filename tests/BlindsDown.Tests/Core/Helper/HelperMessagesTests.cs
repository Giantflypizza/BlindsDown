using System.Text;
using BlindsDown.Core.Helper;
using BlindsDown.Core.Locking;
using BlindsDown.Core.Pnp;
using BlindsDown.Core.Settings;

namespace BlindsDown.Tests.Core.Helper;

public sealed class HelperMessagesTests
{
    [Fact]
    public void ARequestRoundTripsEveryField()
    {
        HelperRequest request = SampleLockRequest();

        HelperRequest parsed = Parse(HelperMessages.SerializeRequest(request));

        Assert.Equal(HelperProtocol.Version, parsed.V);
        Assert.Equal(HelperCommand.Lock, parsed.Command);
        Assert.Equal(17, parsed.SinceEvent);
        Assert.Equal("0.2.0", parsed.ClientVersion);
        Assert.Equal("hotkey", parsed.Source);
        DeviceLockRequest options = parsed.Lock!;
        Assert.True(options.Cameras);
        Assert.True(options.MicHardware);
        Assert.True(options.IncludeInfrared);
        Assert.False(options.IncludeVirtual);
        Assert.Equal(request.Lock!.Exclusions, options.Exclusions);
        Assert.Equal(request.Lock.KeepEnabledInstanceIds, options.KeepEnabledInstanceIds);
    }

    [Fact]
    public void ARequestIsCompactCamelCaseJsonWithEnumNames()
    {
        string json = Encoding.UTF8.GetString(HelperMessages.SerializeRequest(new HelperRequest { Command = HelperCommand.Status, SinceEvent = 3 }));

        Assert.Equal("""{"v":1,"command":"Status","sinceEvent":3}""", json);
    }

    [Fact]
    public void AResponseRoundTripsEveryField()
    {
        HelperResponse response = SampleResponse();

        HelperResponse parsed = ParseResponse(HelperMessages.SerializeResponse(response));

        Assert.True(parsed.Ok);
        Assert.Equal("0.2.0", parsed.HelperVersion);
        Assert.Equal(42, parsed.LastEventId);
        Assert.Equal(40, parsed.RunId);
        Assert.Equal(LockState.PartiallyLocked, parsed.Status!.State);
        Assert.Equal(response.Status!.Devices, parsed.Status.Devices);
        Assert.Equal(response.Status.Problems, parsed.Status.Problems);
        Assert.Equal(response.Events, parsed.Events);
        Assert.Equal(response.Report!.Lines, parsed.Report!.Lines);
        Assert.Equal(2, parsed.Report.DeviceCount);
        Assert.True(parsed.Report.CoversCameras);
        Assert.Equal(response.Report.Summary, parsed.Report.Summary);
    }

    [Fact]
    public void ComputedPropertiesAreNotSent()
    {
        string json = Encoding.UTF8.GetString(HelperMessages.SerializeResponse(SampleResponse()));

        Assert.DoesNotContain("summary", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allExact", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("problemCount", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"protection\":\"DeviceDisabled\"", json, StringComparison.Ordinal);
        Assert.Contains("\"camera\":\"ColorAndInfrared\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"v":1,"command":"Status","sinceEvent":3,"sinceRun":1760780000000}""", 1760780000000L)]
    [InlineData("""{"v":1,"command":"Status","sinceEvent":3,"sinceRun":null}""", null)]
    [InlineData("""{"v":1,"command":"Status","sinceEvent":3}""", null)]
    [InlineData("""{"v":1,"command":"Status","sinceEvent":3,"sinceRun":0}""", null)]
    [InlineData("""{"v":1,"command":"Status","sinceEvent":3,"sinceRun":-5}""", null)]
    public void ARunIdThatIsNotPositiveIsTreatedAsNone(string json, long? expected)
    {
        HelperRequest request = Parse(json);

        Assert.Equal(expected, request.SinceRun);
        Assert.Equal(3, request.SinceEvent);
    }

    [Fact]
    public void ARunIdIsSentOnlyWhenTheClientKnowsIt()
    {
        HelperRequest request = new() { Command = HelperCommand.Status, SinceEvent = 3, SinceRun = 9 };

        Assert.Equal("""{"v":1,"command":"Status","sinceEvent":3,"sinceRun":9}""", Encoding.UTF8.GetString(HelperMessages.SerializeRequest(request)));
        Assert.Equal(9, Parse(HelperMessages.SerializeRequest(request)).SinceRun);
        Assert.DoesNotContain("sinceRun", Encoding.UTF8.GetString(HelperMessages.SerializeRequest(request with { SinceRun = null })), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"v":1,"ok":true,"lastEventId":5,"runId":4}""", 4L)]
    [InlineData("""{"v":1,"ok":true,"lastEventId":5}""", 0L)] // An older helper.
    [InlineData("""{"v":1,"ok":true,"lastEventId":5,"runId":-4}""", 0L)]
    public void AResponseSaysWhichRunOfTheHelperMadeItOrZero(string json, long expected)
    {
        Assert.Equal(expected, ParseResponse(json).RunId);
    }

    [Fact]
    public void AMinimalRequestIsAccepted()
    {
        HelperRequest request = Parse("""{"v":1,"command":"Hello"}""");

        Assert.Equal(HelperCommand.Hello, request.Command);
        Assert.Null(request.Lock);
    }

    [Fact]
    public void LockOptionsWithoutListsGetEmptyLists()
    {
        HelperRequest request = Parse("""{"v":1,"command":"Lock","lock":{"cameras":true}}""");

        Assert.True(request.Lock!.Cameras);
        Assert.Empty(request.Lock.Exclusions);
        Assert.Empty(request.Lock.KeepEnabledInstanceIds);
    }

    [Fact]
    public void AResponseWithoutEventsOrVersionGetsEmptyOnes()
    {
        HelperResponse response = ParseResponse("""{"v":1,"ok":false,"error":"x"}""");

        Assert.Empty(response.Events);
        Assert.Equal(string.Empty, response.HelperVersion);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("null", "empty")]
    [InlineData("{", "not valid")]
    [InlineData("[]", "not valid")]
    [InlineData("42", "not valid")]
    [InlineData("{\"v\":1,\"command\":\"Hello\"} trailing", "not valid")]
    [InlineData("{\"v\":1,\"command\":\"Hello\",}", "not valid")]
    [InlineData("{/* comment */\"v\":1,\"command\":\"Hello\"}", "not valid")]
    [InlineData("{\"command\":\"Hello\"}", "version")]
    [InlineData("{\"v\":2,\"command\":\"Hello\"}", "version")]
    [InlineData("{\"v\":0,\"command\":\"Hello\"}", "version")]
    [InlineData("{\"v\":2,\"command\":\"Hello\",\"newField\":true}", "version")]
    [InlineData("{\"v\":\"1\",\"command\":\"Hello\"}", "not valid")]
    [InlineData("{\"v\":1,\"command\":\"Format\"}", "not valid")]
    [InlineData("{\"v\":1,\"command\":99}", "Unknown command")]
    [InlineData("{\"v\":1,\"command\":-1}", "Unknown command")]
    [InlineData("{\"v\":1,\"command\":\"Lock\"}", "needs lock options")]
    [InlineData("{\"v\":1,\"command\":\"Lock\",\"lock\":null}", "needs lock options")]
    [InlineData("{\"v\":1,\"command\":\"Hello\",\"deviceId\":\"USB\\\\x\"}", "not valid")]
    [InlineData("{\"v\":1,\"command\":\"Lock\",\"lock\":{\"cameras\":true,\"instanceIds\":[\"x\"]}}", "not valid")]
    [InlineData("{\"v\":1,\"command\":\"Hello\",\"command\":\"Unlock\"}", "not valid")]
    [InlineData("{\"v\":1,\"command\":\"Lock\",\"lock\":{\"exclusions\":null}}", "not valid")]
    [InlineData("{\"v\":1,\"command\":\"Lock\",\"lock\":{\"keepEnabledInstanceIds\":null}}", "not valid")]
    [InlineData("{\"v\":1,\"command\":\"Lock\",\"lock\":{\"exclusions\":[null]}}", "exclusion is empty")]
    [InlineData("{\"v\":1,\"command\":\"Lock\",\"lock\":{\"keepEnabledInstanceIds\":[null]}}", "keep on is empty")]
    [InlineData("{\"v\":1,\"command\":\"Lock\",\"lock\":{\"exclusions\":[{\"displayName\":\"x\"}]}}", "not valid")]
    [InlineData("{\"v\":1,\"command\":\"Lock\",\"lock\":{\"exclusions\":[{\"stableKey\":null,\"displayName\":\"x\"}]}}", "not valid")]
    [InlineData("{\"v\":1,\"command\":\"Lock\",\"lock\":{\"cameras\":\"yes\"}}", "not valid")]
    [InlineData("{\"v\":1,\"command\":\"Hello\",\"source\":\"tray\\n2026-10-17 helper: unlocked\"}", "control characters")]
    [InlineData("{\"v\":1,\"command\":\"Hello\",\"clientVersion\":\"\\u0000\"}", "control characters")]
    [InlineData("{\"v\":1,\"command\":\"Hello\",\"sinceEvent\":1e999}", "not valid")]
    public void MalformedRequestsAreRefusedWithAReason(string json, string reason)
    {
        HelperRequest? request = HelperMessages.ParseRequest(Encoding.UTF8.GetBytes(json), out string? error);

        Assert.Null(request);
        Assert.NotNull(error);
        Assert.Contains(reason, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVersionMismatchSaysVersion()
    {
        HelperMessages.ParseRequest("""{"v":7,"command":"Hello"}"""u8, out string? error);

        Assert.Equal("Protocol version 7 is not supported (this BlindsDown speaks version 1).", error);
    }

    [Fact]
    public void ARequestLargerThanTheLimitIsRefusedBeforeParsing()
    {
        string padding = new(' ', HelperProtocol.MaxMessageBytes);
        byte[] bytes = Encoding.UTF8.GetBytes("{\"v\":1,\"command\":\"Hello\"}" + padding);

        Assert.Null(HelperMessages.ParseRequest(bytes, out string? error));
        Assert.Contains("larger than 65536 bytes", error, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidUtf8IsRefused()
    {
        byte[] bytes = [.. "{\"v\":1,\"command\":\"Hello\",\"source\":\""u8, 0xFF, 0xFE, .. "\"}"u8];

        Assert.Null(HelperMessages.ParseRequest(bytes, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void DeeplyNestedJsonIsRefused()
    {
        string json = "{\"v\":1,\"command\":\"Hello\",\"x\":" + new string('[', 100) + new string(']', 100) + "}";

        Assert.Null(HelperMessages.ParseRequest(Encoding.UTF8.GetBytes(json), out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TooManyExclusionsOrKeptDevicesAreRefused()
    {
        var exclusion = new DeviceExclusion { StableKey = "cam|x|y", DisplayName = "y" };
        HelperRequest tooManyExclusions = SampleLockRequest() with
        {
            Lock = new DeviceLockRequest { Exclusions = Enumerable.Repeat(exclusion, HelperProtocol.MaxExclusions + 1).ToList() },
        };
        HelperRequest tooManyKept = SampleLockRequest() with
        {
            Lock = new DeviceLockRequest { KeepEnabledInstanceIds = Enumerable.Repeat("id", HelperProtocol.MaxExclusions + 1).ToList() },
        };
        HelperRequest justEnough = SampleLockRequest() with
        {
            Lock = new DeviceLockRequest
            {
                Exclusions = Enumerable.Repeat(exclusion, HelperProtocol.MaxExclusions).ToList(),
                KeepEnabledInstanceIds = Enumerable.Repeat("id", HelperProtocol.MaxExclusions).ToList(),
            },
        };

        Assert.Null(HelperMessages.ParseRequest(HelperMessages.SerializeRequest(tooManyExclusions), out string? error1));
        Assert.Contains("Too many exclusions", error1, StringComparison.Ordinal);
        Assert.Null(HelperMessages.ParseRequest(HelperMessages.SerializeRequest(tooManyKept), out string? error2));
        Assert.Contains("Too many devices to keep on", error2, StringComparison.Ordinal);
        Assert.NotNull(HelperMessages.ParseRequest(HelperMessages.SerializeRequest(justEnough), out _));
    }

    [Fact]
    public void EveryStringInARequestIsLimited()
    {
        string tooLong = new('x', HelperProtocol.MaxStringLength + 1);
        string longest = new('x', HelperProtocol.MaxStringLength);
        var cases = new List<HelperRequest>
        {
            SampleLockRequest() with { ClientVersion = tooLong },
            SampleLockRequest() with { Source = tooLong },
            SampleLockRequest() with { Lock = new DeviceLockRequest { Exclusions = [new DeviceExclusion { StableKey = tooLong, DisplayName = "n" }] } },
            SampleLockRequest() with { Lock = new DeviceLockRequest { Exclusions = [new DeviceExclusion { StableKey = "k", LastKnownId = tooLong, DisplayName = "n" }] } },
            SampleLockRequest() with { Lock = new DeviceLockRequest { Exclusions = [new DeviceExclusion { StableKey = "k", DisplayName = tooLong }] } },
            SampleLockRequest() with { Lock = new DeviceLockRequest { KeepEnabledInstanceIds = [tooLong] } },
        };

        foreach (HelperRequest request in cases)
        {
            Assert.Null(HelperMessages.ParseRequest(HelperMessages.SerializeRequest(request), out string? error));
            Assert.Contains($"longer than {HelperProtocol.MaxStringLength} characters", error, StringComparison.Ordinal);
        }

        HelperRequest atTheLimit = SampleLockRequest() with { Source = longest, ClientVersion = longest };
        Assert.NotNull(HelperMessages.ParseRequest(HelperMessages.SerializeRequest(atTheLimit), out _));
    }

    [Fact]
    public void ParsingNeverThrowsWhateverTheBytes()
    {
        var random = new Random(1234);
        byte[] valid = HelperMessages.SerializeRequest(SampleLockRequest());
        byte[] response = HelperMessages.SerializeResponse(SampleResponse());
        for (int i = 0; i < 3000; i++)
        {
            byte[] source = i % 2 == 0 ? valid : response;
            byte[] bytes = Mutate(source, random);

            HelperRequest? request = HelperMessages.ParseRequest(bytes, out string? requestError);
            HelperResponse? parsed = HelperMessages.ParseResponse(bytes, out string? responseError);

            Assert.True(request is not null || requestError is not null);
            Assert.True(parsed is not null || responseError is not null);
        }
    }

    [Fact]
    public void AResponseFromANewerHelperWithMorePropertiesIsAccepted()
    {
        HelperResponse response = ParseResponse(
            """{"v":1,"ok":true,"helperVersion":"0.3.0","futureThing":{"a":[1,2]},"events":[],"lastEventId":5,"status":{"state":"Locked","devices":[],"problems":[],"extra":1}}""");

        Assert.Equal(LockState.Locked, response.Status!.State);
        Assert.Equal(5, response.LastEventId);
    }

    [Theory]
    [InlineData("{\"v\":2,\"ok\":true}", "version")]
    [InlineData("{\"v\":1,\"ok\":true,\"status\":{\"state\":\"Locked\",\"devices\":[null],\"problems\":[]}}", "device in the status is empty")]
    [InlineData("{\"v\":1,\"ok\":true,\"status\":{\"state\":\"Locked\",\"devices\":[],\"problems\":[null]}}", "problem in the status is empty")]
    [InlineData("{\"v\":1,\"ok\":true,\"status\":{\"state\":9,\"devices\":[],\"problems\":[]}}", "lock state is unknown")]
    [InlineData("{\"v\":1,\"ok\":true,\"status\":{\"state\":\"Locked\",\"devices\":null,\"problems\":[]}}", "not valid")]
    [InlineData("{\"v\":1,\"ok\":true,\"status\":{\"state\":\"Locked\",\"problems\":[]}}", "not valid")]
    [InlineData("{\"v\":1,\"ok\":true,\"events\":[null]}", "event is empty")]
    [InlineData("{\"v\":1,\"ok\":true,\"events\":null}", "not valid")]
    [InlineData("{\"v\":1,\"ok\":true,\"events\":[{\"id\":1,\"kind\":77,\"deviceName\":\"c\",\"message\":\"m\",\"urgent\":true}]}", "unknown kind")]
    [InlineData("{\"v\":1,\"ok\":true,\"events\":[{\"id\":1,\"kind\":\"DeviceBusy\",\"message\":\"m\",\"urgent\":true}]}", "not valid")]
    [InlineData("{\"v\":1,\"ok\":true,\"report\":{\"lines\":[null],\"deviceCount\":1}}", "line is empty")]
    [InlineData("{\"v\":1,\"ok\":true,\"report\":{\"lines\":[],\"deviceCount\":-1}}", "impossible size")]
    [InlineData("{\"v\":1,\"ok\":true,\"report\":{\"lines\":[{\"outcome\":12,\"deviceName\":\"c\",\"message\":\"m\"}],\"deviceCount\":1}}", "unknown outcome")]
    [InlineData("{\"v\":1,\"ok\":true,\"helperVersion\":null}", "not valid")]
    [InlineData("{\"v\":1,\"ok\":true,\"ok\":false}", "not valid")]
    public void MalformedResponsesAreRefusedWithAReason(string json, string reason)
    {
        HelperResponse? response = HelperMessages.ParseResponse(Encoding.UTF8.GetBytes(json), out string? error);

        Assert.Null(response);
        Assert.Contains(reason, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AResponseWithAnUnknownDeviceProtectionIsRefused()
    {
        HelperResponse response = SampleResponse();
        HelperResponse bad = response with
        {
            Status = response.Status! with { Devices = [response.Status.Devices[0] with { Protection = (DeviceProtection)99 }] },
        };

        Assert.Null(HelperMessages.ParseResponse(HelperMessages.SerializeResponse(bad), out string? error));
        Assert.Contains("unknown kind or protection", error, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryStringAndListInAResponseIsLimited()
    {
        string tooLong = new('x', HelperProtocol.MaxStringLength + 1);
        HelperResponse response = SampleResponse();
        DeviceLockInfo device = response.Status!.Devices[0];
        var cases = new List<HelperResponse>
        {
            response with { Error = tooLong },
            response with { HelperVersion = tooLong },
            response with { Status = response.Status with { Devices = [device with { Name = tooLong }] } },
            response with { Status = response.Status with { Devices = [device with { Problem = tooLong }] } },
            response with { Status = response.Status with { Problems = [tooLong] } },
            response with { Status = response.Status with { Devices = Enumerable.Repeat(device, 257).ToList() } },
            response with { Events = [response.Events[0] with { Message = tooLong }] },
            response with { Report = response.Report! with { Lines = [response.Report.Lines[0] with { DeviceName = tooLong }] } },
        };

        foreach (HelperResponse bad in cases)
        {
            Assert.Null(HelperMessages.ParseResponse(HelperMessages.SerializeResponse(bad), out string? error));
            Assert.NotNull(error);
        }
    }

    [Fact]
    public void ForWireTrimsAResponseUntilItParses()
    {
        string tooLong = new('é', HelperProtocol.MaxStringLength * 2);
        HelperResponse response = SampleResponse();
        HelperResponse bad = response with
        {
            Error = tooLong,
            Status = response.Status! with
            {
                Devices = Enumerable.Repeat(response.Status.Devices[0] with { Name = tooLong, Problem = tooLong }, 300).ToList(),
                Problems = [tooLong],
            },
            Events = Enumerable.Range(1, 300).Select(i => new HelperEvent(i, EngineAlertKind.DeviceBusy, tooLong, "m", true)).ToList(),
            Report = response.Report! with { Lines = [response.Report.Lines[0] with { Message = tooLong }] },
        };

        HelperResponse wire = HelperMessages.ForWire(bad);

        Assert.Equal(HelperMessages.MaxListItems, wire.Status!.Devices.Count);
        Assert.Equal(300, wire.Events[^1].Id); // The newest events are kept.
        Assert.Equal(HelperProtocol.MaxStringLength, wire.Error!.Length);
        Assert.EndsWith("...", wire.Error, StringComparison.Ordinal);

        // Small enough to also fit the size limit (non-ASCII text is escaped, six bytes per character).
        HelperResponse fewer = wire with { Events = wire.Events.TakeLast(2).ToList(), Status = wire.Status with { Devices = wire.Status.Devices.Take(2).ToList() } };
        Assert.NotNull(HelperMessages.ParseResponse(HelperMessages.SerializeResponse(fewer), out string? error));
        Assert.Null(error);
    }

    [Fact]
    public void ClipNeverSplitsASurrogatePair()
    {
        string text = new string('a', 5) + "\U0001F600" + new string('b', 10);

        string clipped = HelperMessages.Clip(text, 9);

        Assert.Equal("aaaaa...", clipped);
        Assert.Equal("short", HelperMessages.Clip("short", 9));
        Assert.Equal(string.Empty, HelperMessages.Clip(null));
    }

    private static HelperRequest Parse(byte[] bytes)
    {
        HelperRequest? request = HelperMessages.ParseRequest(bytes, out string? error);
        Assert.True(request is not null, error);
        return request;
    }

    private static HelperRequest Parse(string json) => Parse(Encoding.UTF8.GetBytes(json));

    private static HelperResponse ParseResponse(byte[] bytes)
    {
        HelperResponse? response = HelperMessages.ParseResponse(bytes, out string? error);
        Assert.True(response is not null, error);
        return response;
    }

    private static HelperResponse ParseResponse(string json) => ParseResponse(Encoding.UTF8.GetBytes(json));

    private static byte[] Mutate(byte[] source, Random random)
    {
        var bytes = new List<byte>(source);
        int edits = random.Next(1, 6);
        for (int e = 0; e < edits && bytes.Count > 0; e++)
        {
            int at = random.Next(bytes.Count);
            switch (random.Next(4))
            {
                case 0:
                    bytes[at] = (byte)random.Next(256);
                    break;
                case 1:
                    bytes.RemoveAt(at);
                    break;
                case 2:
                    const string JsonChars = "{}[],:\"0n\\";
                    bytes.Insert(at, (byte)JsonChars[random.Next(JsonChars.Length)]);
                    break;
                default:
                    bytes.RemoveRange(at, bytes.Count - at);
                    break;
            }
        }

        return [.. bytes];
    }

    private static HelperRequest SampleLockRequest() => new()
    {
        Command = HelperCommand.Lock,
        SinceEvent = 17,
        ClientVersion = "0.2.0",
        Source = "hotkey",
        Lock = new DeviceLockRequest
        {
            Cameras = true,
            MicHardware = true,
            IncludeInfrared = true,
            Exclusions = [new DeviceExclusion { StableKey = "cam|USB\\VID_046D&PID_085E&MI_00|LOGITECH BRIO", LastKnownId = "USB\\VID_046D&PID_085E&MI_00\\7&1", DisplayName = "Logitech BRIO" }],
            KeepEnabledInstanceIds = [@"USB\VID_2345&PID_6789&MI_00\8&2B3C"],
        },
    };

    private static HelperResponse SampleResponse() => new()
    {
        Ok = true,
        HelperVersion = "0.2.0",
        LastEventId = 42,
        RunId = 40,
        Status = new DeviceLockStatus(
            LockState.PartiallyLocked,
            [
                new DeviceLockInfo(@"USB\VID_3456&PID_789A&MI_00\6&4E", "HD Webcam C270", "cam|USB\\VID_3456&PID_789A&MI_00|HD WEBCAM C270", DeviceKind.Camera, false, false, DeviceProtection.DeviceDisabled, null, CameraType.ColorAndInfrared),
                new DeviceLockInfo(@"USB\VID_1&PID_2&MI_02\3", "USB Microphone", "hw|USB\\VID_1&PID_2&MI_02|USB MICROPHONE", DeviceKind.MicrophoneHardware, false, false, DeviceProtection.None, "In use by an app; BlindsDown keeps trying."),
            ],
            ["USB Microphone: In use by an app; BlindsDown keeps trying."]),
        Events =
        [
            new HelperEvent(41, EngineAlertKind.DeviceBusy, "USB Microphone", "A microphone is in use by an app, so Windows won't turn it off yet. BlindsDown keeps trying: USB Microphone.", true),
            new HelperEvent(42, EngineAlertKind.NewDeviceLocked, "HD Webcam C270", "A new camera was connected while locked. BlindsDown turned it off: HD Webcam C270.", false),
        ],
        Report = new RestoreReport(
            [
                new RestoreLine(RestoreOutcome.Exact, "HD Webcam C270", "Turned back on, as before the lock.", DeviceKind.Camera),
                new RestoreLine(RestoreOutcome.Failed, "USB Microphone", "Could not be turned back on (Windows refused). Restart the PC to turn it back on.", DeviceKind.MicrophoneHardware),
            ],
            2,
            CoversCameras: true),
    };
}
