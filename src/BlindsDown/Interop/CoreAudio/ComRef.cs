using System.Runtime.InteropServices;

namespace BlindsDown.Interop.CoreAudio;

/// <summary>
/// One owned reference to a COM object, released as soon as it is disposed.
///
/// Why not let the garbage collector do it: every endpoint, volume control and session manager holds a
/// connection to the Windows audio service, and BlindsDown re-reads devices every few seconds. Left to the GC
/// (which may not run for minutes in a quiet tray app) thousands of these would pile up in the audio service.
///
/// Why a unique wrapper: the runtime normally shares one wrapper (RCW) per COM object across the whole
/// process. Releasing a shared wrapper would break any other component that happens to hold the same object
/// (Windows may hand out the same object twice). <see cref="ComRef.TryTake{T}"/> therefore wraps each pointer
/// in a wrapper of its own (Marshal.GetUniqueObjectForIUnknown), which only this <see cref="ComRef{T}"/> uses.
/// </summary>
internal sealed class ComRef<T> : IDisposable
    where T : class
{
    private T? _value;

    internal ComRef(T value)
    {
        _value = value;
    }

    public T Value => _value ?? throw new ObjectDisposedException(typeof(T).Name);

    public void Dispose()
    {
        T? value = Interlocked.Exchange(ref _value, null);
        if (value is not null)
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }
}

/// <summary>Creates <see cref="ComRef{T}"/> instances from the raw interface pointers our COM declarations return.</summary>
internal static class ComRef
{
    /// <summary>
    /// Takes ownership of an interface pointer returned through an "out" parameter. Returns null when the
    /// pointer is null or the object does not implement <typeparamref name="T"/>. Either way the raw reference
    /// is released here: the returned wrapper holds a reference of its own.
    /// </summary>
    public static ComRef<T>? TryTake<T>(nint pointer)
        where T : class
    {
        if (pointer == 0)
        {
            return null;
        }

        object wrapper;
        try
        {
            wrapper = Marshal.GetUniqueObjectForIUnknown(pointer);
        }
        finally
        {
            _ = Marshal.Release(pointer);
        }

        // The type test is a QueryInterface for T on the unique wrapper.
        if (wrapper is T value)
        {
            return new ComRef<T>(value);
        }

        _ = Marshal.ReleaseComObject(wrapper);
        return null;
    }

    /// <summary>Like <see cref="TryTake{T}"/>, but a missing object is an error.</summary>
    public static ComRef<T> Take<T>(nint pointer, string operation)
        where T : class =>
        TryTake<T>(pointer) ?? throw new CoreAudioException(operation, CoreAudioErrors.NoInterface);
}
