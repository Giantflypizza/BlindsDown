using System.Diagnostics.CodeAnalysis;

namespace BlindsDown.Usage;

/// <summary>
/// A small thread-safe cache whose entries expire a fixed time after they were stored. It is hard-bounded:
/// when full, expired entries are dropped and, if that is not enough, everything is. Everything cached here
/// is cheap to look up again, so a strict memory bound matters more than a clever eviction order.
/// </summary>
internal sealed class ExpiringCache<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly object _gate = new();
    private readonly long _lifetimeMs;
    private readonly int _maxEntries;
    private readonly Func<long> _nowMs;

    /// <param name="nowMilliseconds">Monotonic clock in milliseconds (tests pass a fake one).</param>
    public ExpiringCache(TimeSpan lifetime, int maxEntries, IEqualityComparer<TKey>? comparer = null, Func<long>? nowMilliseconds = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _entries = new Dictionary<TKey, Entry>(comparer);
        _lifetimeMs = (long)lifetime.TotalMilliseconds;
        _maxEntries = maxEntries;
        _nowMs = nowMilliseconds ?? (() => Environment.TickCount64);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry entry))
            {
                if (_nowMs() < entry.ExpiresAt)
                {
                    value = entry.Value;
                    return true;
                }

                _entries.Remove(key);
            }

            value = default;
            return false;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            long now = _nowMs();
            if (_entries.Count >= _maxEntries && !_entries.ContainsKey(key))
            {
                foreach (TKey expired in _entries.Where(e => now >= e.Value.ExpiresAt).Select(e => e.Key).ToList())
                {
                    _entries.Remove(expired);
                }

                if (_entries.Count >= _maxEntries)
                {
                    _entries.Clear();
                }
            }

            _entries[key] = new Entry(value, now + _lifetimeMs);
        }
    }

    private readonly record struct Entry(TValue Value, long ExpiresAt);
}
