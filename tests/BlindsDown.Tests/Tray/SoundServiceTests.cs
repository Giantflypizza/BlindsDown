using BlindsDown.Ui;

namespace BlindsDown.Tests.Tray;

public class SoundServiceTests
{
    [Fact]
    public void CanBeCreatedAndDisposedWithoutPlaying()
    {
        var sounds = new SoundService();
        sounds.Dispose();
        sounds.Play(SoundCue.Lock); // ignored after Dispose, never throws
    }

    [Fact]
    public void PlaysEveryCueWithoutThrowing()
    {
        TrayTestEnvironment.RequireRealUi(); // audible on the real output device
        using var sounds = new SoundService();
        foreach (SoundCue cue in Enum.GetValues<SoundCue>())
        {
            sounds.Play(cue);
            Thread.Sleep(400);
        }
    }
}
