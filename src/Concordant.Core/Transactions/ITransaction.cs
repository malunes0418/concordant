using Concordant.Internal;
using Concordant.Shared;

namespace Concordant.Transactions;

/// <summary>Mutation scope for a single local transaction on a <see cref="ConcordantDocument"/>.</summary>
public interface ITransaction
{
    ConcordantDocument Document { get; }

    SharedMap GetOrCreateMap(string name);

    SharedArray GetOrCreateArray(string name);

    SharedText GetOrCreateText(string name);

    SharedMap Map(string name);

    SharedArray Array(string name);

    SharedText Text(string name);
}

internal sealed class Transaction : ITransaction
{
    private readonly ConcordantDocument _document;
    private readonly LocalClock _clock;
    private readonly List<ConcordantOperation> _ops = new();
    private readonly Dictionary<string, RootKind> _declaredRoots = new(StringComparer.Ordinal);
    private bool _completed;

    internal Transaction(ConcordantDocument document, LocalClock clock)
    {
        _document = document;
        _clock = clock;
    }

    public ConcordantDocument Document => _document;

    public SharedMap GetOrCreateMap(string name)
    {
        EnsureActive();
        _document.EnsureRootDeclared(this, name, RootKind.Map);
        return _document.GetMapHandle(name);
    }

    public SharedArray GetOrCreateArray(string name)
    {
        EnsureActive();
        _document.EnsureRootDeclared(this, name, RootKind.Array);
        return _document.GetArrayHandle(name);
    }

    public SharedText GetOrCreateText(string name)
    {
        EnsureActive();
        _document.EnsureRootDeclared(this, name, RootKind.Text);
        return _document.GetTextHandle(name);
    }

    public SharedMap Map(string name)
    {
        EnsureActive();
        SharedMap map = _document.GetMapHandle(name);
        RootKind? kind = _document.Store.TryGetRootKind(name);
        if (kind is null)
        {
            throw new InvalidOperationException($"Root '{name}' does not exist.");
        }

        if (kind != RootKind.Map)
        {
            throw new InvalidOperationException($"Root '{name}' is {kind}, not Map.");
        }

        return map;
    }

    public SharedArray Array(string name)
    {
        EnsureActive();
        SharedArray array = _document.GetArrayHandle(name);
        RootKind? kind = _document.Store.TryGetRootKind(name);
        if (kind is null)
        {
            throw new InvalidOperationException($"Root '{name}' does not exist.");
        }

        if (kind != RootKind.Array)
        {
            throw new InvalidOperationException($"Root '{name}' is {kind}, not Array.");
        }

        return array;
    }

    public SharedText Text(string name)
    {
        EnsureActive();
        SharedText text = _document.GetTextHandle(name);
        RootKind? kind = _document.Store.TryGetRootKind(name);
        if (kind is null)
        {
            throw new InvalidOperationException($"Root '{name}' does not exist.");
        }

        if (kind != RootKind.Text)
        {
            throw new InvalidOperationException($"Root '{name}' is {kind}, not Text.");
        }

        return text;
    }

    internal ConcordantOperation Append(Func<OpId, ulong, OpId?, ConcordantOperation> factory)
    {
        EnsureActive();
        ulong lamport = _clock.NextLamport(out OpId? source);
        OpId id = _clock.NextId();
        ConcordantOperation op = factory(id, lamport, source);
        _ops.Add(op);
        _clock.NoteLocal(id, lamport);

        // Eagerly integrate so mid-transaction reads and chained inserts see prior ops.
        ApplyResult result = _document.Store.Apply(new OperationBatch(new[] { op }));
        if (result.Status is not ApplyStatus.Integrated and not ApplyStatus.Duplicate)
        {
            throw new InvalidOperationException(
                $"Local operation failed to integrate: {result.Status} {result.Detail}");
        }

        return op;
    }

    internal void DeclareRoot(string name, RootKind kind)
    {
        if (_declaredRoots.TryGetValue(name, out RootKind existing))
        {
            if (existing != kind)
            {
                throw new InvalidOperationException($"Root '{name}' is {existing}, not {kind}.");
            }

            return;
        }

        _declaredRoots[name] = kind;
        Append((id, lamport, source) => new ConcordantOperation.RootDeclare(name, kind)
        {
            Id = id,
            Lamport = lamport,
            LamportSource = source,
        });
    }

    internal void MapSet(ContainerRef map, string key, ConcordantContent value)
    {
        Append((id, lamport, source) => new ConcordantOperation.MapSet(map, key, value)
        {
            Id = id,
            Lamport = lamport,
            LamportSource = source,
        });
    }

    internal ConcordantOperation SeqInsert(ContainerRef container, OpId? left, OpId? right, ConcordantContent content)
    {
        return Append((id, lamport, source) => new ConcordantOperation.SeqInsert(container, left, right, content)
        {
            Id = id,
            Lamport = lamport,
            LamportSource = source,
        });
    }

    internal void SeqDelete(OpId target)
    {
        Append((id, lamport, source) => new ConcordantOperation.SeqDelete(target)
        {
            Id = id,
            Lamport = lamport,
            LamportSource = source,
        });
    }

    internal OperationBatch Complete()
    {
        EnsureActive();
        _completed = true;
        if (_ops.Count == 0)
        {
            throw new InvalidOperationException("Transaction produced no operations.");
        }

        return new OperationBatch(_ops.ToArray());
    }

    internal bool HasOperations => _ops.Count > 0;

    private void EnsureActive()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Transaction has already completed.");
        }
    }
}
