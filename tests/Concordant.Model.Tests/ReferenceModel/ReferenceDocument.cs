using System.Text;

namespace Concordant.Model.Tests.ReferenceModel;

/// <summary>
/// Intentionally simple executable oracle for the operation model.
/// Not a production store: singleton sequence items, linear scans, clear predicates.
/// </summary>
public sealed class ReferenceDocument
{
    private readonly Dictionary<OpId, RefOperation> _ops = new();
    private readonly Dictionary<SessionId, ulong> _frontier = new();
    private readonly Dictionary<SessionId, ulong> _sessionLamport = new();
    private readonly List<RefOperation> _pending = new();
    private readonly Dictionary<string, RootState> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Map, string Key), List<MapAssignment>> _maps = new();
    private readonly LinkedList<SeqItem> _text = new();
    private readonly Dictionary<OpId, LinkedListNode<SeqItem>> _textIndex = new();

    private sealed class RootState
    {
        public required RootKind Kind { get; set; }
        public required OpId DeclarationId { get; set; }
        public bool Conflict { get; set; }
    }

    private sealed class MapAssignment
    {
        public required OpId Id { get; init; }
        public required ulong Lamport { get; init; }
        public required RefScalar Value { get; init; }
    }

    private sealed class SeqItem
    {
        public required OpId Id { get; init; }
        public required OpId? LeftOrigin { get; init; }
        public required OpId? RightOrigin { get; init; }
        public required RefScalar Content { get; init; }
        public bool Deleted { get; set; }
    }

    public IReadOnlyDictionary<SessionId, ulong> StateVector => _frontier;

    public ApplyResult Apply(RefBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        // Fast duplicate detection when every op is already present with identical payload.
        bool allKnown = true;
        foreach (RefOperation op in batch.Operations)
        {
            if (!_ops.TryGetValue(op.Id, out RefOperation? existing))
            {
                allKnown = false;
                break;
            }

            if (!OperationsEqual(existing, op))
            {
                return ApplyResult.Rejected($"ReplicaFork at {op.Id}");
            }
        }

        if (allKnown)
        {
            return ApplyResult.Duplicate();
        }

        // Validate batch-local contiguity and fork against store before mutating.
        var staged = new List<RefOperation>();
        foreach (RefOperation op in batch.Operations)
        {
            if (_ops.TryGetValue(op.Id, out RefOperation? existing))
            {
                if (!OperationsEqual(existing, op))
                {
                    return ApplyResult.Rejected($"ReplicaFork at {op.Id}");
                }

                continue;
            }

            if (staged.Any(s => s.Id == op.Id))
            {
                return ApplyResult.Rejected($"Duplicate OpId inside batch: {op.Id}");
            }

            staged.Add(op);
        }

        if (staged.Count == 0)
        {
            return ApplyResult.Duplicate();
        }

        foreach (IGrouping<SessionId, RefOperation> group in staged.GroupBy(o => o.Id.Session))
        {
            List<RefOperation> ordered = group.OrderBy(o => o.Id.Clock).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Id.Clock != ordered[i - 1].Id.Clock + 1)
                {
                    return ApplyResult.Rejected($"Non-contiguous clocks in batch for session {group.Key}");
                }
            }
        }

        foreach (RefOperation op in staged)
        {
            _pending.Add(op);
        }

        IntegratePending();

        bool anyIntegrated = staged.Any(o => _ops.ContainsKey(o.Id));
        bool anyPending = staged.Any(o => !_ops.ContainsKey(o.Id));
        if (anyIntegrated && !anyPending)
        {
            return ApplyResult.Integrated();
        }

        if (anyPending && !anyIntegrated)
        {
            return ApplyResult.Pending("Waiting for causal dependencies.");
        }

        if (anyIntegrated)
        {
            return ApplyResult.Integrated();
        }

        return ApplyResult.Pending("Waiting for causal dependencies.");
    }

    public string VisibleText()
    {
        var sb = new StringBuilder();
        for (LinkedListNode<SeqItem>? n = _text.First; n is not null; n = n.Next)
        {
            if (n.Value.Deleted)
            {
                continue;
            }

            if (n.Value.Content is RefScalar.StringScalar s)
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

    public IReadOnlyDictionary<string, RefScalar> VisibleMap(string mapName)
    {
        var result = new SortedDictionary<string, RefScalar>(StringComparer.Ordinal);
        foreach (KeyValuePair<(string Map, string Key), List<MapAssignment>> entry in _maps)
        {
            if (!string.Equals(entry.Key.Map, mapName, StringComparison.Ordinal))
            {
                continue;
            }

            MapAssignment winner = entry.Value
                .OrderByDescending(a => a.Lamport)
                .ThenByDescending(a => a.Id)
                .First();
            result[entry.Key.Key] = winner.Value;
        }

        return result;
    }

    public RootKind? TryGetRootKind(string name) =>
        _roots.TryGetValue(name, out RootState? root) ? root.Kind : null;

    public bool HasRootConflict(string name) =>
        _roots.TryGetValue(name, out RootState? root) && root.Conflict;

    /// <summary>Canonical fingerprint of visible state for convergence assertions.</summary>
    public string VisibleFingerprint()
    {
        var sb = new StringBuilder();
        sb.Append("text=").Append(VisibleText()).Append('|');
        sb.Append("roots=");
        foreach (KeyValuePair<string, RootState> root in _roots.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            sb.Append(root.Key).Append(':').Append(root.Value.Kind)
                .Append(root.Value.Conflict ? '!' : '.')
                .Append(';');
        }

        sb.Append("|maps=");
        foreach (string mapName in _maps.Keys.Select(k => k.Map).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            sb.Append(mapName).Append('{');
            foreach (KeyValuePair<string, RefScalar> kv in VisibleMap(mapName))
            {
                sb.Append(kv.Key).Append('=').Append(kv.Value.CanonicalKey()).Append(';');
            }

            sb.Append('}');
        }

        sb.Append("|ops=");
        foreach (OpId id in _ops.Keys.OrderBy(id => id))
        {
            sb.Append(id).Append(';');
        }

        return sb.ToString();
    }

    private void IntegratePending()
    {
        bool progress;
        do
        {
            progress = false;
            for (int i = 0; i < _pending.Count;)
            {
                RefOperation op = _pending[i];
                if (_ops.ContainsKey(op.Id))
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                if (!CanIntegrate(op))
                {
                    i++;
                    continue;
                }

                try
                {
                    IntegrateOne(op);
                }
                catch (InvalidOperationException)
                {
                    // Leave rejected ops out of the store; remove from pending to avoid spin.
                    _pending.RemoveAt(i);
                    continue;
                }

                _pending.RemoveAt(i);
                progress = true;
            }
        }
        while (progress);
    }

    private bool CanIntegrate(RefOperation op)
    {
        ulong expectedClock = _frontier.TryGetValue(op.Id.Session, out ulong n) ? n + 1 : 1UL;
        if (op.Id.Clock != expectedClock)
        {
            return false;
        }

        if (op.LamportSource is OpId source && !_ops.ContainsKey(source))
        {
            return false;
        }

        ulong previousLamport = _sessionLamport.TryGetValue(op.Id.Session, out ulong pl) ? pl : 0UL;
        ulong sourceLamport = 0;
        if (op.LamportSource is OpId src)
        {
            sourceLamport = _ops[src].Lamport;
        }

        ulong expectedLamport = checked(Math.Max(previousLamport, sourceLamport) + 1);
        if (op.Lamport != expectedLamport)
        {
            return false;
        }

        switch (op)
        {
            case RefOperation.SeqInsert insert:
                if (insert.LeftOrigin is OpId left && !_textIndex.ContainsKey(left) && !_ops.ContainsKey(left))
                {
                    return false;
                }

                if (insert.RightOrigin is OpId right && !_textIndex.ContainsKey(right) && !_ops.ContainsKey(right))
                {
                    return false;
                }

                if (insert.LeftOrigin is OpId l2 && !_textIndex.ContainsKey(l2))
                {
                    return false;
                }

                if (insert.RightOrigin is OpId r2 && !_textIndex.ContainsKey(r2))
                {
                    return false;
                }

                break;
            case RefOperation.SeqDelete delete:
                if (!_textIndex.ContainsKey(delete.TargetId))
                {
                    return false;
                }

                break;
            case RefOperation.MapSet mapSet:
                if (!_roots.ContainsKey(mapSet.MapName))
                {
                    return false;
                }

                break;
        }

        return true;
    }

    private void IntegrateOne(RefOperation op)
    {
        ulong expectedClock = _frontier.TryGetValue(op.Id.Session, out ulong n) ? n + 1 : 1UL;
        if (op.Id.Clock != expectedClock)
        {
            throw new InvalidOperationException("Clock hole.");
        }

        switch (op)
        {
            case RefOperation.RootDeclare root:
                IntegrateRoot(root);
                break;
            case RefOperation.MapSet mapSet:
                IntegrateMapSet(mapSet);
                break;
            case RefOperation.SeqInsert insert:
                IntegrateSeqInsert(insert);
                break;
            case RefOperation.SeqDelete delete:
                IntegrateSeqDelete(delete);
                break;
            default:
                throw new InvalidOperationException("Unknown operation kind.");
        }

        _ops[op.Id] = op;
        _frontier[op.Id.Session] = op.Id.Clock;
        _sessionLamport[op.Id.Session] = op.Lamport;
    }

    private void IntegrateRoot(RefOperation.RootDeclare op)
    {
        if (!_roots.TryGetValue(op.Name, out RootState? existing))
        {
            _roots[op.Name] = new RootState
            {
                Kind = op.Kind,
                DeclarationId = op.Id,
            };
            return;
        }

        if (existing.Kind == op.Kind)
        {
            return;
        }

        // Concurrent different-kind: minimum OpId wins.
        existing.Conflict = true;
        if (op.Id.CompareTo(existing.DeclarationId) < 0)
        {
            existing.Kind = op.Kind;
            existing.DeclarationId = op.Id;
        }
    }

    private void IntegrateMapSet(RefOperation.MapSet op)
    {
        var key = (op.MapName, op.Key);
        if (!_maps.TryGetValue(key, out List<MapAssignment>? list))
        {
            list = new List<MapAssignment>();
            _maps[key] = list;
        }

        list.Add(new MapAssignment
        {
            Id = op.Id,
            Lamport = op.Lamport,
            Value = op.Value,
        });
    }

    private void IntegrateSeqDelete(RefOperation.SeqDelete op)
    {
        if (_textIndex.TryGetValue(op.TargetId, out LinkedListNode<SeqItem>? node))
        {
            node.Value.Deleted = true;
        }
    }

    private void IntegrateSeqInsert(RefOperation.SeqInsert op)
    {
        EnsureTextRoot(op.ContainerName);

        var item = new SeqItem
        {
            Id = op.Id,
            LeftOrigin = op.LeftOrigin,
            RightOrigin = op.RightOrigin,
            Content = op.Content,
        };

        LinkedListNode<SeqItem>? leftNode = op.LeftOrigin is OpId left
            ? _textIndex[left]
            : null;
        LinkedListNode<SeqItem>? rightNode = op.RightOrigin is OpId right
            ? _textIndex[right]
            : null;

        // YATA-style scan between origins; insert before the first conflict peer with greater OpId.
        LinkedListNode<SeqItem>? cursor = leftNode?.Next ?? _text.First;
        LinkedListNode<SeqItem>? insertBefore = rightNode;

        while (cursor is not null && cursor != rightNode)
        {
            SeqItem c = cursor.Value;
            if (ShouldSkipConflict(c, op))
            {
                cursor = cursor.Next;
                continue;
            }

            if (c.Id.CompareTo(op.Id) > 0)
            {
                insertBefore = cursor;
                break;
            }

            // c.Id <= op.Id and not skippable → still advance when c is smaller.
            if (c.Id.CompareTo(op.Id) < 0)
            {
                cursor = cursor.Next;
                continue;
            }

            insertBefore = cursor;
            break;
        }

        LinkedListNode<SeqItem> node = insertBefore is null
            ? _text.AddLast(item)
            : _text.AddBefore(insertBefore, item);
        _textIndex[op.Id] = node;
    }

    /// <summary>
    /// YATA conflict skip: walk past smaller-ID peers whose left origin is still at/before ours
    /// (same left origin, or left origin structurally at/before the new item's left).
    /// </summary>
    private bool ShouldSkipConflict(SeqItem candidate, RefOperation.SeqInsert inserting)
    {
        if (candidate.Id.CompareTo(inserting.Id) >= 0)
        {
            return false;
        }

        if (EqualsOrigin(candidate.LeftOrigin, inserting.LeftOrigin))
        {
            return true;
        }

        return IsOriginAtOrBefore(candidate.LeftOrigin, inserting.LeftOrigin);
    }

    private bool IsOriginAtOrBefore(OpId? candidateLeft, OpId? insertingLeft)
    {
        if (candidateLeft is null)
        {
            return true;
        }

        if (insertingLeft is null)
        {
            // Inserting at head; any non-null left origin is to the right → do not skip.
            return false;
        }

        if (!_textIndex.TryGetValue(candidateLeft.Value, out LinkedListNode<SeqItem>? candNode))
        {
            return false;
        }

        if (!_textIndex.TryGetValue(insertingLeft.Value, out LinkedListNode<SeqItem>? insNode))
        {
            return false;
        }

        // candidateLeft is at or before insertingLeft if we can walk from candidate to inserting.
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

    private void EnsureTextRoot(string name)
    {
        if (!_roots.TryGetValue(name, out RootState? root))
        {
            // Implicit text root for oracle convenience when tests insert without declare.
            _roots[name] = new RootState
            {
                Kind = RootKind.Text,
                DeclarationId = new OpId(SessionId.FromSeed(0), 1),
            };
            return;
        }

        if (root.Kind != RootKind.Text && root.Kind != RootKind.Array)
        {
            throw new InvalidOperationException($"Root '{name}' is not a sequence container.");
        }
    }

    private static bool OperationsEqual(RefOperation a, RefOperation b)
    {
        if (a.Id != b.Id || a.Lamport != b.Lamport || a.LamportSource != b.LamportSource)
        {
            return false;
        }

        return (a, b) switch
        {
            (RefOperation.RootDeclare ra, RefOperation.RootDeclare rb) =>
                string.Equals(ra.Name, rb.Name, StringComparison.Ordinal) && ra.Kind == rb.Kind,
            (RefOperation.MapSet ma, RefOperation.MapSet mb) =>
                string.Equals(ma.MapName, mb.MapName, StringComparison.Ordinal)
                && string.Equals(ma.Key, mb.Key, StringComparison.Ordinal)
                && ma.Value.CanonicalKey() == mb.Value.CanonicalKey(),
            (RefOperation.SeqInsert sa, RefOperation.SeqInsert sb) =>
                string.Equals(sa.ContainerName, sb.ContainerName, StringComparison.Ordinal)
                && sa.LeftOrigin == sb.LeftOrigin
                && sa.RightOrigin == sb.RightOrigin
                && sa.Content.CanonicalKey() == sb.Content.CanonicalKey(),
            (RefOperation.SeqDelete da, RefOperation.SeqDelete db) => da.TargetId == db.TargetId,
            _ => false,
        };
    }
}
