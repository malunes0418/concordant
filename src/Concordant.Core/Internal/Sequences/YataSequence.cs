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

    /// <summary>Monotonic structural order key for O(1) origin comparisons.</summary>
    public long StructuralOrder { get; set; }
}

/// <summary>
/// YATA structural sequence with order keys and an indexed visible UTF-16 rank view for SharedText.
/// </summary>
internal sealed class YataSequence
{
    private const long OrderGap = 1L << 32;

    private readonly LinkedList<SeqItem> _items = new();
    private readonly Dictionary<OpId, LinkedListNode<SeqItem>> _index = new();

    /// <summary>Visible string-scalar items in document order (SharedText rank index).</summary>
    private readonly List<(OpId Id, int Utf16Len)> _visibleUtf16 = new();
    private readonly Dictionary<OpId, int> _visibleIndex = new();
    private int _visibleUtf16Length;
    private bool _orderNeedsRebalance;

    public IReadOnlyDictionary<OpId, LinkedListNode<SeqItem>> Index => _index;

    public LinkedListNode<SeqItem>? First => _items.First;

    public int VisibleUtf16Length => _visibleUtf16Length;

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
        AssignStructuralOrder(node);
        NoteVisibleInsert(node);
    }

    public void MarkDeleted(OpId targetId)
    {
        if (!_index.TryGetValue(targetId, out LinkedListNode<SeqItem>? node))
        {
            return;
        }

        if (node.Value.Deleted)
        {
            return;
        }

        node.Value.Deleted = true;
        NoteVisibleDelete(targetId);
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

    /// <summary>Visible string-scalar items used by SharedText UTF-16 addressing.</summary>
    public IReadOnlyList<(OpId Id, int Utf16Len)> GetVisibleUtf16Items() => _visibleUtf16;

    /// <summary>
    /// Resolves insert origins for a UTF-16 offset in O(log n) over the visible rank index.
    /// </summary>
    public void ResolveUtf16InsertOrigins(int utf16Offset, out OpId? left, out OpId? right)
    {
        if (utf16Offset < 0 || utf16Offset > _visibleUtf16Length)
        {
            throw new ArgumentOutOfRangeException(nameof(utf16Offset));
        }

        left = null;
        right = null;
        if (_visibleUtf16.Count == 0)
        {
            if (utf16Offset != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(utf16Offset));
            }

            return;
        }

        int index = FindVisibleIndexAtOrAfter(utf16Offset, out int start);
        if (start != utf16Offset)
        {
            throw new ArgumentException(
                "UTF-16 offset must align to a Unicode scalar boundary.",
                nameof(utf16Offset));
        }

        if (index > 0)
        {
            left = _visibleUtf16[index - 1].Id;
        }

        if (index < _visibleUtf16.Count)
        {
            right = _visibleUtf16[index].Id;
        }
    }

    /// <summary>
    /// Collects visible item ids fully covered by a UTF-16 delete range.
    /// </summary>
    public List<OpId> CollectUtf16DeleteTargets(int utf16Offset, int utf16Length)
    {
        if (utf16Offset < 0 || utf16Length < 0 || utf16Offset + utf16Length > _visibleUtf16Length)
        {
            throw new ArgumentOutOfRangeException(nameof(utf16Length));
        }

        var targets = new List<OpId>();
        if (utf16Length == 0)
        {
            return targets;
        }

        int index = FindVisibleIndexAtOrAfter(utf16Offset, out int start);
        if (start != utf16Offset || index >= _visibleUtf16.Count)
        {
            throw new ArgumentException("Delete range must align to Unicode scalar boundaries.");
        }

        int end = utf16Offset + utf16Length;
        int cursor = start;
        while (cursor < end)
        {
            if (index >= _visibleUtf16.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(utf16Length));
            }

            (OpId id, int len) = _visibleUtf16[index];
            int itemEnd = cursor + len;
            if (itemEnd > end)
            {
                throw new ArgumentException("Delete range must align to Unicode scalar boundaries.");
            }

            targets.Add(id);
            cursor = itemEnd;
            index++;
        }

        return targets;
    }

    /// <summary>Builds visible text without allocating per-call item lists.</summary>
    public string BuildVisibleText()
    {
        if (_visibleUtf16Length == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(_visibleUtf16Length);
        for (LinkedListNode<SeqItem>? n = _items.First; n is not null; n = n.Next)
        {
            if (n.Value.Deleted)
            {
                continue;
            }

            if (n.Value.Content is ConcordantContent.ScalarContent { Value: ConcordantScalar.StringScalar s })
            {
                sb.Append(s.Value);
            }
            else
            {
                sb.Append(n.Value.Content.CanonicalKey());
            }
        }

        return sb.ToString();
    }

    public YataSequence Clone()
    {
        var copy = new YataSequence();
        for (LinkedListNode<SeqItem>? n = _items.First; n is not null; n = n.Next)
        {
            SeqItem src = n.Value;
            var item = new SeqItem
            {
                Id = src.Id,
                LeftOrigin = src.LeftOrigin,
                RightOrigin = src.RightOrigin,
                Content = src.Content,
                Deleted = src.Deleted,
                StructuralOrder = src.StructuralOrder,
            };
            LinkedListNode<SeqItem> node = copy._items.AddLast(item);
            copy._index[item.Id] = node;
        }

        foreach ((OpId id, int len) in _visibleUtf16)
        {
            copy._visibleUtf16.Add((id, len));
            copy._visibleIndex[id] = copy._visibleUtf16.Count - 1;
        }

        copy._visibleUtf16Length = _visibleUtf16Length;
        copy._orderNeedsRebalance = _orderNeedsRebalance;
        return copy;
    }

    private void AssignStructuralOrder(LinkedListNode<SeqItem> node)
    {
        long left = node.Previous?.Value.StructuralOrder ?? 0;
        long right = node.Next?.Value.StructuralOrder ?? 0;

        if (node.Previous is null && node.Next is null)
        {
            node.Value.StructuralOrder = OrderGap;
            return;
        }

        if (node.Previous is null)
        {
            node.Value.StructuralOrder = right - OrderGap;
            if (node.Value.StructuralOrder >= right)
            {
                RebalanceOrders();
            }

            return;
        }

        if (node.Next is null)
        {
            node.Value.StructuralOrder = left + OrderGap;
            if (node.Value.StructuralOrder <= left)
            {
                RebalanceOrders();
            }

            return;
        }

        if (right - left > 1)
        {
            node.Value.StructuralOrder = left + ((right - left) / 2);
            return;
        }

        RebalanceOrders();
    }

    private void RebalanceOrders()
    {
        long order = OrderGap;
        for (LinkedListNode<SeqItem>? n = _items.First; n is not null; n = n.Next)
        {
            n.Value.StructuralOrder = order;
            order = checked(order + OrderGap);
        }

        _orderNeedsRebalance = false;
    }

    private void NoteVisibleInsert(LinkedListNode<SeqItem> node)
    {
        if (node.Value.Deleted)
        {
            return;
        }

        if (node.Value.Content is not ConcordantContent.ScalarContent { Value: ConcordantScalar.StringScalar s })
        {
            return;
        }

        // Place via the nearest prior visible neighbor (O(tombstones)) instead of scanning
        // from the head on every insert (O(n) — quadratic for sequential appends).
        int visiblePos = 0;
        for (LinkedListNode<SeqItem>? n = node.Previous; n is not null; n = n.Previous)
        {
            if (n.Value.Deleted)
            {
                continue;
            }

            if (n.Value.Content is ConcordantContent.ScalarContent { Value: ConcordantScalar.StringScalar }
                && _visibleIndex.TryGetValue(n.Value.Id, out int prevIdx))
            {
                visiblePos = prevIdx + 1;
                break;
            }
        }

        _visibleUtf16.Insert(visiblePos, (node.Value.Id, s.Value.Length));
        for (int i = visiblePos; i < _visibleUtf16.Count; i++)
        {
            _visibleIndex[_visibleUtf16[i].Id] = i;
        }

        _visibleUtf16Length += s.Value.Length;
    }

    private void NoteVisibleDelete(OpId id)
    {
        if (!_visibleIndex.TryGetValue(id, out int index))
        {
            return;
        }

        _visibleUtf16Length -= _visibleUtf16[index].Utf16Len;
        _visibleUtf16.RemoveAt(index);
        _ = _visibleIndex.Remove(id);
        for (int i = index; i < _visibleUtf16.Count; i++)
        {
            _visibleIndex[_visibleUtf16[i].Id] = i;
        }
    }

    private int FindVisibleIndexAtOrAfter(int utf16Offset, out int startOffset)
    {
        if (utf16Offset <= 0)
        {
            startOffset = 0;
            return 0;
        }

        if (utf16Offset >= _visibleUtf16Length)
        {
            startOffset = _visibleUtf16Length;
            return _visibleUtf16.Count;
        }

        // Prefix scan is fine for modest visible counts; middle-insert List shifts remain O(n).
        int cursor = 0;
        for (int i = 0; i < _visibleUtf16.Count; i++)
        {
            if (cursor >= utf16Offset)
            {
                startOffset = cursor;
                return i;
            }

            cursor += _visibleUtf16[i].Utf16Len;
        }

        startOffset = cursor;
        return _visibleUtf16.Count;
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

        if (_orderNeedsRebalance)
        {
            RebalanceOrders();
        }

        return candNode.Value.StructuralOrder <= insNode.Value.StructuralOrder;
    }

    private static bool EqualsOrigin(OpId? a, OpId? b) =>
        a is null ? b is null : b is not null && a.Value == b.Value;
}
