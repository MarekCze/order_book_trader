using OrderFlow.Domain.Primitives;

namespace OrderFlow.Domain.Features;

/// <summary>
/// Rolling maximum (or minimum) over a trailing time window via a monotonic deque —
/// O(1) amortized per add. Used for F9's 30-minute swing-extreme tracking. An entry
/// ages out when exactly windowSeconds old. Deterministic; ties keep the newest entry.
/// </summary>
public sealed class MonotonicWindowExtreme
{
    private readonly int _windowSeconds;
    private readonly bool _trackMax;
    private (long Second, long Value)[] _deque = new (long, long)[16];
    private int _head; // first valid
    private int _count;

    public MonotonicWindowExtreme(int windowSeconds, bool trackMax)
    {
        _windowSeconds = windowSeconds;
        _trackMax = trackMax;
    }

    /// <summary>Current window extreme; null when the window is empty.</summary>
    public long? Extreme => _count > 0 ? _deque[_head].Value : null;

    public void Add(Timestamp ts, long value)
    {
        long sec = (long)(ts.UnixNanos / 1_000_000_000UL);
        Evict(sec);
        // Pop dominated entries from the back: anything not strictly better than the
        // newcomer can never be the window extreme again.
        while (_count > 0)
        {
            long back = _deque[(_head + _count - 1) % _deque.Length].Value;
            bool dominated = _trackMax ? back <= value : back >= value;
            if (!dominated)
            {
                break;
            }
            _count--;
        }
        if (_count == _deque.Length)
        {
            Grow();
        }
        _deque[(_head + _count) % _deque.Length] = (sec, value);
        _count++;
    }

    /// <summary>Evicts aged entries without adding (call before reading after idle gaps).</summary>
    public void Advance(Timestamp ts) => Evict((long)(ts.UnixNanos / 1_000_000_000UL));

    private void Evict(long nowSecond)
    {
        while (_count > 0 && nowSecond - _deque[_head].Second >= _windowSeconds)
        {
            _head = (_head + 1) % _deque.Length;
            _count--;
        }
    }

    private void Grow()
    {
        var bigger = new (long, long)[_deque.Length * 2];
        for (int i = 0; i < _count; i++)
        {
            bigger[i] = _deque[(_head + i) % _deque.Length];
        }
        _deque = bigger;
        _head = 0;
    }
}
