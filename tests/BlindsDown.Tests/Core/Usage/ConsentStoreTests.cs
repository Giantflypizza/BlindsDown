using BlindsDown.Core.Usage;

namespace BlindsDown.Tests.Core.Usage;

public sealed class ConsentStoreTests
{
    private static readonly DateTimeOffset Boot = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NonPackagedKeyNamesReplaceBackslashes()
    {
        Assert.Equal("C:#Program Files#Zoom#bin#Zoom.exe", ConsentStore.EncodeNonPackagedKeyName(@"C:\Program Files\Zoom\bin\Zoom.exe"));
        Assert.Equal(@"C:\Tools\Zoom.exe", ConsentStore.DecodeNonPackagedKeyNameForDisplay("C:#Tools#Zoom.exe"));
    }

    [Fact]
    public void FileTimesConvertToUtc()
    {
        long fileTime = Boot.AddHours(1).ToFileTime();

        Assert.Equal(Boot.AddHours(1), ConsentStore.FromFileTime(fileTime));
        Assert.Equal(TimeSpan.Zero, ConsentStore.FromFileTime(fileTime)!.Value.Offset);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(long.MaxValue)]
    public void InvalidFileTimesAreNull(long fileTime) => Assert.Null(ConsentStore.FromFileTime(fileTime));

    [Fact]
    public void InUseOnlyWhenStartedAfterBootNotStoppedAndRunning()
    {
        long afterBoot = Boot.AddMinutes(5).ToFileTime();
        long beforeBoot = Boot.AddMinutes(-5).ToFileTime();
        long stopped = Boot.AddMinutes(6).ToFileTime();

        Assert.True(ConsentStore.LooksInUse(afterBoot, 0, Boot, processRunning: true));
        Assert.False(ConsentStore.LooksInUse(afterBoot, stopped, Boot, processRunning: true));
        Assert.False(ConsentStore.LooksInUse(afterBoot, 0, Boot, processRunning: false));
        Assert.False(ConsentStore.LooksInUse(beforeBoot, 0, Boot, processRunning: true));
        Assert.False(ConsentStore.LooksInUse(0, 0, Boot, processRunning: true));
    }

    [Fact]
    public void RegistryPathsPointAtTheConsentStore()
    {
        Assert.EndsWith(@"ConsentStore\microphone", ConsentStore.MicrophoneKey, StringComparison.Ordinal);
        Assert.EndsWith(@"ConsentStore\webcam", ConsentStore.WebcamKey, StringComparison.Ordinal);
        Assert.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager", ConsentStore.MicrophoneKey, StringComparison.Ordinal);
    }
}
