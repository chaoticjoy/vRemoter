namespace vRemoter.Core.Input;

/// <summary>
/// Thread-safe, fail-open RawInput &lt;-&gt; WH_KEYBOARD_LL per-device key correlation engine.
/// Ported from 10RemoteHub's win_hid_suppress.py (CorrelationEngine).
/// Ensures that only keystrokes proven to originate from a target remote are suppressed
/// when remapped, while keystrokes from the PC's built-in keyboard always pass through.
/// </summary>
public sealed class KeyCorrelationEngine
{
    private readonly record struct RecordedEvent(long TimestampMs, ushort VirtualKey, bool IsDown, bool IsRemote);

    private readonly object _sync = new();
    private readonly Queue<RecordedEvent> _recent = new(128);
    private readonly HashSet<ushort> _remoteHeldVks = [];
    private readonly HashSet<ushort> _otherHeldVks = [];
    private readonly Func<long> _clockMs;
    private readonly long _windowMs;

    public KeyCorrelationEngine(long windowMs = 80, Func<long>? clockMs = null)
    {
        _windowMs = Math.Max(10, windowMs);
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// Records a RawInput keyboard or consumer event from a device handle.
    /// </summary>
    public void RecordRawEvent(ushort virtualKey, bool isDown, bool isTargetRemote)
    {
        if (virtualKey == 0) return;
        long now = _clockMs();
        lock (_sync)
        {
            PruneLocked(now);
            while (_recent.Count >= 128)
            {
                _recent.Dequeue();
            }
            _recent.Enqueue(new RecordedEvent(now, virtualKey, isDown, isTargetRemote));

            var heldSet = isTargetRemote ? _remoteHeldVks : _otherHeldVks;
            if (isDown)
            {
                heldSet.Add(virtualKey);
            }
            else
            {
                heldSet.Remove(virtualKey);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _recent.Clear();
            _remoteHeldVks.Clear();
            _otherHeldVks.Clear();
        }
    }

    /// <summary>
    /// Decides whether a WH_KEYBOARD_LL hook event for <paramref name="virtualKey"/>
    /// came from a target remote.
    /// Returns:
    /// - true: proven from target remote
    /// - false: proven from another keyboard (must pass through!)
    /// - null: no matching raw input event recorded yet
    /// </summary>
    public bool? TryDecideIsRemote(ushort virtualKey, bool isDown)
    {
        if (virtualKey == 0) return false;
        long now = _clockMs();
        lock (_sync)
        {
            PruneLocked(now);

            int count = _recent.Count;
            bool matched = false;
            bool matchedIsRemote = false;

            for (int i = 0; i < count; i++)
            {
                var item = _recent.Dequeue();
                if (!matched && item.VirtualKey == virtualKey && item.IsDown == isDown)
                {
                    matched = true;
                    matchedIsRemote = item.IsRemote;
                    continue;
                }
                _recent.Enqueue(item);
            }

            if (matched)
            {
                return matchedIsRemote;
            }

            if (_remoteHeldVks.Contains(virtualKey) && !_otherHeldVks.Contains(virtualKey))
            {
                return true;
            }
            if (_otherHeldVks.Contains(virtualKey) && !_remoteHeldVks.Contains(virtualKey))
            {
                return false;
            }
            return null;
        }
    }

    private void PruneLocked(long now)
    {
        long cutoff = now - _windowMs;
        while (_recent.Count > 0 && _recent.Peek().TimestampMs < cutoff)
        {
            _recent.Dequeue();
        }
    }
}

