using System.Buffers.Binary;

namespace BlindsDown.Core.Audio;

/// <summary>
/// Generates the lock/unlock sounds in memory as small WAV files: two soft tones, falling for
/// "lock" and rising for "unlock". No sound files are shipped or loaded from disk.
/// </summary>
public static class ToneGenerator
{
    private const int SampleRate = 44100;

    /// <summary>Falling two-tone (G5 then C5): "blinds down".</summary>
    public static byte[] LockSound() => TwoTone(783.99, 523.25);

    /// <summary>Rising two-tone (C5 then G5): "blinds up".</summary>
    public static byte[] UnlockSound() => TwoTone(523.25, 783.99);

    /// <summary>A short, low double beep for warnings while locked.</summary>
    public static byte[] AlertSound() => TwoTone(440, 440, toneMs: 90, gapMs: 60);

    public static byte[] TwoTone(double firstHz, double secondHz, int toneMs = 140, int gapMs = 30, double volume = 0.22)
    {
        int toneSamples = SampleRate * toneMs / 1000;
        int gapSamples = SampleRate * gapMs / 1000;
        int total = (toneSamples * 2) + gapSamples;
        var samples = new short[total];
        WriteTone(samples, 0, toneSamples, firstHz, volume);
        WriteTone(samples, toneSamples + gapSamples, toneSamples, secondHz, volume);
        return ToWav(samples);
    }

    private static void WriteTone(short[] buffer, int offset, int count, double hz, double volume)
    {
        int fade = Math.Min(count / 4, SampleRate * 8 / 1000); // 8 ms fade in/out avoids clicks.
        for (int i = 0; i < count; i++)
        {
            double envelope = 1.0;
            if (i < fade)
            {
                envelope = i / (double)fade;
            }
            else if (i > count - fade)
            {
                envelope = (count - i) / (double)fade;
            }

            double t = i / (double)SampleRate;

            // A little second harmonic makes it sound like a soft chime instead of a pure beep.
            double sample = (Math.Sin(2 * Math.PI * hz * t) * 0.85) + (Math.Sin(4 * Math.PI * hz * t) * 0.15);
            buffer[offset + i] = (short)(sample * envelope * volume * short.MaxValue);
        }
    }

    /// <summary>Wraps 16-bit mono PCM samples in a standard RIFF/WAVE header.</summary>
    public static byte[] ToWav(short[] samples)
    {
        int dataBytes = samples.Length * 2;
        var wav = new byte[44 + dataBytes];
        Span<byte> span = wav;
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16); // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2); // block align
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16); // bits per sample
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (i * 2))..], samples[i]);
        }

        return wav;
    }
}
