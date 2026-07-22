namespace Concordant.Internal;

/// <summary>
/// Keyed pending-operation index with per-session clock ordering and a ready queue
/// for dependency-driven integration (avoids O(n²) list rescans).
/// </summary>
internal sealed class PendingIndex
{
    private readonly Dictionary<OpId, ConcordantOperation> _byId = new();
    private readonly Dictionary<SessionId, SortedDictionary<ulong, ConcordantOperation>> _bySession = new();
    private readonly Dictionary<OpId, HashSet<OpId>> _waiters = new();
    private readonly Queue<OpId> _ready = new();
    private readonly HashSet<OpId> _readySet = new();

    public int Count => _byId.Count;

    public bool Contains(OpId id) => _byId.ContainsKey(id);

    public bool TryGet(OpId id, out ConcordantOperation op) => _byId.TryGetValue(id, out op!);

    public IEnumerable<ConcordantOperation> Operations => _byId.Values;

    public IEnumerable<SessionId> Sessions => _bySession.Keys;

    public bool TryGetSessionMinClock(SessionId session, out ulong minClock)
    {
        if (_bySession.TryGetValue(session, out SortedDictionary<ulong, ConcordantOperation>? clocks)
            && clocks.Count > 0)
        {
            minClock = clocks.Keys.Min();
            return true;
        }

        minClock = 0;
        return false;
    }

    public bool TryGetBySessionClock(SessionId session, ulong clock, out ConcordantOperation op)
    {
        if (_bySession.TryGetValue(session, out SortedDictionary<ulong, ConcordantOperation>? clocks)
            && clocks.TryGetValue(clock, out ConcordantOperation? found))
        {
            op = found;
            return true;
        }

        op = null!;
        return false;
    }

    public void Add(ConcordantOperation op)
    {
        if (!_byId.TryAdd(op.Id, op))
        {
            return;
        }

        if (!_bySession.TryGetValue(op.Id.Session, out SortedDictionary<ulong, ConcordantOperation>? clocks))
        {
            clocks = new SortedDictionary<ulong, ConcordantOperation>();
            _bySession[op.Id.Session] = clocks;
        }

        clocks[op.Id.Clock] = op;
    }

    public bool Remove(OpId id, out ConcordantOperation op)
    {
        if (!_byId.Remove(id, out op!))
        {
            return false;
        }

        if (_bySession.TryGetValue(id.Session, out SortedDictionary<ulong, ConcordantOperation>? clocks))
        {
            _ = clocks.Remove(id.Clock);
            if (clocks.Count == 0)
            {
                _ = _bySession.Remove(id.Session);
            }
        }

        _ = _readySet.Remove(id);
        ClearWaiterEntries(id);
        return true;
    }

    public void Clear()
    {
        _byId.Clear();
        _bySession.Clear();
        _waiters.Clear();
        _ready.Clear();
        _readySet.Clear();
    }

    public void EnqueueReady(OpId id)
    {
        if (!_byId.ContainsKey(id) || !_readySet.Add(id))
        {
            return;
        }

        _ready.Enqueue(id);
    }

    public bool TryDequeueReady(out ConcordantOperation op)
    {
        while (_ready.Count > 0)
        {
            OpId id = _ready.Dequeue();
            _ = _readySet.Remove(id);
            if (_byId.TryGetValue(id, out ConcordantOperation? found))
            {
                op = found;
                return true;
            }
        }

        op = null!;
        return false;
    }

    public void RegisterWaiter(OpId dependency, OpId waiter)
    {
        if (!_waiters.TryGetValue(dependency, out HashSet<OpId>? set))
        {
            set = new HashSet<OpId>();
            _waiters[dependency] = set;
        }

        _ = set.Add(waiter);
    }

    public void NotifyIntegrated(OpId integratedId, Action<OpId> enqueueCandidate)
    {
        if (_waiters.Remove(integratedId, out HashSet<OpId>? waiters))
        {
            foreach (OpId waiter in waiters)
            {
                if (_byId.ContainsKey(waiter))
                {
                    enqueueCandidate(waiter);
                }
            }
        }
    }

    public PendingIndex Clone()
    {
        var copy = new PendingIndex();
        foreach (ConcordantOperation op in _byId.Values)
        {
            copy.Add(op);
        }

        return copy;
    }

    private void ClearWaiterEntries(OpId waiter)
    {
        if (_waiters.Count == 0)
        {
            return;
        }

        List<OpId>? emptyKeys = null;
        foreach (KeyValuePair<OpId, HashSet<OpId>> entry in _waiters)
        {
            if (entry.Value.Remove(waiter) && entry.Value.Count == 0)
            {
                emptyKeys ??= new List<OpId>();
                emptyKeys.Add(entry.Key);
            }
        }

        if (emptyKeys is null)
        {
            return;
        }

        foreach (OpId key in emptyKeys)
        {
            _ = _waiters.Remove(key);
        }
    }
}
