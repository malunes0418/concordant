using Concordant.Internal;
using Concordant.Internal.Sequences;
using Concordant.Transactions;
using Concordant.Values;

namespace Concordant.Shared;

/// <summary>Typed view over an array sequence container (root or nested).</summary>
public sealed class SharedArray
{
    private readonly ConcordantDocument _document;
    private readonly ContainerRef _container;

    internal SharedArray(ConcordantDocument document, ContainerRef container)
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
            YataSequence? seq = _document.Store.TryGetSequence(_container);
            if (seq is null)
            {
                return 0;
            }

            int count = 0;
            foreach (SeqItem _ in seq.VisibleItems())
            {
                count++;
            }

            return count;
        }
    }

    public ConcordantContent this[int index]
    {
        get
        {
            _document.EnsureReadable();
            return GetVisibleItem(index).Content;
        }
    }

    public void Insert(int index, ConcordantScalar value)
    {
        ArgumentNullException.ThrowIfNull(value);
        InsertContent(index, ConcordantContent.Scalar(value));
    }

    public SharedMap InsertMap(int index)
    {
        ConcordantOperation op = InsertContent(index, ConcordantContent.Nested(RootKind.Map));
        return _document.GetMapHandle(ContainerRef.Nested(op.Id));
    }

    public SharedArray InsertArray(int index)
    {
        ConcordantOperation op = InsertContent(index, ConcordantContent.Nested(RootKind.Array));
        return _document.GetArrayHandle(ContainerRef.Nested(op.Id));
    }

    public SharedText InsertText(int index)
    {
        ConcordantOperation op = InsertContent(index, ConcordantContent.Nested(RootKind.Text));
        return _document.GetTextHandle(ContainerRef.Nested(op.Id));
    }

    public void Add(ConcordantScalar value) => Insert(Count, value);

    public void Delete(int index, int count = 1)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Transaction tx = _document.RequireTransaction();
        for (int i = 0; i < count; i++)
        {
            SeqItem item = GetVisibleItem(index);
            tx.SeqDelete(item.Id);
        }
    }

    public IReadOnlyList<ConcordantContent> ToList()
    {
        _document.EnsureReadable();
        YataSequence? seq = _document.Store.TryGetSequence(_container);
        if (seq is null)
        {
            return System.Array.Empty<ConcordantContent>();
        }

        var list = new List<ConcordantContent>();
        foreach (SeqItem item in seq.VisibleItems())
        {
            list.Add(item.Content);
        }

        return list;
    }

    private ConcordantOperation InsertContent(int index, ConcordantContent content)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        Transaction tx = _document.RequireTransaction();
        ResolveInsertOrigins(index, out OpId? left, out OpId? right);
        return tx.SeqInsert(_container, left, right, content);
    }

    private void ResolveInsertOrigins(int index, out OpId? left, out OpId? right)
    {
        YataSequence? seq = _document.Store.TryGetSequence(_container);
        var visible = new List<SeqItem>();
        if (seq is not null)
        {
            visible.AddRange(seq.VisibleItems());
        }

        if (index > visible.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        left = index == 0 ? null : visible[index - 1].Id;
        right = index >= visible.Count ? null : visible[index].Id;
    }

    private SeqItem GetVisibleItem(int index)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        YataSequence? seq = _document.Store.TryGetSequence(_container);
        if (seq is null)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        int i = 0;
        foreach (SeqItem item in seq.VisibleItems())
        {
            if (i == index)
            {
                return item;
            }

            i++;
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }
}
