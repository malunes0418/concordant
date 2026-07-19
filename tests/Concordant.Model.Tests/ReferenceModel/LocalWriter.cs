namespace Concordant.Model.Tests.ReferenceModel;

/// <summary>Helper that allocates contiguous clocks and validated Lamport values for one session.</summary>
public sealed class LocalWriter
{
    private readonly SessionId _session;
    private ulong _clock;
    private ulong _lamport;
    private OpId? _maxObserved;

    public LocalWriter(SessionId session)
    {
        _session = session;
    }

    public SessionId Session => _session;

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

    private ulong _observedLamport;

    public RefBatch Transact(Action<TransactionBuilder> build)
    {
        var builder = new TransactionBuilder(this);
        build(builder);
        return builder.Complete();
    }

    internal OpId NextId()
    {
        _clock++;
        return new OpId(_session, _clock);
    }

    internal ulong NextLamport(out OpId? source)
    {
        source = _maxObserved;
        ulong next = checked(Math.Max(_lamport, _observedLamport) + 1);
        _lamport = next;
        return next;
    }

    internal void NoteLocal(OpId id, ulong lamport)
    {
        _maxObserved = id;
        _observedLamport = lamport;
    }

    public sealed class TransactionBuilder
    {
        private readonly LocalWriter _writer;
        private readonly List<RefOperation> _ops = new();

        internal TransactionBuilder(LocalWriter writer)
        {
            _writer = writer;
        }

        public TransactionBuilder DeclareRoot(string name, RootKind kind)
        {
            ulong lamport = _writer.NextLamport(out OpId? source);
            OpId id = _writer.NextId();
            _ops.Add(new RefOperation.RootDeclare(name, kind)
            {
                Id = id,
                Lamport = lamport,
                LamportSource = source,
            });
            _writer.NoteLocal(id, lamport);
            return this;
        }

        public TransactionBuilder MapSet(string mapName, string key, RefScalar value)
        {
            ulong lamport = _writer.NextLamport(out OpId? source);
            OpId id = _writer.NextId();
            _ops.Add(new RefOperation.MapSet(mapName, key, value)
            {
                Id = id,
                Lamport = lamport,
                LamportSource = source,
            });
            _writer.NoteLocal(id, lamport);
            return this;
        }

        public TransactionBuilder Insert(string container, OpId? left, OpId? right, RefScalar content)
        {
            ulong lamport = _writer.NextLamport(out OpId? source);
            OpId id = _writer.NextId();
            _ops.Add(new RefOperation.SeqInsert(container, left, right, content)
            {
                Id = id,
                Lamport = lamport,
                LamportSource = source,
            });
            _writer.NoteLocal(id, lamport);
            return this;
        }

        public TransactionBuilder Delete(OpId target)
        {
            ulong lamport = _writer.NextLamport(out OpId? source);
            OpId id = _writer.NextId();
            _ops.Add(new RefOperation.SeqDelete(target)
            {
                Id = id,
                Lamport = lamport,
                LamportSource = source,
            });
            _writer.NoteLocal(id, lamport);
            return this;
        }

        internal RefBatch Complete() => new(_ops);
    }
}
