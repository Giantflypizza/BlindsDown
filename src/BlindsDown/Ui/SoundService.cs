using BlindsDown.Core.Audio;
using BlindsDown.Infrastructure;
using BlindsDown.Ui.Native;

namespace BlindsDown.Ui;

internal enum SoundCue
{
    Lock,
    Unlock,
    Alert,
}

/// <summary>
/// Plays the generated tones from <see cref="ToneGenerator"/> from memory (never a file path or URL).
/// The WAV images are kept for the app's lifetime. Never throws.
///
/// Why not SoundPlayer(Stream): it copies the stream into an ordinary managed byte[] that is pinned only
/// for the duration of the PlaySound call, but with SND_ASYNC winmm keeps reading that memory on its own
/// thread for the length of the sound. A garbage collection in that window moves the array (verified:
/// the buffer is a gen-0 object), so winmm would read freed memory: noise at best, an access violation
/// if the GC released the region. Here the WAV images live on the pinned object heap, which never moves,
/// and stay referenced until playback is stopped in <see cref="Dispose"/>.
/// </summary>
internal sealed class SoundService : IDisposable
{
    private readonly byte[] _lock = Pin(ToneGenerator.LockSound());
    private readonly byte[] _unlock = Pin(ToneGenerator.UnlockSound());
    private readonly byte[] _alert = Pin(ToneGenerator.AlertSound());
    private bool _failureLogged;
    private bool _disposed;

    /// <summary>Starts the sound and returns immediately; a new cue cuts off the previous one.</summary>
    public void Play(SoundCue cue)
    {
        if (_disposed)
        {
            return;
        }

        byte[] wav = cue switch
        {
            SoundCue.Lock => _lock,
            SoundCue.Unlock => _unlock,
            _ => _alert,
        };

        try
        {
            if (!PlayAsync(wav) && !_failureLogged)
            {
                _failureLogged = true; // e.g. no audio output device; once is enough
                Log.Warn($"Could not play the {cue} sound (no audio output?).");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Playing the {cue} sound failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Stop();
        }
        catch (Exception ex)
        {
            Log.Error("Stopping sound playback failed", ex);
        }
    }

    /// <summary>Copies a WAV image onto the pinned object heap (allocated once, never moved by the GC).</summary>
    private static byte[] Pin(byte[] wav)
    {
        byte[] pinned = GC.AllocateUninitializedArray<byte>(wav.Length, pinned: true);
        wav.CopyTo(pinned, 0);
        return pinned;
    }

    private static unsafe bool PlayAsync(byte[] pinnedWav)
    {
        // `fixed` on a pinned-heap array is free and the address stays valid after the block ends.
        fixed (byte* wav = pinnedWav)
        {
            return TrayNative.PlaySound(wav, 0, TrayNative.SndMemory | TrayNative.SndAsync | TrayNative.SndNoDefault) != 0;
        }
    }

    /// <summary>A null sound stops whatever this process is playing, so winmm lets go of our buffers.</summary>
    private static unsafe void Stop() => TrayNative.PlaySound(null, 0, 0);
}
