using Concordant.Internal;
using Concordant.Transactions;
using Concordant.Values;

namespace Concordant.Shared;

/// <summary>
/// Typed view over a map container (root or nested).
/// <para>
/// Beta map semantics are last-writer-wins assignment history only. Keys cannot be removed:
/// there is no <c>MapDelete</c> / remove API in this release because a removal marker would require
/// a native-v1 wire change. Overwrite with a new <see cref="Set"/> value (including
/// <see cref="ConcordantScalar.Null"/>) if you need to clear a logical value; the prior assignment
/// remains in history and continues to occupy retention quotas.
/// </para>
/// </summary>
public sealed class SharedMap
{
    private readonly ConcordantDocument _document;
    private readonly ContainerRef _container;

    internal SharedMap(ConcordantDocument document, ContainerRef container)
    {
        _document = document;
        _container = container;
    }

    public ContainerRef Container => _container;

    public int Count
    {
        get
        {
            _document.EnsureReadable();
            return _document.Store.VisibleMap(_container).Count;
        }
    }

    public IReadOnlyDictionary<string, ConcordantContent> ToDictionary()
    {
        _document.EnsureReadable();
        return _document.Store.VisibleMap(_container);
    }

    public bool TryGet(string key, out ConcordantContent? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _document.EnsureReadable();
        IReadOnlyDictionary<string, ConcordantContent> map = _document.Store.VisibleMap(_container);
        if (map.TryGetValue(key, out ConcordantContent? found))
        {
            value = found;
            return true;
        }

        value = null;
        return false;
    }

    public bool TryGetScalar(string key, out ConcordantScalar? value)
    {
        if (TryGet(key, out ConcordantContent? content) && content is ConcordantContent.ScalarContent s)
        {
            value = s.Value;
            return true;
        }

        value = null;
        return false;
    }

    public void Set(string key, ConcordantScalar value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        Transaction tx = _document.RequireTransaction();
        tx.MapSet(_container, key, ConcordantContent.Scalar(value));
    }

    public SharedMap CreateMap(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Transaction tx = _document.RequireTransaction();
        ConcordantOperation op = tx.Append((id, lamport, source) => new ConcordantOperation.MapSet(
            _container,
            key,
            ConcordantContent.Nested(RootKind.Map))
        {
            Id = id,
            Lamport = lamport,
            LamportSource = source,
        });
        return _document.GetMapHandle(ContainerRef.Nested(op.Id));
    }

    public SharedArray CreateArray(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Transaction tx = _document.RequireTransaction();
        ConcordantOperation op = tx.Append((id, lamport, source) => new ConcordantOperation.MapSet(
            _container,
            key,
            ConcordantContent.Nested(RootKind.Array))
        {
            Id = id,
            Lamport = lamport,
            LamportSource = source,
        });
        return _document.GetArrayHandle(ContainerRef.Nested(op.Id));
    }

    public SharedText CreateText(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Transaction tx = _document.RequireTransaction();
        ConcordantOperation op = tx.Append((id, lamport, source) => new ConcordantOperation.MapSet(
            _container,
            key,
            ConcordantContent.Nested(RootKind.Text))
        {
            Id = id,
            Lamport = lamport,
            LamportSource = source,
        });
        return _document.GetTextHandle(ContainerRef.Nested(op.Id));
    }

    public bool TryGetMap(string key, out SharedMap? map)
    {
        if (TryGet(key, out ConcordantContent? content)
            && content is ConcordantContent.NestedContent { Kind: RootKind.Map }
            && FindWinningNestedId(key) is OpId id)
        {
            map = _document.GetMapHandle(ContainerRef.Nested(id));
            return true;
        }

        map = null;
        return false;
    }

    public bool TryGetArray(string key, out SharedArray? array)
    {
        if (TryGet(key, out ConcordantContent? content)
            && content is ConcordantContent.NestedContent { Kind: RootKind.Array }
            && FindWinningNestedId(key) is OpId id)
        {
            array = _document.GetArrayHandle(ContainerRef.Nested(id));
            return true;
        }

        array = null;
        return false;
    }

    public bool TryGetText(string key, out SharedText? text)
    {
        if (TryGet(key, out ConcordantContent? content)
            && content is ConcordantContent.NestedContent { Kind: RootKind.Text }
            && FindWinningNestedId(key) is OpId id)
        {
            text = _document.GetTextHandle(ContainerRef.Nested(id));
            return true;
        }

        text = null;
        return false;
    }

    private OpId? FindWinningNestedId(string key)
    {
        // VisibleMap already selects the winner; recover its OpId from store operations via content match.
        // Nested container id is the winning MapSet OpId.
        IReadOnlyDictionary<string, ConcordantContent> visible = _document.Store.VisibleMap(_container);
        if (!visible.TryGetValue(key, out ConcordantContent? content) || content is not ConcordantContent.NestedContent)
        {
            return null;
        }

        // Scan integrated map assignments for this key; pick Lamport/OpId max.
        OpId? bestId = null;
        ulong bestLamport = 0;
        foreach (ConcordantOperation op in _document.Store.Operations.Values)
        {
            if (op is ConcordantOperation.MapSet mapSet
                && mapSet.Map == _container
                && string.Equals(mapSet.Key, key, StringComparison.Ordinal)
                && mapSet.Value is ConcordantContent.NestedContent)
            {
                if (bestId is null
                    || mapSet.Lamport > bestLamport
                    || (mapSet.Lamport == bestLamport && mapSet.Id.CompareTo(bestId.Value) > 0))
                {
                    bestId = mapSet.Id;
                    bestLamport = mapSet.Lamport;
                }
            }
        }

        return bestId;
    }
}
