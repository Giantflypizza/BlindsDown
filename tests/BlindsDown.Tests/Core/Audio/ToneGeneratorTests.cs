using System.Buffers.Binary;
using System.Text;
using BlindsDown.Core.Audio;

namespace BlindsDown.Tests.Core.Audio;

public sealed class ToneGeneratorTests
{
    public static TheoryData<string> Sounds => ["lock", "unlock", "alert"];

    [Theory]
    [MemberData(nameof(Sounds))]
    public void SoundsAreValidPcmWaveFiles(string name)
    {
        byte[] wav = Generate(name);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(44100, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(88200, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(wav.Length - 44, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(0, (wav.Length - 44) % 2);
    }

    [Theory]
    [MemberData(nameof(Sounds))]
    public void SoundsAreShortQuietAndClickFree(string name)
    {
        short[] samples = Samples(Generate(name));

        double seconds = samples.Length / 44100.0;
        Assert.InRange(seconds, 0.1, 0.5);
        int peak = samples.Max(s => Math.Abs((int)s));
        Assert.InRange(peak, 1000, (int)(short.MaxValue * 0.25));

        // Fades in from silence and out to near-silence: no click at either end.
        Assert.Equal(0, samples[0]);
        Assert.InRange(Math.Abs((int)samples[^1]), 0, 200);
    }

    [Fact]
    public void LockFallsAndUnlockRises()
    {
        Assert.Equal(ToneGenerator.LockSound().Length, ToneGenerator.UnlockSound().Length);
        Assert.NotEqual(ToneGenerator.LockSound(), ToneGenerator.UnlockSound());

        // 140 ms tones, 30 ms gap at 44.1 kHz.
        Assert.Equal(44 + (2 * ((6174 * 2) + 1323)), ToneGenerator.LockSound().Length);
        Assert.True(ZeroCrossings(Samples(ToneGenerator.LockSound())[..6174]) > ZeroCrossings(Samples(ToneGenerator.LockSound())[^6174..]));
        Assert.True(ZeroCrossings(Samples(ToneGenerator.UnlockSound())[..6174]) < ZeroCrossings(Samples(ToneGenerator.UnlockSound())[^6174..]));
    }

    [Fact]
    public void GenerationIsDeterministic() => Assert.Equal(ToneGenerator.AlertSound(), ToneGenerator.AlertSound());

    [Fact]
    public void ToWavOfNoSamplesIsAnEmptyButValidFile()
    {
        byte[] wav = ToneGenerator.ToWav([]);

        Assert.Equal(44, wav.Length);
        Assert.Equal(36, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }

    [Fact]
    public void ToWavWritesSamplesLittleEndian()
    {
        byte[] wav = ToneGenerator.ToWav([1, -2, short.MaxValue]);

        byte[] expected = [0x01, 0x00, 0xFE, 0xFF, 0xFF, 0x7F];
        Assert.Equal(expected, wav[44..]);
    }

    private static byte[] Generate(string name) => name switch
    {
        "lock" => ToneGenerator.LockSound(),
        "unlock" => ToneGenerator.UnlockSound(),
        _ => ToneGenerator.AlertSound(),
    };

    private static short[] Samples(byte[] wav)
    {
        var samples = new short[(wav.Length - 44) / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (i * 2)));
        }

        return samples;
    }

    private static int ZeroCrossings(short[] samples)
    {
        int count = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] < 0) != (samples[i] < 0))
            {
                count++;
            }
        }

        return count;
    }
}
