using Concordant.Values;

namespace Concordant.Internal.Sequences;

/// <summary>YATA-style structural sequence item (including tombstones).</summary>
internal sealed class SeqItem
{
    public required OpId Id { get; init; }

    public required OpId? LeftOrigin { get; init; }

    public required OpId? RightOrigin { get; init; }

    public required ConcordantContent Content { get; init; }

    public bool Deleted { get; set; }
}

/// <summary>Linked structural sequence with YATA insert integration matching the reference oracle.</summary>
internal sealed class YataSequence
{
    private readonly LinkedList<SeqItem> _items = new();
    private readonly Dictionary<OpId, LinkedListNode<SeqItem>> _index = new();

    public IReadOnlyDictionary<OpId, LinkedListNode<SeqItem>> Index => _index;

    public LinkedListNode<SeqItem>? First => _items.First;

    public bool TryGetNode(OpId id, out LinkedListNode<SeqItem> node) =>
        _index.TryGetValue(id, out node!);

    public bool Contains(OpId id) => _index.ContainsKey(id);

    public void IntegrateInsert(OpId id, OpId? leftOrigin, OpId? rightOrigin, ConcordantContent content)
    {
        var item = new SeqItem
        {
            Id = id,
            LeftOrigin = leftOrigin,
            RightOrigin = rightOrigin,
            Content = content,
        };

        LinkedListNode<SeqItem>? leftNode = leftOrigin is OpId left ? _index[left] : null;
        LinkedListNode<SeqItem>? rightNode = rightOrigin is OpId right ? _index[right] : null;

        LinkedListNode<SeqItem>? cursor = leftNode?.Next ?? _items.First;
        LinkedListNode<SeqItem>? insertBefore = rightNode;

        while (cursor is not null && cursor != rightNode)
        {
            SeqItem c = cursor.Value;
            if (ShouldSkipConflict(c, id, leftOrigin))
            {
                cursor = cursor.Next;
                continue;
            }

            if (c.Id.CompareTo(id) > 0)
            {
                insertBefore = cursor;
                break;
            }

            if (c.Id.CompareTo(id) < 0)
            {
                cursor = cursor.Next;
                continue;
            }

            insertBefore = cursor;
            break;
        }

        LinkedListNode<SeqItem> node = insertBefore is null
            ? _items.AddLast(item)
            : _items.AddBefore(insertBefore, item);
        _index[id] = node;
    }

    public void MarkDeleted(OpId targetId)
    {
        if (_index.TryGetValue(targetId, out LinkedListNode<SeqItem>? node))
        {
            node.Value.Deleted = true;
        }
    }

    public IEnumerable<SeqItem> VisibleItems()
    {
        for (LinkedListNode<SeqItem>? n = _items.First; n is not null; n = n.Next)
        {
            if (!n.Value.Deleted)
            {
                yield return n.Value;
            }
        }
    }

    private bool ShouldSkipConflict(SeqItem candidate, OpId insertingId, OpId? insertingLeft)
    {
        if (candidate.Id.CompareTo(insertingId) >= 0)
        {
            return false;
        }

        if (EqualsOrigin(candidate.LeftOrigin, insertingLeft))
        {
            return true;
        }

        return IsOriginAtOrBefore(candidate.LeftOrigin, insertingLeft);
    }

    private bool IsOriginAtOrBefore(OpId? candidateLeft, OpId? insertingLeft)
    {
        if (candidateLeft is null)
        {
            return true;
        }

        if (insertingLeft is null)
        {
            return false;
        }

        if (!_index.TryGetValue(candidateLeft.Value, out LinkedListNode<SeqItem>? candNode))
        {
            return false;
        }

        if (!_index.TryGetValue(insertingLeft.Value, out LinkedListNode<SeqItem>? insNode))
        {
            return false;
        }

        for (LinkedListNode<SeqItem>? n = candNode; n is not null; n = n.Next)
        {
            if (ReferenceEquals(n, insNode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EqualsOrigin(OpId? a, OpId? b) =>
        a is null ? b is null : b is not null && a.Value == b.Value;
}
