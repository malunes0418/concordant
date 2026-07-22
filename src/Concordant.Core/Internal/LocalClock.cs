namespace Concordant.Internal;

/// <summary>Allocates contiguous clocks and validated Lamport values for the local writer session.</summary>
internal sealed class LocalClock
{
    private readonly SessionId _session;
    private ulong _clock;
    private ulong _lamport;
    private OpId? _maxObserved;
    private ulong _observedLamport;

    public LocalClock(SessionId session)
    {
        _session = session;
    }

    public SessionId Session => _session;

    public ulong Clock => _clock;

    internal readonly struct Snapshot
    {
        public required ulong Clock { get; init; }
        public required ulong Lamport { get; init; }
        public required OpId? MaxObserved { get; init; }
        public required ulong ObservedLamport { get; init; }
    }

    internal Snapshot Capture() => new()
    {
        Clock = _clock,
        Lamport = _lamport,
        MaxObserved = _maxObserved,
        ObservedLamport = _observedLamport,
    };

    internal void Restore(Snapshot snapshot)
    {
        _clock = snapshot.Clock;
        _lamport = snapshot.Lamport;
        _maxObserved = snapshot.MaxObserved;
        _observedLamport = snapshot.ObservedLamport;
    }

    public void ObserveOp(OpId id, ulong lamport)
    {
        if (_maxObserved is null
            || lamport > _observedLamport
            || (lamport == _observedLamport && id.CompareTo(_maxObserved.Value) > 0))
        {
            _maxObserved = id;
            _observedLamport = lamport;
        }
    }

    public void ObserveIntegrated(IEnumerable<ConcordantOperation> operations)
    {
        foreach (ConcordantOperation op in operations)
        {
            ObserveOp(op.Id, op.Lamport);
        }
    }

    public OpId NextId()
    {
        _clock = checked(_clock + 1);
        return new OpId(_session, _clock);
    }

    public ulong NextLamport(out OpId? source)
    {
        source = _maxObserved;
        ulong next = checked(Math.Max(_lamport, _observedLamport) + 1);
        _lamport = next;
        return next;
    }

    public void NoteLocal(OpId id, ulong lamport)
    {
        _maxObserved = id;
        _observedLamport = lamport;
        _lamport = lamport;
    }
}
