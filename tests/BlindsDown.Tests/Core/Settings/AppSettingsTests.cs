using BlindsDown.Core.Devices;
using BlindsDown.Core.Settings;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Settings;

public sealed class AppSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void DefaultsAreSafe()
    {
        var settings = new AppSettings();

        Assert.Equal("Ctrl+Alt+Shift+M", settings.Hotkey);
        Assert.Equal(Hotkey.Default, settings.GetHotkey());
        Assert.True(settings.PlaySounds);
        Assert.True(settings.ShowOverlay);
        Assert.True(settings.HideOverlayFromScreenCapture);
        Assert.True(settings.AlertsEnabled);
        Assert.False(settings.AlertOnStop);
        Assert.True(settings.HistoryEnabled);
        Assert.Equal(30, settings.HistoryRetentionDays);
        Assert.Empty(settings.ExcludedDevices);
        Assert.Empty(settings.AllowedApps);
        Assert.False(settings.FirstRunDone);
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(3650, 3650)]
    [InlineData(99999, 3650)]
    public void NormalizeClampsRetention(int days, int expected)
    {
        var settings = new AppSettings { HistoryRetentionDays = days };

        settings.Normalize();

        Assert.Equal(expected, settings.HistoryRetentionDays);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Banana")]
    [InlineData("M")]
    [InlineData("Ctrl+F12")]
    [InlineData("Ctrl+Shift")]
    public void NormalizeReplacesUnusableHotkeys(string? hotkey)
    {
        var settings = new AppSettings { Hotkey = hotkey! };

        Assert.Equal(Hotkey.Default, settings.GetHotkey());
        settings.Normalize();

        Assert.Equal("Ctrl+Alt+Shift+M", settings.Hotkey);
    }

    [Fact]
    public void NormalizeKeepsAValidCustomHotkey()
    {
        var settings = new AppSettings { Hotkey = "F13" };

        settings.Normalize();

        Assert.Equal("F13", settings.Hotkey);
        Assert.Equal(new Hotkey(HotkeyModifiers.None, 0x7C), settings.GetHotkey());
    }

    [Fact]
    public void NormalizeRepairsMissingAndNullListEntries()
    {
        var settings = new AppSettings { ExcludedDevices = null!, AllowedApps = null! };
        settings.Normalize();
        Assert.Empty(settings.ExcludedDevices);
        Assert.Empty(settings.AllowedApps);

        settings.ExcludedDevices = [null!, new DeviceExclusion { StableKey = "k", DisplayName = "d" }];
        settings.AllowedApps = [null!];
        settings.Normalize();
        Assert.Single(settings.ExcludedDevices);
        Assert.Empty(settings.AllowedApps);
        Assert.False(settings.IsExcluded(Mic("x"), [Mic("x")]));
        Assert.False(settings.IsAppAllowed("x"));
    }

    [Fact]
    public void ExclusionMatchesByStableKeyIgnoringCaseAndPort()
    {
        MicEndpoint port1 = Mic("{0.0.1.00000000}.{1}", @"USB\VID_1038&PID_12E0&MI_00\9&16010608&0&0000");
        MicEndpoint port2 = Mic("{0.0.1.00000000}.{2}", @"usb\vid_1038&pid_12e0&mi_00\A&1234ABCD&0&0000");
        MicEndpoint otherModel = Mic("{0.0.1.00000000}.{3}", @"USB\VID_1038&PID_12E1&MI_00\9&16010608&0&0000");
        var settings = new AppSettings
        {
            ExcludedDevices = [new DeviceExclusion { StableKey = port1.StableKey.ToLowerInvariant(), DisplayName = "Arctis" }],
        };

        // The same mic moved to another USB port: only one of them is connected at a time.
        Assert.True(settings.IsExcluded(port1, [port1, otherModel]));
        Assert.True(settings.IsExcluded(port2, [port2, otherModel]));
        Assert.False(settings.IsExcluded(otherModel, [port1, otherModel]));
    }

    [Fact]
    public void ExcludingOneOfTwoIdenticalMicsLeavesTheOtherLocked()
    {
        MicEndpoint a = Mic("{0.0.1.00000000}.{A}", @"USB\VID_046D&PID_0825&MI_02\6&AAAA&0&0002");
        MicEndpoint b = Mic("{0.0.1.00000000}.{B}", @"USB\VID_046D&PID_0825&MI_02\6&BBBB&0&0002");
        Assert.Equal(a.StableKey, b.StableKey);
        var settings = new AppSettings
        {
            ExcludedDevices = [new DeviceExclusion { StableKey = a.StableKey, LastKnownId = a.Id, DisplayName = "Webcam A" }],
        };

        Assert.True(settings.IsExcluded(a, [a, b]));
        Assert.False(settings.IsExcluded(b, [a, b]));

        // A is unplugged (Windows still lists it as not present) and comes back on another port as C.
        MicEndpoint aGone = a with { State = EndpointState.NotPresent };
        MicEndpoint c = Mic("{0.0.1.00000000}.{C}", @"USB\VID_046D&PID_0825&MI_02\6&CCCC&0&0002");
        Assert.True(settings.IsExcluded(c, [aGone, c]));

        // ...but with its twin B also connected, BlindsDown can't tell which one moved, so it locks both.
        Assert.False(settings.IsExcluded(c, [aGone, b, c]));
        Assert.False(settings.IsExcluded(b, [aGone, b, c]));
    }

    [Fact]
    public void DifferentBluetoothHeadsetsHaveDifferentStableKeys()
    {
        MicEndpoint airPods = Mic("{0.0.1.00000000}.{1}", @"BTHHFENUM\BTHHFPAUDIO\D&258BE8DF&0&97") with { Name = "Headset (AirPods Pro - Find My Hands-Free)" };
        MicEndpoint other = Mic("{0.0.1.00000000}.{2}", @"BTHHFENUM\BTHHFPAUDIO\D&11111111&0&12") with { Name = "Headset (WH-1000XM5 Hands-Free)" };

        Assert.NotEqual(airPods.StableKey, other.StableKey);
    }

    [Fact]
    public void WindowsDuplicateNumberingDoesNotChangeTheStableKey()
    {
        MicEndpoint first = Mic("{0.0.1.00000000}.{1}", @"USB\VID_1038&PID_12E0&MI_00\9&22DA850E&0&0000") with { Name = "Microphone (Arctis Nova Pro Wireless)" };
        MicEndpoint second = Mic("{0.0.1.00000000}.{2}", @"USB\VID_1038&PID_12E0&MI_00\9&16010608&0&0000") with { Name = "Microphone (2- Arctis Nova Pro Wireless)" };

        Assert.Equal(first.StableKey, second.StableKey);
    }

    [Fact]
    public void ExclusionMatchesByLastKnownIdIgnoringCase()
    {
        var settings = new AppSettings
        {
            ExcludedDevices = [new DeviceExclusion { StableKey = "mic|GONE|X", LastKnownId = "{0.0.1.00000000}.{ABC}", DisplayName = "Old" }],
        };

        Assert.True(settings.IsExcluded(Mic("{0.0.1.00000000}.{abc}"), [Mic("{0.0.1.00000000}.{abc}")]));
        Assert.False(settings.IsExcluded(Mic("{0.0.1.00000000}.{abd}"), [Mic("{0.0.1.00000000}.{abd}")]));
    }

    [Fact]
    public void AllowedAppsIgnoreCase()
    {
        var settings = new AppSettings { AllowedApps = [new AllowedApp { AppKey = "exe:SONAR.EXE|SONAR", DisplayName = "Sonar" }] };

        Assert.True(settings.IsAppAllowed("exe:sonar.exe|sonar"));
        Assert.False(settings.IsAppAllowed("exe:zoom.exe|zoom"));
    }

    [Fact]
    public void StoreRoundTripsEverySetting()
    {
        var store = new SettingsStore(_temp.File("settings.json"));
        var settings = new AppSettings
        {
            Hotkey = "Ctrl+F13",
            PlaySounds = false,
            ShowOverlay = false,
            OverlayCorner = OverlayCorner.BottomLeft,
            ShowScreenEdges = true,
            HideOverlayFromScreenCapture = false,
            AlertsEnabled = false,
            AlertOnStop = true,
            HistoryEnabled = false,
            HistoryRetentionDays = 7,
            ExcludedDevices = [new DeviceExclusion { StableKey = "mic|X|Y", LastKnownId = "id", DisplayName = "Sonar" }],
            AllowedApps = [new AllowedApp { AppKey = "exe:SONAR.EXE|SONAR", DisplayName = "Sonar" }],
            FirstRunDone = true,
        };

        store.Save(settings);
        AppSettings loaded = store.Load();

        Assert.Equal("Ctrl+F13", loaded.Hotkey);
        Assert.False(loaded.PlaySounds);
        Assert.False(loaded.ShowOverlay);
        Assert.Equal(OverlayCorner.BottomLeft, loaded.OverlayCorner);
        Assert.True(loaded.ShowScreenEdges);
        Assert.False(loaded.HideOverlayFromScreenCapture);
        Assert.False(loaded.AlertsEnabled);
        Assert.True(loaded.AlertOnStop);
        Assert.False(loaded.HistoryEnabled);
        Assert.Equal(7, loaded.HistoryRetentionDays);
        Assert.Equal(settings.ExcludedDevices, loaded.ExcludedDevices);
        Assert.Equal(settings.AllowedApps, loaded.AllowedApps);
        Assert.True(loaded.FirstRunDone);
    }

    [Fact]
    public void MissingOrDamagedSettingsGiveDefaults()
    {
        string path = _temp.File("settings.json");
        Assert.Equal(30, new SettingsStore(path).Load().HistoryRetentionDays);

        File.WriteAllText(path, "{ \"historyRetentionDays\": ");
        Assert.Equal(30, new SettingsStore(path).Load().HistoryRetentionDays);
    }

    [Fact]
    public void DamagedSettingsFallBackToTheBackup()
    {
        string path = _temp.File("settings.json");
        var store = new SettingsStore(path);
        store.Save(new AppSettings { HistoryRetentionDays = 5 });
        store.Save(new AppSettings { HistoryRetentionDays = 6 });
        File.WriteAllText(path, "garbage");

        Assert.Equal(5, store.Load().HistoryRetentionDays);
    }

    [Fact]
    public void LoadedSettingsAreNormalized()
    {
        string path = _temp.File("settings.json");
        File.WriteAllText(path, "{ \"hotkey\": \"nope\", \"historyRetentionDays\": 0, \"excludedDevices\": [null], \"allowedApps\": null, }");

        AppSettings settings = new SettingsStore(path).Load();

        Assert.Equal("Ctrl+Alt+Shift+M", settings.Hotkey);
        Assert.Equal(1, settings.HistoryRetentionDays);
        Assert.Empty(settings.ExcludedDevices);
        Assert.Empty(settings.AllowedApps);
        Assert.False(settings.IsExcluded(Mic("x"), [Mic("x")]));
    }

    [Fact]
    public void UndefinedOverlayCornerFromANumberIsRepaired()
    {
        string path = _temp.File("settings.json");
        File.WriteAllText(path, "{ \"overlayCorner\": 7 }");
        Assert.Equal(OverlayCorner.TopRight, new SettingsStore(path).Load().OverlayCorner);

        File.WriteAllText(path, "{ \"overlayCorner\": 3 }");
        Assert.Equal(OverlayCorner.BottomLeft, new SettingsStore(path).Load().OverlayCorner);
    }

    [Fact]
    public void CloneIsDeep()
    {
        var settings = new AppSettings { ExcludedDevices = [new DeviceExclusion { StableKey = "k", DisplayName = "d" }] };

        AppSettings copy = SettingsStore.Clone(settings);
        copy.ExcludedDevices.Clear();
        copy.HistoryRetentionDays = 1;

        Assert.Single(settings.ExcludedDevices);
        Assert.Equal(30, settings.HistoryRetentionDays);
    }

    private static MicEndpoint Mic(string id, string? hardware = null) => new()
    {
        Id = id,
        Name = "Microphone (Arctis Nova Pro Wireless)",
        EndpointDescription = "Microphone",
        State = EndpointState.Active,
        HardwareInstanceId = hardware,
    };
}
