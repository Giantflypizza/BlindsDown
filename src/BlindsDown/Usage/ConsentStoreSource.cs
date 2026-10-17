using System.Security;
using BlindsDown.Core.Usage;
using BlindsDown.Infrastructure;
using Microsoft.Win32;

namespace BlindsDown.Usage;

/// <summary>
/// One app's entry in Windows' usage registry. <see cref="KeyName"/> is the package family for Store apps,
/// or the exe path with '\' encoded as '#' for desktop apps. Times are UTC FILETIMEs (0 = not set).
/// </summary>
internal sealed record ConsentStoreEntry(CaptureKind Kind, string KeyName, bool IsPackaged, long LastUsedTimeStart, long LastUsedTimeStop);

/// <summary>Where <see cref="ConsentStoreMonitor"/> gets its entries: the real registry, or a fake in tests.</summary>
internal interface IConsentStoreSource : IDisposable
{
    /// <summary>Every entry that has usage times for one capability, from every hive.</summary>
    IReadOnlyList<ConsentStoreEntry> Read(CaptureKind kind);

    /// <summary>
    /// Makes sure change notifications are armed. Safe to call repeatedly: keys that did not exist yet
    /// (the webcam key appears on first camera use) or whose watch broke are retried.
    /// <paramref name="onChanged"/> may be called on any thread.
    /// </summary>
    void EnsureWatching(Action onChanged);
}

/// <summary>
/// Reads the ConsentStore under HKCU (per-user apps) and HKLM (machine-wide entries, e.g. services).
/// Read-only: it never writes to the registry.
/// </summary>
internal sealed class RegistryConsentStoreSource : IConsentStoreSource
{
    private readonly IReadOnlyList<(RegistryKey Hive, string Name)> _hives;
    private readonly object _gate = new();
    private readonly Dictionary<string, RegistryKeyWatch> _watches = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readFailuresLogged = new(StringComparer.Ordinal);
    private bool _disposed;

    public RegistryConsentStoreSource()
        : this([(Registry.CurrentUser, "HKCU"), (Registry.LocalMachine, "HKLM")])
    {
    }

    /// <param name="hives">Roots the ConsentStore paths are read under: HKCU and HKLM in the app, a throwaway
    /// test key in tests. The caller keeps ownership of the keys.</param>
    internal RegistryConsentStoreSource(IReadOnlyList<(RegistryKey Hive, string Name)> hives)
    {
        _hives = hives;
    }

    public IReadOnlyList<ConsentStoreEntry> Read(CaptureKind kind)
    {
        var entries = new List<ConsentStoreEntry>();
        foreach ((RegistryKey hive, string hiveName) in _hives)
        {
            try
            {
                ReadCapability(hive, hiveName, kind, entries);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                LogReadFailureOnce(hiveName, kind, ex);
            }
        }

        return entries;
    }

    public void EnsureWatching(Action onChanged)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach ((RegistryKey hive, string hiveName) in _hives)
            {
                foreach (CaptureKind kind in Enum.GetValues<CaptureKind>())
                {
                    string id = hiveName + "|" + kind;
                    if (_watches.TryGetValue(id, out RegistryKeyWatch? existing))
                    {
                        if (!existing.IsBroken)
                        {
                            continue;
                        }

                        existing.Dispose();
                        _watches.Remove(id);
                    }

                    RegistryKeyWatch? watch = RegistryKeyWatch.TryStart(hive, KeyPath(kind), onChanged);
                    if (watch is not null)
                    {
                        _watches[id] = watch;
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (RegistryKeyWatch watch in _watches.Values)
            {
                watch.Dispose();
            }

            _watches.Clear();
        }
    }

    /// <summary>LastUsedTime values are REG_QWORD; a REG_BINARY of 8 bytes is accepted as well.</summary>
    internal static long ReadFileTime(RegistryKey key, string valueName) => key.GetValue(valueName) switch
    {
        long value => value,
        byte[] { Length: >= 8 } bytes => BitConverter.ToInt64(bytes, 0),
        _ => 0,
    };

    private static string KeyPath(CaptureKind kind) => kind == CaptureKind.Camera ? ConsentStore.WebcamKey : ConsentStore.MicrophoneKey;

    private void ReadCapability(RegistryKey hive, string hiveName, CaptureKind kind, List<ConsentStoreEntry> into)
    {
        using RegistryKey? capability = hive.OpenSubKey(KeyPath(kind));
        if (capability is null)
        {
            return;
        }

        foreach (string name in capability.GetSubKeyNames())
        {
            if (!name.Equals(ConsentStore.NonPackagedSubkey, StringComparison.OrdinalIgnoreCase))
            {
                AddEntry(capability, name, hiveName, kind, isPackaged: true, into);
                continue;
            }

            try
            {
                using RegistryKey? nonPackaged = capability.OpenSubKey(name);
                if (nonPackaged is null)
                {
                    continue;
                }

                foreach (string exeKeyName in nonPackaged.GetSubKeyNames())
                {
                    AddEntry(nonPackaged, exeKeyName, hiveName, kind, isPackaged: false, into);
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Keep the Store-app entries of this hive.
                LogReadFailureOnce(hiveName, kind, ex);
            }
        }
    }

    /// <summary>One app's key. Failures stay contained here: one key with a restrictive ACL, or one deleted
    /// while it is read, must not hide every other app in the hive.</summary>
    private void AddEntry(RegistryKey parent, string name, string hiveName, CaptureKind kind, bool isPackaged, List<ConsentStoreEntry> into)
    {
        try
        {
            // Null when the app's key was deleted between listing and opening.
            using RegistryKey? key = parent.OpenSubKey(name);
            if (key is null)
            {
                return;
            }

            // Apps that were only granted or denied permission have no usage times: nothing to report.
            long start = ReadFileTime(key, "LastUsedTimeStart");
            if (start == 0)
            {
                return;
            }

            into.Add(new ConsentStoreEntry(kind, name, isPackaged, start, ReadFileTime(key, "LastUsedTimeStop")));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            LogReadFailureOnce(hiveName, kind, ex);
        }
    }

    private void LogReadFailureOnce(string hiveName, CaptureKind kind, Exception ex)
    {
        lock (_gate)
        {
            if (!_readFailuresLogged.Add(hiveName + "|" + kind))
            {
                return;
            }
        }

        Log.Warn($"Can't read the {hiveName} usage registry for {kind}: {ex.GetType().Name}: {ex.Message}");
    }
}
