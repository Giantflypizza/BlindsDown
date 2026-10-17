using BlindsDown.Core.Usage;
using BlindsDown.Usage;
using Microsoft.Win32;

namespace BlindsDown.Tests.SystemServices;

/// <summary>Writes only under a throwaway HKCU\Software\BlindsDownTests\{guid} key; the real usage
/// registry is only read.</summary>
public sealed class RegistryConsentStoreSourceTests
{
    private static readonly long Start = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero).ToFileTime();
    private static readonly long Stop = Start + TimeSpan.TicksPerMinute;

    [Fact]
    public void FileTimesAreReadFromQwordAndBinaryValues()
    {
        using var test = new TestRegistryKey();
        test.Key.SetValue("Qword", 134367154186115602L, RegistryValueKind.QWord);
        test.Key.SetValue("Binary", BitConverter.GetBytes(134367154186115602L), RegistryValueKind.Binary);
        test.Key.SetValue("Short", new byte[] { 1, 2 }, RegistryValueKind.Binary);
        test.Key.SetValue("Text", "nope", RegistryValueKind.String);

        Assert.Equal(134367154186115602L, RegistryConsentStoreSource.ReadFileTime(test.Key, "Qword"));
        Assert.Equal(134367154186115602L, RegistryConsentStoreSource.ReadFileTime(test.Key, "Binary"));
        Assert.Equal(0, RegistryConsentStoreSource.ReadFileTime(test.Key, "Short"));
        Assert.Equal(0, RegistryConsentStoreSource.ReadFileTime(test.Key, "Text"));
        Assert.Equal(0, RegistryConsentStoreSource.ReadFileTime(test.Key, "Missing"));
    }

    [Fact]
    public async Task WatchReportsChangesInTheSubtreeAndKeepsWatching()
    {
        using var test = new TestRegistryKey();
        using RegistryKey child = test.Key.CreateSubKey("NonPackaged", writable: true);
        var changes = new SemaphoreSlim(0);

        using RegistryKeyWatch? watch = RegistryKeyWatch.TryStart(Registry.CurrentUser, test.Path, () => changes.Release());
        Assert.NotNull(watch);

        child.SetValue("LastUsedTimeStart", 1L, RegistryValueKind.QWord);
        Assert.True(await changes.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // Re-armed after the first signal: a second change is seen too.
        using (test.Key.CreateSubKey("Contoso.App_abc"))
        {
        }

        Assert.True(await changes.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(watch.IsBroken);
    }

    [Fact]
    public void ConsentStoreLayoutIsReadFromEveryHive()
    {
        using var hkcu = new TestRegistryKey();
        using var hklm = new TestRegistryKey();
        WriteApp(hkcu.Key, ConsentStore.MicrophoneKey + @"\Contoso.Recorder_abc123", Start, 0);
        WriteApp(hkcu.Key, ConsentStore.MicrophoneKey + @"\NonPackaged\C:#Apps#Tool#2#tool.exe", Start, Stop);
        using (RegistryKey neverUsed = hkcu.Key.CreateSubKey(ConsentStore.MicrophoneKey + @"\Contoso.NeverUsed_abc123", writable: true))
        {
            // Only a permission, no usage times: not an entry.
            neverUsed.SetValue("Value", "Deny");
        }

        WriteApp(hklm.Key, ConsentStore.MicrophoneKey + @"\NonPackaged\C:#Windows#System32#svchost.exe", Start, 0);
        WriteApp(hklm.Key, ConsentStore.WebcamKey + @"\Contoso.Camera_abc123", Start, Stop);

        using var source = new RegistryConsentStoreSource([(hkcu.Key, "HKCU"), (hklm.Key, "HKLM")]);

        IReadOnlyList<ConsentStoreEntry> microphone = source.Read(CaptureKind.Microphone);
        Assert.Equal(3, microphone.Count);
        Assert.Contains(new ConsentStoreEntry(CaptureKind.Microphone, "Contoso.Recorder_abc123", true, Start, 0), microphone);
        Assert.Contains(new ConsentStoreEntry(CaptureKind.Microphone, "C:#Apps#Tool#2#tool.exe", false, Start, Stop), microphone);
        Assert.Contains(new ConsentStoreEntry(CaptureKind.Microphone, "C:#Windows#System32#svchost.exe", false, Start, 0), microphone);
        Assert.Equal([new ConsentStoreEntry(CaptureKind.Camera, "Contoso.Camera_abc123", true, Start, Stop)], source.Read(CaptureKind.Camera));
    }

    [Fact]
    public void MonitorReportsInUseEntriesFromARealRegistryLayout()
    {
        using var hkcu = new TestRegistryKey();
        WriteApp(hkcu.Key, ConsentStore.MicrophoneKey + @"\NonPackaged\C:#Apps#Tool#2#tool.exe", Start, 0);
        WriteApp(hkcu.Key, ConsentStore.MicrophoneKey + @"\NonPackaged\C:#Apps#Done#done.exe", Start, Stop);
        using var monitor = new ConsentStoreMonitor(
            new FakeIdentityResolver(),
            new RegistryConsentStoreSource([(hkcu.Key, "HKCU")]),
            () => [@"C:\Apps\Tool#2\tool.exe", @"C:\Apps\Done\done.exe"],
            () => DateTimeOffset.FromFileTime(Start).AddHours(-1));

        UsageSignal signal = Assert.Single(monitor.GetActiveSignals(CaptureKind.Microphone));
        Assert.Equal(@"c:\apps\tool#2\tool.exe", signal.App.Key);
        Assert.Empty(monitor.GetActiveSignals(CaptureKind.Camera));

        UsageRecord record = Assert.Single(monitor.GetLastUsedRecords(DateTimeOffset.FromFileTime(Start).AddDays(-1)));
        Assert.Equal(@"C:\Apps\Done\done.exe", record.ExePath);
        Assert.True(record.Approximate);
    }

    [Fact]
    public async Task WatchingPicksUpACapabilityKeyCreatedLater()
    {
        using var root = new TestRegistryKey();
        using var source = new RegistryConsentStoreSource([(root.Key, "TEST")]);
        var changes = new SemaphoreSlim(0);

        // Windows creates the webcam key on first camera use, so it may not exist when watching starts.
        source.EnsureWatching(() => changes.Release());
        using (root.Key.CreateSubKey(ConsentStore.WebcamKey, writable: true))
        {
        }

        // The monitor's poll calls this again; now the key exists and gets a watch.
        source.EnsureWatching(() => changes.Release());
        WriteApp(root.Key, ConsentStore.WebcamKey + @"\Contoso.Camera_abc123", Start, 0);

        Assert.True(await changes.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletingTheWatchedKeyMarksTheWatchBroken()
    {
        using var test = new TestRegistryKey();
        using (test.Key.CreateSubKey("watched"))
        {
        }

        var changes = new SemaphoreSlim(0);
        using RegistryKeyWatch? watch = RegistryKeyWatch.TryStart(Registry.CurrentUser, test.Path + @"\watched", () => changes.Release());
        Assert.NotNull(watch);
        Assert.False(watch.IsBroken);

        test.Key.DeleteSubKeyTree("watched");

        // Re-arming a deleted key fails, so the owner knows to start a new watch once the key is back.
        Assert.True(await changes.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(watch.IsBroken);
    }

    [Fact]
    public void MissingKeysCannotBeWatched()
    {
        using var test = new TestRegistryKey();

        Assert.Null(RegistryKeyWatch.TryStart(Registry.CurrentUser, test.Path + @"\does-not-exist", () => { }));
    }

    [Fact]
    public void RealUsageRegistryIsReadable()
    {
        using var source = new RegistryConsentStoreSource();

        foreach (CaptureKind kind in Enum.GetValues<CaptureKind>())
        {
            foreach (ConsentStoreEntry entry in source.Read(kind))
            {
                Assert.Equal(kind, entry.Kind);
                Assert.NotEqual(0, entry.LastUsedTimeStart);
                Assert.False(string.IsNullOrEmpty(entry.KeyName));
                Assert.NotEqual(ConsentStore.NonPackagedSubkey, entry.KeyName, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Writes one app's usage times the way Windows does (REG_QWORD FILETIMEs).</summary>
    private static void WriteApp(RegistryKey root, string path, long start, long stop)
    {
        using RegistryKey app = root.CreateSubKey(path, writable: true);
        app.SetValue("Value", "Allow");
        app.SetValue("LastUsedTimeStart", start, RegistryValueKind.QWord);
        app.SetValue("LastUsedTimeStop", stop, RegistryValueKind.QWord);
    }
}
